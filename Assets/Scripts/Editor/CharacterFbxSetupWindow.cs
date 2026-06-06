using System.IO;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Wizard for importing a new character FBX: humanoid rig, URP materials/textures, Move AI retarget map, scene wiring.
    /// </summary>
    public class CharacterFbxSetupWindow : EditorWindow
    {
        GameObject fbxAsset;
        DefaultAsset materialsFolder;
        bool configureHumanoid = true;
        bool bindMaterials = true;
        bool buildRetargetMap = true;
        bool assignScene = true;
        Vector2 scroll;
        string lastLog = "";

        // Menu entry lives in CharacterMaterialFixer (TENDOR/Characters/Setup Character FBX...), which calls Open().
        public static void Open()
        {
            var window = GetWindow<CharacterFbxSetupWindow>("Character FBX Setup");
            window.minSize = new Vector2(420, 360);
            window.Show();
        }

        [MenuItem("TENDOR/Characters/Setup Selected FBX (Quick)", priority = 1)]
        public static void SetupSelectedQuick()
        {
            string path = GetSelectedFbxPath();
            if (path == null)
            {
                EditorUtility.DisplayDialog("Character FBX Setup", "Select a .fbx in the Project window first.", "OK");
                return;
            }

            var result = CharacterFbxSetupUtility.SetupCharacterFbx(path);
            Debug.Log(result.log);
            EditorUtility.DisplayDialog(
                result.success ? "Character FBX Setup" : "Character FBX Setup — Issues",
                result.log,
                "OK");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("New character import pipeline", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drop in a new FBX (with a .fbm texture folder if exported from Avaturn/Ready Player Me). " +
                "This tool will:\n" +
                "• Set Humanoid rig + auto-create avatar\n" +
                "• Build URP/Lit materials from textures and remap slots\n" +
                "• Generate Move AI bone retarget map from the humanoid skeleton\n" +
                "• Wire the open scene (FBXCharacterController, FusedCharacterPlayer, Move AI)",
                MessageType.Info);

            fbxAsset = (GameObject)EditorGUILayout.ObjectField("Character FBX", fbxAsset, typeof(GameObject), false);
            materialsFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                "Materials output folder (optional)",
                materialsFolder,
                typeof(DefaultAsset),
                false);

            EditorGUILayout.Space(4);
            configureHumanoid = EditorGUILayout.ToggleLeft("Configure Humanoid import + avatar", configureHumanoid);
            bindMaterials = EditorGUILayout.ToggleLeft("Bind textures → URP materials", bindMaterials);
            buildRetargetMap = EditorGUILayout.ToggleLeft("Build Move AI retarget map from rig", buildRetargetMap);
            assignScene = EditorGUILayout.ToggleLeft("Assign prefab + retarget map in open scene", assignScene);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(fbxAsset == null))
            {
                if (GUILayout.Button("Setup Character", GUILayout.Height(32)))
                    RunSetup();
            }

            if (GUILayout.Button("Use Selected Project FBX"))
            {
                string path = GetSelectedFbxPath();
                if (path != null)
                    fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            if (!string.IsNullOrEmpty(lastLog))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(120));
                EditorGUILayout.TextArea(lastLog, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }
        }

        void RunSetup()
        {
            string fbxPath = AssetDatabase.GetAssetPath(fbxAsset);
            string matFolder = materialsFolder != null ? AssetDatabase.GetAssetPath(materialsFolder) : null;
            if (!string.IsNullOrEmpty(matFolder) && !AssetDatabase.IsValidFolder(matFolder))
            {
                EditorUtility.DisplayDialog("Character FBX Setup", "Materials output must be a folder inside Assets.", "OK");
                return;
            }

            var result = CharacterFbxSetupUtility.SetupCharacterFbx(
                fbxPath,
                configureHumanoid,
                bindMaterials,
                buildRetargetMap,
                assignScene,
                matFolder);

            lastLog = result.log;
            Debug.Log(result.log);
            Repaint();
        }

        static string GetSelectedFbxPath()
        {
            var obj = Selection.activeObject;
            string path = obj != null ? AssetDatabase.GetAssetPath(obj) : null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                return null;
            return path;
        }
    }
}
