using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.Vision
{
    /// <summary>
    /// Drives the native Apple Vision 3D body-pose plugin from the existing AR camera feed.
    ///
    /// Each throttled tick it acquires the latest AR CPU image, converts it to a BGRA buffer,
    /// disposes the image immediately (so Immersal/image tracking keep their access), then runs
    /// the Vision request on a background thread. Results are camera-relative meters which it
    /// converts to Unity world space using the AR camera pose captured at acquisition time.
    ///
    /// A hold-last-valid window keeps the last good pose for a short time when detection briefly
    /// drops, so the skeleton does not flash empty between frames.
    /// </summary>
    public class VisionBodyPoseRunner : MonoBehaviour
    {
        [Header("Camera source")]
        [Tooltip("Optional. Falls back to Globals.CameraManager when null.")]
        [SerializeField] ARCameraManager cameraManager;
        [Tooltip("Camera whose world pose maps Vision camera-relative joints into the scene. " +
                 "Falls back to the camera on the ARCameraManager, then Camera.main.")]
        [SerializeField] Camera arCamera;
        [Tooltip("LiDAR environment depth source used to place the body at its true distance. " +
                 "Auto-found when null.")]
        [SerializeField] AROcclusionManager occlusionManager;

        [Header("Inference")]
        [Tooltip("Minimum seconds between Vision detections. ~0.06 = ~16 Hz to leave the AR " +
                 "session room for Immersal/image tracking. Recorder samples the latest held pose.")]
        [SerializeField, Min(0f)] float inferenceInterval = 0.06f;
        [Tooltip("CGImagePropertyOrientation passed to Vision so the person appears upright. " +
                 "For a portrait rear camera with a None-transformed CPU image this is 6 (right).")]
        [SerializeField] int cgOrientation = 6;

        [Header("Hold-last-valid")]
        [Tooltip("Seconds to keep returning the last good pose when detection drops, to avoid " +
                 "empty-skeleton flashes. Set 0 to disable.")]
        [SerializeField, Min(0f)] float holdTimeout = 0.25f;

        [Header("Coordinate mapping")]
        [Tooltip("Vision returns +z toward the viewer; Unity camera looks down +z. Leave on to " +
                 "place joints in front of the camera. Flip if the body appears behind you.")]
        [SerializeField] bool invertZ = true;
        [SerializeField] bool invertX = false;
        [SerializeField] bool invertY = false;
        [Tooltip("Roll (degrees, around the camera view axis) applied to Vision joints before mapping " +
                 "to world. Compensates for the image orientation we feed Vision. Try 0 / 90 / 180 / 270 " +
                 "if the skeleton is rotated relative to your body.")]
        [SerializeField] float rollDegrees = 0f;

        [Header("Depth correction (LiDAR)")]
        [Tooltip("Vision estimates distance assuming a ~1.8m person, so its monocular depth (and the " +
                 "resulting body size) is unreliable. When on, the skeleton is scaled each frame so the " +
                 "root sits at the true LiDAR depth measured at the body's screen position. This adapts " +
                 "automatically as the body moves toward/away from the camera.")]
        [SerializeField] bool useLidarDepth = true;
        [Tooltip("Reject LiDAR samples outside this metric range (meters).")]
        [SerializeField] float minDepth = 0.4f;
        [SerializeField] float maxDepth = 8f;
        [Tooltip("Smoothing (0..1) for the depth scale to reduce jitter. 0 = none, 0.5 = heavy.")]
        [SerializeField, Range(0f, 0.95f)] float depthScaleSmoothing = 0.5f;
        [Tooltip("Optional fixed distance (meters) used only when LiDAR depth is off or unavailable. " +
                 "0 = trust Vision's own depth.")]
        [SerializeField, Min(0f)] float fixedBodyDistance = 0f;

        [Header("Debug")]
        [SerializeField] bool verboseLogging = true;

        public VisionBodyWorldPose LatestWorld => m_World;
        public bool IsInitialized => m_Initialized;
        public bool HasFreshPose => m_World != null && m_World.valid;

        bool m_Initialized;
        float m_LastInferenceTime = -999f;
        float m_LastLogTime = -999f;

        // Reused managed BGRA buffer pinned for the native call.
        byte[] m_Buffer;
        int m_BufferWidth;
        int m_BufferHeight;
        int m_BufferStride;

        // Camera pose captured at acquisition time for the in-flight detection.
        Matrix4x4 m_CaptureCameraToWorld;
        float m_CaptureTime;

        // Background detection state.
        volatile bool m_Detecting;
        volatile bool m_ResultReady;
        readonly float[] m_RawPositions = new float[VisionBodyNative.JointCount * 3];
        readonly float[] m_RawConfidences = new float[VisionBodyNative.JointCount];
        int m_RawJointCount;
        float m_RawBodyHeight;
        bool m_RawDetected;

        readonly VisionBodyWorldPose m_World = new VisionBodyWorldPose();
        float m_LastValidTime = -999f;

        // Reused per-frame camera-local joints (after invert + roll) so depth correction can scale them.
        readonly Vector3[] m_CamLocal = new Vector3[VisionBodyNative.JointCount];
        float m_SmoothedDepthScale = 1f;
        string m_LastProbeInfo = "probe n/a";

        void OnEnable()
        {
            m_Initialized = VisionBodyNative.VisionBody_Initialize();
            if (!m_Initialized && verboseLogging)
                Debug.LogWarning("[VisionBodyPoseRunner] Vision 3D body pose unavailable (needs iOS 17+ device build).");

            if (occlusionManager == null)
                occlusionManager = FindAnyObjectByType<AROcclusionManager>();
        }

        void OnDisable()
        {
            VisionBodyNative.VisionBody_Shutdown();
        }

        void Update()
        {
            if (m_ResultReady)
                ConsumeResult();

            if (!m_Detecting && Time.time - m_LastInferenceTime >= inferenceInterval)
                TryStartDetection();

            ApplyHoldTimeout();
        }

        void TryStartDetection()
        {
            var cm = cameraManager != null ? cameraManager : Globals.CameraManager;
            if (cm == null)
            {
                if (verboseLogging && ShouldLog())
                    Debug.LogWarning("[VisionBodyPoseRunner] No ARCameraManager available.");
                return;
            }

            if (!cm.TryAcquireLatestCpuImage(out XRCpuImage image))
                return;

            try
            {
                var conversionParams = new XRCpuImage.ConversionParams(
                    image, TextureFormat.BGRA32, XRCpuImage.Transformation.None);
                var dims = conversionParams.outputDimensions;
                int size = image.GetConvertedDataSize(conversionParams);

                if (m_Buffer == null || m_Buffer.Length != size)
                    m_Buffer = new byte[size];

                using (var tmp = new NativeArray<byte>(size, Allocator.Temp))
                {
                    image.Convert(conversionParams, tmp);
                    tmp.CopyTo(m_Buffer);
                }

                m_BufferWidth = dims.x;
                m_BufferHeight = dims.y;
                m_BufferStride = size / dims.y;
            }
            catch (Exception e)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[VisionBodyPoseRunner] CPU image convert failed: {e.Message}");
                image.Dispose();
                return;
            }
            finally
            {
                image.Dispose();
            }

            var cam = ResolveCamera();
            m_CaptureCameraToWorld = cam != null ? cam.transform.localToWorldMatrix : Matrix4x4.identity;
            m_CaptureTime = Time.time;
            m_LastInferenceTime = Time.time;
            m_Detecting = true;
            m_ResultReady = false;

            _ = DetectAsync();
        }

        async Task DetectAsync()
        {
            int width = m_BufferWidth;
            int height = m_BufferHeight;
            int stride = m_BufferStride;
            int orientation = cgOrientation;

            await Task.Run(() =>
            {
                GCHandle handle = GCHandle.Alloc(m_Buffer, GCHandleType.Pinned);
                try
                {
                    IntPtr ptr = handle.AddrOfPinnedObject();
                    m_RawDetected = VisionBodyNative.VisionBody_Detect(
                        ptr, width, height, stride, orientation,
                        m_RawPositions, m_RawConfidences,
                        out m_RawJointCount, out m_RawBodyHeight);
                }
                catch (Exception)
                {
                    m_RawDetected = false;
                    m_RawJointCount = 0;
                }
                finally
                {
                    handle.Free();
                }
            });

            m_ResultReady = true;
        }

        void ConsumeResult()
        {
            m_ResultReady = false;
            m_Detecting = false;

            if (m_RawDetected && m_RawJointCount == VisionBodyNative.JointCount)
            {
                // Roll compensation around the camera view (Z) axis for the image orientation we fed Vision.
                Quaternion roll = Quaternion.AngleAxis(rollDegrees, Vector3.forward);
                int rootIdx = VisionBodySkeleton.Root;

                // 1) Camera-local joints (Unity convention: +x right, +y up, +z forward).
                for (int i = 0; i < VisionBodyNative.JointCount; i++)
                    m_CamLocal[i] = roll * MapLocal(i);

                float visionDepth = m_CamLocal[rootIdx].z; // forward distance Vision assumed for the root

                // 2) Decide the true root distance. Prefer LiDAR; fall back to a fixed distance, else Vision.
                float scale = 1f;
                float measuredDepth = 0f;
                bool fromLidar = false;
                if (visionDepth > 0.01f)
                {
                    if (useLidarDepth)
                    {
                        measuredDepth = SampleLidarDepth(m_CamLocal[rootIdx]);
                        if (measuredDepth > 0f)
                            fromLidar = true;
                    }
                    if (measuredDepth <= 0f && fixedBodyDistance > 0f)
                        measuredDepth = fixedBodyDistance;

                    if (measuredDepth > 0f)
                    {
                        float target = measuredDepth / visionDepth;
                        // Smooth the scale to avoid depth jitter snapping the whole body.
                        m_SmoothedDepthScale = Mathf.Lerp(target, m_SmoothedDepthScale, depthScaleSmoothing);
                        scale = m_SmoothedDepthScale;
                    }
                }

                // 3) Map (optionally scaled) joints to world. Scaling about the camera origin keeps the
                //    on-screen projection identical while placing the body at the measured distance and
                //    giving it a physically-consistent size.
                int tracked = 0;
                for (int i = 0; i < VisionBodyNative.JointCount; i++)
                {
                    bool ok = m_RawConfidences[i] > 0.5f;
                    m_World.joints[i].worldPosition = m_CaptureCameraToWorld.MultiplyPoint3x4(m_CamLocal[i] * scale);
                    m_World.joints[i].tracked = ok;
                    if (ok) tracked++;
                }

                m_World.valid = tracked > 0;
                m_World.held = false;
                m_World.timestamp = m_CaptureTime;
                m_World.bodyHeight = m_RawBodyHeight;
                m_World.trackedJointCount = tracked;

                if (m_World.valid)
                    m_LastValidTime = Time.time;

                if (verboseLogging && ShouldLog())
                {
                    string depthSrc = fromLidar ? "LiDAR" : (measuredDepth > 0f ? "fixed" : "vision");
                    Debug.Log($"[VisionBodyPoseRunner] {tracked}/17 joints | visionDepth={visionDepth:F2}m | " +
                              $"measured({depthSrc})={measuredDepth:F2}m | scale={scale:F2} | " +
                              $"rootWorld={m_World.joints[rootIdx].worldPosition:F2} | {m_LastProbeInfo}");
                }
            }
            else if (verboseLogging && ShouldLog())
            {
                Debug.Log("[VisionBodyPoseRunner] No body this frame.");
            }
        }

        Vector3 MapLocal(int jointIndex)
        {
            return new Vector3(
                m_RawPositions[jointIndex * 3 + 0] * (invertX ? -1f : 1f),
                m_RawPositions[jointIndex * 3 + 1] * (invertY ? -1f : 1f),
                m_RawPositions[jointIndex * 3 + 2] * (invertZ ? -1f : 1f));
        }

        /// <summary>
        /// Returns the true LiDAR depth (meters) at the body root's projected sensor pixel, or 0 when
        /// unavailable / out of range. Works entirely in sensor/intrinsics space so it is independent of
        /// the aspect-fill AR background cropping. Must be called on the main thread.
        /// </summary>
        float SampleLidarDepth(Vector3 camLocalRoot)
        {
            if (occlusionManager == null || camLocalRoot.z <= 0.01f)
                return 0f;

            var cm = cameraManager != null ? cameraManager : Globals.CameraManager;
            if (cm == null || !cm.TryGetIntrinsics(out XRCameraIntrinsics intr))
                return 0f;

            // camLocal (Unity cam: +x right, +y up, +z forward) -> intrinsics pixel.
            float px = intr.principalPoint.x + intr.focalLength.x * (camLocalRoot.x / camLocalRoot.z);
            float py = intr.principalPoint.y + intr.focalLength.y * (-camLocalRoot.y / camLocalRoot.z);
            Vector2 portraitUv = new Vector2(px / intr.resolution.x, py / intr.resolution.y);

            if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
                return 0f;

            float result = 0f;
            try
            {
                int w = depthImage.width;
                int h = depthImage.height;

                var remap = BodyTracking.AI.BlazePoseOrientation.DetectRemap(w, h, intr.resolution);
                Vector2 sensorUv = InverseRemap(portraitUv, remap);

                int cx = Mathf.RoundToInt(sensorUv.x * w);
                int cy = Mathf.RoundToInt(sensorUv.y * h);

                var plane = depthImage.GetPlane(0);
                var depthData = plane.data.Reinterpret<float>(UnsafeUtility.SizeOf<byte>());
                int rowStride = plane.rowStride / sizeof(float);
                if (rowStride <= 0) rowStride = w;

                // Median over a small neighborhood for robustness against edges/noise.
                float best = 0f;
                int count = 0;
                float sum = 0f;
                for (int oy = -2; oy <= 2; oy++)
                for (int ox = -2; ox <= 2; ox++)
                {
                    int sx = Mathf.Clamp(cx + ox, 0, w - 1);
                    int sy = Mathf.Clamp(cy + oy, 0, h - 1);
                    float d = depthData[sy * rowStride + sx];
                    if (d >= minDepth && d <= maxDepth)
                    {
                        sum += d;
                        count++;
                    }
                }
                if (count > 0)
                    best = sum / count;
                result = best;

                m_LastProbeInfo = $"probe sensorUv={sensorUv:F2} px=({cx},{cy})/{w}x{h} hits={count}";
            }
            finally
            {
                depthImage.Dispose();
            }

            return result;
        }

        static Vector2 InverseRemap(Vector2 uv, BodyTracking.AI.BlazePoseOrientation.Remap r)
        {
            if (r.flipY) uv.y = 1f - uv.y;
            if (r.flipX) uv.x = 1f - uv.x;
            if (r.swapXY) uv = new Vector2(uv.y, uv.x);
            return uv;
        }

        void ApplyHoldTimeout()
        {
            if (!m_World.valid)
                return;

            float age = Time.time - m_LastValidTime;
            if (age > holdTimeout)
            {
                // Hold window expired without a fresh detection: report no body.
                m_World.valid = false;
                m_World.held = false;
                m_World.trackedJointCount = 0;
            }
            else if (age > 0f)
            {
                // No fresh detection this frame, but still within the hold window:
                // keep returning the last good pose, flagged as held.
                m_World.held = true;
            }
        }

        Camera ResolveCamera()
        {
            if (arCamera != null)
                return arCamera;
            var cm = cameraManager != null ? cameraManager : Globals.CameraManager;
            if (cm != null)
            {
                var c = cm.GetComponent<Camera>();
                if (c != null)
                    return c;
            }
            return Camera.main;
        }

        bool ShouldLog()
        {
            if (Time.time - m_LastLogTime < 1f)
                return false;
            m_LastLogTime = Time.time;
            return true;
        }
    }
}
