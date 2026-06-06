using System;
using UnityEngine;
using BodyTracking.Data;

namespace BodyTracking.Spatial
{
    /// <summary>Which spatial source the RouteRootManager is allowed to use for the route frame.</summary>
    public enum SpatialSourcePolicy
    {
        ImmersalOnly,
        ImageTargetOnly,
        ImmersalPreferredWithMarkerFallback,
    }

    /// <summary>
    /// Selects the active <see cref="IRouteRootProvider"/> (Immersal primary, image target fallback) and
    /// exposes a single stable RouteRoot frame to the recorder/player. Also hosts a manual route
    /// offset/yaw correction hook (brief sections 13/14) for nudging visible misalignment without
    /// re-recording. The correction is applied via a child transform so the underlying provider math is
    /// untouched (defaults to identity = no effect).
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class RouteRootManager : MonoBehaviour, IRouteRootProvider
    {
        [Header("Providers (auto-found if left empty)")]
        [SerializeField] private ImmersalRouteRootProvider immersalProvider;
        [SerializeField] private ImageTargetRouteRootProvider imageTargetProvider;

        [Header("Source policy")]
        [Tooltip("ImmersalOnly: use only Immersal as the route frame (never the image marker) so record and " +
                 "playback always share the same frame and the source can't switch mid-session. ImageTargetOnly: " +
                 "use only the marker. ImmersalPreferredWithMarkerFallback: original behaviour (marker used until " +
                 "Immersal localizes).")]
        [SerializeField] private SpatialSourcePolicy sourcePolicy = SpatialSourcePolicy.ImmersalOnly;
        [Tooltip("Once a provider has been selected (localized) this session, stay on it and never switch frames, " +
                 "even if another provider later reports localized. Prevents mid-session placement jumps.")]
        [SerializeField] private bool lockSourceForSession = true;

        private IRouteRootProvider sessionLockedProvider;

        [Header("Manual route correction (applied to the active RouteRoot)")]
        [Tooltip("Local offset (metres) added on top of the provider RouteRoot to correct visible misalignment.")]
        [SerializeField] private Vector3 routeOffset = Vector3.zero;
        [Tooltip("Local yaw (degrees about up) added on top of the provider RouteRoot.")]
        [SerializeField] private float routeYawDegrees = 0f;

        private GameObject correctionRoot;
        private IRouteRootProvider activeProvider;
        private bool lastLocalizedState;
        private float nextProviderResolveTime;
        private const float ProviderResolveRetrySeconds = 1f;

        public event Action<bool> OnLocalizationChanged;

        public ImageTargetRouteRootProvider ImageTargetProvider => imageTargetProvider;
        public ImmersalRouteRootProvider ImmersalProvider => immersalProvider;

        public IRouteRootProvider ActiveProvider
        {
            get
            {
                SelectActiveProvider();
                return activeProvider;
            }
        }

        public Transform RouteRoot
        {
            get
            {
                SelectActiveProvider();
                EnsureCorrectionRoot();
                Transform baseRoot = activeProvider != null ? activeProvider.RouteRoot : null;
                if (baseRoot == null)
                    return correctionRoot.transform;

                // Re-parent under the active provider's RouteRoot and apply the manual correction.
                if (correctionRoot.transform.parent != baseRoot)
                    correctionRoot.transform.SetParent(baseRoot, false);
                correctionRoot.transform.localPosition = routeOffset;
                correctionRoot.transform.localRotation = Quaternion.Euler(0f, routeYawDegrees, 0f);
                correctionRoot.transform.localScale = Vector3.one;
                return correctionRoot.transform;
            }
        }

        public bool IsLocalized
        {
            get
            {
                SelectActiveProvider();
                return activeProvider != null && activeProvider.IsLocalized;
            }
        }

        public bool IsAvailable
        {
            get
            {
                SelectActiveProvider();
                return activeProvider != null && activeProvider.IsAvailable;
            }
        }

        public string MapId
        {
            get
            {
                SelectActiveProvider();
                return activeProvider != null ? activeProvider.MapId : "";
            }
        }

        public string RouteId
        {
            get
            {
                SelectActiveProvider();
                return activeProvider != null ? activeProvider.RouteId : "";
            }
        }

        public SpatialSourceType Source
        {
            get
            {
                SelectActiveProvider();
                return activeProvider != null ? activeProvider.Source : SpatialSourceType.None;
            }
        }

        public CoordinateFrame GetRouteRootFrame()
        {
            var t = RouteRoot;
            return t != null ? new CoordinateFrame(t) : default;
        }

        // --- Manual correction hook (stubbed UI wires into these) ---
        public Vector3 RouteOffset { get => routeOffset; set => routeOffset = value; }
        public float RouteYawDegrees { get => routeYawDegrees; set => routeYawDegrees = value; }
        public void NudgeRouteOffset(Vector3 delta) => routeOffset += delta;
        public void NudgeRouteYaw(float deltaDegrees) => routeYawDegrees += deltaDegrees;
        public void ResetRouteCorrection() { routeOffset = Vector3.zero; routeYawDegrees = 0f; }

        void Awake()
        {
            ResolveMissingProviders(force: true);
            EnsureCorrectionRoot();
        }

        void Update()
        {
            SelectActiveProvider();
            bool now = IsLocalized;
            if (now != lastLocalizedState)
            {
                lastLocalizedState = now;
                OnLocalizationChanged?.Invoke(now);
            }
        }

        /// <summary>
        /// Picks the route frame according to <see cref="sourcePolicy"/>. With ImmersalOnly the image marker is
        /// never used as the frame (no mid-session switching). When <see cref="lockSourceForSession"/> is on, the
        /// first provider that localizes is kept for the rest of the session so record and playback never jump
        /// between different frames.
        /// </summary>
        private void SelectActiveProvider()
        {
            ResolveMissingProviders();

            // Once locked to a provider for the session, never switch away from it.
            if (lockSourceForSession && sessionLockedProvider != null && sessionLockedProvider.IsAvailable)
            {
                activeProvider = sessionLockedProvider;
                return;
            }

            bool immersalAvail = immersalProvider != null && immersalProvider.IsAvailable;
            bool immersalLoc = immersalAvail && immersalProvider.IsLocalized;
            bool markerAvail = imageTargetProvider != null && imageTargetProvider.IsAvailable;

            switch (sourcePolicy)
            {
                case SpatialSourcePolicy.ImmersalOnly:
                    // Only Immersal is ever the route frame. Stays null (app waits for the green lock) until Immersal
                    // confidently localizes — by design, so recordings are always in the Immersal frame.
                    activeProvider = immersalAvail ? immersalProvider : null;
                    break;

                case SpatialSourcePolicy.ImageTargetOnly:
                    activeProvider = markerAvail ? (IRouteRootProvider)imageTargetProvider : null;
                    break;

                default: // ImmersalPreferredWithMarkerFallback
                    if (immersalLoc) activeProvider = immersalProvider;
                    else if (markerAvail) activeProvider = imageTargetProvider;
                    else if (immersalAvail) activeProvider = immersalProvider;
                    else activeProvider = null;
                    break;
            }

            // Remember the first confidently-localized provider for the rest of the session.
            if (lockSourceForSession && sessionLockedProvider == null &&
                activeProvider != null && activeProvider.IsLocalized)
            {
                sessionLockedProvider = activeProvider;
            }
        }

        private void ResolveMissingProviders(bool force = false)
        {
            if (!force && immersalProvider != null && imageTargetProvider != null)
                return;

            float now = Application.isPlaying ? Time.unscaledTime : 0f;
            if (!force && now < nextProviderResolveTime)
                return;

            // Avoid per-frame scene searches while still catching providers added during startup.
            nextProviderResolveTime = now + ProviderResolveRetrySeconds;
            if (immersalProvider == null)
                immersalProvider = UnityEngine.Object.FindAnyObjectByType<ImmersalRouteRootProvider>();
            if (imageTargetProvider == null)
                imageTargetProvider = UnityEngine.Object.FindAnyObjectByType<ImageTargetRouteRootProvider>();
        }

        private void EnsureCorrectionRoot()
        {
            if (correctionRoot == null)
                correctionRoot = new GameObject("RouteRoot_Corrected");
        }

        void OnDestroy()
        {
            if (correctionRoot != null)
                Destroy(correctionRoot);
        }
    }
}
