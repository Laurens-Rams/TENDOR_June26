using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using BodyTracking.Data;
using BodyTracking.Recording;
using BodyTracking.Playback;
using BodyTracking.Storage;
using BodyTracking.AR;
using BodyTracking.Spatial;
using BodyTracking.MoveAI;

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

        [Header("Recording start")]
        [Tooltip("When recording, wait until ARKit detects a body before capture begins, so the joint recording and the paired video both start with the climber already in frame.")]
        public bool waitForBodyBeforeRecording = true;
        [Tooltip("Require the body to be tracked this many consecutive frames before starting (debounces detection flicker).")]
        public int bodyDetectConsecutiveFrames = 3;
        [Tooltip("Start recording anyway if no body is detected within this many seconds (0 = wait until the user taps stop to cancel).")]
        public float waitForBodyTimeoutSeconds = 25f;
        [Tooltip("Trim this many seconds off the end of each recording (and the Move AI clip), since the climber usually steps out of frame to tap stop. 0 disables trimming.")]
        public float trimEndSeconds = 2f;
        
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

        [Header("UI")]
        public TMPro.TextMeshProUGUI statusText;
        
        // State
        private bool isInitialized = false;
        private OperationMode currentMode = OperationMode.Ready;
        private bool isPaused;
        private HipRecording lastRecording;
        private string lastRecordingFileName;
        private Coroutine armingCoroutine;
        
        // Events
        public event System.Action<OperationMode> OnModeChanged;
        public event System.Action<HipRecording> OnRecordingComplete;
        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;
        
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

        /// <summary>True when a fused Move AI replay is driving playback (instead of the dot-skeleton player).</summary>
        public bool IsFusedPlaying => fusionCoordinator != null && fusionCoordinator.IsFusedPlaying;
        public float FusedCurrentTime => fusionCoordinator != null ? fusionCoordinator.FusedCurrentTime : 0f;
        public float FusedDuration => fusionCoordinator != null ? fusionCoordinator.FusedDuration : 0f;

        public event Action<string> OnFusionStatusChanged;

        void Start()
        {
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
        /// Remove frames in the last <paramref name="seconds"/> of the recording and shorten its duration to match.
        /// No-op when the recording is shorter than the trim amount.
        /// </summary>
        private void TrimRecordingTail(HipRecording recording, float seconds)
        {
            if (recording == null || recording.frames == null || recording.frames.Count == 0 || seconds <= 0f)
                return;

            float cutoff = recording.duration - seconds;
            if (cutoff <= 0f)
            {
                Debug.LogWarning($"[BodyTrackingController] Trim {seconds:F1}s skipped — recording only {recording.duration:F2}s long.");
                return;
            }

            int removed = recording.frames.RemoveAll(f => f.timestamp > cutoff);
            recording.duration = cutoff;
            Debug.Log($"[BodyTrackingController] Trimmed last {seconds:F1}s ({removed} frames removed); duration now {cutoff:F2}s.");
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

            // Drop the tail where the climber steps out of frame to tap stop (keeps the video, hip data, and the
            // Move AI clip window consistent since the video is stopped right after this).
            if (lastRecording != null && trimEndSeconds > 0f)
                TrimRecordingTail(lastRecording, trimEndSeconds);

            // Drop leading frames with no body/hip (common in portrait while ARKit is still locking on).
            if (lastRecording != null)
                TrimRecordingLeadingInvalid(lastRecording);

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

                StopPairedVideoCapture(lastRecording);
            }
            
            SetMode(OperationMode.Ready);
            return lastRecording;
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
                return true;
            }

            if (player.LoadRecording(recording))
            {
                player.StartPlayback();
                isPaused = false;
                SetMode(OperationMode.Playing);
                UpdateStatus($"Playing back hip movement: {recording.FrameCount} frames");
                Debug.Log("[BodyTrackingController] Started hip playback");
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

        /// <summary>
        /// Reload the most recent recording from storage so playback always uses the latest clip.
        /// </summary>
        public void LoadLatestRecording() => TryAutoLoadLatestRecording();

        /// <summary>
        /// Load a recording from storage
        /// </summary>
        public bool LoadRecording(string fileName, bool loadAssociatedWorldMap = true)
        {
            var recording = RecordingStorage.LoadRecording(fileName);
            if (recording != null && recording.IsValid)
            {
                lastRecording = recording;
                lastRecordingFileName = fileName;
                UpdateStatus($"Loaded hip recording: {recording.ValidFrameCount}/{recording.FrameCount} valid frames");
                Debug.Log($"[BodyTrackingController] Loaded '{fileName}' ({recording.ValidFrameCount}/{recording.FrameCount} valid, {recording.duration:F1}s)");
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
            bool enable = mode == OperationMode.Playing;

            // Free ARKit to choose an occlusion-capable configuration by dropping 3D body tracking during playback.
            if (humanBodyManager != null && humanBodyManager.enabled == enable)
            {
                humanBodyManager.enabled = !enable;
                Debug.Log($"[BodyTrackingController] ARHumanBodyManager.enabled={humanBodyManager.enabled} for mode {mode} (occlusion needs body tracking off).");
            }

            if (characterOcclusion == null)
                characterOcclusion = FindFirstObjectByType<ARCharacterOcclusion>();
            if (characterOcclusion == null)
                return;

            if (characterOcclusion.OcclusionEnabled != enable)
                characterOcclusion.SetOcclusionEnabled(enable);
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