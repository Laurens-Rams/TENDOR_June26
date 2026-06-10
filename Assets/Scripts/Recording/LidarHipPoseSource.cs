using System.Collections.Generic;
using BodyTracking.AI;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.Recording
{
    /// <summary>
    /// Hip lock state used by the live marker / UI: green = LiDAR depth-locked, amber = person seen but no
    /// usable depth at the hips (out of LiDAR range / depth edge), none = no person in frame.
    /// </summary>
    public enum LidarHipLockState
    {
        None,
        PoseOnlyNoDepth,
        DepthLocked
    }

    /// <summary>
    /// <see cref="IBodyPoseSource"/> that positions the climber via BlazePose 2D landmarks + LiDAR environment
    /// depth, replacing ARKit 3D body tracking (which is unreliable from behind). Per BlazePose result:
    /// left/right hip pixels are depth-sampled individually (5x5 median), unprojected through the camera
    /// intrinsics + the camera pose captured at image-acquisition time, averaged in 3D, then pushed half a
    /// body-depth along the view ray so the surface sample approximates the pelvis center (parity with the
    /// ARKit hip joint the fusion baker expects). A small joint subset (head/shoulders/hips/knees/ankles) is
    /// lifted too so the live/review skeleton and the baker's height estimate keep working.
    /// </summary>
    public class LidarHipPoseSource : IBodyPoseSource
    {
        /// <summary>Tunables owned (and serialized) by the recorder.</summary>
        [System.Serializable]
        public class Settings
        {
            [Tooltip("Reject LiDAR depth below this (meters) — closer than any plausible climber.")]
            public float minDepthMeters = 0.3f;
            [Tooltip("Reject LiDAR depth beyond this (meters). LiDAR degrades past ~5 m, which matters for climbers high on a wall.")]
            public float maxDepthMeters = 6f;
            [Tooltip("Landmark visibility below this is treated as not seen.")]
            [Range(0f, 1f)] public float visibilityThreshold = 0.35f;
            [Tooltip("Meters to push the unprojected body-surface point along the camera ray so it approximates the pelvis center.")]
            public float bodyHalfOffsetMeters = 0.10f;
            [Tooltip("Verify the hip pixel lies on the person via the ARKit human stencil (guards against the hip pixel landing on the wall between the legs). Skipped when no stencil image is available.")]
            public bool useHumanStencilCheck = true;
            [Tooltip("A BlazePose result older than this (seconds) counts as 'no person in frame'.")]
            public float staleResultSeconds = 0.6f;
        }

        // Joint subset lifted into recordedJoints: enough for the skeleton viz to read as a body and for
        // MoveAIFusionBaker.EstimateArkitHeight (head-to-feet extent) to produce the character scale.
        static readonly int[] LiftedJointIndices =
        {
            BlazePoseSkeleton.Nose,
            BlazePoseSkeleton.LeftShoulder, BlazePoseSkeleton.RightShoulder,
            BlazePoseSkeleton.LeftHip, BlazePoseSkeleton.RightHip,
            BlazePoseSkeleton.LeftKnee, BlazePoseSkeleton.RightKnee,
            BlazePoseSkeleton.LeftAnkle, BlazePoseSkeleton.RightAnkle,
            BlazePoseSkeleton.LeftHeel, BlazePoseSkeleton.RightHeel
        };

        // BlazePose has no head-top landmark, so the nose would short-change the baker's head-to-feet height
        // estimate (and shrink the character) by ~10%. Synthesize a head-top sample by extending the
        // shoulder-mid -> nose direction past the nose; emitted with this out-of-topology index (parent: nose).
        public const int SyntheticHeadTopIndex = BlazePoseSkeleton.NumKeypoints; // 33
        const float HeadTopExtension = 0.5f;

        readonly BlazePoseRunner runner;
        readonly AROcclusionManager occlusionManager;
        readonly Settings settings;

        // Per-landmark lift results for the subset (indexed by BlazePose landmark index).
        readonly bool[] jointLifted = new bool[BlazePoseSkeleton.NumKeypoints];
        readonly Vector3[] jointWorld = new Vector3[BlazePoseSkeleton.NumKeypoints];
        readonly float[] jointDepth = new float[BlazePoseSkeleton.NumKeypoints];
        readonly bool[] jointDepthValid = new bool[BlazePoseSkeleton.NumKeypoints];
        readonly float[] medianBuffer = new float[25];

        int lastProcessedFrameId = -1;
        float lastProcessedAt = -999f;

        // Cached output of the last processed BlazePose result.
        bool cachedBodyFound;
        bool cachedHipTracked;
        Vector3 cachedHipWorld;
        float cachedHipConfidence;
        LidarHipLockState cachedLockState = LidarHipLockState.None;
        // Best-effort hip position for the live marker even when depth is invalid (amber state). Never recorded.
        bool cachedHipVisualValid;
        Vector3 cachedHipVisualWorld;

        // Synthetic head-top sample (see SyntheticHeadTopIndex).
        bool headTopLifted;
        Vector3 headTopWorld;

        bool hasTrackedBody;
        int trackedJointCount;

        // Cumulative diagnostics (recorder snapshots at StartRecording and diffs at stop for the A/B log).
        public long HipDepthAccepted { get; private set; }
        public long HipDepthRejected { get; private set; }
        public double HipDepthSumMeters { get; private set; }

        public LidarHipPoseSource(BlazePoseRunner runner, AROcclusionManager occlusionManager, Settings settings)
        {
            this.runner = runner;
            this.occlusionManager = occlusionManager;
            this.settings = settings ?? new Settings();
        }

        public string SourceName => "LiDARHip";
        public bool IsAvailable => runner != null && occlusionManager != null;
        // Hip is synthetic (mid 23/24); report right-hip as the nominal index for reference.
        public int HipJointIndex => BlazePoseSkeleton.RightHip;
        public bool HasTrackedBody => hasTrackedBody;
        public int TrackedBodyCount => hasTrackedBody ? 1 : 0;
        public int TrackedJointCount => trackedJointCount;

        /// <summary>Lock state of the most recent processed frame (drives the live marker colors).</summary>
        public LidarHipLockState LockState => IsResultFresh() ? cachedLockState : LidarHipLockState.None;
        /// <summary>Confidence (min of hip visibility and depth quality) of the last hip sample, 0 when invalid.</summary>
        public float LastHipConfidence => IsResultFresh() ? cachedHipConfidence : 0f;
        /// <summary>One-line status for the recording UI.</summary>
        public string StatusLine
        {
            get
            {
                switch (LockState)
                {
                    case LidarHipLockState.DepthLocked: return "Hip lock: LiDAR";
                    case LidarHipLockState.PoseOnlyNoDepth: return "Body seen — out of depth range";
                    default: return "No body in frame";
                }
            }
        }

        bool IsResultFresh()
        {
            return Time.unscaledTime - lastProcessedAt <= settings.staleResultSeconds;
        }

        /// <summary>
        /// Best-effort world hip position for the live marker: the depth-locked hip when available, otherwise
        /// the hips lifted with the body-depth fallback (amber state). Visualization only — never recorded.
        /// </summary>
        public bool TryGetHipVisual(out Vector3 world)
        {
            world = cachedHipVisualWorld;
            return cachedHipVisualValid && IsResultFresh();
        }

        public bool TryGetCurrentPose(List<BodyPoseJoint> jointsOut, out Vector3 hipWorldPosition, out bool hipTracked)
        {
            jointsOut?.Clear();
            hipWorldPosition = Vector3.zero;
            hipTracked = false;

            var result = runner != null ? runner.LatestResult : null;
            if (result != null && result.valid && result.frameId != lastProcessedFrameId)
            {
                ProcessResult(result);
                lastProcessedFrameId = result.frameId;
            }
            else if (result != null && !result.valid && result.frameId != lastProcessedFrameId)
            {
                // Detector explicitly reported no person — clear immediately instead of waiting for staleness.
                lastProcessedFrameId = result.frameId;
                cachedBodyFound = false;
                cachedHipTracked = false;
                cachedLockState = LidarHipLockState.None;
                lastProcessedAt = -999f;
            }

            if (!cachedBodyFound || !IsResultFresh())
            {
                hasTrackedBody = false;
                trackedJointCount = 0;
                return false;
            }

            trackedJointCount = 0;
            if (jointsOut != null)
            {
                for (int s = 0; s < LiftedJointIndices.Length; s++)
                {
                    int i = LiftedJointIndices[s];
                    if (!jointLifted[i])
                        continue;
                    jointsOut.Add(new BodyPoseJoint(i, BlazePoseSkeleton.Parents[i], jointWorld[i], true));
                    trackedJointCount++;
                }
                if (headTopLifted)
                {
                    jointsOut.Add(new BodyPoseJoint(SyntheticHeadTopIndex, BlazePoseSkeleton.Nose, headTopWorld, true));
                    trackedJointCount++;
                }
            }
            else
            {
                for (int s = 0; s < LiftedJointIndices.Length; s++)
                    if (jointLifted[LiftedJointIndices[s]])
                        trackedJointCount++;
                if (headTopLifted)
                    trackedJointCount++;
            }

            hipWorldPosition = cachedHipWorld;
            hipTracked = cachedHipTracked;
            hasTrackedBody = trackedJointCount > 0;
            return hasTrackedBody;
        }

        void ProcessResult(BlazePoseResult result)
        {
            cachedBodyFound = false;
            cachedHipTracked = false;
            cachedHipConfidence = 0f;
            cachedHipVisualValid = false;
            headTopLifted = false;
            cachedLockState = LidarHipLockState.None;
            for (int i = 0; i < jointLifted.Length; i++)
            {
                jointLifted[i] = false;
                jointDepthValid[i] = false;
            }

            // Capture-time intrinsics + pose: without them we can't unproject metrically.
            if (!result.hasIntrinsics || !result.hasCameraPose)
                return;

            lastProcessedAt = Time.unscaledTime;

            var intrinsics = result.intrinsics;
            var remap = BlazePoseOrientation.DetectRemap(
                Mathf.RoundToInt(result.textureWidth), Mathf.RoundToInt(result.textureHeight), intrinsics.resolution);

            SampleDepthForSubset(result, remap);
            ApplyHumanStencilGuard(result, remap);

            // Body depth fallback for subset joints with no LiDAR hit: median of the joints that do have depth.
            float bodyDepth = MedianSubsetDepth();

            Vector3 camPos = result.cameraPosition;
            Quaternion camRot = result.cameraRotation;

            int lifted = 0;
            for (int s = 0; s < LiftedJointIndices.Length; s++)
            {
                int i = LiftedJointIndices[s];
                var lm = result.landmarks[i];
                if (lm.visibility < settings.visibilityThreshold)
                    continue;

                float depth = jointDepthValid[i] ? jointDepth[i] : bodyDepth;
                if (depth <= 0f)
                    continue;

                jointWorld[i] = Unproject(lm.imageUV, depth, intrinsics, remap, camPos, camRot);
                jointLifted[i] = true;
                lifted++;
            }

            cachedBodyFound = lifted > 0;
            if (!cachedBodyFound)
                return;

            if (jointLifted[BlazePoseSkeleton.Nose] &&
                jointLifted[BlazePoseSkeleton.LeftShoulder] && jointLifted[BlazePoseSkeleton.RightShoulder])
            {
                Vector3 shoulderMid = 0.5f * (jointWorld[BlazePoseSkeleton.LeftShoulder] + jointWorld[BlazePoseSkeleton.RightShoulder]);
                headTopWorld = jointWorld[BlazePoseSkeleton.Nose] +
                               (jointWorld[BlazePoseSkeleton.Nose] - shoulderMid) * HeadTopExtension;
                headTopLifted = true;
            }

            ComputeHip(result, intrinsics, remap, camPos, camRot);
            UpdateHipVisual();
            LogUnprojectDiagnostics(result, intrinsics, remap, camPos);
        }

        float lastDiagAt = -999f;

        /// <summary>
        /// Throttled (1 Hz) device log of the values needed to verify hip placement / orientation: sensor vs
        /// intrinsics resolution, screen orientation, chosen remap, the mid-hip pixel, sampled depth, the
        /// resulting world hip, and its distance from the camera (should ≈ the sampled depth + body-half offset,
        /// and the hip should be roughly in front of the camera, not off to the side).
        /// </summary>
        void LogUnprojectDiagnostics(BlazePoseResult result, XRCameraIntrinsics intrinsics,
                                     BlazePoseOrientation.Remap remap, Vector3 camPos)
        {
            if (Time.unscaledTime - lastDiagAt < 1f)
                return;
            lastDiagAt = Time.unscaledTime;

            float hipDist = cachedHipTracked ? Vector3.Distance(camPos, cachedHipWorld) : -1f;
            var leftLm = result.landmarks[BlazePoseSkeleton.LeftHip];
            var rightLm = result.landmarks[BlazePoseSkeleton.RightHip];
            Vector2 midUv = 0.5f * (leftLm.imageUV + rightLm.imageUV);
            Vector2 midPx = BlazePoseOrientation.SensorUvToPixel(midUv, intrinsics.resolution, remap);

            UnityEngine.Debug.Log(
                $"[LidarHipPoseSource] diag sensor={Mathf.RoundToInt(result.textureWidth)}x{Mathf.RoundToInt(result.textureHeight)} " +
                $"intrinsics={intrinsics.resolution.x}x{intrinsics.resolution.y} screen={Screen.orientation} " +
                $"remap(swap={remap.swapXY},flipX={remap.flipX},flipY={remap.flipY}) " +
                $"hipUV=({midUv.x:F2},{midUv.y:F2}) hipPx=({midPx.x:F0},{midPx.y:F0}) " +
                $"lock={cachedLockState} hipDepth={(jointDepthValid[BlazePoseSkeleton.LeftHip] ? jointDepth[BlazePoseSkeleton.LeftHip] : (jointDepthValid[BlazePoseSkeleton.RightHip] ? jointDepth[BlazePoseSkeleton.RightHip] : -1f)):F2}m " +
                $"hipWorld={cachedHipWorld:F2} camPos={camPos:F2} dist={hipDist:F2}m");
        }

        void UpdateHipVisual()
        {
            if (cachedHipTracked)
            {
                cachedHipVisualWorld = cachedHipWorld;
                cachedHipVisualValid = true;
                return;
            }

            bool left = jointLifted[BlazePoseSkeleton.LeftHip];
            bool right = jointLifted[BlazePoseSkeleton.RightHip];
            if (left && right)
                cachedHipVisualWorld = 0.5f * (jointWorld[BlazePoseSkeleton.LeftHip] + jointWorld[BlazePoseSkeleton.RightHip]);
            else if (left)
                cachedHipVisualWorld = jointWorld[BlazePoseSkeleton.LeftHip];
            else if (right)
                cachedHipVisualWorld = jointWorld[BlazePoseSkeleton.RightHip];
            else
                return;
            cachedHipVisualValid = true;
        }

        /// <summary>
        /// Hip = average of the *individually* depth-lifted left/right hip points (a depth edge on one hip
        /// can't skew the result), falling back to a single hip, then pushed half a body along the view ray.
        /// </summary>
        void ComputeHip(BlazePoseResult result, XRCameraIntrinsics intrinsics, BlazePoseOrientation.Remap remap,
                        Vector3 camPos, Quaternion camRot)
        {
            var leftLm = result.landmarks[BlazePoseSkeleton.LeftHip];
            var rightLm = result.landmarks[BlazePoseSkeleton.RightHip];

            bool leftOk = leftLm.visibility >= settings.visibilityThreshold && jointDepthValid[BlazePoseSkeleton.LeftHip];
            bool rightOk = rightLm.visibility >= settings.visibilityThreshold && jointDepthValid[BlazePoseSkeleton.RightHip];

            Vector3 surface;
            float hipVisibility;
            if (leftOk && rightOk)
            {
                surface = 0.5f * (jointWorld[BlazePoseSkeleton.LeftHip] + jointWorld[BlazePoseSkeleton.RightHip]);
                hipVisibility = Mathf.Min(leftLm.visibility, rightLm.visibility);
            }
            else if (leftOk)
            {
                surface = jointWorld[BlazePoseSkeleton.LeftHip];
                hipVisibility = leftLm.visibility;
            }
            else if (rightOk)
            {
                surface = jointWorld[BlazePoseSkeleton.RightHip];
                hipVisibility = rightLm.visibility;
            }
            else
            {
                // 2D pose sees the person but LiDAR has no usable depth at either hip → amber, hip invalid
                // (the fusion baker hold-fills gaps; we never record a guessed depth).
                bool poseSeesHips = leftLm.visibility >= settings.visibilityThreshold ||
                                    rightLm.visibility >= settings.visibilityThreshold;
                if (poseSeesHips)
                    HipDepthRejected++; // hips visible in 2D but depth unusable = a real depth failure
                cachedLockState = poseSeesHips ? LidarHipLockState.PoseOnlyNoDepth : LidarHipLockState.None;
                cachedHipTracked = false;
                return;
            }

            // Body-half offset: depth sampled the camera-facing body surface; push along the view ray to
            // approximate the pelvis center (what ARKit's hip joint and the fusion baker expect).
            Vector3 ray = (surface - camPos).normalized;
            cachedHipWorld = surface + ray * settings.bodyHalfOffsetMeters;
            cachedHipTracked = true;
            cachedLockState = LidarHipLockState.DepthLocked;

            // Depth quality: fraction of valid samples in the median windows that produced this hip.
            float depthQuality = 0.5f * (DepthWindowQuality(BlazePoseSkeleton.LeftHip, leftOk) +
                                         DepthWindowQuality(BlazePoseSkeleton.RightHip, rightOk));
            cachedHipConfidence = Mathf.Clamp01(Mathf.Min(hipVisibility, depthQuality));

            HipDepthAccepted++;
            HipDepthSumMeters += Vector3.Distance(camPos, surface);
        }

        // Per-hip depth window quality stored during sampling (valid-sample fraction of the 5x5 window).
        readonly float[] windowQuality = new float[BlazePoseSkeleton.NumKeypoints];

        float DepthWindowQuality(int jointIndex, bool used) => used ? windowQuality[jointIndex] : 1f;

        Vector3 Unproject(Vector2 sensorUv, float depth, XRCameraIntrinsics intrinsics,
                          BlazePoseOrientation.Remap remap, Vector3 camPos, Quaternion camRot)
        {
            Vector2 px = BlazePoseOrientation.SensorUvToPixel(sensorUv, intrinsics.resolution, remap);
            float x = (px.x - intrinsics.principalPoint.x) / intrinsics.focalLength.x * depth;
            float y = (px.y - intrinsics.principalPoint.y) / intrinsics.focalLength.y * depth;
            // The intrinsics describe the native (landscape) sensor optical frame: pixel x grows right, pixel y
            // grows DOWN, camera looks along +Z into the scene (standard CV pinhole). Convert that ray into the
            // Unity AR-camera local frame, which is right / UP / +Z-forward: flip Y, keep depth POSITIVE (the
            // Unity camera looks down its own +Z, so a point in front of it has +depth). Using -depth here put
            // every joint *behind* the camera. The Unity AR camera pose (camRot) is oriented for the current
            // DISPLAY orientation, which on a portrait phone is rotated ~90° from the landscape sensor, so we
            // also rotate the ray about the optical axis (forward) before applying the world pose.
            Vector3 camLocal = new Vector3(x, -y, depth);
            camLocal = SensorToDisplayRotation() * camLocal;
            return camPos + camRot * camLocal;
        }

        /// <summary>
        /// Rotation (about the camera optical axis, +Z toward the scene = -forward) that maps a ray built in the
        /// native sensor frame into the Unity AR-camera (display-oriented) frame. iOS reports intrinsics in the
        /// landscape sensor frame regardless of how the phone is held; ARFoundation rotates the camera transform
        /// to the interface orientation, so we must undo that delta here.
        /// </summary>
        static Quaternion SensorToDisplayRotation()
        {
            // Portrait: the climber's vertical motion lands on the landscape sensor's X axis, which must map to
            // Unity camera-up. -90° about the optical axis does that. (An earlier note claimed +90°, but that was
            // "verified" while the depth sign was wrong and the whole body sat *behind* the camera — a mirrored
            // view that hid the real inversion. With the body correctly in front, +90° reads climb-up as
            // world-down.) The four cases stay 90° apart so any interface orientation is handled consistently.
            switch (Screen.orientation)
            {
                case ScreenOrientation.Portrait:           return Quaternion.AngleAxis(-90f, Vector3.forward);
                case ScreenOrientation.PortraitUpsideDown: return Quaternion.AngleAxis( 90f, Vector3.forward);
                case ScreenOrientation.LandscapeRight:     return Quaternion.identity;
                case ScreenOrientation.LandscapeLeft:      return Quaternion.AngleAxis(180f, Vector3.forward);
                default:                                   return Quaternion.AngleAxis(-90f, Vector3.forward);
            }
        }

        /// <summary>Sample a 5x5-median LiDAR depth at each subset joint's pixel.</summary>
        void SampleDepthForSubset(BlazePoseResult result, BlazePoseOrientation.Remap remap)
        {
            if (occlusionManager == null || !occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
                return;

            try
            {
                int w = depthImage.width;
                int h = depthImage.height;
                var plane = depthImage.GetPlane(0);
                var depthData = plane.data.Reinterpret<float>(UnsafeUtility.SizeOf<byte>());
                int rowStride = plane.rowStride / sizeof(float);
                if (rowStride <= 0)
                    rowStride = w;

                var depthRes = new Vector2Int(w, h);
                for (int s = 0; s < LiftedJointIndices.Length; s++)
                {
                    int i = LiftedJointIndices[s];
                    var lm = result.landmarks[i];
                    if (lm.visibility < settings.visibilityThreshold)
                        continue;

                    Vector2 px = BlazePoseOrientation.SensorUvToPixel(lm.imageUV, depthRes, remap);
                    int cx = Mathf.Clamp(Mathf.RoundToInt(px.x), 0, w - 1);
                    int cy = Mathf.Clamp(Mathf.RoundToInt(px.y), 0, h - 1);

                    int valid = 0;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int yy = Mathf.Clamp(cy + dy, 0, h - 1);
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = Mathf.Clamp(cx + dx, 0, w - 1);
                            float d = depthData[yy * rowStride + xx];
                            if (d >= settings.minDepthMeters && d <= settings.maxDepthMeters)
                                medianBuffer[valid++] = d;
                        }
                    }

                    // Require a minimally filled window so a single stray return can't pass as the body.
                    if (valid >= 3)
                    {
                        System.Array.Sort(medianBuffer, 0, valid);
                        jointDepth[i] = medianBuffer[valid / 2];
                        jointDepthValid[i] = true;
                        windowQuality[i] = valid / 25f;
                    }
                    else
                    {
                        windowQuality[i] = 0f;
                    }
                }
            }
            finally
            {
                depthImage.Dispose();
            }
        }

        /// <summary>
        /// Reject hip depth samples whose pixel is not on a person per the ARKit human stencil — guards
        /// against the hip-midpoint pixel landing on the wall between the legs. No-op without a stencil image.
        /// </summary>
        void ApplyHumanStencilGuard(BlazePoseResult result, BlazePoseOrientation.Remap remap)
        {
            if (!settings.useHumanStencilCheck)
                return;
            if (!jointDepthValid[BlazePoseSkeleton.LeftHip] && !jointDepthValid[BlazePoseSkeleton.RightHip])
                return;
            if (occlusionManager == null || !occlusionManager.TryAcquireHumanStencilCpuImage(out XRCpuImage stencilImage))
                return;

            try
            {
                int w = stencilImage.width;
                int h = stencilImage.height;
                var plane = stencilImage.GetPlane(0);
                var data = plane.data;
                int rowStride = plane.rowStride;
                var stencilRes = new Vector2Int(w, h);

                CheckStencilForHip(BlazePoseSkeleton.LeftHip, result, remap, stencilRes, data, rowStride, w, h);
                CheckStencilForHip(BlazePoseSkeleton.RightHip, result, remap, stencilRes, data, rowStride, w, h);
            }
            finally
            {
                stencilImage.Dispose();
            }
        }

        void CheckStencilForHip(int jointIndex, BlazePoseResult result, BlazePoseOrientation.Remap remap,
                                Vector2Int stencilRes, Unity.Collections.NativeArray<byte> data, int rowStride, int w, int h)
        {
            if (!jointDepthValid[jointIndex])
                return;

            Vector2 px = BlazePoseOrientation.SensorUvToPixel(result.landmarks[jointIndex].imageUV, stencilRes, remap);
            int cx = Mathf.Clamp(Mathf.RoundToInt(px.x), 0, w - 1);
            int cy = Mathf.Clamp(Mathf.RoundToInt(px.y), 0, h - 1);

            // Accept if any pixel of a 3x3 patch is on the person (tolerates 1px landmark jitter at the silhouette).
            for (int dy = -1; dy <= 1; dy++)
            {
                int yy = Mathf.Clamp(cy + dy, 0, h - 1);
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = Mathf.Clamp(cx + dx, 0, w - 1);
                    if (data[yy * rowStride + xx] > 0)
                        return;
                }
            }

            jointDepthValid[jointIndex] = false;
        }

        float MedianSubsetDepth()
        {
            int count = 0;
            for (int s = 0; s < LiftedJointIndices.Length; s++)
            {
                int i = LiftedJointIndices[s];
                if (jointDepthValid[i])
                    medianBuffer[count++] = jointDepth[i];
            }
            if (count == 0)
                return 0f;
            System.Array.Sort(medianBuffer, 0, count);
            return medianBuffer[count / 2];
        }
    }
}
