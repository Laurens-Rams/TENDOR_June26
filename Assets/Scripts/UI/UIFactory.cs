using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BodyTracking.UI
{
    /// <summary>
    /// Reusable, token-driven UI builder. Every visual element in the app is composed from these
    /// helpers so styling stays centralized in <see cref="UITokens"/> rather than scattered per element.
    ///
    /// All sprites (rounded rectangles, circles, the play triangle) are generated procedurally and
    /// cached, so the UI has zero art-asset dependencies and renders crisply at any size (rounded rects
    /// are emitted as 9-sliced sprites). All text uses TextMeshPro with the project's
    /// LiberationSans SDF font for readability.
    /// </summary>
    public static class UIFactory
    {
        private static TMP_FontAsset s_font;
        private static readonly Dictionary<int, Sprite> s_roundedCache = new Dictionary<int, Sprite>();
        private static Sprite s_circle;
        private static Sprite s_triangle;

        // ------------------------------------------------------------------------------------
        // FONT
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// Resolves the TMP font once. Prefers the TMP default (LiberationSans SDF in this project),
        /// falling back to an explicit Resources load. Returns null only if TMP is misconfigured.
        /// </summary>
        public static TMP_FontAsset GetFont()
        {
            if (s_font != null) return s_font;
            s_font = TMP_Settings.defaultFontAsset;
            if (s_font == null)
                s_font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
            return s_font;
        }

        // ------------------------------------------------------------------------------------
        // CORE OBJECT CREATION
        // ------------------------------------------------------------------------------------

        /// <summary>Create a bare RectTransform child (no graphic).</summary>
        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Stretch a RectTransform to fill its parent with optional uniform padding.</summary>
        public static void Stretch(RectTransform rect, float padding = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        // ------------------------------------------------------------------------------------
        // PANELS / SURFACES
        // ------------------------------------------------------------------------------------

        /// <summary>A rounded surface panel filled with a token color.</summary>
        public static Image CreatePanel(string name, Transform parent, Color color, int radius)
        {
            var rect = CreateRect(name, parent);
            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite(radius);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // ------------------------------------------------------------------------------------
        // TEXT
        // ------------------------------------------------------------------------------------

        public static TextMeshProUGUI CreateText(
            string name,
            Transform parent,
            string text,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left)
        {
            var rect = CreateRect(name, parent);
            var tmp = rect.gameObject.AddComponent<TextMeshProUGUI>();
            var font = GetFont();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            return tmp;
        }

        // ------------------------------------------------------------------------------------
        // BUTTONS
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// A circular, minimal transport/icon button (record, play/pause, stop). Returns the Button;
        /// the caller adds an icon child via the icon helpers. The background is a token surface and
        /// can be recolored (e.g. accent for play, danger for record).
        /// </summary>
        public static Button CreateCircleButton(string name, Transform parent, float diameter, Color bgColor)
        {
            var rect = CreateRect(name, parent);
            rect.sizeDelta = new Vector2(diameter, diameter);

            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = CircleSprite();
            img.type = Image.Type.Simple;
            img.color = bgColor;
            img.raycastTarget = true;

            var btn = rect.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            ApplyButtonColors(btn, bgColor);
            return btn;
        }

        /// <summary>
        /// A pill-shaped text button. <paramref name="ghost"/> renders an outline-only (transparent)
        /// secondary style; otherwise it is a filled accent primary button.
        /// </summary>
        public static Button CreatePillButton(string name, Transform parent, string label, bool ghost)
        {
            var rect = CreateRect(name, parent);
            rect.sizeDelta = new Vector2(140f, 44f);

            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite(18);
            img.type = Image.Type.Sliced;
            Color bg = ghost ? new Color(1f, 1f, 1f, 0.06f) : UITokens.Primary;
            img.color = bg;
            img.raycastTarget = true;

            var btn = rect.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            ApplyButtonColors(btn, bg);

            var text = CreateText("Label", rect, label, UITokens.FontBody,
                ghost ? UITokens.OnSurface : Color.white, TextAlignmentOptions.Center);
            Stretch(text.rectTransform, UITokens.Space8);
            return btn;
        }

        private static void ApplyButtonColors(Button btn, Color baseColor)
        {
            var cb = ColorBlock.defaultColorBlock;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
            cb.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            cb.selectedColor = Color.white;
            cb.disabledColor = UITokens.Disabled;
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
        }

        /// <summary>A single-line TMP input field on a raised surface background.</summary>
        public static TMP_InputField CreateInputField(string name, Transform parent, string placeholder, float height = 40f)
        {
            var rect = CreateRect(name, parent);
            rect.sizeDelta = new Vector2(0f, height);

            var bg = rect.gameObject.AddComponent<Image>();
            bg.sprite = RoundedSprite((int)UITokens.RadiusMedium);
            bg.type = Image.Type.Sliced;
            bg.color = UITokens.SurfaceElevated;
            bg.raycastTarget = true;

            var textArea = CreateRect("TextArea", rect);
            Stretch(textArea, UITokens.Space8);

            var placeholderRect = CreateRect("Placeholder", textArea);
            Stretch(placeholderRect);
            var placeholderTmp = placeholderRect.gameObject.AddComponent<TextMeshProUGUI>();
            var font = GetFont();
            if (font != null) placeholderTmp.font = font;
            placeholderTmp.text = placeholder;
            placeholderTmp.fontSize = UITokens.FontBody;
            placeholderTmp.color = UITokens.Muted;
            placeholderTmp.alignment = TextAlignmentOptions.MidlineLeft;
            placeholderTmp.raycastTarget = false;

            var textRect = CreateRect("Text", textArea);
            Stretch(textRect);
            var textTmp = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) textTmp.font = font;
            textTmp.fontSize = UITokens.FontBody;
            textTmp.color = UITokens.OnSurface;
            textTmp.alignment = TextAlignmentOptions.MidlineLeft;
            textTmp.raycastTarget = false;

            var input = rect.gameObject.AddComponent<TMP_InputField>();
            input.textViewport = textArea;
            input.textComponent = textTmp;
            input.placeholder = placeholderTmp;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.keyboardType = TouchScreenKeyboardType.NumberPad;
            return input;
        }

        // ------------------------------------------------------------------------------------
        // TRANSPORT ICONS (built from primitives so they always render, no glyph dependency)
        // ------------------------------------------------------------------------------------

        /// <summary>Right-pointing triangle (play). Centered in the parent.</summary>
        public static Image AddPlayIcon(Transform parent, float size, Color color)
        {
            var rect = CreateRect("Icon_Play", parent);
            Center(rect, new Vector2(size, size));
            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = TriangleSprite();
            img.color = color;
            img.raycastTarget = false;
            // Nudge right so the visual mass looks centered in a circle.
            rect.anchoredPosition = new Vector2(size * 0.08f, 0f);
            return img;
        }

        /// <summary>Two vertical bars (pause).</summary>
        public static GameObject AddPauseIcon(Transform parent, float size, Color color)
        {
            var root = CreateRect("Icon_Pause", parent);
            Center(root, new Vector2(size, size));
            float barW = size * 0.28f;
            float gap = size * 0.18f;
            MakeBar(root, "BarL", new Vector2(-(barW + gap) * 0.5f, 0f), new Vector2(barW, size), color);
            MakeBar(root, "BarR", new Vector2((barW + gap) * 0.5f, 0f), new Vector2(barW, size), color);
            return root.gameObject;
        }

        /// <summary>Filled square (stop).</summary>
        public static Image AddStopIcon(Transform parent, float size, Color color)
        {
            var rect = CreateRect("Icon_Stop", parent);
            Center(rect, new Vector2(size, size));
            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite(4);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Filled circle (record dot) or rounded square when recording (stop-record).</summary>
        public static Image AddRecordIcon(Transform parent, float size, Color color, bool square)
        {
            var rect = CreateRect("Icon_Record", parent);
            Center(rect, new Vector2(size, size));
            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = square ? RoundedSprite(4) : CircleSprite();
            img.type = square ? Image.Type.Sliced : Image.Type.Simple;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private static void MakeBar(Transform parent, string name, Vector2 pos, Vector2 sizeDelta, Color color)
        {
            var rect = CreateRect(name, parent);
            Center(rect, sizeDelta);
            rect.anchoredPosition = pos;
            var img = rect.gameObject.AddComponent<Image>();
            img.sprite = RoundedSprite(3);
            img.type = Image.Type.Sliced;
            img.color = color;
            img.raycastTarget = false;
        }

        private static void Center(RectTransform rect, Vector2 sizeDelta)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = sizeDelta;
        }

        // ------------------------------------------------------------------------------------
        // STATUS PILL (icon dot + label, used in the top status bar)
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// A compact status pill: a colored dot followed by a label. Returns the dot Image and the
        /// label so the caller can update color/text as state changes.
        /// </summary>
        public static (RectTransform root, Image dot, TextMeshProUGUI label) CreateStatusPill(
            string name, Transform parent, string initialLabel, bool autoSize = true)
        {
            var root = CreateRect(name, parent);
            root.sizeDelta = new Vector2(180f, UITokens.PillHeight);

            var bg = root.gameObject.AddComponent<Image>();
            bg.sprite = RoundedSprite(Mathf.RoundToInt(UITokens.PillHeight * 0.5f));
            bg.type = Image.Type.Sliced;
            bg.color = UITokens.SurfaceElevated;
            bg.raycastTarget = false;

            var layout = root.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset((int)UITokens.Space8, (int)UITokens.Space8, 0, 0);
            layout.spacing = UITokens.Space4;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            // autoSize=true: pill grows to its content (ContentSizeFitter). autoSize=false: the parent
            // layout controls the pill width (used by the top bar so pills share width and never overflow).
            if (autoSize)
            {
                var fitter = root.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            }
            var le = root.gameObject.AddComponent<LayoutElement>();
            le.minHeight = UITokens.PillHeight;
            le.preferredHeight = UITokens.PillHeight;

            var dotRect = CreateRect("Dot", root);
            dotRect.sizeDelta = new Vector2(8f, 8f);
            var dot = dotRect.gameObject.AddComponent<Image>();
            dot.sprite = CircleSprite();
            dot.color = UITokens.Muted;
            dot.raycastTarget = false;
            var dotLayout = dotRect.gameObject.AddComponent<LayoutElement>();
            dotLayout.minWidth = 8f; dotLayout.minHeight = 8f;
            dotLayout.preferredWidth = 8f; dotLayout.preferredHeight = 8f;
            dotLayout.flexibleWidth = 0f;

            var label = CreateText("Label", root, initialLabel, UITokens.FontCaption - 2f, UITokens.OnSurface, TextAlignmentOptions.Left);
            var labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.flexibleWidth = 1f;

            return (root, dot, label);
        }

        // ------------------------------------------------------------------------------------
        // SLIDER (scrub / timeline)
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// A minimal horizontal scrub slider: rounded track, accent fill and a round handle.
        /// Returns the configured <see cref="Slider"/> (0..1).
        /// </summary>
        public static Slider CreateScrubSlider(string name, Transform parent)
        {
            var rect = CreateRect(name, parent);
            rect.sizeDelta = new Vector2(200f, UITokens.ScrubHandleDiameter);

            var slider = rect.gameObject.AddComponent<Slider>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;

            // Background track (full width, vertically centered).
            var bg = CreateRect("Track", rect);
            CenterStretchHorizontal(bg, UITokens.ScrubTrackHeight);
            var bgImg = bg.gameObject.AddComponent<Image>();
            bgImg.sprite = RoundedSprite(Mathf.RoundToInt(UITokens.ScrubTrackHeight * 0.5f) + 1);
            bgImg.type = Image.Type.Sliced;
            bgImg.color = new Color(1f, 1f, 1f, 0.18f);
            bgImg.raycastTarget = true;

            // Fill area / fill (accent).
            var fillArea = CreateRect("Fill Area", rect);
            CenterStretchHorizontal(fillArea, UITokens.ScrubTrackHeight);
            var fill = CreateRect("Fill", fillArea);
            fill.anchorMin = new Vector2(0f, 0f);
            fill.anchorMax = new Vector2(1f, 1f);
            fill.sizeDelta = Vector2.zero;
            var fillImg = fill.gameObject.AddComponent<Image>();
            fillImg.sprite = RoundedSprite(Mathf.RoundToInt(UITokens.ScrubTrackHeight * 0.5f) + 1);
            fillImg.type = Image.Type.Sliced;
            fillImg.color = UITokens.Primary;
            fillImg.raycastTarget = false;

            // Handle.
            var handleArea = CreateRect("Handle Slide Area", rect);
            handleArea.anchorMin = new Vector2(0f, 0f);
            handleArea.anchorMax = new Vector2(1f, 1f);
            handleArea.offsetMin = new Vector2(UITokens.ScrubHandleDiameter * 0.5f, 0f);
            handleArea.offsetMax = new Vector2(-UITokens.ScrubHandleDiameter * 0.5f, 0f);
            var handle = CreateRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(UITokens.ScrubHandleDiameter, UITokens.ScrubHandleDiameter);
            var handleImg = handle.gameObject.AddComponent<Image>();
            handleImg.sprite = CircleSprite();
            handleImg.color = Color.white;
            handleImg.raycastTarget = true;

            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImg;

            return slider;
        }

        private static void CenterStretchHorizontal(RectTransform rect, float height)
        {
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0f, height);
            rect.anchoredPosition = Vector2.zero;
        }

        // ------------------------------------------------------------------------------------
        // PROCEDURAL SPRITES (cached)
        // ------------------------------------------------------------------------------------

        /// <summary>9-sliced rounded-rectangle sprite for the given corner radius (cached).</summary>
        public static Sprite RoundedSprite(int radius)
        {
            radius = Mathf.Max(1, radius);
            if (s_roundedCache.TryGetValue(radius, out var cached) && cached != null)
                return cached;

            const int center = 4; // stretchable middle band for 9-slicing
            int size = radius * 2 + center;
            var tex = NewTexture(size);
            var halfSize = new Vector2(size * 0.5f, size * 0.5f);
            var c = halfSize;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f) - c;
                    // Signed distance to a rounded box.
                    var b = halfSize - new Vector2(radius, radius);
                    var q = new Vector2(Mathf.Abs(p.x) - b.x, Mathf.Abs(p.y) - b.y);
                    float outside = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f)).magnitude;
                    float inside = Mathf.Min(Mathf.Max(q.x, q.y), 0f);
                    float d = outside + inside - radius;
                    float a = Mathf.Clamp01(0.5f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply(false, true);

            float br = radius;
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(br, br, br, br));
            sprite.name = $"RoundedRect_{radius}";
            s_roundedCache[radius] = sprite;
            return sprite;
        }

        /// <summary>Antialiased filled circle sprite (cached).</summary>
        public static Sprite CircleSprite()
        {
            if (s_circle != null) return s_circle;
            const int size = 96;
            var tex = NewTexture(size);
            var c = new Vector2(size * 0.5f, size * 0.5f);
            float r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c) - r;
                    float a = Mathf.Clamp01(0.5f - d);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply(false, true);
            s_circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s_circle.name = "Circle";
            return s_circle;
        }

        /// <summary>Antialiased right-pointing triangle sprite (cached). Used for the play icon.</summary>
        public static Sprite TriangleSprite()
        {
            if (s_triangle != null) return s_triangle;
            const int size = 96;
            var tex = NewTexture(size);
            // Vertices of a right-pointing triangle inset from the edges.
            var a = new Vector2(size * 0.20f, size * 0.16f);
            var b = new Vector2(size * 0.20f, size * 0.84f);
            var t = new Vector2(size * 0.84f, size * 0.50f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    // Wind the vertices counter-clockwise (a -> t -> b) so the edge normals point inward.
                    float alpha = TriangleCoverage(p, a, t, b);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply(false, true);
            s_triangle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s_triangle.name = "Triangle";
            return s_triangle;
        }

        private static float TriangleCoverage(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            // 1px-ish antialiased coverage using signed edge distances.
            float d0 = EdgeDistance(p, a, b);
            float d1 = EdgeDistance(p, b, c);
            float d2 = EdgeDistance(p, c, a);
            // Inside when all edge distances share sign; use min as conservative AA edge.
            float inside = Mathf.Min(d0, Mathf.Min(d1, d2));
            return Mathf.Clamp01(inside + 0.5f);
        }

        private static float EdgeDistance(Vector2 p, Vector2 v0, Vector2 v1)
        {
            // Positive on the interior side (CCW), with magnitude ~= pixel distance to the edge line.
            Vector2 e = v1 - v0;
            Vector2 n = new Vector2(-e.y, e.x).normalized;
            return Vector2.Dot(p - v0, n);
        }

        private static Texture2D NewTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }
    }
}
