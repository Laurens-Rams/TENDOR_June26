using UnityEngine;

#if IMMERSAL_SDK_PRESENT
using Immersal;
using Immersal.XR;
#endif

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Drives Immersal sparse point-cloud dot colors during localization so scan progress is readable:
    /// grey (not started) → greener with each successful map match → solid green when locked;
    /// brief red flash on failed attempts; amber when matches exist but tracking quality is below threshold.
    /// </summary>
    [DisallowMultipleComponent]
    public class ImmersalScanVisualizationDriver : MonoBehaviour
    {
        [Tooltip("Successful localizations needed before the dots reach full green (match ImmersalRouteRootProvider).")]
        [SerializeField] private int minSuccessfulLocalizations = 4;
        [Tooltip("Immersal tracking quality (0–3) treated as 'confident'. Below this tints dots amber.")]
        [SerializeField] private int minTrackingQuality = 3;

        [Header("Feedback timing")]
        [SerializeField] private float failFlashSeconds = 0.45f;
        [SerializeField] private float successPulseSeconds = 0.28f;
        [SerializeField] private float colorBlendSpeed = 6f;

#if IMMERSAL_SDK_PRESENT
        [SerializeField] private ImmersalRouteRootProvider routeRootProvider;
        [SerializeField] private ImmersalMapSwitcher mapSwitcher;

        static readonly Color Grey = new Color(0.55f, 0.57f, 0.60f, 1f);
        static readonly Color LockedGreen = new Color(0.19f, 0.82f, 0.35f, 1f);
        static readonly Color FailRed = new Color(1.00f, 0.27f, 0.23f, 1f);
        static readonly Color LowQualityAmber = new Color(1.00f, 0.62f, 0.04f, 1f);
        static readonly Color SuccessPulse = new Color(0.45f, 1.00f, 0.55f, 1f);

        Color displayedColor = Grey;
        int lastAttemptCount = -1;
        int lastSuccessCount = -1;
        float failFlashUntil;
        float successPulseUntil;

        void Awake()
        {
            if (routeRootProvider == null)
                routeRootProvider = GetComponent<ImmersalRouteRootProvider>();
            if (mapSwitcher == null)
                mapSwitcher = FindAnyObjectByType<ImmersalMapSwitcher>();
        }

        void OnEnable()
        {
            ResetScanState();
            MapManager.MapRegisteredAndLoaded?.AddListener(OnMapLoaded);
            if (mapSwitcher != null)
                mapSwitcher.OnMapSwitched += OnMapLoadedInt;
            if (routeRootProvider != null)
                routeRootProvider.OnLocalizationChanged += OnLocalizationChanged;
        }

        void OnDisable()
        {
            MapManager.MapRegisteredAndLoaded?.RemoveListener(OnMapLoaded);
            if (mapSwitcher != null)
                mapSwitcher.OnMapSwitched -= OnMapLoadedInt;
            if (routeRootProvider != null)
                routeRootProvider.OnLocalizationChanged -= OnLocalizationChanged;
        }

        void Update()
        {
            UpdateTrackingDeltas();
            Color target = ComputeTargetColor();
            displayedColor = Color.Lerp(displayedColor, target, Time.deltaTime * colorBlendSpeed);
            ApplyToVisualizations(displayedColor);
        }

        void OnLocalizationChanged(bool _)
        {
            if (routeRootProvider != null && routeRootProvider.IsAnchorFrozen)
                displayedColor = LockedGreen;
        }

        void OnMapLoaded(int _) => ResetScanState();
        void OnMapLoadedInt(int _) => ResetScanState();

        void ResetScanState()
        {
            lastAttemptCount = -1;
            lastSuccessCount = -1;
            failFlashUntil = 0f;
            successPulseUntil = 0f;
            displayedColor = Grey;
            ApplyToVisualizations(Grey);
        }

        void UpdateTrackingDeltas()
        {
            var status = GetTrackingStatus();
            if (status == null)
                return;

            if (lastAttemptCount < 0)
            {
                lastAttemptCount = status.LocalizationAttemptCount;
                lastSuccessCount = status.LocalizationSuccessCount;
                return;
            }

            bool newAttempt = status.LocalizationAttemptCount > lastAttemptCount;
            bool newSuccess = status.LocalizationSuccessCount > lastSuccessCount;

            if (newAttempt && !newSuccess)
                failFlashUntil = Time.time + failFlashSeconds;

            if (newSuccess)
                successPulseUntil = Time.time + successPulseSeconds;

            lastAttemptCount = status.LocalizationAttemptCount;
            lastSuccessCount = status.LocalizationSuccessCount;
        }

        Color ComputeTargetColor()
        {
            if (routeRootProvider != null && routeRootProvider.IsAnchorFrozen)
                return LockedGreen;

            var status = GetTrackingStatus();
            if (status == null)
                return Grey;

            int successes = status.LocalizationSuccessCount;
            int quality = status.TrackingQuality;
            int need = Mathf.Max(1, routeRootProvider != null
                ? routeRootProvider.MinSuccessfulLocalizations
                : minSuccessfulLocalizations);
            int qualityThreshold = routeRootProvider != null
                ? routeRootProvider.MinTrackingQuality
                : minTrackingQuality;

            if (Time.time < failFlashUntil)
                return FailRed;

            Color progress;
            if (successes <= 0)
            {
                progress = Grey;
            }
            else
            {
                float t = Mathf.Clamp01(successes / (float)need);
                progress = Color.Lerp(Grey, LockedGreen, t);
            }

            if (routeRootProvider != null && routeRootProvider.IsLocalized)
                progress = LockedGreen;

            if (successes > 0 && quality < qualityThreshold)
                progress = Color.Lerp(progress, LowQualityAmber, 0.55f);

            if (Time.time < successPulseUntil)
                progress = Color.Lerp(progress, SuccessPulse, 0.65f);

            return progress;
        }

        static ITrackingStatus GetTrackingStatus()
        {
            var sdk = ImmersalSDK.Instance;
            return sdk != null ? sdk.TrackingStatus : null;
        }

        static void ApplyToVisualizations(Color color)
        {
            foreach (var vis in FindObjectsByType<XRMapVisualization>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (vis == null) continue;
                vis.pointColor = color;
            }
        }
#endif
    }
}
