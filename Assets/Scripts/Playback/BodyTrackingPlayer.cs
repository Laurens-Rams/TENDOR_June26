using UnityEngine;
using BodyTracking.Data;
using BodyTracking.Animation;

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
        private Transform imageTargetTransform;
        private CoordinateFrame currentImageTargetFrame;
        
        // Playback state
        private HipRecording recording;
        private bool isPlaying = false;
        private float playbackStartTime;
        private float currentPlaybackTime = 0f;
        private int lastPlaybackDebugFrame = -9999;
        
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
        
        // Public properties
        public bool IsPlaying => isPlaying;
        public float PlaybackProgress => recording != null ? Mathf.Clamp01(currentPlaybackTime / recording.duration) : 0f;
        public float CurrentTime => currentPlaybackTime;
        public float Duration => recording?.duration ?? 0f;

        /// <summary>
        /// Re-bind the tracked image target (call when re-detecting the marker or before playback).
        /// </summary>
        public void SetImageTarget(Transform imageTarget)
        {
            if (imageTarget == null) return;
            imageTargetTransform = imageTarget;
            currentImageTargetFrame = new CoordinateFrame(imageTargetTransform);
        }

        /// <summary>
        /// Initialize the player with required dependencies
        /// </summary>
        public bool Initialize(Transform imageTarget)
        {
            imageTargetTransform = imageTarget;
            
            if (imageTargetTransform == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] Image target transform is required");
                return false;
            }
            
            currentImageTargetFrame = new CoordinateFrame(imageTargetTransform);
            
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
                characterController = FindObjectOfType<FBXCharacterController>();
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

            if (imageTargetTransform == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingPlayer] No playback reference transform. Wait for world-map relocalization or image target lock, then try again.");
                return;
            }
            
            // Map recorded reference-space points through this transform (image marker or world-map anchor).
            currentImageTargetFrame = new CoordinateFrame(imageTargetTransform);
            UnityEngine.Debug.Log($"[BodyTrackingPlayer] StartPlayback with reference pos={imageTargetTransform.position}, rot={imageTargetTransform.rotation.eulerAngles}, scale={imageTargetTransform.localScale}, duration={recording.duration:F2}s, frames={recording.FrameCount}");
            
            isPlaying = true;
            playbackStartTime = Time.time;
            currentPlaybackTime = 0f;
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

            // Draw the first recorded skeleton immediately instead of waiting for the next frame.
            var firstFrame = recording.GetFrameAtTime(0f);
            if (firstFrame.IsValid)
            {
                UpdatePlaybackFrame(firstFrame);
            }
        }

        /// <summary>
        /// Stop playback
        /// </summary>
        public void StopPlayback()
        {
            if (!isPlaying) return;
            
            isPlaying = false;
            
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
        /// Seek to specific time in the recording
        /// </summary>
        public void SeekToTime(float time)
        {
            if (recording == null) return;
            
            currentPlaybackTime = Mathf.Clamp(time, 0f, recording.duration);
            
            if (isPlaying)
            {
                playbackStartTime = Time.time - currentPlaybackTime / playbackSpeed;
            }
        }

        void Update()
        {
            if (!isPlaying || recording == null) return;
            
            // Update playback time
            float elapsedTime = (Time.time - playbackStartTime) * playbackSpeed;
            currentPlaybackTime = elapsedTime;
            
            // Handle looping
            if (currentPlaybackTime >= recording.duration)
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
            
            if (currentFrame.IsValid)
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
                var material = new Material(Shader.Find("Unlit/Color"));
                material.color = Color.blue;
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
                
                pathLine.material = new Material(Shader.Find("Sprites/Default"));
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
        /// Transform recorded hip position to current image target space
        /// </summary>
        private Vector3 TransformRecordedPointToCurrentSpace(Vector3 recordedPosition)
        {
            // Transform from recorded reference space to current world space
            return currentImageTargetFrame.TransformPoint(recordedPosition);
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
            }

            if (skeletonJointMaterial == null)
            {
                skeletonJointMaterial = new Material(Shader.Find("Unlit/Color"));
                skeletonJointMaterial.color = skeletonJointColor;
            }

            if (skeletonBoneMaterial == null)
            {
                skeletonBoneMaterial = new Material(Shader.Find("Sprites/Default"));
                var c = skeletonBoneColor;
                c.a = BoneOpacity;
                skeletonBoneMaterial.color = c;
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