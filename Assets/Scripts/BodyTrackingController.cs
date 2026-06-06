using UnityEngine;
using UnityEngine.XR.ARFoundation;
using BodyTracking.Data;
using BodyTracking.Recording;
using BodyTracking.Playback;
using BodyTracking.Storage;
using BodyTracking.AR;

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
        
        [Header("Components")]
        public BodyTrackingRecorder recorder;
        public BodyTrackingPlayer player;
        
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
        
        [Header("UI")]
        public TMPro.TextMeshProUGUI statusText;
        
        // State
        private bool isInitialized = false;
        private OperationMode currentMode = OperationMode.Ready;
        private HipRecording lastRecording;
        private string lastRecordingFileName;
        
        // Events
        public event System.Action<OperationMode> OnModeChanged;
        public event System.Action<HipRecording> OnRecordingComplete;
        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;
        
        // Public Properties
        public bool IsInitialized => isInitialized;
        public OperationMode CurrentMode => currentMode;
        public bool CanRecord => isInitialized && imageTargetManager != null && imageTargetManager.IsImageDetected && currentMode == OperationMode.Ready;
        public bool CanPlayback => isInitialized && HasPlaybackReferenceFrame() && lastRecording != null && lastRecording.IsValid && currentMode == OperationMode.Ready;
        public bool IsRecording => currentMode == OperationMode.Recording;
        public bool IsPlaying => currentMode == OperationMode.Playing;
        public string LastRecordingFileName => lastRecordingFileName;
        public string WorldMapStatus => worldMapPersistence != null ? worldMapPersistence.LastStatusMessage : "WorldMap service unavailable";

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
            if (lastRecording != null && lastRecording.IsValid)
            {
                UpdateStatus($"Hip tracking initialized - loaded '{lastRecordingFileName}'. Detect image target to replay.");
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
            
            if (recorder.StartRecording())
            {
                SetMode(OperationMode.Recording);
                UpdateStatus("Recording hip position...");
                Debug.Log("[BodyTrackingController] Started hip recording");
                return true;
            }
            
            return false;
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
            
            if (lastRecording != null)
            {
                if (!lastRecording.IsValid)
                {
                    Debug.LogWarning($"[BodyTrackingController] Recording has no valid hip frames: {lastRecording.FrameCount} total frames");
                    UpdateStatus("Recording stopped, but no tracked hip frames were captured");
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
            
            Transform targetTransform = null;
            string targetSource = "None";

            if (preferWorldMapAnchorOverImageTargetForPlayback &&
                worldMapPersistence != null &&
                worldMapPersistence.IsRelocalized)
            {
                targetTransform = worldMapPersistence.GetOrCreatePlaybackAnchor(
                    recording.referenceImageTargetPosition,
                    recording.referenceImageTargetRotation,
                    recording.referenceImageTargetScale);
                targetSource = "WorldMapAnchor";
            }
            else if (imageTargetManager != null && imageTargetManager.IsImageDetected)
            {
                targetTransform = imageTargetManager.ImageTargetTransform;
                targetSource = "ImageTarget";
            }
            else if (worldMapPersistence != null && worldMapPersistence.IsRelocalized)
            {
                targetTransform = worldMapPersistence.GetOrCreatePlaybackAnchor(
                    recording.referenceImageTargetPosition,
                    recording.referenceImageTargetRotation,
                    recording.referenceImageTargetScale);
                targetSource = "WorldMapAnchor";
            }

            if (targetTransform != null)
            {
                player.SetImageTarget(targetTransform);
                Debug.Log($"[BodyTrackingController] Playback target source: {targetSource}, pos={targetTransform.position}, rot={targetTransform.rotation.eulerAngles}, scale={targetTransform.localScale}");
            }
            else
            {
                Debug.LogWarning("[BodyTrackingController] No playback target transform available (image target missing and world map not relocalized).");
            }

            if (player.LoadRecording(recording))
            {
                player.StartPlayback();
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
            SetMode(OperationMode.Ready);
            UpdateStatus("Hip playback stopped");
            Debug.Log("[BodyTrackingController] Stopped hip playback");
        }

        /// <summary>
        /// Load a recording from storage
        /// </summary>
        public bool LoadRecording(string fileName)
        {
            var recording = RecordingStorage.LoadRecording(fileName);
            if (recording != null && recording.IsValid)
            {
                lastRecording = recording;
                lastRecordingFileName = fileName;
                UpdateStatus($"Loaded hip recording: {recording.ValidFrameCount}/{recording.FrameCount} valid frames");
                Debug.Log($"[BodyTrackingController] Loaded hip recording: {fileName}");
                if (autoLoadWorldMapForLoadedRecording && worldMapPersistence != null)
                {
                    StartCoroutine(worldMapPersistence.LoadWorldMapForRecording(fileName));
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
            return RecordingStorage.GetAvailableRecordings();
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
                humanBodyManager = FindObjectOfType<ARHumanBodyManager>();
                if (humanBodyManager == null)
                {
                    Debug.LogError("[BodyTrackingController] ARHumanBodyManager not found");
                    UpdateStatus("Error: ARHumanBodyManager missing");
                    return false;
                }
            }
            
            if (imageTargetManager == null)
            {
                imageTargetManager = FindObjectOfType<ARImageTargetManager>();
                if (imageTargetManager == null)
                {
                    Debug.LogError("[BodyTrackingController] ARImageTargetManager not found");
                    UpdateStatus("Error: ARImageTargetManager missing");
                    return false;
                }
            }

            if (worldMapPersistence == null)
            {
                worldMapPersistence = FindObjectOfType<ARWorldMapPersistence>();
                if (worldMapPersistence == null)
                {
                    worldMapPersistence = gameObject.AddComponent<ARWorldMapPersistence>();
                    Debug.Log("[BodyTrackingController] Added ARWorldMapPersistence component");
                }
            }
            
            return true;
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
            
            // Initialize components if image target is available
            if (imageTargetManager.IsImageDetected)
            {
                InitializeRecorderAndPlayer();
            }
            // Otherwise, they will be initialized when image target is detected
        }
        
        private void InitializeRecorderAndPlayer()
        {
            var imageTarget = imageTargetManager.ImageTargetTransform;
            if (imageTarget == null) return;

            // Initialize the player first so the image target is always set even if recorder/character
            // setup throws (e.g. InvalidCast on Instantiate) — otherwise playback hits NRE in CoordinateFrame.
            if (!player.Initialize(imageTarget))
            {
                Debug.LogWarning("[BodyTrackingController] BodyTrackingPlayer failed to initialize");
            }

            try
            {
                if (!recorder.Initialize(humanBodyManager, imageTarget))
                {
                    Debug.LogWarning("[BodyTrackingController] BodyTrackingRecorder failed to initialize");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BodyTrackingController] BodyTrackingRecorder initialize error (recording may be limited): {e.Message}\n{e.StackTrace}");
            }

            Debug.Log("[BodyTrackingController] Recorder and player initialized with image target");
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
        }

        private void TryAutoLoadLatestRecording()
        {
            var available = RecordingStorage.GetAvailableRecordings();
            if (available == null || available.Count == 0)
            {
                Debug.Log("[BodyTrackingController] No previous recordings found to auto-load");
                return;
            }

            string bestFile = null;
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

            // Fallback when metadata timestamps are unavailable/corrupt.
            if (string.IsNullOrEmpty(bestFile))
            {
                bestFile = available[available.Count - 1];
            }

            if (LoadRecording(bestFile))
            {
                Debug.Log($"[BodyTrackingController] Auto-loaded latest recording on initialize: {bestFile}");
            }
            else
            {
                Debug.LogWarning($"[BodyTrackingController] Failed to auto-load latest recording: {bestFile}");
            }
        }

        private bool HasPlaybackReferenceFrame()
        {
            bool imageTargetReady = imageTargetManager != null && imageTargetManager.IsImageDetected;
            bool worldMapReady = worldMapPersistence != null && worldMapPersistence.IsRelocalized;
            return imageTargetReady || worldMapReady;
        }

        private void SetMode(OperationMode newMode)
        {
            if (currentMode != newMode)
            {
                currentMode = newMode;
                OnModeChanged?.Invoke(currentMode);
                
                Debug.Log($"[BodyTrackingController] Mode changed to: {currentMode}");
            }
        }

        private void UpdateStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
            
            Debug.Log($"[BodyTrackingController] Status: {message}");
        }

        #endregion

        #region Event Handlers

        private void OnImageTargetDetected(Transform imageTarget)
        {
            // Initialize recorder and player with the detected image target (recording still uses the marker frame).
            InitializeRecorderAndPlayer();

            ApplyPlaybackReferenceTransformAfterImageEvent(imageTarget);

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
            UpdateStatus("Image target lost");
            
            // Stop any ongoing operations
            if (IsRecording)
            {
                StopRecording();
            }

            // Keep playback if we are aligned to the world map anchor (recorded pose), not the live marker.
            bool playbackUsesWorldAnchor = preferWorldMapAnchorOverImageTargetForPlayback &&
                worldMapPersistence != null &&
                worldMapPersistence.IsRelocalized &&
                lastRecording != null &&
                lastRecording.IsValid;

            if (IsPlaying && !playbackUsesWorldAnchor)
            {
                StopPlayback();
            }
            else if (IsPlaying && playbackUsesWorldAnchor && player != null)
            {
                var anchor = worldMapPersistence.GetOrCreatePlaybackAnchor(
                    lastRecording.referenceImageTargetPosition,
                    lastRecording.referenceImageTargetRotation,
                    lastRecording.referenceImageTargetScale);
                player.SetImageTarget(anchor);
            }
        }

        /// <summary>
        /// Choose the transform used to map recorded reference-space points to world space during playback.
        /// When a world map has relocalized, the frozen anchor matches the recording file; the live tracked
        /// image pose can differ and would misalign the skeleton if used here.
        /// </summary>
        private void ApplyPlaybackReferenceTransformAfterImageEvent(Transform imageTarget)
        {
            if (player == null)
                return;

            if (preferWorldMapAnchorOverImageTargetForPlayback &&
                worldMapPersistence != null &&
                worldMapPersistence.IsRelocalized &&
                lastRecording != null &&
                lastRecording.IsValid)
            {
                var anchor = worldMapPersistence.GetOrCreatePlaybackAnchor(
                    lastRecording.referenceImageTargetPosition,
                    lastRecording.referenceImageTargetRotation,
                    lastRecording.referenceImageTargetScale);
                player.SetImageTarget(anchor);
                return;
            }

            if (imageTarget != null)
                player.SetImageTarget(imageTarget);
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

        private void OnWorldMapStatusChanged(string message)
        {
            Debug.Log($"[BodyTrackingController] WorldMap: {message}");
        }

        private void OnWorldMapLoadedForPlaybackAnchor(string _)
        {
            if (!preferWorldMapAnchorOverImageTargetForPlayback)
                return;
            if (worldMapPersistence == null || !worldMapPersistence.IsRelocalized)
                return;
            if (lastRecording == null || !lastRecording.IsValid || player == null)
                return;

            var anchor = worldMapPersistence.GetOrCreatePlaybackAnchor(
                lastRecording.referenceImageTargetPosition,
                lastRecording.referenceImageTargetRotation,
                lastRecording.referenceImageTargetScale);
            player.SetImageTarget(anchor);
            Debug.Log("[BodyTrackingController] Playback reference frame switched to world-map anchor after relocalization.");
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
        Ready,      // System ready, waiting for commands
        Recording,  // Currently recording
        Playing     // Currently playing back
    }
} 