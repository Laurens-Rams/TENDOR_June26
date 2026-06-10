using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;
using BodyTracking.AI;
using BodyTracking.Data;
using BodyTracking.Animation;
using BodyTracking.Spatial;
using BodyTracking.Utils;
using System;
using System.Collections.Generic;

namespace BodyTracking.Recording
{
    /// <summary>Which backend supplies the recorded body pose.</summary>
    public enum BodyPoseSourceMode
    {
        /// <summary>ARKit 3D human body tracking (ARHumanBodyManager).</summary>
        ARKit3D,
        /// <summary>BlazePose 2D landmarks + LiDAR environment depth (view-independent hip positioning).</summary>
        LidarHip
    }

    /// <summary>
    /// Records body pose (hip + full skeleton) into the stable RouteRoot frame. The actual skeleton comes from
    /// <see cref="IBodyPoseSource"/> (ARKit human body tracking, or BlazePose+LiDAR) without changing
    /// RouteRoot-local recording math or the v3 file format.
    /// </summary>
    public class BodyTrackingRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [SerializeField] private float targetFrameRate = 30f;
        [SerializeField] private bool showVisualization = false;

        [Header("Pose source")]
        [Tooltip("ARKit3D = ARHumanBodyManager skeleton (poor from behind). LidarHip = BlazePose 2D + LiDAR depth " +
                 "(view-independent; requires the BlazePose models imported and a LiDAR device). Falls back to " +
                 "ARKit3D when the BlazePose pipeline is unavailable.")]
        [SerializeField] private BodyPoseSourceMode poseSourceMode = BodyPoseSourceMode.ARKit3D;
        [Tooltip("LiDAR hip source tunables (depth gates, body-half offset, stencil guard).")]
        [SerializeField] private LidarHipPoseSource.Settings lidarHipSettings = new LidarHipPoseSource.Settings();

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

        // Pose sources
        private ARKitBodyPoseSource arkitSource;
        private LidarHipPoseSource lidarSource;
        private IBodyPoseSource activeSource;
        private readonly List<BodyPoseJoint> frameJoints = new List<BodyPoseJoint>(96);

        // Diagnostics snapshot taken at StartRecording for the per-recording A/B log.
        private long lidarDepthAcceptedAtStart;
        private long lidarDepthRejectedAtStart;
        private double lidarDepthSumAtStart;

        // Recording state
        private HipRecording currentRecording;
        private bool isRecording = false;
        private float recordingStartTime;
        private float nextRecordTime;
        private bool warnedNoJointsThisSession;

        // Live hip marker + short trail (LidarHip source): green = LiDAR depth lock, amber = body seen but no
        // usable depth, hidden = no person. Gives clear in-app feedback that the tracked path is forming.
        private GameObject hipMarker;
        private Material hipMarkerMaterial;
        private LineRenderer hipTrail;
        private readonly List<Vector3> trailPositions = new List<Vector3>(128);
        private readonly List<float> trailTimes = new List<float>(128);
        private const float HipTrailSeconds = 2f;
        private static readonly Color HipDepthLockedColor = new Color(0.15f, 0.9f, 0.3f);
        private static readonly Color HipPoseOnlyColor = new Color(1f, 0.72f, 0.1f);

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
        /// <summary>Serialized pose-source selection (may differ from <see cref="ActiveSourceName"/> if LidarHip fell back).</summary>
        public BodyPoseSourceMode ConfiguredPoseSourceMode => poseSourceMode;

        public string ActiveSourceName => activeSource != null ? activeSource.SourceName : "none";
        public bool HasTrackedBody => activeSource != null && activeSource.HasTrackedBody;
        public int LastTrackedBodyCount => activeSource != null ? activeSource.TrackedBodyCount : 0;
        public int LastTrackedJointCount => activeSource != null ? activeSource.TrackedJointCount : 0;

        /// <summary>True when recording is driven by the BlazePose+LiDAR source (controller uses this to pick
        /// the AR session config: ARHumanBodyManager off, environment depth on).</summary>
        public bool UsesLidarHipSource => activeSource != null && activeSource == lidarSource;
        /// <summary>The LiDAR hip source when it is active, else null (live marker / UI status).</summary>
        public LidarHipPoseSource ActiveLidarSource => UsesLidarHipSource ? lidarSource : null;
        /// <summary>One-line tracking status for the recording UI ("Hip lock: LiDAR" etc.). Empty for ARKit.</summary>
        public string HipTrackingStatusLine => UsesLidarHipSource ? lidarSource.StatusLine : string.Empty;

        /// <summary>
        /// "Body detected" gate for the arming countdown. For the LiDAR source this requires a depth-locked
        /// hip (not just a 2D pose), so recording starts with real positional samples available.
        /// </summary>
        public bool IsBodyDetectedForArming =>
            UsesLidarHipSource ? lidarSource.LockState == LidarHipLockState.DepthLocked : HasTrackedBody;

        /// <summary>Force a pose poll this frame so <see cref="HasTrackedBody"/> is current (used while arming).</summary>
        public void PollBodyDetection()
        {
            if (activeSource != null)
                activeSource.TryGetCurrentPose(null, out _, out _);
            // Show the hip marker while arming too, so the user can see the lock state before capture starts.
            UpdateLiveHipMarker();
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
                HideHipMarker();
                HideHipTrail();
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
                UnityEngine.Debug.LogError("[BodyTrackingRecorder] No pose source available — need an ARHumanBodyManager (ARKit3D) or a BlazePoseRunner + AROcclusionManager (LidarHip)");
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

            lidarSource = null;
            if (poseSourceMode == BodyPoseSourceMode.LidarHip)
            {
                // The BlazePose pipeline may be disabled while idle (enable-on-demand), so search inactive too.
                var runner = FindFirstObjectByType<BlazePoseRunner>(FindObjectsInactive.Include);
                var occlusion = FindFirstObjectByType<AROcclusionManager>(FindObjectsInactive.Include);
                if (runner != null && occlusion != null)
                {
                    lidarSource = new LidarHipPoseSource(runner, occlusion, lidarHipSettings);
                }
                else
                {
                    UnityEngine.Debug.LogWarning("[BodyTrackingRecorder] LidarHip selected but BlazePoseRunner/AROcclusionManager missing — falling back to ARKit3D. Import the BlazePose models (Assets/AI/BlazePose/IMPORT_MODELS.txt) and add the pipeline to the scene.");
                }
            }

            activeSource = lidarSource ?? (IBodyPoseSource)arkitSource;
            if (lidarSource == null && poseSourceMode == BodyPoseSourceMode.LidarHip && arkitSource != null)
                UnityEngine.Debug.Log("[BodyTrackingRecorder] Pose source fallback: ARKit3D");
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
                poseBackend = activeSource.SourceName,
                recordingTimestamp = DateTime.Now
            };

            if (lidarSource != null)
            {
                lidarDepthAcceptedAtStart = lidarSource.HipDepthAccepted;
                lidarDepthRejectedAtStart = lidarSource.HipDepthRejected;
                lidarDepthSumAtStart = lidarSource.HipDepthSumMeters;
            }

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
            HideHipTrail();

            LogRecordedDepthSpread();
            LogPoseSourceDiagnostics();

            OnRecordingComplete?.Invoke(currentRecording);
            return currentRecording;
        }

        /// <summary>
        /// Per-recording accuracy diagnostics in the same log style, enabling ARKit-vs-LiDAR A/B comparison
        /// across recordings: valid-hip %, mean confidence, and (LiDAR only) depth accept/reject stats.
        /// </summary>
        private void LogPoseSourceDiagnostics()
        {
            if (currentRecording == null || currentRecording.frames == null || currentRecording.frames.Count == 0)
                return;

            int total = currentRecording.frames.Count;
            int validHip = 0;
            float confidenceSum = 0f;
            foreach (var frame in currentRecording.frames)
            {
                if (frame.hipJoint.isTracked)
                {
                    validHip++;
                    confidenceSum += frame.hipJoint.confidence;
                }
            }

            float validPct = 100f * validHip / total;
            float meanConfidence = validHip > 0 ? confidenceSum / validHip : 0f;
            string msg = $"[BodyTrackingRecorder] Pose source diagnostics — backend={currentRecording.poseBackend}, " +
                         $"validHip={validHip}/{total} ({validPct:F0}%), meanConfidence={meanConfidence:F2}";

            if (UsesLidarHipSource && lidarSource != null)
            {
                long accepted = lidarSource.HipDepthAccepted - lidarDepthAcceptedAtStart;
                long rejected = lidarSource.HipDepthRejected - lidarDepthRejectedAtStart;
                double depthSum = lidarSource.HipDepthSumMeters - lidarDepthSumAtStart;
                long attempts = accepted + rejected;
                float depthFailPct = attempts > 0 ? 100f * rejected / attempts : 0f;
                float meanDepth = accepted > 0 ? (float)(depthSum / accepted) : 0f;
                msg += $", depthFail={depthFailPct:F0}% ({rejected}/{attempts}), meanHipDepth={meanDepth:F2}m";
            }

            UnityEngine.Debug.Log(msg);
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
                // Avoid scanning ARKit body trackables while idle; arming refreshes detection explicitly
                // (PollBodyDetection also drives the live hip marker while arming).
                HideSkeletonVisualization();
                HideHipMarker();
                HideHipTrail();
                return;
            }

            // Sample the current pose once per frame and reuse it for both visualization and recording so the
            // stored data exactly matches what was shown.
            bool foundBody = activeSource.TryGetCurrentPose(frameJoints, out Vector3 hipWorld, out bool hipTracked);
            UpdateLiveHipMarker();

            // The LiDAR-hip joint skeleton is a per-joint depth lift: only the hip (green marker) is solid; the
            // limb joints scatter in depth and read as a shaky non-body. Show just the hip marker for LiDAR-hip;
            // keep the full dot/line skeleton for ARKit. (Joint capture/recording is unaffected either way.)
            bool drawJointSkeleton = showSkeletonVisualization && !UsesLidarHipSource &&
                                     (isRecording || showSkeletonWhenNotRecording);
            if (drawJointSkeleton)
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
                    // LiDAR source reports a real per-sample confidence (min of landmark visibility and depth
                    // quality); ARKit has no equivalent, so it stays at the legacy constant 1.0.
                    float confidence = UsesLidarHipSource ? lidarSource.LastHipConfidence : 1.0f;
                    frame.hipJoint = new HipJointData(referencePosition, confidence, true);
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

        // ========================================================================================
        // Live hip marker + trail (LidarHip source)
        // ========================================================================================

        /// <summary>
        /// Colored sphere at the lifted hip: green = LiDAR depth lock, amber = body seen but depth invalid
        /// (out of LiDAR range / depth edge), hidden = no person. A short trail of depth-locked positions
        /// (last ~2 s) shows the tracked path forming while recording/arming.
        /// </summary>
        private void UpdateLiveHipMarker()
        {
            if (!UsesLidarHipSource || !showSkeletonVisualization)
                return;

            var src = lidarSource;
            var state = src.LockState;
            if (state == LidarHipLockState.None || !src.TryGetHipVisual(out Vector3 hipWorld))
            {
                HideHipMarker();
                return;
            }

            EnsureHipMarker();
            hipMarker.transform.position = hipWorld;
            hipMarker.SetActive(true);
            if (hipMarkerMaterial != null)
                hipMarkerMaterial.color = state == LidarHipLockState.DepthLocked ? HipDepthLockedColor : HipPoseOnlyColor;

            if (state == LidarHipLockState.DepthLocked)
                AppendHipTrailPoint(hipWorld);
            PruneHipTrail();
        }

        private void EnsureHipMarker()
        {
            if (hipMarker != null)
                return;

            hipMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            hipMarker.name = "LidarHipMarker";
            hipMarker.transform.localScale = Vector3.one * 0.12f;

            var xrOrigin = FindFirstObjectByType<XROrigin>();
            if (xrOrigin != null)
                hipMarker.transform.SetParent(xrOrigin.transform);

            var renderer = hipMarker.GetComponent<Renderer>();
            if (renderer != null)
            {
                hipMarkerMaterial = DebugVisualizationMaterials.CreateSolidColorMaterial(HipDepthLockedColor);
                if (hipMarkerMaterial != null)
                    renderer.material = hipMarkerMaterial;
            }

            var collider = hipMarker.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var trailObject = new GameObject("LidarHipTrail");
            if (xrOrigin != null)
                trailObject.transform.SetParent(xrOrigin.transform);
            hipTrail = trailObject.AddComponent<LineRenderer>();
            hipTrail.useWorldSpace = true;
            hipTrail.startWidth = 0.015f;
            hipTrail.endWidth = 0.015f;
            hipTrail.numCapVertices = 2;
            var trailMaterial = DebugVisualizationMaterials.CreateLineMaterial(HipDepthLockedColor);
            if (trailMaterial != null)
                hipTrail.material = trailMaterial;
            var faded = HipDepthLockedColor; faded.a = 0.1f;
            hipTrail.startColor = faded;          // oldest point fades out
            hipTrail.endColor = HipDepthLockedColor;
            hipTrail.positionCount = 0;
        }

        private void AppendHipTrailPoint(Vector3 worldPosition)
        {
            // Skip sub-centimeter moves so a still climber doesn't fill the buffer with noise.
            if (trailPositions.Count > 0 && (trailPositions[trailPositions.Count - 1] - worldPosition).sqrMagnitude < 0.0001f)
            {
                trailTimes[trailTimes.Count - 1] = Time.time;
                return;
            }
            trailPositions.Add(worldPosition);
            trailTimes.Add(Time.time);
        }

        private void PruneHipTrail()
        {
            if (hipTrail == null)
                return;

            float cutoff = Time.time - HipTrailSeconds;
            int firstKept = 0;
            while (firstKept < trailTimes.Count && trailTimes[firstKept] < cutoff)
                firstKept++;
            if (firstKept > 0)
            {
                trailPositions.RemoveRange(0, firstKept);
                trailTimes.RemoveRange(0, firstKept);
            }

            hipTrail.positionCount = trailPositions.Count;
            for (int i = 0; i < trailPositions.Count; i++)
                hipTrail.SetPosition(i, trailPositions[i]);
            hipTrail.gameObject.SetActive(trailPositions.Count >= 2);
        }

        private void HideHipMarker()
        {
            if (hipMarker != null)
                hipMarker.SetActive(false);
        }

        private void HideHipTrail()
        {
            trailPositions.Clear();
            trailTimes.Clear();
            if (hipTrail != null)
            {
                hipTrail.positionCount = 0;
                hipTrail.gameObject.SetActive(false);
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
            if (hipMarker != null)
                Destroy(hipMarker);
            if (hipTrail != null)
                Destroy(hipTrail.gameObject);
            ClearSkeletonObjects();
            if (skeletonRoot != null)
            {
                Destroy(skeletonRoot);
            }
        }
    }
}
