using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BodyTracking.UI;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Editor menu command to (re)generate the body-tracking UI into the current scene's Canvas.
    /// The same <see cref="BodyTrackingUI.BuildUI"/> runs at runtime, so this is purely a convenience
    /// for previewing / regenerating the interface at edit time.
    ///
    /// Usage: open the main scene (Assets/Scenes/NewVersion.unity) and choose
    /// "TENDOR/UI/Rebuild Body Tracking UI".
    /// </summary>
    public static class BodyTrackingUIRebuilder
    {
        [MenuItem("TENDOR/UI/Rebuild Body Tracking UI")]
        public static void Rebuild()
        {
            // Per project convention, use Object.FindObjectOfType (not the bare global) in editor scripts.
            var ui = Object.FindFirstObjectByType<BodyTrackingUI>();
            if (ui == null)
            {
                EditorUtility.DisplayDialog(
                    "Rebuild Body Tracking UI",
                    "No BodyTrackingUI component was found in the open scene.\n\n" +
                    "Open Assets/Scenes/NewVersion.unity (or add a BodyTrackingUI to the Canvas) and try again.",
                    "OK");
                return;
            }

            if (ui.controller == null)
                ui.controller = Object.FindFirstObjectByType<BodyTrackingController>();

            ui.BuildUI();

            EditorUtility.SetDirty(ui);
            EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
            Selection.activeGameObject = ui.gameObject;

            Debug.Log("[BodyTrackingUIRebuilder] Rebuilt the body-tracking UI on the Canvas. Save the scene to persist it.");
        }
    }
}
