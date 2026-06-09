using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using BodyTracking.Data;
using BodyTracking.Animation;
using BodyTracking.Spatial;
using BodyTracking.Utils;
using System;
using System.Collections.Generic;

namespace BodyTracking.Recording
{
    /// <summary>
    /// Records body pose (hip + full skeleton) into the stable RouteRoot frame. The actual skeleton comes from
    /// <see cref="IBodyPoseSource"/> (ARKit human body tracking) without changing RouteRoot-local recording math
    /// or the v3 file format.
    /// </summary>
    public class BodyTrackingRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [SerializeField] private float targetFrameRate = 30f;
        [SerializeField] private bool showVisualization = false;

        // ARKit 3D skeleton: 0=Root, 1=Hips (pelvis center), 2=LeftUpLeg. Use Hips, not LeftUpLeg, so the
        // recorded hip matches Move AI's pelvis Root for fusion alignment.
        [SerializeField] private int preferredHipJointIndex = 1;

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

        // Dependencies
        private ARHumanBodyManager bodyManager;
        // Supplies the stable wall frame (RouteRoot) recordings are stored relative to.
        private IRouteRootProvider routeRootProvider;
        private CoordinateFrame referenceFrame;
        // The concrete provider (Immersal or image target) locked in at StartRecording so a RouteRootManager
        // provider switch mid-recording can't corrupt stored coordinates.
        private IRouteRootProvider activeRecordingProvider;

        // Pose source (ARKit)
        private ARKitBodyPoseSource arkitSource;
        private IBodyPoseSource activeSource;
        private readonly List<BodyPoseJoint> frameJoints = new List<BodyPoseJoint>(96);

        // Recording state
        private HipRecording currentRecording;
        private bool isRecording = false;
        private float recordingStartTime;
        private float nextRecordTime;
        private bool warnedNoJointsThisSession;

        // Visualization
        private GameObject hipVisualizationSphere;
        private GameObject skeletonRoot;
        private GameObject[] jointSpheres;
        private LineRenderer[] boneLines;
        private Vector3[] jointWorldByIndex;
        private bool[] jointPresentByIndex;
        private int[] jointParentByIndex;
        private Material jointMaterial;
        private Material boneMaterial;
        private const float BoneOpacity = 0.7f;

        // Events
        public event System.Action<HipRecording> OnRecordingComplete;
        public event System.Action<float> OnRecordingProgress;

        // Public properties
        public bool IsRecording => isRecording;
        public float RecordingDuration => isRecording ? Time.time - recordingStartTime : 0f;
        public HipRecording LastRecording => currentRecording;
        public string ActiveSourceName => activeSource != null ? activeSource.SourceName : "none";
        public bool HasTrackedBody => activeSource != null && activeSource.HasTrackedBody;
        public int LastTrackedBodyCount => activeSource != null ? activeSource.TrackedBodyCount : 0;
        public int LastTrackedJointCount => activeSource != null ? activeSource.TrackedJointCount : 0;

        /// <summary>Force a pose poll this frame so <see cref="HasTrackedBody"/> is current (used while arming).</summary>
        public void PollBodyDetection()
        {
            if (activeSource != null)
                activeSource.TryGetCurrentPose(null, out _, out _);
        }

        /// <summary>
        /// Show/hide the live joint+bone skeleton overlay (the green "dots") drawn while recording. Used by the
        /// clean-view toggle so only the final character remains. Recording/joint capture is unaffected.
        /// </summary>
        public void SetSkeletonVisible(bool visible)
        {
            showSkeletonVisualization = visible;
            if (!visible)
            {
                HideSkeletonVisualization();
                if (hipVisualizationSphere != null)
                    hipVisualizationSphere.SetActive(false);
            }
        }

        /// <summary>
        /// Initialize the recorder. <paramref name="humanBodyManager"/> backs ARKit body tracking.
        /// <paramref name="provider"/> supplies the RouteRoot frame recordings are stored relative to.
        /// </summary>
        public bool Initialize(ARHumanBodyManager humanBodyManager, IRouteRootProvider provider)
        {
            bodyManager = humanBodyManager;
            routeRootProvider = provider;

            if (routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
                UnityEngine.Debug.LogError("[BodyTrackingRecorder] RouteRoot provider (with a RouteRoot transform) is required");
                return false;
            }

            BuildPoseSources();
            if (activeSource == null)
            {
                UnityEngine.Debug.LogError("[BodyTrackingRecorder] No pose source available — ARHumanBodyManager is required");
                return false;
            }

            // Store reference frame for coordinate transformations
            referenceFrame = new CoordinateFrame(routeRootProvider.RouteRoot);

            // Setup character controller integration
            SetupCharacterController();

            UnityEngine.Debug.Log($"[BodyTrackingRecorder] Initialized - source: {activeSource.SourceName}, showSkeletonVisualization: {showSkeletonVisualization}, targetFrameRate: {targetFrameRate}");
            return true;
        }

        private void BuildPoseSources()
        {
            arkitSource = bodyManager != null
                ? new ARKitBodyPoseSource(bodyManager, preferredHipJointIndex)
                : null;
            activeSource = arkitSource;
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
        /// Start recording body pose
        /// </summary>
        public bool StartRecording()
        {
            if (isRecording)
            {
                UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] Already recording");
                return false;
            }

            if (activeSource == null || routeRootProvider == null || routeRootProvider.RouteRoot == null)
            {
                UnityEngine.Debug.LogError("[BodyTrackingRecorder] Not properly initialized");
                return false;
            }

            warnedNoJointsThisSession = false;

            // Lock to the concrete active provider for this whole session.
            activeRecordingProvider = ResolveConcreteProvider();

            // Update reference frame at recording start from the current RouteRoot pose.
            referenceFrame = new CoordinateFrame(activeRecordingProvider.RouteRoot);

            string spatialSource = activeRecordingProvider.Source == SpatialSourceType.Immersal ? "immersal" : "imageTarget";

            // Initialize recording (format v3: RouteRoot-local samples + map/route metadata).
            currentRecording = new HipRecording
            {
                recordingFormatVersion = 3,
                frameRate = targetFrameRate,
                referenceImageTargetPosition = referenceFrame.position,
                referenceImageTargetRotation = referenceFrame.rotation,
                referenceImageTargetScale = referenceFrame.scale,
                mapId = routeRootProvider.MapId,
                routeId = routeRootProvider.RouteId,
                spatialSource = spatialSource,
                recordingTimestamp = DateTime.Now
            };

            isRecording = true;
            recordingStartTime = Time.time;
            nextRecordTime = 0f;

            return true;
        }

        /// <summary>
        /// Resolve the concrete provider behind the (possibly manager) RouteRoot provider, so recording is
        /// pinned to one real anchor (Immersal or image target) instead of a manager that can switch.
        /// </summary>
        private IRouteRootProvider ResolveConcreteProvider()
        {
            if (routeRootProvider is RouteRootManager manager && manager.ActiveProvider != null)
                return manager.ActiveProvider;
            return routeRootProvider;
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

            if (hipVisualizationSphere != null)
            {
                hipVisualizationSphere.SetActive(false);
            }

            LogRecordedDepthSpread();

            OnRecordingComplete?.Invoke(currentRecording);
            return currentRecording;
        }

        /// <summary>
        /// Diagnostic: report the recorded RouteRoot-local bounds across the whole clip. The Z (depth, out from
        /// the wall) spread is the key number — a tiny zSpan means the recorded skeleton genuinely has little
        /// depth (e.g. ARKit produced near-flat data or the climber was flat against the wall), which is what
        /// makes the blue review skeleton look "at the same depth". A healthy clip should show several cm to
        /// tens of cm of zSpan.
        /// </summary>
        private void LogRecordedDepthSpread()
        {
            if (currentRecording == null || currentRecording.frames == null)
                return;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int samples = 0;

            foreach (var frame in currentRecording.frames)
            {
                if (frame.recordedJoints == null) continue;
                foreach (var j in frame.recordedJoints)
                {
                    if (j == null || !j.isTracked) continue;
                    Vector3 p = j.positionReference;
                    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                    if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
                    samples++;
                }
            }

            if (samples == 0)
            {
                UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] Recorded 0 tracked joints — nothing to place in space.");
                return;
            }

            UnityEngine.Debug.Log(
                $"[BodyTrackingRecorder] Recorded RouteRoot-local spread over {samples} joint samples: " +
                $"xSpan={(maxX - minX):F2}m ySpan={(maxY - minY):F2}m zSpan(depth)={(maxZ - minZ):F2}m " +
                $"(z range {minZ:F2}..{maxZ:F2}). Low zSpan = little real depth in the recording.");
        }

        void Update()
        {
            if (activeSource == null)
            {
                HideSkeletonVisualization();
                return;
            }

            bool shouldSamplePose = isRecording || (showSkeletonVisualization && showSkeletonWhenNotRecording);
            if (!shouldSamplePose)
            {
                // Avoid scanning ARKit body trackables while idle; arming refreshes detection explicitly.
                HideSkeletonVisualization();
                return;
            }

            // Sample the current pose once per frame and reuse it for both visualization and recording so the
            // stored data exactly matches what was shown.
            bool foundBody = activeSource.TryGetCurrentPose(frameJoints, out Vector3 hipWorld, out bool hipTracked);

            if (showSkeletonVisualization && (isRecording || showSkeletonWhenNotRecording))
            {
                if (foundBody)
                    UpdateSkeletonVisualization(frameJoints);
                else
                    HideSkeletonVisualization();
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
                RecordCurrentFrame(currentTime, foundBody, hipWorld, hipTracked);
                nextRecordTime += 1f / targetFrameRate;
                OnRecordingProgress?.Invoke(currentTime);
            }
        }

        /// <summary>
        /// Record the current frame's pose into the live RouteRoot frame.
        /// </summary>
        private void RecordCurrentFrame(float timestamp, bool foundBody, Vector3 hipWorld, bool hipTracked)
        {
            var frame = new HipFrame
            {
                timestamp = timestamp,
                hipJoint = HipJointData.Invalid,
                skeletonJoints = null,
                recordedJoints = new List<RecordedJointSample>()
            };

            bool foundValidJoint = false;

            // Sample the RouteRoot pose live for this frame (matches playback, survives Immersal re-anchoring).
            // Use the provider locked in at StartRecording so the frame can't switch anchors mid-recording.
            var frameProvider = activeRecordingProvider ?? routeRootProvider;
            CoordinateFrame liveFrame = (frameProvider != null && frameProvider.RouteRoot != null)
                ? new CoordinateFrame(frameProvider.RouteRoot)
                : referenceFrame;

            if (foundBody)
            {
                // Store every tracked joint in RouteRoot-local space exactly as ARKit reported it (raw, no
                // smoothing, no depth projection). Preserving the true out-from-wall depth (Z) is what makes the
                // recorded (blue) review skeleton sit where it was actually recorded instead of flattened onto a
                // plane. The flat-wall slab prior, when wanted, is applied downstream (WallProjectionResolver on
                // the fused character at playback), never destructively baked into the source recording.
                for (int i = 0; i < frameJoints.Count; i++)
                {
                    var j = frameJoints[i];
                    Vector3 rawReference = liveFrame.InverseTransformPoint(j.worldPosition);
                    frame.recordedJoints.Add(new RecordedJointSample(j.jointIndex, j.parentIndex, rawReference, j.isTracked));
                }

                if (hipTracked)
                {
                    Vector3 referencePosition = liveFrame.InverseTransformPoint(hipWorld);
                    frame.hipJoint = new HipJointData(referencePosition, 1.0f, true);
                    foundValidJoint = true;

                    if (showVisualization)
                    {
                        Vector3 rawWorld = liveFrame.TransformPoint(referencePosition);
                        UpdateVisualization(rawWorld);
                    }
                }
            }

            if (!foundValidJoint && !frame.HasSkeleton && !warnedNoJointsThisSession)
            {
                warnedNoJointsThisSession = true;
                UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] No body/hip tracked yet — keep climber in frame (landscape helps).");
            }

            // Always add frame (even if no body detected) to maintain timing
            currentRecording.frames.Add(frame);
        }

        /// <summary>
        /// Update hip visualization and character positioning
        /// </summary>
        private void UpdateVisualization(Vector3 worldPosition)
        {
            if (hipVisualizationSphere == null)
            {
                hipVisualizationSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                hipVisualizationSphere.name = "HipVisualization_AR";
                hipVisualizationSphere.transform.localScale = Vector3.one * 0.3f;

                var xrOrigin = FindFirstObjectByType<XROrigin>();
                if (xrOrigin != null)
                {
                    hipVisualizationSphere.transform.SetParent(xrOrigin.transform);
                }

                var renderer = hipVisualizationSphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var material = DebugVisualizationMaterials.CreateSolidColorMaterial(Color.red);
                    if (material != null)
                        renderer.material = material;
                }

                var collider = hipVisualizationSphere.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }
            }

            hipVisualizationSphere.transform.position = worldPosition;
            hipVisualizationSphere.SetActive(true);

            if (driveCharacterDuringRecording && characterController != null && characterController.IsInitialized)
            {
                characterController.SetTargetHipPosition(worldPosition);
            }
        }

        /// <summary>
        /// Draw the current frame's skeleton from a generic joint list (works for any pose source). Joints are
        /// keyed by their topology index so bones can connect to their parent regardless of which source emitted
        /// them.
        /// </summary>
        private void UpdateSkeletonVisualization(List<BodyPoseJoint> joints)
        {
            if (joints == null || joints.Count == 0)
            {
                HideSkeletonVisualization();
                return;
            }

            int capacity = 0;
            for (int i = 0; i < joints.Count; i++)
            {
                capacity = Mathf.Max(capacity, joints[i].jointIndex + 1);
                if (joints[i].parentIndex >= 0)
                    capacity = Mathf.Max(capacity, joints[i].parentIndex + 1);
            }
            if (capacity == 0)
            {
                HideSkeletonVisualization();
                return;
            }

            EnsureSkeletonObjects(capacity);

            for (int i = 0; i < jointPresentByIndex.Length; i++)
                jointPresentByIndex[i] = false;

            for (int i = 0; i < joints.Count; i++)
            {
                var j = joints[i];
                if (j.jointIndex < 0 || j.jointIndex >= jointPresentByIndex.Length)
                    continue;
                jointPresentByIndex[j.jointIndex] = true;
                jointWorldByIndex[j.jointIndex] = j.worldPosition;
                jointParentByIndex[j.jointIndex] = j.parentIndex;
            }

            for (int i = 0; i < jointSpheres.Length; i++)
            {
                bool visible = i < jointPresentByIndex.Length && jointPresentByIndex[i];

                if (jointSpheres[i] != null)
                {
                    jointSpheres[i].SetActive(visible);
                    if (visible)
                        jointSpheres[i].transform.position = jointWorldByIndex[i];
                }

                if (boneLines[i] != null)
                {
                    int parentIndex = visible ? jointParentByIndex[i] : -1;
                    bool lineVisible = visible &&
                                       parentIndex >= 0 &&
                                       parentIndex < jointPresentByIndex.Length &&
                                       jointPresentByIndex[parentIndex];

                    boneLines[i].gameObject.SetActive(lineVisible);
                    if (lineVisible)
                    {
                        boneLines[i].SetPosition(0, jointWorldByIndex[parentIndex]);
                        boneLines[i].SetPosition(1, jointWorldByIndex[i]);
                    }
                }
            }
        }

        private void EnsureSkeletonObjects(int jointCount)
        {
            if (skeletonRoot == null)
            {
                skeletonRoot = new GameObject("BodyPoseSkeletonVisualization");
                var xrOrigin = FindFirstObjectByType<XROrigin>();
                if (xrOrigin != null)
                {
                    skeletonRoot.transform.SetParent(xrOrigin.transform);
                }
            }

            if (jointMaterial == null)
                jointMaterial = DebugVisualizationMaterials.CreateSolidColorMaterial(trackedJointColor);

            if (boneMaterial == null)
            {
                var c = boneColor;
                c.a = BoneOpacity;
                boneMaterial = DebugVisualizationMaterials.CreateLineMaterial(c);
            }

            if (jointSpheres != null && jointSpheres.Length >= jointCount)
                return;

            ClearSkeletonObjects();

            jointSpheres = new GameObject[jointCount];
            boneLines = new LineRenderer[jointCount];
            jointWorldByIndex = new Vector3[jointCount];
            jointPresentByIndex = new bool[jointCount];
            jointParentByIndex = new int[jointCount];

            for (int i = 0; i < jointCount; i++)
            {
                var jointSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                jointSphere.name = $"Joint_{i}";
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

                var boneObject = new GameObject($"Bone_{i}");
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
                        jointSpheres[i].SetActive(false);
                }
            }

            if (boneLines != null)
            {
                for (int i = 0; i < boneLines.Length; i++)
                {
                    if (boneLines[i] != null)
                        boneLines[i].gameObject.SetActive(false);
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
                        Destroy(jointSpheres[i]);
                }
            }

            if (boneLines != null)
            {
                for (int i = 0; i < boneLines.Length; i++)
                {
                    if (boneLines[i] != null)
                        Destroy(boneLines[i].gameObject);
                }
            }
        }

        void OnDestroy()
        {
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
