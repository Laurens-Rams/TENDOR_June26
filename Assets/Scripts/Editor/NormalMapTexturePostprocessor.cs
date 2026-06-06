using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Auto-marks character normal-map textures as normal maps on import.
    ///
    /// DeepMotion / Ready-Player-Me style FBX files ship their textures embedded; Unity extracts
    /// them into a "<model>.fbm" folder on import. The normal maps come out as plain color textures,
    /// so materials that bind them to a normal slot trigger the
    /// "texture must be marked as a normal map in the import settings" warning and render with wrong
    /// lighting. Because the .fbm extraction is regenerated on reimport, fixing the import settings by
    /// hand does not survive — this postprocessor reapplies the fix automatically.
    ///
    /// Any texture whose file name contains "normalmap" (e.g. head_normalmap.jpg) is treated as a
    /// normal map. Use the menu command below to reimport already-imported textures once.
    /// </summary>
    public class NormalMapTexturePostprocessor : AssetPostprocessor
    {
        private const string Marker = "normalmap";

        private static bool LooksLikeNormalMap(string assetPath)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            return file != null && file.ToLowerInvariant().Contains(Marker);
        }

        void OnPreprocessTexture()
        {
            var importer = (TextureImporter)assetImporter;

            if (LooksLikeNormalMap(assetPath) && importer.textureType != TextureImporterType.NormalMap)
                importer.textureType = TextureImporterType.NormalMap;

            // Crisper character textures: Avaturn ships 1024² maps, so we can't add resolution, but the soft/blocky
            // look comes from low-quality compression + no anisotropic filtering. Bump both for any texture that
            // lives in a character's .fbm folder.
            if (assetPath != null && assetPath.Replace('\\', '/').Contains(".fbm/"))
            {
                importer.compressionQuality = 100;                 // best block-compression search
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.anisoLevel = Mathf.Max(importer.anisoLevel, 4); // sharper at grazing angles
                importer.mipmapEnabled = true;
            }
        }

        [MenuItem("TENDOR/Characters/Fix Normal Map Imports", priority = 30)]
        public static void FixExistingNormalMaps()
        {
            var fixedPaths = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!LooksLikeNormalMap(path))
                    continue;

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null || importer.textureType == TextureImporterType.NormalMap)
                    continue;

                importer.textureType = TextureImporterType.NormalMap;
                importer.SaveAndReimport();
                fixedPaths.Add(path);
            }

            if (fixedPaths.Count == 0)
                Debug.Log("[NormalMapTexturePostprocessor] No textures needed fixing — all normal maps already marked correctly.");
            else
                Debug.Log($"[NormalMapTexturePostprocessor] Marked {fixedPaths.Count} texture(s) as normal maps:\n - " +
                          string.Join("\n - ", fixedPaths));
        }
    }
}
