using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Playback;

namespace BodyTracking.UI
{
    /// <summary>
    /// Dedicated playback-only screen: vertical move timeline on the right, frame-step transport on the
    /// bottom-left. Reached via <see cref="BodyTrackingUISwitcher"/> from the record/settings screen.
    /// </summary>
    public class PlaybackScreenUI : MonoBehaviour
    {
        private const string RootName = "PlaybackUIRoot";

        [Header("System")]
        public BodyTrackingController controller;

        private RectTransform uiRoot;
        private RectTransform trackArea;
        private RectTransform playhead;
        private PlaybackTimelineDragHandle playheadDrag;
        private Image trackLine;
        private readonly List<CheckpointWidget> checkpoints = new List<CheckpointWidget>();
        private Button stepBackButton;
        private Button playPauseButton;
        private Button stepForwardButton;
        private Button speedButton;
        private Button loopButton;
        private TextMeshProUGUI speedLabel;
        private TextMeshProUGUI loopLabel;
        private GameObject playIcon;
        private GameObject pauseIcon;
        private Button backToRecordButton;
        private Button finishButton;
        private Image finishCircle;
        private GameObject finishCheckmark;

        private bool initialized;
        private bool isVisible;
        private const float RefreshInterval = 0.05f;
        private float nextRefreshTime;

        private struct CheckpointWidget
        {
            public Button button;
            public Image circle;
            public TextMeshProUGUI label;
            public int index;
        }

        void OnEnable()
        {
            EnsureInitialized();
            controller?.LoadLatestRecording();
            RefreshAll();
        }

        void OnDisable()
        {
            // Visibility is controlled by BodyTrackingUISwitcher.SetVisible.
        }

        public void BuildUI()
        {
            var canvas = EnsureCanvas();
            EnsureEventSystem();

            var existing = canvas.transform.Find(RootName);
            if (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
            }

            uiRoot = UIFactory.CreateRect(RootName, canvas.transform);
            UIFactory.Stretch(uiRoot);
            uiRoot.gameObject.AddComponent<UISafeArea>();

            BuildVerticalTimeline(uiRoot);
            BuildTransportCluster(uiRoot);
            BuildBackButton(uiRoot);

            uiRoot.gameObject.SetActive(isVisible);
        }

        public void SetVisible(bool visible)
        {
            isVisible = visible;
            EnsureInitialized();
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(visible);
            if (visible)
                RefreshAll();
        }

        private void EnsureInitialized()
        {
            if (initialized) return;

            if (controller == null)
                controller = Object.FindFirstObjectByType<BodyTrackingController>();

            BuildUI();
            HookUpEvents();
            initialized = true;
        }

        private void HookUpEvents()
        {
            if (stepBackButton != null)
                stepBackButton.onClick.AddListener(() => controller?.StepPlaybackTwoFrames(-1));
            if (stepForwardButton != null)
                stepForwardButton.onClick.AddListener(() => controller?.StepPlaybackTwoFrames(1));
            if (playPauseButton != null)
                playPauseButton.onClick.AddListener(OnPlayPauseClicked);
            if (speedButton != null)
                speedButton.onClick.AddListener(OnSpeedClicked);
            if (loopButton != null)
                loopButton.onClick.AddListener(OnLoopClicked);
        }

        void Update()
        {
            if (uiRoot == null || !uiRoot.gameObject.activeSelf)
                return;

            if (Time.unscaledTime >= nextRefreshTime)
            {
                nextRefreshTime = Time.unscaledTime + RefreshInterval;
                RefreshAll();
            }
        }

        private void RefreshAll()
        {
            UpdatePlayhead();
            UpdateCheckpointColors();
            UpdateTransportButtons();
        }

        // ============================================================================================
        // VERTICAL TIMELINE (right edge)
        // ============================================================================================

        private void BuildVerticalTimeline(RectTransform root)
        {
            var column = UIFactory.CreateRect("VerticalTimeline", root);
            column.anchorMin = new Vector2(1f, 0f);
            column.anchorMax = new Vector2(1f, 1f);
            column.pivot = new Vector2(1f, 0.5f);
            column.anchoredPosition = new Vector2(-UITokens.Space12, 18f);
            column.sizeDelta = new Vector2(72f, -UITokens.Space24 * 2f);

            trackArea = UIFactory.CreateRect("TrackArea", column);
            UIFactory.Stretch(trackArea, UITokens.Space8);

            // White vertical track line.
            var lineRect = UIFactory.CreateRect("TrackLine", trackArea);
            lineRect.anchorMin = new Vector2(0.5f, 0f);
            lineRect.anchorMax = new Vector2(0.5f, 1f);
            lineRect.pivot = new Vector2(0.5f, 0.5f);
            lineRect.sizeDelta = new Vector2(UITokens.PlaybackTrackWidth, 0f);
            trackLine = lineRect.gameObject.AddComponent<Image>();
            trackLine.color = UITokens.PlaybackTrack;
            trackLine.raycastTarget = false;

            checkpoints.Clear();
            for (int i = 0; i < BodyTrackingController.PlaybackCheckpointCount; i++)
                checkpoints.Add(BuildCheckpoint(trackArea, i));

            BuildFinishMarker(trackArea);

            // Moving playhead — teal circle with a white play icon; draggable to scrub playback.
            playhead = UIFactory.CreateRect("Playhead", trackArea);
            playhead.anchorMin = new Vector2(0.5f, 0f);
            playhead.anchorMax = new Vector2(0.5f, 0f);
            playhead.pivot = new Vector2(0.5f, 0.5f);
            playhead.sizeDelta = new Vector2(UITokens.PlaybackPlayheadDiameter, UITokens.PlaybackPlayheadDiameter);
            var playheadImg = playhead.gameObject.AddComponent<Image>();
            playheadImg.sprite = UIFactory.CircleSprite();
            playheadImg.color = UITokens.PlaybackPlayhead;
            playheadImg.raycastTarget = true;
            UIFactory.AddPlayIcon(playhead, UITokens.PlaybackPlayheadDiameter * 0.55f, Color.white);

            var checkpointButtons = new Button[checkpoints.Count];
            for (int i = 0; i < checkpoints.Count; i++)
                checkpointButtons[i] = checkpoints[i].button;

            playheadDrag = playhead.gameObject.AddComponent<PlaybackTimelineDragHandle>();
            playheadDrag.Initialize(controller, trackArea, checkpointButtons);
            playheadDrag.OnScrubbed = UpdatePlayhead;
        }

        /// <summary>Top-of-route finish marker (play-button size, checkmark inside).</summary>
        private void BuildFinishMarker(RectTransform parent)
        {
            float diameter = UITokens.TransportPrimaryDiameter;
            finishButton = UIFactory.CreateCircleButton("FinishMarker", parent, diameter, UITokens.PlaybackNode);
            var rect = (RectTransform)finishButton.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;

            finishCircle = finishButton.GetComponent<Image>();
            finishCheckmark = UIFactory.AddCheckmarkIcon(rect, diameter * 0.42f, UITokens.PlaybackNodeActive);
            finishButton.onClick.AddListener(OnFinishClicked);
        }

        private void OnFinishClicked()
        {
            if (controller == null) return;
            float duration = controller.PlaybackDuration;
            if (duration <= 0f) return;
            controller.PlayFromTime(Mathf.Max(0f, duration - 0.05f));
            RefreshAll();
        }

        private CheckpointWidget BuildCheckpoint(RectTransform parent, int index)
        {
            int moveNumber = index + 1;
            var btn = UIFactory.CreateCircleButton(
                $"Checkpoint_{moveNumber}",
                parent,
                UITokens.PlaybackNodeDiameter,
                UITokens.PlaybackNode);

            var rect = (RectTransform)btn.transform;
            float normalized = index / (float)BodyTrackingController.PlaybackCheckpointCount;
            rect.anchorMin = new Vector2(0.5f, normalized);
            rect.anchorMax = new Vector2(0.5f, normalized);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;

            var label = UIFactory.CreateBoldText(
                "Label",
                rect,
                moveNumber.ToString(),
                UITokens.FontBody,
                UITokens.OnSurface,
                TextAlignmentOptions.Center);
            UIFactory.Stretch(label.rectTransform, UITokens.Space4);

            int captured = index;
            btn.onClick.AddListener(() => OnCheckpointClicked(captured));

            return new CheckpointWidget
            {
                button = btn,
                circle = btn.GetComponent<Image>(),
                label = label,
                index = index
            };
        }

        private void UpdatePlayhead()
        {
            if (playhead == null || controller == null)
                return;

            float progress = controller.PlaybackNormalizedProgress;
            playhead.anchorMin = new Vector2(0.5f, progress);
            playhead.anchorMax = new Vector2(0.5f, progress);
            playhead.anchoredPosition = Vector2.zero;
        }

        private void UpdateCheckpointColors()
        {
            if (controller == null) return;
            int active = controller.ActiveCheckpointIndex;

            foreach (var cp in checkpoints)
            {
                bool isActive = cp.index == active;
                if (cp.circle != null)
                    cp.circle.color = isActive ? UITokens.PlaybackNodeActive : UITokens.PlaybackNode;
                if (cp.label != null)
                    cp.label.color = isActive ? Color.black : UITokens.OnSurface;
            }
        }

        private void OnCheckpointClicked(int index)
        {
            if (controller == null || (playheadDrag != null && playheadDrag.IsDragging))
                return;
            controller.JumpToCheckpoint(index);
            RefreshAll();
        }

        // ============================================================================================
        // BOTTOM-LEFT TRANSPORT
        // ============================================================================================

        private void BuildTransportCluster(RectTransform root)
        {
            // Floating controls — no panel background; individual glass circles only.
            var clusterRect = UIFactory.CreateRect("PlaybackTransport", root);
            clusterRect.anchorMin = new Vector2(0f, 0f);
            clusterRect.anchorMax = new Vector2(0f, 0f);
            clusterRect.pivot = new Vector2(0f, 0f);
            clusterRect.anchoredPosition = new Vector2(UITokens.Space12, UITokens.Space12);
            clusterRect.sizeDelta = new Vector2(272f, UITokens.TransportPrimaryDiameter);

            var layout = clusterRect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UITokens.Space8;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            stepBackButton = UIFactory.CreateCircleButton("StepBack", clusterRect, 44f, UITokens.PlaybackTransportBtn);
            SetTransportLayout(stepBackButton.gameObject, 44f);
            UIFactory.CreateText("Label", stepBackButton.transform, "−2", UITokens.FontCaption, UITokens.OnSurface,
                TextAlignmentOptions.Center);

            playPauseButton = UIFactory.CreateCircleButton("PlayPause", clusterRect,
                UITokens.TransportPrimaryDiameter, UITokens.PlaybackTransportPlay);
            SetTransportLayout(playPauseButton.gameObject, UITokens.TransportPrimaryDiameter);
            playIcon = UIFactory.AddPlayIcon(playPauseButton.transform, 24f, Color.white).gameObject;
            pauseIcon = UIFactory.AddPauseIcon(playPauseButton.transform, 20f, Color.white);
            pauseIcon.SetActive(false);

            stepForwardButton = UIFactory.CreateCircleButton("StepForward", clusterRect, 44f, UITokens.PlaybackTransportBtn);
            SetTransportLayout(stepForwardButton.gameObject, 44f);
            UIFactory.CreateText("Label", stepForwardButton.transform, "+2", UITokens.FontCaption, UITokens.OnSurface,
                TextAlignmentOptions.Center);

            speedButton = UIFactory.CreateCircleButton("Speed", clusterRect, 44f, UITokens.PlaybackTransportBtn);
            SetTransportLayout(speedButton.gameObject, 44f);
            speedLabel = UIFactory.CreateText("SpeedLabel", speedButton.transform, "1.0×", UITokens.FontCaption,
                UITokens.OnSurface, TextAlignmentOptions.Center);
            UIFactory.Stretch(speedLabel.rectTransform, UITokens.Space4);

            loopButton = UIFactory.CreateCircleButton("LoopMode", clusterRect, 34f, UITokens.PlaybackTransportBtn);
            SetTransportLayout(loopButton.gameObject, 34f);
            loopLabel = UIFactory.CreateBoldText("LoopLabel", loopButton.transform, "A", 16f,
                UITokens.OnSurface, TextAlignmentOptions.Center);
            UIFactory.Stretch(loopLabel.rectTransform, 2f);
        }

        private static void SetTransportLayout(GameObject go, float size)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredWidth = size;
            le.preferredHeight = size;
            le.minWidth = size;
            le.minHeight = size;
        }

        private void UpdateTransportButtons()
        {
            if (controller == null) return;

            bool isPlaying = controller.IsPlaying;
            bool isPaused = controller.IsPaused;
            bool showPause = isPlaying && !isPaused;

            if (playIcon != null) playIcon.SetActive(!showPause);
            if (pauseIcon != null) pauseIcon.SetActive(showPause);

            if (playPauseButton != null)
            {
                var playBg = playPauseButton.GetComponent<Image>();
                if (playBg != null)
                    playBg.color = showPause ? UITokens.PlaybackNodeActive : UITokens.PlaybackTransportPlay;
            }

            if (speedLabel != null)
                speedLabel.text = $"{controller.PlaybackSpeed:0.0}×";

            if (loopLabel != null)
            {
                loopLabel.text = controller.PlaybackLoopMode == PlaybackLoopMode.Segment ? "M" : "A";
                loopLabel.color = controller.PlaybackLoopMode == PlaybackLoopMode.Segment
                    ? UITokens.PlaybackNodeActive
                    : new Color(1f, 1f, 1f, 0.72f);
            }

            // Highlight finish marker when playback is near the end.
            if (finishCircle != null && controller.PlaybackDuration > 0f)
            {
                bool atFinish = controller.PlaybackNormalizedProgress >= 0.95f;
                finishCircle.color = atFinish ? UITokens.PlaybackNodeActive : UITokens.PlaybackNode;
            }

            bool canControl = controller.CanPlayback || isPlaying;
            bool draggingPlayhead = playheadDrag != null && playheadDrag.IsDragging;
            if (playPauseButton != null) playPauseButton.interactable = canControl && !draggingPlayhead;
            if (stepBackButton != null) stepBackButton.interactable = canControl && !draggingPlayhead;
            if (stepForwardButton != null) stepForwardButton.interactable = canControl && !draggingPlayhead;
            if (speedButton != null) speedButton.interactable = canControl && !draggingPlayhead;
            if (loopButton != null) loopButton.interactable = canControl && !draggingPlayhead;

            if (!draggingPlayhead)
            {
                foreach (var cp in checkpoints)
                {
                    if (cp.button != null)
                        cp.button.interactable = canControl;
                }
            }
        }

        private void OnPlayPauseClicked()
        {
            if (controller == null) return;

            if (!controller.IsPlaying)
            {
                controller.LoadLatestRecording();
                if (!controller.StartPlayback())
                    return;
            }
            else if (controller.IsPaused)
            {
                controller.ResumePlayback();
            }
            else
            {
                controller.PausePlayback();
            }

            RefreshAll();
        }

        private void OnSpeedClicked()
        {
            controller?.CyclePlaybackSpeed();
            RefreshAll();
        }

        private void OnLoopClicked()
        {
            controller?.CyclePlaybackLoopMode();
            RefreshAll();
        }

        // ============================================================================================
        // SCREEN SWITCH
        // ============================================================================================

        private void BuildBackButton(RectTransform root)
        {
            backToRecordButton = UIFactory.CreatePillButton("BackToRecord", root, "Record", ghost: true);
            var rect = (RectTransform)backToRecordButton.transform;
            rect.sizeDelta = new Vector2(96f, 34f);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(UITokens.Space12, -UITokens.Space8);
        }

        public Button BackToRecordButton => backToRecordButton;

        // ============================================================================================
        // SHARED CANVAS / EVENT SYSTEM
        // ============================================================================================

        private Canvas EnsureCanvas()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = Object.FindFirstObjectByType<Canvas>();

            if (canvas == null)
            {
                var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(390f, 844f);
                scaler.matchWidthOrHeight = 0.5f;
            }

            return canvas;
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
        }
    }
}
