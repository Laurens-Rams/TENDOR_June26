using System;
using System.Collections;
using Unity.Collections;
using Unity.Mathematics;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.AI
{
    /// <summary>
    /// On-device BlazePose runner. Acquires the AR camera CPU image, runs the MediaPipe BlazePose detector +
    /// landmark models through Unity Inference Engine (Sentis 2.x), and exposes 33 landmarks in normalized
    /// image space. Pipeline math mirrors the official Unity sentis-samples BlazeDetectionSample (Pose).
    ///
    /// Stage 1 of the BlazePose live pipeline: produces landmarks for the 2D overlay validation gate.
    /// </summary>
    public class BlazePoseRunner : MonoBehaviour
    {
        [Header("Models (import from HuggingFace unity/inference-engine-blaze-pose - see Assets/AI/BlazePose/IMPORT_MODELS.txt)")]
        [Tooltip("pose_detection.onnx imported as a Sentis ModelAsset")]
        [SerializeField] ModelAsset poseDetector;
        [Tooltip("pose_landmarks_detector_full.onnx (or lite/heavy) imported as a Sentis ModelAsset")]
        [SerializeField] ModelAsset poseLandmarker;
        [Tooltip("Detector anchors CSV (2254 lines) imported as a TextAsset")]
        [SerializeField] TextAsset anchorsCSV;

        [Header("Camera source")]
        [Tooltip("Optional. Falls back to Globals.CameraManager when null.")]
        [SerializeField] ARCameraManager cameraManager;
        [Tooltip("CPU image -> texture transformation. Keep None so landmark UVs stay in the native sensor frame " +
                 "used by camera intrinsics + LiDAR depth (Stage 2). Display orientation is handled by the overlay.")]
        [SerializeField] XRCpuImage.Transformation cpuTransformation = XRCpuImage.Transformation.None;

        [Header("Inference")]
        [Tooltip("Backend used for both workers. GPUCompute is fastest in Editor; on iOS device we auto-fallback to CPU because GPUCompute + heavy BlazePose + live AR depth has caused crashes.")]
        [SerializeField] BackendType backend = BackendType.GPUCompute;
        [Tooltip("Minimum seconds between inferences. 0 = every frame (heavy).")]
        [SerializeField, Min(0f)] float inferenceInterval = 0.04f; // ~25 Hz target
        [Tooltip("Detector score above which a pose is accepted.")]
        [SerializeField, Range(0f, 1f)] float scoreThreshold = 0.45f;
        [Tooltip("Longest edge of the camera texture fed to inference. Lower = faster, slightly less accurate.")]
        [SerializeField, Min(256)] int maxInferenceDimension = 640;

        [Header("Debug")]
        [Tooltip("Throttled Console logging of init / camera / detection state to diagnose 'no body'.")]
        [SerializeField] bool verboseLogging = true;
        float m_LastLogTime = -999f;

        public event Action<BlazePoseResult> OnPoseUpdated;
        public BlazePoseResult LatestResult => m_Result;
        public bool IsInitialized => m_Initialized;

        const int k_NumAnchors = 2254;
        const int k_NumKeypoints = BlazePoseSkeleton.NumKeypoints;
        const int k_DetectorInputSize = 224;
        const int k_LandmarkerInputSize = 256;
        // Landmark model emits 5 floats per keypoint: x, y, z, visibility, presence.
        const int k_LandmarkStride = 5;

        float[,] m_Anchors;
        Worker m_PoseDetectorWorker;
        Worker m_PoseLandmarkerWorker;
        Tensor<float> m_DetectorInput;
        Tensor<float> m_LandmarkerInput;

        Texture2D m_CameraTexture;
        Texture2D m_InferenceTexture;
        readonly BlazePoseResult m_Result = new BlazePoseResult();
        bool m_Initialized;
        bool m_Initializing;
        Coroutine m_InitCoroutine;
        bool m_Running;
        float m_LastInferenceTime = -999f;
        int m_FrameCounter;

        // Camera state snapshotted when the CPU image is acquired (inference finishes 1-2 frames later,
        // so consumers must unproject with the pose/intrinsics from acquisition time, not publish time).
        bool m_PendingHasCameraPose;
        Vector3 m_PendingCameraPosition;
        Quaternion m_PendingCameraRotation;
        bool m_PendingHasIntrinsics;
        XRCameraIntrinsics m_PendingIntrinsics;
        float m_PendingCaptureTime;

        void OnEnable()
        {
            if (m_Initialized || m_Initializing)
                return;
            if (m_InitCoroutine != null)
                StopCoroutine(m_InitCoroutine);
            // Defer worker creation a few frames so we don't load the heavy Sentis models on the same
            // frame ARKit switches to the LiDAR-depth camera configuration (that combo has crashed devices).
            m_InitCoroutine = StartCoroutine(DeferredInitialize());
        }

        IEnumerator DeferredInitialize()
        {
            m_Initializing = true;
            for (int i = 0; i < 8; i++)
                yield return null;
            m_InitCoroutine = null;
            Initialize();
            m_Initializing = false;
        }

        static BackendType ResolveBackend(BackendType configured)
        {
#if UNITY_IOS && !UNITY_EDITOR
            // GPUCompute + pose_landmarks_detector_heavy + live AR depth/textures → device OOM/crash observed on A17 Pro.
            if (configured == BackendType.GPUCompute)
                return BackendType.CPU;
#endif
            return configured;
        }

        void Initialize()
        {
            if (m_Initialized)
                return;

            if (poseDetector == null || poseLandmarker == null || anchorsCSV == null)
            {
                Debug.LogWarning("[BlazePoseRunner] Missing model assets. Import BlazePose models/anchors and assign them. See Assets/AI/BlazePose/IMPORT_MODELS.txt.");
                return;
            }

            var resolvedBackend = ResolveBackend(backend);
            m_Anchors = BlazeUtils.LoadAnchors(anchorsCSV.text, k_NumAnchors);

            // Detector: append argmax/score filtering so the worker returns (idx, score, box) for the best pose.
            var poseDetectorModel = ModelLoader.Load(poseDetector);
            var graph = new FunctionalGraph();
            var input = graph.AddInput(poseDetectorModel, 0);
            var outputs = Functional.Forward(poseDetectorModel, input);
            var boxes = outputs[0];  // (1, 2254, 12)
            var scores = outputs[1]; // (1, 2254, 1)
            var idx_scores_boxes = BlazeUtils.ArgMaxFiltering(boxes, scores);
            poseDetectorModel = graph.Compile(idx_scores_boxes.Item1, idx_scores_boxes.Item2, idx_scores_boxes.Item3);
            m_PoseDetectorWorker = new Worker(poseDetectorModel, resolvedBackend);

            var poseLandmarkerModel = ModelLoader.Load(poseLandmarker);
            m_PoseLandmarkerWorker = new Worker(poseLandmarkerModel, resolvedBackend);

            m_DetectorInput = new Tensor<float>(new TensorShape(1, k_DetectorInputSize, k_DetectorInputSize, 3));
            m_LandmarkerInput = new Tensor<float>(new TensorShape(1, k_LandmarkerInputSize, k_LandmarkerInputSize, 3));

            m_Initialized = true;
            Debug.Log($"[BlazePoseRunner] Initialized OK. anchors={m_Anchors.GetLength(0)}, backend={resolvedBackend} (configured={backend}), interval={inferenceInterval}, scoreThreshold={scoreThreshold}");
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

        async void Update()
        {
            if (!m_Initialized)
            {
                if (ShouldLog())
                    Debug.LogWarning("[BlazePoseRunner] Not initialized (models/anchors missing or init threw). Check earlier errors.");
                return;
            }
            if (m_Running)
                return;
            if (inferenceInterval > 0f && Time.time - m_LastInferenceTime < inferenceInterval)
                return;
            if (!TryUpdateCameraTexture())
            {
                if (ShouldLog())
                {
                    var cm = cameraManager != null ? cameraManager : Globals.CameraManager;
                    Debug.LogWarning($"[BlazePoseRunner] No CPU image. cameraManager={(cm == null ? "NULL" : cm.name)}, " +
                                     $"enabled={(cm != null && cm.enabled)}. Waiting for AR camera frames.");
                }
                return;
            }

            m_Running = true;
            m_LastInferenceTime = Time.time;
            try
            {
                var inferenceTex = GetInferenceTexture(m_CameraTexture);
                await Detect(inferenceTex, m_CameraTexture.width, m_CameraTexture.height);
            }
            catch (OperationCanceledException)
            {
                // Component disabled mid-inference.
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BlazePoseRunner] Inference failed: {e.Message}");
            }
            finally
            {
                m_Running = false;
            }
        }

        bool TryUpdateCameraTexture()
        {
            var cm = cameraManager != null ? cameraManager : Globals.CameraManager;
            if (cm == null)
                return false;
            if (!cm.TryAcquireLatestCpuImage(out XRCpuImage image))
                return false;

            // Snapshot camera state at acquisition (paired with this image, published with the result).
            var camTransform = cm.transform;
            m_PendingHasCameraPose = camTransform != null;
            if (m_PendingHasCameraPose)
            {
                m_PendingCameraPosition = camTransform.position;
                m_PendingCameraRotation = camTransform.rotation;
            }
            m_PendingHasIntrinsics = cm.TryGetIntrinsics(out m_PendingIntrinsics);
            m_PendingCaptureTime = Time.unscaledTime;

            try
            {
                var conversionParams = new XRCpuImage.ConversionParams(image, TextureFormat.RGBA32, cpuTransformation);
                var dims = conversionParams.outputDimensions;
                EnsureCameraTexture(dims.x, dims.y);

                int size = image.GetConvertedDataSize(conversionParams);
                using var buffer = new NativeArray<byte>(size, Allocator.Temp);
                image.Convert(conversionParams, buffer);
                m_CameraTexture.LoadRawTextureData(buffer);
                m_CameraTexture.Apply(false);
            }
            finally
            {
                image.Dispose();
            }

            return true;
        }

        void EnsureCameraTexture(int width, int height)
        {
            if (m_CameraTexture != null && m_CameraTexture.width == width && m_CameraTexture.height == height)
                return;
            if (m_CameraTexture != null)
                Destroy(m_CameraTexture);
            m_CameraTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        Texture GetInferenceTexture(Texture2D source)
        {
            int maxDim = maxInferenceDimension;
            int w = source.width;
            int h = source.height;
            if (Mathf.Max(w, h) <= maxDim)
                return source;

            float scale = maxDim / (float)Mathf.Max(w, h);
            int dw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
            int dh = Mathf.Max(1, Mathf.RoundToInt(h * scale));
            EnsureInferenceTexture(dw, dh);

            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(dw, dh, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            m_InferenceTexture.ReadPixels(new Rect(0, 0, dw, dh), 0, 0);
            m_InferenceTexture.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return m_InferenceTexture;
        }

        void EnsureInferenceTexture(int width, int height)
        {
            if (m_InferenceTexture != null && m_InferenceTexture.width == width && m_InferenceTexture.height == height)
                return;
            if (m_InferenceTexture != null)
                Destroy(m_InferenceTexture);
            m_InferenceTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        async Awaitable Detect(Texture texture, float sensorWidth, float sensorHeight)
        {
            float textureWidth = sensorWidth;
            float textureHeight = sensorHeight;
            var size = Mathf.Max(texture.width, texture.height);

            // Affine transform: detector tensor coords -> image coords (letterboxed square crop).
            var scale = size / (float)k_DetectorInputSize;
            var M = BlazeUtils.mul(
                BlazeUtils.TranslationMatrix(0.5f * (new float2(texture.width, texture.height) + new float2(-size, size))),
                BlazeUtils.ScaleMatrix(new float2(scale, -scale)));
            BlazeUtils.SampleImageAffine(texture, m_DetectorInput, M);

            m_PoseDetectorWorker.Schedule(m_DetectorInput);

            var outputIdxAwaitable = (m_PoseDetectorWorker.PeekOutput(0) as Tensor<int>).ReadbackAndCloneAsync();
            var outputScoreAwaitable = (m_PoseDetectorWorker.PeekOutput(1) as Tensor<float>).ReadbackAndCloneAsync();
            var outputBoxAwaitable = (m_PoseDetectorWorker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync();

            using var outputIdx = await outputIdxAwaitable;
            using var outputScore = await outputScoreAwaitable;
            using var outputBox = await outputBoxAwaitable;

            float score = outputScore[0];
            if (ShouldLog())
                Debug.Log($"[BlazePoseRunner] Detector ran. tex={texture.width}x{texture.height}, score={score:F3} (threshold={scoreThreshold})");
            if (score < scoreThreshold)
            {
                PublishInvalid(textureWidth, textureHeight, score);
                return;
            }

            var idx = outputIdx[0];
            var anchorPosition = k_DetectorInputSize * new float2(m_Anchors[idx, 0], m_Anchors[idx, 1]);

            // Two keypoints (hip-center + scale point) define the rotated crop fed to the landmark model.
            var kp1_ImageSpace = BlazeUtils.mul(M, anchorPosition + new float2(outputBox[0, 0, 4 + 2 * 0 + 0], outputBox[0, 0, 4 + 2 * 0 + 1]));
            var kp2_ImageSpace = BlazeUtils.mul(M, anchorPosition + new float2(outputBox[0, 0, 4 + 2 * 1 + 0], outputBox[0, 0, 4 + 2 * 1 + 1]));
            var delta_ImageSpace = kp2_ImageSpace - kp1_ImageSpace;
            var dscale = 1.25f;
            var radius = dscale * math.length(delta_ImageSpace);
            var theta = math.atan2(delta_ImageSpace.y, delta_ImageSpace.x);
            var origin2 = new float2(0.5f * k_LandmarkerInputSize, 0.5f * k_LandmarkerInputSize);
            var scale2 = radius / (0.5f * k_LandmarkerInputSize);
            var M2 = BlazeUtils.mul(
                BlazeUtils.mul(
                    BlazeUtils.mul(BlazeUtils.TranslationMatrix(kp1_ImageSpace), BlazeUtils.ScaleMatrix(new float2(scale2, -scale2))),
                    BlazeUtils.RotationMatrix(0.5f * Mathf.PI - theta)),
                BlazeUtils.TranslationMatrix(-origin2));
            BlazeUtils.SampleImageAffine(texture, m_LandmarkerInput, M2);

            m_PoseLandmarkerWorker.Schedule(m_LandmarkerInput);

            var landmarksAwaitable = (m_PoseLandmarkerWorker.PeekOutput("Identity") as Tensor<float>).ReadbackAndCloneAsync();
            using var landmarks = await landmarksAwaitable; // (1, 195)

            m_Result.valid = true;
            m_Result.score = score;
            m_Result.textureWidth = textureWidth;
            m_Result.textureHeight = textureHeight;
            m_Result.frameId = ++m_FrameCounter;
            ApplyPendingCameraState();

            for (var i = 0; i < k_NumKeypoints; i++)
            {
                var position_ImageSpace = BlazeUtils.mul(M2, new float2(landmarks[k_LandmarkStride * i + 0], landmarks[k_LandmarkStride * i + 1]));
                float visibility = landmarks[k_LandmarkStride * i + 3];
                float presence = landmarks[k_LandmarkStride * i + 4];

                // UV is in full sensor space; uniform downscale preserves normalized coordinates.
                m_Result.landmarks[i] = new BlazeLandmark
                {
                    imageUV = new Vector2(position_ImageSpace.x / texture.width, position_ImageSpace.y / texture.height),
                    zRelative = landmarks[k_LandmarkStride * i + 2],
                    visibility = visibility,
                    presence = presence,
                    tracked = visibility > 0.3f
                };
            }

            OnPoseUpdated?.Invoke(m_Result);
        }

        void PublishInvalid(float textureWidth, float textureHeight, float score)
        {
            m_Result.valid = false;
            m_Result.score = score;
            m_Result.textureWidth = textureWidth;
            m_Result.textureHeight = textureHeight;
            m_Result.frameId = ++m_FrameCounter;
            ApplyPendingCameraState();
            OnPoseUpdated?.Invoke(m_Result);
        }

        void ApplyPendingCameraState()
        {
            m_Result.hasCameraPose = m_PendingHasCameraPose;
            m_Result.cameraPosition = m_PendingCameraPosition;
            m_Result.cameraRotation = m_PendingCameraRotation;
            m_Result.hasIntrinsics = m_PendingHasIntrinsics;
            m_Result.intrinsics = m_PendingIntrinsics;
            m_Result.captureTime = m_PendingCaptureTime;
        }

        void OnDisable()
        {
            if (m_InitCoroutine != null)
            {
                StopCoroutine(m_InitCoroutine);
                m_InitCoroutine = null;
                m_Initializing = false;
            }
            Dispose();
        }

        void OnDestroy()
        {
            Dispose();
            if (m_CameraTexture != null)
                Destroy(m_CameraTexture);
            if (m_InferenceTexture != null)
                Destroy(m_InferenceTexture);
        }

        void Dispose()
        {
            m_PoseDetectorWorker?.Dispose();
            m_PoseLandmarkerWorker?.Dispose();
            m_DetectorInput?.Dispose();
            m_LandmarkerInput?.Dispose();
            m_PoseDetectorWorker = null;
            m_PoseLandmarkerWorker = null;
            m_DetectorInput = null;
            m_LandmarkerInput = null;
            m_Initialized = false;
        }
    }
}
