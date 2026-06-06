using System.Collections.Generic;
using System.Text;
using UnityEngine;
using BodyTracking.Animation;
using BodyTracking.Data;
using BodyTracking.Spatial;
using BodyTracking.MoveAI;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Replays a baked <see cref="MoveAIFusionAsset"/>: drives a rigged character's bone local rotations from
    /// the Move AI pose, places the character root each frame from the baked RouteRoot-local trajectory (mapped
    /// through the live RouteRoot so it inherits Immersal/world-map drift correction), and applies one uniform
    /// height scale from the recording so the FBX matches the orange skeleton size. Hidden until localized.
    /// </summary>
    public class FusedCharacterPlayer : MonoBehaviour
    {
        [Header("Character")]
        [Tooltip("Root of the rigged character to drive. Bones are resolved by name under this transform.")]
        [SerializeField] private Transform characterRoot;
        [SerializeField] private FBXCharacterController fbxCharacterController;
        [SerializeField] private bool autoFindCharacter = true;
        [SerializeField] private MoveJointMap jointMap = MoveJointMap.CreateDefaultMixamo();

        [Header("Playback")]
        [Tooltip("Flip the Move pose 180° in yaw (affects pose anchoring). Keep matched to the compare overlay.")]
        [SerializeField] private bool invertFacing = false;
        [Tooltip("Spin the character root 180° about its up axis. Use when the model's authored forward is -Z so " +
                 "its torso faces the same way as the recorded climber (limbs are aimed separately and stay correct).")]
        [SerializeField] private bool flipCharacterForward = true;
        [SerializeField] private bool loop = true;
        [SerializeField] private float playbackSpeed = 1f;
        [Tooltip("Lateral offset in RouteRoot-local space (used for side-by-side compare with ARKit).")]
        [SerializeField] private Vector3 routeRootLocalOffset = Vector3.zero;

        /// <summary>How the single uniform character scale is chosen.</summary>
        public enum HeightFitMode
        {
            // Fit the character's RENDERED height — the crown of the mesh (hair included) down to the soles — to
            // the recorded climber height. The orange/cyan skeletons only reach the Head JOINT (well below the
            // crown) and the toe JOINT (above the sole), so this stops the visible mesh overshooting top/bottom.
            RenderedMeshHeight,
            // Fit the foot->head BONE-CHAIN (joint to joint) to the skeleton. Lands the character's joints on the
            // orange joints, but lets the mesh crown/soles stick out past the skeleton (character looks taller).
            BoneChain,
        }

        [Header("Fit")]
        [Tooltip("How the one uniform character scale is chosen. RenderedMeshHeight (default) matches the visible " +
                 "mesh — hair/crown down to the soles — to the recorded climber height, so the character stops " +
                 "looking too tall. BoneChain matches joint-to-joint and lets the mesh overshoot the skeleton.")]
        [SerializeField] private HeightFitMode heightFitMode = HeightFitMode.RenderedMeshHeight;
        [Tooltip("Optional fine-tune multiplier on the auto-computed height scale. Leave at 1 for an exact fit; " +
                 "nudge down/up if the mesh still reads slightly large/small. Live-adjustable in play mode.")]
        [SerializeField, Range(0.5f, 1.5f)] private float skeletonFitScale = 1f;
        private float lastAppliedFitScale = float.NaN;

        private IRouteRootProvider routeRootProvider;
        private CoordinateFrame referenceFrame;

        private MoveAIFusionAsset asset;
        private HipRecording recording; // source ARKit recording, for pelvis anchor + facing
        private bool isPlaying;
        private bool isPaused;
        private float playbackTime;
        private bool waitingForLocalization;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private int dbgFrame;
#endif
        private bool readyToApply; // set in Update, consumed in LateUpdate so we win over the rig Animator
        private bool loggedPlacement; // one-shot placement diagnostic per playback
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // #region agent log
        private bool loggedMapping; // one-shot joint->bone mapping dump
        // #endregion
#endif
        private Animator characterAnimator; // disabled while we drive bones procedurally
        private FusedPoseSolver.AnchorState anchorState; // last-good ARKit anchor/facing through dropouts

        // Resolved bone transforms keyed by Move joint index.
        private readonly Dictionary<int, Transform> boneByJoint = new Dictionary<int, Transform>();
        // Bind-pose local rotation per bound bone, captured at resolve. The retarget resets to this each frame so
        // the per-frame swing doesn't accumulate roll drift (which looked like progressive twisting).
        private readonly Dictionary<int, Quaternion> bindLocalRotation = new Dictionary<int, Quaternion>();
        // Bind-space aim/up basis for each driven bone. The up axis gives elbows/knees/feet a stable bend plane,
        // avoiding the 180-degree limb flips that happen when only the parent-child direction is constrained.
        private readonly Dictionary<int, Vector3> bindAimLocal = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Vector3> bindUpLocal = new Dictionary<int, Vector3>();
        // Primary child joint per Move joint (defines that bone's aim direction), -1 if none mapped.
        private readonly Dictionary<int, int> primaryChild = new Dictionary<int, int>();
        private int rootJointIndex, spineJointIndex = -1, lHipJointIndex = -1, rHipJointIndex = -1;
        private float recordingHeightMeters;
        // The rig's anatomical (forward,up) basis expressed in characterRoot-local space at bind. Used to align
        // the character's facing to the Move body each frame WITHOUT a manual flip, so it can't end up reversed
        // regardless of how the model was authored (its visual front may be +Z or -Z).
        private Quaternion rigBindLocalAnatomical = Quaternion.identity;
        private bool hasRigBindAnatomical;

        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;
        /// <summary>The 180° yaw flip applied to the Move pose. The compare overlay reads this so the orange
        /// skeleton and the driven character always share one facing (they both feed off FusedPoseSolver).</summary>
        public bool InvertFacing => invertFacing;
        public bool IsWaitingForLocalization => waitingForLocalization;
        public float Duration => asset?.Duration ?? 0f;
        public float CurrentTime => playbackTime;

        public void SetRouteRootLocalOffset(Vector3 offset) => routeRootLocalOffset = offset;

        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;

        public void SetRouteRootProvider(IRouteRootProvider provider)
        {
            routeRootProvider = provider;
            if (provider?.RouteRoot != null)
                referenceFrame = new CoordinateFrame(provider.RouteRoot);
        }

        public void SetCharacterRoot(Transform root)
        {
            characterRoot = root;
            boneByJoint.Clear();
        }

        /// <summary>
        /// Swap the driven character to a different rig at runtime (used by <see cref="CharacterSwitcher"/>).
        /// Safe to call mid-playback: re-resolves bones, re-captures the bind pose, disables the new rig's
        /// Animator, re-applies the skeleton-fit scale, and (if playing) shows the new model immediately.
        /// The previous model is left untouched (the switcher hides it); pass null only to detach.
        /// </summary>
        public void RebindCharacter(Transform newRoot)
        {
            // Hide the old rig if we're swapping it out during playback so two characters never show at once.
            if (characterRoot != null && characterRoot != newRoot)
                SetCharacterVisible(false);

            characterRoot = newRoot;

            // Drop all cached rig state so nothing from the previous model leaks into the new one.
            boneByJoint.Clear();
            bindLocalRotation.Clear();
            bindAimLocal.Clear();
            bindUpLocal.Clear();
            primaryChild.Clear();
            characterAnimator = null;
            hasRigBindAnatomical = false;
            loggedPlacement = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            loggedMapping = false;
#endif

            if (newRoot == null)
                return;

            // Bones can only be resolved once we know the pose (asset). Before the first asset loads we just
            // remember the root; LoadAsset/ResolveCharacterRoot will keep it because it is now non-null.
            if (asset?.pose != null)
            {
                ResolveBones();
                if (boneByJoint.Count == 0)
                    Debug.LogWarning($"[FusedCharacterPlayer] Rebind to '{newRoot.name}' mapped 0 bones — check the rig is Humanoid or its bone names match.");
                DisableRigAnimator();
                if (isPlaying)
                    ApplyRecordingHeightScale();
            }

            SetCharacterVisible(isPlaying && !waitingForLocalization);
            Debug.Log($"[FusedCharacterPlayer] Rebound character to '{newRoot.name}' (playing={isPlaying}).");
        }

        /// <summary>The ARKit recording the asset was baked from; used to anchor the pelvis and align facing.</summary>
        public void SetSourceRecording(HipRecording sourceRecording) => recording = sourceRecording;

        public bool LoadAsset(MoveAIFusionAsset fusionAsset)
        {
            if (fusionAsset == null || fusionAsset.pose == null || fusionAsset.FrameCount == 0)
            {
                Debug.LogError("[FusedCharacterPlayer] Invalid fusion asset");
                return false;
            }
            asset = fusionAsset;
            playbackTime = 0f;
            recordingHeightMeters = EstimateRecordingHeight(recording);
            UpdatePoseScaleFromRecording();
            ResolveCharacterRoot();
            if (characterRoot == null)
                Debug.LogWarning("[FusedCharacterPlayer] No character rig — timeline + compare skeletons only.");
            else
            {
                ResolveBones();
                if (boneByJoint.Count == 0)
                    Debug.LogWarning("[FusedCharacterPlayer] No bones mapped — check MoveJointMap vs your rig bone names.");
                DisableRigAnimator();
            }
            return true;
        }

        /// <summary>
        /// The FBX rig's Animator (added/enabled by FBXCharacterController) overwrites local bone rotations in
        /// the animation pass, which runs after Update and clobbers our retarget. Disable it so the procedural
        /// pose we write in LateUpdate sticks.
        /// </summary>
        void DisableRigAnimator()
        {
            if (characterRoot == null) return;
            characterAnimator = characterRoot.GetComponentInChildren<Animator>(true);
            if (characterAnimator != null && characterAnimator.enabled)
            {
                characterAnimator.enabled = false;
                Debug.Log("[FusedCharacterPlayer] Disabled rig Animator so procedural retarget can drive bones.");
            }
        }

        public void StartPlayback()
        {
            if (asset == null)
            {
                Debug.LogError("[FusedCharacterPlayer] No asset loaded");
                return;
            }
            if (routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
                Debug.LogError("[FusedCharacterPlayer] No RouteRoot provider; wait for localization");
                return;
            }

            if (characterRoot != null)
                ApplyRecordingHeightScale();

            DisableRigAnimator();
            isPlaying = true;
            isPaused = false;
            playbackTime = 0f;
            readyToApply = false;
            loggedPlacement = false;
            anchorState = default;
            waitingForLocalization = !routeRootProvider.IsLocalized;
            SetCharacterVisible(characterRoot != null && !waitingForLocalization);
            OnPlaybackStarted?.Invoke();
        }

        public void StopPlayback()
        {
            if (!isPlaying) return;
            isPlaying = false;
            isPaused = false;
            SetCharacterVisible(false);
            OnPlaybackStopped?.Invoke();
        }

        /// <summary>Pause the fused replay in place without resetting the timeline.</summary>
        public void PausePlayback()
        {
            if (!isPlaying || isPaused) return;
            isPaused = true;
        }

        /// <summary>Resume the fused replay from where it was paused.</summary>
        public void ResumePlayback()
        {
            if (!isPlaying || !isPaused) return;
            isPaused = false;
        }

        /// <summary>Seek the fused replay to a specific time (seconds).</summary>
        public void SeekToTime(float time)
        {
            if (asset == null) return;
            playbackTime = Mathf.Clamp(time, 0f, asset.Duration);
            // Re-apply the frozen frame next LateUpdate so the character snaps to the new time even when paused.
            readyToApply = isPlaying && !waitingForLocalization;
        }

        void Update()
        {
            readyToApply = false;
            if (!isPlaying || asset == null) return;

            // Live calibration: if the fit slider was nudged in the inspector during play mode, re-apply the
            // uniform height scale (the ratio math is scale-invariant, so re-applying is stable, not cumulative).
            if (characterRoot != null && !Mathf.Approximately(skeletonFitScale, lastAppliedFitScale))
                ApplyRecordingHeightScale();

            bool localized = routeRootProvider == null || routeRootProvider.IsLocalized;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            if ((dbgFrame++ % 60) == 0)
                BodyTracking.DebugTools.DebugSessionLog.Log("A", "FusedCharacterPlayer.cs:Update",
                    "fused Update tick",
                    "{\"localized\":" + (localized ? "true" : "false") +
                    ",\"waiting\":" + (waitingForLocalization ? "true" : "false") +
                    ",\"playbackTime\":" + playbackTime.ToString("F2") +
                    ",\"characterNull\":" + (characterRoot == null ? "true" : "false") + "}");
            // #endregion
#endif
            if (!localized)
            {
                if (!waitingForLocalization)
                {
                    waitingForLocalization = true;
                    SetCharacterVisible(false);
                }
                return;
            }
            if (waitingForLocalization)
            {
                waitingForLocalization = false;
                if (characterRoot != null)
                    SetCharacterVisible(true);
            }

            // Track the live RouteRoot pose each frame (Immersal scene updates / image-target follow).
            if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                referenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);

            // While paused, keep re-applying the frozen frame (so it stays anchored if the phone moves) but
            // do not advance the timeline.
            if (isPaused)
            {
                readyToApply = true;
                return;
            }

            playbackTime += Time.deltaTime * playbackSpeed;
            float duration = asset.Duration;
            if (playbackTime >= duration)
            {
                if (loop) playbackTime %= duration;
                else { StopPlayback(); return; }
            }

            // Defer the actual bone write to LateUpdate so it lands after the rig's animation pass.
            readyToApply = true;
        }

        void LateUpdate()
        {
            if (!isPlaying || !readyToApply || characterRoot == null) return;
            ApplyFrame(playbackTime);
        }

        void ApplyFrame(float t)
        {
            if (characterRoot == null) return;

            // Positions-based retarget: drive the rig from Move's reliable joint POSITIONS rather than its
            // source local rotations. Each bone aims at its Move child and uses a captured bind-pose up axis
            // to keep elbows, knees, hands, and feet from twisting/flipping around the segment direction.
            Vector3[] local = FusedPoseSolver.ComputeLocalJoints(asset, recording, t, ref anchorState, invertFacing);
            if (local == null) return;

            int n = local.Length;
            var world = new Vector3[n];
            for (int i = 0; i < n; i++)
                world[i] = referenceFrame.TransformPoint(local[i] + routeRootLocalOffset);

            // Reset every bound bone to its bind pose so this frame's swing is computed fresh (no roll drift).
            foreach (var kv in boneByJoint)
                if (kv.Value != null && bindLocalRotation.TryGetValue(kv.Key, out var bind))
                    kv.Value.localRotation = bind;

            // Place + face the whole character from the Move pelvis basis.
            OrientAndPlaceRoot(world);
            BodyBasis bodyBasis = ComputeBodyBasis(world);

            if (!loggedPlacement)
            {
                loggedPlacement = true;
                Vector3 rr = referenceFrame.position;
                Vector3 hipW = world[rootJointIndex];
                Debug.Log($"[FusedCharacterPlayer] Placement: RouteRoot world={rr:F3} rot={referenceFrame.rotation.eulerAngles:F1} " +
                          $"| hip world={hipW:F3} | moveBodyForward={bodyBasis.forward:F3} | localized={(routeRootProvider == null || routeRootProvider.IsLocalized)}. " +
                          "moveBodyForward should be the climber's anatomical front; the per-bone twist reference matches it to the rig bind forward.");
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            if (!loggedMapping)
            {
                loggedMapping = true;
                // Focus on the extremities (hands/feet) where the flip is visible, incl. whether the child bone the
                // swing aims at is actually mapped (H5: unmapped child => leaf bone never oriented).
                string[] probe = { "Left_elbow", "Left_wrist", "Right_elbow", "Right_wrist",
                                   "Left_knee", "Left_ankle", "Left_toe", "Right_knee", "Right_ankle", "Right_toe" };
                var sb = new System.Text.StringBuilder("[DBG5f9dd8][H4/H5/H6] joint->bone map: ");
                foreach (var name in probe)
                {
                    int idx = asset.pose.IndexOfJoint(name);
                    string bn = (idx >= 0 && boneByJoint.TryGetValue(idx, out var b) && b != null) ? b.name : "<none>";
                    int childIdx = (idx >= 0 && primaryChild.TryGetValue(idx, out var c)) ? c : -1;
                    string cn = childIdx >= 0 && childIdx < asset.pose.jointNames.Count ? asset.pose.jointNames[childIdx] : "<none>";
                    bool childMapped = childIdx >= 0 && boneByJoint.TryGetValue(childIdx, out var cb) && cb != null;
                    sb.Append($"{name}[{idx}]->{bn}(child={cn},childMapped={childMapped}) ");
                }
                UnityEngine.Debug.Log(sb.ToString());
            }
            // Log left/right hip world X each ~30 frames to detect mirroring (left should differ in sign from right).
            if ((dbgFrame % 30) == 0)
            {
                int li = asset.pose.IndexOfJoint("Left_hip"), ri = asset.pose.IndexOfJoint("Right_hip");
                if (li >= 0 && ri >= 0 && li < world.Length && ri < world.Length)
                    UnityEngine.Debug.Log($"[DBG5f9dd8][H3] hipWorld L={world[li]:F3} R={world[ri]:F3} (L-R)={(world[li]-world[ri]):F3}");
            }
            // #endregion
#endif

            // Aim each mapped bone toward its primary mapped child (parents already iterate first because
            // Move topology is parent-ordered), while preserving the target limb bend plane.
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                if (!boneByJoint.TryGetValue(j, out var bone) || bone == null) continue;
                if (!primaryChild.TryGetValue(j, out int child) || child < 0) continue;
                if (!boneByJoint.TryGetValue(child, out var childBone) || childBone == null) continue;
                if (childBone == bone) continue;

                ApplyBoneBasis(j, child, bone, world, bodyBasis);
            }
        }

        /// <summary>Position the character so its hips sit on the Move pelvis, facing the Move body basis.</summary>
        void OrientAndPlaceRoot(Vector3[] world)
        {
            Vector3 hipsW = world[rootJointIndex];
            BodyBasis basis = ComputeBodyBasis(world);

            // Target world basis = the Move climber's anatomical front/up.
            Quaternion targetAnatomical = Quaternion.LookRotation(basis.forward, basis.up);
            if (hasRigBindAnatomical)
            {
                // Rotate the root so its OWN anatomical basis lands on the Move basis. Because the rig basis was
                // captured from the same bones (cross of hip + spine axes), this faces the character the same way
                // as the orange regardless of the model's authored forward — no manual flip, can't end up reversed.
                characterRoot.rotation = targetAnatomical * Quaternion.Inverse(rigBindLocalAnatomical);
            }
            else
            {
                // Fallback only if the rig hips/spine couldn't be resolved: legacy behaviour + manual flip.
                characterRoot.rotation = targetAnatomical;
                if (flipCharacterForward)
                    characterRoot.rotation *= Quaternion.Euler(0f, 180f, 0f);
            }

            // After rotating, shift so the actual hips bone lands on the Move pelvis.
            if (boneByJoint.TryGetValue(rootJointIndex, out var hipsBone) && hipsBone != null)
                characterRoot.position += hipsW - hipsBone.position;
            else
                characterRoot.position = hipsW;
        }

        struct BodyBasis
        {
            public Vector3 up;
            public Vector3 right;
            public Vector3 forward;
        }

        BodyBasis ComputeBodyBasis(Vector3[] world)
        {
            Vector3 hipsW = world[rootJointIndex];
            Vector3 up = spineJointIndex >= 0 ? world[spineJointIndex] - hipsW : referenceFrame.rotation * Vector3.up;
            Vector3 right = (lHipJointIndex >= 0 && rHipJointIndex >= 0)
                ? world[rHipJointIndex] - world[lHipJointIndex]
                : referenceFrame.rotation * Vector3.right;

            if (up.sqrMagnitude < 1e-8f) up = referenceFrame.rotation * Vector3.up;
            if (right.sqrMagnitude < 1e-8f) right = referenceFrame.rotation * Vector3.right;

            Vector3 forward = Vector3.Cross(right.normalized, up.normalized);
            if (forward.sqrMagnitude < 1e-8f) forward = referenceFrame.rotation * Vector3.forward;

            return new BodyBasis
            {
                up = up.normalized,
                right = right.normalized,
                forward = forward.normalized
            };
        }

        void ApplyBoneBasis(int joint, int child, Transform bone, Vector3[] world, BodyBasis bodyBasis)
        {
            if (!bindAimLocal.TryGetValue(joint, out var bindAim) ||
                !bindUpLocal.TryGetValue(joint, out var bindUp))
                return;

            Vector3 currentAim = bone.TransformDirection(bindAim);
            Vector3 currentUp = bone.TransformDirection(bindUp);
            Vector3 targetAim = world[child] - world[joint];
            // Twist reference is the Move body's anatomical FRONT — the SAME reference captured at bind time
            // (see CaptureBindAxes / ComputeRigBindBasis). Using one consistent reference for both is what
            // stops limbs from rolling 180° (flipping) on rigs whose authored forward differs from cross(right, up).
            Vector3 targetUp = bodyBasis.forward;

            if (!TryBuildRotation(currentAim, currentUp, out var from) ||
                !TryBuildRotation(targetAim, targetUp, out var to))
                return;

            bone.rotation = to * Quaternion.Inverse(from) * bone.rotation;
        }

        static bool TryBuildRotation(Vector3 aim, Vector3 upHint, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (aim.sqrMagnitude < 1e-8f)
                return false;

            Vector3 forward = aim.normalized;
            if (!TryProjectUp(forward, upHint, out var up))
                return false;

            rotation = Quaternion.LookRotation(forward, up);
            return true;
        }

        static bool TryProjectUp(Vector3 forward, Vector3 upHint, out Vector3 up)
        {
            up = Vector3.ProjectOnPlane(upHint, forward);
            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.ProjectOnPlane(Vector3.up, forward);
            if (up.sqrMagnitude < 1e-8f)
                up = Vector3.ProjectOnPlane(Vector3.forward, forward);
            if (up.sqrMagnitude < 1e-8f)
                return false;

            up.Normalize();
            return true;
        }

        void UpdatePoseScaleFromRecording()
        {
            if (asset?.pose == null) return;
            if (recordingHeightMeters <= 0.1f) return;

            float poseHeight = EstimatePoseHeight(asset.pose);
            if (poseHeight <= 0.1f) return;

            asset.scale = Mathf.Max(0.01f, recordingHeightMeters / poseHeight);
            Debug.Log($"[FusedCharacterPlayer] Pose height scale={asset.scale:F3} " +
                      $"(recording {recordingHeightMeters:F2}m / Move pose {poseHeight:F2}m)");
        }

        void ApplyRecordingHeightScale()
        {
            if (characterRoot == null) return;

            float fit = Mathf.Max(0.01f, skeletonFitScale);
            lastAppliedFitScale = skeletonFitScale;

            float currentScale = Mathf.Max(0.01f, (characterRoot.localScale.x + characterRoot.localScale.y + characterRoot.localScale.z) / 3f);

            // Preferred (default): match the character's RENDERED height — the crown of the mesh (hair included)
            // down to the soles — to the recorded climber's height. The orange/cyan skeletons only reach the Head
            // JOINT (well below the crown) and the toe JOINT (above the sole), so a joint-to-joint chain fit leaves
            // the mesh overshooting top and bottom and the character looks too tall. Fitting the mesh extent makes
            // the visible character sit inside the skeleton instead. Hips stay pinned every frame in OrientAndPlaceRoot.
            if (heightFitMode == HeightFitMode.RenderedMeshHeight)
            {
                float meshTarget = recordingHeightMeters;
                if (meshTarget <= 0.1f)
                    meshTarget = EstimatePoseHeight(asset?.pose) * Mathf.Max(0.01f, asset?.scale ?? 1f);

                if (meshTarget > 0.1f && TryEstimateCharacterMeshHeight(out float meshHeight) && meshHeight > 0.1f)
                {
                    float meshScale = Mathf.Max(0.01f, currentScale * meshTarget / meshHeight * fit);
                    characterRoot.localScale = Vector3.one * meshScale;
                    Debug.Log($"[FusedCharacterPlayer] Character mesh-height fit scale={meshScale:F3} " +
                              $"(climber {meshTarget:F2}m / mesh crown-to-sole {meshHeight:F2}m, fit={fit:F2})");
                    return;
                }
                // Mesh bounds couldn't be measured — fall through to the bone-chain / extent estimate below.
            }

            // Preferred fit: match the FBX's foot->head BONE-CHAIN length to the orange (Move) skeleton's, both
            // measured over the SAME mapped joints. Chain lengths are pose-invariant (segment lengths are fixed),
            // so with roughly-matched proportions a single uniform scale lands BOTH the head and the feet on the
            // orange joints automatically — the hips are already pinned every frame in OrientAndPlaceRoot.
            if (TryMatchChainHeight(out float chainScale))
            {
                float chainTarget = Mathf.Max(0.01f, chainScale * fit);
                characterRoot.localScale = Vector3.one * chainTarget;
                Debug.Log($"[FusedCharacterPlayer] Character chain-fit scale={chainTarget:F3} " +
                          $"(foot->head chain match, fit={fit:F2})");
                return;
            }

            // Fallback: overall head-to-foot extent vs the solved Move height (used when the foot/head joints
            // can't be resolved on this rig).
            float targetHeight = EstimatePoseHeight(asset?.pose) * Mathf.Max(0.01f, asset?.scale ?? 1f);
            if (targetHeight <= 0.1f)
                targetHeight = recordingHeightMeters;
            if (targetHeight <= 0.1f)
            {
                characterRoot.localScale = Vector3.one * Mathf.Max(0.01f, (asset?.scale ?? 1f) * fit);
                return;
            }

            bool usingBoneHeight = TryEstimateCharacterBoneHeight(out float currentHeight);
            if (!usingBoneHeight || currentHeight <= 0.1f)
                currentHeight = EstimateCharacterHeight(characterRoot);
            if (currentHeight <= 0.1f)
            {
                characterRoot.localScale = Vector3.one * Mathf.Max(0.01f, (asset?.scale ?? 1f) * fit);
                return;
            }

            float targetScale = Mathf.Max(0.01f, currentScale * targetHeight / currentHeight * fit);
            characterRoot.localScale = Vector3.one * targetScale;
            Debug.Log($"[FusedCharacterPlayer] Character height scale={targetScale:F3} " +
                      $"(target {targetHeight:F2}m / model {currentHeight:F2}m, " +
                      $"fit={fit:F2}, measure={(usingBoneHeight ? "joints" : "mesh-bounds")})");
        }

        /// <summary>
        /// Uniform localScale that makes the character's foot->head bone-chain length equal the orange (Move)
        /// skeleton's, measured over the same mapped joints. Returns false if the head/foot joints or enough of
        /// the chain can't be resolved on this rig (caller then falls back to the extent-based estimate).
        /// </summary>
        bool TryMatchChainHeight(out float scale)
        {
            scale = 0f;
            if (asset?.pose == null || boneByJoint.Count == 0 || asset.pose.FrameCount == 0)
                return false;

            var pose = asset.pose;
            int headIdx = pose.IndexOfJoint("Head");
            if (headIdx < 0) headIdx = pose.IndexOfJoint("Neck");
            int footIdx = pose.IndexOfJoint("Left_toe");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Left_ankle");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Right_toe");
            if (footIdx < 0) footIdx = pose.IndexOfJoint("Right_ankle");
            if (headIdx < 0 || footIdx < 0) return false;

            // Move joint world positions (rest is fine — bone lengths are constant), scaled the same way the
            // orange skeleton is drawn (asset.scale).
            Vector3[] fk = pose.ForwardKinematics(pose.frames[0]);
            if (fk == null || fk.Length == 0) return false;
            float poseScale = Mathf.Max(0.01f, asset.scale);

            float moveLen = 0f, charLen = 0f;
            bool ok = AccumulateChain(headIdx, fk, ref moveLen, ref charLen) &
                      AccumulateChain(footIdx, fk, ref moveLen, ref charLen);
            if (!ok || moveLen <= 0.05f || charLen <= 0.05f) return false;

            float currentScale = Mathf.Max(0.01f, (characterRoot.localScale.x + characterRoot.localScale.y + characterRoot.localScale.z) / 3f);
            scale = Mathf.Max(0.01f, currentScale * (moveLen * poseScale) / charLen);
            return true;
        }

        /// <summary>
        /// Walk from <paramref name="leaf"/> up the Move parent chain to the root, summing segment lengths over
        /// joints that are mapped to a character bone — accumulating the Move length (from <paramref name="fk"/>)
        /// and the character length (from the bound bone world positions) over the SAME consecutive joints so the
        /// two are directly comparable. Returns false if fewer than two chain joints are mapped.
        /// </summary>
        bool AccumulateChain(int leaf, Vector3[] fk, ref float moveLen, ref float charLen)
        {
            var pose = asset.pose;
            int prev = -1;
            int counted = 0;
            int guard = 0;
            for (int j = leaf; j >= 0 && guard < 128; guard++)
            {
                if (boneByJoint.TryGetValue(j, out var bone) && bone != null && j < fk.Length)
                {
                    if (prev >= 0)
                    {
                        moveLen += Vector3.Distance(fk[prev], fk[j]);
                        charLen += Vector3.Distance(boneByJoint[prev].position, bone.position);
                        counted++;
                    }
                    prev = j;
                }
                j = j < pose.jointParents.Count ? pose.jointParents[j] : -1;
            }
            return counted > 0;
        }

        /// <summary>
        /// Vertical extent of the character's MAPPED bones in its current (bind) pose — head bone down to the
        /// lowest foot/toe bone. This mirrors <see cref="EstimatePoseHeight"/> (min/max joint Y of the Move pose),
        /// so scaling the character to the recorded height by this metric makes its joints land exactly on the
        /// orange skeleton instead of falling short because the mesh box is taller than the skeleton.
        /// </summary>
        bool TryEstimateCharacterBoneHeight(out float height)
        {
            height = 0f;
            if (boneByJoint.Count == 0) return false;

            float min = float.MaxValue;
            float max = float.MinValue;
            int counted = 0;
            foreach (var kv in boneByJoint)
            {
                if (kv.Value == null) continue;
                float y = kv.Value.position.y;
                min = Mathf.Min(min, y);
                max = Mathf.Max(max, y);
                counted++;
            }

            if (counted < 2 || max <= min) return false;
            height = max - min;
            return height > 0.1f;
        }

        /// <summary>
        /// The character's RENDERED vertical extent (crown of the mesh/hair down to the soles) in world meters at
        /// the current scale. Uses each skinned mesh's bind-pose <c>sharedMesh.bounds</c> (pose-invariant and valid
        /// even while the model is hidden), so it captures the hair/crown that sits above the Head joint — the part
        /// the bone-chain fit ignores and that makes the character look too tall.
        /// </summary>
        bool TryEstimateCharacterMeshHeight(out float height)
        {
            height = 0f;
            if (characterRoot == null) return false;

            var renderers = characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers == null || renderers.Length == 0) return false;

            float min = float.MaxValue;
            float max = float.MinValue;
            bool any = false;

            foreach (var smr in renderers)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                Bounds b = smr.sharedMesh.bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;
                Matrix4x4 m = smr.transform.localToWorldMatrix;

                // Transform all 8 corners so non-axis-aligned rigs are measured correctly.
                for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    Vector3 w = m.MultiplyPoint3x4(c + new Vector3(e.x * sx, e.y * sy, e.z * sz));
                    min = Mathf.Min(min, w.y);
                    max = Mathf.Max(max, w.y);
                    any = true;
                }
            }

            if (!any || max <= min) return false;
            height = max - min;
            return height > 0.1f;
        }

        static float EstimateCharacterHeight(Transform root)
        {
            if (root == null) return 0f;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds && bounds.size.y > 0.1f)
                return bounds.size.y;

            var bones = root.GetComponentsInChildren<Transform>(true);
            if (bones == null || bones.Length == 0) return 0f;
            float min = float.MaxValue;
            float max = float.MinValue;
            foreach (var bone in bones)
            {
                min = Mathf.Min(min, bone.position.y);
                max = Mathf.Max(max, bone.position.y);
            }
            return max > min ? max - min : 0f;
        }

        static float EstimatePoseHeight(MoveMotion pose)
        {
            if (pose == null || pose.FrameCount == 0) return 0f;

            var heights = new List<float>();
            int stride = Mathf.Max(1, pose.FrameCount / 60);
            for (int i = 0; i < pose.FrameCount; i += stride)
            {
                var fk = pose.ForwardKinematics(pose.frames[i]);
                if (fk == null || fk.Length == 0) continue;
                float min = float.MaxValue;
                float max = float.MinValue;
                for (int j = 0; j < fk.Length; j++)
                {
                    min = Mathf.Min(min, fk[j].y);
                    max = Mathf.Max(max, fk[j].y);
                }
                if (max > min) heights.Add(max - min);
            }

            return Percentile(heights, 0.95f);
        }

        static float EstimateRecordingHeight(HipRecording source)
        {
            if (source?.frames == null || source.frames.Count == 0) return 0f;

            var chainHeights = new List<float>();
            var extentHeights = new List<float>();
            foreach (var frame in source.frames)
            {
                if (frame.recordedJoints == null || frame.recordedJoints.Count == 0) continue;
                if (TryEstimateArkitChainHeight(frame, out float chainHeight))
                    chainHeights.Add(chainHeight);
                if (TryEstimateHeadFootExtent(frame, out float extentHeight))
                    extentHeights.Add(extentHeight);
            }

            float height = Median(chainHeights);
            if (height > 0.1f) return height;
            return Percentile(extentHeights, 0.95f);
        }

        static bool TryEstimateArkitChainHeight(HipFrame frame, out float height)
        {
            height = 0f;

            // ARKit 3D body chain: head -> neck/spine -> hips plus the longer leg to toes/foot.
            int[] torso3D = { 51, 50, 49, 48, 47, 18, 17, 16, 15, 14, 13, 12, 1 };
            int[] leftLeg3D = { 1, 2, 3, 4, 5 };
            int[] rightLeg3D = { 1, 7, 8, 9, 10 };
            if (TryChainLength(frame, torso3D, out float torso) &&
                (TryChainLength(frame, leftLeg3D, out float leftLeg) | TryChainLength(frame, rightLeg3D, out float rightLeg)))
            {
                height = torso + Mathf.Max(leftLeg, rightLeg);
                return height > 0.5f;
            }

            // ARKit 2D fallback used by older recordings.
            int[] torso2D = { 0, 1, 16 };
            int[] leftLeg2D = { 16, 11, 12, 13 };
            int[] rightLeg2D = { 16, 8, 9, 10 };
            if (TryChainLength(frame, torso2D, out torso) &&
                (TryChainLength(frame, leftLeg2D, out leftLeg) | TryChainLength(frame, rightLeg2D, out rightLeg)))
            {
                height = torso + Mathf.Max(leftLeg, rightLeg);
                return height > 0.5f;
            }

            return false;
        }

        static bool TryEstimateHeadFootExtent(HipFrame frame, out float height)
        {
            height = 0f;
            int[] headCandidates = { 51, 58, 54, 59, 0, 14, 15 };
            int[] footCandidates = { 5, 6, 10, 11, 4, 9, 13, 10 };
            bool gotHead = false;
            bool gotFoot = false;
            float headY = float.MinValue;
            float footY = float.MaxValue;

            foreach (int idx in headCandidates)
            {
                if (TryGetSample(frame, idx, out var sample))
                {
                    gotHead = true;
                    headY = Mathf.Max(headY, sample.positionReference.y);
                }
            }

            foreach (int idx in footCandidates)
            {
                if (TryGetSample(frame, idx, out var sample))
                {
                    gotFoot = true;
                    footY = Mathf.Min(footY, sample.positionReference.y);
                }
            }

            if (!gotHead || !gotFoot || headY <= footY)
                return false;

            height = headY - footY;
            return height > 0.1f;
        }

        static bool TryChainLength(HipFrame frame, int[] indices, out float length)
        {
            length = 0f;
            if (indices == null || indices.Length < 2) return false;

            bool gotAny = false;
            for (int i = 1; i < indices.Length; i++)
            {
                if (!TryGetSample(frame, indices[i - 1], out var a) ||
                    !TryGetSample(frame, indices[i], out var b))
                    return false;

                length += Vector3.Distance(a.positionReference, b.positionReference);
                gotAny = true;
            }

            return gotAny && length > 0.1f;
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

        static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            return values[values.Count / 2];
        }

        static float Percentile(List<float> values, float percentile)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            int index = Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * percentile), 0, values.Count - 1);
            return values[index];
        }

        void ResolveCharacterRoot()
        {
            if (characterRoot != null) return;

            if (fbxCharacterController == null && autoFindCharacter)
                fbxCharacterController = FindFirstObjectByType<FBXCharacterController>();

            if (fbxCharacterController != null)
            {
                // Recording uses skeleton-only mode; spawn the rig on demand for fused replay.
                if (fbxCharacterController.CharacterRoot == null)
                    fbxCharacterController.Initialize();
                if (fbxCharacterController.CharacterRoot != null)
                    characterRoot = fbxCharacterController.CharacterRoot.transform;
            }
        }

        void ResolveBones()
        {
            boneByJoint.Clear();
            if (characterRoot == null || asset?.pose == null) return;

            // Prefer the Humanoid avatar mapping: it is rig-name independent, so any humanoid robot/character
            // resolves correctly even when its bone names don't match Mixamo. Fall back to name matching.
            if (characterAnimator == null)
                characterAnimator = characterRoot.GetComponentInChildren<Animator>(true);
            bool humanoid = characterAnimator != null && characterAnimator.isHuman && characterAnimator.avatar != null;

            var transforms = characterRoot.GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, Transform>();
            foreach (var tr in transforms)
            {
                if (!byName.ContainsKey(tr.name))
                    byName[tr.name] = tr;
            }

            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                string moveName = asset.pose.jointNames[j];
                Transform bone = null;

                if (humanoid && MoveToHumanBone.TryGetValue(moveName, out var hbb))
                    bone = characterAnimator.GetBoneTransform(hbb);

                if (bone == null && jointMap.TryGetBone(moveName, out string boneName))
                    bone = FindBone(byName, boneName);

                if (bone != null)
                    boneByJoint[j] = bone;
            }

            // Fallback for non-humanoid / non-Mixamo rigs: bind remaining joints by common bone-name conventions
            // (UE/Blender/Daz/Unity etc.), so any rig animates without requiring an exact name match.
            ResolveBonesByConvention(transforms);

            // Capture the bind/T-pose local rotations now (rig is at bind here) for the non-accumulating retarget.
            bindLocalRotation.Clear();
            foreach (var kv in boneByJoint)
                if (kv.Value != null)
                    bindLocalRotation[kv.Key] = kv.Value.localRotation;

            BuildRetargetTopology();
            CaptureBindAxes();
            Debug.Log($"[FusedCharacterPlayer] Resolved {boneByJoint.Count}/{asset.pose.jointNames.Count} bones " +
                      $"(humanoid avatar: {humanoid})");
            VerifyRig(humanoid);
        }

        void CaptureBindAxes()
        {
            bindAimLocal.Clear();
            bindUpLocal.Clear();

            // The bind twist reference MUST be the rig's anatomical FRONT computed the SAME way the Move body
            // front is computed at runtime (cross of the hip axis and the up/spine axis). Deriving it from the
            // rig's own bind-pose bones — rather than characterRoot.forward, which is the model's authored facing
            // and can be 180° off — is what keeps every limb's roll consistent so they don't flip.
            hasRigBindAnatomical = ComputeRigBindBasis(out Vector3 rigForward, out Vector3 rigUp);
            if (hasRigBindAnatomical && characterRoot != null)
            {
                // Store the anatomical basis relative to the root so OrientAndPlaceRoot can map it onto the Move
                // body basis directly (self-correcting facing — no manual 180° flip that depends on the model).
                Quaternion worldAnatomical = Quaternion.LookRotation(rigForward, rigUp);
                rigBindLocalAnatomical = Quaternion.Inverse(characterRoot.rotation) * worldAnatomical;
                Debug.Log($"[FusedCharacterPlayer] Rig bind anatomical forward={rigForward:F3} up={rigUp:F3} " +
                          $"(authored characterRoot.forward={characterRoot.forward:F3}). Facing is auto-aligned to the Move body.");
            }

            foreach (var kv in primaryChild)
            {
                int joint = kv.Key;
                int child = kv.Value;
                if (child < 0) continue;
                if (!boneByJoint.TryGetValue(joint, out var bone) || bone == null) continue;
                if (!boneByJoint.TryGetValue(child, out var childBone) || childBone == null) continue;
                if (childBone == bone) continue;

                Vector3 aim = childBone.position - bone.position;
                if (aim.sqrMagnitude < 1e-8f) continue;
                if (!TryProjectUp(aim.normalized, rigForward, out var projectedUp))
                    continue;

                bindAimLocal[joint] = bone.InverseTransformDirection(aim.normalized);
                bindUpLocal[joint] = bone.InverseTransformDirection(projectedUp);
            }
        }

        /// <summary>
        /// The rig's anatomical (forward,up) basis in its current bind pose, from its own hip/spine bones.
        /// Mirrors <see cref="ComputeBodyBasis"/> (which derives the Move basis) so bind and runtime share one
        /// reference for both per-bone twist and whole-body facing. Returns false if the bones aren't resolved.
        /// </summary>
        bool ComputeRigBindBasis(out Vector3 forward, out Vector3 up)
        {
            forward = characterRoot != null ? characterRoot.forward : Vector3.forward;
            up = characterRoot != null ? characterRoot.up : Vector3.up;

            if (!boneByJoint.TryGetValue(rootJointIndex, out var hips) || hips == null)
                return false;

            Vector3 rigUp = Vector3.up;
            if (spineJointIndex >= 0 && boneByJoint.TryGetValue(spineJointIndex, out var spine) && spine != null)
                rigUp = spine.position - hips.position;

            Vector3 right = Vector3.right;
            if (lHipJointIndex >= 0 && rHipJointIndex >= 0 &&
                boneByJoint.TryGetValue(lHipJointIndex, out var lHip) && lHip != null &&
                boneByJoint.TryGetValue(rHipJointIndex, out var rHip) && rHip != null)
                right = rHip.position - lHip.position;

            if (rigUp.sqrMagnitude < 1e-8f) rigUp = Vector3.up;
            if (right.sqrMagnitude < 1e-8f) right = Vector3.right;

            Vector3 rigForward = Vector3.Cross(right.normalized, rigUp.normalized);
            if (rigForward.sqrMagnitude < 1e-8f)
                return false;

            forward = rigForward.normalized;
            up = rigUp.normalized;
            return true;
        }

        /// <summary>
        /// One-shot rig sanity report so any FBX character can be validated at a glance: avatar/humanoid status,
        /// which Move joints failed to bind, and any required humanoid bones the avatar is missing.
        /// </summary>
        void VerifyRig(bool humanoid)
        {
            if (characterAnimator == null)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: no Animator on character — using bone-name matching only. " +
                                 "Set the FBX Rig → Animation Type = Humanoid for reliable retargeting on any character.");
            else if (!humanoid)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: Animator present but not Humanoid (avatar null or generic). " +
                                 "Set Rig → Animation Type = Humanoid and Apply so bones map regardless of names.");

            // Move joints that should drive a bone but didn't bind.
            var unbound = new List<string>();
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
            {
                string n = asset.pose.jointNames[j];
                if (MoveToHumanBone.ContainsKey(n) && !boneByJoint.ContainsKey(j))
                    unbound.Add(n);
            }
            if (unbound.Count > 0)
                Debug.LogWarning("[FusedCharacterPlayer] Rig check: Move joints with no bound bone (rig is missing these): " +
                                 string.Join(", ", unbound));

            // Required humanoid bones the avatar itself lacks (only meaningful for humanoid rigs).
            if (humanoid)
            {
                var missing = new List<string>();
                foreach (var kv in MoveToHumanBone)
                    if (characterAnimator.GetBoneTransform(kv.Value) == null)
                        missing.Add(kv.Value.ToString());
                if (missing.Count > 0)
                    Debug.Log("[FusedCharacterPlayer] Rig check: optional humanoid bones not present on this avatar: " +
                              string.Join(", ", missing));
                else
                    Debug.Log("[FusedCharacterPlayer] Rig check: humanoid avatar has all mapped bones. Good to go.");
            }
        }

        /// <summary>
        /// Bind any still-unbound Move joints by matching common bone-name conventions (rig-name independent).
        /// Joints are processed most-specific first and each rig transform is claimed once, so e.g. "forearm"
        /// can't get stolen by the upper-arm match.
        /// </summary>
        void ResolveBonesByConvention(Transform[] transforms)
        {
            // Already-claimed transforms (from humanoid / name-map pass) must not be reused.
            var claimed = new HashSet<Transform>();
            foreach (var kv in boneByJoint) claimed.Add(kv.Value);

            // Normalized (lowercase, separators stripped) name for each candidate transform.
            var norm = new List<KeyValuePair<string, Transform>>(transforms.Length);
            foreach (var tr in transforms)
                norm.Add(new KeyValuePair<string, Transform>(Norm(tr.name), tr));

            // Map Move joint name -> index, for quick "is this already bound" checks.
            var indexByName = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < asset.pose.jointNames.Count; j++)
                if (!indexByName.ContainsKey(asset.pose.jointNames[j]))
                    indexByName[asset.pose.jointNames[j]] = j;

            foreach (var moveName in ConventionOrder)
            {
                if (!indexByName.TryGetValue(moveName, out int j)) continue;
                if (boneByJoint.ContainsKey(j)) continue;
                if (!ConventionAliases.TryGetValue(moveName, out var aliases)) continue;

                Transform best = MatchByAliases(norm, claimed, aliases);
                if (best != null)
                {
                    boneByJoint[j] = best;
                    claimed.Add(best);
                }
            }
        }

        static Transform MatchByAliases(List<KeyValuePair<string, Transform>> norm, HashSet<Transform> claimed, string[] aliases)
        {
            // Pass 1: exact normalized equality. Pass 2: endswith (prefix rigs). Pass 3: contains (suffix rigs).
            foreach (var a in aliases)
                foreach (var kv in norm)
                    if (!claimed.Contains(kv.Value) && kv.Key == a) return kv.Value;
            foreach (var a in aliases)
                foreach (var kv in norm)
                    if (!claimed.Contains(kv.Value) && kv.Key.EndsWith(a, System.StringComparison.Ordinal)) return kv.Value;
            foreach (var a in aliases)
                if (a.Length >= 6)
                    foreach (var kv in norm)
                        if (!claimed.Contains(kv.Value) && kv.Key.Contains(a)) return kv.Value;
            return null;
        }

        static string Norm(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // Most-specific first so longer joints claim their transform before shorter/ambiguous ones.
        static readonly string[] ConventionOrder =
        {
            "Left_toe","Right_toe","Left_ankle","Right_ankle","Left_knee","Right_knee","Left_hip","Right_hip",
            "Left_wrist","Right_wrist","Left_elbow","Right_elbow","Left_shoulder","Right_shoulder",
            "Left_shoulder_rotation","Right_shoulder_rotation","Left_clavicle","Right_clavicle",
            "Spine3","Spine2","Spine1","Neck","Head","Root",
        };

        static readonly Dictionary<string, string[]> ConventionAliases = new Dictionary<string, string[]>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Root", new[]{ "hips","pelvis","root","cog" } },
            { "Spine1", new[]{ "spine1","spine01","spine","abdomenlower" } },
            { "Spine2", new[]{ "spine2","spine02","chest","abdomenupper" } },
            { "Spine3", new[]{ "spine3","spine03","upperchest" } },
            { "Neck", new[]{ "neck1","neck" } },
            { "Head", new[]{ "head" } },
            { "Left_clavicle", new[]{ "leftshoulder","leftclavicle","claviclel","leftcollar","collarleft","lcollar" } },
            { "Right_clavicle", new[]{ "rightshoulder","rightclavicle","clavicler","rightcollar","collarright","rcollar" } },
            { "Left_shoulder", new[]{ "leftupperarm","leftarm","upperarmleft","upperarml","luparm","armleft" } },
            { "Right_shoulder", new[]{ "rightupperarm","rightarm","upperarmright","upperarmr","ruparm","armright" } },
            { "Left_shoulder_rotation", new[]{ "leftupperarm","leftarm","upperarmleft","upperarml" } },
            { "Right_shoulder_rotation", new[]{ "rightupperarm","rightarm","upperarmright","upperarmr" } },
            { "Left_elbow", new[]{ "leftforearm","leftlowerarm","lowerarmleft","forearmleft","forearml","loarml" } },
            { "Right_elbow", new[]{ "rightforearm","rightlowerarm","lowerarmright","forearmright","forearmr" } },
            { "Left_wrist", new[]{ "lefthand","handleft","leftwrist","wristleft","handl","wristl" } },
            { "Right_wrist", new[]{ "righthand","handright","rightwrist","wristright","handr","wristr" } },
            { "Left_hip", new[]{ "leftupleg","leftupperleg","upperlegleft","leftthigh","thighleft","upperlegl","thighl","lthigh" } },
            { "Right_hip", new[]{ "rightupleg","rightupperleg","upperlegright","rightthigh","thighright","upperlegr","thighr" } },
            { "Left_knee", new[]{ "leftlowerleg","lowerlegleft","leftcalf","calfleft","leftshin","shinleft","leftleg","lowerlegl","calfl","shinl" } },
            { "Right_knee", new[]{ "rightlowerleg","lowerlegright","rightcalf","calfright","rightshin","shinright","rightleg","lowerlegr" } },
            { "Left_ankle", new[]{ "leftfoot","footleft","leftankle","ankleleft","footl","anklel" } },
            { "Right_ankle", new[]{ "rightfoot","footright","rightankle","ankleright","footr","ankler" } },
            { "Left_toe", new[]{ "lefttoebase","lefttoes","lefttoe","toebaseleft","toeleft","ltoe","balll" } },
            { "Right_toe", new[]{ "righttoebase","righttoes","righttoe","toebaseright","toeright","rtoe","ballr" } },
        };

        // Move biomechanical joint -> Unity Humanoid bone. Drives any humanoid rig regardless of bone names.
        static readonly Dictionary<string, HumanBodyBones> MoveToHumanBone = new Dictionary<string, HumanBodyBones>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Root", HumanBodyBones.Hips },
            { "Left_hip", HumanBodyBones.LeftUpperLeg }, { "Left_knee", HumanBodyBones.LeftLowerLeg }, { "Left_ankle", HumanBodyBones.LeftFoot }, { "Left_toe", HumanBodyBones.LeftToes },
            { "Right_hip", HumanBodyBones.RightUpperLeg }, { "Right_knee", HumanBodyBones.RightLowerLeg }, { "Right_ankle", HumanBodyBones.RightFoot }, { "Right_toe", HumanBodyBones.RightToes },
            { "Spine1", HumanBodyBones.Spine }, { "Spine2", HumanBodyBones.Chest }, { "Spine3", HumanBodyBones.UpperChest }, { "Neck", HumanBodyBones.Neck }, { "Head", HumanBodyBones.Head },
            { "Left_clavicle", HumanBodyBones.LeftShoulder }, { "Left_shoulder", HumanBodyBones.LeftUpperArm }, { "Left_shoulder_rotation", HumanBodyBones.LeftUpperArm },
            { "Left_elbow", HumanBodyBones.LeftLowerArm }, { "Left_wrist", HumanBodyBones.LeftHand },
            { "Right_clavicle", HumanBodyBones.RightShoulder }, { "Right_shoulder", HumanBodyBones.RightUpperArm }, { "Right_shoulder_rotation", HumanBodyBones.RightUpperArm },
            { "Right_elbow", HumanBodyBones.RightLowerArm }, { "Right_wrist", HumanBodyBones.RightHand },
        };

        /// <summary>Cache pelvis/spine/hip indices and the primary mapped child per joint for the swing solver.</summary>
        void BuildRetargetTopology()
        {
            primaryChild.Clear();
            var pose = asset.pose;
            rootJointIndex = Mathf.Max(0, pose.IndexOfJoint("Root"));
            spineJointIndex = pose.IndexOfJoint("Spine1");
            if (spineJointIndex < 0) spineJointIndex = pose.IndexOfJoint("Spine");
            lHipJointIndex = pose.IndexOfJoint("Left_hip");
            rHipJointIndex = pose.IndexOfJoint("Right_hip");

            for (int j = 0; j < pose.jointNames.Count; j++)
            {
                int best = -1;
                bool bestPreferred = false;
                for (int c = 0; c < pose.jointParents.Count; c++)
                {
                    if (pose.jointParents[c] != j) continue;
                    if (!boneByJoint.ContainsKey(c)) continue;
                    // Prefer the spine/neck/head continuation so the torso aims up, not into an arm.
                    string cn = pose.jointNames[c];
                    bool preferred = cn.IndexOf("Spine", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     cn.IndexOf("Neck", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     cn.IndexOf("Head", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (best < 0 || (preferred && !bestPreferred))
                    {
                        best = c;
                        bestPreferred = preferred;
                    }
                }
                primaryChild[j] = best;
            }
        }

        static Transform FindBone(Dictionary<string, Transform> byName, string boneName)
        {
            if (byName.TryGetValue(boneName, out var exact)) return exact;
            // Suffix match (e.g. "mixamorig:Hips" vs "Hips").
            foreach (var kv in byName)
            {
                if (kv.Key.EndsWith(boneName, System.StringComparison.OrdinalIgnoreCase) ||
                    boneName.EndsWith(kv.Key, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        void SetCharacterVisible(bool visible)
        {
            if (characterRoot != null && characterRoot.gameObject.activeSelf != visible)
                characterRoot.gameObject.SetActive(visible);
        }
    }
}
