using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using BodyTracking.Recording;
using BodyTracking.Playback;
using Immersal.XR;
using BodyTracking.AR;
using TENDOR.AR;

namespace BodyTracking.UI
{
    /// <summary>
    /// One switch for a clean presentation view. Hides developer/debug visuals — AR plane dots/mesh, Immersal
    /// point cloud, skeleton overlays, Move AI compare skeletons, tracked-image debug quad — while keeping plane
    /// DETECTION running and leaving presentation features (character floor shadow) untouched.
    /// Fully reversible at runtime.
    /// </summary>
    public class DebugVisualsController : MonoBehaviour
    {
        [Tooltip("Whether debug visuals are shown when the scene starts. Off = launch straight into the clean view.")]
        [SerializeField] private bool visibleOnStart = false;

        /// <summary>True when developer visuals are currently shown.</summary>
        public bool VisualsVisible { get; private set; } = false;

        readonly List<ARPlaneManager> subscribedPlaneManagers = new List<ARPlaneManager>();

        private void OnEnable() => BindPlaneManagers();
        private void OnDisable() => UnbindPlaneManagers();
        private void Start() => SetVisible(visibleOnStart);

        /// <summary>Flip between the clean view and the full debug view.</summary>
        public void Toggle() => SetVisible(!VisualsVisible);

        /// <summary>Show (true) or hide (false) all developer/debug visuals.</summary>
        public void SetVisible(bool visible)
        {
            VisualsVisible = visible;

            // Immersal area-mapping point cloud (the "dots on the wall").
            XRMapVisualization.pointCloudVisible = visible;
            foreach (var vis in FindObjectsByType<XRMapVisualization>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var renderer = vis.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.enabled = visible;
            }

            // AR plane debug mesh only — NEVER disable ARPlaneManager or plane trackables; floor shadow needs them.
            EnsurePlaneDetectionActive();
            foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                foreach (var plane in planeManager.trackables)
                    SetPlaneDebugVisualVisible(plane, visible);
            }

            // Skeleton overlays only (not the character mesh or its floor shadow).
            foreach (var rec in FindObjectsByType<BodyTrackingRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                rec.SetSkeletonVisible(visible);
            foreach (var player in FindObjectsByType<BodyTrackingPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                player.SetSkeletonVisible(visible);

            foreach (var cmp in FindObjectsByType<PlaybackCompareVisualizer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                cmp.SetSuppressed(!visible);

            foreach (var quad in FindObjectsByType<ARTrackedImageDebugQuad>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                quad.enabled = visible;
        }

        void BindPlaneManagers()
        {
            UnbindPlaneManagers();
            foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                planeManager.trackablesChanged.AddListener(OnPlaneTrackablesChanged);
                subscribedPlaneManagers.Add(planeManager);
            }
            EnsurePlaneDetectionActive();
        }

        void UnbindPlaneManagers()
        {
            for (int i = 0; i < subscribedPlaneManagers.Count; i++)
            {
                if (subscribedPlaneManagers[i] != null)
                    subscribedPlaneManagers[i].trackablesChanged.RemoveListener(OnPlaneTrackablesChanged);
            }
            subscribedPlaneManagers.Clear();
        }

        void OnPlaneTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> changes)
        {
            if (VisualsVisible) return;
            foreach (var plane in changes.added)
                SetPlaneDebugVisualVisible(plane, false);
            foreach (var plane in changes.updated)
                SetPlaneDebugVisualVisible(plane, false);
        }

        /// <summary>Keep plane subsystem alive for shadow projection even in clean view.</summary>
        static void EnsurePlaneDetectionActive()
        {
            foreach (var planeManager in FindObjectsByType<ARPlaneManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!planeManager.enabled)
                    planeManager.enabled = true;
            }
        }

        /// <summary>Toggle only the plane debug draw — not detection, not presentation shadows.</summary>
        static void SetPlaneDebugVisualVisible(ARPlane plane, bool visible)
        {
            if (plane == null) return;

            // Only the plane prefab's own debug draw components — never child renderers that might belong elsewhere.
            var meshRenderer = plane.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.GetComponentInParent<ARPresentationShadow>() == null)
                meshRenderer.enabled = visible;

            var lineRenderer = plane.GetComponent<LineRenderer>();
            if (lineRenderer != null)
                lineRenderer.enabled = visible;

            // Feathered-plane sample attaches MeshRenderer on the same GO as ARPlaneMeshVisualizer.
            var meshVisualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
            if (meshVisualizer != null)
                meshVisualizer.enabled = visible;
        }
    }
}
