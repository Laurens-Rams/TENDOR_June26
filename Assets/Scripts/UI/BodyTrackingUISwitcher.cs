using UnityEngine;
using UnityEngine.UI;

namespace BodyTracking.UI
{
    /// <summary>Which full-screen UI mode is active.</summary>
    public enum AppScreen
    {
        Record,
        Playback
    }

    /// <summary>
    /// Toggles between the record/settings screen (<see cref="BodyTrackingUI"/>) and the dedicated
    /// playback screen (<see cref="PlaybackScreenUI"/>).
    /// </summary>
    public class BodyTrackingUISwitcher : MonoBehaviour
    {
        [Header("Screens")]
        public BodyTrackingUI recordScreen;
        public PlaybackScreenUI playbackScreen;

        [Header("State")]
        [SerializeField] private AppScreen activeScreen = AppScreen.Record;

        public AppScreen ActiveScreen => activeScreen;

        void Awake()
        {
            EnsureScreens();
        }

        void Start()
        {
            EnsureScreens();
            WireToggleButtons();
            ApplyScreen(activeScreen, stopPlaybackOnLeave: false);
        }

        public void ShowRecord()
        {
            ApplyScreen(AppScreen.Record, stopPlaybackOnLeave: true);
        }

        public void ShowPlayback()
        {
            ApplyScreen(AppScreen.Playback, stopPlaybackOnLeave: false);
        }

        public void ToggleScreen()
        {
            ApplyScreen(activeScreen == AppScreen.Record ? AppScreen.Playback : AppScreen.Record,
                stopPlaybackOnLeave: activeScreen == AppScreen.Playback);
        }

        private void EnsureScreens()
        {
            if (recordScreen == null)
                recordScreen = GetComponent<BodyTrackingUI>();
            if (recordScreen == null)
                recordScreen = Object.FindFirstObjectByType<BodyTrackingUI>();

            if (playbackScreen == null)
                playbackScreen = GetComponent<PlaybackScreenUI>();
            if (playbackScreen == null)
                playbackScreen = Object.FindFirstObjectByType<PlaybackScreenUI>();

            if (playbackScreen == null)
            {
                playbackScreen = gameObject.AddComponent<PlaybackScreenUI>();
                if (recordScreen != null && recordScreen.controller != null)
                    playbackScreen.controller = recordScreen.controller;
            }
        }

        private void WireToggleButtons()
        {
            if (recordScreen != null)
                recordScreen.WireScreenSwitcher(this);

            if (playbackScreen != null && playbackScreen.BackToRecordButton != null)
            {
                playbackScreen.BackToRecordButton.onClick.RemoveListener(ShowRecord);
                playbackScreen.BackToRecordButton.onClick.AddListener(ShowRecord);
            }
        }

        private void ApplyScreen(AppScreen screen, bool stopPlaybackOnLeave)
        {
            EnsureScreens();

            if (stopPlaybackOnLeave && activeScreen == AppScreen.Playback && screen == AppScreen.Record)
            {
                var controller = recordScreen != null ? recordScreen.controller : null;
                if (controller == null && playbackScreen != null)
                    controller = playbackScreen.controller;
                if (controller != null && controller.IsPlaying)
                    controller.StopPlayback();
            }

            activeScreen = screen;

            if (recordScreen != null)
                recordScreen.SetScreenVisible(screen == AppScreen.Record);

            if (playbackScreen != null)
                playbackScreen.SetVisible(screen == AppScreen.Playback);

            if (screen == AppScreen.Playback && playbackScreen != null)
            {
                var controller = playbackScreen.controller;
                if (controller == null && recordScreen != null)
                    controller = recordScreen.controller;
                controller?.LoadLatestRecording();
            }
        }
    }
}
