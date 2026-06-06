using UnityEngine;
using UnityEngine.XR.ARFoundation;
using BodyTracking.Recording;
using BodyTracking.Playback;
using Immersal.XR;
using TENDOR.AR;

namespace BodyTracking.UI
{
    /// <summary>
    /// One switch for a clean presentation view. Hides every developer/debug visual — the AR Foundation plane
    /// detection visuals (dots/mesh on surfaces), the Immersal area-mapping point cloud (dots), the live recording
    /// skeleton, the recorded playback "ghost" skeleton, the Move AI compare overlay, and the tracked-image debug
    /// quad — so only the playback UI and the final 3D character remain.
    /// Fully reversible at runtime.
    /// </summary>
    public class DebugVisualsController : MonoBehaviour
    {
        [Tooltip("Whether debug visuals are shown when the scene starts. Off = launch straight into the clean view.")]
        [SerializeField] private bool visibleOnStart = true;

        /// <summary>True when developer visuals are currently shown.</summary>
        public bool VisualsVisible { get; private set; } = true;

        private void Start() => SetVisible(visibleOnStart);

        /// <summary>Flip between the clean view and the full debug view.</summary>
        public void Toggle() => SetVisible(!VisualsVisible);

        /// <summary>Show (true) or hide (false) all developer/debug visuals.</summary>
        public void SetVisible(bool visible)
        {
            VisualsVisible = visible;

            // Immersal area-mapping point cloud (the "dots on the wall").
            // The static flag covers any visualization loaded later at runtime, but on its own it only takes
            // effect through the SDK's per-frame OnRenderObject/UpdateMaterial path, which can silently fail to
            // hide existing dots (e.g. material early-return, render-mode/camera quirks). So we also disable the
            // MeshRenderer of every current visualization directly, which is immediate and reliable.
            XRMapVisualization.pointCloudVisible = visible;
            foreach (var vis in FindObjectsByType<XRMapVisualization>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var renderer = vis.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = visible;
            }

            // AR Foundation plane-detection visuals (the "dots" / mesh drawn on detected surfaces).
            // Hide every currently detected plane, and disable the manager so no new plane visuals spawn while
            // hidden; re-enabling restores detection and shows the planes again.
            foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                foreach (var plane in planeManager.trackables)
                    plane.gameObject.SetActive(visible);
                planeManager.enabled = visible;
            }

            // Live recording skeleton (green) and recorded ghost skeleton (cyan).
            foreach (var rec in FindObjectsByType<BodyTrackingRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                rec.SetSkeletonVisible(visible);
            foreach (var player in FindObjectsByType<BodyTrackingPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                player.SetSkeletonVisible(visible);

            // Move AI compare overlay (orange/cyan skeletons drawn during fused replay).
            foreach (var cmp in FindObjectsByType<PlaybackCompareVisualizer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                cmp.SetSuppressed(!visible);

            // Tracked-image debug quad (translucent rectangle on the marker). Disabling destroys its quads.
            foreach (var quad in FindObjectsByType<ARTrackedImageDebugQuad>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                quad.enabled = visible;
        }
    }
}
