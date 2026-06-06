using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BodyTracking.Animation;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// One-click fixer for the "character imports all white / no materials" problem.
    ///
    /// Two things are wrong with a freshly imported Ready-Player-Me style FBX in this URP project:
    ///   1. The FBX ships with empty embedded materials, so nothing gets assigned -> meshes render flat white.
    ///   2. The matching .mat files in Assets/Materials use the Built-in "Standard" shader, which is not valid
    ///      under URP (would render magenta if assigned).
    ///
    /// This tool converts those materials to URP/Lit (keeping their albedo/normal/etc. textures) and then remaps
    /// them onto the character FBX slots by mesh name through the ModelImporter. The remap is persistent and
    /// survives reimport, so the fix is permanent (no runtime code needed).
    /// </summary>
    public static class CharacterMaterialFixer
    {
        // Folder that holds the source .mat files (skin/face/eye/...).
        private const string MaterialsFolder = "Assets/Materials";

        // Character FBX files to fix. Any that don't exist are skipped silently.
        private static readonly string[] CharacterFbxPaths =
        {
            "Assets/DeepMotion/CharacerME_Test.fbx",
            "Assets/DeepMotion/test23.fbx",
            "Assets/DeepMotion/test23new.fbx",
            "Assets/DeepMotion/NewBody.fbx",
            "Assets/DeepMotion/model.fbx",
            "Assets/DeepMotion/priyal/model.fbx",
        };

        private const string Test23NewFbxPath = "Assets/DeepMotion/test23new.fbx";

        // Map from a substring of the mesh (renderer GameObject) name to the .mat file to use.
        // Order matters: more specific keys must come before generic ones (e.g. "eyelash"/"eyeao" before "eye").
        private static readonly (string meshKey, string matName)[] MeshNameToMaterial =
        {
            ("eyelash", "hair"),
            ("eyebrow", "hair"),
            ("eyeao", "face"),
            ("eye", "eye"),
            ("teeth", "teeth"),
            ("tongue", "teeth"),
            ("head", "face"),
            ("body", "skin"),
            ("hair", "hair"),
            ("top", "shirt"),
            ("shirt", "shirt"),
            ("outfit_top", "shirt"),
            ("bottom", "pants"),
            ("pant", "pants"),
            ("outfit_bottom", "pants"),
            ("footwear", "shoe"),
            ("shoe", "shoe"),
            ("foot", "shoe"),
        };

        [MenuItem("TENDOR/Setup Character FBX...", priority = 12)]
        public static void OpenSetupWindowFromRootMenu() => CharacterFbxSetupWindow.Open();

        [MenuItem("TENDOR/Characters/Setup Character FBX...", priority = 0)]
        public static void OpenSetupWindowFromCharactersMenu() => CharacterFbxSetupWindow.Open();

        [MenuItem("TENDOR/Characters/Fix Character Materials (URP)", priority = 10)]
        public static void FixCharacterMaterials() => FixCharacterMaterials(CharacterFbxPaths);

        [MenuItem("TENDOR/Characters/Fix test23new Materials (URP)", priority = 11)]
        public static void FixTest23NewMaterials() => FixCharacterMaterials(new[] { Test23NewFbxPath });

        [MenuItem("TENDOR/Characters/Use Selected FBX As Character", priority = 20)]
        public static void UseSelectedAsCharacter()
        {
            var sel = Selection.activeObject;
            string path = sel != null ? AssetDatabase.GetAssetPath(sel) : null;
            if (string.IsNullOrEmpty(path) || System.IO.Path.GetExtension(path).ToLowerInvariant() != ".fbx")
            {
                Debug.LogError("[CharacterMaterialFixer] Select the character .fbx in the Project window first.");
                return;
            }

            var result = CharacterFbxSetupUtility.SetupCharacterFbx(path);
            Debug.Log(result.log);
        }

        [MenuItem("TENDOR/Characters/Use model.fbx As Character", priority = 21)]
        public static void UseModelAsCharacter()
        {
            var result = CharacterFbxSetupUtility.SetupCharacterFbx("Assets/DeepMotion/model.fbx");
            Debug.Log(result.log);
        }

        private static void FixCharacterMaterials(IEnumerable<string> fbxPaths)
        {
            int convertedMaterials = ConvertMaterialsToUrp();

            int totalRemapped = 0;
            int fbxProcessed = 0;
            foreach (string fbxPath in fbxPaths)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath) == null)
                {
                    Debug.LogWarning($"[CharacterMaterialFixer] FBX not found at '{fbxPath}'. Skipping.");
                    continue;
                }

                fbxProcessed++;
                totalRemapped += RemapFbxMaterials(fbxPath);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CharacterMaterialFixer] Done. Converted {convertedMaterials} material(s) to URP/Lit, " +
                      $"remapped {totalRemapped} slot(s) across {fbxProcessed} FBX file(s). " +
                      $"If a mesh is still white, its name didn't match any rule (check the warnings above).");
        }

        private static int ConvertMaterialsToUrp()
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null)
            {
                Debug.LogWarning("[CharacterMaterialFixer] Could not find 'Universal Render Pipeline/Lit' shader. " +
                                 "Is URP installed/active? Skipping material conversion.");
                return 0;
            }

            int converted = 0;
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialsFolder });
            var targetNames = new HashSet<string>(MeshNameToMaterial.Select(m => m.matName));

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;
                if (!targetNames.Contains(mat.name)) continue;        // only touch the character mats
                if (mat.shader == urpLit) continue;                    // already converted -> idempotent

                // Capture Built-in/Standard properties BEFORE swapping the shader (they're lost afterwards).
                Texture albedo = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
                Vector2 albedoScale = albedo != null ? mat.GetTextureScale("_MainTex") : Vector2.one;
                Vector2 albedoOffset = albedo != null ? mat.GetTextureOffset("_MainTex") : Vector2.zero;
                Color baseColor = mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white;
                Texture bump = mat.HasProperty("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
                Texture metallicMap = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
                float metallic = mat.HasProperty("_Metallic") ? mat.GetFloat("_Metallic") : 0f;
                float smoothness = mat.HasProperty("_Glossiness") ? mat.GetFloat("_Glossiness") : 0.5f;
                Texture occlusion = mat.HasProperty("_OcclusionMap") ? mat.GetTexture("_OcclusionMap") : null;
                Texture emissionMap = mat.HasProperty("_EmissionMap") ? mat.GetTexture("_EmissionMap") : null;
                Color emissionColor = mat.HasProperty("_EmissionColor") ? mat.GetColor("_EmissionColor") : Color.black;
                bool emissionEnabled = mat.IsKeywordEnabled("_EMISSION");

                mat.shader = urpLit;

                if (mat.HasProperty("_BaseMap"))
                {
                    mat.SetTexture("_BaseMap", albedo);
                    mat.SetTextureScale("_BaseMap", albedoScale);
                    mat.SetTextureOffset("_BaseMap", albedoOffset);
                }
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);

                if (bump != null && mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", bump);
                    mat.EnableKeyword("_NORMALMAP");
                }

                if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
                if (metallicMap != null && mat.HasProperty("_MetallicGlossMap"))
                {
                    mat.SetTexture("_MetallicGlossMap", metallicMap);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                }

                if (occlusion != null && mat.HasProperty("_OcclusionMap"))
                {
                    mat.SetTexture("_OcclusionMap", occlusion);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                }

                if (emissionEnabled && mat.HasProperty("_EmissionColor"))
                {
                    mat.SetColor("_EmissionColor", emissionColor);
                    if (emissionMap != null && mat.HasProperty("_EmissionMap"))
                        mat.SetTexture("_EmissionMap", emissionMap);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }

                EditorUtility.SetDirty(mat);
                converted++;
                Debug.Log($"[CharacterMaterialFixer] Converted '{mat.name}' to URP/Lit (albedo: {(albedo != null ? albedo.name : "none")}).");
            }

            return converted;
        }

        private static int RemapFbxMaterials(string fbxPath)
        {
            var importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[CharacterMaterialFixer] '{fbxPath}' is not a model. Skipping.");
                return 0;
            }

            // Use external materials and import via descriptions so the remap below is honoured.
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.External;

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (root == null)
            {
                Debug.LogWarning($"[CharacterMaterialFixer] Could not load GameObject at '{fbxPath}'. Skipping.");
                return 0;
            }

            int remapped = 0;
            var remappedNames = new HashSet<string>();

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material target = ResolveMaterialForMesh(renderer.gameObject.name);
                if (target == null)
                {
                    Debug.LogWarning($"[CharacterMaterialFixer] No material rule matched mesh '{renderer.gameObject.name}' " +
                                     $"in '{fbxPath}'. It will keep the default (white) material.");
                    continue;
                }

                foreach (var embedded in renderer.sharedMaterials)
                {
                    if (embedded == null) continue;
                    if (!remappedNames.Add(embedded.name)) continue;   // remap each source slot once

                    var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), embedded.name);
                    importer.AddRemap(id, target);
                    remapped++;
                    Debug.Log($"[CharacterMaterialFixer] {System.IO.Path.GetFileName(fbxPath)}: " +
                              $"slot '{embedded.name}' (mesh '{renderer.gameObject.name}') -> '{target.name}'.");
                }
            }

            importer.SaveAndReimport();
            return remapped;
        }

        private static Material ResolveMaterialForMesh(string meshName)
        {
            string lower = meshName.ToLowerInvariant();
            foreach (var (meshKey, matName) in MeshNameToMaterial)
            {
                if (lower.Contains(meshKey))
                    return LoadMaterial(matName);
            }
            return null;
        }

        private static readonly Dictionary<string, Material> MaterialCache = new Dictionary<string, Material>();

        private static Material LoadMaterial(string matName)
        {
            if (MaterialCache.TryGetValue(matName, out var cached) && cached != null)
                return cached;

            string path = $"{MaterialsFolder}/{matName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                Debug.LogWarning($"[CharacterMaterialFixer] Expected material '{path}' not found.");
            else
                MaterialCache[matName] = mat;
            return mat;
        }
    }
}
