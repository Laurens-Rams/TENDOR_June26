using BodyTracking;
using BodyTracking.Animation;
using BodyTracking.MoveAI;
using BodyTracking.Playback;
using RenderHeads.Media.AVProMovieCapture;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Wires Move AI fusion (API client, coordinator, fused player), AVPro video capture, and
    /// BodyTrackingController references in the active scene. Run via TENDOR / Move AI Fusion / Setup Scene.
    /// </summary>
    public static class MoveAIFusionSceneSetup
    {
        const string FusionRootName = "MoveAIFusion";
        const string VideoCaptureName = "VideoCapture";

        [MenuItem("TENDOR/Move AI Fusion/Setup Scene")]
        public static void SetupSceneMenu()
        {
            if (SetupScene(showDialog: true))
            {
                EditorUtility.DisplayDialog(
                    "Move AI Fusion Setup",
                    "Scene wired for Move AI fusion + AVPro video capture.\n\n" +
                    "Next: set your Move API key on MoveApiClient (MVP only).\n" +
                    "Record a climb on device; after processing (~5–30 min) fused replay is used automatically when available.",
                    "OK");
            }
        }

        [MenuItem("TENDOR/Move AI Fusion/Validate Setup")]
        public static void ValidateMenu()
        {
            var issues = Validate();
            EditorUtility.DisplayDialog(
                issues.Count == 0 ? "Move AI Fusion OK" : "Move AI Fusion Issues",
                issues.Count == 0 ? "All required references are present." : string.Join("\n", issues),
                "OK");
        }

        public static bool SetupScene(bool showDialog = false)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[MoveAIFusionSceneSetup] No active scene.");
                return false;
            }

            var controller = Object.FindAnyObjectByType<BodyTrackingController>();
            if (controller == null)
            {
                var msg = "BodyTrackingController not found. Open NewVersion.unity or add BodyTrackingSystem first.";
                Debug.LogError("[MoveAIFusionSceneSetup] " + msg);
                if (showDialog) EditorUtility.DisplayDialog("Move AI Fusion Setup", msg, "OK");
                return false;
            }

            var xrOrigin = Object.FindAnyObjectByType<XROrigin>();
            var cameraManager = EnsureCameraManager(xrOrigin);
            if (cameraManager == null)
            {
                const string msg = "Could not find or create ARCameraManager. Ensure an XR Origin (AR Rig) with a Camera exists in the scene.";
                Debug.LogError("[MoveAIFusionSceneSetup] " + msg);
                if (showDialog) EditorUtility.DisplayDialog("Move AI Fusion Setup", msg, "OK");
                return false;
            }

            FixGlobals(cameraManager);

            var fusionRoot = FindOrCreateChild(controller.transform, FusionRootName);
            var moveApi = GetOrAdd<MoveApiClient>(fusionRoot);
            var coordinator = GetOrAdd<MoveAIFusionCoordinator>(fusionRoot);
            var fusedPlayer = GetOrAdd<FusedCharacterPlayer>(fusionRoot);

            var videoGo = FindOrCreateVideoCapture(controller.transform);
            videoGo.SetActive(true);
            var capture = GetOrAdd<CaptureFromTexture>(videoGo);
            var videoRecorder = GetOrAdd<VideoRecorder>(videoGo);
            ConfigureAvProCapture(capture);
            WireVideoRecorder(videoRecorder, cameraManager, capture);

            WireFusionStack(controller, coordinator, moveApi, fusedPlayer, videoRecorder);
            WireFusedPlayerCharacter(fusedPlayer);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = fusionRoot;
            Debug.Log("[MoveAIFusionSceneSetup] Scene setup complete.");
            return true;
        }

        static System.Collections.Generic.List<string> Validate()
        {
            var issues = new System.Collections.Generic.List<string>();
            var controller = Object.FindAnyObjectByType<BodyTrackingController>();
            if (controller == null) { issues.Add("Missing BodyTrackingController"); return issues; }

            var so = new SerializedObject(controller);
            if (so.FindProperty("fusionCoordinator").objectReferenceValue == null)
                issues.Add("BodyTrackingController.fusionCoordinator not assigned");
            if (so.FindProperty("videoRecorder").objectReferenceValue == null)
                issues.Add("BodyTrackingController.videoRecorder not assigned");

            var api = Object.FindAnyObjectByType<MoveApiClient>();
            if (api == null)
                issues.Add("MoveApiClient missing");
            else if (!api.HasApiKey)
                issues.Add("MoveApiClient has no API key (required for fusion submit)");

            var vr = Object.FindAnyObjectByType<VideoRecorder>();
            if (vr == null)
                issues.Add("VideoRecorder missing");
            else
            {
                var cap = vr.GetComponent<CaptureFromTexture>();
                if (cap == null)
                    issues.Add("VideoRecorder missing CaptureFromTexture on same GameObject");
            }

            if (Globals.CameraManager == null && Object.FindAnyObjectByType<ARCameraManager>() == null)
                issues.Add("ARCameraManager not wired (Globals or scene)");

            return issues;
        }

        /// <summary>
        /// AR Foundation expects ARCameraManager on the AR Camera (child of XR Origin), not on the origin root.
        /// The project's XR Origin (AR Rig) prefab often omits it — we add it here when missing.
        /// </summary>
        public static ARCameraManager EnsureCameraManager(XROrigin xrOrigin)
        {
            var existing = Object.FindAnyObjectByType<ARCameraManager>();
            if (existing != null)
                return existing;

            if (xrOrigin == null)
                return null;

            var onOrigin = xrOrigin.GetComponent<ARCameraManager>();
            if (onOrigin != null)
                return onOrigin;

            Camera cam = xrOrigin.Camera;
            if (cam == null)
                cam = xrOrigin.GetComponentInChildren<Camera>(true);
            if (cam == null)
            {
                Debug.LogError("[MoveAIFusionSceneSetup] No Camera under XR Origin.");
                return null;
            }

            var added = Undo.AddComponent<ARCameraManager>(cam.gameObject);
            Debug.Log($"[MoveAIFusionSceneSetup] Added ARCameraManager to '{cam.gameObject.name}' (required for video capture).");
            return added;
        }

        static void FixGlobals(ARCameraManager cameraManager)
        {
            var globals = Object.FindAnyObjectByType<Globals>();
            if (globals == null) return;

            var so = new SerializedObject(globals);
            so.FindProperty("cameraManager").objectReferenceValue = cameraManager;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[MoveAIFusionSceneSetup] Wired Globals.cameraManager");
        }

        static GameObject FindOrCreateChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t.gameObject;

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            return go;
        }

        static GameObject FindOrCreateVideoCapture(Transform parent)
        {
            var existing = Object.FindAnyObjectByType<VideoRecorder>();
            if (existing != null)
                return existing.gameObject;

            return FindOrCreateChild(parent, VideoCaptureName);
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) return c;
            return Undo.AddComponent<T>(go);
        }

        static void ConfigureAvProCapture(CaptureFromTexture capture)
        {
            var so = new SerializedObject(capture);
            so.FindProperty("_outputFolderType").enumValueIndex = (int)CaptureBase.OutputPath.RelativeToPersistentData;
            so.FindProperty("_outputFolderPath").stringValue = "BodyTrackingVideos";
            so.FindProperty("_filenamePrefix").stringValue = "tendor_climb";
            so.FindProperty("_appendFilenameTimestamp").boolValue = true;
            so.FindProperty("_filenameExtension").stringValue = "mp4";
            so.FindProperty("_frameRate").floatValue = 30f;
            so.FindProperty("_isRealTime").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void WireVideoRecorder(VideoRecorder vr, ARCameraManager cm, CaptureFromTexture capture)
        {
            var so = new SerializedObject(vr);
            so.FindProperty("capture").objectReferenceValue = capture;
            so.FindProperty("cameraManager").objectReferenceValue = cm;
            so.FindProperty("configureCaptureDefaults").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void WireFusionStack(
            BodyTrackingController controller,
            MoveAIFusionCoordinator coordinator,
            MoveApiClient api,
            FusedCharacterPlayer player,
            VideoRecorder video)
        {
            var coordSo = new SerializedObject(coordinator);
            coordSo.FindProperty("moveApiClient").objectReferenceValue = api;
            coordSo.FindProperty("fusedPlayer").objectReferenceValue = player;
            coordSo.ApplyModifiedPropertiesWithoutUndo();

            var ctrlSo = new SerializedObject(controller);
            ctrlSo.FindProperty("fusionCoordinator").objectReferenceValue = coordinator;
            ctrlSo.FindProperty("videoRecorder").objectReferenceValue = video;
            ctrlSo.ApplyModifiedPropertiesWithoutUndo();
        }

        static void WireFusedPlayerCharacter(FusedCharacterPlayer fusedPlayer)
        {
            var fbx = Object.FindAnyObjectByType<FBXCharacterController>();
            Transform characterRoot = null;

            if (fbx != null && fbx.CharacterRootForEditor != null)
                characterRoot = fbx.CharacterRootForEditor.transform;

            if (characterRoot == null)
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (!go.scene.IsValid()) continue;
                    if (go.name == "NewBody" || go.name == "Newbody")
                    {
                        characterRoot = go.transform;
                        break;
                    }
                }
            }

            var so = new SerializedObject(fusedPlayer);
            if (characterRoot != null)
                so.FindProperty("characterRoot").objectReferenceValue = characterRoot;
            if (fbx != null)
                so.FindProperty("fbxCharacterController").objectReferenceValue = fbx;
            so.FindProperty("autoFindCharacter").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            if (characterRoot == null)
                Debug.LogWarning("[MoveAIFusionSceneSetup] No character root found for FusedCharacterPlayer — assign manually or run FBXCharacterController.Initialize on device.");
        }
    }
}
