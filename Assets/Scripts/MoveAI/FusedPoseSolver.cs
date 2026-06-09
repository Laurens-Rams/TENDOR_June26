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
        public const int ArkitRightUpLeg = 7; // Unity 3D skeleton hip socket (8 = RightLeg knee, 6 = LeftToesEnd)

        // Minimum horizontal hip-socket separation (m) for the ARKit pelvis axis to be trusted for facing.
        // When the tracked body is lost the legs collapse and this axis degenerates into spinning noise.
        const float MinHipAxis = 0.08f;
        // Max plausible per-update pelvis jump (m). Larger deltas are treated as a tracking glitch, not motion.
        const float MaxAnchorJump = 1.0f;

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
            FollowBakedRoot,
            /// <summary>TEST: drive world movement from the Move GLB's OWN root motion. Capture the world anchor +
            /// yaw ONCE from ARKit at the first valid frame (or on re-align request), then translate purely by the
            /// GLB root delta from that moment. ARKit is read once; the body may drift vs the wall over time. Easy
            /// to remove (delete this value + the UI toggle).</summary>
            FollowMoveGlbRoot
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

            [Header("Move-driven test mode (FollowMoveGlbRoot) auto re-align")]
            [Tooltip("ON: while riding the Move GLB root motion, automatically re-anchor to the live ARKit pelvis whenever the Move-predicted position drifts past the threshold below. The correction is eased (no teleport). OFF: only the manual 'Re-align now' button re-anchors.")]
            public bool moveAutoRealign;
            [Tooltip("Drift distance (m) between the Move-predicted pelvis and the live ARKit pelvis that triggers an automatic re-align. Set above normal ARKit jitter (~0.05m) but below a noticeable offset. ~0.2m is a good start.")]
            public float moveRealignDriftThreshold;
            [Tooltip("Seconds over which a re-align (manual or automatic) glides the body from its drifted spot onto the fresh ARKit anchor, so it slides instead of snapping. Small = snappy, larger = smoother. ~0.4s is a good start.")]
            public float moveRealignEaseSeconds;

            public static AnchorSettings Default => new AnchorSettings
            {
                mode = AnchorMode.MoveAIDriftCorrected,
                stillnessVelocity = 0.06f,
                fullMotionVelocity = 0.2f,
                followSeconds = 0.1f,
                correctXZOnly = false,
                moveDrivenFacing = true,
                facingCorrectionSeconds = 2f,
                moveAutoRealign = true,
                moveRealignDriftThreshold = 0.2f,
                moveRealignEaseSeconds = 0.4f,
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

            // --- FollowMoveGlbRoot state (capture-once world anchor + facing, then ride the GLB root delta) ---
            public bool hasMoveDrivenAnchor;   // anchor0/facing0/root0 have been captured
            public Vector3 moveDrivenAnchor0;  // world-local pelvis anchor captured from ARKit at the align moment
            public Quaternion moveDrivenFacing0; // yaw captured from ARKit hip-axis at the align moment
            public Vector3 moveDrivenRoot0;    // GLB root world position at the align moment
            public bool requestRealign;        // set by the trigger hook to re-capture at the current frame
            // Eased re-align: on re-anchor we keep the body where it was and decay these toward zero/identity so
            // the correction glides on rather than teleporting. Position offset + yaw offset applied to the render.
            public Vector3 moveDrivenPosCorrection;     // rendered = target + this; eased toward Vector3.zero
            public Quaternion moveDrivenRotCorrection;  // rendered = this * facing0; eased toward identity
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
            Vector3 glbRootWorld = Vector3.zero;
            bool usedGlbPose = glbSource != null && glbSource.TryBuildJointOffsets(t, asset.pose.jointNames, out fk, out glbRootWorld);
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
            bool hipAxisFacingActive = false;
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

                // FACING = exact match to the recorded ARKit (blue) skeleton. The alignment rotates the Move hip
                // lateral axis directly onto the ARKit hip lateral axis, so the orange hip line is always PARALLEL
                // to the blue hip line. Applied DIRECTLY every valid frame (no easing/slerp) so orange tracks blue
                // with zero lag and can't slowly drift or spin. The hip SOCKETS (upper-leg joints) stay apart even
                // when the feet/knees are together during a climb, so this stays valid; we only hold the last good
                // yaw across genuine tracking dropouts (no valid hip pair this frame).
                if (gotFacing)
                {
                    state.facing = f;
                    facing = f;
                    state.hasFacing = true;
                }
                else if (state.hasFacing)
                {
                    facing = state.facing; // hold last good facing through a dropout so the body doesn't spin
                }
                else
                {
                    facing = Quaternion.identity;
                }

                if (anchorSettings.mode == AnchorMode.FollowMoveGlbRoot)
                {
                    // TEST mode: capture world anchor + facing ONCE from ARKit, then translate purely by the GLB
                    // root delta from that moment. facing is FROZEN to the captured yaw (overrides the per-frame
                    // hip-axis facing computed above) so turning comes entirely from Move after the alignment.
                    // Over time Move's own motion drifts vs the wall; we re-anchor to a confident ARKit pelvis
                    // either on request (manual button) or automatically once the predicted position has drifted
                    // past a threshold. The re-anchor is EASED (a decaying position/yaw correction) so the body
                    // slides onto the fresh anchor instead of teleporting.
                    bool firstCapture = !state.hasMoveDrivenAnchor;

                    // Auto trigger: compare the pure Move prediction (no eased correction) to the live ARKit pelvis.
                    bool wantRealign = state.requestRealign;
                    if (!firstCapture && usedGlbPose && gotAnchor && anchorSettings.moveAutoRealign && !wantRealign)
                    {
                        Vector3 dNow = (glbRootWorld - state.moveDrivenRoot0) * poseScale;
                        Vector3 predicted = state.moveDrivenAnchor0 + state.moveDrivenFacing0 * dNow;
                        if ((predicted - pc).sqrMagnitude >
                            anchorSettings.moveRealignDriftThreshold * anchorSettings.moveRealignDriftThreshold)
                            wantRealign = true;
                    }

                    if (usedGlbPose && firstCapture)
                    {
                        // First alignment: snap the base to ARKit with no correction (nothing to glide from).
                        state.moveDrivenAnchor0 = gotAnchor ? pc
                            : state.hasAnchor ? state.anchor
                            : (asset.rootPathLocal != null && k < asset.rootPathLocal.Count) ? asset.rootPathLocal[k]
                            : Vector3.zero;
                        state.moveDrivenFacing0 = gotFacing ? f : (state.hasFacing ? state.facing : Quaternion.identity);
                        state.moveDrivenRoot0 = glbRootWorld;
                        state.moveDrivenPosCorrection = Vector3.zero;
                        state.moveDrivenRotCorrection = Quaternion.identity;
                        state.hasMoveDrivenAnchor = true;
                        state.requestRealign = false;
                    }
                    else if (usedGlbPose && wantRealign && gotAnchor)
                    {
                        // Re-anchor with easing: capture where the body is rendered RIGHT NOW, move the base onto
                        // the fresh ARKit anchor (delta resets to 0), then set the correction so the rendered pose
                        // is unchanged this frame and decays onto the new base over moveRealignEaseSeconds.
                        Vector3 dOld = (glbRootWorld - state.moveDrivenRoot0) * poseScale;
                        Vector3 oldRendered = state.moveDrivenAnchor0 + state.moveDrivenFacing0 * dOld + state.moveDrivenPosCorrection;
                        Quaternion oldFacing = state.moveDrivenRotCorrection * state.moveDrivenFacing0;

                        Quaternion newFacing0 = gotFacing ? f : state.moveDrivenFacing0;
                        state.moveDrivenAnchor0 = pc;
                        state.moveDrivenRoot0 = glbRootWorld;
                        state.moveDrivenPosCorrection = oldRendered - pc;
                        state.moveDrivenRotCorrection = oldFacing * Quaternion.Inverse(newFacing0);
                        state.moveDrivenFacing0 = newFacing0;
                        state.requestRealign = false;
                    }
                    // else (requestRealign but no ARKit/GLB this frame): keep requestRealign pending until ARKit returns.

                    if (state.hasMoveDrivenAnchor)
                    {
                        // Decay the eased correction toward zero/identity (frame-rate independent).
                        float wEase = 1f - Mathf.Exp(-Mathf.Max(1e-4f, Time.deltaTime)
                            / Mathf.Max(1e-4f, anchorSettings.moveRealignEaseSeconds));
                        state.moveDrivenPosCorrection = Vector3.Lerp(state.moveDrivenPosCorrection, Vector3.zero, wEase);
                        state.moveDrivenRotCorrection = Quaternion.Slerp(state.moveDrivenRotCorrection, Quaternion.identity, wEase);

                        // No fresh GLB root this frame: hold the last anchor rather than snapping to a zero delta.
                        Vector3 d = usedGlbPose ? (glbRootWorld - state.moveDrivenRoot0) * poseScale : Vector3.zero;
                        facing = state.moveDrivenRotCorrection * state.moveDrivenFacing0;
                        anchor = usedGlbPose
                            ? state.moveDrivenAnchor0 + state.moveDrivenFacing0 * d + state.moveDrivenPosCorrection
                            : state.anchor;
                        // The captured facing came from the ARKit hip-axis, so treat it like an active hip-axis
                        // facing: keeps invertFacing gated identically to the other modes (no-op while valid).
                        state.hasFacing = true;
                    }
                    else
                    {
                        // Not captured yet (no GLB available): fall back to the baked path so the body is placed.
                        anchor = (asset.rootPathLocal != null && k < asset.rootPathLocal.Count)
                            ? asset.rootPathLocal[k] : (state.hasAnchor ? state.anchor : Vector3.zero);
                    }
                    state.anchor = anchor;
                    state.hasAnchor = true;
                }
                else if (anchorSettings.mode == AnchorMode.FollowBakedRoot && asset.rootPathLocal != null && k < asset.rootPathLocal.Count)
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
                hipAxisFacingActive = state.hasFacing;
            }
            else
            {
                anchor = asset.rootPathLocal[k];
                facing = Quaternion.identity;
            }

            // Legacy 180° fudge from before hip-socket alignment. Applying it on top of hip-axis facing anti-aligns
            // the orange overlay (worldAxisErrorDeg ≈ 180). Only honor it when no hip-axis yaw is available.
            if (invertFacing && !hipAxisFacingActive)
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
        /// Pure-yaw rotation that maps the Move hip lateral axis (Right_hip - Left_hip) onto the recorded ARKit hip
        /// lateral axis (rightUpLeg - leftUpLeg). Both are taken between the HIP SOCKETS, which stay apart and
        /// horizontal regardless of leg/foot position, so the result is stable during a climb (unlike a torso or
        /// translation forward, which collapses when the body is upright/still). Applying this every frame keeps the
        /// orange skeleton's hip line exactly parallel to the blue ARKit hip line — i.e. orange matches blue's yaw.
        /// Returns false (caller holds its last good yaw) only when either hip pair is missing this frame.
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

            // Pure YAW about world up so the body stays upright and can't pick up pitch/roll from noisy axes.
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
