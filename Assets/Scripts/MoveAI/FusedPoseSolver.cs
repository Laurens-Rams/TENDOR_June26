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

        /// <summary>How the character's world position (pelvis anchor) is derived each frame.</summary>
        public enum AnchorMode
        {
            /// <summary>Original behavior: pin the pelvis to the ARKit pelvis every frame (most responsive, but
            /// inherits ARKit drift — feet/legs creep while the climber is actually still).</summary>
            FollowArkit,
            /// <summary>Ride Move AI's own (steadier) root trajectory for position, and only re-sync to ARKit
            /// occasionally (timer / drift threshold), eased in, and only while the climber is actually moving.
            /// Stops the legs drifting while still.</summary>
            MoveAIDriftCorrected
        }

        /// <summary>Tunables for <see cref="AnchorMode.MoveAIDriftCorrected"/>. Owned/serialized by the caller
        /// (e.g. FusedCharacterPlayer) and passed in so the character and the compare overlay stay identical.</summary>
        [System.Serializable]
        public struct AnchorSettings
        {
            public AnchorMode mode;
            [Tooltip("Pelvis speed (m/s) BELOW which motion is treated as drift and frozen out (legs stop creeping while still). Real walking/climbing is faster than this and passes through. Lower if a slow shuffle gets frozen; raise if drift still leaks.")]
            public float stillnessVelocity;
            [Tooltip("Pelvis speed (m/s) at/above which motion is followed 1:1. Between stillnessVelocity and this the follow eases in. Keep a few× stillnessVelocity. 0 = auto (3× stillnessVelocity).")]
            public float fullMotionVelocity;
            [Tooltip("Re-sync when the held anchor diverges from the ARKit pelvis by more than this (m), while moving. 0 = disabled (recommended: keep the gated position).")]
            public float maxDriftMeters;
            [Tooltip("Seconds to ease a re-sync in, so the correction never pops.")]
            public float resyncBlendSeconds;
            [Tooltip("Gate only horizontal (X/Z) motion; always follow vertical 1:1. Off = gate all 3 axes (recommended, so a still climber doesn't drift vertically on the wall).")]
            public bool correctXZOnly;

            public static AnchorSettings Default => new AnchorSettings
            {
                mode = AnchorMode.MoveAIDriftCorrected,
                stillnessVelocity = 0.12f,
                fullMotionVelocity = 0.45f,
                maxDriftMeters = 0f,
                resyncBlendSeconds = 0.4f,
                correctXZOnly = false,
            };
        }

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

            // --- MoveAIDriftCorrected state ---
            public Vector3 moveAnchor;     // running world-local pelvis position (gated ARKit motion integrated in)
            public bool hasMoveAnchor;     // moveAnchor has been seeded
            public Vector3 lastArkitPc;    // previous frame's ARKit pelvis center, for delta integration
            public bool hasLastArkitPc;
            public float blendRemaining;   // >0 while easing an optional drift re-sync toward ARKit
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
        public static Vector3[] ComputeLocalJoints(MoveAIFusionAsset asset, HipRecording recording, float t, ref AnchorState state, bool invertFacing = false, AnchorSettings settings = default)
        {
            if (asset?.pose == null) return null;
            var poseFrame = asset.pose.FrameAtTime(t);
            if (poseFrame == null) return null;
            var fk = asset.pose.ForwardKinematics(poseFrame);
            if (fk == null || fk.Length == 0) return null;

            int k = Mathf.Clamp(Mathf.RoundToInt(t * asset.frameRate), 0, asset.FrameCount - 1);
            float poseScale = Mathf.Max(0.01f, asset.scale);

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
                // Facing is resolved first because MoveAIDriftCorrected rotates the Move root delta by it.
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

                if (settings.mode == AnchorMode.MoveAIDriftCorrected)
                {
                    anchor = ComputeDriftCorrectedAnchor(asset, k, gotAnchor, pc, arkit, ref state, settings);
                }
                else // FollowArkit (original behavior)
                {
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
                }

                state.initialized = state.hasAnchor || state.hasFacing || state.hasMoveAnchor;
            }
            else
            {
                anchor = asset.rootPathLocal[k];
                facing = Quaternion.identity;
            }

            Vector3 root = fk[0];
            var outp = new Vector3[fk.Length];
            for (int i = 0; i < fk.Length; i++)
                outp[i] = anchor + facing * ((fk[i] - root) * poseScale);
            return outp;
        }

        /// <summary>
        /// Position the pelvis by integrating the ARKit pelvis motion — which carries the real walking/climbing
        /// translation — but GATED by speed: per-frame pelvis deltas are scaled by a smooth gate so slow drift
        /// (below <see cref="AnchorSettings.stillnessVelocity"/>) is dropped (the legs stop creeping while still)
        /// while genuine locomotion (at/above <see cref="AnchorSettings.fullMotionVelocity"/>) is followed 1:1.
        /// Climbing up the wall works the same way: vertical pelvis motion is real and passes the gate.
        /// </summary>
        static Vector3 ComputeDriftCorrectedAnchor(MoveAIFusionAsset asset, int k, bool gotAnchor, Vector3 pc,
            HipFrame arkit, ref AnchorState state, AnchorSettings settings)
        {
            float dt = Mathf.Max(0f, Time.deltaTime);

            if (!state.hasMoveAnchor)
            {
                // Seed from the best available world-anchored position.
                state.moveAnchor = gotAnchor ? pc
                    : state.hasAnchor ? state.anchor
                    : arkit.hipJoint.IsValid ? arkit.hipJoint.position
                    : asset.rootPathLocal[k];
                state.hasMoveAnchor = true;
                state.lastArkitPc = state.moveAnchor;
                state.hasLastArkitPc = gotAnchor;
                state.blendRemaining = 0f;
                return state.moveAnchor;
            }

            // No fresh pelvis this frame (tracking dropout): hold position and re-seed the delta baseline so the
            // next valid frame doesn't integrate a stale, oversized jump.
            if (!gotAnchor)
            {
                state.hasLastArkitPc = false;
                return state.moveAnchor;
            }

            if (!state.hasLastArkitPc)
            {
                state.lastArkitPc = pc;
                state.hasLastArkitPc = true;
                return state.moveAnchor;
            }

            Vector3 deltaPc = pc - state.lastArkitPc;
            state.lastArkitPc = pc;

            // A huge single-frame pelvis jump is a loop wrap (clip end -> start) or glitch, not real motion: snap.
            if (deltaPc.magnitude > MaxAnchorJump)
            {
                state.moveAnchor = pc;
                state.blendRemaining = 0f;
                return state.moveAnchor;
            }

            float speed = dt > 1e-4f ? deltaPc.magnitude / dt : 0f;

            // Smooth speed gate: 0 below stillness (drift frozen out), 1 at/above full-motion (locomotion 1:1).
            float lo = Mathf.Max(0.001f, settings.stillnessVelocity);
            float hi = settings.fullMotionVelocity > lo ? settings.fullMotionVelocity : lo * 3f;
            float gate = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(lo, hi, speed));

            // Integrate the gated delta. Optionally always follow vertical so only horizontal drift is filtered.
            Vector3 applied = deltaPc * gate;
            if (settings.correctXZOnly) applied.y = deltaPc.y;
            state.moveAnchor += applied;

            // Optional safety: if the gated anchor drifts too far from the live pelvis while genuinely moving,
            // ease back toward ARKit so long sessions can't accumulate a large offset. Off by default.
            if (settings.maxDriftMeters > 0f && gate > 0.5f &&
                Vector3.Distance(state.moveAnchor, pc) > settings.maxDriftMeters)
            {
                float u = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, settings.resyncBlendSeconds));
                Vector3 tgt = settings.correctXZOnly ? new Vector3(pc.x, state.moveAnchor.y, pc.z) : pc;
                state.moveAnchor = Vector3.Lerp(state.moveAnchor, tgt, u);
            }

            return state.moveAnchor;
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
