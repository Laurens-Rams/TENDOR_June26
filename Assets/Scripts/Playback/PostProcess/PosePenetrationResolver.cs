using UnityEngine;
using BodyTracking.AR;

namespace BodyTracking.Playback.PostProcess
{
    [System.Serializable]
    public struct PenetrationSettings
    {
        [Header("Floor (standing)")]
        [Tooltip("Lift the character so the lowest foot rests on the floor instead of sinking into it.")]
        public bool enableFloorFix;
        [Tooltip("How close (m) a foot must be to the floor for the pose to count as 'standing'.")]
        public float floorContactBand;
        [Tooltip("Climbing guard: only treat a pose as 'standing' (and apply the floor lift) when the hips are within " +
                 "this height (m) above the floor. Stops a mid-wall climbing pose — where a low foot happens to dangle " +
                 "near the floor plane — from lifting (floating) the whole climber. 0 = disabled (legacy behaviour).")]
        public float maxStandingHipHeightAboveFloor;
        [Tooltip("How far (m) a standing pose may be DROPPED down onto the floor when it is floating above it. The floor " +
                 "fix is bidirectional: a foot sunk into the floor is lifted up, and a foot floating above it (within this " +
                 "distance, and below the hip-height climbing guard) is snapped back down so the lowest foot rests on the " +
                 "floor. 0 = only correct feet that are at/below the floor (legacy: never drop a floating pose).")]
        public float maxFloorSnapMeters;

        [Header("Wall (climbing)")]
        [Tooltip("Pin a hand to the wall surface (two-bone IK) when it sinks through the wall.")]
        public bool enableWallHandIK;
        [Tooltip("Also pin feet to the wall when they sink through it.")]
        public bool enableWallFootIK;
        [Range(0f, 1f)] public float maxIkWeight;
        [Tooltip("Penetration depth (m) that maps to full IK weight; shallower clips get a gentler nudge.")]
        public float penetrationForFullWeight;

        [Header("Whole-body fallback (opt-in)")]
        [Tooltip("Push the entire skeleton out along the wall normal when many hands/feet are deep in the wall. " +
                 "Off by default — turning on the master penetration fix alone only runs floor + limb IK.")]
        public bool enableWholeBodyPush;
        [Tooltip("Hands/feet must penetrate at least this deep (m) before they count toward a whole-body push.")]
        public float minWholeBodyPenetration;
        [Tooltip("If more than this fraction of wall-contact joints (hands/feet) are deep in the wall, push the whole body.")]
        [Range(0f, 1f)] public float wholeBodyPenetrationFraction;
        [Tooltip("Cap the whole-body push per frame (m) so a bad surface probe can't teleport the character forward.")]
        public float maxWholeBodyPushMeters;

        [Tooltip("Skip all penetration fixes while the character is jumping (so a jump isn't snapped to the floor).")]
        public bool skipDuringJump;

        [Tooltip("Draw debug lines for surface hits, penetration vectors and IK targets (Scene view / dev builds).")]
        public bool debugDraw;

        public static PenetrationSettings Default => new PenetrationSettings
        {
            // Off until floorLocalY is calibrated on the probe (RouteRoot origin usually sits up the wall, not on
            // the floor) — otherwise a standing pose would be lifted to the wrong height.
            enableFloorFix = false,
            floorContactBand = 0.12f,
            maxStandingHipHeightAboveFloor = 1.3f,
            maxFloorSnapMeters = 0.5f,
            enableWallHandIK = true,
            enableWallFootIK = true,
            maxIkWeight = 1f,
            penetrationForFullWeight = 0.08f,
            enableWholeBodyPush = false,
            minWholeBodyPenetration = 0.04f,
            wholeBodyPenetrationFraction = 0.75f,
            maxWholeBodyPushMeters = 0.12f,
            skipDuringJump = true,
            debugDraw = false,
        };
    }

    /// <summary>
    /// Keeps the character from clipping into the real wall/floor. Two phases, both character-agnostic:
    ///   • <see cref="ResolveBodyAndFloor"/> runs on the shared world joint array BEFORE the hips are pinned:
    ///     standing -> lift so the lowest foot sits on the floor; whole-body-in-wall -> push out along the normal.
    ///   • <see cref="ResolveLimbs"/> runs on the rig bones AFTER the pose is written: for each penetrating hand
    ///     (and optionally foot) it pins the tip to the wall surface via two-bone IK, leaving the rest of the body
    ///     untouched — so a correct body with one bad hand becomes a correct body with a correct hand.
    /// </summary>
    public sealed class PosePenetrationResolver
    {
        /// <summary>Per-limb result of the contact push-out, for debug coloring.</summary>
        public enum LimbPushStatus { None, Anchored, PushedOut, Inside }

        public struct LimbPush
        {
            public LimbPushStatus status;
            public Vector3 world; // the limb tip world position after correction
        }

        ARSurfaceProbe probe;
        bool lastStanding;

        // Indexed by (int)WallProjectionResolver.ClimbLimb (LeftHand, RightHand, LeftFoot, RightFoot).
        readonly LimbPush[] limbPush = new LimbPush[4];

        public void SetProbe(ARSurfaceProbe p) => probe = p;
        public bool LastClassifiedStanding => lastStanding;

        /// <summary>Latest per-limb push-out status (for the debug overlay). Index = ClimbLimb.</summary>
        public System.Collections.Generic.IReadOnlyList<LimbPush> LastLimbPush => limbPush;

        /// <summary>
        /// World-array phase. <paramref name="footWorldIndices"/> are joint indices of the feet (ankles/toes).
        /// Mutates <paramref name="world"/> in place. Returns the standing/climbing classification.
        /// </summary>
        public bool ResolveBodyAndFloor(Vector3[] world, int rootIndex, int[] footWorldIndices, int[] wallContactIndices,
            bool jumping, in PenetrationSettings s)
        {
            if (world == null || probe == null) return lastStanding;

            // Whole-body push is opt-in: only when hands/feet are genuinely deep in the wall, not on every master-toggle on.
            if (s.enableWholeBodyPush && wallContactIndices != null && wallContactIndices.Length > 0)
            {
                float deepest = 0f;
                Vector3 pushNormal = Vector3.zero;
                int deepContacts = 0;
                float minPen = Mathf.Max(0.005f, s.minWholeBodyPenetration);
                foreach (int i in wallContactIndices)
                {
                    if (i < 0 || i >= world.Length) continue;
                    if (!probe.TryWall(world[i], out var h) || !h.inside || h.penetration < minPen) continue;
                    deepContacts++;
                    if (h.penetration > deepest)
                    {
                        deepest = h.penetration;
                        pushNormal = h.normal;
                    }
                }

                int need = Mathf.Max(1, Mathf.CeilToInt(wallContactIndices.Length * Mathf.Clamp01(s.wholeBodyPenetrationFraction)));
                if (deepContacts >= need && deepest > 0f && pushNormal.sqrMagnitude > 1e-6f)
                {
                    float cap = Mathf.Max(0.01f, s.maxWholeBodyPushMeters);
                    deepest = Mathf.Min(deepest, cap);
                    Vector3 push = pushNormal.normalized * deepest;
                    for (int i = 0; i < world.Length; i++) world[i] += push;
                }
            }

            // Standing classification + floor lift.
            bool standing = false;
            float queryY = (rootIndex >= 0 && rootIndex < world.Length) ? world[rootIndex].y : float.NaN;
            if (footWorldIndices != null && footWorldIndices.Length > 0 && probe.TryFloorWorldY(out float floorY, queryY))
            {
                float lowestFoot = float.MaxValue;
                foreach (int fi in footWorldIndices)
                    if (fi >= 0 && fi < world.Length) lowestFoot = Mathf.Min(lowestFoot, world[fi].y);

                if (lowestFoot < float.MaxValue)
                {
                    float footAboveFloor = lowestFoot - floorY; // <0 sunk into floor, >0 floating above it

                    // Climbing guard: a mid-wall pose can have a trailing foot dangle near the floor plane. Only treat
                    // a pose as standing (and snap it to the floor) when the hips are low enough to plausibly be grounded.
                    bool hipsLowEnough = s.maxStandingHipHeightAboveFloor <= 0f || float.IsNaN(queryY) ||
                                         (queryY - floorY) <= s.maxStandingHipHeightAboveFloor;

                    // The downward snap range: how far a floating pose may be pulled back onto the floor. Falls back to
                    // the contact band when not configured, so the snap is at least symmetric with the touch tolerance.
                    float snapDown = Mathf.Max(s.floorContactBand, Mathf.Max(0f, s.maxFloorSnapMeters));

                    // "Standing" = the lowest foot is at/below the floor or floating within the snap range, and the hips
                    // are low enough. This now includes floating ground poses so they get pulled DOWN, not just lifted up.
                    standing = hipsLowEnough && footAboveFloor <= snapDown;

                    if (standing && !jumping && s.enableFloorFix)
                    {
                        // Bidirectional: lift up when the foot is sunk into the floor, drop down when it floats above it,
                        // so the lowest foot ends up resting exactly on the floor plane.
                        float delta = floorY - lowestFoot; // >0 lift up, <0 drop down
                        // Cap the downward drop so a noisy floor probe can't slam a high pose to the ground.
                        if (delta < 0f)
                            delta = Mathf.Max(delta, -snapDown);
                        if (Mathf.Abs(delta) > 1e-4f)
                            for (int i = 0; i < world.Length; i++) world[i] += new Vector3(0f, delta, 0f);
                    }

                    if (s.debugDraw && rootIndex >= 0 && rootIndex < world.Length)
                    {
                        Vector3 hipXZ = world[rootIndex];
                        Debug.DrawLine(new Vector3(hipXZ.x - 0.5f, floorY, hipXZ.z), new Vector3(hipXZ.x + 0.5f, floorY, hipXZ.z),
                            standing ? Color.green : Color.gray);
                    }
                }
            }

            lastStanding = standing;
            return standing;
        }

        /// <summary>
        /// Bone phase. Pins penetrating hands/feet to the wall surface with two-bone IK. Only runs the wall fixes
        /// when climbing (not standing) so a grounded pose isn't disturbed.
        /// </summary>
        public void ResolveLimbs(Animator animator, bool jumping, in PenetrationSettings s)
        {
            if (animator == null || probe == null || !animator.isHuman) return;
            if (jumping && s.skipDuringJump) return;
            if (lastStanding) return; // wall IK is for climbing poses

            if (s.enableWallHandIK)
            {
                SolveLimbToWall(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, s);
                SolveLimbToWall(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, s);
            }
            if (s.enableWallFootIK)
            {
                // Effector is the TOE (front of the shoe) — that's what touches the wall in climbing — falling back to
                // the ankle only on rigs with no toe bone.
                SolveLimbToWall(animator, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, FootTip(animator, HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot), s);
                SolveLimbToWall(animator, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, FootTip(animator, HumanBodyBones.RightToes, HumanBodyBones.RightFoot), s);
            }
        }

        /// <summary>
        /// Climbing contact pass: pin each hand/foot that <see cref="WallProjectionResolver"/> has latched onto a hold
        /// to its locked wall-surface point via two-bone IK. Unlike <see cref="ResolveLimbs"/> (which only pushes a
        /// limb OUT when it penetrates), this also pulls a contact limb IN onto the surface, so a hand that ARKit/Move
        /// floated slightly off the wall is brought back onto the hold it is actually gripping. Runs on the humanoid
        /// rig after the pose is written, so it works for both the procedural and GLB articulation paths.
        /// </summary>
        public void ResolveContactLimbs(Animator animator, WallProjectionResolver.WallContact[] contacts, bool jumping, in PenetrationSettings s)
        {
            if (animator == null || !animator.isHuman || contacts == null) return;
            if (jumping && s.skipDuringJump) return;

            foreach (var c in contacts)
            {
                if (!c.active || c.weight <= 0f) continue;
                switch (c.limb)
                {
                    case WallProjectionResolver.ClimbLimb.LeftHand:
                        PinLimb(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, c.targetWorld, c.weight);
                        break;
                    case WallProjectionResolver.ClimbLimb.RightHand:
                        PinLimb(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, c.targetWorld, c.weight);
                        break;
                    case WallProjectionResolver.ClimbLimb.LeftFoot:
                        PinLimb(animator, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, FootTip(animator, HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot), c.targetWorld, c.weight);
                        break;
                    case WallProjectionResolver.ClimbLimb.RightFoot:
                        PinLimb(animator, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, FootTip(animator, HumanBodyBones.RightToes, HumanBodyBones.RightFoot), c.targetWorld, c.weight);
                        break;
                }
            }
        }

        /// <summary>
        /// Always-on contact push-out: for every hand/foot tip that sits BEHIND the wall skin (depth &lt; the slab's
        /// wall-surface depth), pin the tip back onto the surface with two-bone IK. Unlike <see cref="ResolveLimbs"/>
        /// this uses the SAME flat wall plane the depth slab uses (passed in) instead of the AR probe, and it is NOT
        /// gated by the standing classification — so a GLB limb that the slab can't reach (the slab only moves world
        /// joints, not the muscle-space mesh) is reliably brought out of the wall regardless of pose. Limbs already
        /// latched to a hold (<paramref name="holdContacts"/> with kind Hold) are skipped so the two passes don't fight.
        /// Records per-limb status for the debug overlay (green = on hold, yellow = pushed out, red = still inside).
        /// </summary>
        public void ResolveContactPlane(Animator animator, WallProjectionResolver.WallContact[] holdContacts,
            Vector3 wallPoint, Vector3 wallNormal, float surfaceDepth, bool jumping, in PenetrationSettings s)
        {
            for (int i = 0; i < limbPush.Length; i++)
                limbPush[i] = new LimbPush { status = LimbPushStatus.None, world = Vector3.zero };

            if (animator == null || !animator.isHuman) return;
            if (jumping && s.skipDuringJump) return;
            if (wallNormal.sqrMagnitude < 1e-8f) return;
            wallNormal.Normalize();

            if (s.enableWallHandIK)
            {
                PushTipToPlane(animator, WallProjectionResolver.ClimbLimb.LeftHand, holdContacts,
                    HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                    wallPoint, wallNormal, surfaceDepth, s);
                PushTipToPlane(animator, WallProjectionResolver.ClimbLimb.RightHand, holdContacts,
                    HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                    wallPoint, wallNormal, surfaceDepth, s);
            }
            if (s.enableWallFootIK)
            {
                PushTipToPlane(animator, WallProjectionResolver.ClimbLimb.LeftFoot, holdContacts,
                    HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, FootTip(animator, HumanBodyBones.LeftToes, HumanBodyBones.LeftFoot),
                    wallPoint, wallNormal, surfaceDepth, s);
                PushTipToPlane(animator, WallProjectionResolver.ClimbLimb.RightFoot, holdContacts,
                    HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, FootTip(animator, HumanBodyBones.RightToes, HumanBodyBones.RightFoot),
                    wallPoint, wallNormal, surfaceDepth, s);
            }
        }

        void PushTipToPlane(Animator animator, WallProjectionResolver.ClimbLimb limb,
            WallProjectionResolver.WallContact[] holdContacts, HumanBodyBones rootBone, HumanBodyBones midBone,
            HumanBodyBones tipBone, Vector3 wallPoint, Vector3 wallNormal, float surfaceDepth, in PenetrationSettings s)
        {
            int li = (int)limb;
            // A limb anchored to a hold is already being pinned to the surface by the hold pass — leave it alone.
            if (holdContacts != null && li < holdContacts.Length && holdContacts[li].active &&
                holdContacts[li].kind == WallProjectionResolver.ContactKind.Hold)
            {
                limbPush[li] = new LimbPush { status = LimbPushStatus.Anchored, world = holdContacts[li].targetWorld };
                return;
            }

            Transform root = animator.GetBoneTransform(rootBone);
            Transform mid = animator.GetBoneTransform(midBone);
            Transform tip = animator.GetBoneTransform(tipBone);
            if (root == null || mid == null || tip == null) return;

            float depth = Vector3.Dot(tip.position - wallPoint, wallNormal);
            float pen = surfaceDepth - depth; // >0 => the tip is behind the wall skin (inside the wall)
            if (pen <= 0f)
            {
                limbPush[li] = new LimbPush { status = LimbPushStatus.None, world = tip.position };
                return;
            }

            Vector3 target = tip.position + pen * wallNormal; // straight out onto the wall skin
            float weight = Mathf.Clamp01(s.maxIkWeight <= 0f ? 1f : s.maxIkWeight);
            Vector3 hint = mid.position + wallNormal * 0.1f;

            if (s.debugDraw)
            {
                Debug.DrawLine(tip.position, target, Color.red);
                Debug.DrawRay(target, wallNormal * 0.1f, Color.yellow);
            }

            LimbIKSolver.Solve(root, mid, tip, target, weight, hint);

            // Re-measure after the solve: if the tip reached the skin it's a clean push-out (yellow); if it still
            // can't reach (limb too short / weight capped) flag it inside (red) so it's visible in the overlay.
            float postDepth = Vector3.Dot(tip.position - wallPoint, wallNormal);
            limbPush[li] = new LimbPush
            {
                status = postDepth < surfaceDepth - 0.01f ? LimbPushStatus.Inside : LimbPushStatus.PushedOut,
                world = tip.position,
            };
        }

        // Prefer the toe bone (front of the shoe) as the foot IK effector; fall back to the ankle on rigs that have
        // no mapped toe bone. With the toe as the tip, the two-bone solver (hip→knee→toe) places the FRONT of the foot
        // on the wall, keeping the ankle behind it — instead of jamming the heel/ankle into the wall.
        static HumanBodyBones FootTip(Animator animator, HumanBodyBones toes, HumanBodyBones foot)
            => animator.GetBoneTransform(toes) != null ? toes : foot;

        void PinLimb(Animator animator, HumanBodyBones rootBone, HumanBodyBones midBone, HumanBodyBones tipBone, Vector3 target, float weight)
        {
            Transform root = animator.GetBoneTransform(rootBone);
            Transform mid = animator.GetBoneTransform(midBone);
            Transform tip = animator.GetBoneTransform(tipBone);
            if (root == null || mid == null || tip == null) return;

            // Only pin when the tip is actually off the hold; tiny corrections aren't worth a rig change.
            if ((tip.position - target).sqrMagnitude < 1e-4f) return;

            Vector3 hint = probe != null && probe.TryWall(mid.position, out var h)
                ? mid.position + h.normal * 0.1f
                : mid.position;
            LimbIKSolver.Solve(root, mid, tip, target, weight, hint);
        }

        void SolveLimbToWall(Animator animator, HumanBodyBones rootBone, HumanBodyBones midBone, HumanBodyBones tipBone, in PenetrationSettings s)
        {
            Transform root = animator.GetBoneTransform(rootBone);
            Transform mid = animator.GetBoneTransform(midBone);
            Transform tip = animator.GetBoneTransform(tipBone);
            if (root == null || mid == null || tip == null) return;

            if (!probe.TryWall(tip.position, out var hit) || !hit.inside || hit.penetration <= 0f)
                return;

            float weight = s.maxIkWeight * Mathf.Clamp01(hit.penetration / Mathf.Max(1e-4f, s.penetrationForFullWeight));
            if (weight <= 0f) return;

            if (s.debugDraw)
            {
                Debug.DrawLine(tip.position, hit.surfacePoint, Color.red);             // penetration vector
                Debug.DrawRay(hit.surfacePoint, hit.normal * 0.1f, Color.yellow);      // surface normal
            }

            // Hint biases the bend AWAY from the wall (along the outward normal) only when the limb is straight.
            Vector3 hint = mid.position + hit.normal * 0.1f;
            LimbIKSolver.Solve(root, mid, tip, hit.surfacePoint, weight, hint);
        }
    }
}
