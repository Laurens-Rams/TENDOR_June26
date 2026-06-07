using UnityEngine;
using BodyTracking.Data;
using BodyTracking.Playback;

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
        // ARKit 3D body skeleton joint indices (Unity XRHumanBodyJoint / ARSkeletonDefinition order).
        public const int ArkitRoot = 0;
        public const int ArkitHips = 1;
        public const int ArkitLeftUpLeg = 2;
        public const int ArkitRightUpLeg = 6; // index 7 is rightLeg (knee), not the hip socket
        // First spine joints above the pelvis (spine7, spine6, …); any one can define torso forward.
        static readonly int[] ArkitSpineCandidates = { 10, 11, 12, 13 };

        // Minimum horizontal hip-socket separation (m) for the ARKit pelvis axis to be trusted for facing.
        // When the tracked body is lost the legs collapse and this axis degenerates into spinning noise.
        const float MinHipAxis = 0.08f;
        // Max plausible per-update pelvis jump (m). Larger deltas are treated as a tracking glitch, not motion.
        const float MaxAnchorJump = 1.0f;
        // Facing smoothing: fraction of the way the held yaw moves toward a new valid measurement each frame.
        // Low value = very stable (heavily damped) facing that can't flicker/flip from per-frame hip-axis noise.
        const float FacingSmoothing = 0.12f;

        /// <summary>How the character's world position (pelvis anchor) is derived each frame.</summary>
        public enum AnchorMode
        {
            /// <summary>Original behavior: pin the pelvis to the ARKit pelvis every frame (most responsive, but
            /// inherits ARKit drift — feet/legs creep while the climber is actually still).</summary>
            FollowArkit,
            /// <summary>Ride Move AI's own (steadier) root trajectory for position, and only re-sync to ARKit
            /// occasionally (timer / drift threshold), eased in, and only while the climber is actually moving.
            /// Stops the legs drifting while still.</summary>
            MoveAIDriftCorrected,
            /// <summary>Use the BAKED root path (<see cref="MoveAIFusionAsset.rootPathLocal"/>) for position. That
            /// path is Move AI's trajectory already fused with ARKit at bake time (aligned + smoothed + drift
            /// corrected), indexed by frame, so it is absolute and cannot accumulate drift over loops/seeks.</summary>
            FollowBakedRoot
        }

        /// <summary>Tunables for <see cref="AnchorMode.MoveAIDriftCorrected"/>. Owned/serialized by the caller
        /// (e.g. FusedCharacterPlayer) and passed in so the character and the compare overlay stay identical.</summary>
        [System.Serializable]
        public struct AnchorSettings
        {
            public AnchorMode mode;
            [Tooltip("STILL threshold (m/s): once moving, the body re-freezes when pelvis speed drops below this. Set just above the ARKit drift/jitter speed so a standing climber latches and stops creeping.")]
            public float stillnessVelocity;
            [Tooltip("MOVE threshold (m/s): from a frozen state, real locomotion is detected (and ARKit followed 1:1) once pelvis speed rises above this. Set below a normal walk/climb speed but above noise. Must be > stillnessVelocity (hysteresis band).")]
            public float fullMotionVelocity;
            [Tooltip("Seconds to ease the anchor onto the live ARKit pelvis once moving (catch-up after a freeze). Small = snappy, larger = smoother.")]
            public float followSeconds;
            [Tooltip("Freeze only horizontal (X/Z); always follow vertical 1:1. Off = freeze all 3 axes (recommended, so a still climber doesn't drift vertically on the wall).")]
            public bool correctXZOnly;

            [Header("Facing")]
            [Tooltip("ON (recommended): the character's turning comes from the steady Move body; ARKit only slowly corrects the absolute yaw offset, so leg-stride/tracking noise no longer wobbles the facing. OFF: legacy per-frame ARKit hip-axis facing.")]
            public bool moveDrivenFacing;
            [Tooltip("Seconds over which the Move->ARKit yaw offset is eased (move-driven facing only). Larger = steadier but slower to correct an initial mis-facing. ~2s is a good start.")]
            public float facingCorrectionSeconds;

            public static AnchorSettings Default => new AnchorSettings
            {
                mode = AnchorMode.MoveAIDriftCorrected,
                stillnessVelocity = 0.06f,
                fullMotionVelocity = 0.2f,
                followSeconds = 0.1f,
                correctXZOnly = false,
                moveDrivenFacing = true,
                facingCorrectionSeconds = 2f,
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
            public Vector3 moveAnchor;     // held world-local pelvis position (follows ARKit while moving, frozen while still)
            public bool hasMoveAnchor;     // moveAnchor has been seeded
            public Vector3 lastArkitPc;    // previous frame's ARKit pelvis center, for speed estimation
            public bool hasLastArkitPc;
            public float speedEma;         // low-pass filtered pelvis speed (m/s), for a noise-robust still/moving gate
            public bool moving;            // hysteresis latch: true once real locomotion is detected
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
        /// spinning from degenerate hip-axis noise. <paramref name="effectiveFacingLocal"/> is the final yaw
        /// applied to joint offsets (including <paramref name="invertFacing"/>); pass it to root placement so
        /// the character and orange overlay stay identical.
        /// </summary>
        public static Vector3[] ComputeLocalJoints(MoveAIFusionAsset asset, HipRecording recording, float t, ref AnchorState state, out Quaternion effectiveFacingLocal, bool invertFacing = false, AnchorSettings settings = default, MoveGlbSource glbSource = null)
        {
            effectiveFacingLocal = Quaternion.identity;
            if (asset?.pose == null) return null;

            Vector3[] fk = null;
            bool usedGlbPose = glbSource != null && glbSource.TryBuildJointOffsets(t, asset.pose.jointNames, out fk);
            if (!usedGlbPose)
            {
                var poseFrame = asset.pose.FrameAtTime(t);
                if (poseFrame == null) return null;
                fk = asset.pose.ForwardKinematics(poseFrame);
            }
            if (fk == null || fk.Length == 0) return null;

            // Live GLB drives joint offsets; anchor mode still selects world placement (baked Move path vs live ARKit).
            var anchorSettings = settings;

            int k = Mathf.Clamp(Mathf.RoundToInt(t * asset.frameRate), 0, asset.FrameCount - 1);
            float poseScale = Mathf.Max(0.01f, asset.scale);

            Vector3 anchor;
            Quaternion facing;
            if (recording != null)
            {
                HipFrame arkit = recording.GetFrameAtTime(t);
                bool gotAnchor = TryGetPelvisCenter(arkit, out var pc);
                bool gotFacing = TryComputeFacingAlignment(asset, fk, arkit, out var f);

                // Reject a sudden teleport of the pelvis (garbage frame during a dropout) — but only briefly.
                // A SUSTAINED large jump is a real move (most commonly the loop restart, where the pelvis
                // legitimately snaps from the clip's end position back to its start). Holding the stale anchor
                // forever in that case froze the body; so after a few consecutive rejections we accept the jump.
                if (gotAnchor && state.hasAnchor && (pc - state.anchor).sqrMagnitude > MaxAnchorJump * MaxAnchorJump)
                {
                    state.rejectStreak++;
                    if (state.rejectStreak <= MaxConsecutiveAnchorRejections)
                    {
                        gotAnchor = false; // transient glitch: hold last good position
                    }
                    // else: sustained jump — fall through and accept pc (snaps to the new location).
                }
                else
                {
                    state.rejectStreak = 0;
                }

                // Anchor (hip POSITION) and facing are tracked INDEPENDENTLY. The body must keep moving with the
                // hip whenever the pelvis is tracked, even when facing can't be computed (legs together during a
                // climb makes the hip axis too short to derive facing). Coupling them froze the whole body.
                // Facing is resolved first because MoveAIDriftCorrected rotates the Move root delta by it.
                // Baked-path + live GLB: the GLB clip carries body turn; ease the Move->AR yaw offset each frame
                // so the orange overlay tracks the cyan ARKit skeleton as the climber turns.
                if (gotFacing)
                {
                    if (!state.hasFacing)
                    {
                        state.facing = f; // first valid sample snaps
                    }
                    else if (settings.moveDrivenFacing)
                    {
                        // The body's per-frame turning is already carried by the Move pose (fk). The alignment is just
                        // the (near-constant) Move->AR yaw OFFSET, so ease it VERY slowly: leg-stride / ARKit hip-axis
                        // noise is averaged out instead of being injected into the facing every frame.
                        float dtF = Mathf.Max(0f, Time.deltaTime);
                        float rate = 1f - Mathf.Exp(-dtF / Mathf.Max(0.05f, settings.facingCorrectionSeconds));
                        state.facing = Quaternion.Slerp(state.facing, f, rate);
                    }
                    else
                    {
                        state.facing = Quaternion.Slerp(state.facing, f, FacingSmoothing);
                    }
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

                if (anchorSettings.mode == AnchorMode.FollowBakedRoot && asset.rootPathLocal != null && k < asset.rootPathLocal.Count)
                {
                    // Absolute, frame-indexed baked trajectory (Move fused with ARKit at bake time). It can't
                    // accumulate drift; the teleport guard below only matters for the live-ARKit branches, so we
                    // simply track it as the last-good anchor for any downstream consumers.
                    anchor = asset.rootPathLocal[k];
                    state.anchor = anchor;
                    state.hasAnchor = true;
                }
                else if (anchorSettings.mode == AnchorMode.MoveAIDriftCorrected)
                {
                    anchor = ComputeDriftCorrectedAnchor(asset, k, gotAnchor, pc, arkit, ref state, anchorSettings);
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

            // Apply the optional 180° yaw last, every frame, so toggling invertFacing takes effect immediately
            // (including baked GLB snap-once facing) and stays consistent with root placement.
            if (invertFacing)
                facing = Quaternion.AngleAxis(180f, Vector3.up) * facing;
            effectiveFacingLocal = facing;

            Vector3 root = fk[0];
            var outp = new Vector3[fk.Length];
            for (int i = 0; i < fk.Length; i++)
                outp[i] = anchor + facing * ((fk[i] - root) * poseScale);
            return outp;
        }

        /// <summary>
        /// Place the pelvis by following the ARKit pelvis ABSOLUTELY while the climber is genuinely moving (so
        /// walking/climbing translates exactly like FollowArkit), and FREEZING it while still (so slow ARKit drift
        /// and jitter never reach the legs). A hysteresis latch on a low-pass-filtered pelvis speed switches between
        /// the two: it starts following only above <see cref="AnchorSettings.fullMotionVelocity"/> and re-freezes
        /// below <see cref="AnchorSettings.stillnessVelocity"/>. Following is absolute (Lerp toward the live pelvis),
        /// never integrated, so noise can't accumulate into a random-walk.
        /// </summary>
        static Vector3 ComputeDriftCorrectedAnchor(MoveAIFusionAsset asset, int k, bool gotAnchor, Vector3 pc,
            HipFrame arkit, ref AnchorState state, AnchorSettings settings)
        {
            float dt = Mathf.Max(1e-4f, Time.deltaTime);

            if (!state.hasMoveAnchor)
            {
                state.moveAnchor = gotAnchor ? pc
                    : state.hasAnchor ? state.anchor
                    : arkit.hipJoint.IsValid ? arkit.hipJoint.position
                    : asset.rootPathLocal[k];
                state.hasMoveAnchor = true;
                state.lastArkitPc = state.moveAnchor;
                state.hasLastArkitPc = gotAnchor;
                state.speedEma = 0f;
                state.moving = false;
                return state.moveAnchor;
            }

            // No fresh pelvis this frame (tracking dropout): hold and re-seed the speed baseline.
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

            // A huge single-frame pelvis jump is a loop wrap (clip end -> start) or glitch: snap and reset.
            if (deltaPc.magnitude > MaxAnchorJump)
            {
                state.moveAnchor = pc;
                state.speedEma = 0f;
                state.moving = false;
                return state.moveAnchor;
            }

            // Low-pass the instantaneous speed so a single noisy frame can't trip the gate.
            float rawSpeed = deltaPc.magnitude / dt;
            float aSpeed = dt / (0.18f + dt);
            state.speedEma += aSpeed * (rawSpeed - state.speedEma);

            // Hysteresis: need a real move (> fullMotionVelocity) to START following; re-freeze once slow again
            // (< stillnessVelocity). The gap between the two keeps moderate noise from flickering the state.
            float onThresh = Mathf.Max(settings.stillnessVelocity + 1e-3f, settings.fullMotionVelocity);
            float offThresh = Mathf.Max(1e-3f, settings.stillnessVelocity);
            if (!state.moving && state.speedEma > onThresh) state.moving = true;
            else if (state.moving && state.speedEma < offThresh) state.moving = false;

            if (state.moving)
            {
                // Ease ONTO the live pelvis (absolute) — translates with the real walk/climb, no accumulation.
                float w = 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, settings.followSeconds));
                Vector3 tgt = settings.correctXZOnly ? new Vector3(pc.x, state.moveAnchor.y, pc.z) : pc;
                state.moveAnchor = Vector3.Lerp(state.moveAnchor, tgt, w);
            }
            else if (settings.correctXZOnly)
            {
                // Frozen horizontally, but still follow real vertical motion (e.g. a slow vertical reach).
                state.moveAnchor.y = pc.y;
            }
            // else: fully frozen — drift and jitter are rejected.

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
        /// Yaw that lines up the Move body with the recorded ARKit skeleton. Prefers matching horizontal torso
        /// forward (pelvis → spine) so the orange overlay faces the same way as the cyan ARKit rig; falls back to
        /// aligning the hip lateral axes when spine joints are missing.
        /// </summary>
        public static bool TryComputeFacingAlignment(MoveAIFusionAsset asset, Vector3[] fk, HipFrame arkit, out Quaternion facing)
        {
            facing = Quaternion.identity;

            if (TryComputeMoveHorizontalForward(asset, fk, out var moveFwd) &&
                TryComputeArkitHorizontalForward(arkit, out var arkitFwd))
            {
                float yaw = Vector3.SignedAngle(moveFwd, arkitFwd, Vector3.up);
                facing = Quaternion.AngleAxis(yaw, Vector3.up);
                return true;
            }

            // Fallback: hip-line lateral axis only (legacy).
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

            float yawFallback = Vector3.SignedAngle(moveAxis.normalized, arkitAxis.normalized, Vector3.up);
            facing = Quaternion.AngleAxis(yawFallback, Vector3.up);
            return true;
        }

        /// <summary>Horizontal torso forward from Move/GLB offsets (root/hips → spine).</summary>
        static bool TryComputeMoveHorizontalForward(MoveAIFusionAsset asset, Vector3[] fk, out Vector3 forward)
        {
            forward = Vector3.zero;
            int root = asset.pose.IndexOfJoint("Root");
            if (root < 0) root = 0;
            if (root < 0 || root >= fk.Length) return false;

            Vector3 pelvis = fk[root];
            int lHip = asset.pose.IndexOfJoint("Left_hip");
            int rHip = asset.pose.IndexOfJoint("Right_hip");
            if (lHip >= 0 && rHip >= 0 && lHip < fk.Length && rHip < fk.Length)
                pelvis = (fk[lHip] + fk[rHip]) * 0.5f;

            int spine = asset.pose.IndexOfJoint("Spine1");
            if (spine < 0) spine = asset.pose.IndexOfJoint("Spine2");
            if (spine < 0 || spine >= fk.Length) return false;

            forward = fk[spine] - pelvis;
            forward.y = 0f;
            return forward.sqrMagnitude >= 1e-6f;
        }

        /// <summary>Horizontal torso forward from the recorded ARKit skeleton (pelvis → spine).</summary>
        static bool TryComputeArkitHorizontalForward(HipFrame arkit, out Vector3 forward)
        {
            forward = Vector3.zero;
            if (!TryGetPelvisCenter(arkit, out var pelvis)) return false;

            foreach (int spineIdx in ArkitSpineCandidates)
            {
                if (!TryGetSample(arkit, spineIdx, out var spine)) continue;
                forward = spine.positionReference - pelvis;
                forward.y = 0f;
                if (forward.sqrMagnitude >= 1e-6f) return true;
            }

            // No spine: derive forward from the hip line (right × up).
            if (!TryGetSample(arkit, ArkitLeftUpLeg, out var l) || !TryGetSample(arkit, ArkitRightUpLeg, out var r))
                return false;
            Vector3 right = r.positionReference - l.positionReference;
            if (right.sqrMagnitude < MinHipAxis * MinHipAxis) return false;
            forward = Vector3.Cross(right.normalized, Vector3.up);
            forward.y = 0f;
            return forward.sqrMagnitude >= 1e-6f;
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
