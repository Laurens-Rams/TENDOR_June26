using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Animation;
using BodyTracking.Playback;

namespace BodyTracking.UI
{
    /// <summary>
    /// Dedicated playback-only screen: vertical move timeline on the right, frame-step transport on the
    /// bottom-left, top-left screen toggle + settings icons, recording picker on the timeline toolbar.
    /// Reached via <see cref="BodyTrackingUISwitcher"/> from the record screen.
    /// </summary>
    public class PlaybackScreenUI : MonoBehaviour
    {
        private const string RootName = "PlaybackUIRoot";
        private const float TimelineColumnWidth = 72f;
        private const float RecordingPickerPanelWidth = 252f;
        private const float RecordingPickerRowHeight = 36f;
        private const float RecordingPickerToggleWidth = 42f;
        private const float RecordingPickerRowFont = 13f;
        private const float RecordingPickerToggleFont = 11f;
        private const float PlaybackEdgeInset = UITokens.Space12;
        /// <summary>Top band reserved for the recording/character toolbar (matches top-left screen toggle row).</summary>
        private static float TopToolbarBand => PlaybackEdgeInset + UITokens.ToolbarIconDiameter + UITokens.Space8;
        /// <summary>Bottom inset shared with the left transport cluster (circle bottoms sit on this line).</summary>
        private static float BottomChromeInset => PlaybackEdgeInset;
        private static float PlayheadRadius => UITokens.PlaybackPlayheadDiameter * 0.5f;

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
        private Button tuneButton;
        private Button recordingPickerButton;
        private GameObject recordingPickerPanel;
        private RectTransform recordingPickerList;
        private bool recordingPickerVisible;
        private bool rebuildingRecordingPicker;
        private Button characterCycleButton;
        private CharacterSwitcher characterSwitcher;
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
            BuildTopLeftToolbar(uiRoot);

            uiRoot.gameObject.SetActive(isVisible);
        }

        public void SetVisible(bool visible)
        {
            isVisible = visible;
            EnsureInitialized();
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(visible);
            if (!visible)
            {
                recordingPickerVisible = false;
                if (recordingPickerPanel != null)
                    recordingPickerPanel.SetActive(false);
            }
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
            RecordingSelection.Instance.OnChanged += OnRecordingSelectionChanged;
            initialized = true;
        }

        void OnDestroy()
        {
            if (initialized)
                RecordingSelection.Instance.OnChanged -= OnRecordingSelectionChanged;
        }

        private void OnRecordingSelectionChanged()
        {
            if (recordingPickerVisible && !rebuildingRecordingPicker)
                RebuildRecordingPickerList();
        }

        private void HookUpEvents()
        {
            if (stepBackButton != null)
                stepBackButton.onClick.AddListener(() => controller?.StepPlaybackFrame(-1));
            if (stepForwardButton != null)
                stepForwardButton.onClick.AddListener(() => controller?.StepPlaybackFrame(1));
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
            float finishTopPad = UITokens.TransportPrimaryDiameter * 0.5f + UITokens.Space8;
            float timelineCenterX = -(PlaybackEdgeInset + TimelineColumnWidth * 0.5f);
            float toolbarY = -(PlaybackEdgeInset + UITokens.ToolbarIconDiameter * 0.5f);
            float iconSize = UITokens.ToolbarIconDiameter;

            var column = UIFactory.CreateRect("VerticalTimeline", root);
            column.anchorMin = new Vector2(1f, 0f);
            column.anchorMax = new Vector2(1f, 1f);
            column.pivot = new Vector2(1f, 0f);
            column.offsetMin = new Vector2(-(TimelineColumnWidth + PlaybackEdgeInset), BottomChromeInset);
            column.offsetMax = new Vector2(-PlaybackEdgeInset, -TopToolbarBand);

            characterCycleButton = UIFactory.CreateCircleButton(
                "CharacterCycle", root, iconSize, UITokens.PlaybackTransportBtn);
            var cycleRect = (RectTransform)characterCycleButton.transform;
            cycleRect.anchorMin = new Vector2(1f, 1f);
            cycleRect.anchorMax = new Vector2(1f, 1f);
            cycleRect.pivot = new Vector2(0.5f, 0.5f);
            cycleRect.anchoredPosition = new Vector2(timelineCenterX, toolbarY);
            UIFactory.AddSwitchIcon(cycleRect, iconSize * 0.46f, UITokens.OnSurface);
            characterSwitcher = Object.FindFirstObjectByType<CharacterSwitcher>();
            characterCycleButton.onClick.AddListener(OnCycleCharacterClicked);

            recordingPickerButton = UIFactory.CreateCircleButton(
                "RecordingPicker", root, iconSize, UITokens.PlaybackTransportBtn);
            var pickerRect = (RectTransform)recordingPickerButton.transform;
            pickerRect.anchorMin = new Vector2(1f, 1f);
            pickerRect.anchorMax = new Vector2(1f, 1f);
            pickerRect.pivot = new Vector2(0.5f, 0.5f);
            float pickerX = timelineCenterX - (iconSize + UITokens.Space8);
            pickerRect.anchoredPosition = new Vector2(pickerX, toolbarY);
            UIFactory.AddListIcon(pickerRect, iconSize * 0.46f, UITokens.OnSurface);
            recordingPickerButton.onClick.AddListener(ToggleRecordingPicker);
            BuildRecordingPickerPanel(root, pickerX);

            trackArea = UIFactory.CreateRect("TrackArea", column);
            trackArea.anchorMin = Vector2.zero;
            trackArea.anchorMax = Vector2.one;
            trackArea.offsetMin = new Vector2(UITokens.Space8, PlayheadRadius);
            trackArea.offsetMax = new Vector2(-UITokens.Space8, -(finishTopPad + UITokens.Space8));

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
            clusterRect.anchoredPosition = new Vector2(BottomChromeInset, BottomChromeInset);
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
            UIFactory.CreateText("Label", stepBackButton.transform, "−", UITokens.FontCaption, UITokens.OnSurface,
                TextAlignmentOptions.Center);

            playPauseButton = UIFactory.CreateCircleButton("PlayPause", clusterRect,
                UITokens.TransportPrimaryDiameter, UITokens.PlaybackTransportPlay);
            SetTransportLayout(playPauseButton.gameObject, UITokens.TransportPrimaryDiameter);
            playIcon = UIFactory.AddPlayIcon(playPauseButton.transform, 24f, Color.white).gameObject;
            pauseIcon = UIFactory.AddPauseIcon(playPauseButton.transform, 20f, Color.white);
            pauseIcon.SetActive(false);

            stepForwardButton = UIFactory.CreateCircleButton("StepForward", clusterRect, 44f, UITokens.PlaybackTransportBtn);
            SetTransportLayout(stepForwardButton.gameObject, 44f);
            UIFactory.CreateText("Label", stepForwardButton.transform, "+", UITokens.FontCaption, UITokens.OnSurface,
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

        private void OnCycleCharacterClicked()
        {
            if (characterSwitcher == null)
                characterSwitcher = Object.FindFirstObjectByType<CharacterSwitcher>();
            if (characterSwitcher == null)
            {
                UnityEngine.Debug.LogWarning("[PlaybackScreenUI] No CharacterSwitcher found — cannot cycle characters.");
                return;
            }

            characterSwitcher.CycleCharacter();

            // Keep the Recordings list in sync: the global switch drives the primary (timeline) recording, so
            // record its new character on that recording's entry.
            var sel = RecordingSelection.Instance;
            string primary = sel != null ? sel.PrimaryFileName : null;
            if (!string.IsNullOrEmpty(primary))
                sel.SetCharacterIndex(primary, characterSwitcher.CurrentIndex);
        }

        private void ToggleRecordingPicker()
        {
            recordingPickerVisible = !recordingPickerVisible;
            if (recordingPickerPanel != null)
            {
                if (recordingPickerVisible)
                    RebuildRecordingPickerList();
                recordingPickerPanel.SetActive(recordingPickerVisible);
            }
        }

        private void BuildRecordingPickerPanel(RectTransform root, float anchorX)
        {
            var panelRect = UIFactory.CreateRect("RecordingPickerPanel", root);
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(
                anchorX + UITokens.ToolbarIconDiameter * 0.5f,
                -TopToolbarBand);
            panelRect.sizeDelta = new Vector2(RecordingPickerPanelWidth, 0f);

            var bg = panelRect.gameObject.AddComponent<Image>();
            bg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusMedium);
            bg.type = Image.Type.Sliced;
            bg.color = UITokens.SurfaceElevated;
            bg.raycastTarget = true;

            var panelLe = panelRect.gameObject.AddComponent<LayoutElement>();
            panelLe.minWidth = RecordingPickerPanelWidth;
            panelLe.preferredWidth = RecordingPickerPanelWidth;
            panelLe.flexibleWidth = 0f;

            var vlayout = panelRect.gameObject.AddComponent<VerticalLayoutGroup>();
            vlayout.padding = new RectOffset((int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8);
            vlayout.spacing = UITokens.Space4;
            vlayout.childAlignment = TextAnchor.UpperLeft;
            vlayout.childControlWidth = true;
            vlayout.childControlHeight = true;
            vlayout.childForceExpandWidth = true;
            vlayout.childForceExpandHeight = false;

            var fitter = panelRect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = UIFactory.CreateText("RecordingPickerHeader", panelRect, "Recordings",
                UITokens.FontCaption - 4f, UITokens.Muted, TextAlignmentOptions.Left);
            var headerLe = header.gameObject.AddComponent<LayoutElement>();
            headerLe.minHeight = 18f;
            headerLe.preferredHeight = 18f;

            recordingPickerList = UIFactory.CreateRect("RecordingPickerList", panelRect);
            var listLe = recordingPickerList.gameObject.AddComponent<LayoutElement>();
            listLe.minWidth = 0f;
            listLe.flexibleWidth = 1f;
            var rows = recordingPickerList.gameObject.AddComponent<VerticalLayoutGroup>();
            rows.spacing = UITokens.Space4;
            rows.childControlWidth = true;
            rows.childControlHeight = true;
            rows.childForceExpandWidth = true;
            rows.childForceExpandHeight = false;
            var rowsFitter = recordingPickerList.gameObject.AddComponent<ContentSizeFitter>();
            rowsFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            rowsFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            recordingPickerPanel = panelRect.gameObject;
            recordingPickerPanel.SetActive(false);
        }

        private void RebuildRecordingPickerList()
        {
            if (recordingPickerList == null || rebuildingRecordingPicker)
                return;

            rebuildingRecordingPicker = true;
            try
            {
                for (int i = recordingPickerList.childCount - 1; i >= 0; i--)
                {
                    var child = recordingPickerList.GetChild(i);
                    if (Application.isPlaying) Destroy(child.gameObject);
                    else DestroyImmediate(child.gameObject);
                }

                var sel = RecordingSelection.Instance;
                if (controller != null)
                    sel.Refresh(controller.GetActiveMapId());

                var entries = sel.Entries;

                if (entries.Count == 0)
                {
                    var empty = UIFactory.CreateText("Empty", recordingPickerList, "No recordings yet",
                        RecordingPickerRowFont, UITokens.Muted, TextAlignmentOptions.Left);
                    var emptyLe = empty.gameObject.AddComponent<LayoutElement>();
                    emptyLe.minHeight = RecordingPickerRowHeight;
                    emptyLe.preferredHeight = RecordingPickerRowHeight;
                    return;
                }

                foreach (var entry in entries)
                    BuildRecordingPickerRow(entry);
            }
            finally
            {
                rebuildingRecordingPicker = false;
            }
        }

        private void BuildRecordingPickerRow(RecordingSelection.Entry entry)
        {
            var row = UIFactory.CreateRect("Row_" + entry.fileName, recordingPickerList);
            var rowLe = row.gameObject.AddComponent<LayoutElement>();
            rowLe.minHeight = RecordingPickerRowHeight;
            rowLe.preferredHeight = RecordingPickerRowHeight;
            rowLe.minWidth = 0f;
            rowLe.flexibleWidth = 1f;

            var rowBg = row.gameObject.AddComponent<Image>();
            rowBg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusSmall);
            rowBg.type = Image.Type.Sliced;
            rowBg.color = UITokens.Surface;
            rowBg.raycastTarget = false;

            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset((int)UITokens.Space8, (int)UITokens.Space8, 0, 0);
            hlg.spacing = UITokens.Space8;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            var label = UIFactory.CreateText("Label", row, FormatRecordingPickerLabel(entry),
                RecordingPickerRowFont, entry.hasFusion ? UITokens.OnSurface : UITokens.Muted,
                TextAlignmentOptions.Left);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            var labelLe = label.gameObject.AddComponent<LayoutElement>();
            labelLe.flexibleWidth = 1f;
            labelLe.minWidth = 0f;
            labelLe.preferredHeight = RecordingPickerRowHeight;

            var toggle = UIFactory.CreatePillButton("Toggle", row, entry.enabled ? "On" : "Off", ghost: true);
            ConfigurePickerToggle(toggle);
            var toggleText = toggle.GetComponentInChildren<TextMeshProUGUI>();
            var toggleImg = toggle.GetComponent<Image>();
            ApplyPickerToggleVisual(toggleImg, toggleText, entry.enabled);

            string file = entry.fileName;
            toggle.onClick.AddListener(() => OnRecordingToggleClicked(file, toggleImg, toggleText));
        }

        private static string FormatRecordingPickerLabel(RecordingSelection.Entry entry)
        {
            if (entry == null)
                return "—";
            if (entry.timestamp > System.DateTime.MinValue)
                return entry.timestamp.ToString("MMM d · HH:mm", CultureInfo.InvariantCulture);
            return entry.ShortLabel;
        }

        private static void ConfigurePickerToggle(Button btn)
        {
            var le = btn.gameObject.GetComponent<LayoutElement>();
            if (le == null) le = btn.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = RecordingPickerToggleWidth;
            le.minWidth = RecordingPickerToggleWidth;
            le.flexibleWidth = 0f;
            le.preferredHeight = RecordingPickerRowHeight - 8f;
            le.minHeight = RecordingPickerRowHeight - 8f;
            le.flexibleHeight = 0f;
            ((RectTransform)btn.transform).sizeDelta = new Vector2(RecordingPickerToggleWidth, RecordingPickerRowHeight - 8f);

            var text = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.fontSize = RecordingPickerToggleFont;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.overflowMode = TextOverflowModes.Overflow;
                text.margin = Vector4.zero;
                UIFactory.Stretch(text.rectTransform, 2f);
            }
        }

        private static void ApplyPickerToggleVisual(Image img, TextMeshProUGUI text, bool on)
        {
            if (text != null)
            {
                text.text = on ? "On" : "Off";
                text.color = on ? Color.white : UITokens.Muted;
            }
            if (img != null)
                img.color = on ? UITokens.Primary : UITokens.SurfaceElevated;
        }

        private void OnRecordingToggleClicked(string fileName, Image toggleImg, TextMeshProUGUI toggleText)
        {
            if (controller == null || string.IsNullOrEmpty(fileName))
                return;

            bool enabled = !RecordingSelection.Instance.IsEnabled(fileName);
            RecordingSelection.Instance.SetEnabled(fileName, enabled);
            ApplyPickerToggleVisual(toggleImg, toggleText, enabled);

            float t = controller.IsPlaying ? controller.PlaybackCurrentTime : 0f;
            controller.ApplyRecordingSelection(t);
            RefreshAll();
        }

        // ============================================================================================
        // TOP-LEFT TOOLBAR (screen toggle + settings)
        // ============================================================================================

        private void BuildTopLeftToolbar(RectTransform root)
        {
            backToRecordButton = UIFactory.CreateCircleButton(
                "ScreenToggle", root, UITokens.ToolbarIconDiameter, UITokens.PlaybackTransportBtn);
            UIFactory.PlaceTopLeftToolbarButton(
                (RectTransform)backToRecordButton.transform, 0, UITokens.ToolbarIconDiameter, PlaybackEdgeInset);
            UIFactory.AddRecordIcon(backToRecordButton.transform, UITokens.ToolbarIconDiameter * 0.28f,
                UITokens.Danger, square: false);

            tuneButton = UIFactory.CreateCircleButton(
                "OpenTuning", root, UITokens.ToolbarIconDiameter, UITokens.PlaybackTransportBtn);
            UIFactory.PlaceTopLeftToolbarButton(
                (RectTransform)tuneButton.transform, 1, UITokens.ToolbarIconDiameter, PlaybackEdgeInset);
            UIFactory.AddSettingsIcon(tuneButton.transform, UITokens.ToolbarIconDiameter * 0.46f, UITokens.OnSurface);
        }

        public Button BackToRecordButton => backToRecordButton;
        public Button TuneButton => tuneButton;

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
