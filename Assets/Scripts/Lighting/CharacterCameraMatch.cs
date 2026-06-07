using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace BodyTracking.LookDev
{
    /// <summary>
    /// Softens the virtual character so it reads more like part of the AR camera feed: material normal reduction,
    /// texture bias, optional screen pass (CharacterCameraSofteningFeature), and FXAA when post-processing is on.
    /// </summary>
    public static class CharacterCameraMatch
    {
        static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");

        public struct Settings
        {
            public bool enabled;
            public bool textureSoftening;
            public bool materialSoftening;
            public bool screenSoftening;
            public bool edgeSoftening;
            public float textureMipBias;
            public int textureAnisoLevel;
            public float materialNormalScale;
            public float screenStrength;
            public float screenMinBlend;
            public float screenBlurRadius;
            public float screenDepthStrength;

            public static Settings Default => new Settings
            {
                enabled = true,
                textureSoftening = true,
                materialSoftening = true,
                screenSoftening = true,
                edgeSoftening = true,
                textureMipBias = 1.3f,
                textureAnisoLevel = 1,
                materialNormalScale = 0.45f,
                screenStrength = 5.5f,
                screenMinBlend = 0.14f,
                screenBlurRadius = 2.4f,
                screenDepthStrength = 18f,
            };
        }

        static Settings current = Settings.Default;

        public static Settings Current => current;

        public static void SetSettings(Settings settings) => current = settings;

        public static void ApplyToCharacter(Transform root)
        {
            if (root == null || !current.enabled)
                return;

            var seenTextures = new HashSet<Texture>();
            var seenMaterials = new HashSet<Material>();

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled)
                    continue;

                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || !seenMaterials.Add(mat))
                        continue;

                    if (current.materialSoftening && mat.HasProperty(BumpScaleId))
                        mat.SetFloat(BumpScaleId, current.materialNormalScale);

                    if (!current.textureSoftening)
                        continue;

                    foreach (var prop in mat.GetTexturePropertyNames())
                    {
                        var tex = mat.GetTexture(prop);
                        if (tex == null || !seenTextures.Add(tex))
                            continue;

                        tex.mipMapBias = current.textureMipBias;
                        if (tex is Texture2D tex2D)
                            tex2D.anisoLevel = Mathf.Clamp(current.textureAnisoLevel, 1, 8);
                    }
                }
            }
        }

        public static void ApplyToCamera(Camera camera)
        {
            if (camera == null || !current.enabled || !current.edgeSoftening)
                return;

            var urp = camera.GetComponent<UniversalAdditionalCameraData>();
            if (urp == null)
                return;

            urp.renderPostProcessing = true;
            if (urp.antialiasing == AntialiasingMode.None)
            {
                urp.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
                urp.antialiasingQuality = AntialiasingQuality.Low;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoApplyCameraSettings()
        {
            if (!current.enabled)
                return;
            ApplyToCamera(Camera.main);
        }
    }
}
