using System.Collections.Generic;
using System.IO;
using BodyTracking.AI;
using BodyTracking.Recording;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

namespace BodyTracking.Editor
{
    /// <summary>
    /// One-click wiring of the BlazePose + LiDAR hip-tracking pipeline. Auto-discovers whatever model files
    /// were dropped into Assets/AI/BlazePose (any landmark variant: heavy > full > lite; any anchors csv),
    /// wires the BlazePosePipeline GameObject + AROcclusionManager, and leaves the runner/debug components
    /// disabled (BodyTrackingController enables the runner on demand while arming/recording).
    ///
    /// Run via TENDOR > BlazePose > Setup Scene, or automatically on editor reload while the
    /// .setup_pending marker exists in the models folder.
    /// </summary>
    public static class BlazePoseSceneSetup
    {
        const string PipelineName = "BlazePosePipeline";
        const string ModelsFolder = "Assets/AI/BlazePose";
        const int ExpectedAnchorCount = 2254;

        [MenuItem("TENDOR/BlazePose/Setup Scene")]
        public static void SetupSceneMenu()
        {
            if (SetupScene(showDialog: true))
                EditorUtility.DisplayDialog("BlazePose Setup",
                    "BlazePose pipeline is wired in the active scene.\n\n" +
                    "Recording uses the LiDAR hip source by default (BodyTrackingRecorder > Pose Source Mode). " +
                    "Build to a LiDAR iPhone to test.", "OK");
        }

        /// <summary>Called automatically while the .setup_pending marker exists (see BlazePoseAutoSetup).</summary>
        public static bool SetupScene(bool showDialog = false, bool quiet = false)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[BlazePoseSceneSetup] No active scene.");
                return false;
            }

            if (!TryResolveModelAssets(out var poseDetector, out var poseLandmarker, out var anchors, out string assetSummary))
            {
                var msg = "[BlazePoseSceneSetup] Missing model assets under " + ModelsFolder + "/.\n" + assetSummary +
                          "\nNeeded: pose_detection.onnx, a pose_landmarks_detector_*.onnx (heavy/full/lite), and the " +
                          "Pose sample's anchors.csv (" + ExpectedAnchorCount + " rows). " +
                          "Reimport the ONNX files (Inference Engine importer) if they show as generic assets.";
                if (quiet)
                    Debug.Log(msg); // auto-retry path: informational, not an error on every reload
                else
                    Debug.LogError(msg);
                if (showDialog)
                    EditorUtility.DisplayDialog("BlazePose Setup", msg, "OK");
                return false;
            }

            var xrOrigin = Object.FindAnyObjectByType<XROrigin>();
            var cameraManager = MoveAIFusionSceneSetup.EnsureCameraManager(xrOrigin);
            if (cameraManager == null)
            {
                Debug.LogError("[BlazePoseSceneSetup] ARCameraManager not found and could not be created.");
                if (showDialog)
                    EditorUtility.DisplayDialog("BlazePose Setup", "ARCameraManager missing. Ensure XR Origin (AR Rig) with a Camera is in the scene.", "OK");
                return false;
            }

            var pipeline = FindOrCreatePipeline();
            pipeline.SetActive(true);

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
            SetReference(depthLift, "occlusionManager", occlusion);

            var arCamera = cameraManager.GetComponent<Camera>();
            if (arCamera != null)
                SetReference(depthLift, "arCamera", arCamera);

            // Enable-on-demand: the controller turns the runner on only while arming/recording with the
            // LidarHip source; the 2D/3D debug visualizers stay off unless manually enabled for debugging.
            runner.enabled = false;
            overlay.enabled = false;
            depthLift.enabled = false;
            visualizer.enabled = false;

            // Default the recorder to the LiDAR hip source so the wiring takes effect without manual steps.
            var recorder = Object.FindAnyObjectByType<BodyTrackingRecorder>(FindObjectsInactive.Include);
            if (recorder != null)
            {
                var so = new SerializedObject(recorder);
                var prop = so.FindProperty("poseSourceMode");
                if (prop != null && prop.intValue != (int)BodyPoseSourceMode.LidarHip)
                {
                    prop.intValue = (int)BodyPoseSourceMode.LidarHip;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // LidarHip needs environment depth, not ARKit 3D body tracking — keep the body manager off in scene.
            var bodyMgr = Object.FindAnyObjectByType<ARHumanBodyManager>(FindObjectsInactive.Include);
            if (bodyMgr != null && bodyMgr.enabled)
            {
                Undo.RecordObject(bodyMgr, "Disable ARHumanBodyManager for LidarHip");
                bodyMgr.enabled = false;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = pipeline;
            Debug.Log($"[BlazePoseSceneSetup] BlazePose pipeline configured.\n{assetSummary}");
            return true;
        }

        /// <summary>
        /// Find the model/anchor assets anywhere under the models folder, tolerating filename variants:
        /// detector = ModelAsset containing "detection" (not "landmark"), landmarker = best available variant
        /// (heavy > full > lite > any), anchors = a csv TextAsset with the expected 2254 anchor rows.
        /// </summary>
        static bool TryResolveModelAssets(out ModelAsset detector, out ModelAsset landmarker, out TextAsset anchors, out string summary)
        {
            detector = null;
            landmarker = null;
            anchors = null;

            var models = new List<ModelAsset>();
            foreach (var guid in AssetDatabase.FindAssets("t:ModelAsset", new[] { ModelsFolder }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                    models.Add(asset);
            }

            foreach (var model in models)
            {
                string n = model.name.ToLowerInvariant();
                if (n.Contains("landmark"))
                    landmarker = PickBetterLandmarker(landmarker, model);
                else if (n.Contains("detect"))
                    detector = model;
            }

            // Anchors: prefer a csv whose row count matches the detector's anchor grid.
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset", new[] { ModelsFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (candidate == null)
                    continue;

                int rows = CountNonEmptyLines(candidate.text);
                if (rows == ExpectedAnchorCount)
                {
                    anchors = candidate;
                    break;
                }
                Debug.LogWarning($"[BlazePoseSceneSetup] Skipping '{path}': {rows} rows (expected {ExpectedAnchorCount}). " +
                                 "Make sure you copied the POSE sample's anchors.csv (not Face/Hand).");
            }

            summary = $"  detector:   {(detector != null ? detector.name : "MISSING (pose_detection.onnx)")}\n" +
                      $"  landmarker: {(landmarker != null ? landmarker.name : "MISSING (pose_landmarks_detector_heavy/full/lite.onnx)")}\n" +
                      $"  anchors:    {(anchors != null ? anchors.name + ".csv" : $"MISSING ({ExpectedAnchorCount}-row csv from BlazeDetectionSample/Pose)")}";
            return detector != null && landmarker != null && anchors != null;
        }

        /// <summary>heavy > full > lite > anything else, so the best-accuracy variant wins when several are present.</summary>
        static ModelAsset PickBetterLandmarker(ModelAsset current, ModelAsset candidate)
        {
            if (current == null) return candidate;
            return LandmarkerRank(candidate) > LandmarkerRank(current) ? candidate : current;
        }

        static int LandmarkerRank(ModelAsset model)
        {
            string n = model.name.ToLowerInvariant();
            if (n.Contains("heavy")) return 3;
            if (n.Contains("full")) return 2;
            if (n.Contains("lite")) return 1;
            return 0;
        }

        static int CountNonEmptyLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            foreach (var line in text.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line))
                    count++;
            return count;
        }

        static GameObject FindOrCreatePipeline()
        {
            // GameObject.Find skips inactive objects; search transforms so an inactive pipeline is reused.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name == PipelineName && t.gameObject.scene.IsValid())
                    return t.gameObject;
            }

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
            so.FindProperty("scoreThreshold").floatValue = 0.35f;
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
    }

    /// <summary>
    /// Runs scene setup automatically after each script reload while the marker file exists, so dropping the
    /// model files into Assets/AI/BlazePose is the only manual step. The marker (and the wired scene) are
    /// saved/removed on success.
    /// </summary>
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
            if (!File.Exists(MarkerPath))
                return;

            if (BlazePoseSceneSetup.SetupScene(showDialog: false, quiet: true))
            {
                // Dot-files are invisible to the AssetDatabase, so delete via System.IO.
                File.Delete(MarkerPath);
                if (File.Exists(MarkerPath + ".meta"))
                    File.Delete(MarkerPath + ".meta");

                var scene = EditorSceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isDirty)
                    EditorSceneManager.SaveScene(scene);

                Debug.Log("[BlazePoseAutoSetup] Automatic scene setup completed and scene saved.");
            }
            // else: SetupScene already logged what's missing; it retries on the next import/reload.
        }
    }
}
