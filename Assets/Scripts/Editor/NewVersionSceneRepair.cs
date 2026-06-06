using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Repairs script GUID references in NewVersion.unity after a partial project recovery.
    /// Run via TENDOR / Scene / Repair NewVersion Script References.
    /// </summary>
    public static class NewVersionSceneRepair
    {
        const string ScenePath = "Assets/Scenes/NewVersion.unity";

        // Old GUID (lost .meta) -> current script GUID in this project.
        static readonly Dictionary<string, string> GuidMap = new Dictionary<string, string>
        {
            { "cf14681ab983146ca9cd820ff02c3aaf", "5620ccbaf57224d40aa3f494869078ae" }, // BodyTrackingPlayer
            { "7478155c8fa02476e8f3130252735189", "0cb1aedd5efbb45d7bd143e1906ec942" }, // BodyTrackingRecorder
            { "a40fc6e510601461b90668b5c8e5dda6", "ce96968048d064582a69b95038261c51" }, // ARImageTargetManager
            { "d09d7384bcd9048f182e7f86dd6c6842", "119f885aa259b4bfa8d334e907e81a9e" }, // BodyTrackingController
            { "21e6b6dc7f5b249b09d8a14215d38b9b", "752818980b08b478da9b545c809fc4da" }, // BodyTrackingUI
            { "1721a2f2ec19a4aad8c79128c9547cf5", "b34a3d2100c524096b086b7c1968476d" }, // FBXCharacterController
        };

        [MenuItem("TENDOR/Scene/Repair NewVersion Script References")]
        public static void RepairSceneMenu()
        {
            int replaced = RepairSceneFileOnDisk();
            AssetDatabase.Refresh();

            if (EditorSceneManager.GetActiveScene().path == ScenePath)
                EditorSceneManager.OpenScene(ScenePath);

            EditorUtility.DisplayDialog(
                "NewVersion scene repair",
                replaced > 0
                    ? $"Updated {replaced} script GUID reference(s) in NewVersion.unity.\n\n" +
                      "Next in Unity (with NewVersion open):\n" +
                      "1. TENDOR → Immersal → Setup Scene\n" +
                      "2. TENDOR → Move AI Fusion → Setup Scene\n" +
                      "3. TENDOR → UI → Rebuild Body Tracking UI\n" +
                      "4. Save the scene (Cmd+S)"
                    : "No stale GUIDs found — scene file may already be repaired.\n\n" +
                      "If references are still missing, run the TENDOR setup menus listed above.",
                "OK");
        }

        public static int RepairSceneFileOnDisk()
        {
            string fullPath = Path.Combine(Application.dataPath, "Scenes/NewVersion.unity");
            if (!File.Exists(fullPath))
            {
                Debug.LogError("[NewVersionSceneRepair] Scene not found: " + ScenePath);
                return 0;
            }

            string yaml = File.ReadAllText(fullPath);
            int count = 0;
            foreach (var pair in GuidMap)
            {
                string pattern = @"guid: " + pair.Key;
                string replacement = "guid: " + pair.Value;
                int hits = Regex.Matches(yaml, Regex.Escape(pattern)).Count;
                if (hits > 0)
                {
                    yaml = yaml.Replace(pattern, replacement);
                    count += hits;
                }
            }

            if (count > 0)
            {
                File.WriteAllText(fullPath, yaml);
                Debug.Log($"[NewVersionSceneRepair] Replaced {count} GUID(s) in {ScenePath}");
            }

            return count;
        }
    }
}
