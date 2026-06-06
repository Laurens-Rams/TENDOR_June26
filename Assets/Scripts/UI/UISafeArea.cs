using UnityEngine;

namespace BodyTracking.UI
{
    /// <summary>
    /// Keeps a RectTransform inside the device safe area (iPhone notch / home indicator / Dynamic Island).
    /// Attach to a full-screen child of the Canvas; it re-anchors itself to <see cref="Screen.safeArea"/>
    /// and re-applies whenever the screen size or orientation changes.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class UISafeArea : MonoBehaviour
    {
        private RectTransform rect;
        private Rect lastSafeArea;
        private Vector2Int lastScreen;

        void Awake()
        {
            rect = GetComponent<RectTransform>();
            Apply();
        }

        void Update()
        {
            // Cheap guard: only recompute when the safe area or resolution actually changes.
            if (Screen.safeArea != lastSafeArea ||
                Screen.width != lastScreen.x ||
                Screen.height != lastScreen.y)
            {
                Apply();
            }
        }

        private void Apply()
        {
            if (rect == null) return;

            lastSafeArea = Screen.safeArea;
            lastScreen = new Vector2Int(Screen.width, Screen.height);

            if (Screen.width <= 0 || Screen.height <= 0)
                return;

            Vector2 anchorMin = lastSafeArea.position;
            Vector2 anchorMax = lastSafeArea.position + lastSafeArea.size;
            anchorMin.x /= Screen.width;
            anchorMin.y /= Screen.height;
            anchorMax.x /= Screen.width;
            anchorMax.y /= Screen.height;

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
