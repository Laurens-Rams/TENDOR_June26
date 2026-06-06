#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Locates GameObjects carrying "missing" MonoBehaviours — components whose backing script asset
    /// no longer resolves (the source of the runtime "The referenced script on this Behaviour is
    /// missing!" warning). Reports the full hierarchy path of each offender, and offers an explicit
    /// (non-automatic) command to strip them from the open scene(s).
    /// </summary>
    public static class MissingScriptFinder
    {
        [MenuItem("TENDOR/Diagnostics/Find Missing Scripts (Open Scenes)", priority = 40)]
        public static void FindInOpenScenes()
        {
            var report = new StringBuilder();
            int totalMissing = 0;
            int objectsAffected = 0;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var go in root.GetComponentsInChildren<Transform>(true))
                    {
                        int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject);
                        if (count <= 0)
                            continue;

                        totalMissing += count;
                        objectsAffected++;
                        report.AppendLine($"  [{scene.name}] {GetHierarchyPath(go)}  →  {count} missing script(s)");
                    }
                }
            }

            if (totalMissing == 0)
                Debug.Log("[MissingScriptFinder] No missing scripts found in open scene(s).");
            else
                Debug.LogWarning($"[MissingScriptFinder] Found {totalMissing} missing script(s) on {objectsAffected} GameObject(s):\n{report}\n" +
                                 "Use 'TENDOR/Diagnostics/Remove Missing Scripts (Open Scenes)' to clean them.");
        }

        [MenuItem("TENDOR/Diagnostics/Remove Missing Scripts (Open Scenes)", priority = 41)]
        public static void RemoveInOpenScenes()
        {
            if (!EditorUtility.DisplayDialog(
                    "Remove Missing Scripts",
                    "This will permanently remove all missing-script components from the currently open scene(s). " +
                    "Make sure the correct scenes are open. Continue?",
                    "Remove", "Cancel"))
                return;

            int removed = 0;
            int objectsAffected = 0;

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var go in root.GetComponentsInChildren<Transform>(true))
                    {
                        int count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject);
                        if (count <= 0)
                            continue;

                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go.gameObject);
                        removed += count;
                        objectsAffected++;
                    }
                }

                EditorSceneManager.MarkSceneDirty(scene);
            }

            Debug.Log($"[MissingScriptFinder] Removed {removed} missing script(s) from {objectsAffected} GameObject(s). " +
                      "Save the scene(s) to persist.");
        }

        private static string GetHierarchyPath(Transform t)
        {
            var stack = new Stack<string>();
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack);
        }
    }
}
#endif
