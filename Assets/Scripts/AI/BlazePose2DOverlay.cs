using UnityEngine;
using UnityEngine.UI;

namespace BodyTracking.AI
{
    /// <summary>
    /// Stage 1 validation gate: draws the BlazePose landmarks + bones on a screen-space overlay so detection
    /// can be confirmed on device before any depth/world-space work. Builds its own Canvas at runtime.
    /// </summary>
    public class BlazePose2DOverlay : MonoBehaviour
    {
        [SerializeField] BlazePoseRunner runner;

        [Header("Orientation")]
        [Tooltip("Auto-detect portrait sensor->display rotation (recommended on iPhone).")]
        [SerializeField] bool autoOrient = true;
        [SerializeField] bool flipX;
        [SerializeField] bool flipY;
        [SerializeField] bool swapXY;

        [Header("Appearance")]
        [SerializeField, Min(1f)] float dotSize = 18f;
        [SerializeField, Min(1f)] float boneThickness = 6f;
        [SerializeField, Range(0f, 1f)] float visibilityThreshold = 0.3f;
        [SerializeField] Color trackedColor = new Color(0.1f, 1f, 0.4f, 0.95f);
        [SerializeField] Color untrackedColor = new Color(1f, 0.5f, 0.1f, 0.5f);
        [SerializeField] Color boneColor = new Color(1f, 1f, 1f, 0.7f);

        Canvas m_Canvas;
        Sprite m_Sprite;
        RectTransform[] m_Dots;
        Image[] m_DotImages;
        RectTransform[] m_Bones;
        Image[] m_BoneImages;
        readonly Vector2[] m_ScreenPoints = new Vector2[BlazePoseSkeleton.NumKeypoints];

        void Awake()
        {
            if (runner == null)
                runner = FindAnyObjectByType<BlazePoseRunner>();
            BuildOverlay();
        }

        void OnEnable()
        {
            if (runner != null)
                runner.OnPoseUpdated += HandlePose;
        }

        void OnDisable()
        {
            if (runner != null)
                runner.OnPoseUpdated -= HandlePose;
        }

        void BuildOverlay()
        {
            var canvasGo = new GameObject("BlazePoseOverlayCanvas");
            canvasGo.transform.SetParent(transform, false);
            m_Canvas = canvasGo.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = 5000;

            var tex = Texture2D.whiteTexture;
            m_Sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));

            int n = BlazePoseSkeleton.NumKeypoints;
            m_Dots = new RectTransform[n];
            m_DotImages = new Image[n];
            m_Bones = new RectTransform[n];
            m_BoneImages = new Image[n];

            for (int i = 0; i < n; i++)
            {
                if (BlazePoseSkeleton.Parents[i] < 0)
                    continue;
                var bone = CreateElement($"Bone_{i}", boneColor);
                m_Bones[i] = bone.rect;
                m_BoneImages[i] = bone.image;
            }

            for (int i = 0; i < n; i++)
            {
                var dot = CreateElement($"Dot_{i}", trackedColor);
                m_Dots[i] = dot.rect;
                m_DotImages[i] = dot.image;
            }

            SetAllActive(false);
        }

        (RectTransform rect, Image image) CreateElement(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(m_Canvas.transform, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            var image = go.AddComponent<Image>();
            image.sprite = m_Sprite;
            image.color = color;
            image.raycastTarget = false;
            return (rect, image);
        }

        void HandlePose(BlazePoseResult result)
        {
            if (m_Canvas == null)
                return;

            if (result == null || !result.valid)
            {
                SetAllActive(false);
                return;
            }

            var remap = GetRemap(result);
            int texW = Mathf.RoundToInt(result.textureWidth);
            int texH = Mathf.RoundToInt(result.textureHeight);

            for (int i = 0; i < BlazePoseSkeleton.NumKeypoints; i++)
                m_ScreenPoints[i] = BlazePoseOrientation.SensorUvToScreen(result.landmarks[i].imageUV, texW, texH, remap);

            for (int i = 0; i < BlazePoseSkeleton.NumKeypoints; i++)
            {
                var lm = result.landmarks[i];
                bool visible = lm.visibility >= visibilityThreshold;
                var dot = m_Dots[i];
                dot.gameObject.SetActive(visible);
                if (visible)
                {
                    dot.anchoredPosition = m_ScreenPoints[i];
                    dot.sizeDelta = new Vector2(dotSize, dotSize);
                    m_DotImages[i].color = lm.tracked ? trackedColor : untrackedColor;
                }

                var bone = m_Bones[i];
                if (bone == null)
                    continue;
                int parent = BlazePoseSkeleton.Parents[i];
                bool boneVisible = visible && result.landmarks[parent].visibility >= visibilityThreshold;
                bone.gameObject.SetActive(boneVisible);
                if (boneVisible)
                    LayoutBone(bone, m_ScreenPoints[i], m_ScreenPoints[parent]);
            }
        }

        BlazePoseOrientation.Remap GetRemap(BlazePoseResult result)
        {
            if (autoOrient)
            {
                int w = Mathf.RoundToInt(result.textureWidth);
                int h = Mathf.RoundToInt(result.textureHeight);
                return BlazePoseOrientation.DetectRemap(w, h);
            }

            return new BlazePoseOrientation.Remap { swapXY = swapXY, flipX = flipX, flipY = flipY };
        }

        void LayoutBone(RectTransform bone, Vector2 a, Vector2 b)
        {
            Vector2 dir = b - a;
            float len = dir.magnitude;
            bone.anchoredPosition = (a + b) * 0.5f;
            bone.sizeDelta = new Vector2(len, boneThickness);
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            bone.localRotation = Quaternion.Euler(0, 0, angle);
        }

        void SetAllActive(bool active)
        {
            if (m_Dots != null)
                foreach (var d in m_Dots)
                    if (d != null) d.gameObject.SetActive(active);
            if (m_Bones != null)
                foreach (var b in m_Bones)
                    if (b != null) b.gameObject.SetActive(active);
        }
    }
}
