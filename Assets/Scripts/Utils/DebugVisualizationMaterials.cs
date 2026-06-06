using UnityEngine;
using UnityEngine.Rendering;

namespace BodyTracking.Utils
{
    /// <summary>
    /// Creates runtime debug materials that work in both URP and Built-in pipelines.
    /// Shader.Find("Unlit/Color") returns null on URP projects (Unity 6), which breaks skeleton viz.
    /// </summary>
    public static class DebugVisualizationMaterials
    {
        public static Material CreateSolidColorMaterial(Color color)
        {
            var shader = FindShader(
                "Universal Render Pipeline/Unlit",
                "Unlit/Color",
                "Sprites/Default");
            if (shader == null)
                return null;

            var material = new Material(shader);
            ApplyColor(material, color);
            return material;
        }

        public static Material CreateLineMaterial(Color color)
        {
            var shader = FindShader(
                "Universal Render Pipeline/Unlit",
                "Sprites/Default",
                "Unlit/Color");
            if (shader == null)
                return null;

            var material = new Material(shader);
            ApplyColor(material, color);
            if (color.a < 0.999f)
                ConfigureTransparent(material);
            return material;
        }

        static Shader FindShader(params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                var shader = Shader.Find(names[i]);
                if (shader != null)
                    return shader;
            }

            Debug.LogError("[DebugVisualizationMaterials] No shader found among: " + string.Join(", ", names));
            return null;
        }

        static void ApplyColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
        }

        static void ConfigureTransparent(Material material)
        {
            if (!material.HasProperty("_Surface"))
                return;

            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }
    }
}
