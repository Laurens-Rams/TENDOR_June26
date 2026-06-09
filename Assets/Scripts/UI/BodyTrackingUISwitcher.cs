using UnityEngine;
using UnityEngine.UI;

namespace BodyTracking.UI
{
    /// <summary>Which full-screen UI mode is active.</summary>
    public enum AppScreen
    {
        Record,
        Playback,
        Tuning,
        Recordings
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
        public TuningScreenUI tuningScreen;
        public RecordingsMenuUI recordingsScreen;

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

        public void ShowTuning()
        {
            // Reached from Playback; keep playback running so edits are visible on the character.
            ApplyScreen(AppScreen.Tuning, stopPlaybackOnLeave: false);
        }

        public void ShowRecordings()
        {
            // Reached from Playback; keep playback running so toggled recordings overlap live in the scene.
            ApplyScreen(AppScreen.Recordings, stopPlaybackOnLeave: false);
        }

        public void ToggleScreen()
        {
            // Cycle Record -> Playback -> Tuning -> Record.
            AppScreen next = activeScreen == AppScreen.Record ? AppScreen.Playback
                : activeScreen == AppScreen.Playback ? AppScreen.Tuning
                : AppScreen.Record;
            ApplyScreen(next, stopPlaybackOnLeave: activeScreen != AppScreen.Record);
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

            if (tuningScreen == null)
                tuningScreen = GetComponent<TuningScreenUI>();
            if (tuningScreen == null)
                tuningScreen = Object.FindFirstObjectByType<TuningScreenUI>();
            if (tuningScreen == null)
                tuningScreen = gameObject.AddComponent<TuningScreenUI>();

            if (recordingsScreen == null)
                recordingsScreen = GetComponent<RecordingsMenuUI>();
            if (recordingsScreen == null)
                recordingsScreen = Object.FindFirstObjectByType<RecordingsMenuUI>();
            if (recordingsScreen == null)
                recordingsScreen = gameObject.AddComponent<RecordingsMenuUI>();
            if (recordingsScreen.controller == null && recordScreen != null)
                recordingsScreen.controller = recordScreen.controller;
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

            if (playbackScreen != null && playbackScreen.TuneButton != null)
            {
                playbackScreen.TuneButton.onClick.RemoveListener(ShowTuning);
                playbackScreen.TuneButton.onClick.AddListener(ShowTuning);
            }

            if (tuningScreen != null && tuningScreen.BackButton != null)
            {
                tuningScreen.BackButton.onClick.RemoveListener(ShowPlayback);
                tuningScreen.BackButton.onClick.AddListener(ShowPlayback);
            }

            if (recordingsScreen != null && recordingsScreen.BackButton != null)
            {
                recordingsScreen.BackButton.onClick.RemoveListener(ShowPlayback);
                recordingsScreen.BackButton.onClick.AddListener(ShowPlayback);
            }
        }

        private void ApplyScreen(AppScreen screen, bool stopPlaybackOnLeave)
        {
            EnsureScreens();

            if (stopPlaybackOnLeave && activeScreen != AppScreen.Record && screen == AppScreen.Record)
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

            if (tuningScreen != null)
                tuningScreen.SetVisible(screen == AppScreen.Tuning);

            if (recordingsScreen != null)
                recordingsScreen.SetVisible(screen == AppScreen.Recordings);

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
