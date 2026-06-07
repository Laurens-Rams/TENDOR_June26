using UnityEngine;
using BodyTracking.Data;
using BodyTracking.Animation;
using BodyTracking.Spatial;
using BodyTracking.Utils;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Handles frame-accurate playback of recorded hip position data
    /// </summary>
    public class BodyTrackingPlayer : MonoBehaviour
    {
        [Header("Playback Settings")]
        [SerializeField] private bool loopPlayback = true;
        [SerializeField] private float playbackSpeed = 1.0f;
        [SerializeField] private bool showVisualization = false;
        
        [Header("Visualization")]
        [SerializeField] private bool showRecordedSkeleton = true;
        [SerializeField] private float skeletonJointRadius = 0.035f;
        [SerializeField] private float skeletonBoneLineWidth = 0.03f;
        [SerializeField] private Color skeletonJointColor = Color.cyan;
        [SerializeField] private Color skeletonBoneColor = new Color(0f, 0f, 1f, 0.7f);
        [SerializeField] private bool showPath = false;
        [SerializeField] private int maxPathPoints = 100;
        
        [Header("Character Integration")]
        [SerializeField] private FBXCharacterController characterController;
        [SerializeField] private bool autoFindCharacterController = false;
        [SerializeField] private bool useFbxCharacterPlayback = false;
        
        // Dependencies
        // Supplies the RouteRoot frame recorded points are mapped back through. The provider itself owns
        // live-follow vs frozen vs world-map-anchor behaviour, so the player just reads RouteRoot each frame.
        private IRouteRootProvider routeRootProvider;
        private CoordinateFrame currentReferenceFrame;
        private bool hasReferenceFrame = false;
        
        // Playback state
        private HipRecording recording;
        private bool isPlaying = false;
        private bool isPaused = false;
        private float playbackStartTime;
        private float currentPlaybackTime = 0f;
        private int lastPlaybackDebugFrame = -9999;
        private bool waitingForLocalization = false;
        
        // Visualization
        private GameObject hipSphere;
        private LineRenderer pathLine;
        private GameObject skeletonRoot;
        private GameObject[] skeletonJointSpheres;
        private LineRenderer[] skeletonBoneLines;
        private Material skeletonJointMaterial;
        private Material skeletonBoneMaterial;
        private const float BoneOpacity = 0.7f;
        
        // Events
        public event System.Action OnPlaybackStarted;
        public event System.Action OnPlaybackStopped;
        public event System.Action OnPlaybackLooped;
        public event System.Action<float> OnPlaybackProgress; // 0-1
        
        // Segment loop (used by the playback screen when looping a single move).
        private bool segmentLoopEnabled;
        private float segmentLoopStart;
        private float segmentLoopEnd;

        // Public properties
        public bool IsPlaying => isPlaying;
        public bool IsPaused => isPaused;
        public float PlaybackProgress => recording != null ? Mathf.Clamp01(currentPlaybackTime / recording.duration) : 0f;
        public float CurrentTime => currentPlaybackTime;
        public float Duration => recording?.duration ?? 0f;
        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set
            {
                if (Mathf.Approximately(playbackSpeed, value)) return;
                float t = currentPlaybackTime;
                playbackSpeed = Mathf.Max(0.01f, value);
                if (isPlaying && !isPaused)
                    playbackStartTime = Time.time - t / playbackSpeed;
            }
        }
        public bool LoopPlayback
        {
            get => loopPlayback;
            set => loopPlayback = value;
        }
        public float FrameRate => recording != null && recording.frameRate > 0f ? recording.frameRate : 30f;

        /// <summary>
        /// Bind/replace the RouteRoot provider used to map recorded points back to world space.
        /// </summary>
        public void SetRouteRootProvider(IRouteRootProvider provider)
        {
            if (provider == null) return;
            routeRootProvider = provider;
            var root = routeRootProvider.RouteRoot;
            if (root != null)
            {
                currentReferenceFrame = new CoordinateFrame(root);
                hasReferenceFrame = true;
            }
        }

        public bool HasReferenceFrame => hasReferenceFrame;

        /// <summary>
        /// Show/hide the recorded dot+line skeleton "ghost" drawn during playback. Used by the clean-view
        /// toggle so only the final character remains visible.
        /// </summary>
        public void SetSkeletonVisible(bool visible)
        {
            showRecordedSkeleton = visible;
            if (!visible)
            {
                HideRecordedSkeleton();
                if (hipSphere != null)
                    hipSphere.SetActive(false);
            }
        }

        /// <summary>True when playback is loaded/running but the RouteRoot is not yet localized.</summary>
        public bool IsWaitingForLocalization => waitingForLocalization;

        /// <summary>
        /// Initialize the player with the RouteRoot provider (Immersal primary, image target fallback).
        /// </summary>
        public bool Initialize(IRouteRootProvider provider)
        {
            routeRootProvider = provider;
            
            if (routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] RouteRoot provider (with a RouteRoot transform) is required");
                return false;
            }
            
            currentReferenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);
            hasReferenceFrame = true;
            
            if (showVisualization)
            {
                InitializeVisualization();
            }
            
            if (useFbxCharacterPlayback)
            {
                SetupCharacterController();
            }
            
            return true;
        }

        /// <summary>
        /// Setup integration with character controller
        /// </summary>
        private void SetupCharacterController()
        {
            if (characterController == null && autoFindCharacterController)
            {
                characterController = FindFirstObjectByType<FBXCharacterController>();
                if (characterController != null)
                {
                   UnityEngine.Debug.Log("[BodyTrackingPlayer] Found FBXCharacterController automatically");
                }
            }
            
            if (characterController != null)
            {
                if (!characterController.IsInitialized)
                {
                    try
                    {
                        characterController.Initialize();
                    }
                    catch (System.Exception e)
                    {
                       UnityEngine.Debug.LogError($"[BodyTrackingPlayer] Character controller init failed: {e.Message}");
                    }
                }
               UnityEngine.Debug.Log("[BodyTrackingPlayer] Character controller integration enabled for playback");
            }
            else
            {
               UnityEngine.Debug.Log("[BodyTrackingPlayer] No character controller found - visualization only");
            }
        }

        /// <summary>
        /// Load a recording for playback
        /// </summary>
        public bool LoadRecording(HipRecording newRecording)
        {
            if (newRecording == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] Invalid hip recording provided");
                return false;
            }

            newRecording.NormalizeFormatAfterLoad();

            if (!newRecording.IsValid)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] Invalid hip recording provided");
                return false;
            }
            
            // Stop current playback if running
            if (isPlaying)
            {
                StopPlayback();
            }
            
            recording = newRecording;
            currentPlaybackTime = 0f;
            
            return true;
        }

        /// <summary>
        /// Start playback of loaded recording
        /// </summary>
        public void StartPlayback()
        {
            if (recording == null || !recording.IsValid)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] No valid hip recording loaded");
                return;
            }
            
            if (isPlaying)
            {
               UnityEngine.Debug.LogWarning("[BodyTrackingPlayer] Already playing");
                return;
            }

            if (routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] No RouteRoot provider. Wait for Immersal/image-target localization, then try again.");
                return;
            }
            
            // Map recorded RouteRoot-local points through the current RouteRoot pose.
            var startRoot = routeRootProvider.RouteRoot;
            currentReferenceFrame = new CoordinateFrame(startRoot);
            hasReferenceFrame = true;
            // Ghost stays hidden until the RouteRoot is localized; rendering resumes automatically.
            waitingForLocalization = !routeRootProvider.IsLocalized;
            UnityEngine.Debug.Log($"[BodyTrackingPlayer] StartPlayback source={routeRootProvider.Source}, localized={routeRootProvider.IsLocalized}, pos={startRoot.position}, rot={startRoot.rotation.eulerAngles}, duration={recording.duration:F2}s, frames={recording.FrameCount}");

            // Frame-shift diagnostic: compare the RouteRoot world pose captured at RECORD time (stored in the
            // recording) against the RouteRoot world pose NOW. A large delta = the anchor landed in a different
            // place than when recorded (bad Immersal lock or cross-session variance) and is exactly the offset
            // the played-back body will show.
            {
                Vector3 recPos = recording.referenceImageTargetPosition;
                Quaternion recRot = recording.referenceImageTargetRotation;
                float dPos = Vector3.Distance(recPos, startRoot.position);
                float dRot = Quaternion.Angle(recRot, startRoot.rotation);
                UnityEngine.Debug.Log($"[BodyTrackingPlayer] Frame-shift vs record-time RouteRoot: dPos={dPos:F2}m dRot={dRot:F1}deg " +
                    $"(recorded pos={recPos} rot={recRot.eulerAngles}). If dPos/dRot are large, the body will be offset by this much.");
            }
            
            isPlaying = true;
            isPaused = false;
            float startTime = recording.GetFirstValidFrameTime();
            currentPlaybackTime = startTime;
            playbackStartTime = Time.time - startTime / playbackSpeed;
            HideVisualization();
            
            // Start animation playback only when FBX playback is explicitly enabled.
            if (useFbxCharacterPlayback && characterController != null && characterController.IsInitialized)
            {
                bool animationStarted = characterController.StartAnimationPlayback();
                if (animationStarted)
                {
                   UnityEngine.Debug.Log("[BodyTrackingPlayer] Started synchronized animation playback");
                }
                else
                {
                   UnityEngine.Debug.LogWarning("[BodyTrackingPlayer] Failed to start animation playback - continuing with hip-only playback");
                }
            }
            
            OnPlaybackStarted?.Invoke();

            // Draw the first valid recorded skeleton immediately (unless we are waiting for localization).
            if (!waitingForLocalization)
            {
                var firstFrame = recording.GetFrameAtTime(startTime);
                if (firstFrame.IsValid)
                {
                    UpdatePlaybackFrame(firstFrame);
                }
            }
            else
            {
                HideRecordedSkeleton();
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public void StopPlayback()
        {
            if (!isPlaying) return;
            
            isPlaying = false;
            isPaused = false;
            
            if (showVisualization)
            {
                HideVisualization();
            }
            
            HideRecordedSkeleton();

            // Stop animation playback only when FBX playback is explicitly enabled.
            if (useFbxCharacterPlayback && characterController != null && characterController.IsInitialized)
            {
                characterController.StopAnimationPlayback();
               UnityEngine.Debug.Log("[BodyTrackingPlayer] Stopped synchronized animation playback");
            }
            
            OnPlaybackStopped?.Invoke();
        }

        /// <summary>
        /// Pause playback in place (freezes the timeline without resetting it).
        /// </summary>
        public void PausePlayback()
        {
            if (!isPlaying || isPaused) return;
            isPaused = true;
        }

        /// <summary>
        /// Resume playback from where it was paused.
        /// </summary>
        public void ResumePlayback()
        {
            if (!isPlaying || !isPaused) return;
            isPaused = false;
            // Re-anchor the clock so the timeline continues from the paused position.
            playbackStartTime = Time.time - currentPlaybackTime / playbackSpeed;
        }

        /// <summary>
        /// Seek to specific time in the recording
        /// </summary>
        public void SeekToTime(float time)
        {
            if (recording == null) return;
            
            currentPlaybackTime = Mathf.Clamp(time, 0f, recording.duration);
            
            if (isPlaying)
            {
                playbackStartTime = Time.time - currentPlaybackTime / playbackSpeed;
                if (isPaused)
                {
                    var frame = recording.GetFrameAtTime(currentPlaybackTime);
                    if (frame.IsValid && !waitingForLocalization)
                        UpdatePlaybackFrame(frame);
                }
            }
        }

        /// <summary>Configure looping between two times (inclusive start, exclusive end).</summary>
        public void SetSegmentLoop(float start, float end, bool enabled)
        {
            segmentLoopEnabled = enabled && end > start;
            segmentLoopStart = Mathf.Max(0f, start);
            segmentLoopEnd = end;
        }

        /// <summary>Step the timeline by whole frames (positive = forward).</summary>
        public void StepFrames(int deltaFrames)
        {
            if (recording == null || deltaFrames == 0) return;
            float step = deltaFrames * (1f / FrameRate);
            SeekToTime(currentPlaybackTime + step);
        }

        void Update()
        {
            if (!isPlaying || recording == null) return;

            // Gate the ghost on localization: hide it (but keep the timeline advancing) until the
            // RouteRoot reports a usable, wall-aligned pose. When localization returns, rendering resumes.
            bool localized = routeRootProvider == null || routeRootProvider.IsLocalized;
            if (!localized)
            {
                if (!waitingForLocalization)
                {
                    waitingForLocalization = true;
                    HideRecordedSkeleton();
                    if (hipSphere != null) hipSphere.SetActive(false);
                }
            }
            else
            {
                waitingForLocalization = false;
            }

            // Track the live RouteRoot pose each frame (Immersal scene updates / image target follow).
            // Raw: snap directly to the live frame every frame (no smoothing).
            if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
            {
                currentReferenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);
                hasReferenceFrame = true;
            }

            // While paused, keep the skeleton anchored at the frozen frame (so it stays aligned if the phone
            // moves) but do not advance the timeline.
            if (isPaused)
            {
                var pausedFrame = recording.GetFrameAtTime(currentPlaybackTime);
                if (pausedFrame.IsValid && !waitingForLocalization)
                    UpdatePlaybackFrame(pausedFrame);
                return;
            }
            
            // Update playback time
            float elapsedTime = (Time.time - playbackStartTime) * playbackSpeed;
            currentPlaybackTime = elapsedTime;
            
            // Handle looping (segment loop takes priority over full loop).
            if (segmentLoopEnabled && segmentLoopEnd > segmentLoopStart)
            {
                if (currentPlaybackTime >= segmentLoopEnd)
                {
                    currentPlaybackTime = segmentLoopStart;
                    playbackStartTime = Time.time - currentPlaybackTime / playbackSpeed;
                    OnPlaybackLooped?.Invoke();
                    if (useFbxCharacterPlayback && characterController != null && characterController.IsInitialized)
                        characterController.StartAnimationPlayback();
                }
            }
            else if (currentPlaybackTime >= recording.duration)
            {
                if (loopPlayback)
                {
                    currentPlaybackTime = currentPlaybackTime % recording.duration;
                    playbackStartTime = Time.time - currentPlaybackTime / playbackSpeed;
                    OnPlaybackLooped?.Invoke();
                    
                    // Restart animation for loop
                    if (useFbxCharacterPlayback && characterController != null && characterController.IsInitialized)
                    {
                        characterController.StartAnimationPlayback();
                    }
                }
                else
                {
                    StopPlayback();
                    return;
                }
            }
            
            // Get current frame
            var currentFrame = recording.GetFrameAtTime(currentPlaybackTime);
            
            if (currentFrame.IsValid && !waitingForLocalization)
            {
                UpdatePlaybackFrame(currentFrame);
            }
            
            // Notify progress
            OnPlaybackProgress?.Invoke(PlaybackProgress);
        }

        /// <summary>
        /// Initialize visualization components
        /// </summary>
        private void InitializeVisualization()
        {
            // Create hip sphere
            hipSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hipSphere.name = "PlaybackHipSphere";
            hipSphere.transform.localScale = Vector3.one * 0.15f;
            
            // Setup material - blue for playback
            var renderer = hipSphere.GetComponent<Renderer>();
            if (renderer != null)
            {
                var material = DebugVisualizationMaterials.CreateSolidColorMaterial(Color.blue);
                if (material != null)
                    renderer.material = material;
            }
            
            // Remove collider
            if (hipSphere.TryGetComponent<Collider>(out var collider))
            {
                Destroy(collider);
            }
            
            hipSphere.SetActive(false);
            
            // Create path line renderer if enabled
            if (showPath)
            {
                var pathLineObject = new GameObject("HipPathLine");
                pathLine = pathLineObject.AddComponent<LineRenderer>();
                
                pathLine.material = DebugVisualizationMaterials.CreateLineMaterial(Color.cyan);
                pathLine.startColor = Color.cyan;
                pathLine.endColor = Color.cyan;
                pathLine.startWidth = 0.03f;
                pathLine.endWidth = 0.03f;
                pathLine.positionCount = 0;
                pathLine.useWorldSpace = true;
            }
        }

        /// <summary>
        /// Update visualization for current frame
        /// </summary>
        private void UpdatePlaybackFrame(HipFrame frame)
        {
            Vector3 worldPosition = Vector3.zero;
            bool hasHip = frame.hipJoint.IsValid;
            if (hasHip)
            {
                // Transform hip position to current coordinate system
                worldPosition = TransformRecordedPointToCurrentSpace(frame.hipJoint.position);
                if (Time.frameCount - lastPlaybackDebugFrame > 120)
                {
                    lastPlaybackDebugFrame = Time.frameCount;
                    UnityEngine.Debug.Log($"[BodyTrackingPlayer] Frame t={frame.timestamp:F2}s localHip={frame.hipJoint.position} -> worldHip={worldPosition}");
                }
            }
            
            if (showRecordedSkeleton && frame.HasSkeleton)
            {
                UpdateRecordedSkeleton(frame);
            }
            
            // Update hip sphere
            if (showVisualization && hipSphere != null && hasHip)
            {
                hipSphere.transform.position = worldPosition;
                hipSphere.SetActive(true);
            }
            
            // Update path line
            if (showPath && pathLine != null && hasHip)
            {
                UpdatePathLine(worldPosition);
            }
            
            // Update character position only when FBX playback is explicitly enabled.
            if (useFbxCharacterPlayback && hasHip && characterController != null && characterController.IsInitialized)
            {
                characterController.SetTargetHipPosition(worldPosition);
            }
        }

        /// <summary>
        /// Transform a recorded RouteRoot-local point to current world space via the live RouteRoot frame.
        /// </summary>
        private Vector3 TransformRecordedPointToCurrentSpace(Vector3 recordedPosition)
        {
            // Transform from recorded RouteRoot-local space to current world space (math unchanged).
            return currentReferenceFrame.TransformPoint(recordedPosition);
        }

        private void UpdateRecordedSkeleton(HipFrame frame)
        {
            if (!frame.HasSkeleton)
            {
                HideRecordedSkeleton();
                return;
            }

            if (frame.HasRecordedSkeleton)
                UpdateRecordedSkeletonFromSamples(frame);
            else
                UpdateRecordedSkeletonLegacy(frame);
        }

        private void UpdateRecordedSkeletonFromSamples(HipFrame frame)
        {
            int maxJointIndex = 0;
            for (int i = 0; i < frame.recordedJoints.Count; i++)
                maxJointIndex = Mathf.Max(maxJointIndex, frame.recordedJoints[i].jointIndex);

            EnsureSkeletonObjects(maxJointIndex + 1);
            HideRecordedSkeleton();

            for (int i = 0; i < frame.recordedJoints.Count; i++)
            {
                RecordedJointSample joint = frame.recordedJoints[i];
                if (!joint.isTracked || joint.jointIndex < 0 || joint.jointIndex >= skeletonJointSpheres.Length)
                    continue;

                Vector3 jointWorldPosition = TransformRecordedPointToCurrentSpace(joint.positionReference);
                GameObject jointSphere = skeletonJointSpheres[joint.jointIndex];
                if (jointSphere != null)
                {
                    jointSphere.transform.position = jointWorldPosition;
                    jointSphere.SetActive(true);
                }

                LineRenderer boneLine = skeletonBoneLines[joint.jointIndex];
                if (boneLine == null || joint.parentIndex < 0)
                    continue;

                if (TryGetRecordedJointSample(frame, joint.parentIndex, out RecordedJointSample parentJoint))
                {
                    boneLine.gameObject.SetActive(true);
                    boneLine.SetPosition(0, TransformRecordedPointToCurrentSpace(parentJoint.positionReference));
                    boneLine.SetPosition(1, jointWorldPosition);
                }
            }
        }

        private void UpdateRecordedSkeletonLegacy(HipFrame frame)
        {
            if (frame.skeletonJoints == null)
            {
                HideRecordedSkeleton();
                return;
            }

            int maxJointIndex = 0;
            for (int i = 0; i < frame.skeletonJoints.Count; i++)
            {
                maxJointIndex = Mathf.Max(maxJointIndex, frame.skeletonJoints[i].jointIndex);
            }

            EnsureSkeletonObjects(maxJointIndex + 1);
            HideRecordedSkeleton();

            for (int i = 0; i < frame.skeletonJoints.Count; i++)
            {
                SkeletonJointData joint = frame.skeletonJoints[i];
                if (!joint.isTracked || joint.jointIndex < 0 || joint.jointIndex >= skeletonJointSpheres.Length)
                    continue;

                Vector3 jointWorldPosition = TransformRecordedPointToCurrentSpace(joint.position);
                GameObject jointSphere = skeletonJointSpheres[joint.jointIndex];
                if (jointSphere != null)
                {
                    jointSphere.transform.position = jointWorldPosition;
                    jointSphere.SetActive(true);
                }

                LineRenderer boneLine = skeletonBoneLines[joint.jointIndex];
                if (boneLine == null || joint.parentIndex < 0)
                    continue;

                if (TryGetRecordedJoint(frame, joint.parentIndex, out SkeletonJointData parentJoint))
                {
                    boneLine.gameObject.SetActive(true);
                    boneLine.SetPosition(0, TransformRecordedPointToCurrentSpace(parentJoint.position));
                    boneLine.SetPosition(1, jointWorldPosition);
                }
            }
        }

        private bool TryGetRecordedJointSample(HipFrame frame, int jointIndex, out RecordedJointSample jointData)
        {
            for (int i = 0; i < frame.recordedJoints.Count; i++)
            {
                if (frame.recordedJoints[i].jointIndex == jointIndex && frame.recordedJoints[i].isTracked)
                {
                    jointData = frame.recordedJoints[i];
                    return true;
                }
            }

            jointData = default;
            return false;
        }

        private bool TryGetRecordedJoint(HipFrame frame, int jointIndex, out SkeletonJointData jointData)
        {
            if (frame.skeletonJoints == null)
            {
                jointData = default;
                return false;
            }

            for (int i = 0; i < frame.skeletonJoints.Count; i++)
            {
                if (frame.skeletonJoints[i].jointIndex == jointIndex && frame.skeletonJoints[i].isTracked)
                {
                    jointData = frame.skeletonJoints[i];
                    return true;
                }
            }

            jointData = default;
            return false;
        }

        private void EnsureSkeletonObjects(int jointCount)
        {
            if (jointCount <= 0) return;

            if (skeletonRoot == null)
            {
                skeletonRoot = new GameObject("RecordedSkeletonPlayback");
                // Parent the ghost under the RouteRoot (Immersal XR Space child) so SDK scene updates keep
                // it aligned. Joint world positions are still set explicitly, so parenting is organizational.
                if (routeRootProvider != null && routeRootProvider.RouteRoot != null)
                    skeletonRoot.transform.SetParent(routeRootProvider.RouteRoot, false);
            }

            if (skeletonJointMaterial == null)
                skeletonJointMaterial = DebugVisualizationMaterials.CreateSolidColorMaterial(skeletonJointColor);

            if (skeletonBoneMaterial == null)
            {
                var c = skeletonBoneColor;
                c.a = BoneOpacity;
                skeletonBoneMaterial = DebugVisualizationMaterials.CreateLineMaterial(c);
            }

            if (skeletonJointSpheres != null && skeletonJointSpheres.Length >= jointCount)
                return;

            ClearRecordedSkeletonObjects();

            skeletonJointSpheres = new GameObject[jointCount];
            skeletonBoneLines = new LineRenderer[jointCount];

            for (int i = 0; i < jointCount; i++)
            {
                GameObject jointSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                jointSphere.name = $"RecordedSkeletonJoint_{i}";
                jointSphere.transform.SetParent(skeletonRoot.transform);
                jointSphere.transform.localScale = Vector3.one * skeletonJointRadius;
                jointSphere.SetActive(false);

                Renderer renderer = jointSphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = skeletonJointMaterial;
                }

                Collider collider = jointSphere.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                skeletonJointSpheres[i] = jointSphere;

                GameObject boneObject = new GameObject($"RecordedSkeletonBone_{i}");
                boneObject.transform.SetParent(skeletonRoot.transform);
                LineRenderer line = boneObject.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.startWidth = skeletonBoneLineWidth;
                line.endWidth = skeletonBoneLineWidth;
                line.material = skeletonBoneMaterial;
                var lineColor = skeletonBoneColor;
                lineColor.a = BoneOpacity;
                line.startColor = lineColor;
                line.endColor = lineColor;
                boneObject.SetActive(false);
                skeletonBoneLines[i] = line;
            }
        }

        /// <summary>
        /// Update the path line with new position
        /// </summary>
        private void UpdatePathLine(Vector3 newPosition)
        {
            if (pathLine.positionCount >= maxPathPoints)
            {
                // Shift points back to make room for new point
                Vector3[] positions = new Vector3[pathLine.positionCount];
                pathLine.GetPositions(positions);
                
                for (int i = 0; i < positions.Length - 1; i++)
                {
                    positions[i] = positions[i + 1];
                }
                positions[positions.Length - 1] = newPosition;
                pathLine.SetPositions(positions);
            }
            else
            {
                // Add new point
                pathLine.positionCount++;
                pathLine.SetPosition(pathLine.positionCount - 1, newPosition);
            }
        }

        /// <summary>
        /// Hide visualization elements
        /// </summary>
        private void HideVisualization()
        {
            if (hipSphere != null)
            {
                hipSphere.SetActive(false);
            }
            
            if (pathLine != null)
            {
                pathLine.positionCount = 0;
            }

            HideRecordedSkeleton();
        }

        private void HideRecordedSkeleton()
        {
            if (skeletonJointSpheres != null)
            {
                for (int i = 0; i < skeletonJointSpheres.Length; i++)
                {
                    if (skeletonJointSpheres[i] != null)
                    {
                        skeletonJointSpheres[i].SetActive(false);
                    }
                }
            }

            if (skeletonBoneLines != null)
            {
                for (int i = 0; i < skeletonBoneLines.Length; i++)
                {
                    if (skeletonBoneLines[i] != null)
                    {
                        skeletonBoneLines[i].gameObject.SetActive(false);
                    }
                }
            }
        }

        private void ClearRecordedSkeletonObjects()
        {
            if (skeletonJointSpheres != null)
            {
                for (int i = 0; i < skeletonJointSpheres.Length; i++)
                {
                    if (skeletonJointSpheres[i] != null)
                    {
                        Destroy(skeletonJointSpheres[i]);
                    }
                }
            }

            if (skeletonBoneLines != null)
            {
                for (int i = 0; i < skeletonBoneLines.Length; i++)
                {
                    if (skeletonBoneLines[i] != null)
                    {
                        Destroy(skeletonBoneLines[i].gameObject);
                    }
                }
            }
        }

        void OnDestroy()
        {
            // Clean up visualization
            if (hipSphere != null)
            {
                Destroy(hipSphere);
            }
            
            if (pathLine != null)
            {
                Destroy(pathLine.gameObject);
            }

            ClearRecordedSkeletonObjects();
            if (skeletonRoot != null)
            {
                Destroy(skeletonRoot);
            }
        }
    }
} 