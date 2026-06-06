using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;
using BodyTracking.Data;
using BodyTracking.Animation;
using System;
using System.Collections.Generic;

namespace BodyTracking.Recording
{
    /// <summary>
    /// Handles recording of AR hip joint position with proper coordinate system management
    /// </summary>
    public class BodyTrackingRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [SerializeField] private float targetFrameRate = 30f;
        [SerializeField] private bool showVisualization = false;
        [SerializeField] private int preferredHipJointIndex = 2;

        [Header("Skeleton Visualization")]
        [SerializeField] private bool showSkeletonVisualization = true;
        [SerializeField] private bool showSkeletonWhenNotRecording = false;
        [SerializeField] private float jointRadius = 0.035f;
        [SerializeField] private float boneLineWidth = 0.03f;
        [SerializeField] private Color trackedJointColor = Color.green;
        [SerializeField] private Color boneColor = new Color(1f, 1f, 0f, 0.7f);
        
        [Header("Character Integration")]
        [SerializeField] private FBXCharacterController characterController;
        [SerializeField] private bool autoFindCharacterController = false;
        [SerializeField] private bool driveCharacterDuringRecording = false;

        [Header("Joint smoothing (before save, format v2)")]
        [SerializeField] private bool enableJointSmoothingBeforeSave = true;
        [Tooltip("0 = no smoothing (raw tracking), 1 = strongest damping.")]
        [Range(0f, 1f)]
        [SerializeField] private float jointSmoothingStrength = 0.55f;
        [Tooltip("Per-joint speed in m/s (reference space) above which extra smoothing is applied.")]
        [SerializeField] private float jointHighVelocityMetersPerSecond = 1.25f;
        [Tooltip("Multiplier on blend toward raw when over the velocity threshold (lower = calmer fast motion).")]
        [Range(0.02f, 1f)]
        [SerializeField] private float highVelocitySmoothingScale = 0.15f;
        
        // Dependencies
        private ARHumanBodyManager bodyManager;
        private Transform imageTargetTransform;
        private CoordinateFrame referenceFrame;
        
        // Recording state
        private HipRecording currentRecording;
        private bool isRecording = false;
        private float recordingStartTime;
        private float nextRecordTime;
        
        // Visualization
        private GameObject hipVisualizationSphere;
        private GameObject skeletonRoot;
        private GameObject[] jointSpheres;
        private LineRenderer[] boneLines;
        private Material jointMaterial;
        private Material boneMaterial;
        private const float BoneOpacity = 0.7f;

        // Body tracking diagnostics
        private bool hasTrackedBody = false;
        private int lastTrackedBodyCount = 0;
        private int lastTrackedJointCount = 0;

        // Smoothed joint positions in reference space (recording), keyed by XR joint index
        private readonly Dictionary<int, Vector3> jointSmoothedReference = new Dictionary<int, Vector3>(64);
        private float lastRecordedTimestamp = -1f;
        private Vector3 lastHipSmoothedReference;
        private bool hasSmoothedHipSample;
        
        // Events
        public event System.Action<HipRecording> OnRecordingComplete;
        public event System.Action<float> OnRecordingProgress;
        
        // Public properties
        public bool IsRecording => isRecording;
        public float RecordingDuration => isRecording ? Time.time - recordingStartTime : 0f;
        public HipRecording LastRecording => currentRecording;
        public bool HasTrackedBody => hasTrackedBody;
        public int LastTrackedBodyCount => lastTrackedBodyCount;
        public int LastTrackedJointCount => lastTrackedJointCount;

        /// <summary>
        /// Initialize the recorder with required dependencies
        /// </summary>
        public bool Initialize(ARHumanBodyManager humanBodyManager, Transform imageTarget)
        {
            bodyManager = humanBodyManager;
            imageTargetTransform = imageTarget;
            
            if (bodyManager == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingRecorder] ARHumanBodyManager is required");
                return false;
            }
            
            if (imageTargetTransform == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingRecorder] Image target transform is required");
                return false;
            }
            
            // Store reference frame for coordinate transformations
            referenceFrame = new CoordinateFrame(imageTargetTransform);
            
            // Setup character controller integration
            SetupCharacterController();
            
           UnityEngine.Debug.Log($"[BodyTrackingRecorder] Initialized - showVisualization: {showVisualization}, showSkeletonVisualization: {showSkeletonVisualization}, targetFrameRate: {targetFrameRate}");
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
                   UnityEngine.Debug.Log("[BodyTrackingRecorder] Found FBXCharacterController automatically");
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
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError($"[BodyTrackingRecorder] Character controller init failed (recording can continue): {e.Message}");
                    }
                }
               UnityEngine.Debug.Log("[BodyTrackingRecorder] Character controller integration enabled");
            }
            else
            {
               UnityEngine.Debug.Log("[BodyTrackingRecorder] No character controller found - hip tracking only");
            }
        }

        /// <summary>
        /// Start recording hip joint position
        /// </summary>
        public bool StartRecording()
        {
            if (isRecording)
            {
               UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] Already recording");
                return false;
            }
            
            if (bodyManager == null || imageTargetTransform == null)
            {
               UnityEngine.Debug.LogError("[BodyTrackingRecorder] Not properly initialized");
                return false;
            }
            
            // Update reference frame at recording start
            referenceFrame = new CoordinateFrame(imageTargetTransform);
            
            // Initialize recording
            currentRecording = new HipRecording
            {
                recordingFormatVersion = 2,
                frameRate = targetFrameRate,
                referenceImageTargetPosition = referenceFrame.position,
                referenceImageTargetRotation = referenceFrame.rotation,
                referenceImageTargetScale = referenceFrame.scale,
                recordingTimestamp = DateTime.Now
            };

            jointSmoothedReference.Clear();
            lastRecordedTimestamp = -1f;
            hasSmoothedHipSample = false;
            
            isRecording = true;
            recordingStartTime = Time.time;
            nextRecordTime = 0f;
            
            return true;
        }

        /// <summary>
        /// Stop recording and return the recorded data
        /// </summary>
        public HipRecording StopRecording()
        {
            if (!isRecording)
            {
               UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] Not currently recording");
                return null;
            }
            
            isRecording = false;
            currentRecording.duration = Time.time - recordingStartTime;
            
            // Hide visualization
            if (hipVisualizationSphere != null)
            {
                hipVisualizationSphere.SetActive(false);
            }
            
            OnRecordingComplete?.Invoke(currentRecording);
            return currentRecording;
        }

        void Update()
        {
            if (bodyManager == null)
            {
                HideSkeletonVisualization();
                return;
            }

            ARHumanBody trackedBody;
            bool foundBody = TryGetBestTrackedBody(out trackedBody);
            if (showSkeletonVisualization && (isRecording || showSkeletonWhenNotRecording))
            {
                if (foundBody)
                {
                    UpdateSkeletonVisualization(trackedBody);
                }
                else
                {
                    HideSkeletonVisualization();
                }
            }
            else
            {
                HideSkeletonVisualization();
            }

            if (!isRecording) return;
            
            float currentTime = Time.time - recordingStartTime;
            
            // Record at target frame rate
            if (currentTime >= nextRecordTime)
            {
                RecordCurrentFrame(currentTime, trackedBody);
                nextRecordTime += 1f / targetFrameRate;
                
                // Notify progress
                OnRecordingProgress?.Invoke(currentTime);
            }
        }

        /// <summary>
        /// Record the current frame's hip position
        /// </summary>
        private void RecordCurrentFrame(float timestamp, ARHumanBody trackedBody)
        {
            float dt = 1f / Mathf.Max(0.001f, targetFrameRate);
            if (lastRecordedTimestamp >= 0f)
                dt = Mathf.Max(timestamp - lastRecordedTimestamp, 1e-5f);
            lastRecordedTimestamp = timestamp;

            var frame = new HipFrame
            {
                timestamp = timestamp,
                hipJoint = HipJointData.Invalid,
                skeletonJoints = null,
                recordedJoints = new List<RecordedJointSample>()
            };
            
            bool foundValidJoint = false;

            if (trackedBody != null)
            {
                var joints = trackedBody.joints;
                RecordSmoothedRecordedJoints(trackedBody, joints, frame.recordedJoints, dt);
                
                // ARKit joint anchorPose is in human-body anchor space; localPose is parent-joint space.
                Vector3? bestJointPosition = GetBestHipJointAnchorPosition(joints);

                if (bestJointPosition.HasValue)
                {
                    // Transform from AR session space to world space  
                    Vector3 worldPosition = trackedBody.transform.TransformPoint(bestJointPosition.Value);
                    
                    // Transform to reference frame space
                    Vector3 referencePosition = referenceFrame.InverseTransformPoint(worldPosition);
                    Vector3 smoothedReference = SmoothHipReference(referencePosition, dt);
                    
                    frame.hipJoint = new HipJointData(
                        smoothedReference,
                        1.0f, // Could use actual confidence if available
                        true
                    );
                    
                    foundValidJoint = true;
                    
                    // Update visualization
                    if (showVisualization)
                    {
                        Vector3 smoothedWorld = referenceFrame.TransformPoint(smoothedReference);
                        UpdateVisualization(smoothedWorld);
                    }
                }
            }
            
            // Debug logging for tracking issues
            if (!foundValidJoint && !frame.HasSkeleton && currentRecording.FrameCount % 30 == 0)
            {
               UnityEngine.Debug.LogWarning($"[BodyTrackingRecorder] No valid joint data found at frame {currentRecording.FrameCount}");
            }
            
            // Always add frame (even if no body detected) to maintain timing
            currentRecording.frames.Add(frame);
        }

        private void RecordSmoothedRecordedJoints(ARHumanBody trackedBody, Unity.Collections.NativeArray<XRHumanBodyJoint> joints, List<RecordedJointSample> output, float dt)
        {
            if (!joints.IsCreated || output == null) return;

            for (int i = 0; i < joints.Length; i++)
            {
                XRHumanBodyJoint joint = joints[i];
                if (!IsUsableJoint(joint)) continue;

                Vector3 worldPosition = trackedBody.transform.TransformPoint(joint.anchorPose.position);
                Vector3 rawReference = referenceFrame.InverseTransformPoint(worldPosition);
                Vector3 smoothedReference = SmoothJointReference(i, rawReference, dt);
                output.Add(new RecordedJointSample(i, joint.parentIndex, smoothedReference, true));
            }
        }

        private Vector3 SmoothJointReference(int jointIndex, Vector3 rawReference, float dt)
        {
            if (!enableJointSmoothingBeforeSave || jointSmoothingStrength <= 0.001f)
            {
                jointSmoothedReference[jointIndex] = rawReference;
                return rawReference;
            }

            if (!jointSmoothedReference.TryGetValue(jointIndex, out Vector3 prev))
            {
                jointSmoothedReference[jointIndex] = rawReference;
                return rawReference;
            }

            float t = BlendTowardRaw();
            Vector3 delta = rawReference - prev;
            float speed = delta.magnitude / dt;
            if (speed > jointHighVelocityMetersPerSecond)
                t *= Mathf.Clamp01(highVelocitySmoothingScale);

            Vector3 smoothed = Vector3.Lerp(prev, rawReference, t);
            jointSmoothedReference[jointIndex] = smoothed;
            return smoothed;
        }

        private Vector3 SmoothHipReference(Vector3 rawReference, float dt)
        {
            if (!enableJointSmoothingBeforeSave || jointSmoothingStrength <= 0.001f)
            {
                lastHipSmoothedReference = rawReference;
                hasSmoothedHipSample = true;
                return rawReference;
            }

            if (!hasSmoothedHipSample)
            {
                lastHipSmoothedReference = rawReference;
                hasSmoothedHipSample = true;
                return rawReference;
            }

            float t = BlendTowardRaw();
            Vector3 delta = rawReference - lastHipSmoothedReference;
            float speed = delta.magnitude / dt;
            if (speed > jointHighVelocityMetersPerSecond)
                t *= Mathf.Clamp01(highVelocitySmoothingScale);

            lastHipSmoothedReference = Vector3.Lerp(lastHipSmoothedReference, rawReference, t);
            return lastHipSmoothedReference;
        }

        /// <summary>
        /// Returns Lerp factor toward raw sample: 1 = instant (no smooth), lower = heavier smoothing.
        /// </summary>
        private float BlendTowardRaw()
        {
            float smooth01 = Mathf.Clamp01(jointSmoothingStrength);
            if (smooth01 <= 0.001f)
                return 1f;
            float minBlendWhenSlow = Mathf.Lerp(0.42f, 0.025f, smooth01);
            return Mathf.Lerp(1f, minBlendWhenSlow, smooth01);
        }

        private bool TryGetBestTrackedBody(out ARHumanBody trackedBody)
        {
            trackedBody = null;
            hasTrackedBody = false;
            lastTrackedBodyCount = 0;
            lastTrackedJointCount = 0;

            foreach (var humanBody in bodyManager.trackables)
            {
                if (humanBody == null || humanBody.trackingState == TrackingState.None)
                    continue;

                var joints = humanBody.joints;
                if (!joints.IsCreated || joints.Length == 0)
                    continue;

                int trackedJointCount = CountTrackedJoints(joints);
                if (trackedJointCount == 0)
                    continue;

                lastTrackedBodyCount++;
                if (trackedBody == null || trackedJointCount > lastTrackedJointCount)
                {
                    trackedBody = humanBody;
                    lastTrackedJointCount = trackedJointCount;
                }
            }

            hasTrackedBody = trackedBody != null;
            return hasTrackedBody;
        }

        private int CountTrackedJoints(Unity.Collections.NativeArray<XRHumanBodyJoint> joints)
        {
            int count = 0;
            for (int i = 0; i < joints.Length; i++)
            {
                if (IsUsableJoint(joints[i]))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// Update hip visualization and character positioning
        /// </summary>
        private void UpdateVisualization(Vector3 worldPosition)
        {
            if (hipVisualizationSphere == null)
            {
                // Create red sphere for hip visualization
                hipVisualizationSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                hipVisualizationSphere.name = "HipVisualization_AR";
                hipVisualizationSphere.transform.localScale = Vector3.one * 0.3f;
                
                // Parent to XR Origin for proper AR sync
                var xrOrigin = FindObjectOfType<XROrigin>();
                if (xrOrigin != null)
                {
                    hipVisualizationSphere.transform.SetParent(xrOrigin.transform);
                   UnityEngine.Debug.Log("[BodyTrackingRecorder] Parented hip sphere to XR Origin");
                }
                
                // Make it bright red using AR-compatible shader
                var renderer = hipVisualizationSphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var material = new Material(Shader.Find("Unlit/Color"));
                    material.color = Color.red;
                    renderer.material = material;
                   UnityEngine.Debug.Log("[BodyTrackingRecorder] Applied red material to hip sphere");
                }
                
                // Remove collider
                var collider = hipVisualizationSphere.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
                
               UnityEngine.Debug.Log($"[BodyTrackingRecorder] Created hip visualization sphere at {worldPosition}");
            }
            
            hipVisualizationSphere.transform.position = worldPosition;
            hipVisualizationSphere.SetActive(true);
            
            // Update character position if controller is available
            if (driveCharacterDuringRecording && characterController != null && characterController.IsInitialized)
            {
                characterController.SetTargetHipPosition(worldPosition);
            }
            
            // Log sphere position every few frames to verify it's being updated
            if (Time.frameCount % 60 == 0)
            {
               UnityEngine.Debug.Log($"[BodyTrackingRecorder] Hip sphere at {worldPosition:F3}, active: {hipVisualizationSphere.activeInHierarchy}");
                if (driveCharacterDuringRecording && characterController != null)
                {
                   UnityEngine.Debug.Log($"[BodyTrackingRecorder] Character hip updated to {worldPosition:F3}");
                }
            }
        }

        private Vector3? GetBestHipJointAnchorPosition(Unity.Collections.NativeArray<XRHumanBodyJoint> joints)
        {
            if (preferredHipJointIndex >= 0 &&
                preferredHipJointIndex < joints.Length &&
                IsUsableJoint(joints[preferredHipJointIndex]))
            {
                return joints[preferredHipJointIndex].anchorPose.position;
            }

            for (int i = 0; i < joints.Length; i++)
            {
                if (IsUsableJoint(joints[i]))
                {
                    return joints[i].anchorPose.position;
                }
            }

            return null;
        }

        private bool IsUsableJoint(XRHumanBodyJoint joint)
        {
            return joint.tracked && joint.anchorPose.position != Vector3.zero;
        }

        private void UpdateSkeletonVisualization(ARHumanBody humanBody)
        {
            var joints = humanBody.joints;
            if (!joints.IsCreated || joints.Length == 0)
            {
                HideSkeletonVisualization();
                return;
            }

            EnsureSkeletonObjects(joints.Length);

            for (int i = 0; i < joints.Length; i++)
            {
                XRHumanBodyJoint joint = joints[i];
                bool jointVisible = IsUsableJoint(joint);

                if (jointSpheres[i] != null)
                {
                    jointSpheres[i].SetActive(jointVisible);
                    if (jointVisible)
                    {
                        jointSpheres[i].transform.position = humanBody.transform.TransformPoint(joint.anchorPose.position);
                    }
                }

                if (boneLines[i] != null)
                {
                    int parentIndex = joint.parentIndex;
                    bool lineVisible = jointVisible &&
                                       parentIndex >= 0 &&
                                       parentIndex < joints.Length &&
                                       IsUsableJoint(joints[parentIndex]);

                    boneLines[i].gameObject.SetActive(lineVisible);
                    if (lineVisible)
                    {
                        Vector3 jointPosition = humanBody.transform.TransformPoint(joint.anchorPose.position);
                        Vector3 parentPosition = humanBody.transform.TransformPoint(joints[parentIndex].anchorPose.position);
                        boneLines[i].SetPosition(0, parentPosition);
                        boneLines[i].SetPosition(1, jointPosition);
                    }
                }
            }
        }

        private void EnsureSkeletonObjects(int jointCount)
        {
            if (skeletonRoot == null)
            {
                skeletonRoot = new GameObject("ARKitSkeletonVisualization");
                var xrOrigin = FindObjectOfType<XROrigin>();
                if (xrOrigin != null)
                {
                    skeletonRoot.transform.SetParent(xrOrigin.transform);
                }
            }

            if (jointMaterial == null)
            {
                jointMaterial = new Material(Shader.Find("Unlit/Color"));
                jointMaterial.color = trackedJointColor;
            }

            if (boneMaterial == null)
            {
                boneMaterial = new Material(Shader.Find("Sprites/Default"));
                var c = boneColor;
                c.a = BoneOpacity;
                boneMaterial.color = c;
            }

            if (jointSpheres != null && jointSpheres.Length == jointCount)
                return;

            ClearSkeletonObjects();

            jointSpheres = new GameObject[jointCount];
            boneLines = new LineRenderer[jointCount];

            for (int i = 0; i < jointCount; i++)
            {
                var jointSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                jointSphere.name = $"ARKitJoint_{i}";
                jointSphere.transform.SetParent(skeletonRoot.transform);
                jointSphere.transform.localScale = Vector3.one * jointRadius;
                jointSphere.SetActive(false);

                var renderer = jointSphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material = jointMaterial;
                }

                var collider = jointSphere.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                jointSpheres[i] = jointSphere;

                var boneObject = new GameObject($"ARKitBone_{i}");
                boneObject.transform.SetParent(skeletonRoot.transform);
                var line = boneObject.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.startWidth = boneLineWidth;
                line.endWidth = boneLineWidth;
                line.material = boneMaterial;
                var lineColor = boneColor;
                lineColor.a = BoneOpacity;
                line.startColor = lineColor;
                line.endColor = lineColor;
                boneObject.SetActive(false);
                boneLines[i] = line;
            }
        }

        private void HideSkeletonVisualization()
        {
            if (jointSpheres != null)
            {
                for (int i = 0; i < jointSpheres.Length; i++)
                {
                    if (jointSpheres[i] != null)
                    {
                        jointSpheres[i].SetActive(false);
                    }
                }
            }

            if (boneLines != null)
            {
                for (int i = 0; i < boneLines.Length; i++)
                {
                    if (boneLines[i] != null)
                    {
                        boneLines[i].gameObject.SetActive(false);
                    }
                }
            }
        }

        private void ClearSkeletonObjects()
        {
            if (jointSpheres != null)
            {
                for (int i = 0; i < jointSpheres.Length; i++)
                {
                    if (jointSpheres[i] != null)
                    {
                        Destroy(jointSpheres[i]);
                    }
                }
            }

            if (boneLines != null)
            {
                for (int i = 0; i < boneLines.Length; i++)
                {
                    if (boneLines[i] != null)
                    {
                        Destroy(boneLines[i].gameObject);
                    }
                }
            }
        }

        void OnDestroy()
        {
            // Clean up visualization
            if (hipVisualizationSphere != null)
            {
                Destroy(hipVisualizationSphere);
            }
            ClearSkeletonObjects();
            if (skeletonRoot != null)
            {
                Destroy(skeletonRoot);
            }
        }
    }
} 