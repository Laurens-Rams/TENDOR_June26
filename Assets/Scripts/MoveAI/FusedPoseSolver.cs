using UnityEngine;
using BodyTracking.Data;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Shared geometry for placing the Move AI pose into the AR scene. Produces Move joint positions in
    /// RouteRoot-local space for a given time, anchored on the ARKit pelvis center and yaw-aligned to the
    /// ARKit facing. Both the compare overlay and the character retargeter use this so the driven character
    /// lands exactly on the orange skeleton.
    ///
    /// The Move offsets are reconstructed by <see cref="MoveMotion.ForwardKinematics"/> (which sums the stored
    /// world-frame deltas), then re-anchored: the brittle Move root drift is discarded in favour of the
    /// world-anchored ARKit hip, and a single yaw lines up the body's facing with the recorded climber.
    /// </summary>
    public static class FusedPoseSolver
    {
        // ARKit 3D body skeleton joint indices (see ARHumanBodyManager 3D joint table).
        public const int ArkitRoot = 0;
        public const int ArkitHips = 1;
        public const int ArkitLeftUpLeg = 2;
        public const int ArkitRightUpLeg = 7;

        // Minimum horizontal hip-socket separation (m) for the ARKit pelvis axis to be trusted for facing.
        // When the tracked body is lost the legs collapse and this axis degenerates into spinning noise.
        const float MinHipAxis = 0.08f;
        // Max plausible per-update pelvis jump (m). Larger deltas are treated as a tracking glitch, not motion.
        const float MaxAnchorJump = 1.0f;
        // Facing smoothing: fraction of the way the held yaw moves toward a new valid measurement each frame.
        // Low value = very stable (heavily damped) facing that can't flicker/flip from per-frame hip-axis noise.
        const float FacingSmoothing = 0.12f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // #region agent log
        static int dbgTick;
        // #endregion
#endif

        /// <summary>
        /// Last-good anchor + facing so the Move pose stays put through ARKit dropouts instead of whirling around.
        /// Callers own one of these per playback and reset it when (re)starting. Pass <c>default</c> for one-shot.
        /// </summary>
        public struct AnchorState
        {
            public Vector3 anchor;
            public Quaternion facing;
            public bool hasAnchor;   // a valid pelvis position has been seen
            public bool hasFacing;   // a valid facing has been seen
            public bool initialized; // retained for back-compat; true once either anchor or facing is known
            public int rejectStreak; // consecutive teleport-guard rejections (for recovery from real jumps/loops)
        }

        // After this many consecutive "too big" pelvis jumps, treat it as a real move (e.g. loop restart) and
        // accept it instead of holding the stale anchor forever. Filters 1-2 frame glitches; recovers from
        // sustained jumps within a few frames.
        const int MaxConsecutiveAnchorRejections = 2;

        /// <summary>
        /// Move joint positions in RouteRoot-local space for the frame nearest <paramref name="t"/>. Returns
        /// null when the asset has no usable pose. <paramref name="recording"/> may be null (then the fused
        /// root path is used as the anchor and no facing alignment is applied). The <paramref name="state"/>
        /// holds the last good ARKit-derived anchor/facing so a lost body freezes the pose in place rather than
        /// spinning from degenerate hip-axis noise.
        /// </summary>
        public static Vector3[] ComputeLocalJoints(MoveAIFusionAsset asset, HipRecording recording, float t, ref AnchorState state, bool invertFacing = false)
        {
            if (asset?.pose == null) return null;
            var poseFrame = asset.pose.FrameAtTime(t);
            if (poseFrame == null) return null;
            var fk = asset.pose.ForwardKinematics(poseFrame);
            if (fk == null || fk.Length == 0) return null;

            int k = Mathf.Clamp(Mathf.RoundToInt(t * asset.frameRate), 0, asset.FrameCount - 1);

            Vector3 anchor;
            Quaternion facing;
            if (recording != null)
            {
                HipFrame arkit = recording.GetFrameAtTime(t);
                bool gotAnchor = TryGetPelvisCenter(arkit, out var pc);
                bool gotFacing = TryComputeFacingAlignment(asset, fk, arkit, out var f);
                // Optional 180° yaw: the hip-lateral alignment can land the body facing away from the climber on
                // some captures; this flips it to face the same way without touching the per-joint pose.
                if (gotFacing && invertFacing) f = Quaternion.AngleAxis(180f, Vector3.up) * f;

                // Reject a sudden teleport of the pelvis (garbage frame during a dropout) — but only briefly.
                // A SUSTAINED large jump is a real move (most commonly the loop restart, where the pelvis
                // legitimately snaps from the clip's end position back to its start). Holding the stale anchor
                // forever in that case froze the body; so after a few consecutive rejections we accept the jump.
                bool jumpRejected = false;
                if (gotAnchor && state.hasAnchor && (pc - state.anchor).sqrMagnitude > MaxAnchorJump * MaxAnchorJump)
                {
                    state.rejectStreak++;
                    if (state.rejectStreak <= MaxConsecutiveAnchorRejections)
                    {
                        gotAnchor = false; // transient glitch: hold last good position
                        jumpRejected = true;
                    }
                    // else: sustained jump — fall through and accept pc (snaps to the new location).
                }
                else
                {
                    state.rejectStreak = 0;
                }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // #region agent log
                if ((dbgTick++ % 30) == 0 || jumpRejected)
                    UnityEngine.Debug.Log($"[DBG5f9dd8][H1/H2] t={t:F2} gotAnchorRaw={(jumpRejected ? "rej" : gotAnchor.ToString())} " +
                        $"gotFacing={gotFacing} pc={pc:F3} stateAnchor={(state.hasAnchor ? state.anchor.ToString("F3") : "none")} " +
                        $"dist={(state.hasAnchor ? (pc - state.anchor).magnitude.ToString("F3") : "-")} jumpRejected={jumpRejected}");
                // #endregion
#endif

                // Anchor (hip POSITION) and facing are tracked INDEPENDENTLY. The body must keep moving with the
                // hip whenever the pelvis is tracked, even when facing can't be computed (legs together during a
                // climb makes the hip axis too short to derive facing). Coupling them froze the whole body.
                if (gotAnchor)
                {
                    anchor = pc;
                    state.anchor = pc;
                    state.hasAnchor = true;
                }
                else if (state.hasAnchor)
                {
                    anchor = state.anchor; // brief pelvis dropout: hold last known position
                }
                else
                {
                    anchor = arkit.hipJoint.IsValid ? arkit.hipJoint.position : asset.rootPathLocal[k];
                }

                if (gotFacing)
                {
                    // Smooth toward the new yaw (first valid sample snaps) so noisy frames can't flip the body.
                    state.facing = state.hasFacing ? Quaternion.Slerp(state.facing, f, FacingSmoothing) : f;
                    facing = state.facing;
                    state.hasFacing = true;
                }
                else if (state.hasFacing)
                {
                    facing = state.facing; // hold last good facing so the body doesn't spin
                }
                else
                {
                    facing = Quaternion.identity;
                }

                state.initialized = state.hasAnchor || state.hasFacing;
            }
            else
            {
                anchor = asset.rootPathLocal[k];
                facing = Quaternion.identity;
            }

            Vector3 root = fk[0];
            float poseScale = Mathf.Max(0.01f, asset.scale);
            var outp = new Vector3[fk.Length];
            for (int i = 0; i < fk.Length; i++)
                outp[i] = anchor + facing * ((fk[i] - root) * poseScale);
            return outp;
        }

        /// <summary>
        /// ARKit pelvis center in reference-local space. Prefers the midpoint of the two upper-leg joints (matches
        /// Move's pelvis Root), but falls back to the Hips/Root joint — which are tracked far more reliably — so the
        /// body keeps following the hip whenever ANY pelvis joint is tracked (e.g. at the start of each loop before
        /// the legs are reacquired) instead of freezing in place.
        /// </summary>
        public static bool TryGetPelvisCenter(HipFrame arkit, out Vector3 center)
        {
            center = Vector3.zero;
            bool gotL = TryGetSample(arkit, ArkitLeftUpLeg, out var l);
            bool gotR = TryGetSample(arkit, ArkitRightUpLeg, out var r);
            if (gotL && gotR) { center = (l.positionReference + r.positionReference) * 0.5f; return true; }
            if (TryGetSample(arkit, ArkitHips, out var h)) { center = h.positionReference; return true; }
            if (TryGetSample(arkit, ArkitRoot, out var rt)) { center = rt.positionReference; return true; }
            if (gotL) { center = l.positionReference; return true; }
            if (gotR) { center = r.positionReference; return true; }
            // Last resort: the dedicated hip joint channel (also reference-local in v3 recordings).
            if (arkit.hipJoint.IsValid) { center = arkit.hipJoint.position; return true; }
            return false;
        }

        /// <summary>
        /// Yaw that rotates the Move pelvis lateral axis (Right_hip - Left_hip) onto the ARKit one. Anatomy
        /// based, so it stays valid for mostly-vertical climbing where translation-based yaw would be noise.
        /// Returns false (and identity) when either axis is missing or the ARKit hip axis is too short to trust
        /// (body lost / legs collapsed), so callers can hold their last good facing instead of spinning.
        /// </summary>
        public static bool TryComputeFacingAlignment(MoveAIFusionAsset asset, Vector3[] fk, HipFrame arkit, out Quaternion facing)
        {
            facing = Quaternion.identity;

            int lHip = asset.pose.IndexOfJoint("Left_hip");
            int rHip = asset.pose.IndexOfJoint("Right_hip");
            if (lHip < 0 || rHip < 0 || lHip >= fk.Length || rHip >= fk.Length)
                return false;

            Vector3 moveAxis = fk[rHip] - fk[lHip];
            moveAxis.y = 0f;
            if (moveAxis.sqrMagnitude < 1e-6f) return false;

            if (!TryGetSample(arkit, ArkitLeftUpLeg, out var l) || !TryGetSample(arkit, ArkitRightUpLeg, out var r))
                return false;

            Vector3 arkitAxis = r.positionReference - l.positionReference;
            arkitAxis.y = 0f;
            if (arkitAxis.magnitude < MinHipAxis) return false;

            // Pure YAW only (about world up). FromToRotation can inject pitch/roll when the axes are noisy or
            // near-anti-parallel, which tilts the whole basis and makes individual bones look flipped. A signed
            // yaw keeps the body upright and stable.
            float yaw = Vector3.SignedAngle(moveAxis.normalized, arkitAxis.normalized, Vector3.up);
            facing = Quaternion.AngleAxis(yaw, Vector3.up);
            return true;
        }

        static bool TryGetSample(HipFrame frame, int jointIndex, out RecordedJointSample sample)
        {
            sample = null;
            if (frame.recordedJoints == null) return false;
            foreach (var j in frame.recordedJoints)
            {
                if (j.jointIndex == jointIndex && j.isTracked)
                {
                    sample = j;
                    return true;
                }
            }
            return false;
        }
    }
}
