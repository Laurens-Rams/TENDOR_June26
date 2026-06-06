using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AI
{
    /// <summary>One BlazePose joint lifted to world space.</summary>
    public struct BlazeWorldJoint
    {
        public Vector3 worldPosition;
        public bool tracked;
        public bool hasDepth; // true if from LiDAR, false if from BlazePose-z fallback
    }

    /// <summary>World-space BlazePose pose. Reused/mutated each frame by <see cref="BlazePoseDepthLift"/>.</summary>
    public class BlazePoseWorldResult
    {
        public bool valid;
        public int frameId;
        public readonly BlazeWorldJoint[] joints = new BlazeWorldJoint[BlazePoseSkeleton.NumKeypoints];
    }

    /// <summary>
    /// Stage 2: lifts BlazePose 2D landmarks into world space using LiDAR environment depth + camera intrinsics.
    /// </summary>
    public class BlazePoseDepthLift : MonoBehaviour
    {
        [SerializeField] BlazePoseRunner runner;
        [SerializeField] AROcclusionManager occlusionManager;
        [SerializeField] ARCameraManager cameraManager;
        [Tooltip("AR camera used to transform camera-space points to world. Falls back to Camera.main.")]
        [SerializeField] Camera arCamera;

        [Header("Orientation")]
        [Tooltip("Auto-detect portrait sensor->intrinsics rotation (recommended on iPhone).")]
        [SerializeField] bool autoOrient = true;
        [SerializeField] bool flipX;
        [SerializeField] bool flipY = true;
        [SerializeField] bool swapXY;

        [Header("Depth")]
        [Tooltip("Reject depth samples outside this metric range (meters).")]
        [SerializeField] float minDepth = 0.3f;
        [SerializeField] float maxDepth = 8f;
        [Tooltip("Scales BlazePose relative-z when used as a depth fallback.")]
        [SerializeField] float fallbackZScale = 1.5f;

        [Header("Debug")]
        [SerializeField] bool verboseLogging = true;
        float m_LastLogTime = -999f;

        public System.Action<BlazePoseWorldResult> OnWorldPose;
        public BlazePoseWorldResult LatestWorld => m_World;

        readonly BlazePoseWorldResult m_World = new BlazePoseWorldResult();
        readonly float[] m_SampledDepth = new float[BlazePoseSkeleton.NumKeypoints];
        readonly bool[] m_DepthValid = new bool[BlazePoseSkeleton.NumKeypoints];

        void Awake()
        {
            if (runner == null)
                runner = FindAnyObjectByType<BlazePoseRunner>();
            if (occlusionManager == null)
                occlusionManager = FindAnyObjectByType<AROcclusionManager>();
            if (cameraManager == null)
                cameraManager = Globals.CameraManager != null ? Globals.CameraManager : FindAnyObjectByType<ARCameraManager>();
            if (arCamera == null)
                arCamera = cameraManager != null ? cameraManager.GetComponent<Camera>() : Camera.main;
        }

        void OnEnable()
        {
            if (runner != null)
                runner.OnPoseUpdated += HandlePose;
        }

        void OnDisable()
        {
            if (runner != null)
                runner.OnPoseUpdated -= HandlePose;
        }

        bool ShouldLog()
        {
            if (!verboseLogging)
                return false;
            if (Time.time - m_LastLogTime < 1f)
                return false;
            m_LastLogTime = Time.time;
            return true;
        }

        BlazePoseOrientation.Remap GetRemap(BlazePoseResult result, XRCameraIntrinsics intrinsics)
        {
            if (autoOrient)
            {
                int w = Mathf.RoundToInt(result.textureWidth);
                int h = Mathf.RoundToInt(result.textureHeight);
                return BlazePoseOrientation.DetectRemap(w, h, intrinsics.resolution);
            }

            return new BlazePoseOrientation.Remap { swapXY = swapXY, flipX = flipX, flipY = flipY };
        }

        void HandlePose(BlazePoseResult result)
        {
            if (result == null || !result.valid || arCamera == null || cameraManager == null)
            {
                if (ShouldLog())
                    Debug.LogWarning($"[BlazePoseDepthLift] Skipped. resultValid={(result != null && result.valid)}, " +
                                     $"arCamera={(arCamera == null ? "NULL" : "ok")}, cameraManager={(cameraManager == null ? "NULL" : "ok")}");
                m_World.valid = false;
                OnWorldPose?.Invoke(m_World);
                return;
            }

            if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                if (ShouldLog())
                    Debug.LogWarning("[BlazePoseDepthLift] TryGetIntrinsics failed - cannot unproject. AR camera not ready yet?");
                m_World.valid = false;
                OnWorldPose?.Invoke(m_World);
                return;
            }

            var remap = GetRemap(result, intrinsics);
            SampleAllDepth(result, remap);

            float bodyDepth = MedianValidDepth();
            if (bodyDepth <= 0f)
                bodyDepth = EstimateBodyDepthFromBlazeZ(result);

            int n = BlazePoseSkeleton.NumKeypoints;
            var camTransform = arCamera.transform;
            for (int i = 0; i < n; i++)
            {
                var lm = result.landmarks[i];
                bool hasDepth = m_DepthValid[i];
                float depth = hasDepth
                    ? m_SampledDepth[i]
                    : bodyDepth + lm.zRelative * fallbackZScale / Mathf.Max(1f, result.textureHeight);

                Vector2 px = BlazePoseOrientation.SensorUvToPixel(lm.imageUV, intrinsics.resolution, remap);
                float x = (px.x - intrinsics.principalPoint.x) / intrinsics.focalLength.x * depth;
                float y = (px.y - intrinsics.principalPoint.y) / intrinsics.focalLength.y * depth;
                Vector3 camLocal = new Vector3(x, -y, -depth);
                Vector3 world = camTransform.TransformPoint(camLocal);

                ref var joint = ref m_World.joints[i];
                joint.worldPosition = world;
                joint.tracked = lm.visibility > 0.3f && depth > 0f;
                joint.hasDepth = hasDepth;
            }

            m_World.valid = true;
            m_World.frameId = result.frameId;
            if (ShouldLog())
            {
                int depthCount = 0;
                for (int i = 0; i < m_DepthValid.Length; i++)
                    if (m_DepthValid[i]) depthCount++;
                Debug.Log($"[BlazePoseDepthLift] World pose OK. score={result.score:F2}, " +
                          $"lidarDepthJoints={depthCount}/{BlazePoseSkeleton.NumKeypoints}, " +
                          $"sensor={Mathf.RoundToInt(result.textureWidth)}x{Mathf.RoundToInt(result.textureHeight)}, " +
                          $"intrinsics={intrinsics.resolution.x}x{intrinsics.resolution.y}, " +
                          $"remap swap={remap.swapXY} flipX={remap.flipX} flipY={remap.flipY}");
            }
            OnWorldPose?.Invoke(m_World);
        }

        float EstimateBodyDepthFromBlazeZ(BlazePoseResult result)
        {
            // Mid-hip relative z gives a rough metric depth when LiDAR has no hits.
            var left = result.landmarks[BlazePoseSkeleton.LeftHip];
            var right = result.landmarks[BlazePoseSkeleton.RightHip];
            float z = 0.5f * (left.zRelative + right.zRelative);
            return Mathf.Clamp(1.5f + z * fallbackZScale * 0.01f, minDepth, maxDepth);
        }

        void SampleAllDepth(BlazePoseResult result, BlazePoseOrientation.Remap remap)
        {
            int n = BlazePoseSkeleton.NumKeypoints;
            for (int i = 0; i < n; i++)
            {
                m_SampledDepth[i] = 0f;
                m_DepthValid[i] = false;
            }

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
                for (int i = 0; i < n; i++)
                {
                    Vector2 px = BlazePoseOrientation.SensorUvToPixel(result.landmarks[i].imageUV, depthRes, remap);
                    int dx = Mathf.Clamp(Mathf.RoundToInt(px.x), 0, w - 1);
                    int dy = Mathf.Clamp(Mathf.RoundToInt(px.y), 0, h - 1);
                    float d = depthData[dy * rowStride + dx];
                    if (d >= minDepth && d <= maxDepth)
                    {
                        m_SampledDepth[i] = d;
                        m_DepthValid[i] = true;
                    }
                }
            }
            finally
            {
                depthImage.Dispose();
            }
        }

        float MedianValidDepth()
        {
            int count = 0;
            float sum = 0f;
            for (int i = 0; i < m_DepthValid.Length; i++)
            {
                if (!m_DepthValid[i])
                    continue;
                count++;
                sum += m_SampledDepth[i];
            }
            if (count == 0)
                return 0f;
            return sum / count;
        }
    }
}
