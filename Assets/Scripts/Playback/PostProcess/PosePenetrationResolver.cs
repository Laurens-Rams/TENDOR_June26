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
        ARSurfaceProbe probe;
        bool lastStanding;

        public void SetProbe(ARSurfaceProbe p) => probe = p;
        public bool LastClassifiedStanding => lastStanding;

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
                    standing = lowestFoot <= floorY + s.floorContactBand;
                    if (standing && !jumping && s.enableFloorFix && lowestFoot < floorY)
                    {
                        float lift = floorY - lowestFoot; // only lift up; never push a floating pose down
                        for (int i = 0; i < world.Length; i++) world[i] += new Vector3(0f, lift, 0f);
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
                SolveLimbToWall(animator, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, s);
                SolveLimbToWall(animator, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, s);
            }
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
