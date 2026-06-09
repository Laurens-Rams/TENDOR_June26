using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using BodyTracking.Playback;
using BodyTracking.Playback.PostProcess;
using BodyTracking.MoveAI;
using BodyTracking.Animation;

namespace BodyTracking.UI
{
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

        // Compact typography + insets so labels fit inside rows without clipping at the mask edge.
        private const float SheetSideInset = 28f;
        private const float RowSideInset = 8f;
        private const float SectionFontSize = 11f;
        private const float LabelFontSize = 12f;
        private const float ValueFontSize = 11f;
        private const float HeaderFontSize = 16f;
        private const float ValueColumnWidth = 52f;
        private const float ToggleButtonWidth = 48f;
        private const float ModeButtonWidth = 72f;
        private const float ControlRowHeight = 28f;
        private const float SliderRowHeight = 54f;

        public Button BackButton => backButton;

        void OnEnable() => EnsureInitialized();

        public void SetVisible(bool visible)
        {
            isVisible = visible;
            EnsureInitialized();
            if (uiRoot != null)
                uiRoot.gameObject.SetActive(visible);
            if (visible)
                RebuildRows();
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

            // Scroll view filling the rest of the sheet.
            var viewport = UIFactory.CreateRect("Viewport", sheet);
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.offsetMin = new Vector2(UITokens.Space12, UITokens.Space8);
            viewport.offsetMax = new Vector2(-UITokens.Space12, -44f);
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
            vlg.padding = new RectOffset(
                Mathf.RoundToInt(RowSideInset),
                Mathf.RoundToInt(RowSideInset),
                0,
                Mathf.RoundToInt(UITokens.Space12));
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

            var p = Player();
            if (p == null)
            {
                AddSection("No FusedCharacterPlayer found in scene");
                return;
            }

            AddSection("Position / trajectory");
            // TEST: drive world movement straight from the Move AI GLB root motion (anchored once to ARKit).
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

            AddSection("Wall / floor penetration");
            AddToggle("Enable penetration fix", () => p.EnablePenetrationFix, v => p.EnablePenetrationFix = v);
            AddToggle("Floor fix (feet on floor)", () => p.PenetrationSettingsLive.enableFloorFix,
                v => { var s = p.PenetrationSettingsLive; s.enableFloorFix = v; p.PenetrationSettingsLive = s; });
            AddSlider("Floor contact band (m)", 0.02f, 0.4f, () => p.PenetrationSettingsLive.floorContactBand,
                v => { var s = p.PenetrationSettingsLive; s.floorContactBand = v; p.PenetrationSettingsLive = s; }, "0.00");
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

        private static void ConfigureRowLabel(TextMeshProUGUI tmp, bool allowWrap = false)
        {
            if (tmp == null) return;
            tmp.extraPadding = true;
            tmp.margin = new Vector4(2f, 0f, 2f, 0f);
            tmp.textWrappingMode = allowWrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            tmp.overflowMode = allowWrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        }

        private static void ConfigureCompactPillLabel(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            tmp.fontSize = LabelFontSize;
            tmp.extraPadding = true;
            tmp.margin = new Vector4(2f, 0f, 2f, 0f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            UIFactory.Stretch(tmp.rectTransform, UITokens.Space4);
        }

        private static HorizontalLayoutGroup AddHorizontalRow(RectTransform row)
        {
            var hlg = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(
                Mathf.RoundToInt(RowSideInset),
                Mathf.RoundToInt(RowSideInset),
                0,
                0);
            hlg.spacing = UITokens.Space8;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            return hlg;
        }

        private static TextMeshProUGUI AddFlexibleLabel(Transform parent, string text, float fontSize, Color color)
        {
            var lbl = UIFactory.CreateText("Label", parent, text, fontSize, color, TextAlignmentOptions.Left);
            ConfigureRowLabel(lbl);
            var le = lbl.gameObject.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;
            le.minWidth = 0f;
            return lbl;
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
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 24f; le.preferredHeight = 24f;
            var t = UIFactory.CreateBoldText("Label", row, title.ToUpperInvariant(), SectionFontSize,
                UITokens.Primary, TextAlignmentOptions.BottomLeft);
            ConfigureRowLabel(t, allowWrap: true);
            UIFactory.Stretch(t.rectTransform, RowSideInset);
        }

        private void AddSlider(string label, float min, float max, Func<float> getter, Action<float> setter, string fmt)
        {
            var row = UIFactory.CreateRect("Row_" + label, content);
            var rowLe = row.gameObject.AddComponent<LayoutElement>();
            rowLe.minHeight = SliderRowHeight;
            rowLe.preferredHeight = SliderRowHeight;

            var vlg = row.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(
                Mathf.RoundToInt(RowSideInset),
                Mathf.RoundToInt(RowSideInset),
                2,
                2);
            vlg.spacing = UITokens.Space4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var lbl = UIFactory.CreateText("Label", row, label, LabelFontSize, UITokens.OnSurface,
                TextAlignmentOptions.Left);
            ConfigureRowLabel(lbl, allowWrap: true);
            var lblLe = lbl.gameObject.AddComponent<LayoutElement>();
            lblLe.minHeight = 16f;
            lblLe.preferredHeight = 16f;
            lblLe.flexibleHeight = 0f;

            var bottom = UIFactory.CreateRect("Bottom", row);
            var bottomLe = bottom.gameObject.AddComponent<LayoutElement>();
            bottomLe.minHeight = ControlRowHeight;
            bottomLe.preferredHeight = ControlRowHeight;
            var bottomLayout = bottom.gameObject.AddComponent<HorizontalLayoutGroup>();
            bottomLayout.spacing = UITokens.Space8;
            bottomLayout.childControlWidth = true;
            bottomLayout.childControlHeight = true;
            bottomLayout.childForceExpandWidth = false;
            bottomLayout.childForceExpandHeight = true;
            bottomLayout.childAlignment = TextAnchor.MiddleLeft;

            var slider = UIFactory.CreateScrubSlider("Slider", bottom);
            var sliderLe = slider.gameObject.AddComponent<LayoutElement>();
            sliderLe.flexibleWidth = 1f;
            sliderLe.minWidth = 80f;
            sliderLe.preferredHeight = UITokens.ScrubHandleDiameter;
            sliderLe.minHeight = UITokens.ScrubHandleDiameter;

            var valText = UIFactory.CreateText("Value", bottom, getter().ToString(fmt), ValueFontSize,
                UITokens.Muted, TextAlignmentOptions.Right);
            ConfigureRowLabel(valText);
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
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = ControlRowHeight + 4f;
            le.preferredHeight = ControlRowHeight + 4f;
            AddHorizontalRow(row);

            AddFlexibleLabel(row, label, LabelFontSize, UITokens.OnSurface);

            var btn = AddFixedPill(row, getter() ? "On" : "Off", ToggleButtonWidth, ControlRowHeight, ghost: true);
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
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = ControlRowHeight + 4f;
            le.preferredHeight = ControlRowHeight + 4f;
            AddHorizontalRow(row);

            AddFlexibleLabel(row, label, LabelFontSize, UITokens.OnSurface);

            var btn = AddFixedPill(row, stateText(), ModeButtonWidth, ControlRowHeight, ghost: false);
            var btnText = btn.GetComponentInChildren<TextMeshProUGUI>();
            btn.onClick.AddListener(() =>
            {
                cycle();
                if (btnText != null) btnText.text = stateText();
                PropagateToOverlays();
            });
        }

        /// <summary>A full-width action button.</summary>
        private void AddButton(string label, Action onClick)
        {
            var row = UIFactory.CreateRect("Btn_" + label, content);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = ControlRowHeight + 8f;
            le.preferredHeight = ControlRowHeight + 8f;
            AddHorizontalRow(row);

            var btn = AddFixedPill(row, label, 260f, ControlRowHeight, ghost: false);
            var btnLe = btn.gameObject.GetComponent<LayoutElement>();
            if (btnLe != null)
            {
                btnLe.flexibleWidth = 1f;
                btnLe.minWidth = 180f;
            }

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
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = 28f; le.preferredHeight = 28f;
            var t = UIFactory.CreateText("Label", row, initial ?? "", ValueFontSize, UITokens.Muted,
                TextAlignmentOptions.Left);
            ConfigureRowLabel(t, allowWrap: true);
            UIFactory.Stretch(t.rectTransform, RowSideInset);
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
