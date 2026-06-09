using System.Collections.Generic;
using UnityEngine;
using BodyTracking.Data;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// Climbing-specific world constraint that exploits the strong priors of a flat vertical wall:
    ///   • Nobody is ever BEHIND the wall, and nobody is ever more than ~arm's reach OUT from it. So every
    ///     joint is projected into a thin depth "slab" parallel to the wall (RouteRoot Z in [skin .. maxBodyDepth]).
    ///   • Hands/feet on holds are momentarily STATIC and sit ON the wall surface — exactly like a foot-plant in
    ///     walking mocap. So a contact joint that is near the wall and barely moving is snapped to the wall skin and
    ///     LOCKED there until it visibly moves again. This is what makes the GLB look like it actually grabs holds
    ///     instead of swimming near the surface, and it removes the depth jitter that ARKit's estimated body depth
    ///     produces against a flat wall.
    ///
    /// All geometry is done in world space using the live RouteRoot frame (Z = +forward = out of the wall), so it
    /// inherits Immersal/world-map drift correction every frame. It is character-agnostic: it operates on the shared
    /// world joint array, and additionally emits per-limb contact targets so a humanoid rig (GLB muscle pose) can be
    /// pinned with two-bone IK afterwards.
    /// </summary>
    public sealed class WallProjectionResolver
    {
        public enum ClimbLimb { LeftHand, RightHand, LeftFoot, RightFoot }

        /// <summary>Why a limb has an active wall contact this frame (drives debug coloring).</summary>
        public enum ContactKind { None, Hold }

        /// <summary>Maps one limb to the world-joint index that represents its tip (wrist/ankle).</summary>
        public struct ContactJoint
        {
            public ClimbLimb limb;
            public int worldIndex; // index into the world[] joint array (-1 = unmapped)
        }

        /// <summary>A resolved contact target for one limb, consumed by the rig IK pass.</summary>
        public struct WallContact
        {
            public ClimbLimb limb;
            public bool active;        // the limb is currently latched to a hold
            public Vector3 targetWorld; // world point on the wall skin to pin the tip to
            public float weight;       // 0..1 eased IK weight
            public ContactKind kind;   // Hold = anchored to a known hold (green); None = no anchor
        }

        struct LimbState
        {
            public bool inContact;
            public Vector3 lockedSurface; // latched world point on the wall skin (a known hold)
            public float weight;          // eased IK weight
        }

        // Indexed by (int)ClimbLimb.
        readonly LimbState[] limbs = new LimbState[4];
        readonly WallContact[] contacts = new WallContact[4];

        // Walk-away release state: true while the climber is on the wall (fix active), false once they step off.
        bool engaged = true;
        // Eased 0..1 strength of the whole-body depth slab. Ramps toward 1 while engaged, toward 0 while released, so
        // the body GLIDES onto / off the wall instead of snapping when engagement toggles.
        float slabWeight = 1f;

        /// <summary>True while the wall fix is active. False once the climber has walked away from the wall.</summary>
        public bool IsEngaged => engaged;

        /// <summary>Current eased slab strength (0 = off the wall, 1 = fully pinned). For debug overlays.</summary>
        public float SlabWeight => slabWeight;

        public void Reset()
        {
            for (int i = 0; i < limbs.Length; i++)
                limbs[i] = default;
            engaged = true;
            slabWeight = 1f;
        }

        /// <summary>
        /// Estimate the wall plane's depth (RouteRoot-local Z) directly from a recording, exploiting the climbing
        /// prior: on a flat wall the body is CLOSEST to the wall where hands/feet rest on holds, so the wall surface
        /// is the low end of the joint-depth distribution. Returns a low percentile of every tracked joint's local Z
        /// across the whole recording (robust to a few samples that read slightly behind the wall). This is the value
        /// to put in <see cref="WallProjectionSettings.wallDepthOffset"/> (and the probe's wall offset) when the
        /// RouteRoot origin does not sit on the physical wall. Returns false when there isn't enough tracked data.
        /// </summary>
        public static bool TryEstimateWallDepth(HipRecording recording, out float wallDepth, float lowPercentile = 0.03f)
        {
            wallDepth = 0f;
            if (recording == null || recording.frames == null || recording.frames.Count == 0)
                return false;

            var depths = new List<float>(recording.frames.Count * 8);
            foreach (var frame in recording.frames)
            {
                if (frame.recordedJoints != null)
                {
                    foreach (var j in frame.recordedJoints)
                        if (j != null && j.isTracked)
                            depths.Add(j.positionReference.z);
                }
                else if (frame.hipJoint.IsValid)
                {
                    depths.Add(frame.hipJoint.position.z);
                }
            }

            if (depths.Count < 8)
                return false;

            depths.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(lowPercentile) * (depths.Count - 1)), 0, depths.Count - 1);
            wallDepth = depths[idx];
            return true;
        }

        // Depth is measured OUT from the wall: depth = dot(worldPos - wallPoint, wallNormal). wallPoint sits ON the
        // wall surface and wallNormal points away from the wall (toward the climber/camera). depth 0 = on the wall.
        static float Depth(Vector3 worldPos, Vector3 wallPoint, Vector3 normal)
            => Vector3.Dot(worldPos - wallPoint, normal);

        static Vector3 SetDepth(Vector3 worldPos, float depth, Vector3 wallPoint, Vector3 normal)
            => worldPos + (depth - Depth(worldPos, wallPoint, normal)) * normal;

        /// <summary>
        /// Decide whether the climber is still ON the wall this frame (so the slab clamp + contact lock should run)
        /// or has walked AWAY from it (so the fix should let the raw pose pass through untouched). Uses the depth of
        /// the CLOSEST body joint: while on the wall at least a hand/foot sits near the surface, so the closest joint
        /// is small; when the whole body steps back, even the closest joint is far out. Hysteresis between the release
        /// and re-engage distances stops it flickering at the threshold. When release is disabled the fix is always
        /// engaged. Call once per frame BEFORE <see cref="ProjectIntoSlab"/> / <see cref="ResolveContacts"/> and skip
        /// both when it returns false.
        /// </summary>
        public bool UpdateWallEngagement(Vector3[] world, Vector3 wallPoint, Vector3 wallNormal,
            in WallProjectionSettings s)
            => UpdateWallEngagement(world, wallPoint, wallNormal, false, 0f, null, -1,
                Time.deltaTime, s);

        /// <summary>
        /// Full engagement update with floor awareness and eased on/off. <paramref name="footWorldIndices"/> +
        /// <paramref name="pelvisWorldIndex"/> let the climber be classified as "standing on the floor in front of the
        /// wall" (feet at floor level, hips out past the body-depth slab) — an upright walk-by — so the slab does not
        /// grab the body until they are genuinely on the wall. Returns true while the eased slab still has any
        /// strength (so the caller keeps running <see cref="ProjectIntoSlab"/> through the glide-off).
        /// </summary>
        public bool UpdateWallEngagement(Vector3[] world, Vector3 wallPoint, Vector3 wallNormal,
            bool hasFloor, float floorWorldY, IReadOnlyList<int> footWorldIndices, int pelvisWorldIndex,
            float dt, in WallProjectionSettings s)
        {
            if (world == null || world.Length == 0 || wallNormal.sqrMagnitude < 1e-8f)
                return slabWeight > 0.001f;

            wallNormal.Normalize();
            float minDepth = float.MaxValue;
            for (int i = 0; i < world.Length; i++)
            {
                float d = Depth(world[i], wallPoint, wallNormal);
                if (d < minDepth) minDepth = d;
            }

            // Depth hysteresis latch (walk-away release). When release is disabled the climber is always "near" the wall.
            if (!s.enableWalkAwayRelease)
            {
                engaged = true;
            }
            else
            {
                float release = Mathf.Max(0.05f, s.wallReleaseDepth);
                float reengage = Mathf.Clamp(s.wallReengageDepth, 0f, release - 0.01f);
                if (engaged && minDepth > release) engaged = false;
                else if (!engaged && minDepth < reengage) engaged = true;
            }

            // Floor-stand gate: while the climber stands ON THE FLOOR in front of the wall (feet at floor level AND the
            // hips sitting out past the body-depth slab), they are walking by, not climbing — keep the slab off so the
            // body isn't yanked onto the wall. Once the hips come in close (hugging the wall) or the feet leave the
            // floor, this clears and the slab engages normally.
            bool floorStanding = false;
            if (s.enableFloorStandRelease && hasFloor && footWorldIndices != null && footWorldIndices.Count > 0)
            {
                float floorBand = Mathf.Max(0.02f, s.holdFloorExclusionBand);
                float lowestFoot = float.MaxValue;
                for (int i = 0; i < footWorldIndices.Count; i++)
                {
                    int idx = footWorldIndices[i];
                    if (idx >= 0 && idx < world.Length)
                        lowestFoot = Mathf.Min(lowestFoot, world[idx].y);
                }
                bool feetOnFloor = lowestFoot != float.MaxValue && (lowestFoot - floorWorldY) < floorBand;

                bool hipsOutFromWall = true; // assume out unless we can prove the hips hug the wall
                if (pelvisWorldIndex >= 0 && pelvisWorldIndex < world.Length)
                {
                    float hipsDepth = Depth(world[pelvisWorldIndex], wallPoint, wallNormal);
                    hipsOutFromWall = hipsDepth > Mathf.Max(0.05f, s.maxBodyDepth);
                }
                floorStanding = feetOnFloor && hipsOutFromWall;
            }

            // Drop latched contacts the instant we decide the body is off the wall (fully released or floor-standing) so
            // nothing stays pinned while we glide off.
            bool wantSlab = engaged && !floorStanding;
            if (!wantSlab && slabWeight <= 0.001f)
            {
                for (int i = 0; i < limbs.Length; i++)
                    limbs[i] = default;
            }

            // Ease the slab strength toward the target (frame-rate independent).
            float ease = Mathf.Max(0f, s.wallEngageEaseSeconds);
            if (ease <= 1e-4f)
                slabWeight = wantSlab ? 1f : 0f;
            else
            {
                float w = 1f - Mathf.Exp(-Mathf.Max(1e-4f, dt) / ease);
                slabWeight = Mathf.Lerp(slabWeight, wantSlab ? 1f : 0f, w);
            }
            if (slabWeight < 0.001f) slabWeight = 0f;
            if (slabWeight > 0.999f) slabWeight = 1f;

            return slabWeight > 0.001f;
        }

        /// <summary>
        /// Project every joint into the wall slab: clamp its depth so nothing sinks behind the wall skin and nothing
        /// floats further out than a plausible body depth. The wall is given as a point + outward normal (so it can
        /// come from the RouteRoot plane OR a real AR-detected vertical plane, tilt included). Mutates
        /// <paramref name="world"/> in place.
        /// </summary>
        public void ProjectIntoSlab(Vector3[] world, Vector3 wallPoint, Vector3 wallNormal, in WallProjectionSettings s)
        {
            if (!s.enableSlabClamp || world == null) return;
            if (wallNormal.sqrMagnitude < 1e-8f) return;
            wallNormal.Normalize();

            // Eased engagement: at slabWeight 0 the raw pose passes through untouched, at 1 it is fully clamped into the
            // slab, and in between it glides — so engaging/leaving the wall never snaps the whole body in depth.
            float w = Mathf.Clamp01(slabWeight);
            if (w <= 0.001f) return;

            float minDepth = Mathf.Max(0f, s.wallSurfaceDepth);
            float maxDepth = Mathf.Max(minDepth + 0.01f, s.maxBodyDepth);

            for (int i = 0; i < world.Length; i++)
            {
                float d = Depth(world[i], wallPoint, wallNormal);
                float clamped = Mathf.Clamp(d, minDepth, maxDepth);
                if (!Mathf.Approximately(clamped, d))
                {
                    float applied = Mathf.Lerp(d, clamped, w);
                    world[i] = SetDepth(world[i], applied, wallPoint, wallNormal);
                }
            }
        }

        /// <summary>
        /// Snap hand/foot tips ONTO the wall surface, but ONLY where there is a real (precomputed) climbing hold.
        /// This is the "pull-on" half of the wall constraint; the "push-out" half (depth slab + penetration IK)
        /// runs unconditionally elsewhere and keeps every limb out of the wall regardless of holds.
        ///
        /// There is deliberately NO live speed/stillness gate here. The <paramref name="holdMap"/> is built ahead of
        /// time by scanning the whole recording, so it already encodes WHERE the climber actually grips. At playback
        /// a limb latches purely on geometry against that map:
        ///   • it is OVER a known hold — within <see cref="WallProjectionSettings.holdSnapRadius"/> in the along-wall
        ///     (U/V) plane, of the matching kind (hand-hold vs foot-hold), AND
        ///   • it is CLOSE to the wall — within <see cref="WallProjectionSettings.contactDepthBand"/> in depth.
        /// A limb with no hold under it (e.g. a hand reaching through the air to the next hold) is left exactly where
        /// it is — it is never pulled onto a blank wall. Feet within
        /// <see cref="WallProjectionSettings.holdFloorExclusionBand"/> of the floor are treated as floor stands.
        /// Releasing happens geometrically too: a latched limb lets go once it leaves its hold's neighbourhood (moves
        /// on toward the next hold) or lifts clearly off the surface — no speed threshold, no hysteresis flicker.
        ///
        /// Returns the per-limb contact targets (also used to drive rig IK on the procedural + GLB paths). Requires a
        /// <paramref name="routeRoot"/> + a populated <paramref name="holdMap"/>; without holds nothing is snapped.
        /// </summary>
        public WallContact[] ResolveContacts(Vector3[] world, ContactJoint[] contactJoints, Vector3 wallPoint,
            Vector3 wallNormal, Transform routeRoot, float dt, bool hasFloor, float floorWorldY,
            ClimbingHoldMap holdMap, in WallProjectionSettings s)
        {
            for (int i = 0; i < contacts.Length; i++)
                contacts[i] = new WallContact { limb = (ClimbLimb)i, active = false, weight = 0f };

            if (!s.enableContactLock || world == null || contactJoints == null) return contacts;
            if (wallNormal.sqrMagnitude < 1e-8f) return contacts;
            wallNormal.Normalize();

            // Snapping is hold-driven: with no RouteRoot frame or no detected holds there is nothing to snap onto, so
            // the limbs are left to the push-out slab/IK only. This is the "leave it floating" path.
            if (routeRoot == null || holdMap == null || holdMap.Count == 0) return contacts;

            dt = Mathf.Max(1e-4f, dt);
            float surfaceDepth = Mathf.Max(0f, s.wallSurfaceDepth);
            float snapDepth = Mathf.Max(surfaceDepth + 0.02f, s.contactDepthBand);
            float releaseDepth = snapDepth * 1.8f;        // small depth hysteresis so a gripping limb doesn't flicker
            float snapRadius = Mathf.Max(0.01f, s.holdSnapRadius);
            float releaseRadius = snapRadius * 1.8f;       // U/V hysteresis: hold on a touch longer than we grab
            float easeW = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, s.contactEaseSeconds));
            float floorBand = Mathf.Max(0f, s.holdFloorExclusionBand);

            foreach (var cj in contactJoints)
            {
                if (cj.worldIndex < 0 || cj.worldIndex >= world.Length) continue;
                int li = (int)cj.limb;
                bool isHand = cj.limb == ClimbLimb.LeftHand || cj.limb == ClimbLimb.RightHand;
                ref LimbState st = ref limbs[li];

                Vector3 p = world[cj.worldIndex];
                float depth = Depth(p, wallPoint, wallNormal);
                // RouteRoot-local position for hold lookup (X along wall, Y up, Z depth) — the same frame holds are
                // stored in, so this matches whatever the offline detector / a future ML detector emitted.
                Vector3 routeLocal = routeRoot.InverseTransformPoint(p);
                float u = routeLocal.x, v = routeLocal.y;

                // A foot resting at floor height is standing on the ground, not gripping a wall hold.
                bool floorStand = !isHand && hasFloor && (p.y - floorWorldY) < floorBand;

                if (!st.inContact)
                {
                    // Grab: the limb is close to the wall AND sitting over a known hold of its kind.
                    if (!floorStand && depth <= snapDepth &&
                        holdMap.TryFindNearest(u, v, snapRadius, isHand, out var hold))
                    {
                        st.inContact = true;
                        st.weight = 0f;
                        st.lockedSurface = SetDepth(hold.ToWorld(routeRoot), surfaceDepth, wallPoint, wallNormal);
                    }
                }
                else
                {
                    // Release once the limb leaves the hold's neighbourhood or lifts clearly off the surface; keep
                    // re-tracking the same hold (drift-stable point) while it is still on it.
                    if (!floorStand && depth <= releaseDepth &&
                        holdMap.TryFindNearest(u, v, releaseRadius, isHand, out var hold))
                        st.lockedSurface = SetDepth(hold.ToWorld(routeRoot), surfaceDepth, wallPoint, wallNormal);
                    else
                    {
                        st.inContact = false;
                        st.weight = 0f;
                    }
                }

                if (st.inContact)
                {
                    st.weight = Mathf.Lerp(st.weight, s.maxContactWeight, easeW);

                    // Snap the world joint onto the locked hold so the procedural path (which aims bones at these
                    // joints) lands exactly on the hold, and emit the same target for the rig-IK path.
                    if (s.snapWorldJoints)
                        world[cj.worldIndex] = Vector3.Lerp(p, st.lockedSurface, st.weight);

                    contacts[li] = new WallContact
                    {
                        limb = cj.limb,
                        active = true,
                        targetWorld = st.lockedSurface,
                        weight = st.weight,
                        kind = ContactKind.Hold,
                    };
                }
                else
                {
                    st.weight = 0f;
                }
            }

            return contacts;
        }
    }
}
