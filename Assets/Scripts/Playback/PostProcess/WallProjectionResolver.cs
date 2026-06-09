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
        }

        struct LimbState
        {
            public Vector3 prevWorld;
            public bool hasPrev;
            public float speedEma;
            public bool inContact;
            public Vector3 lockedSurface; // latched world point on the wall skin
            public float weight;          // eased IK weight
        }

        // Indexed by (int)ClimbLimb.
        readonly LimbState[] limbs = new LimbState[4];
        readonly WallContact[] contacts = new WallContact[4];

        public void Reset()
        {
            for (int i = 0; i < limbs.Length; i++)
                limbs[i] = default;
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

            float minDepth = Mathf.Max(0f, s.wallSurfaceDepth);
            float maxDepth = Mathf.Max(minDepth + 0.01f, s.maxBodyDepth);

            for (int i = 0; i < world.Length; i++)
            {
                float d = Depth(world[i], wallPoint, wallNormal);
                float clamped = Mathf.Clamp(d, minDepth, maxDepth);
                if (!Mathf.Approximately(clamped, d))
                    world[i] = SetDepth(world[i], clamped, wallPoint, wallNormal);
            }
        }

        /// <summary>
        /// Detect hand/foot contacts and (optionally) snap+lock them onto the wall surface in the shared world array.
        /// The wall is given as a point + outward normal. Returns the per-limb contact targets (also usable to drive
        /// rig IK on the GLB path). <paramref name="dt"/> is the frame delta used for the stillness estimate.
        /// </summary>
        public WallContact[] ResolveContacts(Vector3[] world, ContactJoint[] contactJoints, Vector3 wallPoint,
            Vector3 wallNormal, float dt, in WallProjectionSettings s)
        {
            for (int i = 0; i < contacts.Length; i++)
                contacts[i] = new WallContact { limb = (ClimbLimb)i, active = false, weight = 0f };

            if (!s.enableContactLock || world == null || contactJoints == null) return contacts;
            if (wallNormal.sqrMagnitude < 1e-8f) return contacts;
            wallNormal.Normalize();

            dt = Mathf.Max(1e-4f, dt);
            float surfaceDepth = Mathf.Max(0f, s.wallSurfaceDepth);
            float enterSpeed = Mathf.Max(1e-3f, s.contactStillnessSpeed);
            float exitSpeed = Mathf.Max(enterSpeed + 1e-3f, s.contactReleaseSpeed);
            float depthBand = Mathf.Max(surfaceDepth + 0.01f, s.contactDepthBand);
            float easeW = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, s.contactEaseSeconds));

            foreach (var cj in contactJoints)
            {
                if (cj.worldIndex < 0 || cj.worldIndex >= world.Length) continue;
                int li = (int)cj.limb;
                ref LimbState st = ref limbs[li];

                Vector3 p = world[cj.worldIndex];

                // Low-pass speed so a single noisy frame can't flip the contact latch.
                float rawSpeed = st.hasPrev ? (p - st.prevWorld).magnitude / dt : 0f;
                float aSpeed = dt / (0.12f + dt);
                st.speedEma = st.hasPrev ? st.speedEma + aSpeed * (rawSpeed - st.speedEma) : 0f;
                st.prevWorld = p;
                st.hasPrev = true;

                float depth = Depth(p, wallPoint, wallNormal);

                // Hysteresis latch: grab a hold when near the wall AND barely moving; release when it clearly moves
                // away or lifts off the surface.
                if (!st.inContact)
                {
                    if (depth <= depthBand && st.speedEma <= enterSpeed)
                    {
                        st.inContact = true;
                        st.lockedSurface = SetDepth(p, surfaceDepth, wallPoint, wallNormal);
                        st.weight = 0f;
                    }
                }
                else
                {
                    bool movedAway = st.speedEma >= exitSpeed || depth > depthBand * 1.6f;
                    if (movedAway)
                    {
                        st.inContact = false;
                        st.weight = 0f;
                    }
                }

                if (st.inContact)
                {
                    st.weight = Mathf.Lerp(st.weight, s.maxContactWeight, easeW);

                    // Snap the world joint onto the locked surface point so the procedural path (which aims bones at
                    // these joints) lands exactly on the hold, and emit the same target for the rig-IK path.
                    if (s.snapWorldJoints)
                        world[cj.worldIndex] = Vector3.Lerp(p, st.lockedSurface, st.weight);

                    contacts[li] = new WallContact
                    {
                        limb = cj.limb,
                        active = true,
                        targetWorld = st.lockedSurface,
                        weight = st.weight,
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
