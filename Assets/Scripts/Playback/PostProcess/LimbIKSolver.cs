using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// Analytic two-bone IK (law of cosines) on three bone <see cref="Transform"/>s. It reads world-space joint
    /// positions but writes the result as LOCAL rotations on the root + mid bones, so the forearm/shin correctly
    /// inherits the upper bone's swing (the mid bone only adds the elbow/knee bend on top). Works on ANY humanoid rig
    /// (FBX or GLB) regardless of proportions. Run it in LateUpdate AFTER the pose is written (the procedural bone
    /// solver or <c>SetHumanPose</c>), to nudge a single limb's tip onto a target (e.g. a hand onto the wall surface)
    /// while keeping the rest of the body untouched.
    ///
    /// It preserves the limb's CURRENT bend plane by default (elbow/knee won't flip); a hint only matters when the
    /// limb is nearly straight. Based on the standard 3-rotation construction (Holden / Unity rigging math).
    /// </summary>
    public static class LimbIKSolver
    {
        /// <summary>
        /// Bend <paramref name="root"/>-<paramref name="mid"/>-<paramref name="tip"/> so <paramref name="tip"/>
        /// reaches <paramref name="targetPos"/>. <paramref name="weight"/> 0..1 blends from the original pose.
        /// <paramref name="hint"/> (optional) is a world-space pole that disambiguates the bend direction only when
        /// the limb is straight.
        /// </summary>
        public static void Solve(Transform root, Transform mid, Transform tip, Vector3 targetPos, float weight = 1f, Vector3? hint = null)
        {
            if (root == null || mid == null || tip == null) return;
            weight = Mathf.Clamp01(weight);
            if (weight <= 0f) return;

            Vector3 a = root.position;
            Vector3 b = mid.position;
            Vector3 c = tip.position;

            Vector3 ab = b - a;
            Vector3 cb = b - c;
            Vector3 ac = c - a;
            Vector3 at = targetPos - a;

            float lab = ab.magnitude;
            float lcb = cb.magnitude;
            if (lab < 1e-5f || lcb < 1e-5f) return;
            float lat = Mathf.Clamp(at.magnitude, 1e-4f, lab + lcb - 1e-4f);

            // Current interior angles.
            float ac_ab_0 = AngleRad(ac, ab);
            float ba_bc_0 = AngleRad(a - b, c - b);
            float ac_at_0 = AngleRad(ac, at);

            // Desired interior angles for the new triangle (law of cosines).
            float ac_ab_1 = SafeAcos((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat));
            float ba_bc_1 = SafeAcos((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb));

            // Bend-plane normal (world): keep the current plane (cross of ac, ab). Fall back to the hint, then an
            // arbitrary perpendicular, when the limb is straight (ac ∥ ab).
            Vector3 bendAxis = Vector3.Cross(ac, ab);
            if (bendAxis.sqrMagnitude < 1e-8f && hint.HasValue)
                bendAxis = Vector3.Cross(ac, hint.Value - a);
            if (bendAxis.sqrMagnitude < 1e-8f)
                bendAxis = Vector3.Cross(ac, Mathf.Abs(ac.normalized.y) < 0.99f ? Vector3.up : Vector3.right);
            if (bendAxis.sqrMagnitude < 1e-8f) return;
            bendAxis = bendAxis.normalized;

            // Swing axis (world): rotate the limb so the tip direction (ac) points at the target (at).
            Vector3 swingAxis = Vector3.Cross(ac, at);

            Quaternion aRot0 = root.rotation;
            Quaternion bRot0 = mid.rotation;

            // Build the rotations as LOCAL rotations (world axis brought into each bone's local space) and apply them
            // by multiplying the local rotation. This is the key: the swing + bend on the ROOT are inherited by the
            // forearm/shin (it is a child), and the elbow/knee bend is layered on top in the mid bone's local space.
            // The previous version set mid.rotation to an absolute world value, which dropped the root's swing from
            // the forearm — so the elbow moved toward the target but the hand/foot never reached it.
            Quaternion r0 = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, Quaternion.Inverse(aRot0) * bendAxis);
            Quaternion r1 = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, Quaternion.Inverse(bRot0) * bendAxis);
            Quaternion r2 = swingAxis.sqrMagnitude > 1e-8f
                ? Quaternion.AngleAxis(ac_at_0 * Mathf.Rad2Deg, Quaternion.Inverse(aRot0) * swingAxis.normalized)
                : Quaternion.identity;

            Quaternion aLocal0 = root.localRotation;
            Quaternion bLocal0 = mid.localRotation;
            Quaternion aSolved = aLocal0 * r0 * r2;
            Quaternion bSolved = bLocal0 * r1;

            root.localRotation = weight < 1f ? Quaternion.Slerp(aLocal0, aSolved, weight) : aSolved;
            mid.localRotation = weight < 1f ? Quaternion.Slerp(bLocal0, bSolved, weight) : bSolved;
        }

        static float AngleRad(Vector3 u, Vector3 v)
        {
            float d = Vector3.Dot(u.normalized, v.normalized);
            return Mathf.Acos(Mathf.Clamp(d, -1f, 1f));
        }

        static float SafeAcos(float x) => Mathf.Acos(Mathf.Clamp(x, -1f, 1f));
    }
}
