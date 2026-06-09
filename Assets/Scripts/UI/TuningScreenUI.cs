using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using BodyTracking;
using BodyTracking.Playback;
using BodyTracking.Playback.PostProcess;
using BodyTracking.MoveAI;
using BodyTracking.Animation;
using BodyTracking.Diagnostics;
using BodyTracking.AR;
using BodyTracking.Spatial;

namespace BodyTracking.UI
{
    /// <summary>Top-level tabs in the tuning sheet.</summary>
    public enum TuningCategory
    {
        /// <summary>Map switch, localization, session debug view.</summary>
        General,
        /// <summary>Wall constraint test plan: slab, calibration, contact lock, debug planes.</summary>
        Wall,
        /// <summary>Penetration / floor gate / wall IK (test plan steps 4–5).</summary>
        IkFloor,
        /// <summary>Smoothing, glitch guard, facing.</summary>
        Pose,
        /// <summary>Movement, anchor, fit, face, fusion bake.</summary>
        Playback
    }

    /// <summary>
    /// Live tuning sheet for the <see cref="FusedCharacterPlayer"/>. A scrollable bottom sheet (so the character
    /// stays visible above it) with sliders + toggles for every main setting: facing, smoothing, glitch guard,
    /// penetration/IK, anchor (live-AR), fit and playback speed. Reached from the screen toggle alongside the
    /// record/playback screens. All edits apply immediately because the player reads these fields every frame.
    /// </summary>
    public class TuningScreenUI : MonoBehaviour
    {
        private const string RootName = "TuningUIRoot";

        public FusedCharacterPlayer player;
        public MoveAIFusionCoordinator coordinator;
        public BodyTrackingController controller;

        private RectTransform uiRoot;
        private RectTransform content;
        private Button backButton;
        private TextMeshProUGUI rebakeStatus;
        private bool initialized;
        private bool isVisible;

        // Translucent so the AR camera + character show through the sheet while tuning.
        private static readonly Color SheetColor = new Color(0.09f, 0.09f, 0.11f, 0.86f);

        // Horizontal gutters + width-constrained rows so TMP never bleeds past the scroll mask.
        private const float SheetSideInset = 24f;
        private const float ContentSideInset = 12f;
        private const float ViewportSideInset = 12f;
        private const float SectionFontSize = 11f;
        private const float LabelFontSize = 12f;
        private const float ValueFontSize = 11f;
        private const float HeaderFontSize = 16f;
        private const float ValueColumnWidth = 44f;
        private const float ToggleButtonWidth = 44f;
        private const float ModeButtonWidth = 64f;
        private const float ControlRowHeight = 28f;
        private const float SliderRowMinHeight = 48f;
        private const float HeaderHeight = 36f;
        private const float CategoryTabHeight = 30f;
        private const float CategoryTabFontSize = 10f;

        private ScrollRect scrollRect;
        private TuningCategory activeCategory = TuningCategory.Wall;
        private Button[] categoryTabButtons;
        private Image[] categoryTabImages;
        private TextMeshProUGUI[] categoryTabLabels;

        private static readonly string[] CategoryTabLabels = { "Gen", "Wall", "IK", "Pose", "Play" };

        private ImmersalMapSwitcher mapSwitcher;
        private TMP_InputField mapIdInput;
        private TextMeshProUGUI mapStatusLabel;
        private Button mapLoadButton;
        private RectTransform mapStrip;
        private RectTransform scrollViewport;
        private bool mapEventsSubscribed;

        private const float MapStripHeight = 112f;

        private DebugVisualsController debugVisuals;

        public Button BackButton => backButton;
        public TuningCategory ActiveCategory => activeCategory;

        void OnEnable() => EnsureInitialized();

        void OnDestroy()
        {
            if (mapSwitcher != null)
            {
                mapSwitcher.OnStatusChanged -= OnMapStatusChanged;
                mapSwitcher.OnMapSwitched -= OnMapSwitched;
            }
        }

        public void SetVisible(bool visible)
        {
            isVisible = visible;
            EnsureInitialized();
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(visible);
            if (visible)
            {
                UpdateMapStripLayout();
                RefreshMapStatusLabel();
                RebuildRows();
            }
        }

        private FusedCharacterPlayer Player()
        {
            if (player == null)
                player = UnityEngine.Object.FindFirstObjectByType<FusedCharacterPlayer>();
            return player;
        }

        private MoveAIFusionCoordinator Coordinator()
        {
            if (coordinator == null)
                coordinator = UnityEngine.Object.FindFirstObjectByType<MoveAIFusionCoordinator>();
            return coordinator;
        }

        private BodyTrackingController Controller()
        {
            if (controller == null)
                controller = UnityEngine.Object.FindFirstObjectByType<BodyTrackingController>();
            return controller;
        }

        private WallFloorDebugVisualizer visualizer;
        private bool searchedVisualizer;

        private WallFloorDebugVisualizer Visualizer()
        {
            if (visualizer == null && !searchedVisualizer)
            {
                searchedVisualizer = true;
                visualizer = UnityEngine.Object.FindFirstObjectByType<WallFloorDebugVisualizer>(FindObjectsInactive.Include);
            }
            return visualizer;
        }

        /// <summary>Find or create the blue/green wall debug overlay (not always present in the scene).</summary>
        private WallFloorDebugVisualizer EnsureVisualizer()
        {
            var v = Visualizer();
            if (v != null) return v;

            var host = Player()?.gameObject ?? Controller()?.gameObject ?? gameObject;
            visualizer = host.AddComponent<WallFloorDebugVisualizer>();
            searchedVisualizer = true;
            return visualizer;
        }

        private void EnableWallPlanePreview()
        {
            var viz = EnsureVisualizer();
            if (viz == null) return;
            viz.ShowVisuals = true;
            viz.ShowPlanes = true;
        }

        private DebugVisualsController EnsureDebugVisuals()
        {
            if (debugVisuals == null)
                debugVisuals = UnityEngine.Object.FindFirstObjectByType<DebugVisualsController>(FindObjectsInactive.Include);
            if (debugVisuals == null)
                debugVisuals = gameObject.AddComponent<DebugVisualsController>();
            return debugVisuals;
        }

        private bool EnsureMapSwitcher()
        {
            if (mapSwitcher != null)
                return true;

            mapSwitcher = UnityEngine.Object.FindFirstObjectByType<ImmersalMapSwitcher>();
            if (mapSwitcher == null)
            {
                var c = Controller();
                RouteRootManager rrm = c != null ? c.routeRootManager : null;
                if (rrm == null)
                    rrm = UnityEngine.Object.FindFirstObjectByType<RouteRootManager>();
                GameObject host = rrm != null ? rrm.gameObject : (c != null ? c.gameObject : null);
                if (host != null)
                {
                    mapSwitcher = host.GetComponent<ImmersalMapSwitcher>();
                    if (mapSwitcher == null)
                        mapSwitcher = host.AddComponent<ImmersalMapSwitcher>();
                }
            }

            if (mapSwitcher != null && !mapEventsSubscribed)
            {
                mapSwitcher.OnStatusChanged += OnMapStatusChanged;
                mapSwitcher.OnMapSwitched += OnMapSwitched;
                mapEventsSubscribed = true;
            }

            return mapSwitcher != null;
        }

        private CharacterMouth mouth;
        private CharacterEyeBlink eyeBlink;
        private CharacterEyeMovement eyeMovement;

        private CharacterMouth Mouth()
        {
            if (mouth == null)
                mouth = UnityEngine.Object.FindFirstObjectByType<CharacterMouth>(FindObjectsInactive.Include);
            return mouth;
        }

        private CharacterEyeBlink EyeBlink()
        {
            if (eyeBlink == null)
                eyeBlink = UnityEngine.Object.FindFirstObjectByType<CharacterEyeBlink>(FindObjectsInactive.Include);
            return eyeBlink;
        }

        private CharacterEyeMovement EyeMovement()
        {
            if (eyeMovement == null)
                eyeMovement = UnityEngine.Object.FindFirstObjectByType<CharacterEyeMovement>(FindObjectsInactive.Include);
            return eyeMovement;
        }

        /// <summary>
        /// The tuning sliders/toggles mutate the primary <see cref="FusedCharacterPlayer"/> only. When several
        /// characters are playing at once, push those edits onto every overlay so they all share one consistent
        /// set of correction settings.
        /// </summary>
        private void PropagateToOverlays()
        {
            Controller()?.ApplyTuningToOverlays();
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            Player();
            Coordinator();
            Controller();
            EnsureMapSwitcher();
            BuildShell();
            initialized = true;
        }

        // ============================================================================================
        // SHELL (bottom sheet + scroll view)
        // ============================================================================================

        private void BuildShell()
        {
            var canvas = EnsureCanvas();
            EnsureEventSystem();

            var existing = canvas.transform.Find(RootName);
            if (existing != null)
            {
                if (Application.isPlaying) Destroy(existing.gameObject); else DestroyImmediate(existing.gameObject);
            }

            uiRoot = UIFactory.CreateRect(RootName, canvas.transform);
            UIFactory.Stretch(uiRoot);
            uiRoot.gameObject.AddComponent<UISafeArea>();

            // Bottom sheet: covers the lower ~58% so the character stays visible above.
            var sheet = UIFactory.CreateRect("Sheet", uiRoot);
            sheet.anchorMin = new Vector2(0f, 0f);
            sheet.anchorMax = new Vector2(1f, 0.58f);
            sheet.offsetMin = new Vector2(SheetSideInset, UITokens.Space8);
            sheet.offsetMax = new Vector2(-SheetSideInset, 0f);
            var sheetBg = sheet.gameObject.AddComponent<Image>();
            sheetBg.sprite = UIFactory.RoundedSprite(UITokens.RadiusLarge);
            sheetBg.type = Image.Type.Sliced;
            sheetBg.color = SheetColor;
            sheetBg.raycastTarget = true;

            // Header: title + Back/Done.
            var title = UIFactory.CreateBoldText("Title", sheet, "Tuning", HeaderFontSize, UITokens.OnSurface,
                TextAlignmentOptions.Left);
            ConfigureRowLabel(title);
            var tr = title.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(0f, 1f);
            tr.sizeDelta = new Vector2(-96f, 24f);
            tr.anchoredPosition = new Vector2(UITokens.Space12, -UITokens.Space8);

            backButton = UIFactory.CreatePillButton("TuningBack", sheet, "Done", ghost: false);
            ConfigureCompactPillLabel(backButton.GetComponentInChildren<TextMeshProUGUI>());
            var br = (RectTransform)backButton.transform;
            br.anchorMin = new Vector2(1f, 1f);
            br.anchorMax = new Vector2(1f, 1f);
            br.pivot = new Vector2(1f, 1f);
            br.sizeDelta = new Vector2(72f, 28f);
            br.anchoredPosition = new Vector2(-UITokens.Space8, -UITokens.Space8);

            BuildCategoryTabs(sheet);
            BuildMapStrip(sheet);

            float scrollTopInset = HeaderHeight + CategoryTabHeight + UITokens.Space8;

            // Scroll view filling the rest of the sheet.
            scrollViewport = UIFactory.CreateRect("Viewport", sheet);
            scrollViewport.anchorMin = new Vector2(0f, 0f);
            scrollViewport.anchorMax = new Vector2(1f, 1f);
            scrollViewport.offsetMin = new Vector2(ViewportSideInset, UITokens.Space8);
            scrollViewport.offsetMax = new Vector2(-ViewportSideInset, -scrollTopInset);
            scrollViewport.gameObject.AddComponent<RectMask2D>();

            content = UIFactory.CreateRect("Content", scrollViewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);
            var contentWidth = content.gameObject.AddComponent<LayoutElement>();
            contentWidth.minWidth = 0f;
            contentWidth.flexibleWidth = 1f;
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = UITokens.Space4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(
                Mathf.RoundToInt(ContentSideInset),
                Mathf.RoundToInt(ContentSideInset),
                0,
                Mathf.RoundToInt(UITokens.Space12));
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect = sheet.gameObject.AddComponent<ScrollRect>();
            scrollRect.content = content;
            scrollRect.viewport = scrollViewport;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 20f;

            UpdateMapStripLayout();
            UpdateCategoryTabVisuals();
            uiRoot.gameObject.SetActive(isVisible);
        }

        /// <summary>Fixed map-id strip (not rebuilt with scroll rows) so the input stays tappable.</summary>
        private void BuildMapStrip(RectTransform sheet)
        {
            EnsureMapSwitcher();

            mapStrip = UIFactory.CreateRect("MapStrip", sheet);
            mapStrip.anchorMin = new Vector2(0f, 1f);
            mapStrip.anchorMax = new Vector2(1f, 1f);
            mapStrip.pivot = new Vector2(0.5f, 1f);
            mapStrip.anchoredPosition = new Vector2(0f, -(HeaderHeight + CategoryTabHeight));
            mapStrip.sizeDelta = new Vector2(-ViewportSideInset * 2f, MapStripHeight);

            var bg = mapStrip.gameObject.AddComponent<Image>();
            bg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusMedium);
            bg.type = Image.Type.Sliced;
            bg.color = UITokens.SurfaceElevated;
            bg.raycastTarget = true;

            var col = mapStrip.gameObject.AddComponent<VerticalLayoutGroup>();
            col.padding = new RectOffset((int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8, (int)UITokens.Space8);
            col.spacing = UITokens.Space4;
            col.childAlignment = TextAnchor.UpperLeft;
            col.childControlWidth = true;
            col.childControlHeight = true;
            col.childForceExpandWidth = true;
            col.childForceExpandHeight = false;

            mapStatusLabel = UIFactory.CreateText("MapStatus", mapStrip, GetActiveMapStatusText(),
                ValueFontSize, UITokens.Muted, TextAlignmentOptions.Left);
            ConfigureRowLabel(mapStatusLabel);
            var statusLe = mapStatusLabel.gameObject.AddComponent<LayoutElement>();
            statusLe.minHeight = 18f;
            statusLe.preferredHeight = 18f;

            mapIdInput = UIFactory.CreateInputField("MapIdInput", mapStrip, "e.g. 147190", 40f);
            var inputLe = mapIdInput.gameObject.AddComponent<LayoutElement>();
            inputLe.minHeight = 40f;
            inputLe.preferredHeight = 40f;
            inputLe.flexibleWidth = 1f;
            inputLe.minWidth = 0f;
            if (mapIdInput.textComponent != null)
                mapIdInput.textComponent.fontSize = LabelFontSize;
            if (mapIdInput.placeholder is TextMeshProUGUI ph)
                ph.fontSize = LabelFontSize;
            SyncMapInput();
            mapIdInput.onSubmit.AddListener(_ => OnMapLoadClicked());

            mapLoadButton = UIFactory.CreatePillButton("MapLoad", mapStrip, "Load map", ghost: false);
            var loadLe = mapLoadButton.gameObject.AddComponent<LayoutElement>();
            loadLe.minHeight = 36f;
            loadLe.preferredHeight = 36f;
            loadLe.flexibleWidth = 1f;
            ConfigureCompactPillLabel(mapLoadButton.GetComponentInChildren<TextMeshProUGUI>());
            mapLoadButton.onClick.AddListener(OnMapLoadClicked);

            mapStrip.gameObject.SetActive(false);
        }

        private void UpdateMapStripLayout()
        {
            if (mapStrip == null || scrollViewport == null)
                return;

            bool showMap = isVisible && activeCategory == TuningCategory.General;
            mapStrip.gameObject.SetActive(showMap);
            if (showMap)
                mapStrip.SetAsLastSibling();

            float scrollTopInset = HeaderHeight + CategoryTabHeight + UITokens.Space8;
            if (showMap)
                scrollTopInset += MapStripHeight + UITokens.Space4;

            scrollViewport.offsetMax = new Vector2(-ViewportSideInset, -scrollTopInset);
        }

        private void RefreshMapStatusLabel()
        {
            if (mapStatusLabel != null)
                mapStatusLabel.text = GetActiveMapStatusText();
        }

        private void BuildCategoryTabs(RectTransform sheet)
        {
            var tabBar = UIFactory.CreateRect("CategoryTabs", sheet);
            tabBar.anchorMin = new Vector2(0f, 1f);
            tabBar.anchorMax = new Vector2(1f, 1f);
            tabBar.pivot = new Vector2(0.5f, 1f);
            tabBar.sizeDelta = new Vector2(-ViewportSideInset * 2f, CategoryTabHeight);
            tabBar.anchoredPosition = new Vector2(0f, -HeaderHeight);

            var hlg = tabBar.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = UITokens.Space4;
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            int tabCount = CategoryTabLabels.Length;
            categoryTabButtons = new Button[tabCount];
            categoryTabImages = new Image[tabCount];
            categoryTabLabels = new TextMeshProUGUI[tabCount];

            for (int i = 0; i < tabCount; i++)
            {
                var category = (TuningCategory)i;
                var btn = UIFactory.CreatePillButton("Tab_" + category, tabBar, CategoryTabLabels[i], ghost: true);
                categoryTabButtons[i] = btn;
                categoryTabImages[i] = btn.GetComponent<Image>();
                categoryTabLabels[i] = btn.GetComponentInChildren<TextMeshProUGUI>();
                if (categoryTabLabels[i] != null)
                {
                    categoryTabLabels[i].fontSize = CategoryTabFontSize;
                    categoryTabLabels[i].overflowMode = TextOverflowModes.Truncate;
                }

                var le = btn.gameObject.AddComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.minWidth = 0f;
                le.preferredHeight = CategoryTabHeight;
                le.minHeight = CategoryTabHeight;

                var rect = (RectTransform)btn.transform;
                rect.sizeDelta = new Vector2(0f, CategoryTabHeight);

                int captured = i;
                btn.onClick.AddListener(() => SelectCategory((TuningCategory)captured));
            }
        }

        private void SelectCategory(TuningCategory category)
        {
            if (activeCategory == category) return;
            activeCategory = category;
            UpdateCategoryTabVisuals();
            UpdateMapStripLayout();
            RebuildRows();
        }

        private void UpdateCategoryTabVisuals()
        {
            if (categoryTabButtons == null) return;
            for (int i = 0; i < categoryTabButtons.Length; i++)
            {
                bool selected = (TuningCategory)i == activeCategory;
                if (categoryTabImages != null && categoryTabImages[i] != null)
                    categoryTabImages[i].color = selected ? UITokens.Primary : UITokens.SurfaceElevated;
                if (categoryTabLabels != null && categoryTabLabels[i] != null)
                    categoryTabLabels[i].color = selected ? Color.white : UITokens.Muted;
            }
        }

        // ============================================================================================
        // ROWS
        // ============================================================================================

        private void RebuildRows()
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                if (Application.isPlaying) Destroy(c.gameObject); else DestroyImmediate(c.gameObject);
            }

            rebakeStatus = null;

            var p = Player();
            if (p == null)
            {
                AddSection("No FusedCharacterPlayer found in scene");
                return;
            }

            switch (activeCategory)
            {
                case TuningCategory.General:
                    BuildGeneralCategoryRows(p);
                    break;
                case TuningCategory.Wall:
                    BuildWallCategoryRows(p);
                    break;
                case TuningCategory.IkFloor:
                    BuildIkFloorCategoryRows(p);
                    break;
                case TuningCategory.Pose:
                    BuildPoseCategoryRows(p);
                    break;
                case TuningCategory.Playback:
                    BuildPlaybackCategoryRows(p);
                    break;
            }

            if (scrollRect != null)
                scrollRect.verticalNormalizedPosition = 1f;

            UpdateCategoryTabVisuals();
        }

        /// <summary>Map switch, localization, and session-level debug view.</summary>
        private void BuildGeneralCategoryRows(FusedCharacterPlayer p)
        {
            AddSection("Localization");
            AddButton("Re-align Immersal map", () => Controller()?.RealignToImmersal());
            AddButton("Retarget & re-align Immersal", () => Controller()?.RetargetAndRealignImmersal());

            AddSection("Debug view");
            var dbg = EnsureDebugVisuals();
            AddToggle("Skeleton & AR debug overlay", () => dbg.VisualsVisible, v => dbg.SetVisible(v));
        }

        private string GetActiveMapStatusText()
        {
            if (mapSwitcher != null && mapSwitcher.IsSwitching)
                return $"Loading map {GetActiveMapIdLabel()}…";
            return $"Active map: {GetActiveMapIdLabel()}";
        }

        private string GetActiveMapIdLabel()
        {
            if (mapSwitcher != null && mapSwitcher.ActiveMapId > 0)
                return mapSwitcher.ActiveMapId.ToString();
            var c = Controller();
            string fromController = c != null ? c.GetActiveMapId() : "";
            return string.IsNullOrWhiteSpace(fromController) ? "—" : fromController.Trim();
        }

        private void SyncMapInput()
        {
            if (mapIdInput == null || mapIdInput.isFocused)
                return;

            if (mapSwitcher != null)
            {
                int id = mapSwitcher.ActiveMapId > 0
                    ? mapSwitcher.ActiveMapId
                    : (mapSwitcher.PendingMapId > 0 ? mapSwitcher.PendingMapId : -1);
                if (id > 0)
                {
                    mapIdInput.text = id.ToString();
                    return;
                }
            }

            var c = Controller();
            string fromController = c != null ? c.GetActiveMapId() : "";
            if (!string.IsNullOrWhiteSpace(fromController))
                mapIdInput.text = fromController.Trim();
        }

        private void OnMapLoadClicked()
        {
            if (!EnsureMapSwitcher() || mapIdInput == null)
            {
                SetMapStatus("Map switcher not ready", error: true);
                return;
            }

            mapIdInput.DeactivateInputField();
            if (TouchScreenKeyboard.isSupported)
                TouchScreenKeyboard.hideInput = true;

            string idText = mapIdInput.text?.Trim();
            if (string.IsNullOrEmpty(idText))
            {
                SetMapStatus("Enter a numeric map ID first", error: true);
                return;
            }

            var c = Controller();
            if (c != null && c.IsRecording)
            {
                SetMapStatus("Stop recording before switching maps", error: true);
                return;
            }

            if (c != null && c.IsPlaying)
                c.StopPlayback();

            SetMapStatus($"Loading map {idText}…", error: false);
            mapSwitcher.SwitchToMapFromInput(idText);
        }

        private void OnMapStatusChanged(string message)
        {
            SetMapStatus(message, mapSwitcher != null &&
                mapSwitcher.LastStatusSeverity == ImmersalMapSwitcher.StatusSeverity.Error);
            if (mapStatusLabel != null && mapSwitcher != null)
            {
                switch (mapSwitcher.LastStatusSeverity)
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
        }

        private void OnMapSwitched(int mapId)
        {
            SyncMapInput();
            RefreshMapStatusLabel();
            var c = Controller();
            if (c != null)
                c.LoadLatestRecording();
            if (activeCategory == TuningCategory.General)
                RebuildRows();
        }

        private void SetMapStatus(string message, bool error)
        {
            if (mapStatusLabel != null)
            {
                mapStatusLabel.text = message;
                mapStatusLabel.color = error ? UITokens.Danger : UITokens.OnSurface;
            }
        }

        /// <summary>Wall constraint test plan: slab, calibration, contact lock, debug planes.</summary>
        private void BuildWallCategoryRows(FusedCharacterPlayer p)
        {
            var viz = EnsureVisualizer();

            AddSection("Wall plane (blue quad)");
            AddButton("Calibrate wall to AR plane (front)", () =>
            {
                EnableWallPlanePreview();
                p.CalibrateWallFromArPlane();
            });
            AddButton("Calibrate wall to climb now", () =>
            {
                EnableWallPlanePreview();
                p.AutoCalibrateWallDepth();
            });
            AddToggle("Debug overlay (planes + HUD)", () => viz.ShowVisuals, v => viz.ShowVisuals = v);
            AddToggle("Blue / green planes", () => viz.ShowPlanes, v => viz.ShowPlanes = v);
            AddToggle("Status HUD", () => viz.ShowStatusHud, v => viz.ShowStatusHud = v);
            AddToggle("Hold contact markers", () => viz.ShowContactMarkers, v => viz.ShowContactMarkers = v);
            if (p.SurfaceProbe != null)
                AddModeToggle("Wall source",
                    () => p.SurfaceProbe.WallSource == ARSurfaceProbe.WallSourceMode.ARVerticalPlane ? "AR plane" : "RouteRoot",
                    () => p.SurfaceProbe.WallSource =
                        p.SurfaceProbe.WallSource == ARSurfaceProbe.WallSourceMode.ARVerticalPlane
                            ? ARSurfaceProbe.WallSourceMode.RouteRootPlane
                            : ARSurfaceProbe.WallSourceMode.ARVerticalPlane);
            AddToggle("Auto-calibrate wall on play", () => p.AutoCalibrateWallOnPlay, v => p.AutoCalibrateWallOnPlay = v);

            AddSection("Depth slab");
            AddToggle("Enable wall projection", () => p.EnableWallProjection, v => p.EnableWallProjection = v);
            AddToggle("Depth slab clamp", () => p.WallProjectionSettingsLive.enableSlabClamp,
                v => { var s = p.WallProjectionSettingsLive; s.enableSlabClamp = v; p.WallProjectionSettingsLive = s; });
            AddSlider("Max body depth (m)", 0.2f, 0.9f, () => p.WallProjectionSettingsLive.maxBodyDepth,
                v => { var s = p.WallProjectionSettingsLive; s.maxBodyDepth = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Wall surface depth (m)", 0f, 0.15f, () => p.WallProjectionSettingsLive.wallSurfaceDepth,
                v => { var s = p.WallProjectionSettingsLive; s.wallSurfaceDepth = v; p.WallProjectionSettingsLive = s; }, "0.000");
            AddSlider("Wall depth offset (m)", -3f, 3f, () => p.WallProjectionSettingsLive.wallDepthOffset,
                v =>
                {
                    var s = p.WallProjectionSettingsLive; s.wallDepthOffset = v; p.WallProjectionSettingsLive = s;
                    if (p.SurfaceProbe != null) p.SurfaceProbe.WallLocalZOffset = v;
                }, "0.000");

            AddSection("Walk-away release");
            AddToggle("Release when off wall", () => p.WallProjectionSettingsLive.enableWalkAwayRelease,
                v => { var s = p.WallProjectionSettingsLive; s.enableWalkAwayRelease = v; p.WallProjectionSettingsLive = s; });
            AddSlider("Release distance (m)", 0.3f, 2f, () => p.WallProjectionSettingsLive.wallReleaseDepth,
                v => { var s = p.WallProjectionSettingsLive; s.wallReleaseDepth = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Re-engage distance (m)", 0.1f, 1.5f, () => p.WallProjectionSettingsLive.wallReengageDepth,
                v => { var s = p.WallProjectionSettingsLive; s.wallReengageDepth = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddToggle("Don't pin while on floor", () => p.WallProjectionSettingsLive.enableFloorStandRelease,
                v => { var s = p.WallProjectionSettingsLive; s.enableFloorStandRelease = v; p.WallProjectionSettingsLive = s; });
            AddSlider("Wall engage ease (s)", 0f, 1f, () => p.WallProjectionSettingsLive.wallEngageEaseSeconds,
                v => { var s = p.WallProjectionSettingsLive; s.wallEngageEaseSeconds = v; p.WallProjectionSettingsLive = s; }, "0.00");

            AddSection("Snap onto holds (pull-on)");
            AddToggle("Snap hands/feet onto holds", () => p.WallProjectionSettingsLive.enableContactLock,
                v => { var s = p.WallProjectionSettingsLive; s.enableContactLock = v; p.WallProjectionSettingsLive = s; });
            AddSlider("Snap depth band (m)", 0.04f, 0.4f, () => p.WallProjectionSettingsLive.contactDepthBand,
                v => { var s = p.WallProjectionSettingsLive; s.contactDepthBand = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Snap radius (m)", 0.03f, 0.4f, () => p.WallProjectionSettingsLive.holdSnapRadius,
                v => { var s = p.WallProjectionSettingsLive; s.holdSnapRadius = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Snap ease (s)", 0.02f, 0.5f, () => p.WallProjectionSettingsLive.contactEaseSeconds,
                v => { var s = p.WallProjectionSettingsLive; s.contactEaseSeconds = v; p.WallProjectionSettingsLive = s; }, "0.00");

            AddSection("Holds (offline pre-detection)");
            AddToggle("Auto-detect holds", () => p.WallProjectionSettingsLive.enableHoldDetection,
                v => { var s = p.WallProjectionSettingsLive; s.enableHoldDetection = v; p.WallProjectionSettingsLive = s; });
            AddToggle("Show holds overlay", () => viz.ShowHolds, v => viz.ShowHolds = v);
            AddSlider("Detect stillness (m/s)", 0.02f, 0.3f, () => p.WallProjectionSettingsLive.contactStillnessSpeed,
                v => { var s = p.WallProjectionSettingsLive; s.contactStillnessSpeed = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Detect release (m/s)", 0.05f, 0.6f, () => p.WallProjectionSettingsLive.contactReleaseSpeed,
                v => { var s = p.WallProjectionSettingsLive; s.contactReleaseSpeed = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Hold dwell (s)", 0.1f, 2f, () => p.WallProjectionSettingsLive.holdDwellSeconds,
                v => { var s = p.WallProjectionSettingsLive; s.holdDwellSeconds = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Merge radius (m)", 0.03f, 0.4f, () => p.WallProjectionSettingsLive.holdMergeRadius,
                v => { var s = p.WallProjectionSettingsLive; s.holdMergeRadius = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Detect wall range (m)", 0.08f, 0.8f, () => p.WallProjectionSettingsLive.holdDetectionDepthBand,
                v => { var s = p.WallProjectionSettingsLive; s.holdDetectionDepthBand = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddSlider("Floor exclusion (m)", 0f, 0.5f, () => p.WallProjectionSettingsLive.holdFloorExclusionBand,
                v => { var s = p.WallProjectionSettingsLive; s.holdFloorExclusionBand = v; p.WallProjectionSettingsLive = s; }, "0.00");
            AddStatusLabel($"Detected holds: {p.HoldMap?.Count ?? 0}");
            AddButton("Regenerate holds from recording", () =>
            {
                p.RegenerateHoldsFromRecording();
                RebuildRows();
            });
            AddButton("Clear holds (this map)", () =>
            {
                p.ClearHolds();
                RebuildRows();
            });
        }

        /// <summary>Penetration, floor gate, wall IK (test plan steps 4–5).</summary>
        private void BuildIkFloorCategoryRows(FusedCharacterPlayer p)
        {
            AddSection("Penetration fix");
            AddToggle("Enable penetration fix", () => p.EnablePenetrationFix, v => p.EnablePenetrationFix = v);
            AddToggle("Floor fix (feet on floor)", () => p.PenetrationSettingsLive.enableFloorFix,
                v => { var s = p.PenetrationSettingsLive; s.enableFloorFix = v; p.PenetrationSettingsLive = s; });
            AddSlider("Floor contact band (m)", 0.02f, 0.4f, () => p.PenetrationSettingsLive.floorContactBand,
                v => { var s = p.PenetrationSettingsLive; s.floorContactBand = v; p.PenetrationSettingsLive = s; }, "0.00");
            AddSlider("Max standing hip height (m)", 0f, 2.5f, () => p.PenetrationSettingsLive.maxStandingHipHeightAboveFloor,
                v => { var s = p.PenetrationSettingsLive; s.maxStandingHipHeightAboveFloor = v; p.PenetrationSettingsLive = s; }, "0.0");
            AddSlider("Max floor drop (m)", 0f, 1f, () => p.PenetrationSettingsLive.maxFloorSnapMeters,
                v => { var s = p.PenetrationSettingsLive; s.maxFloorSnapMeters = v; p.PenetrationSettingsLive = s; }, "0.00");

            AddSection("Wall IK");
            AddToggle("Wall hand IK", () => p.PenetrationSettingsLive.enableWallHandIK,
                v => { var s = p.PenetrationSettingsLive; s.enableWallHandIK = v; p.PenetrationSettingsLive = s; });
            AddToggle("Wall foot IK", () => p.PenetrationSettingsLive.enableWallFootIK,
                v => { var s = p.PenetrationSettingsLive; s.enableWallFootIK = v; p.PenetrationSettingsLive = s; });
            AddToggle("Whole-body wall push", () => p.PenetrationSettingsLive.enableWholeBodyPush,
                v => { var s = p.PenetrationSettingsLive; s.enableWholeBodyPush = v; p.PenetrationSettingsLive = s; });
            AddSlider("Min whole-body depth (m)", 0.01f, 0.15f, () => p.PenetrationSettingsLive.minWholeBodyPenetration,
                v => { var s = p.PenetrationSettingsLive; s.minWholeBodyPenetration = v; p.PenetrationSettingsLive = s; }, "0.000");
            AddSlider("Whole-body contact fraction", 0f, 1f, () => p.PenetrationSettingsLive.wholeBodyPenetrationFraction,
                v => { var s = p.PenetrationSettingsLive; s.wholeBodyPenetrationFraction = v; p.PenetrationSettingsLive = s; }, "0.00");
            AddSlider("Max whole-body push (m)", 0.02f, 0.3f, () => p.PenetrationSettingsLive.maxWholeBodyPushMeters,
                v => { var s = p.PenetrationSettingsLive; s.maxWholeBodyPushMeters = v; p.PenetrationSettingsLive = s; }, "0.000");
            AddSlider("Max IK weight", 0f, 1f, () => p.PenetrationSettingsLive.maxIkWeight,
                v => { var s = p.PenetrationSettingsLive; s.maxIkWeight = v; p.PenetrationSettingsLive = s; }, "0.00");
            AddSlider("Penetration for full weight (m)", 0.02f, 0.2f, () => p.PenetrationSettingsLive.penetrationForFullWeight,
                v => { var s = p.PenetrationSettingsLive; s.penetrationForFullWeight = v; p.PenetrationSettingsLive = s; }, "0.000");
            AddToggle("Skip during jump", () => p.PenetrationSettingsLive.skipDuringJump,
                v => { var s = p.PenetrationSettingsLive; s.skipDuringJump = v; p.PenetrationSettingsLive = s; });
            AddToggle("Debug draw", () => p.PenetrationSettingsLive.debugDraw,
                v => { var s = p.PenetrationSettingsLive; s.debugDraw = v; p.PenetrationSettingsLive = s; });
        }

        /// <summary>Jitter smoothing, glitch rejection, facing.</summary>
        private void BuildPoseCategoryRows(FusedCharacterPlayer p)
        {
            AddSection("Facing");
            AddToggle("Move-driven facing", () => p.AnchorSettingsLive.moveDrivenFacing,
                v => { var s = p.AnchorSettingsLive; s.moveDrivenFacing = v; p.AnchorSettingsLive = s; });
            AddSlider("Facing correction (s)", 0.2f, 5f, () => p.AnchorSettingsLive.facingCorrectionSeconds,
                v => { var s = p.AnchorSettingsLive; s.facingCorrectionSeconds = v; p.AnchorSettingsLive = s; }, "0.0");
            AddToggle("Invert facing (180\u00b0)", () => p.InvertFacing, p.SetInvertFacing);

            AddSection("Smoothing (jitter)");
            AddToggle("Enable pose smoothing", () => p.EnablePoseSmoothing, v => p.EnablePoseSmoothing = v);
            AddSlider("Min cutoff (Hz)", 0.2f, 5f, () => p.PostProcessSettings.minCutoff,
                v => { var s = p.PostProcessSettings; s.minCutoff = v; p.PostProcessSettings = s; }, "0.00");
            AddSlider("Beta (anti-lag)", 0f, 0.3f, () => p.PostProcessSettings.beta,
                v => { var s = p.PostProcessSettings; s.beta = v; p.PostProcessSettings = s; }, "0.000");
            AddToggle("Smooth root translation", () => p.PostProcessSettings.smoothRootTranslation,
                v => { var s = p.PostProcessSettings; s.smoothRootTranslation = v; p.PostProcessSettings = s; });
            AddSlider("Jump velocity threshold", 0.5f, 4f, () => p.PostProcessSettings.jumpVelocityThreshold,
                v => { var s = p.PostProcessSettings; s.jumpVelocityThreshold = v; p.PostProcessSettings = s; }, "0.0");
            AddSlider("Jump beta scale", 1f, 16f, () => p.PostProcessSettings.jumpBetaScale,
                v => { var s = p.PostProcessSettings; s.jumpBetaScale = v; p.PostProcessSettings = s; }, "0.0");

            AddSection("Glitch guard");
            AddToggle("Enable glitch guard", () => p.PostProcessSettings.enableGlitchGuard,
                v => { var s = p.PostProcessSettings; s.enableGlitchGuard = v; p.PostProcessSettings = s; });
            AddSlider("Max joint speed (m/s)", 4f, 30f, () => p.PostProcessSettings.maxJointSpeed,
                v => { var s = p.PostProcessSettings; s.maxJointSpeed = v; p.PostProcessSettings = s; }, "0.0");
            AddSlider("Bone-length tolerance", 0.1f, 1f, () => p.PostProcessSettings.boneLengthTolerance,
                v => { var s = p.PostProcessSettings; s.boneLengthTolerance = v; p.PostProcessSettings = s; }, "0.00");

            // The glitch guard above only cleans the POSE (joints vs the pelvis). This caps how fast the whole body's
            // WORLD placement can travel, so the final GLB/procedural render can never lurch from one spot to another.
            AddSection("Root motion guard (no teleport)");
            AddToggle("Enable root motion guard", () => p.EnableRootMotionGuard, v => p.EnableRootMotionGuard = v);
            AddSlider("Max root speed (m/s)", 1f, 20f, () => p.MaxRootSpeed, v => p.MaxRootSpeed = v, "0.0");
            AddSlider("Teleport snap distance (m)", 0.3f, 3f, () => p.RootTeleportSnapDistance,
                v => p.RootTeleportSnapDistance = v, "0.00");
            AddSlider("Max turn speed (deg/s)", 90f, 1080f, () => p.MaxRootTurnSpeed, v => p.MaxRootTurnSpeed = v, "0");
            AddSlider("Turn snap (deg)", 30f, 180f, () => p.RootTurnSnapDegrees, v => p.RootTurnSnapDegrees = v, "0");
        }

        /// <summary>Movement, anchor, fit, face, fusion bake.</summary>
        private void BuildPlaybackCategoryRows(FusedCharacterPlayer p)
        {
            AddSection("Position / trajectory");
            AddToggle("Use Move AI movement (test)",
                () => p.PlaybackAnchorMode == FusedPoseSolver.AnchorMode.FollowMoveGlbRoot,
                v => p.PlaybackAnchorMode = v
                    ? FusedPoseSolver.AnchorMode.FollowMoveGlbRoot
                    : FusedPoseSolver.AnchorMode.FollowBakedRoot);
            AddButton("Re-align Move movement now", () => p.RealignMoveMovement());
            AddToggle("Auto re-align (Move test)", () => p.AnchorSettingsLive.moveAutoRealign,
                v => { var s = p.AnchorSettingsLive; s.moveAutoRealign = v; p.AnchorSettingsLive = s; });
            AddSlider("Re-align drift threshold (m)", 0.05f, 0.6f, () => p.AnchorSettingsLive.moveRealignDriftThreshold,
                v => { var s = p.AnchorSettingsLive; s.moveRealignDriftThreshold = v; p.AnchorSettingsLive = s; }, "0.00");
            AddSlider("Re-align ease (s)", 0.05f, 1.5f, () => p.AnchorSettingsLive.moveRealignEaseSeconds,
                v => { var s = p.AnchorSettingsLive; s.moveRealignEaseSeconds = v; p.AnchorSettingsLive = s; }, "0.00");
            AddModeToggle("World position source",
                () => p.PlaybackAnchorMode == FusedPoseSolver.AnchorMode.FollowBakedRoot ? "Baked" : "Live",
                () =>
                {
                    p.PlaybackAnchorMode = p.PlaybackAnchorMode == FusedPoseSolver.AnchorMode.FollowBakedRoot
                        ? FusedPoseSolver.AnchorMode.FollowArkit
                        : FusedPoseSolver.AnchorMode.FollowBakedRoot;
                });

            AddSection("Anchor (live AR only)");
            AddSlider("Stillness velocity (m/s)", 0f, 0.5f, () => p.AnchorSettingsLive.stillnessVelocity,
                v => { var s = p.AnchorSettingsLive; s.stillnessVelocity = v; p.AnchorSettingsLive = s; }, "0.00");
            AddSlider("Full-motion velocity (m/s)", 0.05f, 1f, () => p.AnchorSettingsLive.fullMotionVelocity,
                v => { var s = p.AnchorSettingsLive; s.fullMotionVelocity = v; p.AnchorSettingsLive = s; }, "0.00");
            AddSlider("Follow seconds", 0.02f, 0.5f, () => p.AnchorSettingsLive.followSeconds,
                v => { var s = p.AnchorSettingsLive; s.followSeconds = v; p.AnchorSettingsLive = s; }, "0.00");

            AddSection("Fit & playback");
            AddSlider("Character fit scale", 0.5f, 1.5f, () => p.SkeletonFitScale, v => p.SkeletonFitScale = v, "0.00");
            AddSlider("Playback speed", 0.25f, 2f, () => p.PlaybackSpeed, v => p.PlaybackSpeed = v, "0.00");

            AddFaceRows();

            var co = Coordinator();
            if (co != null)
            {
                AddSection("Fusion bake (Rebake to apply)");
                AddSlider("Horizontal weight (X)", 0f, 1f, () => co.BakeSettings.axisWeights.x,
                    v => { var s = co.BakeSettings; s.axisWeights.x = v; co.BakeSettings = s; }, "0.00");
                AddSlider("Vertical weight (Y)", 0f, 1f, () => co.BakeSettings.axisWeights.y,
                    v => { var s = co.BakeSettings; s.axisWeights.y = v; co.BakeSettings = s; }, "0.00");
                AddSlider("Depth weight (Z)", 0f, 1f, () => co.BakeSettings.axisWeights.z,
                    v => { var s = co.BakeSettings; s.axisWeights.z = v; co.BakeSettings = s; }, "0.00");
                AddSlider("Smoothing tau (s)", 0.05f, 1.5f, () => co.BakeSettings.smoothingTau,
                    v => { var s = co.BakeSettings; s.smoothingTau = v; co.BakeSettings = s; }, "0.00");
                AddSlider("Outlier reject (m)", 0.1f, 1.5f, () => co.BakeSettings.outlierMeters,
                    v => { var s = co.BakeSettings; s.outlierMeters = v; co.BakeSettings = s; }, "0.00");
                AddButton("Rebake from latest (no API)", OnRebakeClicked);
                rebakeStatus = AddStatusLabel(co.LastStatus);
            }
        }

        /// <summary>
        /// Eyes + mouth live controls. The eye blink/movement and the new mouth driver are independent
        /// MonoBehaviours layered on top of the body retarget; expose their main knobs here so the smile/talk
        /// test sits right alongside the eye-movement and blinking settings.
        /// </summary>
        private void AddFaceRows()
        {
            var m = Mouth();
            var blink = EyeBlink();
            var move = EyeMovement();
            if (m == null && blink == null && move == null) return;

            AddSection("Face (eyes / mouth)");

            if (blink != null)
                AddToggle("Eye blinking", () => blink.enabled, v => blink.enabled = v);
            if (move != null)
                AddToggle("Eye movement", () => move.enabled, v => move.enabled = v);

            if (m != null)
            {
                AddToggle("Mouth (smile/talk)", () => m.enabled, v => m.enabled = v);
                AddSlider("Smile amount", 0f, 1f, () => m.SmileAmount, v => m.SetSmile(v), "0.00");
                AddButton(m.IsTalking ? "Stop talking" : "Talk (test)", () =>
                {
                    if (m.IsTalking) m.StopTalking(); else m.TriggerTalk();
                });
            }
        }

        private void OnRebakeClicked()
        {
            var c = Controller();
            if (c == null)
            {
                SetRebakeStatus("No BodyTrackingController in scene");
                return;
            }
            if (!c.CanRebake)
            {
                SetRebakeStatus("Nothing to rebake (need a recording with a fused asset)");
                return;
            }
            SetRebakeStatus("Rebaking…");
            c.RebakeLatest();
            var co = Coordinator();
            if (co != null) SetRebakeStatus(co.LastStatus);
        }

        private void SetRebakeStatus(string text)
        {
            if (rebakeStatus != null) rebakeStatus.text = text;
        }

        private static void ConfigureRowLabel(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            tmp.extraPadding = false;
            tmp.margin = Vector4.zero;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.enableWordWrapping = true;
        }

        private static void ConfigureCompactPillLabel(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            tmp.fontSize = LabelFontSize - 1f;
            tmp.extraPadding = false;
            tmp.margin = Vector4.zero;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.enableWordWrapping = true;
            UIFactory.Stretch(tmp.rectTransform, UITokens.Space4);
        }

        /// <summary>Keep every settings row inside the scroll viewport width.</summary>
        private static void ApplyRowWidthConstraint(RectTransform row)
        {
            var le = row.GetComponent<LayoutElement>();
            if (le == null) le = row.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 0f;
            le.flexibleWidth = 1f;
        }

        private static VerticalLayoutGroup AddStackedRow(RectTransform row, float minHeight)
        {
            ApplyRowWidthConstraint(row);
            var le = row.GetComponent<LayoutElement>();
            le.minHeight = minHeight;
            var vlg = row.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = UITokens.Space4;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return vlg;
        }

        private static TextMeshProUGUI AddWrappingLabel(Transform parent, string text, float fontSize, Color color)
        {
            var lbl = UIFactory.CreateText("Label", parent, text, fontSize, color, TextAlignmentOptions.Left);
            ConfigureRowLabel(lbl);
            var le = lbl.gameObject.AddComponent<LayoutElement>();
            le.minWidth = 0f;
            le.flexibleWidth = 1f;
            var fitter = lbl.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return lbl;
        }

        private static RectTransform AddRightAlignedControlRow(Transform parent)
        {
            var bottom = UIFactory.CreateRect("Controls", parent);
            var bottomLe = bottom.gameObject.AddComponent<LayoutElement>();
            bottomLe.minHeight = ControlRowHeight;
            bottomLe.preferredHeight = ControlRowHeight;
            bottomLe.minWidth = 0f;
            bottomLe.flexibleWidth = 1f;
            var hlg = bottom.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.spacing = UITokens.Space4;
            hlg.childAlignment = TextAnchor.MiddleRight;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            return bottom;
        }

        private static LayoutElement AddFixedWidthControl(GameObject go, float width, float height)
        {
            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;
            le.flexibleWidth = 0f;
            le.preferredHeight = height;
            le.minHeight = height;
            return le;
        }

        private static Button AddFixedPill(Transform parent, string label, float width, float height, bool ghost)
        {
            var btn = UIFactory.CreatePillButton("Pill", parent, label, ghost);
            AddFixedWidthControl(btn.gameObject, width, height);
            var rect = (RectTransform)btn.transform;
            rect.sizeDelta = new Vector2(width, height);
            ConfigureCompactPillLabel(btn.GetComponentInChildren<TextMeshProUGUI>());
            return btn;
        }

        private void AddSection(string title)
        {
            var row = UIFactory.CreateRect("Section_" + title, content);
            ApplyRowWidthConstraint(row);
            var le = row.GetComponent<LayoutElement>();
            le.minHeight = 22f;
            var t = UIFactory.CreateBoldText("Label", row, title.ToUpperInvariant(), SectionFontSize,
                UITokens.Primary, TextAlignmentOptions.BottomLeft);
            ConfigureRowLabel(t);
            UIFactory.Stretch(t.rectTransform);
            var fitter = t.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void AddSlider(string label, float min, float max, Func<float> getter, Action<float> setter, string fmt)
        {
            var row = UIFactory.CreateRect("Row_" + label, content);
            AddStackedRow(row, SliderRowMinHeight);

            AddWrappingLabel(row, label, LabelFontSize, UITokens.OnSurface);

            var bottom = UIFactory.CreateRect("Bottom", row);
            var bottomLe = bottom.gameObject.AddComponent<LayoutElement>();
            bottomLe.minHeight = ControlRowHeight;
            bottomLe.preferredHeight = ControlRowHeight;
            bottomLe.minWidth = 0f;
            bottomLe.flexibleWidth = 1f;
            var bottomLayout = bottom.gameObject.AddComponent<HorizontalLayoutGroup>();
            bottomLayout.spacing = UITokens.Space4;
            int handlePad = Mathf.CeilToInt(UITokens.ScrubHandleDiameter * 0.5f);
            bottomLayout.padding = new RectOffset(handlePad, handlePad, 0, 0);
            bottomLayout.childControlWidth = true;
            bottomLayout.childControlHeight = true;
            bottomLayout.childForceExpandWidth = false;
            bottomLayout.childForceExpandHeight = true;
            bottomLayout.childAlignment = TextAnchor.MiddleLeft;

            var slider = UIFactory.CreateScrubSlider("Slider", bottom);
            var sliderLe = slider.gameObject.AddComponent<LayoutElement>();
            sliderLe.flexibleWidth = 1f;
            sliderLe.minWidth = 32f;
            sliderLe.preferredHeight = UITokens.ScrubHandleDiameter;
            sliderLe.minHeight = UITokens.ScrubHandleDiameter;

            var valText = UIFactory.CreateText("Value", bottom, getter().ToString(fmt), ValueFontSize,
                UITokens.Muted, TextAlignmentOptions.Right);
            valText.extraPadding = false;
            valText.margin = Vector4.zero;
            valText.textWrappingMode = TextWrappingModes.NoWrap;
            valText.overflowMode = TextOverflowModes.Ellipsis;
            AddFixedWidthControl(valText.gameObject, ValueColumnWidth, ControlRowHeight);

            slider.minValue = min;
            slider.maxValue = max;
            slider.value = Mathf.Clamp(getter(), min, max);
            slider.onValueChanged.AddListener(v =>
            {
                setter(v);
                valText.text = v.ToString(fmt);
                PropagateToOverlays();
            });
        }

        private void AddToggle(string label, Func<bool> getter, Action<bool> setter)
        {
            var row = UIFactory.CreateRect("Row_" + label, content);
            AddStackedRow(row, ControlRowHeight + 4f);
            AddWrappingLabel(row, label, LabelFontSize, UITokens.OnSurface);

            var controlRow = AddRightAlignedControlRow(row);
            var btn = AddFixedPill(controlRow, getter() ? "On" : "Off", ToggleButtonWidth, ControlRowHeight, ghost: true);
            var btnText = btn.GetComponentInChildren<TextMeshProUGUI>();
            var btnImg = btn.GetComponent<Image>();
            ApplyToggleVisual(btnImg, btnText, getter());

            btn.onClick.AddListener(() =>
            {
                bool nv = !getter();
                setter(nv);
                ApplyToggleVisual(btnImg, btnText, nv);
                PropagateToOverlays();
            });
        }

        private static void ApplyToggleVisual(Image img, TextMeshProUGUI text, bool on)
        {
            if (text != null)
            {
                text.text = on ? "On" : "Off";
                text.color = on ? Color.white : UITokens.Muted;
            }
            if (img != null)
                img.color = on ? UITokens.Primary : UITokens.SurfaceElevated;
        }

        private void AddModeToggle(string label, Func<string> stateText, Action cycle)
        {
            var row = UIFactory.CreateRect("Row_" + label, content);
            AddStackedRow(row, ControlRowHeight + 4f);
            AddWrappingLabel(row, label, LabelFontSize, UITokens.OnSurface);

            var controlRow = AddRightAlignedControlRow(row);
            var btn = AddFixedPill(controlRow, stateText(), ModeButtonWidth, ControlRowHeight, ghost: false);
            var btnText = btn.GetComponentInChildren<TextMeshProUGUI>();
            btn.onClick.AddListener(() =>
            {
                cycle();
                if (btnText != null) btnText.text = stateText();
                PropagateToOverlays();
            });
        }

        /// <summary>A full-width action button (label rendered inside the pill, not clipped to zero width).</summary>
        private void AddButton(string label, Action onClick)
        {
            const float buttonHeight = ControlRowHeight + 8f;

            var row = UIFactory.CreateRect("Btn_" + label, content);
            AddStackedRow(row, buttonHeight + UITokens.Space4);

            var btn = UIFactory.CreatePillButton("Action", row, label, ghost: false);
            var btnText = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (btnText != null)
            {
                btnText.fontSize = LabelFontSize - 1f;
                btnText.extraPadding = false;
                btnText.margin = Vector4.zero;
                btnText.textWrappingMode = TextWrappingModes.Normal;
                btnText.overflowMode = TextOverflowModes.Ellipsis;
                btnText.enableWordWrapping = true;
                btnText.alignment = TextAlignmentOptions.Center;
                UIFactory.Stretch(btnText.rectTransform, UITokens.Space8);
            }

            var btnLe = btn.gameObject.AddComponent<LayoutElement>();
            btnLe.minWidth = 0f;
            btnLe.flexibleWidth = 1f;
            btnLe.preferredHeight = buttonHeight;
            btnLe.minHeight = buttonHeight;
            var btnRect = (RectTransform)btn.transform;
            btnRect.sizeDelta = new Vector2(0f, buttonHeight);

            btn.onClick.AddListener(() =>
            {
                onClick?.Invoke();
                PropagateToOverlays();
            });
        }

        /// <summary>A wrapping status line used under the Rebake button.</summary>
        private TextMeshProUGUI AddStatusLabel(string initial)
        {
            var row = UIFactory.CreateRect("Status", content);
            ApplyRowWidthConstraint(row);
            var le = row.GetComponent<LayoutElement>();
            le.minHeight = 28f;
            var t = UIFactory.CreateText("Label", row, initial ?? "", ValueFontSize, UITokens.Muted,
                TextAlignmentOptions.Left);
            ConfigureRowLabel(t);
            UIFactory.Stretch(t.rectTransform);
            var fitter = t.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return t;
        }

        // ============================================================================================
        // SHARED CANVAS / EVENT SYSTEM
        // ============================================================================================

        private Canvas EnsureCanvas()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
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
            if (UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
                new GameObject("EventSystem",
                    typeof(UnityEngine.EventSystems.EventSystem),
                    typeof(UnityEngine.EventSystems.StandaloneInputModule));
        }
    }
}
