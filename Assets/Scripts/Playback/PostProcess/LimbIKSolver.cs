using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// Analytic two-bone IK (law of cosines), applied directly to three bone <see cref="Transform"/>s in world space
    /// so it works on ANY humanoid rig (FBX or GLB) regardless of proportions. Run it in LateUpdate AFTER the pose
    /// is written (the procedural bone solver or <c>SetHumanPose</c>), to nudge a single limb's tip onto a target
    /// (e.g. a hand onto the wall surface) while keeping the rest of the body untouched.
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

            Quaternion aRot0 = root.rotation;
            Quaternion bRot0 = mid.rotation;

            Vector3 a = root.position;
            Vector3 b = mid.position;
            Vector3 c = tip.position;

            float lab = Vector3.Distance(a, b);
            float lcb = Vector3.Distance(b, c);
            if (lab < 1e-5f || lcb < 1e-5f) return;

            float lat = Mathf.Clamp(Vector3.Distance(a, targetPos), 1e-4f, lab + lcb - 1e-4f);

            Vector3 ac = c - a;
            Vector3 ab = b - a;
            Vector3 at = targetPos - a;

            // Current interior angles.
            float ac_ab_0 = AngleRad(ac, ab);
            float ba_bc_0 = AngleRad(a - b, c - b);

            // Desired interior angles for the new triangle (law of cosines).
            float ac_ab_1 = SafeAcos((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat));
            float ba_bc_1 = SafeAcos((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb));

            // Bend-plane normal: keep the current plane (cross of ac, ab). Fall back to the hint, then an arbitrary
            // perpendicular, when the limb is straight (ac ∥ ab).
            Vector3 bendAxis = Vector3.Cross(ac, ab);
            if (bendAxis.sqrMagnitude < 1e-8f && hint.HasValue)
                bendAxis = Vector3.Cross(ac, hint.Value - a);
            if (bendAxis.sqrMagnitude < 1e-8f)
                bendAxis = Vector3.Cross(ac, Mathf.Abs(ac.y) < 0.99f ? Vector3.up : Vector3.right);
            if (bendAxis.sqrMagnitude < 1e-8f) return;
            bendAxis = bendAxis.normalized;

            // Swing axis: rotate the (original) ac direction onto the target direction.
            Vector3 swingAxis = Vector3.Cross(ac, at);
            float swing = AngleRad(ac, at);

            Quaternion r0 = Quaternion.AngleAxis((ac_ab_1 - ac_ab_0) * Mathf.Rad2Deg, bendAxis);
            Quaternion r1 = Quaternion.AngleAxis((ba_bc_1 - ba_bc_0) * Mathf.Rad2Deg, bendAxis);

            Quaternion aFinal = r0 * aRot0;
            if (swingAxis.sqrMagnitude > 1e-8f)
                aFinal = Quaternion.AngleAxis(swing * Mathf.Rad2Deg, swingAxis.normalized) * aFinal;
            Quaternion bFinal = r1 * bRot0;

            if (weight < 1f)
            {
                aFinal = Quaternion.Slerp(aRot0, aFinal, weight);
                bFinal = Quaternion.Slerp(bRot0, bFinal, weight);
            }

            root.rotation = aFinal;
            mid.rotation = bFinal; // absolute world rotation; Unity reparents correctly after root moved
        }

        static float AngleRad(Vector3 u, Vector3 v)
        {
            float d = Vector3.Dot(u.normalized, v.normalized);
            return Mathf.Acos(Mathf.Clamp(d, -1f, 1f));
        }

        static float SafeAcos(float x) => Mathf.Acos(Mathf.Clamp(x, -1f, 1f));
    }
}
