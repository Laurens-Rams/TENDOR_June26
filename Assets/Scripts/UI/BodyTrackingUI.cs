using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Spatial;
using BodyTracking.Storage;
using BodyTracking.Animation;
using BodyTracking.MoveAI;

namespace BodyTracking.UI
{
    /// <summary>
    /// Production UI for the AR climbing body-tracking app.
    ///
    /// The entire interface is generated programmatically from the centralized design tokens
    /// (<see cref="UITokens"/>) via <see cref="UIFactory"/>, so styling is consistent and there are no
    /// hand-edited scene/prefab dependencies. Call <see cref="BuildUI"/> at runtime (done automatically
    /// in <see cref="Start"/>) or from the Editor menu "TENDOR/UI/Rebuild Body Tracking UI" to (re)create
    /// the canvas contents.
    ///
    /// Layout:
    ///   • Top-left toolbar — round screen-toggle (play ↔ record) matching playback chrome.
    ///   • Top status bar  — alignment + Move AI pills on one compact row (margined from screen edges).
    ///   • Bottom transport bar — media-style record / play-pause / stop controls, a recording selector,
    ///     and a scrub/timeline slider with current/total mm:ss labels.
    ///
    /// The localization pill is bound to <c>BodyTrackingController.IsLocalized</c> (which reflects
    /// <c>RouteRootManager.IsLocalized</c>) and shows "Waiting for localization…" until localized.
    ///
    /// All existing functional hooks are preserved: start/stop recording, play/stop playback, load
    /// recordings, and the controller status/mode text references.
    /// </summary>
    public class BodyTrackingUI : MonoBehaviour
    {
        // Kept public so existing scene wiring and the Editor SceneValidationTool continue to resolve.
        // These are auto-populated by BuildUI() when the UI is generated.
        [Header("UI Elements (auto-generated)")]
        public Button recordButton;
        public Button stopRecordButton;
        public Button playButton;
        public Button stopPlayButton;
        public Button loadButton;
        public TextMeshProUGUI statusText;
        public TextMeshProUGUI modeText;
        public TMP_Dropdown recordingsDropdown;

        [Header("UI visibility")]
        [Tooltip("Optional panel toggled with the main UI visibility (legacy; unused on the minimal record screen).")]
        [SerializeField] private GameObject mainUiPanel;

        [Header("System")]
        public BodyTrackingController controller;

        // Generated element references used for per-frame updates.
        private const string RootName = "TendorUIRoot";
        private RectTransform uiRoot;
        private Image recordIcon;
        private GameObject playIcon;
        private GameObject pauseIcon;
        private Image moveDot;
        private TextMeshProUGUI moveLabel;
        private GameObject moveStatusPill;
        private Button moveStatusButton;
        private TextMeshProUGUI moveCountBadge;
        private GameObject moveQueuePanel;
        private RectTransform moveQueueListContent;
        private TextMeshProUGUI moveQueueEmptyLabel;
        private bool moveQueuePanelVisible;
        private float nextMoveQueueRefreshTime;
        private TextMeshProUGUI currentTimeLabel, totalTimeLabel, recordingNameLabel;
        private Slider scrubSlider;
        private Button prevRecordingButton, nextRecordingButton;
        private Image alignDot;
        private TextMeshProUGUI alignLabel;
        private Button alignButton;
        private ImmersalMapSwitcher mapSwitcher;
        private TextMeshProUGUI mapToggleLabel;
        private GameObject mapPanel;
        private TMP_InputField mapIdInput;
        private Button mapLoadButton;
        private TextMeshProUGUI mapLoadButtonLabel;
        private TextMeshProUGUI mapStatusLabel;
        private bool mapPanelVisible;
        private Button goToPlaybackButton;

        // Record + review controls (minimal record screen).
        private const float RecordButtonDiameter = 74f;
        private const float TopBarInset = UITokens.Space12;
        private const float TopBarPillHeight = 20f;
        private const float TopBarPillFont = 12f;
        private const float TopBarDotSize = 6f;
        private const float TopLeftToolbarInset = UITokens.Space12;
        private GameObject reviewRoot;
        private Button reviewReplayButton;
        private GameObject reviewReplayIcon;
        private GameObject reviewPauseIcon;
        private Button rejectButton;
        private Button confirmButton;
        private TextMeshProUGUI reviewCaption;

        // State
        private readonly List<string> availableRecordings = new List<string>();
        private int selectedRecordingIndex;
        private bool mainUiVisible = true;
        private bool suppressScrubCallback;
        private const float TransportRefreshInterval = 0.1f;
        private float nextTransportRefreshTime;
        private float selectedRecordingDuration;

        private bool initialized;

        void Start()
        {
            if (Object.FindFirstObjectByType<BodyTrackingUISwitcher>() != null)
                return;
            var canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
                canvas.gameObject.AddComponent<BodyTrackingUISwitcher>();
        }

        void OnEnable()
        {
            EnsureInitialized();
            if (uiRoot != null && Object.FindFirstObjectByType<BodyTrackingUISwitcher>() == null)
                uiRoot.gameObject.SetActive(true);
            SubscribeControllerEvents();
            RefreshRecordingsList();
            UpdateUI();
        }

        void OnDisable()
        {
            UnsubscribeControllerEvents();
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            UnsubscribeControllerEvents();

            if (mapSwitcher != null)
            {
                mapSwitcher.OnStatusChanged -= OnMapSwitcherStatusChanged;
                mapSwitcher.OnMapSwitched -= OnMapSwitched;
            }
        }

        private void EnsureInitialized()
        {
            if (initialized)
                return;

            if (controller == null)
                controller = UnityEngine.Object.FindFirstObjectByType<BodyTrackingController>();

            if (controller == null)
                UnityEngine.Debug.LogError("[BodyTrackingUI] BodyTrackingController not found");

            BuildUI();
            HookUpEvents();
            initialized = true;
        }

        private void SubscribeControllerEvents()
        {
            if (controller == null)
                return;

            controller.OnModeChanged -= OnModeChanged;
            controller.OnRecordingComplete -= OnRecordingComplete;
            controller.OnFusionStatusChanged -= OnFusionStatusChanged;
            controller.OnReviewStarted -= OnReviewStarted;
            controller.OnReviewResolved -= OnReviewResolved;
            controller.OnModeChanged += OnModeChanged;
            controller.OnRecordingComplete += OnRecordingComplete;
            controller.OnFusionStatusChanged += OnFusionStatusChanged;
            controller.OnReviewStarted += OnReviewStarted;
            controller.OnReviewResolved += OnReviewResolved;
        }

        private void UnsubscribeControllerEvents()
        {
            if (controller == null)
                return;

            controller.OnModeChanged -= OnModeChanged;
            controller.OnRecordingComplete -= OnRecordingComplete;
            controller.OnFusionStatusChanged -= OnFusionStatusChanged;
            controller.OnReviewStarted -= OnReviewStarted;
            controller.OnReviewResolved -= OnReviewResolved;
        }

        void Update()
        {
            // RouteRootManager may appear after our first BuildUI(); create map controls once it exists.
            TryBuildMapSwitcherLate();

            // Map id can sync after Start(); keep the toggle label current even when the transport bar is hidden.
            if (Time.frameCount % 10 == 0)
                UpdateMapToggleLabel();

            if (!mainUiVisible)
                return;

            // Text and slider updates allocate/layout; 10 Hz is responsive enough for transport feedback.
            if (Time.unscaledTime >= nextTransportRefreshTime)
            {
                nextTransportRefreshTime = Time.unscaledTime + TransportRefreshInterval;
                UpdateTransportTime();
            }

            if (Time.frameCount % 10 == 0)
                UpdateUI();

            // Keep the open Move AI queue panel live (states advance as jobs upload/process).
            if (moveQueuePanelVisible && Time.unscaledTime >= nextMoveQueueRefreshTime)
            {
                nextMoveQueueRefreshTime = Time.unscaledTime + 0.5f;
                RebuildMoveQueueList();
            }
        }

        // ============================================================================================
        // UI CONSTRUCTION
        // ============================================================================================

        /// <summary>
        /// Build (or rebuild) the complete UI under the Canvas. Safe to call repeatedly: any previously
        /// generated root is removed first. Invoked automatically at runtime and by the Editor rebuild
        /// menu command.
        /// </summary>
        public void BuildUI()
        {
            var canvas = EnsureCanvas();
            EnsureEventSystem();

            // Remove a previously generated root so regeneration is idempotent.
            var existing = canvas.transform.Find(RootName);
            if (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject);
                else DestroyImmediate(existing.gameObject);
            }

            uiRoot = UIFactory.CreateRect(RootName, canvas.transform);
            UIFactory.Stretch(uiRoot);
            uiRoot.gameObject.AddComponent<UISafeArea>();

            mapUiBuilt = false;

            DeactivateLegacyUI(canvas);

            BuildTopStatusArea(uiRoot);
            BuildModeCaption(uiRoot);
            BuildRecordButton(uiRoot);
            BuildReviewControls(uiRoot);
            BuildMapSwitcher(uiRoot);
            BuildScreenSwitchButton(uiRoot);

            ApplyMainUiVisibility();
        }

        /// <summary>Mode hint shown just above the record button (e.g. "Localized — tap Record", "Recording").</summary>
        private void BuildModeCaption(RectTransform root)
        {
            // statusText is referenced by tooling/map feedback but hidden from the minimal record UI.
            var hidden = UIFactory.CreateText("StatusText", root, string.Empty, UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Center);
            statusText = hidden;
            statusText.gameObject.SetActive(false);

            modeText = UIFactory.CreateText("ModeCaption", root, "Initializing…", UITokens.FontCaption, UITokens.OnSurface, TextAlignmentOptions.Center);
            var rect = modeText.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(320f, 24f);
            rect.anchoredPosition = new Vector2(0f, RecordButtonDiameter + UITokens.Space12 + UITokens.Space8);
        }

        /// <summary>Single large record button, bottom-center (playback-style glass circle).</summary>
        private void BuildRecordButton(RectTransform root)
        {
            recordButton = UIFactory.CreateCircleButton("RecordButton", root, RecordButtonDiameter, UITokens.PlaybackTransportBtn);
            var rect = (RectTransform)recordButton.transform;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, UITokens.Space12);
            recordIcon = UIFactory.AddRecordIcon(recordButton.transform, 22f, UITokens.Danger, square: false);
        }

        /// <summary>
        /// Bottom-center review cluster shown after a recording stops: replay the skeleton, then Discard or
        /// Confirm. Confirm sends the clip to Move AI; Discard deletes it. Hidden until a review is pending.
        /// </summary>
        private void BuildReviewControls(RectTransform root)
        {
            var clusterRect = UIFactory.CreateRect("ReviewControls", root);
            clusterRect.anchorMin = new Vector2(0.5f, 0f);
            clusterRect.anchorMax = new Vector2(0.5f, 0f);
            clusterRect.pivot = new Vector2(0.5f, 0f);
            clusterRect.anchoredPosition = new Vector2(0f, UITokens.Space12);
            clusterRect.sizeDelta = new Vector2(320f, RecordButtonDiameter);
            reviewRoot = clusterRect.gameObject;

            var layout = clusterRect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UITokens.Space12;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            // Discard (danger).
            rejectButton = UIFactory.CreateCircleButton("RejectButton", clusterRect, 52f, UITokens.Danger);
            SetReviewButtonSize(rejectButton.gameObject, 52f);
            UIFactory.AddStopIcon(rejectButton.transform, 16f, Color.white);

            // Replay / pause (glass, teal-style like the playback transport).
            reviewReplayButton = UIFactory.CreateCircleButton("ReviewReplayButton", clusterRect, RecordButtonDiameter, UITokens.PlaybackTransportPlay);
            SetReviewButtonSize(reviewReplayButton.gameObject, RecordButtonDiameter);
            reviewReplayIcon = UIFactory.AddPlayIcon(reviewReplayButton.transform, 26f, Color.white).gameObject;
            reviewPauseIcon = UIFactory.AddPauseIcon(reviewReplayButton.transform, 22f, Color.white);
            reviewPauseIcon.SetActive(false);

            // Confirm (primary accent).
            confirmButton = UIFactory.CreateCircleButton("ConfirmButton", clusterRect, 52f, UITokens.Success);
            SetReviewButtonSize(confirmButton.gameObject, 52f);
            UIFactory.AddCheckmarkIcon(confirmButton.transform, 22f, Color.white);

            // Caption above the cluster.
            reviewCaption = UIFactory.CreateText("ReviewCaption", root, "Review the skeleton — keep or discard?",
                UITokens.FontCaption, UITokens.OnSurface, TextAlignmentOptions.Center);
            var capRect = reviewCaption.rectTransform;
            capRect.anchorMin = new Vector2(0.5f, 0f);
            capRect.anchorMax = new Vector2(0.5f, 0f);
            capRect.pivot = new Vector2(0.5f, 0f);
            capRect.sizeDelta = new Vector2(340f, 24f);
            capRect.anchoredPosition = new Vector2(0f, RecordButtonDiameter + UITokens.Space12 + UITokens.Space8);

            reviewRoot.SetActive(false);
            reviewCaption.gameObject.SetActive(false);
        }

        private static void SetReviewButtonSize(GameObject go, float size)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minWidth = size; le.minHeight = size;
            le.preferredWidth = size; le.preferredHeight = size;
        }

        /// <summary>Round top-left button that switches to the dedicated playback screen (play icon).</summary>
        private void BuildScreenSwitchButton(RectTransform root)
        {
            goToPlaybackButton = UIFactory.CreateCircleButton(
                "GoToPlaybackButton", root, UITokens.ToolbarIconDiameter, UITokens.PlaybackTransportBtn);
            UIFactory.PlaceTopLeftToolbarButton(
                (RectTransform)goToPlaybackButton.transform, 0, UITokens.ToolbarIconDiameter, TopLeftToolbarInset);
            UIFactory.AddPlayIcon(goToPlaybackButton.transform, UITokens.ToolbarIconDiameter * 0.34f, UITokens.OnSurface);
        }

        /// <summary>Wire the Playback pill to a screen switcher (called by <see cref="BodyTrackingUISwitcher"/>).</summary>
        public void WireScreenSwitcher(BodyTrackingUISwitcher switcher)
        {
            if (goToPlaybackButton == null || switcher == null)
                return;
            goToPlaybackButton.onClick.RemoveListener(switcher.ShowPlayback);
            goToPlaybackButton.onClick.AddListener(switcher.ShowPlayback);
        }

        /// <summary>
        /// Hide the previous (hand-built) UI under the Canvas so the regenerated interface replaces it,
        /// without destroying anything. Skips the freshly created root and never disables this component's
        /// own GameObject or any of its ancestors (so the script keeps running). Reversible: re-enable the
        /// objects in the Hierarchy if the old UI is ever needed again.
        /// </summary>
        private void DeactivateLegacyUI(Canvas canvas)
        {
            var myT = transform;
            for (int i = canvas.transform.childCount - 1; i >= 0; i--)
            {
                var child = canvas.transform.GetChild(i);
                if (child == uiRoot) continue;
                if (child == myT) continue;
                if (myT.IsChildOf(child)) continue; // never disable an ancestor of this component
                child.gameObject.SetActive(false);
            }
        }

        private Canvas EnsureCanvas()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null)
                canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();

            if (canvas == null)
            {
                var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                // Points-based reference (≈iPhone logical points). Using the device PIXEL resolution here
                // makes the scale factor ~1 on Retina screens, rendering all UI at tiny raw point sizes.
                scaler.referenceResolution = new Vector2(390f, 844f);
                scaler.matchWidthOrHeight = 0.5f;
            }
            else
            {
                var scaler = canvas.GetComponent<CanvasScaler>();
                if (scaler == null)
                    scaler = canvas.gameObject.AddComponent<CanvasScaler>();

                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(390f, 844f);
                scaler.matchWidthOrHeight = 0.5f;
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

                if (canvas.GetComponent<GraphicRaycaster>() == null)
                    canvas.gameObject.AddComponent<GraphicRaycaster>();
            }

            return canvas;
        }

        private void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() == null)
                new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }

        private void BuildTopStatusArea(RectTransform root)
        {
            var area = UIFactory.CreateRect("TopStatusArea", root);
            area.anchorMin = new Vector2(0f, 1f);
            area.anchorMax = new Vector2(1f, 1f);
            area.pivot = new Vector2(0.5f, 1f);
            area.anchoredPosition = new Vector2(0f, -TopBarInset);
            float leftReserve = UIFactory.TopLeftToolbarWidth(1, UITokens.ToolbarIconDiameter, TopLeftToolbarInset);
            area.offsetMin = new Vector2(leftReserve, area.offsetMin.y);
            area.offsetMax = new Vector2(-TopBarInset, area.offsetMax.y);

            var layout = area.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.spacing = UITokens.Space4;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var areaFitter = area.gameObject.AddComponent<ContentSizeFitter>();
            areaFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            areaFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var areaLayout = area.gameObject.AddComponent<LayoutElement>();
            areaLayout.preferredHeight = TopBarPillHeight;
            areaLayout.minHeight = TopBarPillHeight;

            BuildTopPillsRow(area);
        }

        private void BuildTopPillsRow(RectTransform parent)
        {
            var align = UIFactory.CreateStatusPill("Pill_Align", parent, "Align", autoSize: false,
                height: TopBarPillHeight, fontSize: TopBarPillFont, dotSize: TopBarDotSize);
            alignDot = align.dot; alignLabel = align.label;
            var alignBg = align.root.GetComponent<Image>();
            if (alignBg != null) alignBg.raycastTarget = true;
            alignButton = align.root.gameObject.AddComponent<Button>();
            alignButton.targetGraphic = alignBg;
            alignButton.transition = Selectable.Transition.None;
            alignButton.onClick.AddListener(OnAlignDotClicked);
            SetPillFlex(align.root, 1f);

            var pill = UIFactory.CreateStatusPill("MoveStatusPill", parent, "Move AI · idle", autoSize: false,
                height: TopBarPillHeight, fontSize: TopBarPillFont, dotSize: TopBarDotSize);
            moveStatusPill = pill.root.gameObject;
            moveDot = pill.dot;
            moveLabel = pill.label;
            if (moveLabel != null)
            {
                moveLabel.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                moveLabel.overflowMode = TextOverflowModes.Ellipsis;
            }
            SetPillFlex(pill.root, 1.4f);

            moveCountBadge = UIFactory.CreateText("MoveCountBadge", pill.root, "", TopBarPillFont - 1f,
                UITokens.Muted, TextAlignmentOptions.Right);
            var badgeLayout = moveCountBadge.gameObject.AddComponent<LayoutElement>();
            badgeLayout.flexibleWidth = 0f;
            badgeLayout.minWidth = 28f;
            badgeLayout.preferredWidth = 36f;

            var pillBg = pill.root.GetComponent<Image>();
            if (pillBg != null) pillBg.raycastTarget = true;
            moveStatusButton = pill.root.gameObject.AddComponent<Button>();
            moveStatusButton.targetGraphic = pillBg;
            moveStatusButton.transition = Selectable.Transition.None;
            moveStatusButton.onClick.AddListener(ToggleMoveQueuePanel);

            BuildMoveQueuePanel();
        }

        /// <summary>
        /// Dropdown panel (hidden until the Move AI pill is tapped) listing every job in the upload queue —
        /// recording name, enqueue date/time and live state — each with a small "x" to remove it from the queue.
        /// </summary>
        private void BuildMoveQueuePanel()
        {
            // Anchored to the top, overlaying the screen below the status area (not part of the top layout flow).
            var panelRect = UIFactory.CreateRect("MoveQueuePanel", uiRoot);
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -(TopBarPillHeight + TopBarInset + UITokens.Space8));
            panelRect.offsetMin = new Vector2(TopBarInset, panelRect.offsetMin.y);
            panelRect.offsetMax = new Vector2(-TopBarInset, panelRect.offsetMax.y);

            var bg = panelRect.gameObject.AddComponent<Image>();
            bg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusMedium);
            bg.type = Image.Type.Sliced;
            bg.color = UITokens.SurfaceElevated;
            bg.raycastTarget = true;

            var vlayout = panelRect.gameObject.AddComponent<VerticalLayoutGroup>();
            vlayout.padding = new RectOffset((int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8);
            vlayout.spacing = UITokens.Space4;
            vlayout.childAlignment = TextAnchor.UpperCenter;
            vlayout.childControlWidth = true;
            vlayout.childControlHeight = true;
            vlayout.childForceExpandWidth = true;
            vlayout.childForceExpandHeight = false;

            var fitter = panelRect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = UIFactory.CreateText("MoveQueueHeader", panelRect, "Move AI queue", UITokens.FontCaption,
                UITokens.OnSurface, TextAlignmentOptions.Left);
            var headerLayout = header.gameObject.AddComponent<LayoutElement>();
            headerLayout.minHeight = 20f;
            headerLayout.preferredHeight = 20f;

            var listRect = UIFactory.CreateRect("MoveQueueList", panelRect);
            var listLayout = listRect.gameObject.AddComponent<VerticalLayoutGroup>();
            listLayout.spacing = UITokens.Space4;
            listLayout.childControlWidth = true;
            listLayout.childControlHeight = true;
            listLayout.childForceExpandWidth = true;
            listLayout.childForceExpandHeight = false;
            var listFitter = listRect.gameObject.AddComponent<ContentSizeFitter>();
            listFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            listFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            moveQueueListContent = listRect;

            moveQueueEmptyLabel = UIFactory.CreateText("MoveQueueEmpty", panelRect, "Nothing queued — record to send a climb.",
                UITokens.FontCaption - 2f, UITokens.Muted, TextAlignmentOptions.Left);
            var emptyLayout = moveQueueEmptyLabel.gameObject.AddComponent<LayoutElement>();
            emptyLayout.minHeight = 18f;
            emptyLayout.preferredHeight = 18f;

            moveQueuePanel = panelRect.gameObject;
            moveQueuePanel.SetActive(false);
        }

        private void ToggleMoveQueuePanel()
        {
            moveQueuePanelVisible = !moveQueuePanelVisible;
            if (moveQueuePanel != null)
            {
                moveQueuePanel.SetActive(moveQueuePanelVisible);
                if (moveQueuePanelVisible)
                    moveQueuePanel.transform.SetAsLastSibling(); // overlay above the rest of the screen
            }
            if (moveQueuePanelVisible)
                RebuildMoveQueueList();
        }

        private MoveAIFusionCoordinator MoveCoordinator =>
            controller != null ? controller.fusionCoordinator : null;

        /// <summary>Rebuild the queue rows from the coordinator snapshot (recording, date/time, state, remove "x").</summary>
        private void RebuildMoveQueueList()
        {
            if (moveQueueListContent == null)
                return;

            for (int i = moveQueueListContent.childCount - 1; i >= 0; i--)
                Destroy(moveQueueListContent.GetChild(i).gameObject);

            var coordinator = MoveCoordinator;
            string mapId = controller != null ? controller.GetActiveMapId() : "";
            var items = coordinator != null
                ? coordinator.GetQueueSnapshot(mapId)
                : new System.Collections.Generic.List<MoveAIFusionCoordinator.MoveQueueItem>();

            if (moveQueueEmptyLabel != null)
                moveQueueEmptyLabel.gameObject.SetActive(items.Count == 0);

            foreach (var item in items)
                BuildMoveQueueRow(item);
        }

        private void BuildMoveQueueRow(MoveAIFusionCoordinator.MoveQueueItem item)
        {
            var row = UIFactory.CreateRect("Row_" + item.recordingFileName, moveQueueListContent);
            var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = UITokens.Space8;
            rowLayout.childAlignment = TextAnchor.MiddleLeft;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = false;
            var rowLe = row.gameObject.AddComponent<LayoutElement>();
            rowLe.minHeight = 38f;
            rowLe.preferredHeight = 38f;

            var rowBg = row.gameObject.AddComponent<Image>();
            rowBg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusSmall);
            rowBg.type = Image.Type.Sliced;
            rowBg.color = UITokens.Surface;
            rowBg.raycastTarget = false;

            // State dot.
            var dotRect = UIFactory.CreateRect("Dot", row);
            var dotImg = dotRect.gameObject.AddComponent<Image>();
            dotImg.sprite = UIFactory.CircleSprite();
            dotImg.color = QueueStateColor(item.state);
            dotImg.raycastTarget = false;
            var dotLe = dotRect.gameObject.AddComponent<LayoutElement>();
            dotLe.minWidth = 9f; dotLe.minHeight = 9f;
            dotLe.preferredWidth = 9f; dotLe.preferredHeight = 9f;
            dotLe.flexibleWidth = 0f;

            // Name + date/time + state (two short lines).
            string when = FormatEnqueuedTime(item.enqueuedUtc);
            string stateText = QueueStateText(item.state, item.error, item.progressPercent);
            var text = UIFactory.CreateText("Info", row,
                $"{item.recordingFileName}\n{when} · {stateText}",
                UITokens.FontCaption - 3f, UITokens.OnSurface, TextAlignmentOptions.Left);
            text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Ellipsis;
            var textLe = text.gameObject.AddComponent<LayoutElement>();
            textLe.flexibleWidth = 1f;

            // Remove ("x") button.
            var removeBtn = UIFactory.CreateCircleButton("Remove", row, 26f, UITokens.Danger);
            var removeLe = removeBtn.gameObject.AddComponent<LayoutElement>();
            removeLe.minWidth = 26f; removeLe.minHeight = 26f;
            removeLe.preferredWidth = 26f; removeLe.preferredHeight = 26f;
            removeLe.flexibleWidth = 0f;
            AddCrossIcon(removeBtn.transform, 11f, Color.white);
            string fileName = item.recordingFileName;
            removeBtn.onClick.AddListener(() => OnRemoveFromQueueClicked(fileName));
        }

        private void OnRemoveFromQueueClicked(string recordingFileName)
        {
            var coordinator = MoveCoordinator;
            if (coordinator != null)
                coordinator.RemoveFromQueue(recordingFileName);
            RebuildMoveQueueList();
            UpdateMoveStatusLine();
        }

        /// <summary>Two crossed strokes forming an "x" (remove glyph), built from primitives so it always renders.</summary>
        private static void AddCrossIcon(Transform parent, float size, Color color)
        {
            for (int i = 0; i < 2; i++)
            {
                var bar = UIFactory.CreateRect(i == 0 ? "CrossA" : "CrossB", parent);
                bar.anchorMin = new Vector2(0.5f, 0.5f);
                bar.anchorMax = new Vector2(0.5f, 0.5f);
                bar.pivot = new Vector2(0.5f, 0.5f);
                bar.sizeDelta = new Vector2(Mathf.Max(2f, size * 0.18f), size);
                bar.anchoredPosition = Vector2.zero;
                bar.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
                var img = bar.gameObject.AddComponent<Image>();
                img.sprite = UIFactory.RoundedSprite(2);
                img.type = Image.Type.Sliced;
                img.color = color;
                img.raycastTarget = false;
            }
        }

        private static string FormatEnqueuedTime(string enqueuedUtc)
        {
            if (string.IsNullOrEmpty(enqueuedUtc))
                return "—";
            if (System.DateTime.TryParse(enqueuedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
                return utc.ToLocalTime().ToString("MMM d, HH:mm");
            return enqueuedUtc;
        }

        private static string QueueStateText(MoveQueueState state, string error, float progressPercent = -1f)
        {
            string label;
            switch (state)
            {
                case MoveQueueState.Queued: return "queued";
                case MoveQueueState.Uploading: label = "uploading"; break;
                case MoveQueueState.Processing: label = "processing"; break;
                case MoveQueueState.Downloading: label = "downloading"; break;
                case MoveQueueState.Baking: label = "baking"; break;
                case MoveQueueState.Done: return "done";
                case MoveQueueState.Failed: return string.IsNullOrEmpty(error) ? "failed" : "failed: " + error;
                default: return state.ToString().ToLowerInvariant();
            }

            if (progressPercent >= 0f && progressPercent <= 100f)
                return $"{label} · {progressPercent:F0}%";
            return label;
        }

        private static Color QueueStateColor(MoveQueueState state)
        {
            switch (state)
            {
                case MoveQueueState.Done: return UITokens.Success;
                case MoveQueueState.Failed: return UITokens.Danger;
                case MoveQueueState.Queued: return UITokens.Muted;
                default: return UITokens.Primary; // uploading / processing / downloading / baking
            }
        }

        private void BuildTopStatusBar(RectTransform root)
        {
            // Legacy entry point — replaced by BuildTopStatusArea.
            BuildTopStatusArea(root);
        }

        private static void SetPillFlex(RectTransform pill, float flex)
        {
            var le = pill.GetComponent<LayoutElement>();
            if (le == null) le = pill.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = flex;
            le.minWidth = 0f;
            le.preferredWidth = 0f;
        }

        /// <summary>
        /// Top-left control to enter a new Immersal map id at runtime (device builds). Downloads map
        /// data and the sparse visualization, then updates localization for the whole session.
        /// </summary>
        private bool mapUiBuilt;

        /// <summary>
        /// Find or create <see cref="ImmersalMapSwitcher"/>. BodyTrackingController creates
        /// <see cref="RouteRootManager"/> in Start(), which can run after our first BuildUI() — so we
        /// also attach to the controller host when needed and retry UI creation from Update().
        /// </summary>
        bool EnsureMapSwitcherComponent()
        {
            if (mapSwitcher != null)
                return true;

            mapSwitcher = UnityEngine.Object.FindFirstObjectByType<ImmersalMapSwitcher>();
            if (mapSwitcher != null)
                return true;

            if (controller == null)
                controller = UnityEngine.Object.FindFirstObjectByType<BodyTrackingController>();

            RouteRootManager rrm = controller != null ? controller.routeRootManager : null;
            if (rrm == null && controller != null)
                rrm = controller.GetComponent<RouteRootManager>();
            if (rrm == null)
                rrm = UnityEngine.Object.FindFirstObjectByType<RouteRootManager>();

            GameObject host = rrm != null ? rrm.gameObject : controller != null ? controller.gameObject : null;
            if (host == null)
                return false;

            mapSwitcher = host.GetComponent<ImmersalMapSwitcher>();
            if (mapSwitcher == null)
                mapSwitcher = host.AddComponent<ImmersalMapSwitcher>();
            return mapSwitcher != null;
        }

        void TryBuildMapSwitcherLate()
        {
            if (mapUiBuilt || uiRoot == null)
                return;
            if (!EnsureMapSwitcherComponent())
                return;

            BuildMapSwitcher(uiRoot);
        }

        private void BuildMapSwitcher(RectTransform root)
        {
            if (!EnsureMapSwitcherComponent())
            {
                Debug.LogWarning("[BodyTrackingUI] Map switcher unavailable — map controls not created yet (will retry).");
                return;
            }

            // Map id switching now lives in the dedicated Recordings menu (RecordingsMenuUI). We still listen for
            // map switches here so the record screen reloads the latest recording + refreshes its list, but the
            // map id pill/panel is no longer built on this screen.
            mapSwitcher.OnStatusChanged -= OnMapSwitcherStatusChanged;
            mapSwitcher.OnMapSwitched -= OnMapSwitched;
            mapSwitcher.OnStatusChanged += OnMapSwitcherStatusChanged;
            mapSwitcher.OnMapSwitched += OnMapSwitched;

            // Retarget/re-align is now driven by the tappable "Align" status pill in the top bar
            // (see BuildTopPillsRow / UpdateAlignPill). Nothing else to build here.
            mapUiBuilt = true;
        }

        private void ToggleMapPanel()
        {
            mapPanelVisible = !mapPanelVisible;
            if (mapPanel != null)
                mapPanel.SetActive(mapPanelVisible);
        }

        private void OnMapLoadClicked()
        {
            if (mapSwitcher == null || mapIdInput == null)
            {
                ShowMapFeedback("Map switcher not ready", error: true);
                return;
            }

            // Keyboard focus pauses AR on iOS; dismiss before starting a network-heavy map load.
            mapIdInput.DeactivateInputField();
            if (TouchScreenKeyboard.isSupported)
                TouchScreenKeyboard.hideInput = true;

            string idText = mapIdInput.text?.Trim();
            if (string.IsNullOrEmpty(idText))
            {
                ShowMapFeedback("Enter a numeric map ID first", error: true);
                return;
            }

            if (controller != null && (controller.IsRecording || controller.IsPlaying))
            {
                ShowMapFeedback("Stop recording/playback before switching maps", error: true);
                return;
            }

            mapPanelVisible = true;
            if (mapPanel != null)
                mapPanel.SetActive(true);

            ShowMapFeedback($"Loading map {idText}…", error: false);
            mapSwitcher.SwitchToMapFromInput(idText);
            UpdateMapControls();
        }

        private void ShowMapFeedback(string message, bool error)
        {
            if (mapStatusLabel != null)
            {
                mapStatusLabel.text = message;
                mapStatusLabel.color = error ? UITokens.Danger : UITokens.OnSurface;
            }

            if (statusText != null)
                statusText.text = message;
        }

        private void SyncMapInputFromSwitcher()
        {
            if (mapIdInput == null || mapSwitcher == null)
                return;

            int id = mapSwitcher.ActiveMapId > 0
                ? mapSwitcher.ActiveMapId
                : (mapSwitcher.PendingMapId > 0 ? mapSwitcher.PendingMapId : -1);
            if (id > 0)
                mapIdInput.text = id.ToString();
        }

        private void OnMapSwitcherStatusChanged(string message)
        {
            if (mapStatusLabel != null)
            {
                mapStatusLabel.text = message;
                switch (mapSwitcher != null ? mapSwitcher.LastStatusSeverity : ImmersalMapSwitcher.StatusSeverity.Info)
                {
                    case ImmersalMapSwitcher.StatusSeverity.Error:
                        mapStatusLabel.color = UITokens.Danger;
                        break;
                    case ImmersalMapSwitcher.StatusSeverity.Success:
                        mapStatusLabel.color = UITokens.Success;
                        break;
                    case ImmersalMapSwitcher.StatusSeverity.Working:
                        mapStatusLabel.color = UITokens.OnSurface;
                        break;
                    default:
                        mapStatusLabel.color = UITokens.Muted;
                        break;
                }
            }

            // Mirror map progress on the main status line so feedback is visible even when the panel is closed.
            if (statusText != null && mapSwitcher != null &&
                mapSwitcher.LastStatusSeverity != ImmersalMapSwitcher.StatusSeverity.Info)
                statusText.text = message;

            UpdateMapToggleLabel();
            UpdateMapControls();
        }

        private void OnMapSwitched(int mapId)
        {
            if (mapIdInput != null)
                mapIdInput.text = mapId.ToString();

            UpdateMapToggleLabel();
            if (controller != null)
                controller.LoadLatestRecording();
            RefreshRecordingsList();
            if (moveQueuePanelVisible)
                RebuildMoveQueueList();
            UpdateMoveStatusLine();
            UpdateUI();
        }

        private string GetDisplayedMapId()
        {
            if (mapSwitcher != null && mapSwitcher.ActiveMapId > 0)
                return mapSwitcher.ActiveMapId.ToString();

            string fromController = controller != null ? controller.GetActiveMapId() : "";
            if (!string.IsNullOrWhiteSpace(fromController))
                return fromController.Trim();

            return "—";
        }

        private void UpdateMapToggleLabel()
        {
            if (mapToggleLabel == null)
                return;

            string id = GetDisplayedMapId();
            bool switching = mapSwitcher != null && mapSwitcher.IsSwitching;
            mapToggleLabel.text = switching ? $"Map {id}…" : $"Map {id}";
        }

        private void UpdateMapControls()
        {
            if (mapSwitcher == null)
                return;

            bool switching = mapSwitcher.IsSwitching;
            int pending = mapSwitcher.PendingMapId;

            // Always allow Load — requests queue instead of being dropped silently.
            if (mapLoadButton != null)
                mapLoadButton.interactable = true;

            if (mapLoadButtonLabel != null)
            {
                if (switching && pending > 0 && pending != mapSwitcher.ActiveMapId)
                    mapLoadButtonLabel.text = $"Queue {pending}";
                else if (switching)
                    mapLoadButtonLabel.text = "Loading…";
                else
                    mapLoadButtonLabel.text = "Load map";
            }

            UpdateMapToggleLabel();
        }

        // ============================================================================================
        // EVENT WIRING (preserves all original functional hooks)
        // ============================================================================================

        private void HookUpEvents()
        {
            if (recordButton != null) recordButton.onClick.AddListener(OnRecordToggleClicked);
            if (playButton != null) playButton.onClick.AddListener(OnPlayToggleClicked);
            if (stopPlayButton != null) stopPlayButton.onClick.AddListener(OnStopClicked);
            if (loadButton != null) loadButton.onClick.AddListener(OnLoadClicked);

            if (reviewReplayButton != null) reviewReplayButton.onClick.AddListener(OnReviewReplayClicked);
            if (rejectButton != null) rejectButton.onClick.AddListener(OnRejectClicked);
            if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmClicked);
        }

        private void OnReviewReplayClicked()
        {
            if (controller == null) return;
            // Toggle the review skeleton replay: start if idle, otherwise pause / resume.
            if (!controller.IsPlaying)
                controller.StartReviewPlayback();
            else if (controller.IsPaused)
                controller.ResumePlayback();
            else
                controller.PausePlayback();
            UpdateUI();
        }

        private void OnRejectClicked()
        {
            if (controller == null) return;
            controller.RejectRecording();
            RefreshRecordingsList();
            UpdateUI();
        }

        private void OnConfirmClicked()
        {
            if (controller == null) return;
            controller.ConfirmRecording();
            RefreshRecordingsList();
            UpdateUI();
        }

        private void OnReviewStarted() => UpdateUI();
        private void OnReviewResolved() => UpdateUI();

        private void OnAlignDotClicked()
        {
            if (controller == null) return;
            // Full re-scan: clear the frozen anchor and re-localize from scratch.
            controller.RetargetAndRealignImmersal();
            UpdateUI();
        }

        // ============================================================================================
        // STATE / UPDATE
        // ============================================================================================

        /// <summary>Show/hide and refresh the post-recording review cluster (Discard / Replay / Confirm).</summary>
        private void UpdateReviewControls(bool awaitingReview)
        {
            if (reviewRoot != null && reviewRoot.activeSelf != awaitingReview)
                reviewRoot.SetActive(awaitingReview);
            if (reviewCaption != null && reviewCaption.gameObject.activeSelf != awaitingReview)
                reviewCaption.gameObject.SetActive(awaitingReview);

            if (!awaitingReview)
                return;

            // Replay/pause icon mirrors the actual playback state of the review clip.
            bool showPause = controller.IsPlaying && !controller.IsPaused;
            if (reviewReplayIcon != null) reviewReplayIcon.SetActive(!showPause);
            if (reviewPauseIcon != null) reviewPauseIcon.SetActive(showPause);
        }

        private void UpdateUI()
        {
            if (controller == null) return;

            UpdateStatusPills();
            UpdateMoveStatusLine();

            if (modeText != null)
            {
                modeText.gameObject.SetActive(!controller.IsAwaitingReview);
                modeText.text = GetModeText();
                modeText.color = GetModeColor();
            }

            bool canRecord = controller.CanRecord;
            bool canPlayback = controller.CanPlayback;
            bool isRecording = controller.IsRecording;
            bool isPlaying = controller.IsPlaying;
            bool isWaitingForBody = controller.IsWaitingForBody;
            bool awaitingReview = controller.IsAwaitingReview;

            UpdateReviewControls(awaitingReview);

            // Record button: record-dot when idle, red stop-square while recording or waiting-to-arm (tap to cancel).
            // Hidden during review (the review cluster takes its place).
            if (recordButton != null)
            {
                recordButton.gameObject.SetActive(!awaitingReview);
                recordButton.interactable = canRecord || isRecording || isWaitingForBody;
                if (recordIcon != null)
                {
                    if (isRecording || isWaitingForBody)
                    {
                        recordIcon.sprite = UIFactory.RoundedSprite(4);
                        recordIcon.type = Image.Type.Sliced;
                        recordIcon.rectTransform.sizeDelta = new Vector2(14f, 14f);
                    }
                    else
                    {
                        recordIcon.sprite = UIFactory.CircleSprite();
                        recordIcon.type = Image.Type.Simple;
                        recordIcon.rectTransform.sizeDelta = new Vector2(18f, 18f);
                    }
                    recordIcon.color = UITokens.Danger;
                }
            }

            // Play button: pause bars while actively playing; play triangle when idle or paused.
            bool isPaused = controller.IsPaused;
            bool showPause = isPlaying && !isPaused;
            if (playButton != null)
            {
                playButton.interactable = canPlayback || isPlaying;
                if (playIcon != null) playIcon.SetActive(!showPause);
                if (pauseIcon != null) pauseIcon.SetActive(showPause);
            }

            if (stopPlayButton != null)
                stopPlayButton.interactable = isPlaying || isRecording;

            // Recording selector availability.
            bool selectorEnabled = availableRecordings.Count > 0 && !isRecording && !isPlaying;
            if (prevRecordingButton != null) prevRecordingButton.interactable = selectorEnabled && availableRecordings.Count > 1;
            if (nextRecordingButton != null) nextRecordingButton.interactable = selectorEnabled && availableRecordings.Count > 1;
            if (loadButton != null) loadButton.interactable = selectorEnabled;

            if (scrubSlider != null)
                scrubSlider.interactable = isPlaying;

            UpdateMapControls();
        }

        private void UpdateStatusPills()
        {
            if (controller == null) return;

            // AR / body-tracking, localization and joint-count pills were removed from the top bar; only the
            // alignment / drift pill remains (the rest surfaces through the mode caption above the record button).
            UpdateAlignPill();
        }

        /// <summary>
        /// Drift/alignment status pill. Reports the passive drift estimate from Immersal (how far the locked
        /// anchor looks off) and acts as a tap target for a full re-scan. Hidden when Immersal isn't available.
        /// </summary>
        private void UpdateAlignPill()
        {
            if (alignButton == null) return;

            var immersal = controller != null && controller.routeRootManager != null
                ? controller.routeRootManager.ImmersalProvider
                : null;
            bool available = immersal != null && immersal.IsAvailable;
            if (alignButton.gameObject.activeSelf != available)
                alignButton.gameObject.SetActive(available);
            if (!available)
                return;

            bool switching = mapSwitcher != null && mapSwitcher.IsSwitching;
            alignButton.interactable = !controller.IsRecording && !controller.IsPlaying && !switching;

            string label;
            Color dot;

            if (switching)
            {
                label = "Loading…"; dot = UITokens.Primary;
            }
            else if (!immersal.IsAnchorFrozen)
            {
                label = "Scanning…"; dot = UITokens.Warning;
            }
            else if (immersal.DriftCheckInProgress && !immersal.HasDriftEstimate)
            {
                label = "Checking…"; dot = UITokens.Primary;
            }
            else if (immersal.HasDriftEstimate)
            {
                float cm = Mathf.Max(0f, immersal.LastDriftMeters) * 100f;
                float deg = Mathf.Max(0f, immersal.LastDriftDegrees);
                dot = DriftColor(immersal.LastDriftMeters, immersal.LastDriftDegrees);
                if (cm < 1.5f && deg < 2f)
                    label = "Aligned";
                else
                    label = deg >= 4f ? $"Off ~{cm:F0}cm/{deg:F0}\u00B0" : $"Off ~{cm:F0}cm";
            }
            else
            {
                label = "Aligned"; dot = UITokens.Success;
            }

            if (alignLabel != null) alignLabel.text = label;
            if (alignDot != null) alignDot.color = dot;
        }

        /// <summary>Green within a tight band, amber for a noticeable but usable offset, red beyond that.</summary>
        private static Color DriftColor(float meters, float degrees)
        {
            if (meters < 0.03f && degrees < 3f) return UITokens.Success;
            if (meters < 0.10f && degrees < 8f) return UITokens.Warning;
            return UITokens.Danger;
        }

        private void UpdateMoveStatusLine()
        {
            if (moveStatusPill == null || moveLabel == null)
                return;

            if (controller == null || !controller.MoveAIEnabled)
            {
                moveStatusPill.SetActive(false);
                if (moveQueuePanel != null && moveQueuePanelVisible)
                {
                    moveQueuePanelVisible = false;
                    moveQueuePanel.SetActive(false);
                }
                return;
            }

            moveStatusPill.SetActive(true);
            string text = GetMoveStatusText();
            moveLabel.text = text;
            if (moveDot != null)
                moveDot.color = GetMoveStatusColor(text);

            // Count badge: number of jobs still pending/in flight, plus a chevron hint that it opens a list.
            if (moveCountBadge != null)
            {
                string mapId = controller != null ? controller.GetActiveMapId() : "";
                int pending = MoveCoordinator != null ? MoveCoordinator.PendingCountForMap(mapId) : 0;
                moveCountBadge.text = pending > 0 ? $"{pending}  >" : ">";
                moveCountBadge.color = pending > 0 ? UITokens.Primary : UITokens.Muted;
            }
        }

        private string GetMoveStatusText()
        {
            if (controller == null || !controller.MoveAIEnabled)
                return "Move AI · off";

            if (!string.IsNullOrEmpty(controller.FusionStatusMessage))
                return "Move · " + controller.FusionStatusMessage;

            if (controller.LastRecordingHasFusionAsset)
                return "Move · fused replay ready";

            if (controller.IsRecording)
                return "Move · recording (processes after stop)";

            return "Move · idle — record to process";
        }

        private static Color GetMoveStatusColor(string text)
        {
            if (string.IsNullOrEmpty(text))
                return UITokens.Muted;

            var lower = text.ToLowerInvariant();
            if (lower.Contains("ready") || lower.Contains("saved to photos") || lower.Contains("100%"))
                return UITokens.Success;
            if (lower.Contains("fail") || lower.Contains("skipped") || lower.Contains("error"))
                return UITokens.Warning;
            if (lower.Contains("submitting") || lower.Contains("move ai:") ||
                lower.Contains("parsing") || lower.Contains("baking") ||
                lower.Contains("encoding") || lower.Contains("preparing") ||
                lower.Contains("recording"))
                return UITokens.Primary;
            return UITokens.Muted;
        }

        private void OnFusionStatusChanged(string message)
        {
            UpdateMoveStatusLine();
            if (moveQueuePanelVisible)
                RebuildMoveQueueList();
        }

        private void UpdateTransportTime()
        {
            if (controller == null) return;

            float current = 0f;
            float total = 0f;
            float progress = 0f;

            if (controller.IsRecording && controller.recorder != null)
            {
                current = controller.recorder.RecordingDuration;
                total = -1f; // unknown while recording
            }
            else if (controller.IsPlaying && controller.IsFusedPlaying)
            {
                current = controller.FusedCurrentTime;
                total = controller.FusedDuration;
                progress = total > 0f ? Mathf.Clamp01(current / total) : 0f;
            }
            else if (controller.IsPlaying && controller.player != null)
            {
                current = controller.player.CurrentTime;
                total = controller.player.Duration;
                progress = controller.player.PlaybackProgress;
            }
            else
            {
                total = GetSelectedRecordingDuration();
            }

            if (currentTimeLabel != null) currentTimeLabel.text = FormatTime(current);
            if (totalTimeLabel != null) totalTimeLabel.text = total < 0f ? "--:--" : FormatTime(total);

            if (scrubSlider != null && !controller.IsPlaying)
            {
                suppressScrubCallback = true;
                scrubSlider.SetValueWithoutNotify(0f);
                suppressScrubCallback = false;
            }
            else if (scrubSlider != null)
            {
                suppressScrubCallback = true;
                scrubSlider.SetValueWithoutNotify(progress);
                suppressScrubCallback = false;
            }
        }

        private float GetSelectedRecordingDuration()
        {
            return selectedRecordingDuration;
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds)) seconds = 0f;
            int total = Mathf.FloorToInt(seconds);
            return $"{total / 60:00}:{total % 60:00}";
        }

        private string GetLocalizationHint()
        {
            if (controller == null) return "Not localized";
            if (controller.IsAnchorLocked) return "Locked — ready to record";
            if (controller.IsLocalized)
                return controller.SpatialSourceLabel == "Immersal" ? "Aligning — keep scanning…" : "Marker OK";
            return controller.SpatialSourceLabel == "Immersal"
                ? "Scanning room…"
                : "Point at Wall 1 marker";
        }

        private string GetModeText()
        {
            if (controller == null) return "No controller";
            if (!controller.IsInitialized) return "Initializing…";
            if (controller.IsWaitingForBody) return "Get into frame…";
            if (controller.IsRecording) return "Recording";
            if (controller.IsPlaying) return controller.IsLocalized ? "Playing back" : GetLocalizationHint();
            if (!controller.IsLocalized) return GetLocalizationHint();
            if (controller.CanPlayback) return "Localized — tap Play";
            if (controller.CanRecord) return "Localized — tap Record";
            return "Ready";
        }

        private Color GetModeColor()
        {
            if (controller == null || !controller.IsInitialized) return UITokens.Warning;
            if (controller.IsRecording || controller.IsWaitingForBody) return UITokens.Danger;
            if (!controller.IsLocalized) return UITokens.Warning;
            return UITokens.OnSurface;
        }

        // ============================================================================================
        // RECORDING SELECTOR
        // ============================================================================================

        private void RefreshRecordingsList()
        {
            availableRecordings.Clear();
            string mapId = controller != null ? controller.GetActiveMapId() : "";
            availableRecordings.AddRange(RecordingStorage.GetAvailableRecordings(mapId: mapId));
            selectedRecordingIndex = Mathf.Clamp(selectedRecordingIndex, 0, Mathf.Max(0, availableRecordings.Count - 1));

            // Keep the (optional) dropdown in sync if one was wired in the scene.
            if (recordingsDropdown != null)
            {
                recordingsDropdown.ClearOptions();
                var options = new List<string>();
                foreach (var recording in availableRecordings)
                {
                    var metadata = RecordingStorage.GetRecordingMetadata(recording);
                    options.Add(metadata != null
                        ? $"{recording} ({metadata.FormattedDuration})"
                        : recording);
                }
                recordingsDropdown.AddOptions(options);
            }

            UpdateRecordingNameLabel();
            UpdateUI();
        }

        private void UpdateRecordingNameLabel()
        {
            if (recordingNameLabel == null) return;
            if (availableRecordings.Count == 0)
            {
                recordingNameLabel.text = "No recordings";
                selectedRecordingDuration = 0f;
                return;
            }
            int idx = Mathf.Clamp(selectedRecordingIndex, 0, availableRecordings.Count - 1);
            var meta = RecordingStorage.GetRecordingMetadata(availableRecordings[idx]);
            selectedRecordingDuration = meta != null ? meta.duration : 0f;
            recordingNameLabel.text = meta != null
                ? $"{availableRecordings[idx]}  ·  {meta.FormattedDuration}"
                : availableRecordings[idx];
        }

        // ============================================================================================
        // VISIBILITY TOGGLE
        // ============================================================================================

        /// <summary>Hide or show the entire record/settings UI (used when switching to playback screen).</summary>
        public void SetScreenVisible(bool visible)
        {
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(visible);
        }

        private void ApplyMainUiVisibility()
        {
            if (mainUiPanel != null)
                mainUiPanel.SetActive(mainUiVisible);
        }

        // ============================================================================================
        // BUTTON HANDLERS (preserve original controller calls)
        // ============================================================================================

        private void OnRecordToggleClicked()
        {
            if (controller == null) return;
            if (controller.IsRecording || controller.IsWaitingForBody) OnStopRecordClicked();
            else OnRecordClicked();
        }

        private void OnPlayToggleClicked()
        {
            if (controller == null) return;
            // Play button now toggles play / pause (it no longer stops). The dedicated stop button resets.
            if (!controller.IsPlaying)
                OnPlayClicked();
            else if (controller.IsPaused)
                controller.ResumePlayback();
            else
                controller.PausePlayback();
            UpdateUI();
        }

        private void OnStopClicked()
        {
            if (controller == null) return;
            if (controller.IsRecording || controller.IsWaitingForBody) OnStopRecordClicked();
            else if (controller.IsPlaying) OnStopPlayClicked();
        }

        private void OnRecordClicked()
        {
            if (!controller.StartRecording())
                UnityEngine.Debug.LogWarning("[BodyTrackingUI] Failed to start recording");
            UpdateUI();
        }

        private void OnStopRecordClicked()
        {
            // If we're still waiting for a body (capture hasn't started), just cancel the arming.
            if (controller.IsWaitingForBody)
            {
                controller.CancelArming();
                UpdateUI();
                return;
            }

            var recording = controller.StopRecording();
            if (recording != null)
                RefreshRecordingsList();
            UpdateUI();
        }

        private void OnPlayClicked()
        {
            // Always replay the most recent recording.
            controller.LoadLatestRecording();
            if (!controller.StartPlayback())
                UnityEngine.Debug.LogWarning("[BodyTrackingUI] Failed to start playback");
            UpdateUI();
        }

        private void OnStopPlayClicked()
        {
            controller.StopPlayback();
            UpdateUI();
        }

        private void OnLoadClicked()
        {
            if (availableRecordings.Count == 0) return;
            int selectedIndex = recordingsDropdown != null ? recordingsDropdown.value : selectedRecordingIndex;
            if (selectedIndex < 0 || selectedIndex >= availableRecordings.Count) return;

            string fileName = availableRecordings[selectedIndex];
            if (controller.LoadRecording(fileName))
                UpdateUI();
            else
            {
                UnityEngine.Debug.LogWarning($"[BodyTrackingUI] Failed to load recording: {fileName}");
            }
        }

        private void OnScrubChanged(float value)
        {
            if (suppressScrubCallback || controller == null) return;
            if (!controller.IsPlaying) return;
            // Works for both the dot-skeleton player and the fused Move AI replay.
            controller.SeekPlaybackNormalized(value);
        }

        // ============================================================================================
        // EVENT HANDLERS
        // ============================================================================================

        private void OnModeChanged(OperationMode newMode) => UpdateUI();

        private void OnRecordingComplete(BodyTracking.Data.HipRecording recording)
        {
            RefreshRecordingsList();
            UpdateUI();
        }

        // ============================================================================================
        // PUBLIC HELPERS (kept for external integration)
        // ============================================================================================

        /// <summary>Concise system status string for external display.</summary>
        public string GetSystemStatus()
        {
            if (controller == null) return "Controller not available";
            if (!controller.IsInitialized) return "Initializing…";
            if (!controller.IsLocalized) return GetLocalizationHint();
            if (controller.IsRecording) return "Recording";
            if (controller.IsPlaying) return "Playing back";
            if (controller.CanRecord) return "Ready to record";
            if (controller.CanPlayback) return "Ready to play";
            return "Ready";
        }

        /// <summary>Summary of stored recordings for external display.</summary>
        public string GetRecordingStats()
        {
            var recordings = RecordingStorage.GetAvailableRecordings();
            var totalSize = RecordingStorage.GetTotalStorageUsed();
            return $"{recordings.Count} recordings, {FormatBytes(totalSize)} total";
        }

        private string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        // ============================================================================================
        // LAYOUT HELPERS
        // ============================================================================================

        private static void SetRowHeight(RectTransform row, float height)
        {
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            le.flexibleHeight = 0f;
        }

        private static void SetLayout(GameObject go, float minW = -1f, float prefW = -1f, float flexW = -1f, float prefH = -1f)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            if (minW >= 0f) le.minWidth = minW;
            if (prefW >= 0f) le.preferredWidth = prefW;
            if (flexW >= 0f) le.flexibleWidth = flexW;
            if (prefH >= 0f) { le.minHeight = prefH; le.preferredHeight = prefH; }
        }
    }
}
