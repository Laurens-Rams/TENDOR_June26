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
        [Tooltip("Up-light / floor bounce coming from BELOW so the lower body and undersides aren't crushed dark " +
                 "where the overhead key/fill/rim don't reach. Creates the light if the rig doesn't have one yet.")]
        [Range(0f, 3f)] public float bottomFillIntensity = 0.45f;
        [Tooltip("Overall ambient lift (Trilight gradient when not using an HDRI).")]
        [Range(0f, 3f)] public float ambientIntensity = 1.0f;
        [Tooltip("Extra lift on the ground (downward) ambient term so light also comes from below.")]
        [Range(1f, 3f)] public float ambientGroundLift = 1.4f;

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

        [Header("Camera match (AR realism)")]
        [Tooltip("Soften the character to better match the soft AR camera feed.")]
        public bool softenForCamera = true;
        [Tooltip("Full-screen soften pass (runs on device via compatibility-mode Execute).")]
        public bool screenSoftening = true;
        [Range(0f, 10f)] public float screenStrength = 5.5f;
        [Range(0f, 0.35f)] public float screenMinBlend = 0.14f;
        [Range(0.5f, 5f)] public float screenBlurRadius = 2.4f;
        [Range(0f, 40f)] public float screenDepthStrength = 18f;
        [Tooltip("Tone-match the character to the soft AR feed (contrast/saturation/black-lift). Masked to CG " +
                 "geometry by depth, so the camera feed is untouched. Runs inside the existing soften pass (free).")]
        public bool toneMatch = true;
        [Tooltip("0 = no grade, 1 = full grade.")]
        [Range(0f, 1f)] public float toneMatchStrength = 1f;
        [Tooltip("<1 lowers the character's contrast toward mid-grey.")]
        [Range(0.5f, 1f)] public float toneMatchContrast = 0.95f;
        [Tooltip("<1 desaturates the character toward the duller camera look.")]
        [Range(0.5f, 1f)] public float toneMatchSaturation = 0.92f;
        [Tooltip("Lifts crushed CG blacks to match the camera's black floor.")]
        [Range(0f, 0.1f)] public float toneMatchBlackLift = 0.006f;
        [Tooltip("Reduce normal-map strength so skin/cloth shading is less crisp.")]
        public bool materialSoftening = true;
        [Range(0f, 1f)] public float materialNormalScale = 0.45f;
        [Tooltip("Enable FXAA on the AR camera (needs renderPostProcessing on).")]
        public bool edgeSoftening = true;
        [Tooltip("Slightly blur character textures via mipmap bias + lower aniso filtering.")]
        public bool textureSoftening = true;
        [Range(0f, 2f)] public float textureMipBias = 1.3f;
        [Range(1, 4)] public int textureAnisoLevel = 1;

        // Light rig group/names match GymLightingSetup so the lab and main scene stay in sync.
        const string GroupName = "TendorLighting";
        const string KeyName = "Key Light (Skylights)";
        const string FillName = "Fill Light (Windows)";
        const string SideFillName = "Side Fill (Bounce)";
        const string RimName = "Rim Light (Back)";
        const string BottomFillName = "Bottom Fill (Floor Bounce Up)";

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

        /// <summary>
        /// Runtime/editor prep shared by AR playback: hide eye-occlusion shells and disable cast/receive shadows.
        /// Material look comes from the shared .mat assets tuned in Character Look Lab.
        /// </summary>
        public static void PrepareForDisplay(Transform root)
        {
            if (root == null) return;
            DisableOcclusionShells(root);
            DisableShadows(root);
            CharacterCameraMatch.ApplyToCharacter(root);
            CharacterCameraMatch.ApplyToCamera(Camera.main);
        }

        public void ApplyMaterials()
        {
            PushCameraMatchSettings();
            if (character == null) return;

            DisableOcclusionShells(character);
            DisableShadows(character);
            CharacterCameraMatch.ApplyToCharacter(character);

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

        /// <summary>
        /// Turn off shadow casting/receiving on every renderer under the character. The gym key light casts a
        /// directional shadow that self-shadows the face (nose → cheek, brow → eye socket) as harsh dark patches.
        /// For a flat-lit AR avatar the fill/rim rig already provides shape; skipping shadows also avoids the
        /// URP main-light shadow-map pass. Returns count of renderers updated.
        /// </summary>
        public static int DisableShadows(Transform root)
        {
            if (root == null) return 0;
            int updated = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.shadowCastingMode == ShadowCastingMode.Off && !r.receiveShadows) continue;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                updated++;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(r);
#endif
            }
            return updated;
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

        void PushCameraMatchSettings()
        {
            CharacterCameraMatch.SetSettings(new CharacterCameraMatch.Settings
            {
                enabled = softenForCamera,
                screenSoftening = screenSoftening,
                materialSoftening = materialSoftening,
                textureSoftening = textureSoftening,
                edgeSoftening = edgeSoftening,
                screenStrength = screenStrength,
                screenMinBlend = screenMinBlend,
                screenBlurRadius = screenBlurRadius,
                screenDepthStrength = screenDepthStrength,
                materialNormalScale = materialNormalScale,
                textureMipBias = textureMipBias,
                textureAnisoLevel = textureAnisoLevel,
                matchStrength = toneMatch ? toneMatchStrength : 0f,
                matchContrast = toneMatchContrast,
                matchSaturation = toneMatchSaturation,
                matchBlackLift = toneMatchBlackLift,
            });
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
            SetBottomFill(bottomFillIntensity);
        }

        void SetLight(string name, float intensity)
        {
            var group = GameObject.Find(GroupName);
            Transform t = group != null ? group.transform.Find(name) : null;
            if (t == null) return;
            var light = t.GetComponent<Light>();
            if (light != null) light.intensity = intensity;
        }

        /// <summary>
        /// Drive the floor-bounce up-light, creating it under the TendorLighting group if the rig predates it
        /// (so the lab works without re-running the editor setup). Aims upward from below to lift the undersides.
        /// </summary>
        void SetBottomFill(float intensity)
        {
            var group = GameObject.Find(GroupName);
            if (group == null) return;

            Transform t = group.transform.Find(BottomFillName);
            if (t == null)
            {
                var go = new GameObject(BottomFillName);
                go.transform.SetParent(group.transform, false);
                t = go.transform;
                var created = go.AddComponent<Light>();
                created.type = LightType.Directional;
                created.color = new Color(0.98f, 0.95f, 0.90f);
                created.shadows = LightShadows.None;
                t.localEulerAngles = new Vector3(-65f, 20f, 0f);
            }

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
                // NOTE: Unity only honours RenderSettings.ambientIntensity in Skybox ambient mode; in Trilight
                // (gradient) mode it's ignored, so the slider must scale the gradient colours directly.
                RenderSettings.ambientMode = AmbientMode.Trilight;
                float k = ambientIntensity;
                RenderSettings.ambientSkyColor = new Color(0.55f, 0.58f, 0.62f) * k;
                RenderSettings.ambientEquatorColor = new Color(0.46f, 0.46f, 0.48f) * k;
                // Ground term lifted (and scaled by ambientGroundLift) so ambient also comes from below.
                RenderSettings.ambientGroundColor = new Color(0.42f, 0.40f, 0.37f) * (k * ambientGroundLift);
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
