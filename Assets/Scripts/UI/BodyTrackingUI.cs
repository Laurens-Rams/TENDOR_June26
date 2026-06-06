using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Spatial;
using BodyTracking.Storage;

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
        [Tooltip("Panel hidden when the visibility toggle is tapped. Auto-set to the bottom transport bar.")]
        [SerializeField] private GameObject mainUiPanel;
        [SerializeField] private bool createToggleIfMissing = true;

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
        private Button visibilityToggleButton;
        private Button realignButton;
        private RectTransform visibilityToggleIcon;
        private Button cleanViewButton;
        private TextMeshProUGUI cleanViewLabel;
        private DebugVisualsController debugVisuals;
        private ImmersalMapSwitcher mapSwitcher;
        private Button mapToggleButton;
        private TextMeshProUGUI mapToggleLabel;
        private GameObject mapPanel;
        private TMP_InputField mapIdInput;
        private Button mapLoadButton;
        private TextMeshProUGUI mapStatusLabel;
        private bool mapPanelVisible;

        // State
        private readonly List<string> availableRecordings = new List<string>();
        private int selectedRecordingIndex;
        private bool mainUiVisible = true;
        private bool suppressScrubCallback;
        private int transportDbgFrame;

        void Start()
        {
            if (controller == null)
                controller = UnityEngine.Object.FindObjectOfType<BodyTrackingController>();

            if (controller == null)
                UnityEngine.Debug.LogError("[BodyTrackingUI] BodyTrackingController not found");

            BuildUI();
            HookUpEvents();

            if (controller != null)
            {
                controller.OnModeChanged += OnModeChanged;
                controller.OnRecordingComplete += OnRecordingComplete;
                controller.OnFusionStatusChanged += OnFusionStatusChanged;
            }

            RefreshRecordingsList();
            UpdateUI();
        }

        void OnDestroy()
        {
            if (controller != null)
            {
                controller.OnModeChanged -= OnModeChanged;
                controller.OnRecordingComplete -= OnRecordingComplete;
                controller.OnFusionStatusChanged -= OnFusionStatusChanged;
            }

            if (mapSwitcher != null)
            {
                mapSwitcher.OnStatusChanged -= OnMapSwitcherStatusChanged;
                mapSwitcher.OnMapSwitched -= OnMapSwitched;
            }
        }

        void Update()
        {
            if (!mainUiVisible)
                return;

            // Timeline/scrub state changes every frame; status pills can update less often.
            UpdateTransportTime();

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

            DeactivateLegacyUI(canvas);

            BuildTopStatusArea(uiRoot);
            BuildBottomTransportBar(uiRoot);
            BuildVisibilityToggle(uiRoot);
            BuildCleanViewToggle(uiRoot);
            BuildMapSwitcher(uiRoot);

            ApplyMainUiVisibility();
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
                canvas = UnityEngine.Object.FindObjectOfType<Canvas>();

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
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() == null)
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
                moveLabel.enableWordWrapping = false;
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

        private void BuildBottomTransportBar(RectTransform root)
        {
            var bar = UIFactory.CreatePanel("BottomTransportBar", root, UITokens.Surface, UITokens.RadiusLarge);
            var barRect = bar.rectTransform;
            barRect.anchorMin = new Vector2(0f, 0f);
            barRect.anchorMax = new Vector2(1f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.sizeDelta = new Vector2(-UITokens.Space12 * 2f, 192f);
            barRect.anchoredPosition = new Vector2(0f, UITokens.Space8);
            bar.raycastTarget = true; // bar absorbs taps so they don't fall through to AR content

            mainUiPanel = bar.gameObject;

            var column = bar.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset((int)UITokens.Space16, (int)UITokens.Space16, (int)UITokens.Space12, (int)UITokens.Space16);
            column.spacing = UITokens.Space8;
            column.childAlignment = TextAnchor.UpperCenter;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            BuildHeaderRow(barRect);
            // Recording selection + Load/Re-align controls removed: playback always uses the latest recording,
            // and the timeline (scrub) below is the only transport affordance besides play/pause/stop.
            BuildScrubRow(barRect);
            BuildTransportRow(barRect);
        }

        private void BuildHeaderRow(RectTransform parent)
        {
            var row = UIFactory.CreateRect("HeaderRow", parent);
            SetRowHeight(row, 28f);

            modeText = UIFactory.CreateText("ModeText", row, "Initializing…", UITokens.FontBody, UITokens.OnSurface, TextAlignmentOptions.Left);
            UIFactory.Stretch(modeText.rectTransform);

            // statusText is kept (referenced by tooling/controller) but hidden from the minimal UI; it
            // carries an optional concise status string for diagnostics.
            statusText = UIFactory.CreateText("StatusText", row, string.Empty, UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Left);
            UIFactory.Stretch(statusText.rectTransform);
            statusText.gameObject.SetActive(false);
        }

        private void BuildScrubRow(RectTransform parent)
        {
            var row = UIFactory.CreateRect("ScrubRow", parent);
            SetRowHeight(row, 26f);

            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UITokens.Space8;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            currentTimeLabel = UIFactory.CreateText("CurrentTime", row, "00:00", UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Center);
            SetLayout(currentTimeLabel.gameObject, prefW: 56f);

            scrubSlider = UIFactory.CreateScrubSlider("Scrubber", row);
            SetLayout(scrubSlider.gameObject, flexW: 1f, prefH: UITokens.ScrubHandleDiameter);
            scrubSlider.onValueChanged.AddListener(OnScrubChanged);

            totalTimeLabel = UIFactory.CreateText("TotalTime", row, "00:00", UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Center);
            SetLayout(totalTimeLabel.gameObject, prefW: 56f);
        }

        private void BuildTransportRow(RectTransform parent)
        {
            var row = UIFactory.CreateRect("TransportRow", parent);
            SetRowHeight(row, UITokens.TransportPrimaryDiameter);

            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = UITokens.Space24;
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            // Record (toggles between record-dot and stop-square).
            recordButton = UIFactory.CreateCircleButton("RecordButton", row, UITokens.TransportSecondaryDiameter, UITokens.SurfaceElevated);
            SetLayout(recordButton.gameObject, prefW: UITokens.TransportSecondaryDiameter, prefH: UITokens.TransportSecondaryDiameter);
            recordIcon = UIFactory.AddRecordIcon(recordButton.transform, 18f, UITokens.Danger, square: false);

            // Play / pause (primary accent).
            playButton = UIFactory.CreateCircleButton("PlayButton", row, UITokens.TransportPrimaryDiameter, UITokens.Primary);
            SetLayout(playButton.gameObject, prefW: UITokens.TransportPrimaryDiameter, prefH: UITokens.TransportPrimaryDiameter);
            playIcon = UIFactory.AddPlayIcon(playButton.transform, 26f, Color.white).gameObject;
            pauseIcon = UIFactory.AddPauseIcon(playButton.transform, 22f, Color.white);
            pauseIcon.SetActive(false);

            // Stop (also wired to the public stopPlayButton field).
            stopPlayButton = UIFactory.CreateCircleButton("StopButton", row, UITokens.TransportSecondaryDiameter, UITokens.SurfaceElevated);
            SetLayout(stopPlayButton.gameObject, prefW: UITokens.TransportSecondaryDiameter, prefH: UITokens.TransportSecondaryDiameter);
            UIFactory.AddStopIcon(stopPlayButton.transform, 16f, UITokens.OnSurface);
        }

        private void BuildVisibilityToggle(RectTransform root)
        {
            if (!createToggleIfMissing)
                return;

            visibilityToggleButton = UIFactory.CreateCircleButton("VisibilityToggle", root, 34f, UITokens.SurfaceElevated);
            var rect = (RectTransform)visibilityToggleButton.transform;
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-UITokens.Space12, -(UITokens.Space8 + UITokens.PillHeight * 2f + UITokens.Space4 + 4f));

            visibilityToggleIcon = (RectTransform)UIFactory.AddPlayIcon(rect, 12f, UITokens.OnSurface).transform;
            visibilityToggleIcon.localRotation = Quaternion.Euler(0f, 0f, -90f); // chevron down = hide
            visibilityToggleButton.onClick.AddListener(ToggleMainUiVisibility);
        }

        /// <summary>
        /// Top-left pill that toggles all developer/debug visuals (Immersal point-cloud dots, skeletons,
        /// BlazePose overlays, image-target debug quad) on/off, leaving the playback UI + final character.
        /// </summary>
        private void BuildCleanViewToggle(RectTransform root)
        {
            debugVisuals = UnityEngine.Object.FindObjectOfType<DebugVisualsController>();
            if (debugVisuals == null)
                debugVisuals = gameObject.AddComponent<DebugVisualsController>();

            cleanViewButton = UIFactory.CreatePillButton("CleanViewToggle", root, "Hide AR debug", ghost: false);
            var rect = (RectTransform)cleanViewButton.transform;
            rect.sizeDelta = new Vector2(132f, 34f);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            // Sit in the same clear left-column slot the Map button uses (proven visible), below the status pills.
            rect.anchoredPosition = new Vector2(UITokens.Space12, -(UITokens.Space8 + UITokens.PillHeight * 2f + UITokens.Space4 + 4f) - 42f);

            cleanViewLabel = cleanViewButton.GetComponentInChildren<TextMeshProUGUI>();
            cleanViewButton.onClick.AddListener(OnCleanViewClicked);

            UpdateCleanViewLabel();
        }

        private void OnCleanViewClicked()
        {
            if (debugVisuals != null)
                debugVisuals.Toggle();
            UpdateCleanViewLabel();
        }

        private void UpdateCleanViewLabel()
        {
            if (cleanViewLabel == null)
                return;
            bool showing = debugVisuals == null || debugVisuals.VisualsVisible;
            cleanViewLabel.text = showing ? "Hide AR debug" : "Show AR debug";
        }

        /// <summary>
        /// Top-left control to enter a new Immersal map id at runtime (device builds). Downloads map
        /// data and the sparse visualization, then updates localization for the whole session.
        /// </summary>
        private void BuildMapSwitcher(RectTransform root)
        {
            mapSwitcher = UnityEngine.Object.FindObjectOfType<ImmersalMapSwitcher>();
            if (mapSwitcher == null && controller != null && controller.routeRootManager != null)
                mapSwitcher = controller.routeRootManager.gameObject.AddComponent<ImmersalMapSwitcher>();

            if (mapSwitcher == null)
                return;

            mapSwitcher.OnStatusChanged += OnMapSwitcherStatusChanged;
            mapSwitcher.OnMapSwitched += OnMapSwitched;

            float topY = -(UITokens.Space8 + UITokens.PillHeight * 2f + UITokens.Space4 + 4f);

            mapToggleButton = UIFactory.CreatePillButton("MapToggle", root, "Map", ghost: true);
            var toggleRect = (RectTransform)mapToggleButton.transform;
            toggleRect.sizeDelta = new Vector2(88f, 34f);
            toggleRect.anchorMin = new Vector2(0f, 1f);
            toggleRect.anchorMax = new Vector2(0f, 1f);
            toggleRect.pivot = new Vector2(0f, 1f);
            toggleRect.anchoredPosition = new Vector2(UITokens.Space12, topY - 84f);
            mapToggleLabel = mapToggleButton.GetComponentInChildren<TextMeshProUGUI>();
            mapToggleButton.onClick.AddListener(ToggleMapPanel);

            mapPanel = UIFactory.CreatePanel("MapPanel", root, UITokens.Surface, UITokens.RadiusLarge).gameObject;
            var panelRect = mapPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.sizeDelta = new Vector2(248f, 132f);
            panelRect.anchoredPosition = new Vector2(UITokens.Space12, topY - 122f);
            mapPanel.GetComponent<Image>().raycastTarget = true;

            var column = mapPanel.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset((int)UITokens.Space12, (int)UITokens.Space12, (int)UITokens.Space8, (int)UITokens.Space8);
            column.spacing = UITokens.Space8;
            column.childAlignment = TextAnchor.UpperLeft;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            mapStatusLabel = UIFactory.CreateText("MapStatus", mapPanel.transform, "Enter Immersal map ID", UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Left);
            SetLayout(mapStatusLabel.gameObject, prefH: 18f);

            mapIdInput = UIFactory.CreateInputField("MapIdInput", mapPanel.transform, "e.g. 147158");
            SetLayout(mapIdInput.gameObject, prefH: 40f);
            if (mapSwitcher.ActiveMapId > 0)
                mapIdInput.text = mapSwitcher.ActiveMapId.ToString();

            var row = UIFactory.CreateRect("MapActions", mapPanel.transform);
            SetLayout(row.gameObject, prefH: 40f);
            var rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = UITokens.Space8;
            rowLayout.childAlignment = TextAnchor.MiddleCenter;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;

            mapLoadButton = UIFactory.CreatePillButton("MapLoadButton", row, "Load map", ghost: false);
            SetLayout(mapLoadButton.gameObject, flexW: 1f, prefH: 40f);
            mapLoadButton.onClick.AddListener(OnMapLoadClicked);

            mapPanelVisible = false;
            mapPanel.SetActive(false);
            UpdateMapToggleLabel();
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
                return;

            mapSwitcher.SwitchToMapFromInput(mapIdInput.text);
            UpdateMapControls();
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
            UpdateMapToggleLabel();
            UpdateMapControls();
        }

        private void OnMapSwitched(int mapId)
        {
            if (controller != null)
                controller.LoadLatestRecording();
            RefreshRecordingsList();
            UpdateUI();
        }

        private void UpdateMapToggleLabel()
        {
            if (mapToggleLabel == null || mapSwitcher == null)
                return;

            string id = mapSwitcher.ActiveMapId > 0 ? mapSwitcher.ActiveMapId.ToString() : "—";
            mapToggleLabel.text = mapSwitcher.IsSwitching ? "Map…" : $"Map {id}";
        }

        private void UpdateMapControls()
        {
            if (mapLoadButton != null && mapSwitcher != null)
                mapLoadButton.interactable = !mapSwitcher.IsSwitching;
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
            if (realignButton != null) realignButton.onClick.AddListener(OnRealignClicked);
        }

        private void OnRealignClicked()
        {
            if (controller != null)
                controller.RealignToImmersal();
        }

        // ============================================================================================
        // STATE / UPDATE
        // ============================================================================================

        private void UpdateUI()
        {
            if (controller == null) return;

            UpdateStatusPills();
            UpdateMoveStatusLine();

            if (modeText != null)
            {
                modeText.text = GetModeText();
                modeText.color = GetModeColor();
            }

            bool canRecord = controller.CanRecord;
            bool canPlayback = controller.CanPlayback;
            bool isRecording = controller.IsRecording;
            bool isPlaying = controller.IsPlaying;
            bool isWaitingForBody = controller.IsWaitingForBody;

            // Record button: record-dot when idle, red stop-square while recording or waiting-to-arm (tap to cancel).
            if (recordButton != null)
            {
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

            // Re-align only makes sense when Immersal is the active, localized source and we're idle.
            if (realignButton != null)
            {
                bool immersalActive = controller.SpatialSourceLabel == "Immersal" && controller.IsLocalized;
                realignButton.gameObject.SetActive(immersalActive);
                realignButton.interactable = immersalActive && !isRecording && !isPlaying;
            }

            if (scrubSlider != null)
                scrubSlider.interactable = isPlaying;
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
                // #region agent log
                if ((transportDbgFrame++ % 60) == 0)
                    BodyTracking.DebugTools.DebugSessionLog.Log("D", "BodyTrackingUI.cs:UpdateTransportTime",
                        "transport reads FusedPlayer",
                        "{\"fusedCurrentTime\":" + current.ToString("F2") +
                        ",\"fusedDuration\":" + total.ToString("F2") + "}");
                // #endregion
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
            if (availableRecordings.Count == 0) return 0f;
            int idx = Mathf.Clamp(selectedRecordingIndex, 0, availableRecordings.Count - 1);
            var meta = RecordingStorage.GetRecordingMetadata(availableRecordings[idx]);
            return meta != null ? meta.duration : 0f;
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
                return;
            }
            int idx = Mathf.Clamp(selectedRecordingIndex, 0, availableRecordings.Count - 1);
            var meta = RecordingStorage.GetRecordingMetadata(availableRecordings[idx]);
            recordingNameLabel.text = meta != null
                ? $"{availableRecordings[idx]}  ·  {meta.FormattedDuration}"
                : availableRecordings[idx];
        }

        // ============================================================================================
        // VISIBILITY TOGGLE
        // ============================================================================================

        private void ToggleMainUiVisibility()
        {
            mainUiVisible = !mainUiVisible;
            ApplyMainUiVisibility();
            if (mainUiVisible) UpdateUI();
        }

        private void ApplyMainUiVisibility()
        {
            if (mainUiPanel != null)
                mainUiPanel.SetActive(mainUiVisible);

            if (visibilityToggleIcon != null)
                visibilityToggleIcon.localRotation = Quaternion.Euler(0f, 0f, mainUiVisible ? -90f : 90f);
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
