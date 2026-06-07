using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>Serialized tunables for <see cref="PosePostProcessor"/> (glitch guard + adaptive smoothing).</summary>
    [System.Serializable]
    public struct PosePostProcessSettings
    {
        [Header("Glitch guard")]
        [Tooltip("Reject NaN/Inf, single-frame teleport spikes, and bone-length pops (hold last good briefly).")]
        public bool enableGlitchGuard;
        [Tooltip("Per-joint speed (m/s, RouteRoot-local) above which a frame is treated as a teleport glitch (unless jumping).")]
        public float maxJointSpeed;
        [Tooltip("Fractional bone-length change vs the reference skeleton above which a joint is snapped back to the reference length. 0 disables.")]
        public float boneLengthTolerance;

        [Header("Smoothing (1euro)")]
        [Tooltip("Adaptive jitter smoothing that stays responsive during fast motion.")]
        public bool enableSmoothing;
        [Tooltip("1euro min cutoff (Hz): lower = more jitter removed but more lag while slow.")]
        public float minCutoff;
        [Tooltip("1euro beta: higher = less lag while fast.")]
        public float beta;
        [Tooltip("Also smooth the ROOT (whole-body translation across the room). OFF (default) so walking/climbing keeps its full travel — only the pose (joints relative to the root) is smoothed. Turn ON only if the whole body jitters in place.")]
        public bool smoothRootTranslation;

        [Header("Jump preservation")]
        [Tooltip("Pelvis upward speed (m/s) above which we treat motion as a jump and reduce smoothing so it isn't damped.")]
        public float jumpVelocityThreshold;
        [Tooltip("Beta is multiplied by this while jumping (>1 = sharper/less lag during the jump).")]
        public float jumpBetaScale;

        public static PosePostProcessSettings Default => new PosePostProcessSettings
        {
            enableGlitchGuard = true,
            maxJointSpeed = 12f,
            boneLengthTolerance = 0.4f,
            enableSmoothing = true,
            minCutoff = 1.0f,
            beta = 0.01f,
            smoothRootTranslation = false,
            jumpVelocityThreshold = 1.5f,
            jumpBetaScale = 8f,
        };
    }

    /// <summary>
    /// Character-agnostic per-frame cleanup of a skeleton's joint POSITIONS (run in RouteRoot-local space so phone
    /// motion is never smoothed, only the pose). Two stages:
    ///   1. Glitch guard — drop NaN/Inf, single-frame teleport spikes, and bone-length pops (hold last good for a
    ///      couple of frames, then accept so it recovers from real fast moves / loop restarts).
    ///   2. 1euro adaptive smoothing — kills jitter while still, stays low-latency while fast; a jump (pelvis rising
    ///      fast) temporarily reduces smoothing so it isn't damped.
    /// Shared by the procedural and MoveGlb paths (both produce the same local joint array upstream).
    /// </summary>
    public sealed class PosePostProcessor
    {
        const int MaxConsecutiveRejections = 2;

        int _n = -1;
        int[] _parents;
        OneEuroFilterVector3[] _filters;
        Vector3[] _prev;
        bool[] _hasPrev;
        int[] _rejectStreak;
        float[] _refBoneLen;
        bool[] _hasRefBoneLen;

        /// <summary>True if the most recent <see cref="Process"/> classified the motion as a jump (pelvis rising fast).</summary>
        public bool LastJumping { get; private set; }

        /// <summary>(Re)allocate for a given joint count + parent topology. Safe to call when the rig changes.</summary>
        public void Configure(int jointCount, System.Collections.Generic.IList<int> jointParents)
        {
            if (jointCount <= 0) { _n = -1; return; }
            _n = jointCount;
            _parents = new int[jointCount];
            for (int i = 0; i < jointCount; i++)
                _parents[i] = (jointParents != null && i < jointParents.Count) ? jointParents[i] : -1;

            _filters = new OneEuroFilterVector3[jointCount];
            for (int i = 0; i < jointCount; i++)
                _filters[i] = new OneEuroFilterVector3();
            _prev = new Vector3[jointCount];
            _hasPrev = new bool[jointCount];
            _rejectStreak = new int[jointCount];
            _refBoneLen = new float[jointCount];
            _hasRefBoneLen = new bool[jointCount];
        }

        public void Reset()
        {
            if (_n <= 0) return;
            for (int i = 0; i < _n; i++)
            {
                _filters[i]?.Reset();
                _hasPrev[i] = false;
                _rejectStreak[i] = 0;
                _hasRefBoneLen[i] = false;
            }
        }

        /// <summary>Clean <paramref name="joints"/> in place (RouteRoot-local space).</summary>
        public void Process(Vector3[] joints, int rootIndex, float dt, in PosePostProcessSettings s)
        {
            if (joints == null) return;
            if (_n != joints.Length)
                Configure(joints.Length, _parents); // size changed without explicit Configure
            if (dt <= 0f) dt = 1f / 60f;

            bool jumping = false;
            if (rootIndex >= 0 && rootIndex < _n && _hasPrev[rootIndex])
            {
                float rootVelUp = (joints[rootIndex].y - _prev[rootIndex].y) / dt;
                jumping = rootVelUp > s.jumpVelocityThreshold;
            }
            LastJumping = jumping;

            if (s.enableGlitchGuard)
                GlitchGuard(joints, dt, jumping, s);

            if (s.enableSmoothing)
            {
                float beta = jumping ? s.beta * Mathf.Max(1f, s.jumpBetaScale) : s.beta;
                bool hasRoot = rootIndex >= 0 && rootIndex < _n;

                // The root's RouteRoot-local position IS the body's translation across the room. Smoothing it would
                // damp the walk/climb (lag + lost travel). So smooth every OTHER joint RELATIVE to the root (pose
                // only) and let the root translation pass through 1:1 unless explicitly asked to smooth it.
                Vector3 root = hasRoot ? joints[rootIndex] : Vector3.zero;
                Vector3 smoothedRoot = root;
                if (hasRoot)
                {
                    if (s.smoothRootTranslation)
                    {
                        _filters[rootIndex].SetParams(s.minCutoff, beta);
                        smoothedRoot = _filters[rootIndex].Filter(root, dt);
                    }
                    joints[rootIndex] = smoothedRoot;
                }

                for (int i = 0; i < _n; i++)
                {
                    if (hasRoot && i == rootIndex) continue;
                    _filters[i].SetParams(s.minCutoff, beta);
                    if (hasRoot)
                    {
                        Vector3 rel = joints[i] - root;            // pose offset (no world translation)
                        rel = _filters[i].Filter(rel, dt);          // smooth only the pose
                        joints[i] = smoothedRoot + rel;
                    }
                    else
                    {
                        joints[i] = _filters[i].Filter(joints[i], dt);
                    }
                }
            }

            for (int i = 0; i < _n; i++)
            {
                _prev[i] = joints[i];
                _hasPrev[i] = true;
            }
        }

        void GlitchGuard(Vector3[] joints, float dt, bool jumping, in PosePostProcessSettings s)
        {
            for (int i = 0; i < _n; i++)
            {
                Vector3 p = joints[i];

                // NaN/Inf: hold last good.
                if (IsBad(p))
                {
                    if (_hasPrev[i]) joints[i] = _prev[i];
                    continue;
                }

                // Teleport spike: hold last good briefly, then accept (recovers from real fast moves / loop wrap).
                if (_hasPrev[i] && s.maxJointSpeed > 0f && !jumping)
                {
                    float speed = (p - _prev[i]).magnitude / dt;
                    if (speed > s.maxJointSpeed)
                    {
                        _rejectStreak[i]++;
                        if (_rejectStreak[i] <= MaxConsecutiveRejections)
                        {
                            joints[i] = _prev[i];
                            continue;
                        }
                    }
                    else
                    {
                        _rejectStreak[i] = 0;
                    }
                }

                // Bone-length pop: keep the direction from the parent but snap to the reference length.
                if (s.boneLengthTolerance > 0f && _parents != null)
                {
                    int par = _parents[i];
                    if (par >= 0 && par < _n)
                    {
                        Vector3 toChild = joints[i] - joints[par];
                        float len = toChild.magnitude;
                        if (len > 1e-5f)
                        {
                            if (!_hasRefBoneLen[i])
                            {
                                _refBoneLen[i] = len;
                                _hasRefBoneLen[i] = true;
                            }
                            else
                            {
                                float refLen = _refBoneLen[i];
                                float change = Mathf.Abs(len - refLen) / Mathf.Max(1e-5f, refLen);
                                if (change > s.boneLengthTolerance)
                                    joints[i] = joints[par] + toChild * (refLen / len);
                                else
                                    _refBoneLen[i] = Mathf.Lerp(refLen, len, 0.05f); // slow adapt to scale
                            }
                        }
                    }
                }
            }
        }

        static bool IsBad(Vector3 v) =>
            float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
    }
}
