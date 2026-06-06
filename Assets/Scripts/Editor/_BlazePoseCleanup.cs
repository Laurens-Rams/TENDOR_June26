using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TENDOR.EditorCleanup
{
    /// <summary>Temporary one-shot: strips the BlazePose validation pipeline + any missing scripts from a scene.</summary>
    public static class BlazePoseCleanup
    {
        public static void RemoveBlazePoseFromScene()
        {
            const string scenePath = "Assets/Scenes/NewVersion.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // 1) Destroy the BlazePosePipeline GameObject(s) (search includes inactive).
            var toDestroy = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t != null && t.gameObject.name == "BlazePosePipeline")
                        toDestroy.Add(t.gameObject);

            int removedGo = 0;
            foreach (var go in toDestroy)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                    removedGo++;
                }
            }

            // 2) Scrub any leftover missing-script components left behind by deleted BlazePose .cs files.
            int removedMissing = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t != null)
                        removedMissing += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[BlazePoseCleanup] Removed {removedGo} BlazePosePipeline GameObject(s) and {removedMissing} missing-script component(s) from {scenePath}.");
        }
    }
}
