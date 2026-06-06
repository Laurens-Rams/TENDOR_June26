using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BodyTracking.LookDev
{
    /// <summary>
    /// Live look-tuning component for the "Character Look Lab" scene. It applies, in edit mode (ExecuteAlways),
    /// three things you can dial in until the character looks right, then push to the main scene:
    ///   1. Material look on the character's SHARED materials (smoothness/metallic/specular) — because the .mat
    ///      files are shared assets, tuning them here updates the main scene's character automatically.
    ///   2. The directional "gym" light rig intensities + ambient lift (same rig GymLightingSetup builds).
    ///   3. Optional indoor HDRI environment (image-based lighting) for a more realistic, grounded look.
    ///
    /// Attach it via TENDOR ▸ Characters ▸ Open Character Look Lab. Adjust values in the inspector and the scene
    /// updates instantly. Use the inspector buttons to re-apply or copy the lighting to the main scene.
    /// </summary>
    [ExecuteAlways]
    public class CharacterLookLab : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Root of the character to tune. Its renderers' shared materials are edited in place.")]
        public Transform character;
        [Tooltip("Re-apply automatically whenever a value changes in the inspector (edit mode).")]
        public bool liveUpdate = true;

        [Header("Material look (matte → glossy)")]
        [Tooltip("0 = dead matte, 1 = mirror. ~0.08 is a realistic matte skin/cloth.")]
        [Range(0f, 1f)] public float smoothness = 0.08f;
        [Range(0f, 1f)] public float metallic = 0f;
        [Tooltip("Keep a small specular highlight + reflections on the eyes so they don't look like paper.")]
        [Range(0f, 1f)] public float eyeSmoothness = 0.2f;
        [Tooltip("Hard specular hotspot. Off = fully matte body/cloth.")]
        public bool specularHighlights = false;
        [Tooltip("Reflection-probe/skybox reflections on the body. Off = matte.")]
        public bool environmentReflections = false;

        [Header("Lighting rig (directional gym rig)")]
        [Range(0f, 3f)] public float keyIntensity = 1.0f;
        [Range(0f, 3f)] public float fillIntensity = 0.40f;
        [Range(0f, 3f)] public float sideFillIntensity = 0.25f;
        [Range(0f, 3f)] public float rimIntensity = 0.55f;
        [Tooltip("Overall ambient lift (Trilight gradient when not using an HDRI).")]
        [Range(0f, 3f)] public float ambientIntensity = 1.0f;

        [Header("Indoor HDRI (optional, realistic IBL)")]
        [Tooltip("Assign an indoor HDRI cubemap for image-based lighting. When set and enabled, it drives the " +
                 "skybox + ambient instead of the flat gradient.")]
        public bool useHdri = false;
        public Cubemap hdri;
        [Range(0f, 8f)] public float hdriExposure = 1.0f;
        [Range(0f, 360f)] public float hdriRotation = 0f;
        public Color hdriTint = Color.white;
        [Tooltip("How strongly the HDRI lights the scene (ambient).")]
        [Range(0f, 3f)] public float hdriAmbientIntensity = 1.0f;

        // Light rig group/names match GymLightingSetup so the lab and main scene stay in sync.
        const string GroupName = "TendorLighting";
        const string KeyName = "Key Light (Skylights)";
        const string FillName = "Fill Light (Windows)";
        const string SideFillName = "Side Fill (Bounce)";
        const string RimName = "Rim Light (Back)";

        Material hdriSkybox; // runtime skybox material built from the HDRI

        void OnEnable() => ScheduleApply();

        void OnValidate()
        {
            if (liveUpdate)
                ScheduleApply();
        }

        /// <summary>
        /// Apply on the next tick rather than inline. Toggling Renderer.enabled (for the eye-occlusion shells) is
        /// illegal during OnValidate/Awake ("SendMessage cannot be called during ..."), so in the editor we defer
        /// to EditorApplication.delayCall; at runtime we just apply immediately.
        /// </summary>
        void ScheduleApply()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= DeferredApply;
            UnityEditor.EditorApplication.delayCall += DeferredApply;
#else
            ApplyAll();
#endif
        }

#if UNITY_EDITOR
        void DeferredApply()
        {
            if (this == null) return; // component/object may have been destroyed before the tick
            ApplyAll();
        }
#endif

        /// <summary>Apply material, lighting, and environment settings to the scene this component lives in.</summary>
        public void ApplyAll()
        {
            ApplyMaterials();
            ApplyLighting();
            ApplyEnvironment();
        }

        public void ApplyMaterials()
        {
            if (character == null) return;

            DisableOcclusionShells(character);

            var seen = new HashSet<Material>();
            foreach (var r in character.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue; // skip the AO shells we just turned off
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || !seen.Add(mat)) continue;
                    bool isEye = mat.name.ToLowerInvariant().Contains("eye");
                    ApplyMatte(mat, isEye);
                }
            }
        }

        /// <summary>
        /// Disable Avaturn/RPM eye ambient-occlusion shell meshes (e.g. "EyeAO_Mesh"). They sit as a dark dome
        /// around the eyeball and, rendered opaque with the borrowed eye material, read as a black ring around the
        /// eyes. They carry no useful texture of their own, so hiding them is the clean fix. Returns count hidden.
        /// </summary>
        public static int DisableOcclusionShells(Transform root)
        {
            if (root == null) return 0;
            int hidden = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled) continue;
                if (IsOcclusionShell(r.name) || MaterialIsOcclusionShell(r))
                {
                    r.enabled = false;
                    hidden++;
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(r);
#endif
                }
            }
            return hidden;
        }

        static bool IsOcclusionShell(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("eyeao") || n.Contains("eye_ao") || n.Contains("occlusion") || n.Contains("cornea");
        }

        static bool MaterialIsOcclusionShell(Renderer r)
        {
            foreach (var m in r.sharedMaterials)
                if (m != null && IsOcclusionShell(m.name))
                    return true;
            return false;
        }

        void ApplyMatte(Material mat, bool isEye)
        {
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", isEye ? eyeSmoothness : smoothness);

            // Drive smoothness from albedo alpha, not the metallic map's alpha, so a stray gloss map can't shine.
            if (mat.HasProperty("_SmoothnessTextureChannel")) mat.SetFloat("_SmoothnessTextureChannel", 1f);
            if (mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            bool spec = specularHighlights || isEye;
            if (mat.HasProperty("_SpecularHighlights"))
                mat.SetFloat("_SpecularHighlights", spec ? 1f : 0f);
            if (spec) mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            else mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");

            bool env = environmentReflections || isEye;
            if (mat.HasProperty("_EnvironmentReflections"))
                mat.SetFloat("_EnvironmentReflections", env ? 1f : 0f);
            if (env) mat.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            else mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(mat);
#endif
        }

        public void ApplyLighting()
        {
            SetLight(KeyName, keyIntensity);
            SetLight(FillName, fillIntensity);
            SetLight(SideFillName, sideFillIntensity);
            SetLight(RimName, rimIntensity);
        }

        void SetLight(string name, float intensity)
        {
            var group = GameObject.Find(GroupName);
            Transform t = group != null ? group.transform.Find(name) : null;
            if (t == null) return;
            var light = t.GetComponent<Light>();
            if (light != null) light.intensity = intensity;
        }

        public void ApplyEnvironment()
        {
            if (useHdri && hdri != null)
            {
                EnsureHdriSkybox();
                RenderSettings.skybox = hdriSkybox;
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = hdriAmbientIntensity;
            }
            else
            {
                // Fall back to the flat gym gradient ambient (matches GymLightingSetup).
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.55f, 0.58f, 0.62f);
                RenderSettings.ambientEquatorColor = new Color(0.44f, 0.44f, 0.46f);
                RenderSettings.ambientGroundColor = new Color(0.30f, 0.28f, 0.24f);
                RenderSettings.ambientIntensity = ambientIntensity;
            }
            RenderSettings.reflectionIntensity = 1f;
            DynamicGI.UpdateEnvironment();
        }

        void EnsureHdriSkybox()
        {
            if (hdriSkybox == null)
            {
                var shader = Shader.Find("Skybox/Cubemap");
                if (shader == null) return;
                hdriSkybox = new Material(shader) { name = "CharacterLab_HDRI" };
            }
            if (hdriSkybox.HasProperty("_Tex")) hdriSkybox.SetTexture("_Tex", hdri);
            if (hdriSkybox.HasProperty("_Exposure")) hdriSkybox.SetFloat("_Exposure", hdriExposure);
            if (hdriSkybox.HasProperty("_Rotation")) hdriSkybox.SetFloat("_Rotation", hdriRotation);
            if (hdriSkybox.HasProperty("_Tint")) hdriSkybox.SetColor("_Tint", hdriTint);
        }
    }
}
