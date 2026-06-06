using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Builds URP/Lit materials for an Avaturn-style FBX from the textures Unity extracted into its ".fbm"
    /// sidecar folder, then remaps those materials onto the model's slots so it renders textured instead of white.
    /// </summary>
    public static class AvaturnMaterialBinder
    {
        private const string DefaultFbxPath = "Assets/DeepMotion/priyal/model.fbx";
        private const string CharactersFolder = "Assets/DeepMotion/Characters";

        static readonly string[] MaterialSuffixes =
        {
            "_base_color", "_basecolor", "_albedo", "_diffuse", "_material",
        };

        [MenuItem("TENDOR/Characters/Bind Avaturn Textures (model.fbx)", priority = 5)]
        public static void BindDefaultModel() => Bind(DefaultFbxPath);

        [MenuItem("TENDOR/Characters/Bind All In Characters Folder", priority = 4)]
        public static void BindAllInCharactersFolder()
        {
            if (!AssetDatabase.IsValidFolder(CharactersFolder))
            {
                Debug.LogError($"[AvaturnMaterialBinder] Folder not found: '{CharactersFolder}'.");
                return;
            }

            // Every FBX directly handled here gets its own per-model materials folder + texture set, so models
            // sharing the directory (model.fbx, modelme.fbx, ...) never overwrite each other's materials.
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { CharactersFolder });
            var fbxPaths = guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() == ".fbx")
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            if (fbxPaths.Count == 0)
            {
                Debug.LogWarning($"[AvaturnMaterialBinder] No .fbx models found under '{CharactersFolder}'.");
                return;
            }

            var log = new StringBuilder();
            int totalBound = 0, modelsProcessed = 0;
            foreach (string fbxPath in fbxPaths)
            {
                log.AppendLine($"{Path.GetFileName(fbxPath)}:");
                int bound = Bind(fbxPath, null, log);
                totalBound += bound;
                if (bound > 0) modelsProcessed++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[AvaturnMaterialBinder] Bound all in '{CharactersFolder}': {totalBound} material(s) across " +
                      $"{modelsProcessed}/{fbxPaths.Count} model(s).\n{log}");
        }

        [MenuItem("TENDOR/Characters/Bind Avaturn Textures (selected FBX)", priority = 6)]
        public static void BindSelected()
        {
            var obj = Selection.activeObject;
            string path = obj != null ? AssetDatabase.GetAssetPath(obj) : null;
            if (string.IsNullOrEmpty(path) || Path.GetExtension(path).ToLowerInvariant() != ".fbx")
            {
                Debug.LogError("[AvaturnMaterialBinder] Select an .fbx in the Project window first.");
                return;
            }
            Bind(path);
        }

        [MenuItem("TENDOR/Characters/Bind Avaturn Textures (selected FBX)", validate = true)]
        private static bool BindSelectedValidate()
        {
            var obj = Selection.activeObject;
            string path = obj != null ? AssetDatabase.GetAssetPath(obj) : null;
            return !string.IsNullOrEmpty(path) && Path.GetExtension(path).ToLowerInvariant() == ".fbx";
        }

        public static int Bind(string fbxPath, string materialsOutputFolder = null, StringBuilder log = null)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[AvaturnMaterialBinder] '{fbxPath}' is not a model/FBX.");
                log?.AppendLine($"Not a model: {fbxPath}");
                return 0;
            }

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null)
            {
                Debug.LogError($"[AvaturnMaterialBinder] Could not load '{fbxPath}'.");
                log?.AppendLine($"Could not load: {fbxPath}");
                return 0;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null)
            {
                Debug.LogError("[AvaturnMaterialBinder] URP/Lit shader not found. Is URP active?");
                log?.AppendLine("URP/Lit shader not found.");
                return 0;
            }

            if (string.IsNullOrEmpty(materialsOutputFolder))
            {
                // Per-MODEL subfolder, not a shared "{fbxDir}/Materials". Avaturn models reuse the same slot
                // names (avaturn_body, avaturn_look_0, ...), so two models in the same directory would otherwise
                // write to the same .mat files and clobber each other — one model would end up showing the
                // other's textures. Scoping by model name keeps each model's materials isolated.
                string fbxDir = Path.GetDirectoryName(fbxPath)?.Replace('\\', '/');
                string modelName = Path.GetFileNameWithoutExtension(fbxPath);
                materialsOutputFolder = $"{fbxDir}/Materials/{modelName}";
            }

            EnsureFolder(materialsOutputFolder);

            var textures = IndexTextures(fbxPath);
            if (textures.Count == 0)
            {
                Debug.LogError($"[AvaturnMaterialBinder] No textures found next to '{fbxPath}'. " +
                               "Expected a '<model>.fbm' folder with *_base_color.* images.");
                log?.AppendLine("No textures found in .fbm folder.");
                return 0;
            }

            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.External;
            ClearMaterialRemaps(importer);

            var slots = CollectFbxMaterialSlots(fbxPath, root);
            int bound = 0, skipped = 0;
            foreach (var slot in slots)
            {
                string textureKey = StripMaterialSuffixes(slot).ToLowerInvariant();
                ResolveTextures(textures, textureKey, out Texture2D baseColor, out Texture2D normal,
                    out Texture2D metallic, out Texture2D roughness);

                if (baseColor == null && normal == null && metallic == null)
                {
                    Debug.LogWarning($"[AvaturnMaterialBinder] No textures matched slot '{slot}' (key '{textureKey}').");
                    log?.AppendLine($"  Skipped '{slot}' — no matching textures.");
                    skipped++;
                    continue;
                }

                if (normal != null)
                    EnsureNormalMap(normal);

                var mat = LoadOrCreateMaterial(slot, materialsOutputFolder, urpLit);
                ApplyMaterialProperties(mat, urpLit, baseColor, normal, metallic, roughness, textureKey);

                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), slot);
                importer.AddRemap(id, mat);
                bound++;
                string line = $"'{slot}' -> {mat.name} (base:{TexName(baseColor)} normal:{TexName(normal)})";
                Debug.Log($"[AvaturnMaterialBinder] {line}");
                log?.AppendLine($"  {line}");
            }

            AssetDatabase.SaveAssets();
            importer.SaveAndReimport();
            AssetDatabase.Refresh();

            Debug.Log($"[AvaturnMaterialBinder] Done on '{Path.GetFileName(fbxPath)}': bound {bound} material(s), " +
                      $"skipped {skipped}. Materials saved in '{materialsOutputFolder}'.");
            return bound;
        }

        /// <summary>
        /// FBX remap identifiers use the original slot names (e.g. avaturn_body), not Unity's imported
        /// material asset names (often avaturn_body_base_color). Avaturn mesh node names match slot names.
        /// </summary>
        static List<string> CollectFbxMaterialSlots(string fbxPath, GameObject root)
        {
            var slots = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                        continue;

                    string slot = GetFbxMaterialSlotName(renderer.gameObject.name, material.name);
                    if (seen.Add(slot))
                        slots.Add(slot);
                }
            }

            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            {
                if (asset is not Material material)
                    continue;

                string slot = GetFbxMaterialSlotName(null, material.name);
                if (seen.Add(slot))
                    slots.Add(slot);
            }

            slots.Sort(StringComparer.Ordinal);
            return slots;
        }

        static string GetFbxMaterialSlotName(string meshName, string materialName)
        {
            if (!string.IsNullOrEmpty(meshName) &&
                meshName.StartsWith("avaturn_", StringComparison.OrdinalIgnoreCase))
                return meshName;

            return StripMaterialSuffixes(materialName);
        }

        static string StripMaterialSuffixes(string name)
        {
            name = name.Replace(" (Instance)", "", StringComparison.OrdinalIgnoreCase).Trim();
            foreach (string suffix in MaterialSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }
            return name;
        }

        static void ResolveTextures(
            Dictionary<string, Texture2D> textures,
            string key,
            out Texture2D baseColor,
            out Texture2D normal,
            out Texture2D metallic,
            out Texture2D roughness)
        {
            baseColor = FindMap(textures, key, "base_color", "basecolor", "albedo", "diffuse");
            normal = FindMap(textures, key, "normalmap", "normal", "_n");
            metallic = FindMap(textures, key, "metallic", "metalness");
            roughness = FindMap(textures, key, "roughness");

            if (baseColor != null || normal != null || metallic != null)
                return;

            // Avaturn sometimes ships hair_0 without its own maps — reuse hair_1.
            if (key.EndsWith("_0"))
            {
                string fallbackKey = key.Substring(0, key.Length - 2) + "1";
                baseColor = FindMap(textures, fallbackKey, "base_color", "basecolor", "albedo", "diffuse");
                normal = FindMap(textures, fallbackKey, "normalmap", "normal", "_n");
                metallic = FindMap(textures, fallbackKey, "metallic", "metalness");
                roughness = FindMap(textures, fallbackKey, "roughness");
            }
        }

        static void ApplyMaterialProperties(
            Material mat,
            Shader urpLit,
            Texture2D baseColor,
            Texture2D normal,
            Texture2D metallic,
            Texture2D roughness,
            string textureKey)
        {
            mat.shader = urpLit;

            if (baseColor != null && mat.HasProperty("_BaseMap"))
            {
                mat.SetTexture("_BaseMap", baseColor);
                mat.SetColor("_BaseColor", Color.white);
            }

            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }
            else
            {
                mat.SetTexture("_BumpMap", null);
                mat.DisableKeyword("_NORMALMAP");
            }

            // Matte look: do NOT assign the metallic/gloss map — its alpha channel feeds smoothness in URP/Lit and
            // is the main source of the plastic shine. Drive smoothness from a single low scalar instead, and read
            // it from the albedo alpha so a stray texture can't re-introduce gloss.
            if (mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", 0f);

            if (mat.HasProperty("_SmoothnessTextureChannel"))
                mat.SetFloat("_SmoothnessTextureChannel", 1f); // 1 = Albedo Alpha (not metallic map alpha)

            if (mat.HasProperty("_Smoothness"))
            {
                // Low, but not dead-zero, so skin/cloth still catch a faint, realistic light falloff. Eyes get a
                // touch more so they don't look like paper.
                float smoothness = textureKey.Contains("eye") ? 0.2f : 0.08f;
                mat.SetFloat("_Smoothness", smoothness);
            }

            // Kill the hard specular hotspot entirely for a fully matte surface (diffuse + normal detail remain).
            if (mat.HasProperty("_SpecularHighlights"))
            {
                mat.SetFloat("_SpecularHighlights", 0f);
                mat.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
                if (textureKey.Contains("eye")) // keep a small highlight on eyes only
                    mat.SetFloat("_SpecularHighlights", 1f);
                else
                    mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }

            if (mat.HasProperty("_EnvironmentReflections"))
            {
                bool isEye = textureKey.Contains("eye");
                mat.SetFloat("_EnvironmentReflections", isEye ? 1f : 0f);
                if (isEye) mat.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                else mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            }

            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 0f);

            // Eye-occlusion / cornea shells are meant to be SEE-THROUGH overlays. Left opaque they render as a
            // solid blob over the iris (reads as black/white eyes). Make them transparent so the real eye shows.
            if (textureKey.Contains("eyeao") || textureKey.Contains("occlusion") || textureKey.Contains("cornea"))
                SetTransparent(mat, baseColor != null ? 0.5f : 0f);

            // Eyelashes / eyebrows / hair are a strand texture on a card with a transparent background. Opaque,
            // that background renders as a solid (black) patch around the eyes. Alpha-clip so only the strands show.
            else if (textureKey.Contains("lash") || textureKey.Contains("brow") || textureKey.Contains("hair"))
            {
                SetAlphaClip(mat, 0.33f);
                if (baseColor != null) EnsureAlphaIsTransparency(baseColor);
            }

            EditorUtility.SetDirty(mat);
        }

        /// <summary>Switch a URP/Lit material to alpha-blended transparency, with the given base-color alpha.</summary>
        static void SetTransparent(Material mat, float alpha)
        {
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // 0 = Opaque, 1 = Transparent
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);       // 0 = Alpha
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (mat.HasProperty("_BaseColor"))
            {
                Color c = mat.GetColor("_BaseColor");
                c.a = alpha;
                mat.SetColor("_BaseColor", c);
            }
        }

        /// <summary>Enable alpha-test cutout on a URP/Lit material so a strand/cutout texture's transparent
        /// background disappears (used for eyelashes, eyebrows, hair) while staying in the cheap opaque pass.</summary>
        static void SetAlphaClip(Material mat, float cutoff)
        {
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 0f); // stays "opaque" surface, just alpha-tested
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 1f);
            if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", cutoff);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        static void ClearMaterialRemaps(ModelImporter importer)
        {
            foreach (var id in importer.GetExternalObjectMap().Keys.ToList())
            {
                if (id.type == typeof(Material))
                    importer.RemoveRemap(id);
            }
        }

        static Dictionary<string, Texture2D> IndexTextures(string fbxPath)
        {
            var result = new Dictionary<string, Texture2D>();
            string dir = Path.GetDirectoryName(fbxPath).Replace('\\', '/');
            string fbm = $"{dir}/{Path.GetFileNameWithoutExtension(fbxPath)}.fbm";

            // Use ONLY the model's own ".fbm" sidecar when it exists. FindAssets recurses, so also scanning the
            // parent directory would pull in sibling models' ".fbm" textures (model.fbm vs modelme.fbm) — and
            // because Avaturn reuses texture names across models (avaturn_look_0_base_color.jpg, etc.) the wrong
            // model's textures would win. Fall back to the directory only when there's no .fbm folder at all.
            var searchFolders = new List<string>();
            if (AssetDatabase.IsValidFolder(fbm)) searchFolders.Add(fbm);
            else if (AssetDatabase.IsValidFolder(dir)) searchFolders.Add(dir);
            if (searchFolders.Count == 0) return result;

            // FindAssets recurses into subfolders. Restrict to textures that live DIRECTLY in a search folder so
            // a nested "Materials" output folder or a sibling model's folder can never feed in the wrong images.
            var allowed = new HashSet<string>(searchFolders, StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", searchFolders.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (parent == null || !allowed.Contains(parent)) continue;
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                string nameKey = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                result[nameKey] = tex;
            }
            return result;
        }

        static Texture2D FindMap(Dictionary<string, Texture2D> textures, string key, params string[] suffixes)
        {
            foreach (var suffix in suffixes)
            {
                string exact = $"{key}_{suffix}";
                if (textures.TryGetValue(exact, out var t)) return t;
            }
            foreach (var kv in textures)
            {
                if (!kv.Key.StartsWith(key)) continue;
                foreach (var suffix in suffixes)
                    if (kv.Key.Contains(suffix)) return kv.Value;
            }
            return null;
        }

        static Material LoadOrCreateMaterial(string slotName, string folder, Shader shader)
        {
            string safe = MakeFileSafe(slotName);
            string path = $"{folder}/{safe}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.name = safe;
                return existing;
            }

            var mat = new Material(shader) { name = safe };
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>Make sure a cutout texture (lash/hair) imports its alpha and dilates color into transparent
        /// texels, so alpha-tested edges don't fringe to black.</summary>
        static void EnsureAlphaIsTransparency(Texture2D tex)
        {
            string path = AssetDatabase.GetAssetPath(tex);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            bool dirty = false;
            if (ti.alphaSource != TextureImporterAlphaSource.FromInput) { ti.alphaSource = TextureImporterAlphaSource.FromInput; dirty = true; }
            if (!ti.alphaIsTransparency) { ti.alphaIsTransparency = true; dirty = true; }
            if (dirty) ti.SaveAndReimport();
        }

        static void EnsureNormalMap(Texture2D normal)
        {
            string path = AssetDatabase.GetAssetPath(normal);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null && ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
        }

        static string MakeFileSafe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        static string TexName(Texture2D t) => t != null ? t.name : "-";

        static void EnsureFolder(string assetPath)
        {
            assetPath = assetPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(assetPath))
                return;

            string parent = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(assetPath);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
