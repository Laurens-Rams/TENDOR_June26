using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using BodyTracking.Animation;
using BodyTracking.Playback;
using BodyTracking.Spatial;

namespace BodyTracking.UI
{
    /// <summary>
    /// Interactive list of all recordings for the currently loaded map. A scrollable bottom sheet (so the AR
    /// scene + characters stay visible above it) showing:
    ///   - a clickable map-id header that reveals the relocated map-switch input + Load button, and
    ///   - one row per recording with a short date label and an on/off toggle.
    ///
    /// Toggling a recording on plays it; toggling a second one on plays both overlapped (via
    /// <see cref="RecordingSelection"/> + the multi-recording engine in <see cref="BodyTrackingController"/>).
    /// This is the home for future per-recording controls — everything routes through the selection model.
    /// </summary>
    public class RecordingsMenuUI : MonoBehaviour
    {
        private const string RootName = "RecordingsUIRoot";

        public BodyTrackingController controller;

        private RectTransform uiRoot;
        private RectTransform content;
        private Button backButton;
        private Button mapHeaderButton;
        private TextMeshProUGUI mapHeaderLabel;
        private GameObject mapPanel;
        private TMP_InputField mapIdInput;
        private Button mapLoadButton;
        private TextMeshProUGUI mapLoadLabel;
        private TextMeshProUGUI mapStatusLabel;
        private bool mapPanelVisible;

        private ImmersalMapSwitcher mapSwitcher;
        private CharacterSwitcher characterSwitcher;
        private bool initialized;
        private bool isVisible;

        private static readonly Color SheetColor = new Color(0.09f, 0.09f, 0.11f, 0.9f);
        private const float HeaderFontSize = 16f;
        private const float RowHeight = 38f;
        private const float ToggleWidth = 36f;
        private const float CharButtonWidth = 24f;
        private const float RowControlHeight = 22f;
        private const float RowControlFontSize = 10f;
        private const float RowSidePadding = 8f;
        private const float CharPickerSpacing = 2f;
        private const float LabelToControlsSpacing = 4f;

        public Button BackButton => backButton;

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
                RefreshFromStorage();
                RebuildRows();
                UpdateMapHeaderLabel();
            }
        }

        private BodyTrackingController Controller()
        {
            if (controller == null)
                controller = Object.FindFirstObjectByType<BodyTrackingController>();
            return controller;
        }

        private CharacterSwitcher Switcher()
        {
            if (characterSwitcher == null)
                characterSwitcher = Object.FindFirstObjectByType<CharacterSwitcher>(FindObjectsInactive.Include);
            return characterSwitcher;
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            Controller();
            EnsureMapSwitcher();
            BuildShell();
            initialized = true;
        }

        private bool EnsureMapSwitcher()
        {
            if (mapSwitcher != null)
                return true;

            mapSwitcher = Object.FindFirstObjectByType<ImmersalMapSwitcher>();
            if (mapSwitcher == null)
            {
                var c = Controller();
                RouteRootManager rrm = c != null ? c.routeRootManager : null;
                if (rrm == null)
                    rrm = Object.FindFirstObjectByType<RouteRootManager>();
                GameObject host = rrm != null ? rrm.gameObject : (c != null ? c.gameObject : null);
                if (host != null)
                {
                    mapSwitcher = host.GetComponent<ImmersalMapSwitcher>();
                    if (mapSwitcher == null)
                        mapSwitcher = host.AddComponent<ImmersalMapSwitcher>();
                }
            }

            if (mapSwitcher != null)
            {
                mapSwitcher.OnStatusChanged -= OnMapStatusChanged;
                mapSwitcher.OnMapSwitched -= OnMapSwitched;
                mapSwitcher.OnStatusChanged += OnMapStatusChanged;
                mapSwitcher.OnMapSwitched += OnMapSwitched;
            }
            return mapSwitcher != null;
        }

        // ============================================================================================
        // SHELL
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

            // Bottom sheet covering the lower ~64% so the characters stay visible above.
            var sheet = UIFactory.CreateRect("Sheet", uiRoot);
            sheet.anchorMin = new Vector2(0f, 0f);
            sheet.anchorMax = new Vector2(1f, 0.64f);
            sheet.offsetMin = new Vector2(UITokens.Space16, UITokens.Space8);
            sheet.offsetMax = new Vector2(-UITokens.Space16, 0f);
            var sheetBg = sheet.gameObject.AddComponent<Image>();
            sheetBg.sprite = UIFactory.RoundedSprite(UITokens.RadiusLarge);
            sheetBg.type = Image.Type.Sliced;
            sheetBg.color = SheetColor;
            sheetBg.raycastTarget = true;

            // Clickable map-id header (top-left).
            mapHeaderButton = UIFactory.CreatePillButton("MapHeader", sheet, "Map —", ghost: true);
            var mh = (RectTransform)mapHeaderButton.transform;
            mh.anchorMin = new Vector2(0f, 1f);
            mh.anchorMax = new Vector2(0f, 1f);
            mh.pivot = new Vector2(0f, 1f);
            mh.sizeDelta = new Vector2(150f, 30f);
            mh.anchoredPosition = new Vector2(UITokens.Space12, -UITokens.Space8);
            mapHeaderLabel = mapHeaderButton.GetComponentInChildren<TextMeshProUGUI>();
            mapHeaderButton.onClick.AddListener(ToggleMapPanel);

            // Done / back (top-right).
            backButton = UIFactory.CreatePillButton("RecordingsBack", sheet, "Done", ghost: false);
            var br = (RectTransform)backButton.transform;
            br.anchorMin = new Vector2(1f, 1f);
            br.anchorMax = new Vector2(1f, 1f);
            br.pivot = new Vector2(1f, 1f);
            br.sizeDelta = new Vector2(72f, 30f);
            br.anchoredPosition = new Vector2(-UITokens.Space8, -UITokens.Space8);

            BuildMapPanel(sheet);

            // Scroll view fills the rest of the sheet below the header.
            var viewport = UIFactory.CreateRect("Viewport", sheet);
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.offsetMin = new Vector2(UITokens.Space12, UITokens.Space8);
            viewport.offsetMax = new Vector2(-UITokens.Space12, -(UITokens.Space8 + 38f));
            viewport.gameObject.AddComponent<RectMask2D>();

            content = UIFactory.CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = UITokens.Space4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.padding = new RectOffset(2, 2, 0, (int)UITokens.Space12);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = sheet.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;

            uiRoot.gameObject.SetActive(isVisible);
        }

        private void BuildMapPanel(RectTransform sheet)
        {
            mapPanel = UIFactory.CreatePanel("MapPanel", sheet, UITokens.SurfaceElevated, (int)UITokens.RadiusMedium).gameObject;
            var pr = mapPanel.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(0f, 1f);
            pr.anchorMax = new Vector2(1f, 1f);
            pr.pivot = new Vector2(0.5f, 1f);
            pr.offsetMin = new Vector2(UITokens.Space12, 0f);
            pr.offsetMax = new Vector2(-UITokens.Space12, 0f);
            pr.sizeDelta = new Vector2(pr.sizeDelta.x, 132f);
            pr.anchoredPosition = new Vector2(0f, -(UITokens.Space8 + 30f + UITokens.Space4));
            mapPanel.GetComponent<Image>().raycastTarget = true;

            var col = mapPanel.AddComponent<VerticalLayoutGroup>();
            col.padding = new RectOffset((int)UITokens.Space12, (int)UITokens.Space12, (int)UITokens.Space8, (int)UITokens.Space8);
            col.spacing = UITokens.Space8;
            col.childControlWidth = true;
            col.childControlHeight = true;
            col.childForceExpandWidth = true;
            col.childForceExpandHeight = false;

            mapStatusLabel = UIFactory.CreateText("MapStatus", mapPanel.transform,
                "Enter a numeric map ID, then Load", UITokens.FontCaption, UITokens.Muted, TextAlignmentOptions.Left);
            SetPrefHeight(mapStatusLabel.gameObject, 32f);
            mapStatusLabel.textWrappingMode = TextWrappingModes.Normal;

            mapIdInput = UIFactory.CreateInputField("MapIdInput", mapPanel.transform, "e.g. 147190");
            SetPrefHeight(mapIdInput.gameObject, 40f);
            mapIdInput.onSubmit.AddListener(_ => OnMapLoadClicked());

            mapLoadButton = UIFactory.CreatePillButton("MapLoad", mapPanel.transform, "Load map", ghost: false);
            SetPrefHeight(mapLoadButton.gameObject, 40f);
            mapLoadLabel = mapLoadButton.GetComponentInChildren<TextMeshProUGUI>();
            mapLoadButton.onClick.AddListener(OnMapLoadClicked);

            mapPanelVisible = false;
            mapPanel.SetActive(false);
            SyncMapInput();
        }

        private static void SetPrefHeight(GameObject go, float h)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.preferredHeight = h;
            le.minHeight = h;
            le.flexibleHeight = 0f;
        }

        // ============================================================================================
        // ROWS
        // ============================================================================================

        private void RefreshFromStorage()
        {
            var c = Controller();
            string mapId = c != null ? c.GetActiveMapId() : "";
            RecordingSelection.Instance.Refresh(mapId);
        }

        private void RebuildRows()
        {
            if (content == null) return;
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var ch = content.GetChild(i);
                if (Application.isPlaying) Destroy(ch.gameObject); else DestroyImmediate(ch.gameObject);
            }

            var entries = RecordingSelection.Instance.Entries;
            if (entries.Count == 0)
            {
                var empty = UIFactory.CreateText("Empty", content,
                    "No recordings for this map yet.", UITokens.FontBody, UITokens.Muted, TextAlignmentOptions.Left);
                var le = empty.gameObject.AddComponent<LayoutElement>();
                le.minHeight = RowHeight; le.preferredHeight = RowHeight;
                empty.textWrappingMode = TextWrappingModes.Normal;
                return;
            }

            foreach (var entry in entries)
                BuildRow(entry);
        }

        private void BuildRow(RecordingSelection.Entry entry)
        {
            var row = UIFactory.CreateRect("Row_" + entry.fileName, content);
            var rowLe = row.gameObject.AddComponent<LayoutElement>();
            rowLe.minHeight = RowHeight; rowLe.preferredHeight = RowHeight;
            var rowBg = row.gameObject.AddComponent<Image>();
            rowBg.sprite = UIFactory.RoundedSprite((int)UITokens.RadiusMedium);
            rowBg.type = Image.Type.Sliced;
            rowBg.color = UITokens.Surface;
            rowBg.raycastTarget = false;

            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(
                Mathf.RoundToInt(RowSidePadding),
                Mathf.RoundToInt(RowSidePadding),
                0,
                0);
            hlg.spacing = LabelToControlsSpacing;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            var label = UIFactory.CreateText("Label", row, entry.ShortLabel, UITokens.FontCaption,
                UITokens.OnSurface, TextAlignmentOptions.Left);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            var labelLe = label.gameObject.AddComponent<LayoutElement>();
            labelLe.flexibleWidth = 0f;
            labelLe.preferredWidth = 92f;
            labelLe.minWidth = 56f;

            // Dim recordings with no fused character (they can only play as the single primary, not overlaid).
            if (!entry.hasFusion)
                label.color = UITokens.Muted;

            var leftSpacer = UIFactory.CreateRect("LeftSpacer", row);
            var leftSpacerLe = leftSpacer.gameObject.AddComponent<LayoutElement>();
            leftSpacerLe.flexibleWidth = 1f;
            leftSpacerLe.minWidth = 0f;

            // Per-recording character picker: one pill per GLB (1…N), tight spacing, centered as a group.
            var controls = UIFactory.CreateRect("Controls", row);
            var controlsLe = controls.gameObject.AddComponent<LayoutElement>();
            controlsLe.flexibleWidth = 0f;
            controlsLe.minWidth = 0f;
            var controlsLayout = controls.gameObject.AddComponent<HorizontalLayoutGroup>();
            controlsLayout.padding = new RectOffset(0, 0, 0, 0);
            controlsLayout.spacing = CharPickerSpacing;
            controlsLayout.childAlignment = TextAnchor.MiddleCenter;
            controlsLayout.childControlWidth = true;
            controlsLayout.childControlHeight = true;
            controlsLayout.childForceExpandWidth = false;
            controlsLayout.childForceExpandHeight = false;
            var controlsFitter = controls.gameObject.AddComponent<ContentSizeFitter>();
            controlsFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            controlsFitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

            BuildCharacterPicker(controls, entry.fileName);

            var toggle = UIFactory.CreatePillButton("Toggle", controls, entry.enabled ? "On" : "Off", ghost: true);
            ConfigureRowControl(toggle, ToggleWidth);
            var toggleText = toggle.GetComponentInChildren<TextMeshProUGUI>();
            var toggleImg = toggle.GetComponent<Image>();
            ApplyToggleVisual(toggleImg, toggleText, entry.enabled);

            string file = entry.fileName;
            toggle.onClick.AddListener(() =>
            {
                bool nv = !RecordingSelection.Instance.IsEnabled(file);
                RecordingSelection.Instance.SetEnabled(file, nv);
                ApplyToggleVisual(toggleImg, toggleText, nv);
                var c = Controller();
                if (c != null)
                    c.ApplyRecordingSelection();
            });

            // Balance leftover width on both sides of the control pair so they sit in the row center.
            var rightSpacer = UIFactory.CreateRect("RightSpacer", row);
            var rightSpacerLe = rightSpacer.gameObject.AddComponent<LayoutElement>();
            rightSpacerLe.flexibleWidth = 1f;
            rightSpacerLe.minWidth = 0f;
        }

        /// <summary>
        /// One numbered pill per GLB character (2 px gaps). Sizes stay compact; the whole strip sits in the
        /// row center via the flanking spacers in <see cref="BuildRow"/>.
        /// </summary>
        private void BuildCharacterPicker(RectTransform parent, string fileName)
        {
            var sw = Switcher();
            int count = sw != null ? sw.Count : 0;
            if (count <= 0)
                return;

            int selected = RecordingSelection.Instance.GetCharacterIndex(fileName);
            if (selected < 0)
                selected = 0;

            for (int i = 0; i < count; i++)
            {
                int index = i;
                string label = (i + 1).ToString();
                var btn = UIFactory.CreatePillButton("Char_" + label, parent, label, ghost: index != selected);
                ConfigureRowControl(btn, CharButtonWidth);
                ApplyCharacterVisual(btn, index == selected);
                btn.onClick.AddListener(() =>
                {
                    RecordingSelection.Instance.SetCharacterIndex(fileName, index);
                    RefreshCharacterPickerVisuals(parent, fileName);
                    var c = Controller();
                    if (c != null)
                        c.ApplyRecordingSelection();
                });
            }
        }

        private void RefreshCharacterPickerVisuals(RectTransform picker, string fileName)
        {
            int selected = RecordingSelection.Instance.GetCharacterIndex(fileName);
            if (selected < 0)
                selected = 0;

            for (int i = 0; i < picker.childCount; i++)
            {
                var child = picker.GetChild(i);
                if (child.name == "Toggle")
                    continue;
                var btn = child.GetComponent<Button>();
                if (btn != null)
                    ApplyCharacterVisual(btn, i == selected);
            }
        }

        private static void ApplyCharacterVisual(Button btn, bool selected)
        {
            var img = btn.GetComponent<Image>();
            var text = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
                text.color = selected ? Color.white : UITokens.Muted;
            if (img != null)
                img.color = selected ? UITokens.Primary : UITokens.SurfaceElevated;
        }

        /// <summary>Size a row pill to a compact fixed width/height with a small, non-clipping centered label.</summary>
        private static void ConfigureRowControl(Button btn, float width)
        {
            var le = btn.gameObject.GetComponent<LayoutElement>();
            if (le == null) le = btn.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = width; le.minWidth = width; le.flexibleWidth = 0f;
            le.preferredHeight = RowControlHeight; le.minHeight = RowControlHeight; le.flexibleHeight = 0f;
            ((RectTransform)btn.transform).sizeDelta = new Vector2(width, RowControlHeight);

            var text = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null)
            {
                text.fontSize = RowControlFontSize;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.overflowMode = TextOverflowModes.Overflow;
                text.margin = Vector4.zero;
                UIFactory.Stretch(text.rectTransform, 2f);
            }
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

        // ============================================================================================
        // MAP SWITCH
        // ============================================================================================

        private void ToggleMapPanel()
        {
            mapPanelVisible = !mapPanelVisible;
            if (mapPanel != null)
            {
                mapPanel.SetActive(mapPanelVisible);
                // The panel is built before the scroll viewport, so without this the list renders on top and
                // swallows taps. Lift it to the front whenever it is shown so the input + Load are tappable.
                if (mapPanelVisible)
                    mapPanel.transform.SetAsLastSibling();
            }
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
            if (c != null && (c.IsRecording || c.IsPlaying))
            {
                SetMapStatus("Stop recording/playback before switching maps", error: true);
                return;
            }

            SetMapStatus($"Loading map {idText}…", error: false);
            mapSwitcher.SwitchToMapFromInput(idText);
        }

        private void OnMapStatusChanged(string message)
        {
            if (mapStatusLabel == null) return;
            mapStatusLabel.text = message;
            switch (mapSwitcher != null ? mapSwitcher.LastStatusSeverity : ImmersalMapSwitcher.StatusSeverity.Info)
            {
                case ImmersalMapSwitcher.StatusSeverity.Error: mapStatusLabel.color = UITokens.Danger; break;
                case ImmersalMapSwitcher.StatusSeverity.Success: mapStatusLabel.color = UITokens.Success; break;
                case ImmersalMapSwitcher.StatusSeverity.Working: mapStatusLabel.color = UITokens.OnSurface; break;
                default: mapStatusLabel.color = UITokens.Muted; break;
            }
            UpdateMapHeaderLabel();
        }

        private void OnMapSwitched(int mapId)
        {
            SyncMapInput();
            UpdateMapHeaderLabel();
            RefreshFromStorage();
            RebuildRows();
        }

        private void SyncMapInput()
        {
            if (mapIdInput == null || mapSwitcher == null) return;
            int id = mapSwitcher.ActiveMapId > 0
                ? mapSwitcher.ActiveMapId
                : (mapSwitcher.PendingMapId > 0 ? mapSwitcher.PendingMapId : -1);
            if (id > 0)
                mapIdInput.text = id.ToString();
        }

        private void UpdateMapHeaderLabel()
        {
            if (mapHeaderLabel == null) return;
            string id = "—";
            if (mapSwitcher != null && mapSwitcher.ActiveMapId > 0)
                id = mapSwitcher.ActiveMapId.ToString();
            else
            {
                var c = Controller();
                string fromController = c != null ? c.GetActiveMapId() : "";
                if (!string.IsNullOrWhiteSpace(fromController))
                    id = fromController.Trim();
            }
            bool switching = mapSwitcher != null && mapSwitcher.IsSwitching;
            mapHeaderLabel.text = switching ? $"Map {id}…" : $"Map {id}";
        }

        private void SetMapStatus(string message, bool error)
        {
            if (mapStatusLabel != null)
            {
                mapStatusLabel.text = message;
                mapStatusLabel.color = error ? UITokens.Danger : UITokens.OnSurface;
            }
        }

        // ============================================================================================
        // SHARED CANVAS / EVENT SYSTEM
        // ============================================================================================

        private Canvas EnsureCanvas()
        {
            var canvas = GetComponentInParent<Canvas>();
            if (canvas == null) canvas = Object.FindFirstObjectByType<Canvas>();
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
