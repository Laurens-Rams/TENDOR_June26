using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BodyTracking.UI;

namespace BodyTracking.Editor
{
    /// <summary>
    /// Editor helpers for switching and rebuilding body-tracking UI skins.
    /// </summary>
    public static class BodyTrackingUIRebuilder
    {
        [MenuItem("TENDOR/UI/Rebuild Body Tracking UI (Classic)")]
        public static void RebuildClassic()
        {
            var ui = Object.FindFirstObjectByType<BodyTrackingUI>();
            if (ui == null)
            {
                EditorUtility.DisplayDialog(
                    "Rebuild Body Tracking UI",
                    "No BodyTrackingUI component was found in the open scene.\n\n" +
                    "Open Assets/Scenes/NewVersion.unity (or add BodyTrackingUI to the Canvas) and try again.",
                    "OK");
                return;
            }

            if (ui.controller == null)
                ui.controller = Object.FindFirstObjectByType<BodyTrackingController>();

            ui.BuildUI();
            MarkDirty(ui.gameObject);
            Debug.Log("[BodyTrackingUIRebuilder] Rebuilt Classic UI.");
        }

        [MenuItem("TENDOR/UI/Rebuild Playback Screen UI")]
        public static void RebuildPlayback()
        {
            var ui = Object.FindFirstObjectByType<PlaybackScreenUI>();
            if (ui == null)
            {
                EditorUtility.DisplayDialog(
                    "Rebuild Playback Screen UI",
                    "No PlaybackScreenUI component was found in the open scene.\n\n" +
                    "Run TENDOR/UI/Add UI Switcher To Canvas first.",
                    "OK");
                return;
            }

            if (ui.controller == null)
                ui.controller = Object.FindFirstObjectByType<BodyTrackingController>();

            ui.BuildUI();
            MarkDirty(ui.gameObject);
            Debug.Log("[BodyTrackingUIRebuilder] Rebuilt Playback Screen UI.");
        }

        [MenuItem("TENDOR/UI/Rebuild Body Tracking UI V2")]
        public static void RebuildV2()
        {
            RebuildPlayback();
        }

        [MenuItem("TENDOR/UI/Use Classic UI")]
        public static void UseClassicUi()
        {
            ApplyScreen(AppScreen.Record);
        }

        [MenuItem("TENDOR/UI/Use Playback UI")]
        public static void UsePlaybackUi()
        {
            ApplyScreen(AppScreen.Playback);
        }

        [MenuItem("TENDOR/UI/Add UI Switcher To Canvas")]
        public static void AddSwitcherToCanvas()
        {
            var canvas = Object.FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                EditorUtility.DisplayDialog("Add UI Switcher", "No Canvas found in the open scene.", "OK");
                return;
            }

            var switcher = canvas.GetComponent<BodyTrackingUISwitcher>();
            if (switcher == null)
                switcher = Undo.AddComponent<BodyTrackingUISwitcher>(canvas.gameObject);

            var classic = canvas.GetComponent<BodyTrackingUI>();
            if (classic == null)
                classic = Undo.AddComponent<BodyTrackingUI>(canvas.gameObject);

            var playback = canvas.GetComponent<PlaybackScreenUI>();
            if (playback == null)
                playback = Undo.AddComponent<PlaybackScreenUI>(canvas.gameObject);

            if (classic.controller == null)
                classic.controller = Object.FindFirstObjectByType<BodyTrackingController>();
            if (playback.controller == null)
                playback.controller = classic.controller;

            switcher.recordScreen = classic;
            switcher.playbackScreen = playback;

            classic.BuildUI();
            playback.BuildUI();
            switcher.ShowRecord();
            MarkDirty(canvas.gameObject);
            Selection.activeGameObject = canvas.gameObject;
            Debug.Log("[BodyTrackingUIRebuilder] Added BodyTrackingUISwitcher + record & playback screens to the Canvas.");
        }

        private static void ApplyScreen(AppScreen screen)
        {
            var switcher = Object.FindFirstObjectByType<BodyTrackingUISwitcher>();
            if (switcher == null)
            {
                AddSwitcherToCanvas();
                switcher = Object.FindFirstObjectByType<BodyTrackingUISwitcher>();
            }

            if (switcher == null)
                return;

            if (screen == AppScreen.Playback)
                switcher.ShowPlayback();
            else
                switcher.ShowRecord();

            MarkDirty(switcher.gameObject);
            Debug.Log($"[BodyTrackingUIRebuilder] Switched active UI to {screen}.");
        }

        private static void MarkDirty(GameObject go)
        {
            EditorUtility.SetDirty(go);
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
        }
    }
}
