using System.Collections.Generic;
using BodyTracking.AR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Wires AR occlusion in the active scene so the character is hidden behind real-world geometry and people.
    /// Adds an <see cref="AROcclusionManager"/> + <see cref="ARCharacterOcclusion"/> to the AR camera and
    /// verifies the active URP renderer has the AR Background renderer feature (which applies occlusion to the
    /// rendered image). Run via TENDOR / Occlusion / Setup Scene.
    /// </summary>
    public static class AROcclusionSetup
    {
        [MenuItem("TENDOR/Occlusion/Setup Scene", priority = 30)]
        public static void SetupSceneMenu()
        {
            bool ok = SetupScene(out string message);
            EditorUtility.DisplayDialog(ok ? "AR Occlusion Setup" : "AR Occlusion Setup — Issues", message, "OK");
        }

        [MenuItem("TENDOR/Occlusion/Validate Setup", priority = 31)]
        public static void ValidateMenu()
        {
            var issues = Validate();
            EditorUtility.DisplayDialog(
                issues.Count == 0 ? "AR Occlusion OK" : "AR Occlusion Issues",
                issues.Count == 0
                    ? "Occlusion is wired: AROcclusionManager + ARCharacterOcclusion present and the URP renderer has the AR Background feature."
                    : string.Join("\n", issues),
                "OK");
        }

        public static bool SetupScene(out string message)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                message = "No active scene open.";
                return false;
            }

            var xrOrigin = Object.FindAnyObjectByType<XROrigin>();
            var cameraManager = MoveAIFusionSceneSetup.EnsureCameraManager(xrOrigin);
            if (cameraManager == null)
            {
                message = "Could not find or create an ARCameraManager. Open the AR scene (XR Origin with a Camera) first.";
                return false;
            }

            var camGo = cameraManager.gameObject;

            var occlusion = camGo.GetComponent<AROcclusionManager>();
            if (occlusion == null)
                occlusion = Undo.AddComponent<AROcclusionManager>(camGo);

            ConfigureOcclusionManagerDefaults(occlusion);
            ConfigureCameraBeforeOpaques(cameraManager);

            var driver = camGo.GetComponent<ARCharacterOcclusion>();
            if (driver == null)
                driver = Undo.AddComponent<ARCharacterOcclusion>(camGo);

            var cameraBackground = camGo.GetComponent<ARCameraBackground>();
            var driverSo = new SerializedObject(driver);
            driverSo.FindProperty("occlusionManager").objectReferenceValue = occlusion;
            driverSo.FindProperty("cameraManager").objectReferenceValue = cameraManager;
            if (cameraBackground != null)
                driverSo.FindProperty("cameraBackground").objectReferenceValue = cameraBackground;
            var onStart = driverSo.FindProperty("occlusionEnabledOnStart");
            if (onStart != null)
                onStart.boolValue = false;
            driverSo.ApplyModifiedPropertiesWithoutUndo();

            var controller = Object.FindAnyObjectByType<BodyTrackingController>();
            if (controller != null)
            {
                var ctrlSo = new SerializedObject(controller);
                var occRef = ctrlSo.FindProperty("characterOcclusion");
                if (occRef != null)
                {
                    occRef.objectReferenceValue = driver;
                    ctrlSo.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            var fbxController = Object.FindAnyObjectByType<BodyTracking.Animation.FBXCharacterController>();
            if (fbxController != null && fbxController.GetComponent<ARCharacterPlanarShadow>() == null)
                Undo.AddComponent<ARCharacterPlanarShadow>(fbxController.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = camGo;

            bool rendererOk = ActiveRendererHasArBackgroundFeature(out string rendererNote);
            string rendererLine = rendererOk
                ? "URP renderer has the AR Background feature (occlusion will render)."
                : "WARNING: " + rendererNote;

            Debug.Log($"[AROcclusionSetup] Added/configured occlusion on '{camGo.name}'. {rendererLine}");

            message =
                "Occlusion wired on the AR camera.\n\n" +
                "• AROcclusionManager + ARCharacterOcclusion added.\n" +
                "• Occlusion starts OFF (BodyTrackingController enables it during playback only).\n" +
                "• Environment depth (LiDAR) + people occlusion configured for replay.\n" +
                "• " + rendererLine + "\n\n" +
                "Build to a LiDAR iPhone/iPad to see the character get hidden behind real objects. " +
                "Make sure the character uses opaque (depth-writing) materials — run TENDOR / Characters / Fix Character Materials if it looks see-through.";
            return rendererOk;
        }

        private static void ConfigureCameraBeforeOpaques(ARCameraManager cameraManager)
        {
            var so = new SerializedObject(cameraManager);
            SetIntIfPresent(so, "m_RenderMode", (int)CameraBackgroundRenderingMode.BeforeOpaques);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureOcclusionManagerDefaults(AROcclusionManager occlusion)
        {
            // Set the requested modes on the manager itself so occlusion is visible in builds even before
            // ARCharacterOcclusion.Start() runs, and so the values are sensible in the inspector.
            var so = new SerializedObject(occlusion);
            SetIntIfPresent(so, "m_EnvironmentDepthMode", (int)EnvironmentDepthMode.Medium);
            SetBoolIfPresent(so, "m_EnvironmentDepthTemporalSmoothing", true);
            SetIntIfPresent(so, "m_HumanSegmentationStencilMode", (int)HumanSegmentationStencilMode.Fastest);
            SetIntIfPresent(so, "m_HumanSegmentationDepthMode", (int)HumanSegmentationDepthMode.Fastest);
            SetIntIfPresent(so, "m_OcclusionPreferenceMode", (int)OcclusionPreferenceMode.PreferEnvironmentOcclusion);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetIntIfPresent(SerializedObject so, string prop, int value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.intValue = value;
        }

        private static void SetBoolIfPresent(SerializedObject so, string prop, bool value)
        {
            var p = so.FindProperty(prop);
            if (p != null) p.boolValue = value;
        }

        private static List<string> Validate()
        {
            var issues = new List<string>();

            var occlusion = Object.FindAnyObjectByType<AROcclusionManager>();
            if (occlusion == null)
                issues.Add("No AROcclusionManager in the scene (run Setup Scene).");

            if (Object.FindAnyObjectByType<ARCharacterOcclusion>() == null)
                issues.Add("No ARCharacterOcclusion driver in the scene (run Setup Scene).");

            if (!ActiveRendererHasArBackgroundFeature(out string note))
                issues.Add(note);

            return issues;
        }

        /// <summary>
        /// Occlusion is applied by the URP "AR Background" renderer feature on the active renderer. Without it,
        /// the AROcclusionManager still produces depth but nothing uses it to hide the character.
        /// </summary>
        private static bool ActiveRendererHasArBackgroundFeature(out string note)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                note = "Active render pipeline is not URP — AR Foundation occlusion needs the URP AR Background renderer feature.";
                return false;
            }

            foreach (var data in EnumerateRendererData(pipeline))
            {
                if (data == null) continue;
                foreach (var feature in data.rendererFeatures)
                {
                    if (feature == null) continue;
                    if (feature.GetType().Name == "ARBackgroundRendererFeature")
                    {
                        note = "OK";
                        return true;
                    }
                }
            }

            note = "The active URP renderer is missing the 'AR Background' renderer feature. " +
                   "Add it on the URP Renderer asset (e.g. ImmersalURPAsset_Renderer) or occlusion will not render.";
            return false;
        }

        private static IEnumerable<ScriptableRendererData> EnumerateRendererData(UniversalRenderPipelineAsset pipeline)
        {
            // rendererDataList is internal; reach it via SerializedObject to stay version-tolerant.
            var so = new SerializedObject(pipeline);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null)
                yield break;

            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                yield return element.objectReferenceValue as ScriptableRendererData;
            }
        }
    }
}
