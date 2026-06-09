using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.Rendering.Universal;
using BodyTracking.Data;
using BodyTracking.Recording;
using BodyTracking.Playback;
using BodyTracking.Storage;
using BodyTracking.AR;
using BodyTracking.Spatial;
using BodyTracking.MoveAI;
using BodyTracking.Animation;
using BodyTracking.Diagnostics;

namespace BodyTracking
{
    /// <summary>
    /// Main controller for hip tracking recording and playback system
    /// Provides a clean interface and coordinates all modular components
    /// </summary>
    public class BodyTrackingController : MonoBehaviour
    {
        [Header("Dependencies")]
        public ARHumanBodyManager humanBodyManager;
        public ARImageTargetManager imageTargetManager;
        public ARWorldMapPersistence worldMapPersistence;

        [Header("Spatial (RouteRoot)")]
        [Tooltip("Selects the active RouteRoot provider (Immersal primary, image target fallback). Auto-created if missing.")]
        public RouteRootManager routeRootManager;
        
        [Header("Components")]
        public BodyTrackingRecorder recorder;
        public BodyTrackingPlayer player;

        [Header("Move AI fusion (optional)")]
        [Tooltip("Coordinates the Move AI fusion path. When assigned, recordings are submitted for fusion on save and fused replays are preferred at playback.")]
        public MoveAIFusionCoordinator fusionCoordinator;
        [Tooltip("File system path of the mp4 captured alongside the last recording (set by the video capture integration). Used to submit the climb to Move AI.")]
        public string pairedVideoFilePath;
        [Tooltip("Optional video recorder driven alongside body recording so each climb has a paired mp4 for Move AI.")]
        public VideoRecorder videoRecorder;
        [Tooltip("Record mp4 whenever Video Recorder is assigned (does not require Move AI coordinator).")]
        public bool captureVideoWithRecording = true;

        [Header("Recording review")]
        [Tooltip("After stopping a recording, replay the captured skeleton for review and wait for an explicit Confirm before sending it to Move AI. Reject discards the recording (and its paired video).")]
        public bool requireReviewBeforeSubmit = true;

        [Header("Recording start")]
        [Tooltip("When recording, wait until ARKit detects a body before capture begins, so the joint recording and the paired video both start with the climber already in frame.")]
        public bool waitForBodyBeforeRecording = true;
        [Tooltip("Require the body to be tracked this many consecutive frames before starting (debounces detection flicker).")]
        public int bodyDetectConsecutiveFrames = 3;
        [Tooltip("Start recording anyway if no body is detected within this many seconds (0 = wait until the user taps stop to cancel).")]
        public float waitForBodyTimeoutSeconds = 25f;
        [Tooltip("End each recording this many seconds before the last tracked body frame (mirrors the start trim). Also drops any untracked tail after the climber steps out of frame to tap stop. Set negative to disable.")]
        public float trimEndBodyBufferSeconds = 1f;
        
        [Header("Settings")]
        public bool autoInitialize = true;
        [Tooltip("On app restart, automatically load the newest valid recording so replay works as soon as the image target is detected again.")]
        public bool autoLoadLatestRecordingOnInitialize = true;
        [Tooltip("Save an ARWorldMap with each successful recording (iOS/ARKit only).")]
        public bool saveWorldMapWithRecording = true;
        [Tooltip("When a recording is loaded, also load its ARWorldMap if present.")]
        public bool autoLoadWorldMapForLoadedRecording = true;
        [Tooltip("After a world map has relocalized, playback uses the saved reference pose anchor instead of the live image target so joints stay aligned when the marker is detected.")]
        public bool preferWorldMapAnchorOverImageTargetForPlayback = true;
        
        [Header("AR occlusion")]
        [Tooltip("Occlusion is off while idle/recording (ARKit body tracking) and on during playback only.")]
        public ARCharacterOcclusion characterOcclusion;

        [Tooltip("AR plane detection is only consumed during playback (floor contact shadow + penetration floor). " +
                 "Gated on for playback and off otherwise so idle/record camera mode doesn't run continuous plane " +
                 "detection. Auto-found if left empty.")]
        public ARPlaneManager planeManager;

        [Header("Idle render / CV cost")]
        [Tooltip("Run full-screen post-processing (incl. SMAA) on the AR camera only during playback. Idle/record " +
                 "show just the camera feed + UI, where antialiasing/grading does nothing, so this skips several " +
                 "full-screen passes per frame. Auto-finds the AR camera's URP data.")]
        public bool postProcessingDuringPlaybackOnly = true;
        [Tooltip("AR camera's URP additional-camera data. Auto-found from the AR camera if left empty.")]
        public UniversalAdditionalCameraData cameraData;
        [Tooltip("Disable ARTrackedImageManager once Immersal has locked its room anchor (and we're not playing " +
                 "back a marker/legacy recording). With the default Immersal route-frame policy the marker is never " +
                 "the frame, so image tracking is redundant CV/heat once localized. Off = always track the marker.")]
        public bool gateImageTrackingWhenImmersalLocked = true;
        [Tooltip("ARTrackedImageManager driving marker detection. Auto-found (via Globals) if left empty.")]
        public ARTrackedImageManager trackedImageManager;

        [Header("UI")]
        public TMPro.TextMeshProUGUI statusText;
        
        // State
        private bool isInitialized = false;
        private OperationMode currentMode = OperationMode.Ready;
        private bool isPaused;
        private HipRecording lastRecording;
        private string lastRecordingFileName;
        private Coroutine armingCoroutine;

        // Recording review state: a just-finished recording is held here until the user confirms (submit to
        // Move AI) or rejects (discard). The paired video may still be encoding when review opens, so we track
        // its readiness and submit as soon as it's available once confirmed.
        private bool isAwaitingReview;
        private HipRecording reviewRecording;
        private string reviewFileName;
        private string reviewVideoPath;
        private bool reviewVideoReady;
        private bool reviewVideoExpected;
        private bool confirmPendingAfterVideo;
        private bool rejectPendingAfterVideo;

        // Multi-recording overlap engine: drives one extra character per additional enabled recording, locked
        // to this controller's primary playhead. Created on demand so scenes don't need manual wiring.
        private MultiRecordingPlayback multiPlayback;
        private CharacterSwitcher characterSwitcher;
        
        // Events
        public event System.Action<OperationMode> OnModeChanged;
        public event System.Action<HipRecording> OnRecordingComplete;
        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;

        /// <summary>Fired when a finished recording enters review (awaiting confirm/reject).</summary>
        public event System.Action OnReviewStarted;
        /// <summary>Fired when a review is resolved (confirmed or rejected).</summary>
        public event System.Action OnReviewResolved;
        
        // Public Properties
        public bool IsInitialized => isInitialized;
        public OperationMode CurrentMode => currentMode;
        public bool CanRecord => isInitialized && IsLocalized && currentMode == OperationMode.Ready;
        public bool CanPlayback => isInitialized && IsLocalized && lastRecording != null && lastRecording.IsValid && currentMode == OperationMode.Ready;
        public bool IsRecording => currentMode == OperationMode.Recording;
        public bool IsPlaying => currentMode == OperationMode.Playing;
        /// <summary>True while playback is loaded but paused in place (timeline frozen, not reset).</summary>
        public bool IsPaused => currentMode == OperationMode.Playing && isPaused;
        /// <summary>True after Record is tapped while waiting for ARKit to detect a body (capture not started yet).</summary>
        public bool IsWaitingForBody => currentMode == OperationMode.WaitingForBody;
        /// <summary>True while a finished recording is being reviewed (awaiting Confirm or Reject).</summary>
        public bool IsAwaitingReview => isAwaitingReview;
        public string LastRecordingFileName => lastRecordingFileName;
        public string WorldMapStatus => worldMapPersistence != null ? worldMapPersistence.LastStatusMessage : "WorldMap service unavailable";

        /// <summary>True when the active RouteRoot provider has a usable, wall-aligned pose.</summary>
        public bool IsLocalized => routeRootManager != null && routeRootManager.IsLocalized;

        /// <summary>
        /// True once the Immersal-backed RouteRoot anchor is locked (frozen) to the ARKit frame — i.e. the world is
        /// pinned and it's safe/stable to record. For non-Immersal sources, treated as locked when localized.
        /// </summary>
        public bool IsAnchorLocked =>
            routeRootManager != null &&
            (routeRootManager.ImmersalProvider != null && routeRootManager.Source == SpatialSourceType.Immersal
                ? routeRootManager.ImmersalProvider.IsAnchorFrozen
                : IsLocalized);

        /// <summary>"Immersal", "ImageTarget" or "None" — which spatial system currently supplies RouteRoot.</summary>
        public string SpatialSourceLabel => routeRootManager != null ? routeRootManager.Source.ToString() : "None";

        /// <summary>Latest Move AI fusion pipeline message (shown on the dedicated UI status line).</summary>
        public string FusionStatusMessage { get; private set; } = "";

        /// <summary>True when Move AI coordinator is wired and has an API key.</summary>
        public bool MoveAIEnabled => fusionCoordinator != null && fusionCoordinator.IsConfigured;

        /// <summary>True when the last loaded/saved recording has a baked fusion asset on disk.</summary>
        public bool LastRecordingHasFusionAsset =>
            !string.IsNullOrEmpty(lastRecordingFileName) &&
            MoveAIFusionCoordinator.HasFusionAsset(lastRecordingFileName);

        /// <summary>True when there's a loaded recording with a cached fused asset that can be rebaked (no API).</summary>
        public bool CanRebake =>
            fusionCoordinator != null &&
            lastRecording != null && lastRecording.IsValid &&
            LastRecordingHasFusionAsset;

        /// <summary>
        /// Re-fuse the latest recording from the cached Move pose with the coordinator's current bake settings —
        /// no Move API call — and restart fused playback. No-op (returns false) when <see cref="CanRebake"/> is false.
        /// </summary>
        public bool RebakeLatest()
        {
            if (!CanRebake) return false;
            return fusionCoordinator.RebakeLatest(lastRecordingFileName, routeRootManager, lastRecording);
        }

        /// <summary>True when a fused Move AI replay is driving playback (instead of the dot-skeleton player).</summary>
        public bool IsFusedPlaying => fusionCoordinator != null && fusionCoordinator.IsFusedPlaying;
        public float FusedCurrentTime => fusionCoordinator != null ? fusionCoordinator.FusedCurrentTime : 0f;
        public float FusedDuration => fusionCoordinator != null ? fusionCoordinator.FusedDuration : 0f;

        public event Action<string> OnFusionStatusChanged;

        // Idle/playback render cap (fps). An AR passthrough + character app gains little from higher rates while
        // paying a large heat/battery cost, so we cap to this WHEN body tracking isn't active.
        // vSyncCount must be 0 or it overrides targetFrameRate (vSync is ignored on mobile, but this keeps the
        // editor and any desktop builds consistent).
        const int IdleFrameRate = 30;
        // Frame cap while ARKit body tracking is live (WaitingForBody/Recording). The 30 fps cap was throttling
        // how often we sample ARKit body poses and made tracking feel laggy/dropped, so we lift it during capture.
        // -1 = uncapped (run as fast as the device/AR session allows) for the most responsive body tracking.
        const int BodyTrackingFrameRate = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ApplyGlobalFrameCap()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = IdleFrameRate;
        }

        /// <summary>
        /// Body tracking samples ARKit poses once per render frame, so the global 30 fps cap also throttled
        /// tracking. Lift the cap while a body is being tracked (arming/recording) and restore the idle cap
        /// otherwise so heat/battery stay low when tracking isn't running.
        /// </summary>
        private void ApplyFrameCapForMode(OperationMode mode)
        {
            bool bodyTracking = mode == OperationMode.WaitingForBody || mode == OperationMode.Recording;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = bodyTracking ? BodyTrackingFrameRate : IdleFrameRate;
        }

        void Start()
        {
            // Re-assert in case something (e.g. AVPro capture teardown) changed it after launch.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = IdleFrameRate;

            if (autoInitialize)
            {
                Initialize();
            }
        }

        /// <summary>
        /// Initialize the hip tracking system
        /// </summary>
        public bool Initialize()
        {
            if (isInitialized)
            {
                Debug.LogWarning("[BodyTrackingController] Already initialized");
                return true;
            }
            
            // Validate dependencies
            if (!ValidateDependencies())
            {
                return false;
            }
            
            // Setup components
            SetupComponents();
            
            // Subscribe to events
            SubscribeToEvents();

            if (autoLoadLatestRecordingOnInitialize)
            {
                TryAutoLoadLatestRecording();
            }
            
            isInitialized = true;
            ApplyOcclusionForMode(currentMode);

            if (lastRecording != null && lastRecording.IsValid)
            {
                string hint = routeRootManager != null && routeRootManager.Source == SpatialSourceType.ImageTarget
                    ? "Point phone at Wall 1 marker to replay."
                    : "Detect image target to replay.";
                UpdateStatus($"Hip tracking initialized - loaded '{lastRecordingFileName}'. {hint}");
            }
            else
            {
                UpdateStatus("Hip tracking system initialized - waiting for image target");
            }
            
            Debug.Log("[BodyTrackingController] Hip tracking system successfully initialized");
            return true;
        }

        /// <summary>
        /// Start recording hip tracking data
        /// </summary>
        public bool StartRecording()
        {
            if (!CanRecord)
            {
                Debug.LogWarning($"[BodyTrackingController] Cannot start hip recording - CanRecord: {CanRecord}");
                UpdateStatus("Cannot start hip recording - check requirements");
                return false;
            }

            // Wait for ARKit to see a body before starting, so the joint recording and the paired video both begin
            // with the climber already in frame (and aligned in time). Tapping record again cancels the wait.
            if (waitForBodyBeforeRecording)
            {
                SetMode(OperationMode.WaitingForBody);
                UpdateStatus("Get into frame — waiting for body…");
                if (MoveAIEnabled && videoRecorder != null && captureVideoWithRecording)
                    SetFusionStatus("Waiting for body…");
                if (armingCoroutine != null)
                    StopCoroutine(armingCoroutine);
                armingCoroutine = StartCoroutine(WaitForBodyThenRecord());
                return true;
            }

            return BeginRecordingNow();
        }

        /// <summary>Start the hip recorder and the paired video together (same frame) and enter Recording mode.</summary>
        private bool BeginRecordingNow()
        {
            if (!recorder.StartRecording())
            {
                SetMode(OperationMode.Ready);
                return false;
            }

            // Capture a paired video for Move AI fusion (no-op if no recorder/coordinator configured).
            if (videoRecorder != null && captureVideoWithRecording)
            {
                if (!videoRecorder.gameObject.activeSelf)
                    videoRecorder.gameObject.SetActive(true);
                if (!videoRecorder.StartRecording())
                {
                    Debug.LogWarning("[BodyTrackingController] Video capture did not start (body recording continues)");
                }
            }

            SetMode(OperationMode.Recording);
            UpdateStatus("Recording hip position...");
            if (MoveAIEnabled && videoRecorder != null && captureVideoWithRecording)
                SetFusionStatus("Recording · Move AI runs after stop");
            Debug.Log("[BodyTrackingController] Started hip recording");
            return true;
        }

        /// <summary>Poll the recorder until ARKit reports a tracked body, then begin recording. Aborts if cancelled.</summary>
        private IEnumerator WaitForBodyThenRecord()
        {
            int stableFrames = 0;
            int needed = Mathf.Max(1, bodyDetectConsecutiveFrames);
            float elapsed = 0f;
            float lastStatusRefresh = -1f;

            while (currentMode == OperationMode.WaitingForBody)
            {
                if (recorder != null)
                    recorder.PollBodyDetection();

                bool bodyVisible = recorder != null && recorder.HasTrackedBody;
                stableFrames = bodyVisible ? stableFrames + 1 : 0;
                if (stableFrames >= needed)
                    break;

                elapsed += Time.unscaledDeltaTime;

                if (elapsed - lastStatusRefresh >= 0.5f)
                {
                    lastStatusRefresh = elapsed;
                    UpdateWaitingForBodyStatus(elapsed, bodyVisible);
                }

                if (waitForBodyTimeoutSeconds > 0f && elapsed >= waitForBodyTimeoutSeconds)
                {
                    Debug.LogWarning("[BodyTrackingController] No body detected before timeout — starting recording anyway.");
                    UpdateStatus("No body detected — recording anyway…");
                    break;
                }

                yield return null;
            }

            armingCoroutine = null;

            // Cancelled (mode changed out of WaitingForBody while we were waiting).
            if (currentMode != OperationMode.WaitingForBody)
                yield break;

            if (!BeginRecordingNow())
                UpdateStatus("Failed to start recording");
        }

        private void UpdateWaitingForBodyStatus(float elapsedSeconds, bool bodyVisibleNow)
        {
            int joints = recorder != null ? recorder.LastTrackedJointCount : 0;
            string source = recorder != null ? recorder.ActiveSourceName : "?";

            if (bodyVisibleNow)
            {
                UpdateStatus($"Body detected ({joints} joints via {source}) — starting…");
                return;
            }

            string hint = joints == 0
                ? "Stand in frame, full body visible; landscape works best on iPhone."
                : $"Get into frame… ({joints} joints, {source})";

            if (waitForBodyTimeoutSeconds > 0f)
                hint += $" · auto-start in {Mathf.Max(0f, waitForBodyTimeoutSeconds - elapsedSeconds):0}s";

            hint += " · tap stop to cancel";
            UpdateStatus(hint);
        }

        /// <summary>
        /// Remove frames from the start until the first valid hip, re-zero timestamps, and shorten duration.
        /// Stops "waiting for body" dead time from inflating clip length and Move AI processing window.
        /// </summary>
        private void TrimRecordingLeadingInvalid(HipRecording recording)
        {
            if (recording?.frames == null || recording.frames.Count == 0)
                return;

            int firstValid = -1;
            for (int i = 0; i < recording.frames.Count; i++)
            {
                if (recording.frames[i].hipJoint.IsValid)
                {
                    firstValid = i;
                    break;
                }
            }
            if (firstValid <= 0)
                return;

            int removed = firstValid;
            float leadingOffset = recording.frames[firstValid].timestamp;
            recording.frames.RemoveRange(0, removed);
            float t0 = recording.frames[0].timestamp;
            for (int i = 0; i < recording.frames.Count; i++)
            {
                var f = recording.frames[i];
                f.timestamp -= t0;
                recording.frames[i] = f;
            }
            recording.duration = recording.frames[recording.frames.Count - 1].timestamp;
            // Preserve the paired video's original clock so Move AI motion stays aligned after timestamp re-zeroing.
            recording.videoStartTimeOffset += leadingOffset;
            Debug.Log($"[BodyTrackingController] Trimmed first {removed} untracked frames ({leadingOffset:F2}s); " +
                      $"duration now {recording.duration:F2}s, video offset {recording.videoStartTimeOffset:F2}s.");
        }

        /// <summary>
        /// Remove frames after the last valid hip (minus <paramref name="bufferBeforeLastBodySeconds"/>), and shorten
        /// duration. Drops the out-of-frame tail between the last body sighting and the stop tap, mirroring
        /// <see cref="TrimRecordingLeadingInvalid"/>.
        /// </summary>
        private void TrimRecordingTrailingInvalid(HipRecording recording, float bufferBeforeLastBodySeconds)
        {
            if (recording?.frames == null || recording.frames.Count == 0 || bufferBeforeLastBodySeconds < 0f)
                return;

            int lastValid = -1;
            for (int i = recording.frames.Count - 1; i >= 0; i--)
            {
                if (recording.frames[i].hipJoint.IsValid)
                {
                    lastValid = i;
                    break;
                }
            }
            if (lastValid < 0)
                return;

            float lastValidTime = recording.frames[lastValid].timestamp;
            float cutoff = lastValidTime - bufferBeforeLastBodySeconds;
            if (cutoff <= 0f)
            {
                Debug.LogWarning($"[BodyTrackingController] Trailing body trim skipped — last body at {lastValidTime:F2}s, " +
                                 $"buffer {bufferBeforeLastBodySeconds:F1}s would leave nothing.");
                return;
            }

            int removed = recording.frames.RemoveAll(f => f.timestamp > cutoff);
            recording.duration = cutoff;
            Debug.Log($"[BodyTrackingController] Trimmed trailing untracked tail at {lastValidTime:F2}s " +
                      $"(cutoff {cutoff:F2}s, buffer {bufferBeforeLastBodySeconds:F1}s, {removed} frames removed); " +
                      $"duration now {cutoff:F2}s.");
        }

        /// <summary>Cancel a pending "waiting for body" arming state without recording.</summary>
        public void CancelArming()
        {
            if (currentMode != OperationMode.WaitingForBody)
                return;

            if (armingCoroutine != null)
            {
                StopCoroutine(armingCoroutine);
                armingCoroutine = null;
            }
            SetMode(OperationMode.Ready);
            UpdateStatus("Recording cancelled");
            if (MoveAIEnabled)
                SetFusionStatus("");
        }

        /// <summary>
        /// Stop recording and save the data
        /// </summary>
        public HipRecording StopRecording()
        {
            if (currentMode != OperationMode.Recording)
            {
                Debug.LogWarning("[BodyTrackingController] Not currently recording");
                return null;
            }
            
            lastRecording = recorder.StopRecording();

            // Drop leading/trailing frames with no body/hip so playback and Move AI match the in-frame window.
            if (lastRecording != null)
            {
                TrimRecordingLeadingInvalid(lastRecording);
                TrimRecordingTrailingInvalid(lastRecording, trimEndBodyBufferSeconds);
            }

            if (lastRecording != null)
            {
                if (!lastRecording.IsValid)
                {
                    Debug.LogWarning($"[BodyTrackingController] Recording has no valid hip frames: {lastRecording.FrameCount} total frames");
                    UpdateStatus("Recording stopped, but no tracked hip frames were captured");
                    StopPairedVideoCapture(lastRecording);
                    SetMode(OperationMode.Ready);
                    return lastRecording;
                }

                // Auto-save the recording
                string fileName = $"hip_recording_{System.DateTime.Now:yyyyMMdd_HHmmss}";
                if (RecordingStorage.SaveRecording(lastRecording, fileName))
                {
                    lastRecordingFileName = fileName;
                    Debug.Log($"[BodyTrackingController] Hip recording saved: {fileName}");
                    UpdateStatus($"Hip recording saved: {lastRecording.ValidFrameCount}/{lastRecording.FrameCount} valid frames");
                    if (saveWorldMapWithRecording && worldMapPersistence != null)
                    {
                        StartCoroutine(worldMapPersistence.SaveWorldMapForRecording(fileName, imageTargetManager != null ? imageTargetManager.targetImageName : "unknown"));
                    }
                }
                else
                {
                    Debug.LogError("[BodyTrackingController] Failed to save hip recording");
                    UpdateStatus("Hip recording completed but save failed");
                }

                OnRecordingComplete?.Invoke(lastRecording);

                // Hold the recording for review (replay the skeleton, then Confirm to submit or Reject to discard)
                // instead of sending it straight to Move AI. Confirm/Reject drive the actual submission/cleanup.
                if (requireReviewBeforeSubmit)
                {
                    SetMode(OperationMode.Ready);
                    BeginReview(lastRecording);
                    return lastRecording;
                }

                StopPairedVideoCapture(lastRecording);
            }
            
            SetMode(OperationMode.Ready);
            return lastRecording;
        }

        // ============================================================================================
        // RECORDING REVIEW (confirm / reject before Move AI submission)
        // ============================================================================================

        /// <summary>
        /// Enter review for a just-finished recording: stop the paired video (without submitting), force the
        /// recorded skeleton visible, and auto-replay it so the user can judge quality before confirming.
        /// </summary>
        private void BeginReview(HipRecording recording)
        {
            isAwaitingReview = true;
            reviewRecording = recording;
            reviewFileName = lastRecordingFileName;
            reviewVideoPath = null;
            reviewVideoReady = false;
            confirmPendingAfterVideo = false;
            rejectPendingAfterVideo = false;
            reviewVideoExpected = videoRecorder != null && videoRecorder.IsRecording;

            if (videoRecorder != null && videoRecorder.IsRecording)
            {
                UpdateStatus("Finalizing video… review the skeleton, then confirm or reject");
                if (MoveAIEnabled)
                    SetFusionStatus("Encoding video… review then confirm");
                videoRecorder.StopRecording(OnReviewVideoReady);
            }
            else
            {
                reviewVideoPath = videoRecorder != null ? VideoRecorder.NormalizeLocalPath(videoRecorder.LastFilePath) : null;
                reviewVideoReady = true;
                if (MoveAIEnabled)
                    SetFusionStatus("Review the skeleton, then confirm to send to Move AI");
            }

            OnReviewStarted?.Invoke();
            StartReviewPlayback();
        }

        /// <summary>Force the recorded dot/line skeleton visible and replay the recording in a loop for review.</summary>
        public void StartReviewPlayback()
        {
            if (!isAwaitingReview || reviewRecording == null)
                return;

            if (player != null)
                player.SetSkeletonVisible(true);

            if (!IsPlaying)
                StartPlayback(reviewRecording);
        }

        private void OnReviewVideoReady(string videoPath)
        {
            reviewVideoPath = VideoRecorder.NormalizeLocalPath(videoPath);
            reviewVideoReady = true;

            // The user may have already resolved the review while the mp4 was still encoding.
            if (confirmPendingAfterVideo)
            {
                confirmPendingAfterVideo = false;
                SubmitReviewedRecording();
            }
            else if (rejectPendingAfterVideo)
            {
                rejectPendingAfterVideo = false;
                TryDeleteVideoFile(reviewVideoPath);
                reviewVideoPath = null;
            }
        }

        /// <summary>Keep the reviewed recording: send it (and its paired video) to Move AI, then exit review.</summary>
        public void ConfirmRecording()
        {
            if (!isAwaitingReview)
                return;

            if (IsPlaying)
                StopPlayback();

            isAwaitingReview = false;
            RestoreSkeletonVisibilityAfterReview();

            if (!reviewVideoReady && reviewVideoExpected)
            {
                // mp4 still encoding — submit automatically once OnReviewVideoReady fires.
                confirmPendingAfterVideo = true;
                if (MoveAIEnabled)
                    SetFusionStatus("Finishing video, will send to Move AI…");
            }
            else
            {
                SubmitReviewedRecording();
            }

            OnReviewResolved?.Invoke();
        }

        private void SubmitReviewedRecording()
        {
            if (reviewRecording == null || string.IsNullOrEmpty(reviewFileName))
                return;

            lastRecording = reviewRecording;
            lastRecordingFileName = reviewFileName;
            pairedVideoFilePath = reviewVideoPath;

            if (!string.IsNullOrEmpty(lastRecordingFileName))
                TrySubmitMoveFusion(lastRecording);
        }

        /// <summary>Discard the reviewed recording: delete its JSON + paired video and reload the previous clip.</summary>
        public void RejectRecording()
        {
            if (!isAwaitingReview)
                return;

            if (IsPlaying)
                StopPlayback();

            isAwaitingReview = false;
            confirmPendingAfterVideo = false;
            RestoreSkeletonVisibilityAfterReview();

            // Discard the paired video. It may still be encoding (BeginReview already requested the stop with the
            // OnReviewVideoReady callback) — in that case defer the delete until the file is written.
            if (videoRecorder != null && videoRecorder.IsRecording)
                videoRecorder.StopRecording(p => TryDeleteVideoFile(VideoRecorder.NormalizeLocalPath(p)));
            else if (reviewVideoReady && !string.IsNullOrEmpty(reviewVideoPath))
                TryDeleteVideoFile(reviewVideoPath);
            else if (reviewVideoExpected)
                rejectPendingAfterVideo = true;

            // Discard the saved hip recording JSON.
            if (!string.IsNullOrEmpty(reviewFileName))
                RecordingStorage.DeleteRecording(reviewFileName);

            reviewRecording = null;
            reviewFileName = null;
            reviewVideoPath = null;
            pairedVideoFilePath = null;
            lastRecording = null;
            lastRecordingFileName = null;

            // Fall back to the previous newest recording (if any) so playback still has something to show.
            TryAutoLoadLatestRecording();

            UpdateStatus("Recording discarded");
            if (MoveAIEnabled)
                SetFusionStatus("Recording discarded");

            OnRecordingComplete?.Invoke(lastRecording);
            OnReviewResolved?.Invoke();
        }

        private void RestoreSkeletonVisibilityAfterReview()
        {
            if (player == null)
                return;

            var debugVisuals = FindFirstObjectByType<BodyTracking.UI.DebugVisualsController>(FindObjectsInactive.Include);
            player.SetSkeletonVisible(debugVisuals != null && debugVisuals.VisualsVisible);
        }

        private static void TryDeleteVideoFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                path = VideoRecorder.NormalizeLocalPath(path);
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                    Debug.Log($"[BodyTrackingController] Deleted discarded paired video: {path}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BodyTrackingController] Failed to delete discarded video '{path}': {e.Message}");
            }
        }

        /// <summary>
        /// Start playback of the last recorded data
        /// </summary>
        public bool StartPlayback()
        {
            return StartPlayback(lastRecording);
        }

        /// <summary>
        /// Start playback of specific recording
        /// </summary>
        public bool StartPlayback(HipRecording recording)
        {
            if (!CanPlayback)
            {
                Debug.LogWarning($"[BodyTrackingController] Cannot start hip playback - CanPlayback: {CanPlayback}");
                UpdateStatus("Cannot start hip playback - check requirements");
                return false;
            }
            
            if (recording == null)
            {
                Debug.LogWarning("[BodyTrackingController] No hip recording provided for playback");
                UpdateStatus("No hip recording available for playback");
                return false;
            }

            // Immersal-sourced recordings replay directly under the live RouteRoot. Legacy/image-target
            // recordings are routed through the image-target fallback provider (world-map anchor or marker).
            ConfigurePlaybackProvider(recording);

            // The fusion asset is keyed by lastRecordingFileName, so only use it for that exact loaded recording.
            bool fusedRecordingMatches = ReferenceEquals(recording, lastRecording);

            // Prefer a baked Move AI fused replay when one exists for this recording; otherwise fall back to
            // the dot/line skeleton player below.
            if (fusionCoordinator != null &&
                fusedRecordingMatches &&
                !string.IsNullOrEmpty(lastRecordingFileName) &&
                MoveAIFusionCoordinator.HasFusionAsset(lastRecordingFileName) &&
                fusionCoordinator.TryStartFusedPlayback(lastRecordingFileName, routeRootManager, recording))
            {
                isPaused = false;
                SetMode(OperationMode.Playing);
                UpdateStatus($"Compare overlay: ARKit (cyan) + Move (orange) at recorded position");
                Debug.Log("[BodyTrackingController] Started fused Move AI playback");
                ApplyPlaybackSpeed();
                ApplyPlaybackLoopSettings();
                SyncOverlaysFromSelection();
                return true;
            }

            if (player.LoadRecording(recording))
            {
                player.StartPlayback();
                isPaused = false;
                SetMode(OperationMode.Playing);
                UpdateStatus($"Playing back hip movement: {recording.FrameCount} frames");
                Debug.Log("[BodyTrackingController] Started hip playback");
                ApplyPlaybackSpeed();
                ApplyPlaybackLoopSettings();
                SyncOverlaysFromSelection();
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Stop current playback
        /// </summary>
        public void StopPlayback()
        {
            if (currentMode != OperationMode.Playing)
            {
                Debug.LogWarning("[BodyTrackingController] Not currently playing");
                return;
            }
            
            player.StopPlayback();
            if (fusionCoordinator != null) fusionCoordinator.StopFusedPlayback();
            if (multiPlayback != null) multiPlayback.ClearOverlays();
            isPaused = false;
            SetMode(OperationMode.Ready);
            UpdateStatus("Hip playback stopped");
            Debug.Log("[BodyTrackingController] Stopped hip playback");
        }

        /// <summary>
        /// Pause playback in place. Unlike <see cref="StopPlayback"/>, this freezes the timeline at the current
        /// position instead of resetting it to the beginning, so playback can be resumed where it left off.
        /// </summary>
        public void PausePlayback()
        {
            if (currentMode != OperationMode.Playing || isPaused) return;

            if (IsFusedPlaying)
                fusionCoordinator.PauseFusedPlayback();
            else
                player.PausePlayback();

            isPaused = true;
            UpdateStatus("Hip playback paused");
        }

        /// <summary>Resume playback from where it was paused.</summary>
        public void ResumePlayback()
        {
            if (currentMode != OperationMode.Playing || !isPaused) return;

            if (IsFusedPlaying)
                fusionCoordinator.ResumeFusedPlayback();
            else
                player.ResumePlayback();

            isPaused = false;
            UpdateStatus("Hip playback resumed");
        }

        /// <summary>Seek the active playback to a normalized position (0..1) along the timeline.</summary>
        public void SeekPlaybackNormalized(float normalized)
        {
            if (currentMode != OperationMode.Playing) return;
            normalized = Mathf.Clamp01(normalized);

            if (IsFusedPlaying)
                fusionCoordinator.SeekFusedPlayback(normalized * FusedDuration);
            else if (player != null)
                player.SeekToTime(normalized * player.Duration);
        }

        // --- Playback screen controls (speed, loop, checkpoints, frame step) ---

        public const int PlaybackCheckpointCount = 5;
        private static readonly float[] PlaybackSpeedSteps = { 0.5f, 1f, 1.5f, 2f };
        private int playbackSpeedIndex = 1;
        private PlaybackLoopMode playbackLoopMode = PlaybackLoopMode.Full;
        private int activeCheckpointIndex = -1;

        public PlaybackLoopMode PlaybackLoopMode => playbackLoopMode;
        public int ActiveCheckpointIndex => activeCheckpointIndex;
        public float PlaybackSpeed => PlaybackSpeedSteps[Mathf.Clamp(playbackSpeedIndex, 0, PlaybackSpeedSteps.Length - 1)];

        /// <summary>Duration of the loaded/active recording (0 when none).</summary>
        public float PlaybackDuration
        {
            get
            {
                if (IsPlaying && IsFusedPlaying) return FusedDuration;
                if (IsPlaying && player != null) return player.Duration;
                return lastRecording != null ? lastRecording.duration : 0f;
            }
        }

        /// <summary>Current playback time in seconds.</summary>
        public float PlaybackCurrentTime
        {
            get
            {
                if (IsPlaying && IsFusedPlaying) return FusedCurrentTime;
                if (IsPlaying && player != null) return player.CurrentTime;
                return 0f;
            }
        }

        /// <summary>Normalized playback progress (0..1).</summary>
        public float PlaybackNormalizedProgress
        {
            get
            {
                float d = PlaybackDuration;
                return d > 0f ? Mathf.Clamp01(PlaybackCurrentTime / d) : 0f;
            }
        }

        /// <summary>Time in seconds for checkpoint index (0..PlaybackCheckpointCount-1), marking move start.</summary>
        public float GetCheckpointTime(int index)
        {
            float duration = PlaybackDuration;
            if (duration <= 0f) return 0f;
            index = Mathf.Clamp(index, 0, PlaybackCheckpointCount - 1);
            return duration * index / PlaybackCheckpointCount;
        }

        /// <summary>End time for the move segment beginning at <paramref name="index"/>.</summary>
        public float GetCheckpointSegmentEnd(int index)
        {
            float duration = PlaybackDuration;
            if (duration <= 0f) return 0f;
            index = Mathf.Clamp(index, 0, PlaybackCheckpointCount - 1);
            return index < PlaybackCheckpointCount - 1
                ? duration * (index + 1) / PlaybackCheckpointCount
                : duration;
        }

        /// <summary>Cycle playback speed: 0.5 → 1.0 → 1.5 → 2.0.</summary>
        public float CyclePlaybackSpeed()
        {
            playbackSpeedIndex = (playbackSpeedIndex + 1) % PlaybackSpeedSteps.Length;
            ApplyPlaybackSpeed();
            return PlaybackSpeed;
        }

        /// <summary>Toggle loop mode between full recording and the active move segment.</summary>
        public PlaybackLoopMode CyclePlaybackLoopMode()
        {
            playbackLoopMode = playbackLoopMode == PlaybackLoopMode.Full
                ? PlaybackLoopMode.Segment
                : PlaybackLoopMode.Full;
            ApplyPlaybackLoopSettings();
            return playbackLoopMode;
        }

        /// <summary>Seek to a checkpoint, mark it active, and resume playback from that time.</summary>
        public bool JumpToCheckpoint(int index)
        {
            if (lastRecording == null || !lastRecording.IsValid)
            {
                LoadLatestRecording();
                if (lastRecording == null || !lastRecording.IsValid)
                    return false;
            }

            index = Mathf.Clamp(index, 0, PlaybackCheckpointCount - 1);
            activeCheckpointIndex = index;
            float time = GetCheckpointTime(index);
            return PlayFromTime(time);
        }

        /// <summary>Start (or continue) playback from an absolute time in seconds.</summary>
        public bool PlayFromTime(float time)
        {
            if (!IsPlaying)
            {
                if (!CanPlayback)
                {
                    LoadLatestRecording();
                    if (!CanPlayback)
                        return false;
                }

                if (!StartPlayback())
                    return false;
            }

            SeekPlaybackTime(time);
            ApplyPlaybackLoopSettings();

            if (IsPaused)
                ResumePlayback();

            return true;
        }

        /// <summary>Seek to an absolute time while playing (no-op when idle).</summary>
        public void SeekPlaybackTime(float time)
        {
            if (currentMode != OperationMode.Playing) return;
            float duration = PlaybackDuration;
            time = duration > 0f ? Mathf.Clamp(time, 0f, duration) : Mathf.Max(0f, time);

            if (IsFusedPlaying)
                fusionCoordinator.SeekFusedPlayback(time);
            else if (player != null)
                player.SeekToTime(time);
        }

        /// <summary>Step forward/back by two frames on the active timeline.</summary>
        public void StepPlaybackTwoFrames(int direction)
        {
            if (direction == 0) return;

            if (!IsPlaying)
            {
                LoadLatestRecording();
                if (!StartPlayback())
                    return;
                PausePlayback();
            }

            int delta = direction > 0 ? 2 : -2;
            if (IsFusedPlaying)
                fusionCoordinator.StepFusedFrames(delta);
            else if (player != null)
                player.StepFrames(delta);
        }

        private void ApplyPlaybackSpeed()
        {
            float speed = PlaybackSpeed;
            if (player != null) player.PlaybackSpeed = speed;
            if (fusionCoordinator != null) fusionCoordinator.FusedPlaybackSpeed = speed;
        }

        private void ApplyPlaybackLoopSettings()
        {
            bool segment = playbackLoopMode == PlaybackLoopMode.Segment && activeCheckpointIndex >= 0;
            float duration = PlaybackDuration;
            float segStart = 0f;
            float segEnd = duration;

            if (segment && duration > 0f)
            {
                segStart = GetCheckpointTime(activeCheckpointIndex);
                segEnd = GetCheckpointSegmentEnd(activeCheckpointIndex);
            }

            if (player != null)
            {
                player.LoopPlayback = playbackLoopMode == PlaybackLoopMode.Full;
                player.SetSegmentLoop(segStart, segEnd, segment);
            }

            if (fusionCoordinator != null)
            {
                fusionCoordinator.FusedLoopPlayback = playbackLoopMode == PlaybackLoopMode.Full;
                fusionCoordinator.SetFusedSegmentLoop(segStart, segEnd, segment);
            }
        }

        /// <summary>
        /// Reload the recording playback should use: the Recordings menu selection's primary when one is
        /// enabled, otherwise the most recent clip for the active map.
        /// </summary>
        public void LoadLatestRecording()
        {
            var sel = RecordingSelection.Instance;
            string primary = sel != null ? sel.PrimaryFileName : null;
            if (!string.IsNullOrEmpty(primary))
            {
                if (primary != lastRecordingFileName)
                    LoadRecording(primary, loadAssociatedWorldMap: false);
                return;
            }
            TryAutoLoadLatestRecording();
        }

        /// <summary>
        /// Load a recording from storage
        /// </summary>
        public bool LoadRecording(string fileName, bool loadAssociatedWorldMap = true)
        {
            using var _ = PerfSampler.Scope("Controller.LoadRecording");
            if (!string.IsNullOrEmpty(fileName) &&
                fileName == lastRecordingFileName &&
                lastRecording != null &&
                lastRecording.IsValid)
            {
                Debug.Log($"[BodyTrackingController] Reusing already loaded recording '{fileName}'");
                return true;
            }

            var recording = RecordingStorage.LoadRecording(fileName);
            if (recording != null && recording.IsValid)
            {
                lastRecording = recording;
                lastRecordingFileName = fileName;
                UpdateStatus($"Loaded hip recording: {recording.ValidFrameCount}/{recording.FrameCount} valid frames");
                Debug.Log($"[BodyTrackingController] Loaded '{fileName}' ({recording.ValidFrameCount}/{recording.FrameCount} valid, {recording.duration:F1}s)");
                if (fusionCoordinator != null)
                    fusionCoordinator.WarmGlb(fileName);
                if (loadAssociatedWorldMap && autoLoadWorldMapForLoadedRecording && worldMapPersistence != null)
                {
                    if (worldMapPersistence.IsWorldMapSupported() && worldMapPersistence.HasWorldMap(fileName))
                    {
                        StartCoroutine(worldMapPersistence.LoadWorldMapForRecording(fileName));
                    }
                    else if (!worldMapPersistence.IsWorldMapSupported())
                    {
                        UpdateStatus($"Loaded '{fileName}' — world map unavailable (AR Remote/Editor). Replay uses live image target only.");
                    }
                    else
                    {
                        UpdateStatus($"Loaded '{fileName}' — no world map file yet. Record again on device, or replay via live image target.");
                    }
                }
                return true;
            }
            
            UpdateStatus($"Failed to load valid hip recording: {fileName}");
            return false;
        }

        /// <summary>
        /// Get list of available recordings
        /// </summary>
        public System.Collections.Generic.List<string> GetAvailableRecordings()
        {
            return RecordingStorage.GetAvailableRecordings(mapId: GetActiveMapId());
        }

        /// <summary>Immersal map id of the active RouteRoot (empty when unknown).</summary>
        public string GetActiveMapId()
        {
            return routeRootManager != null ? routeRootManager.MapId : "";
        }

        // ============================================================================================
        // MULTI-RECORDING SELECTION + OVERLAP
        // ============================================================================================

        /// <summary>Number of overlaid (non-primary) recordings currently rendered.</summary>
        public int OverlayRecordingCount => multiPlayback != null ? multiPlayback.OverlayCount : 0;

        /// <summary>
        /// Re-sync every overlaid character to the primary's current correction/look settings. Call this after
        /// live tuning edits so all characters playing at once share one consistent set of settings.
        /// </summary>
        public void ApplyTuningToOverlays()
        {
            if (multiPlayback != null)
                multiPlayback.ApplyPrimarySettingsToOverlays();
        }

        private MultiRecordingPlayback EnsureMultiPlayback()
        {
            if (multiPlayback == null)
            {
                multiPlayback = FindFirstObjectByType<MultiRecordingPlayback>(FindObjectsInactive.Include);
                if (multiPlayback == null)
                    multiPlayback = gameObject.AddComponent<MultiRecordingPlayback>();
                multiPlayback.Configure(this, fusionCoordinator, CharacterSwitcherRef());
            }
            return multiPlayback;
        }

        private CharacterSwitcher CharacterSwitcherRef()
        {
            if (characterSwitcher == null)
                characterSwitcher = FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            return characterSwitcher;
        }

        /// <summary>
        /// Apply the primary recording's chosen GLB character to the main <see cref="CharacterSwitcher"/> (which
        /// drives the primary fused player). No-op when the recording uses the default (-1).
        /// </summary>
        private void ApplyPrimaryCharacter()
        {
            var sel = RecordingSelection.Instance;
            string primary = sel.PrimaryFileName;
            if (string.IsNullOrEmpty(primary)) return;

            int index = sel.GetCharacterIndex(primary);
            if (index < 0) return;

            var switcher = CharacterSwitcherRef();
            if (switcher != null && switcher.CurrentIndex != index)
                switcher.SelectCharacter(index);
        }

        /// <summary>
        /// Rebuild overlay characters from the <see cref="RecordingSelection"/> model. Overlays are only shown
        /// when the selection's primary recording is the one currently loaded/playing.
        /// </summary>
        private void SyncOverlaysFromSelection()
        {
            var engine = EnsureMultiPlayback();
            var sel = RecordingSelection.Instance;
            string primary = sel.PrimaryFileName;

            ApplyPrimaryCharacter();

            if (string.IsNullOrEmpty(primary) || primary != lastRecordingFileName)
                engine.ClearOverlays();
            else
                engine.SyncOverlays(sel.OverlayFileNames, routeRootManager);
        }

        /// <summary>
        /// Apply the current <see cref="RecordingSelection"/> to playback: load the primary recording, restart
        /// the primary path when it changed, (re)build overlay characters, and optionally seek to a timestamp
        /// (clamped to each clip's last frame). Called by the Recordings menu and the cycle button.
        /// </summary>
        public void ApplyRecordingSelection(float seekTime = -1f)
        {
            using var _ = PerfSampler.Scope("Controller.ApplySelection");
            EnsureMultiPlayback();
            var sel = RecordingSelection.Instance;
            string primary = sel.PrimaryFileName;

            if (string.IsNullOrEmpty(primary))
            {
                multiPlayback.ClearOverlays();
                return;
            }

            bool wasPlaying = IsPlaying;
            float resume = seekTime >= 0f ? seekTime : (wasPlaying ? PlaybackCurrentTime : -1f);
            bool primaryChanged = primary != lastRecordingFileName;

            if (primaryChanged)
                LoadRecording(primary, loadAssociatedWorldMap: false);

            if (wasPlaying)
            {
                if (primaryChanged)
                {
                    // Rebind the primary path to the new recording, then overlays rebuild via StartPlayback.
                    StopPlayback();
                    StartPlayback();
                }
                else
                {
                    SyncOverlaysFromSelection();
                }

                if (resume >= 0f)
                    SeekPlaybackTime(resume);
            }
            // When idle the primary is now loaded; overlays build once playback starts.
        }

        /// <summary>
        /// Cycle the single active recording to the next one for this map (turning all others off) and seek it
        /// to the current playhead. If the new clip is shorter, it holds on its last frame.
        /// </summary>
        public void CycleRecording()
        {
            var sel = RecordingSelection.Instance;
            if (sel.Entries.Count == 0)
                sel.Refresh(GetActiveMapId());

            float t = IsPlaying ? PlaybackCurrentTime : 0f;
            string next = sel.CycleSingle();
            if (string.IsNullOrEmpty(next))
            {
                UpdateStatus("No recordings to cycle for this map");
                return;
            }

            ApplyRecordingSelection(t);
            UpdateStatus($"Recording: {next}");
        }

        /// <summary>
        /// Get recording metadata
        /// </summary>
        public RecordingMetadata GetRecordingMetadata(string fileName)
        {
            return RecordingStorage.GetRecordingMetadata(fileName);
        }

        #region Private Methods

        private bool ValidateDependencies()
        {
            if (humanBodyManager == null)
            {
                humanBodyManager = FindFirstObjectByType<ARHumanBodyManager>();
                if (humanBodyManager == null)
                {
                    Debug.LogError("[BodyTrackingController] ARHumanBodyManager not found");
                    UpdateStatus("Error: ARHumanBodyManager missing");
                    return false;
                }
            }

            if (imageTargetManager == null)
            {
                imageTargetManager = FindFirstObjectByType<ARImageTargetManager>();
                if (imageTargetManager == null)
                {
                    Debug.LogError("[BodyTrackingController] ARImageTargetManager not found");
                    UpdateStatus("Error: ARImageTargetManager missing");
                    return false;
                }
            }

            if (worldMapPersistence == null)
            {
                worldMapPersistence = FindFirstObjectByType<ARWorldMapPersistence>();
                if (worldMapPersistence == null)
                {
                    worldMapPersistence = gameObject.AddComponent<ARWorldMapPersistence>();
                }
            }

            SetupRouteRootProviders();
            
            return true;
        }

        /// <summary>
        /// Ensure a RouteRootManager and both providers (Immersal primary + image-target fallback) exist
        /// and are wired. Without the Immersal SDK installed the Immersal provider reports unavailable, so
        /// the manager transparently uses the image-target provider.
        /// </summary>
        private void SetupRouteRootProviders()
        {
            if (routeRootManager == null)
                routeRootManager = FindFirstObjectByType<RouteRootManager>();

            var imageTargetProvider = FindFirstObjectByType<ImageTargetRouteRootProvider>();
            if (imageTargetProvider == null)
                gameObject.AddComponent<ImageTargetRouteRootProvider>();

            var immersalProvider = FindFirstObjectByType<ImmersalRouteRootProvider>();
            if (immersalProvider == null)
                gameObject.AddComponent<ImmersalRouteRootProvider>();

            if (routeRootManager == null)
                routeRootManager = gameObject.AddComponent<RouteRootManager>();

            if (FindFirstObjectByType<ImmersalDelayedInitializer>() == null)
                gameObject.AddComponent<ImmersalDelayedInitializer>();

            EnsureArAnchorManager();
        }

        /// <summary>
        /// Ensure an ARAnchorManager exists on the XR Origin so the Immersal provider can back its room
        /// anchor with a real ARAnchor (ARKit-maintained, drift-resistant). Harmless if anchors are unused.
        /// </summary>
        private void EnsureArAnchorManager()
        {
            if (humanBodyManager == null) return;
            var xrOriginGo = humanBodyManager.gameObject;
            if (xrOriginGo.GetComponent<ARAnchorManager>() == null)
                xrOriginGo.AddComponent<ARAnchorManager>();
        }

        /// <summary>
        /// Manually re-align the Immersal room anchor to the latest localization (e.g. after walking to a new
        /// wall). Ignores the auto-correction size bands. No-op without the Immersal provider.
        /// </summary>
        public void RealignToImmersal()
        {
            if (IsRecording || IsPlaying)
            {
                UpdateStatus("Stop recording/playback before re-aligning");
                return;
            }

            var immersal = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
            if (immersal == null)
            {
                UpdateStatus("Re-align unavailable (no Immersal provider)");
                return;
            }
            immersal.RequestRealign();
            UpdateStatus("Re-aligning to Immersal…");
        }

        /// <summary>
        /// Clears the frozen Immersal anchor so localization can re-establish, then snaps to the latest fix.
        /// Use after walking to a new wall or when the room anchor has drifted.
        /// </summary>
        public void RetargetAndRealignImmersal()
        {
            if (IsRecording || IsPlaying)
            {
                UpdateStatus("Stop recording/playback before retargeting");
                return;
            }

            var immersal = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
            if (immersal == null)
            {
                UpdateStatus("Retarget unavailable (no Immersal provider)");
                return;
            }

            immersal.ClearFrozenAnchor();
            immersal.RequestRealign();
            UpdateStatus("Retargeting Immersal & re-aligning…");
        }

        private void SetupComponents()
        {
            // Setup recorder
            if (recorder == null)
            {
                recorder = gameObject.AddComponent<BodyTrackingRecorder>();
            }
            
            // Setup player
            if (player == null)
            {
                player = gameObject.AddComponent<BodyTrackingPlayer>();
            }
            
            // The RouteRoot provider always has a (non-null) RouteRoot transform even before localization,
            // so recorder/player can be initialized immediately rather than waiting for the image target.
            InitializeRecorderAndPlayer();

            // Pre-warm the video recorder (activate host, init AVPro, allocate buffers) so the FIRST record tap
            // after launch is instant instead of stalling ~15s on AVPro's first-time initialization.
            if (videoRecorder != null && captureVideoWithRecording)
            {
                if (!videoRecorder.gameObject.activeSelf)
                    videoRecorder.gameObject.SetActive(true);
                videoRecorder.Prewarm();
            }
        }
        
        private void InitializeRecorderAndPlayer()
        {
            if (routeRootManager == null || routeRootManager.RouteRoot == null)
            {
                Debug.LogWarning("[BodyTrackingController] RouteRootManager not ready; recorder/player init deferred");
                return;
            }

            // Initialize the player first so the RouteRoot provider is always set even if recorder/character
            // setup throws (e.g. InvalidCast on Instantiate) — otherwise playback hits NRE in CoordinateFrame.
            if (!player.Initialize(routeRootManager))
            {
                Debug.LogWarning("[BodyTrackingController] BodyTrackingPlayer failed to initialize");
            }

            try
            {
                if (!recorder.Initialize(humanBodyManager, routeRootManager))
                {
                    Debug.LogWarning("[BodyTrackingController] BodyTrackingRecorder failed to initialize");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BodyTrackingController] BodyTrackingRecorder initialize error (recording may be limited): {e.Message}\n{e.StackTrace}");
            }

            Debug.Log($"[BodyTrackingController] Ready — pose source={recorder != null}, RouteRoot={routeRootManager.Source}");
        }

        private void SubscribeToEvents()
        {
            // Image target events
            imageTargetManager.OnImageTargetDetected += OnImageTargetDetected;
            imageTargetManager.OnImageTargetLost += OnImageTargetLost;
            if (worldMapPersistence != null)
            {
                worldMapPersistence.OnStatusChanged += OnWorldMapStatusChanged;
                worldMapPersistence.OnWorldMapLoaded += OnWorldMapLoadedForPlaybackAnchor;
            }
            
            // Recorder events
            recorder.OnRecordingComplete += OnRecorderComplete;
            recorder.OnRecordingProgress += OnRecorderProgress;
            
            // Player events
            player.OnPlaybackStarted += OnPlayerStarted;
            player.OnPlaybackStopped += OnPlayerStopped;
            player.OnPlaybackProgress += OnPlayerProgress;

            // Re-evaluate redundant image tracking the moment Immersal localization state changes (e.g. locks).
            if (routeRootManager != null)
                routeRootManager.OnLocalizationChanged += OnRouteLocalizationChanged;

            if (fusionCoordinator != null)
            {
                fusionCoordinator.OnStatusChanged += HandleFusionCoordinatorStatus;
                fusionCoordinator.OnFusionReady += OnFusionReady;
                if (!string.IsNullOrEmpty(fusionCoordinator.LastStatus))
                    SetFusionStatus(fusionCoordinator.LastStatus);

                // Reconnect to any Move AI job that was still processing when the app was last
                // backgrounded/closed; the mocap runs server-side, so we just re-poll and download.
                fusionCoordinator.ResumeInterruptedJobs();
            }

            if (videoRecorder != null)
                videoRecorder.OnPhotosExportFinished += OnVideoPhotosExportFinished;
        }

        private void TryAutoLoadLatestRecording()
        {
            string mapId = GetActiveMapId();
            string bestFile = null;

            if (!string.IsNullOrEmpty(mapId))
            {
                bestFile = RecordingStorage.GetLatestRecordingForMap(mapId);
                if (string.IsNullOrEmpty(bestFile))
                {
                    lastRecording = null;
                    lastRecordingFileName = null;
                    Debug.Log($"[BodyTrackingController] No recordings for map {mapId}");
                    return;
                }
            }
            else
            {
                var available = RecordingStorage.GetAvailableRecordings();
                if (available == null || available.Count == 0)
                {
                    Debug.Log("[BodyTrackingController] No previous recordings found to auto-load");
                    return;
                }

                System.DateTime bestTimestamp = System.DateTime.MinValue;
                foreach (var fileName in available)
                {
                    var metadata = RecordingStorage.GetRecordingMetadata(fileName);
                    if (metadata != null && metadata.recordingTimestamp > bestTimestamp)
                    {
                        bestTimestamp = metadata.recordingTimestamp;
                        bestFile = fileName;
                    }
                }

                if (string.IsNullOrEmpty(bestFile))
                    bestFile = available[available.Count - 1];
            }

            if (!LoadRecording(bestFile, loadAssociatedWorldMap: false))
                Debug.LogWarning($"[BodyTrackingController] Failed to auto-load latest recording: {bestFile}");
        }

        /// <summary>
        /// Pick the RouteRoot provider the player should use for this recording and configure it.
        /// Immersal-sourced recordings replay under the Immersal RouteRoot when it is available; legacy
        /// v1/v2 and image-target recordings are routed through the image-target fallback provider (pinned to
        /// a relocalized world-map anchor if available, otherwise following the live marker).
        /// </summary>
        private void ConfigurePlaybackProvider(HipRecording recording)
        {
            var imageProvider = routeRootManager != null ? routeRootManager.ImageTargetProvider : null;

            bool useImmersal = recording != null &&
                recording.IsImmersalSourced &&
                routeRootManager != null &&
                routeRootManager.ImmersalProvider != null &&
                routeRootManager.ImmersalProvider.IsAvailable;

            if (useImmersal)
            {
                // Bind directly to the concrete Immersal provider (not the manager) so playback gates on
                // Immersal localization and can never silently fall back to the image-target frame. Mapping
                // Immersal-space joints through the marker frame would render them stable but offset/wrong.
                player.SetRouteRootProvider(routeRootManager.ImmersalProvider);
                return;
            }

            if (imageProvider == null)
            {
                // Nothing better available; fall back to whatever the manager selects.
                if (routeRootManager != null)
                    player.SetRouteRootProvider(routeRootManager);
                return;
            }

            imageProvider.SetRouteId(recording != null ? recording.routeId : "");
            player.SetRouteRootProvider(imageProvider);

            if (recording != null && CanUseWorldMapAnchorForPlayback())
                imageProvider.SetAnchorPose(recording.referenceImageTargetPosition, recording.referenceImageTargetRotation);
            else
                imageProvider.FollowLiveMarker();
        }

        private bool CanUseWorldMapAnchorForPlayback()
        {
            return preferWorldMapAnchorOverImageTargetForPlayback &&
                worldMapPersistence != null &&
                worldMapPersistence.IsRelocalized &&
                !string.IsNullOrEmpty(lastRecordingFileName) &&
                worldMapPersistence.HasWorldMap(lastRecordingFileName);
        }

        private void SetMode(OperationMode newMode)
        {
            if (currentMode != newMode)
            {
                currentMode = newMode;
                ApplyRealignSuppression();
                ApplyOcclusionForMode(currentMode);
                ApplyFrameCapForMode(currentMode);
                OnModeChanged?.Invoke(currentMode);
            }
        }

        /// <summary>
        /// ARKit body tracking conflicts with human segmentation / env-depth occlusion modes. Keep occlusion
        /// off until playback, when we only need the ghost character composited into the real scene.
        ///
        /// Critically, ARKit can only run ONE camera configuration at a time and picks the single descriptor
        /// that satisfies every requested feature. The only descriptor that supports 3D body tracking has NO
        /// environment depth / human occlusion, so as long as <see cref="humanBodyManager"/> is enabled ARKit is
        /// locked to that config and our occlusion requests are silently ignored. During playback we replay
        /// recorded data and don't need live body tracking, so we disable the body manager — that frees ARKit to
        /// switch to the configuration that actually supports occlusion + environment depth.
        /// </summary>
        void ApplyOcclusionForMode(OperationMode mode)
        {
            bool enableOcclusion = mode == OperationMode.Playing;

            // The full-screen character-softening pass only matters while the CG character is composited into the
            // scene (playback). Gate it off otherwise so idle/record camera mode skips two full-screen blits/frame.
            BodyTracking.LookDev.CharacterCameraMatch.ScreenSofteningActive = mode == OperationMode.Playing;

            // 3D body tracking is the heaviest live-AR path and runs continuously while the manager is enabled —
            // it does NOT need to be on while the user is just framing the shot (Ready). Enable it only while
            // arming (WaitingForBody) and Recording; the WaitForBodyThenRecord arming loop already tolerates the
            // brief warm-up before ARKit reports a tracked body. Keep it off in Ready (idle camera = much cooler)
            // and in Playing (so ARKit is free to pick an occlusion-capable configuration).
            bool enableBodyTracking = mode == OperationMode.WaitingForBody || mode == OperationMode.Recording;
            if (humanBodyManager != null && humanBodyManager.enabled != enableBodyTracking)
            {
                humanBodyManager.enabled = enableBodyTracking;
                Debug.Log($"[BodyTrackingController] ARHumanBodyManager.enabled={humanBodyManager.enabled} for mode {mode}.");
            }

            // Plane detection is only used by playback features (floor contact shadow + floor penetration), so run
            // it only during playback. This stops continuous plane detection in idle/record camera mode.
            if (planeManager == null)
                planeManager = FindFirstObjectByType<ARPlaneManager>();
            if (planeManager != null && planeManager.enabled != enableOcclusion)
                planeManager.enabled = enableOcclusion;

            // Post-processing (incl. SMAA) and image tracking are only worthwhile during playback / before lock.
            ApplyRenderCostForMode(mode);
            ApplyImageTrackingForState();

            if (characterOcclusion == null)
                characterOcclusion = FindFirstObjectByType<ARCharacterOcclusion>();
            if (characterOcclusion == null)
                return;

            if (characterOcclusion.OcclusionEnabled != enableOcclusion)
                characterOcclusion.SetOcclusionEnabled(enableOcclusion);
        }

        /// <summary>
        /// Run the URP post-processing stack (SMAA + uber/final blit) on the AR camera only during playback,
        /// where a CG character is composited into the scene. Idle/record render just the camera feed + UI, so
        /// post-processing there is several wasted full-screen passes per frame (heat with no visual benefit).
        /// </summary>
        void ApplyRenderCostForMode(OperationMode mode)
        {
            if (!postProcessingDuringPlaybackOnly)
                return;

            if (cameraData == null)
            {
                var cam = Globals.CameraManager != null ? Globals.CameraManager.GetComponent<Camera>() : Camera.main;
                if (cam != null)
                    cameraData = cam.GetComponent<UniversalAdditionalCameraData>();
            }

            if (cameraData != null)
                cameraData.renderPostProcessing = mode == OperationMode.Playing;
        }

        /// <summary>
        /// Image marker tracking is needed during playback (legacy/marker recordings replay against the marker or
        /// a world-map anchor) and until Immersal has locked its room anchor (pre-lock fallback + scanning). Once
        /// Immersal is frozen and we're idle/recording, the marker is never the route frame, so continuous image
        /// tracking is redundant computer-vision cost. Re-evaluated on mode changes and on localization changes.
        /// </summary>
        void ApplyImageTrackingForState()
        {
            if (!gateImageTrackingWhenImmersalLocked)
                return;

            if (trackedImageManager == null)
                trackedImageManager = Globals.TrackedImageManager != null
                    ? Globals.TrackedImageManager
                    : FindFirstObjectByType<ARTrackedImageManager>();
            if (trackedImageManager == null)
                return;

            bool immersalLocked = routeRootManager != null &&
                routeRootManager.ImmersalProvider != null &&
                routeRootManager.ImmersalProvider.IsAnchorFrozen;

            bool needImageTracking = currentMode == OperationMode.Playing || !immersalLocked;
            if (trackedImageManager.enabled != needImageTracking)
                trackedImageManager.enabled = needImageTracking;
        }

        private void OnRouteLocalizationChanged(bool _)
        {
            // Immersal locking flips IsLocalized; re-evaluate whether the redundant marker tracking can stop.
            ApplyImageTrackingForState();
        }

        /// <summary>
        /// Block Immersal auto re-align while recording or playing so the anchor never jumps mid-clip.
        /// </summary>
        private void ApplyRealignSuppression()
        {
            var immersal = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
            if (immersal != null)
                immersal.SuppressAutoRealign = currentMode != OperationMode.Ready;
        }

        private void UpdateStatus(string message)
        {
            if (statusText != null)
                statusText.text = message;
        }

        /// <summary>Update the Move AI status line (top UI strip).</summary>
        public void SetFusionStatus(string message)
        {
            FusionStatusMessage = message ?? "";
            OnFusionStatusChanged?.Invoke(FusionStatusMessage);
        }

        /// <summary>Stop AVPro capture if running; submit Move AI only when hip JSON was saved.</summary>
        void StopPairedVideoCapture(HipRecording recording)
        {
            if (videoRecorder != null && videoRecorder.IsRecording)
            {
                // Encoding/writing the mp4 takes a few seconds; surface it so the wait doesn't look like a freeze.
                UpdateStatus("Finalizing video… (you can keep moving around)");
                if (MoveAIEnabled)
                    SetFusionStatus("Encoding video…");
                videoRecorder.StopRecording(OnPairedVideoReady);
                return;
            }

            if (!string.IsNullOrEmpty(lastRecordingFileName))
                TrySubmitMoveFusion(recording);
            else if (videoRecorder != null && !string.IsNullOrEmpty(videoRecorder.LastFilePath))
                Debug.Log($"[BodyTrackingController] Paired video on disk (hip not saved): {videoRecorder.LastFilePath}");
        }

        void OnPairedVideoReady(string videoPath)
        {
            pairedVideoFilePath = VideoRecorder.NormalizeLocalPath(videoPath);
            if (!string.IsNullOrEmpty(pairedVideoFilePath))
            {
                string name = System.IO.Path.GetFileName(pairedVideoFilePath);
                Debug.Log($"[BodyTrackingController] Video ready: {pairedVideoFilePath}");
                if (MoveAIEnabled)
                    SetFusionStatus($"Video saved · preparing Move AI…");
                else
                    UpdateStatus($"Video saved ({name}). Check Photos app or Xcode container.");
            }

            if (lastRecording != null && !string.IsNullOrEmpty(lastRecordingFileName))
                TrySubmitMoveFusion(lastRecording);
            else if (!string.IsNullOrEmpty(pairedVideoFilePath))
            {
                UpdateStatus("Video saved — hip tracking had no valid frames (no JSON / Move AI).");
                Debug.LogWarning("[BodyTrackingController] Video saved but hip recording was not — Move AI fusion skipped.");
            }
        }

        void OnVideoPhotosExportFinished(string path, bool success)
        {
            if (!success || string.IsNullOrEmpty(path))
                return;
            UpdateStatus($"Video in Photos: {System.IO.Path.GetFileName(path)}");
        }

        void TrySubmitMoveFusion(HipRecording recording)
        {
            if (fusionCoordinator == null || recording == null || string.IsNullOrEmpty(lastRecordingFileName))
                return;

            recording.videoFileName = string.IsNullOrEmpty(pairedVideoFilePath)
                ? null
                : System.IO.Path.GetFileNameWithoutExtension(pairedVideoFilePath);

            if (string.IsNullOrEmpty(pairedVideoFilePath))
            {
                SetFusionStatus("No paired video — Move AI skipped");
                return;
            }

            // Persist the paired-video metadata onto the saved recording so the on-disk JSON is complete before
            // the (background, queued) Move AI upload begins. The queue itself owns job-id/state persistence.
            RecordingStorage.SaveRecording(recording, lastRecordingFileName);

            fusionCoordinator.SubmitForFusion(recording, lastRecordingFileName, pairedVideoFilePath);
        }

        void OnFusionReady(string recordingFileName)
        {
            SetFusionStatus($"Fused replay ready · {recordingFileName}");
        }

        #endregion

        #region Event Handlers

        private void OnImageTargetDetected(Transform imageTarget)
        {
            // The image-target RouteRoot provider follows the marker on its own; here we only handle init,
            // status, and (for legacy/image-target recordings) re-pinning to a relocalized world-map anchor.
            InitializeRecorderAndPlayer();

            ApplyPlaybackReferenceTransformAfterImageEvent();

            if (lastRecording != null && lastRecording.IsValid)
            {
                string fileLabel = string.IsNullOrEmpty(lastRecordingFileName) ? "loaded recording" : $"'{lastRecordingFileName}'";
                UpdateStatus($"IMAGE TARGET DETECTED - world remapped, ready to replay {fileLabel}");
            }
            else
            {
                UpdateStatus("IMAGE TARGET DETECTED - ready to record ARKit movement");
            }
        }

        private void OnImageTargetLost()
        {
            // Recording must stop when the image-target frame is lost (live marker required to capture data).
            // Immersal-sourced recording is unaffected by the image marker.
            if (IsRecording && routeRootManager != null && routeRootManager.Source == SpatialSourceType.ImageTarget)
            {
                UpdateStatus("Image target lost - recording stopped");
                StopRecording();
                return;
            }

            // Likewise abort a pending "waiting for body" arm if the image-target frame is lost.
            if (IsWaitingForBody && routeRootManager != null && routeRootManager.Source == SpatialSourceType.ImageTarget)
            {
                UpdateStatus("Image target lost - recording cancelled");
                CancelArming();
                return;
            }

            if (!IsPlaying)
            {
                UpdateStatus("Image target lost");
                return;
            }

            // The image-target provider freezes its RouteRoot in the room automatically (ARKit world
            // tracking holds the pose). If a world-map anchor matches this recording, pin to it instead.
            if (CanUseWorldMapAnchorForPlayback() && lastRecording != null && lastRecording.IsValid)
            {
                PinImageTargetProviderToWorldMapAnchor();
                UpdateStatus("Image target lost - playback anchored to world map");
            }
            else
            {
                UpdateStatus("Image target lost - playback anchored in room (re-detect marker to re-sync)");
            }
        }

        /// <summary>
        /// When a world map has relocalized, pin the image-target provider's RouteRoot to the saved
        /// recorded pose (the live tracked image pose can differ and would misalign the skeleton).
        /// </summary>
        private void ApplyPlaybackReferenceTransformAfterImageEvent()
        {
            if (CanUseWorldMapAnchorForPlayback() &&
                lastRecording != null &&
                lastRecording.IsValid)
            {
                PinImageTargetProviderToWorldMapAnchor();
            }
        }

        private void PinImageTargetProviderToWorldMapAnchor()
        {
            var provider = routeRootManager != null ? routeRootManager.ImageTargetProvider : null;
            if (provider == null || lastRecording == null)
                return;
            provider.SetAnchorPose(lastRecording.referenceImageTargetPosition, lastRecording.referenceImageTargetRotation);
        }

        private void OnRecorderComplete(HipRecording recording)
        {
            // This is handled in StopRecording()
        }

        private void OnRecorderProgress(float time)
        {
            if (statusText != null)
            {
                statusText.text = $"Recording hip position... {time:F1}s";
            }
        }

        private void OnPlayerStarted()
        {
            OnPlaybackStarted?.Invoke();
        }

        private void OnPlayerStopped()
        {
            SetMode(OperationMode.Ready);
            OnPlaybackStopped?.Invoke();
        }

        private void OnPlayerProgress(float progress)
        {
            if (statusText != null && lastRecording != null)
            {
                statusText.text = $"Playing hip movement... {progress * 100:F0}%";
            }
        }

        private void HandleFusionCoordinatorStatus(string message)
        {
            if (!string.IsNullOrEmpty(message))
                SetFusionStatus(message);
        }

        private void OnWorldMapStatusChanged(string message)
        {
            if (!string.IsNullOrEmpty(message) &&
                (message.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 message.IndexOf("saved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 message.IndexOf("loaded", StringComparison.OrdinalIgnoreCase) >= 0))
                Debug.Log($"[BodyTrackingController] WorldMap: {message}");
        }

        private void OnWorldMapLoadedForPlaybackAnchor(string _)
        {
            if (!CanUseWorldMapAnchorForPlayback())
                return;
            if (lastRecording == null || !lastRecording.IsValid)
                return;

            PinImageTargetProviderToWorldMapAnchor();
        }

        #endregion

        void OnDestroy()
        {
            // Unsubscribe from events
            if (imageTargetManager != null)
            {
                imageTargetManager.OnImageTargetDetected -= OnImageTargetDetected;
                imageTargetManager.OnImageTargetLost -= OnImageTargetLost;
            }
            
            if (recorder != null)
            {
                recorder.OnRecordingComplete -= OnRecorderComplete;
                recorder.OnRecordingProgress -= OnRecorderProgress;
            }
            
            if (player != null)
            {
                player.OnPlaybackStarted -= OnPlayerStarted;
                player.OnPlaybackStopped -= OnPlayerStopped;
                player.OnPlaybackProgress -= OnPlayerProgress;
            }

            if (fusionCoordinator != null)
            {
                fusionCoordinator.OnStatusChanged -= HandleFusionCoordinatorStatus;
                fusionCoordinator.OnFusionReady -= OnFusionReady;
            }

            if (videoRecorder != null)
                videoRecorder.OnPhotosExportFinished -= OnVideoPhotosExportFinished;

            if (worldMapPersistence != null)
            {
                worldMapPersistence.OnStatusChanged -= OnWorldMapStatusChanged;
                worldMapPersistence.OnWorldMapLoaded -= OnWorldMapLoadedForPlaybackAnchor;
            }

            if (routeRootManager != null)
                routeRootManager.OnLocalizationChanged -= OnRouteLocalizationChanged;
        }
    }

    /// <summary>
    /// Operation modes for the hip tracking system
    /// </summary>
    public enum OperationMode
    {
        Ready,           // System ready, waiting for commands
        WaitingForBody,  // Record tapped; waiting for ARKit to detect a body before capture starts
        Recording,       // Currently recording
        Playing          // Currently playing back
    }
} 