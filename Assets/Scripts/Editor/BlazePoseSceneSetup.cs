using BodyTracking.AI;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Adds the BlazePose pipeline GameObject and AROcclusionManager on the AR camera (debug overlays only;
    /// recording uses ARKit). Run via TENDOR > BlazePose > Setup Scene.
    /// </summary>
    public static class BlazePoseSceneSetup
    {
        const string PipelineName = "BlazePosePipeline";
        const string ModelsFolder = "Assets/AI/BlazePose";
        const string PoseDetectorPath = ModelsFolder + "/pose_detection.onnx";
        const string PoseLandmarkerPath = ModelsFolder + "/pose_landmarks_detector_heavy.onnx";
        const string AnchorsPath = ModelsFolder + "/anchors.csv";

        [MenuItem("TENDOR/BlazePose/Setup Scene")]
        public static void SetupSceneMenu()
        {
            if (SetupScene(showDialog: true))
                EditorUtility.DisplayDialog("BlazePose Setup", "BlazePose pipeline is wired in the active scene.\n\nBuild to a LiDAR iPhone to validate the overlay (Stage 1) and 3D joints (Stage 2).", "OK");
        }

        /// <summary>Called automatically once when .setup_pending marker exists (see BlazePoseAutoSetup).</summary>
        public static bool SetupScene(bool showDialog = false)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[BlazePoseSceneSetup] No active scene.");
                return false;
            }

            var poseDetector = AssetDatabase.LoadAssetAtPath<ModelAsset>(PoseDetectorPath);
            var poseLandmarker = AssetDatabase.LoadAssetAtPath<ModelAsset>(PoseLandmarkerPath);
            var anchors = AssetDatabase.LoadAssetAtPath<TextAsset>(AnchorsPath);
            if (poseDetector == null || poseLandmarker == null || anchors == null)
            {
                var msg = "[BlazePoseSceneSetup] Missing model assets under Assets/AI/BlazePose/. " +
                          "Need pose_detection.onnx, pose_landmarks_detector_heavy.onnx, anchors.csv. " +
                          "Reimport ONNX files (Inference Engine importer) if they show as generic assets.";
                Debug.LogError(msg);
                if (showDialog)
                    EditorUtility.DisplayDialog("BlazePose Setup", msg, "OK");
                return false;
            }

            var cameraManager = Object.FindAnyObjectByType<ARCameraManager>();
            if (cameraManager == null)
            {
                Debug.LogError("[BlazePoseSceneSetup] ARCameraManager not found in scene.");
                if (showDialog)
                    EditorUtility.DisplayDialog("BlazePose Setup", "ARCameraManager not found. Open NewVersion.unity first.", "OK");
                return false;
            }

            var pipeline = FindOrCreatePipeline();
            var runner = GetOrAdd<BlazePoseRunner>(pipeline);
            var overlay = GetOrAdd<BlazePose2DOverlay>(pipeline);
            var depthLift = GetOrAdd<BlazePoseDepthLift>(pipeline);
            var visualizer = GetOrAdd<BlazePose3DVisualizer>(pipeline);

            SetRunnerAssets(runner, poseDetector, poseLandmarker, anchors, cameraManager);
            SetRunnerTuning(runner);
            SetReference(overlay, "runner", runner);
            SetOverlayDefaults(overlay);
            SetReference(depthLift, "runner", runner);
            SetReference(depthLift, "cameraManager", cameraManager);
            SetDepthLiftDefaults(depthLift);
            SetReference(visualizer, "depthLift", depthLift);

            var occlusion = GetOrAdd<AROcclusionManager>(cameraManager.gameObject);
            SetOcclusionDefaults(occlusion);
            SetReference(depthLift, "occlusionManager", occlusion);

            var arCamera = cameraManager.GetComponent<Camera>();
            if (arCamera != null)
                SetReference(depthLift, "arCamera", arCamera);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = pipeline;
            Debug.Log("[BlazePoseSceneSetup] BlazePose pipeline configured in scene.");
            return true;
        }

        static GameObject FindOrCreatePipeline()
        {
            var existing = GameObject.Find(PipelineName);
            if (existing != null)
                return existing;

            var app = GameObject.Find("App");
            var pipeline = new GameObject(PipelineName);
            if (app != null)
                pipeline.transform.SetParent(app.transform, false);
            Undo.RegisterCreatedObjectUndo(pipeline, "Create BlazePose Pipeline");
            return pipeline;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null)
                return c;
            return Undo.AddComponent<T>(go);
        }

        static void SetRunnerAssets(BlazePoseRunner runner, ModelAsset detector, ModelAsset landmarker, TextAsset anchors, ARCameraManager cm)
        {
            var so = new SerializedObject(runner);
            so.FindProperty("poseDetector").objectReferenceValue = detector;
            so.FindProperty("poseLandmarker").objectReferenceValue = landmarker;
            so.FindProperty("anchorsCSV").objectReferenceValue = anchors;
            so.FindProperty("cameraManager").objectReferenceValue = cm;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetRunnerTuning(BlazePoseRunner runner)
        {
            var so = new SerializedObject(runner);
            so.FindProperty("scoreThreshold").floatValue = 0.45f;
            so.FindProperty("inferenceInterval").floatValue = 0.04f;
            so.FindProperty("maxInferenceDimension").intValue = 640;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetOverlayDefaults(BlazePose2DOverlay overlay)
        {
            var so = new SerializedObject(overlay);
            so.FindProperty("autoOrient").boolValue = true;
            so.FindProperty("visibilityThreshold").floatValue = 0.3f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetDepthLiftDefaults(BlazePoseDepthLift depthLift)
        {
            var so = new SerializedObject(depthLift);
            so.FindProperty("autoOrient").boolValue = true;
            so.FindProperty("fallbackZScale").floatValue = 1.5f;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetReference(Object target, string propertyName, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(propertyName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void SetOcclusionDefaults(AROcclusionManager occlusion)
        {
            var so = new SerializedObject(occlusion);
            var envDepth = so.FindProperty("m_EnvironmentDepthMode");
            if (envDepth != null)
                envDepth.intValue = 1; // Fastest

            so.ApplyModifiedPropertiesWithoutUndo();
        }

    }

    /// <summary>Runs scene setup once after the agent drops a marker file (deleted after success).</summary>
    [InitializeOnLoad]
    static class BlazePoseAutoSetup
    {
        const string MarkerPath = "Assets/AI/BlazePose/.setup_pending";

        static BlazePoseAutoSetup()
        {
            EditorApplication.delayCall += TryAutoSetup;
        }

        static void TryAutoSetup()
        {
            if (!System.IO.File.Exists(MarkerPath))
                return;
            if (BlazePoseSceneSetup.SetupScene(showDialog: false))
            {
                AssetDatabase.DeleteAsset(MarkerPath);
                Debug.Log("[BlazePoseAutoSetup] Automatic scene setup completed.");
            }
        }
    }
}
