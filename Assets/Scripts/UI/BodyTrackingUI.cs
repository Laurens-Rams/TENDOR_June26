using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Spatial;
using BodyTracking.Storage;
using BodyTracking.Animation;

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
    ///   • Top status bar  — AR/body-tracking status, localization status, tracked-joint count (pills).
    ///   • Move AI strip   — full-width line below the pills showing upload / fusion progress.
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
        private Image arDot, locDot, jointsDot;
        private TextMeshProUGUI arLabel, locLabel, jointsLabel;
        private Image moveDot;
        private TextMeshProUGUI moveLabel;
        private GameObject moveStatusRow;
        private TextMeshProUGUI currentTimeLabel, totalTimeLabel, recordingNameLabel;
        private Slider scrubSlider;
        private Button prevRecordingButton, nextRecordingButton;
        private Button realignButton;
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

        /// <summary>Pill button that switches to the dedicated playback screen.</summary>
        private void BuildScreenSwitchButton(RectTransform root)
        {
            goToPlaybackButton = UIFactory.CreatePillButton("GoToPlaybackButton", root, "Playback", ghost: true);
            var rect = (RectTransform)goToPlaybackButton.transform;
            rect.sizeDelta = new Vector2(108f, 34f);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(UITokens.Space12, -UITokens.Space8);
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
            area.anchoredPosition = new Vector2(0f, -UITokens.Space8);

            var layout = area.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)UITokens.Space12, (int)UITokens.Space12, 0, 0);
            layout.spacing = UITokens.Space4;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var areaFitter = area.gameObject.AddComponent<ContentSizeFitter>();
            areaFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            areaFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            BuildTopPillsRow(area);
            BuildMoveStatusRow(area);
        }

        private void BuildTopPillsRow(RectTransform parent)
        {
            var bar = UIFactory.CreateRect("TopPillsRow", parent);
            var rowLayout = bar.gameObject.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = UITokens.PillHeight;
            rowLayout.minHeight = UITokens.PillHeight;

            var layout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UITokens.Space8;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            var ar = UIFactory.CreateStatusPill("Pill_AR", bar, "AR", autoSize: false);
            arDot = ar.dot; arLabel = ar.label;
            SetPillFlex(ar.root, 1f);

            var loc = UIFactory.CreateStatusPill("Pill_Localization", bar, "Not localized", autoSize: false);
            locDot = loc.dot; locLabel = loc.label;
            SetPillFlex(loc.root, 1.7f);

            var joints = UIFactory.CreateStatusPill("Pill_Joints", bar, "J: 0", autoSize: false);
            jointsDot = joints.dot; jointsLabel = joints.label;
            SetPillFlex(joints.root, 0.9f);
        }

        private void BuildMoveStatusRow(RectTransform parent)
        {
            moveStatusRow = UIFactory.CreateRect("MoveStatusRow", parent).gameObject;
            var rowLayout = moveStatusRow.AddComponent<LayoutElement>();
            rowLayout.preferredHeight = UITokens.PillHeight;
            rowLayout.minHeight = UITokens.PillHeight;

            var pill = UIFactory.CreateStatusPill("MoveStatusPill", moveStatusRow.transform, "Move AI · idle", autoSize: false);
            UIFactory.Stretch(pill.root);
            moveDot = pill.dot;
            moveLabel = pill.label;
            if (moveLabel != null)
            {
                moveLabel.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
                moveLabel.overflowMode = TextOverflowModes.Ellipsis;
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
            mapUiBuilt = realignButton != null;
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

            float topY = -(UITokens.Space8 + UITokens.PillHeight * 2f + UITokens.Space4 + 4f);
            const float mapPillHeight = 34f;

            realignButton = UIFactory.CreatePillButton("ImmersalRetargetRealign", root, "Retarget & align", ghost: true);
            var realignRect = (RectTransform)realignButton.transform;
            realignRect.sizeDelta = new Vector2(132f, mapPillHeight);
            realignRect.anchorMin = new Vector2(0f, 1f);
            realignRect.anchorMax = new Vector2(0f, 1f);
            realignRect.pivot = new Vector2(0f, 1f);
            realignRect.anchoredPosition = new Vector2(UITokens.Space12, topY - 42f);
            realignButton.onClick.RemoveListener(OnRetargetRealignClicked);
            realignButton.onClick.AddListener(OnRetargetRealignClicked);

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

        private void OnRetargetRealignClicked()
        {
            if (controller != null)
                controller.RetargetAndRealignImmersal();
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

            // Retarget/re-align when Immersal is available (including after a map switch, before re-lock).
            if (realignButton != null)
            {
                var immersal = controller.routeRootManager != null
                    ? controller.routeRootManager.ImmersalProvider
                    : null;
                bool immersalActive = immersal != null && immersal.IsAvailable;
                realignButton.gameObject.SetActive(immersalActive);
                realignButton.interactable = immersalActive && !isRecording && !isPlaying
                    && (mapSwitcher == null || !mapSwitcher.IsSwitching);
            }

            if (scrubSlider != null)
                scrubSlider.interactable = isPlaying;

            UpdateMapControls();
        }

        private void UpdateStatusPills()
        {
            if (controller == null) return;

            // AR / body tracking pill.
            bool hasBody = controller.recorder != null && controller.recorder.HasTrackedBody;
            if (arLabel != null) arLabel.text = hasBody ? "Body OK" : (controller.IsInitialized ? "No body" : "Starting");
            if (arDot != null) arDot.color = hasBody ? UITokens.Success : UITokens.Warning;

            // Localization pill — green dot = success; label names the active source (no unicode symbols;
            // LiberationSans on device does not render checkmarks).
            bool localized = controller.IsLocalized;
            bool locked = controller.IsAnchorLocked;
            string source = controller.SpatialSourceLabel;
            if (locLabel != null)
            {
                if (locked)
                    locLabel.text = "Locked — ready";
                else if (localized)
                    locLabel.text = source == "Immersal" ? "Aligning…" : "Marker OK";
                else if (source == "Immersal")
                    locLabel.text = "Scanning…";
                else
                    locLabel.text = "Need marker";
            }
            // Green only once truly locked/stable; amber while still aligning so you know to keep scanning.
            if (locDot != null)
                locDot.color = locked ? UITokens.Success : (localized ? UITokens.Primary : UITokens.Warning);

            // Tracked-joint count pill.
            int joints = controller.recorder != null ? controller.recorder.LastTrackedJointCount : 0;
            if (jointsLabel != null) jointsLabel.text = $"J: {joints}";
            if (jointsDot != null) jointsDot.color = joints > 0 ? UITokens.Success : UITokens.Muted;
        }

        private void UpdateMoveStatusLine()
        {
            if (moveStatusRow == null || moveLabel == null)
                return;

            if (controller == null || !controller.MoveAIEnabled)
            {
                moveStatusRow.SetActive(false);
                return;
            }

            moveStatusRow.SetActive(true);
            string text = GetMoveStatusText();
            moveLabel.text = text;
            if (moveDot != null)
                moveDot.color = GetMoveStatusColor(text);
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

        private void OnFusionStatusChanged(string message) => UpdateMoveStatusLine();

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
