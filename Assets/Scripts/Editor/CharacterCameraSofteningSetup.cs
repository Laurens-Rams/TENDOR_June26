#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Ensures <see cref="Rendering.CharacterCameraSofteningFeature"/> is present on the active URP renderer.
    /// </summary>
    [InitializeOnLoad]
    public static class CharacterCameraSofteningSetup
    {
        const string RendererPath = "Assets/ImmersalURPAsset_Renderer.asset";

        static CharacterCameraSofteningSetup()
        {
            EditorApplication.delayCall += EnsureRendererFeature;
        }

        [MenuItem("TENDOR/Characters/Setup Camera Softening", priority = 32)]
        public static void SetupMenu()
        {
            bool ok = EnsureRendererFeature(out string message);
            EditorUtility.DisplayDialog(ok ? "Camera Softening" : "Camera Softening — Issue", message, "OK");
        }

        static void EnsureRendererFeature()
        {
            TryEnsureRendererFeature(out _);
        }

        public static bool EnsureRendererFeature(out string message)
        {
            return TryEnsureRendererFeature(out message);
        }

        static bool TryEnsureRendererFeature(out string message)
        {
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (renderer == null)
            {
                message = $"Could not load URP renderer at {RendererPath}.";
                return false;
            }

            foreach (var feature in renderer.rendererFeatures)
            {
                if (feature is Rendering.CharacterCameraSofteningFeature)
                {
                    message = "Character camera softening is already on the URP renderer.";
                    return true;
                }
            }

            var softening = ScriptableObject.CreateInstance<Rendering.CharacterCameraSofteningFeature>();
            softening.name = "CharacterCameraSoftening";
            AssetDatabase.AddObjectToAsset(softening, renderer);
            renderer.rendererFeatures.Add(softening);
            EditorUtility.SetDirty(renderer);
            AssetDatabase.SaveAssets();

            message = "Added CharacterCameraSofteningFeature to ImmersalURPAsset_Renderer.";
            return true;
        }

        [MenuItem("TENDOR/Characters/Validate Camera Softening", priority = 33)]
        public static void ValidateMenu()
        {
            var issues = Validate();
            EditorUtility.DisplayDialog(
                issues.Count == 0 ? "Camera Softening OK" : "Camera Softening Issues",
                issues.Count == 0 ? "Renderer feature is present." : string.Join("\n", issues),
                "OK");
        }

        public static List<string> Validate()
        {
            var issues = new List<string>();
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                issues.Add("Active render pipeline is not URP.");
                return issues;
            }

            bool found = false;
            foreach (var data in EnumerateRendererData(pipeline))
            {
                if (data == null) continue;
                foreach (var feature in data.rendererFeatures)
                {
                    if (feature is Rendering.CharacterCameraSofteningFeature)
                        found = true;
                }
            }

            if (!found)
                issues.Add("Missing CharacterCameraSofteningFeature on the URP renderer. Run TENDOR / Characters / Setup Camera Softening.");

            return issues;
        }

        static IEnumerable<ScriptableRendererData> EnumerateRendererData(UniversalRenderPipelineAsset pipeline)
        {
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
#endif
