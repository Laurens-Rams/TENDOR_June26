using System;
using UnityEngine;
using BodyTracking.AR;
using BodyTracking.Data;

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Fallback <see cref="IRouteRootProvider"/> that wraps the existing AR image target /
    /// ARWorldMap behaviour. RouteRoot follows the live tracked image while it is visible, and can be
    /// frozen (held in the room by ARKit world tracking) or pinned to a saved world-map anchor pose so
    /// legacy v1/v2 recordings keep working exactly as before.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class ImageTargetRouteRootProvider : MonoBehaviour, IRouteRootProvider
    {
        [SerializeField] private ARImageTargetManager imageTargetManager;
        [SerializeField] private ARWorldMapPersistence worldMapPersistence;
        [Tooltip("Optional logical route/problem id surfaced into recordings.")]
        [SerializeField] private string routeId = "";

        private GameObject routeRootObject;
        private bool frozen;
        private bool hasEverLocalized;
        private bool lastLocalizedState;

        public Transform RouteRoot
        {
            get
            {
                EnsureRouteRoot();
                return routeRootObject.transform;
            }
        }

        public bool IsLocalized
        {
            get
            {
                bool imageReady = imageTargetManager != null && imageTargetManager.IsImageDetected;
                bool worldMapReady = worldMapPersistence != null && worldMapPersistence.IsRelocalized;
                // Once localized, a frozen frame stays valid: ARKit world tracking holds the pose in the
                // room as the user moves away from the marker (matches the legacy FreezeReferenceFrame path).
                return imageReady || worldMapReady || (frozen && hasEverLocalized);
            }
        }

        public bool IsAvailable => imageTargetManager != null;

        public string MapId => imageTargetManager != null ? imageTargetManager.targetImageName : "";

        public string RouteId => routeId;

        public SpatialSourceType Source => SpatialSourceType.ImageTarget;

        public event Action<bool> OnLocalizationChanged;

        public CoordinateFrame GetRouteRootFrame() => new CoordinateFrame(RouteRoot);

        void Awake()
        {
            if (imageTargetManager == null)
                imageTargetManager = UnityEngine.Object.FindAnyObjectByType<ARImageTargetManager>();
            if (worldMapPersistence == null)
                worldMapPersistence = UnityEngine.Object.FindAnyObjectByType<ARWorldMapPersistence>();
            EnsureRouteRoot();
        }

        void OnEnable()
        {
            if (imageTargetManager != null)
            {
                imageTargetManager.OnImageTargetDetected += HandleImageDetected;
                imageTargetManager.OnImageTargetUpdated += HandleImageUpdated;
                imageTargetManager.OnImageTargetLost += HandleImageLost;
            }
            lastLocalizedState = IsLocalized;
        }

        void OnDisable()
        {
            if (imageTargetManager != null)
            {
                imageTargetManager.OnImageTargetDetected -= HandleImageDetected;
                imageTargetManager.OnImageTargetUpdated -= HandleImageUpdated;
                imageTargetManager.OnImageTargetLost -= HandleImageLost;
            }
        }

        void Update()
        {
            // Follow the live marker pose while it is visible and we are not pinned to a frozen anchor.
            if (!frozen && imageTargetManager != null && imageTargetManager.IsImageDetected)
            {
                var t = imageTargetManager.ImageTargetTransform;
                if (t != null)
                    RouteRoot.SetPositionAndRotation(t.position, t.rotation);
            }

            RaiseLocalizationChangedIfNeeded();
        }

        public void SetRouteId(string id) => routeId = id;

        /// <summary>
        /// Pin RouteRoot to a fixed world pose (e.g. a relocalized world-map anchor for a recording) and
        /// stop following the live marker.
        /// </summary>
        public void SetAnchorPose(Vector3 position, Quaternion rotation)
        {
            EnsureRouteRoot();
            routeRootObject.transform.SetPositionAndRotation(position, rotation);
            routeRootObject.transform.localScale = Vector3.one;
            frozen = true;
            hasEverLocalized = true;
            RaiseLocalizationChangedIfNeeded();
        }

        /// <summary>Hold the current RouteRoot pose in the room (ARKit world tracking keeps it anchored).</summary>
        public void FreezeAtCurrentPose()
        {
            frozen = true;
            RaiseLocalizationChangedIfNeeded();
        }

        /// <summary>Resume following the live tracked image each frame.</summary>
        public void FollowLiveMarker()
        {
            frozen = false;
            RaiseLocalizationChangedIfNeeded();
        }

        private void HandleImageDetected(Transform t)
        {
            frozen = false;
            hasEverLocalized = true;
            if (t != null)
                RouteRoot.SetPositionAndRotation(t.position, t.rotation);
            RaiseLocalizationChangedIfNeeded();
        }

        private void HandleImageUpdated(Transform t)
        {
            if (!frozen && t != null)
                RouteRoot.SetPositionAndRotation(t.position, t.rotation);
        }

        private void HandleImageLost()
        {
            // Keep the last pose; ARKit world tracking holds it in the room until the marker is re-detected.
            frozen = true;
            RaiseLocalizationChangedIfNeeded();
        }

        private void RaiseLocalizationChangedIfNeeded()
        {
            bool now = IsLocalized;
            if (now != lastLocalizedState)
            {
                lastLocalizedState = now;
                OnLocalizationChanged?.Invoke(now);
            }
        }

        private void EnsureRouteRoot()
        {
            if (routeRootObject == null)
            {
                routeRootObject = new GameObject("RouteRoot_ImageTarget");
            }
        }

        void OnDestroy()
        {
            if (routeRootObject != null)
                Destroy(routeRootObject);
        }
    }
}
