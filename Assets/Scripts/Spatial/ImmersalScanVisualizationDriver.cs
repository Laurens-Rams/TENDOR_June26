using UnityEngine;
using BodyTracking.Utils;

#if IMMERSAL_SDK_PRESENT
using Immersal;
using Immersal.XR;
#endif

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Drives Immersal localization feedback during scanning:
    ///   • a single on-screen status sphere (grey → green with progress, red on fail, amber on low quality), and
    ///   • optionally the sparse point-cloud dot tint (legacy).
    /// The sphere is hidden once the room anchor is locked so the presentation view stays clean.
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

        [Header("Scan visualization")]
        [Tooltip("Master switch for scan feedback. Off = no sphere and no point-cloud tint.")]
        [SerializeField] private bool showScanVisualization = true;
        [Tooltip("Show the on-screen status sphere while scanning (hidden automatically once the anchor locks). " +
                 "Off by default — the point-cloud dots are the primary feedback.")]
        [SerializeField] private bool useStatusSphere = false;
        [Tooltip("Tint the Immersal point-cloud dots with the scan-progress color (the original visualizer look). " +
                 "On by default.")]
        [SerializeField] private bool tintPointCloud = true;
        [Tooltip("Show the Immersal point-cloud dots spread across the room while scanning/localizing, then hide " +
                 "them once the room anchor locks. The dots' renderMode defaults to EditorOnly (invisible on " +
                 "device), so this also forces runtime rendering while scanning.")]
        [SerializeField] private bool showPointCloudWhileScanning = true;
        [Tooltip("Log point-cloud render state (count, mesh, renderMode, renderer enabled) once per second while " +
                 "this driver runs. Use to diagnose why the scanning dots aren't visible, then turn off.")]
        [SerializeField] private bool logPointCloudDiagnostics = true;

        [Header("Status sphere placement (camera-relative)")]
        [Tooltip("Local position of the sphere relative to the AR camera (z is metres in front of the lens).")]
        [SerializeField] private Vector3 sphereLocalPosition = new Vector3(0f, -0.34f, 1.0f);
        [Tooltip("Sphere diameter (Unity units) at the configured distance.")]
        [SerializeField] private float sphereScale = 0.06f;

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

        GameObject statusSphere;
        Renderer statusSphereRenderer;
        MaterialPropertyBlock statusSphereProps;
        Color lastSphereColor = new Color(-1f, -1f, -1f, -1f);
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        XRMapVisualization[] cachedVisualizations = System.Array.Empty<XRMapVisualization>();
        Color lastAppliedColor = new Color(-1f, -1f, -1f, -1f);

        // Tracks the point-cloud visibility we last drove so we only flip the shared static on state changes
        // (scanning ↔ locked) and don't fight the debug-visuals toggle once scanning is complete.
        bool pointCloudShown;
        bool pointCloudStateKnown;
        float nextDiagnosticsLogTime;

        void Awake()
        {
            if (routeRootProvider == null)
                routeRootProvider = GetComponent<ImmersalRouteRootProvider>();
            if (mapSwitcher == null)
                mapSwitcher = FindAnyObjectByType<ImmersalMapSwitcher>();
        }

        void OnEnable()
        {
            RefreshVisualizationCache();
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
            HideStatusSphere();

            // Don't leave the room covered in dots if this driver is disabled mid-scan.
            if (showPointCloudWhileScanning && pointCloudShown)
                ApplyPointCloudVisible(false);
        }

        void Update()
        {
            // Point-cloud visibility is independent of the sphere/tint feedback, so drive it even when the rest
            // of the scan visualization is disabled.
            UpdatePointCloudVisibility();

            if (logPointCloudDiagnostics && Time.unscaledTime >= nextDiagnosticsLogTime)
            {
                nextDiagnosticsLogTime = Time.unscaledTime + 1f;
                LogPointCloudDiagnostics();
            }

            if (!showScanVisualization)
            {
                HideStatusSphere();
                return;
            }

            UpdateTrackingDeltas();
            Color target = ComputeTargetColor();
            displayedColor = Color.Lerp(displayedColor, target, Time.deltaTime * colorBlendSpeed);

            // Keep tinting the point-cloud dots even after lock (original behaviour: they settle on green and
            // stay visible). Only the optional on-screen status sphere hides once scanning is complete.
            if (tintPointCloud)
                ApplyToVisualizations(displayedColor);

            if (useStatusSphere && !IsScanningComplete())
                UpdateStatusSphere(displayedColor);
            else
                HideStatusSphere();
        }

        void OnLocalizationChanged(bool _)
        {
            if (IsScanningComplete())
            {
                HideStatusSphere();
                return;
            }

            if (routeRootProvider != null && routeRootProvider.IsAnchorFrozen)
                displayedColor = LockedGreen;
        }

        void OnMapLoaded(int _)
        {
            RefreshVisualizationCache();
            ResetScanState();
        }

        void OnMapLoadedInt(int _)
        {
            RefreshVisualizationCache();
            ResetScanState();
        }

        void RefreshVisualizationCache()
        {
            cachedVisualizations = FindObjectsByType<XRMapVisualization>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            lastAppliedColor = new Color(-1f, -1f, -1f, -1f);

            // A freshly loaded map's visualization defaults to EditorOnly (invisible on device); re-assert the
            // current scan-state visibility so newly registered dots render immediately while still scanning.
            pointCloudStateKnown = false;
            UpdatePointCloudVisibility();
        }

        /// <summary>
        /// Show the point-cloud dots while scanning/localizing and hide them once the room anchor is locked.
        /// Only flips the shared <see cref="XRMapVisualization.pointCloudVisible"/> on scanning↔locked changes so
        /// it doesn't fight the debug-visuals toggle after lock.
        /// </summary>
        void UpdatePointCloudVisibility()
        {
            if (!showPointCloudWhileScanning)
                return;

            bool show = !IsScanningComplete();

            if (show)
            {
                // Keep asserting while scanning so it wins over the clean-view default that hides the dots.
                ApplyPointCloudVisible(true);
            }
            else if (!pointCloudStateKnown || pointCloudShown)
            {
                // On the transition to locked, hide the dots once and then leave the static alone.
                ApplyPointCloudVisible(false);
            }

            pointCloudStateKnown = true;
        }

        void ApplyPointCloudVisible(bool show)
        {
            XRMapVisualization.pointCloudVisible = show;

            if (show && cachedVisualizations != null)
            {
                foreach (var vis in cachedVisualizations)
                {
                    if (vis == null) continue;
                    // Default renderMode (EditorOnly) never renders on device; allow runtime rendering so the
                    // scanning dots actually appear (and remain available for the debug-visuals toggle later).
                    if (vis.renderMode != XRMapVisualization.RenderMode.EditorAndRuntime)
                        vis.renderMode = XRMapVisualization.RenderMode.EditorAndRuntime;
                }
            }

            pointCloudShown = show;
        }

        void LogPointCloudDiagnostics()
        {
            int count = cachedVisualizations?.Length ?? 0;
            int withMesh = 0, runtimeMode = 0, rendererEnabled = 0, totalVerts = 0;
            if (cachedVisualizations != null)
            {
                foreach (var vis in cachedVisualizations)
                {
                    if (vis == null) continue;
                    if (vis.Mesh != null) { withMesh++; totalVerts += vis.Mesh.vertexCount; }
                    if (vis.renderMode == XRMapVisualization.RenderMode.EditorAndRuntime) runtimeMode++;
                    var mr = vis.GetComponent<MeshRenderer>();
                    if (mr != null && mr.enabled) rendererEnabled++;
                }
            }

            Debug.Log($"[ImmersalScanViz] cached={count} withMesh={withMesh} verts={totalVerts} " +
                      $"runtimeMode={runtimeMode} rendererEnabled={rendererEnabled} " +
                      $"pointCloudVisible={XRMapVisualization.pointCloudVisible} " +
                      $"scanningComplete={IsScanningComplete()} showWhileScanning={showPointCloudWhileScanning} " +
                      $"isEditor={Application.isEditor}");
        }

        void ResetScanState()
        {
            lastAttemptCount = -1;
            lastSuccessCount = -1;
            failFlashUntil = 0f;
            successPulseUntil = 0f;
            displayedColor = Grey;
            lastSphereColor = new Color(-1f, -1f, -1f, -1f);
            if (tintPointCloud)
                ApplyToVisualizations(Grey);
        }

        /// <summary>Scanning is finished once the room anchor is locked for this session.</summary>
        bool IsScanningComplete()
        {
            return routeRootProvider != null && routeRootProvider.IsAnchorFrozen;
        }

        void HideStatusSphere()
        {
            if (statusSphere != null && statusSphere.activeSelf)
                statusSphere.SetActive(false);
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

        void ApplyToVisualizations(Color color)
        {
            if (ColorsApproximatelyEqual(color, lastAppliedColor))
                return;

            if (cachedVisualizations == null || cachedVisualizations.Length == 0)
                return;

            foreach (var vis in cachedVisualizations)
            {
                if (vis == null) continue;
                vis.pointColor = color;
            }

            lastAppliedColor = color;
        }

        static bool ColorsApproximatelyEqual(Color a, Color b)
        {
            const float eps = 0.004f;
            return Mathf.Abs(a.r - b.r) < eps
                && Mathf.Abs(a.g - b.g) < eps
                && Mathf.Abs(a.b - b.b) < eps
                && Mathf.Abs(a.a - b.a) < eps;
        }

        void UpdateStatusSphere(Color color)
        {
            if (!useStatusSphere)
            {
                HideStatusSphere();
                return;
            }

            if (statusSphere == null && !EnsureStatusSphere())
                return;

            if (!statusSphere.activeSelf)
                statusSphere.SetActive(true);

            if (ColorsApproximatelyEqual(color, lastSphereColor))
                return;

            statusSphereRenderer.GetPropertyBlock(statusSphereProps);
            statusSphereProps.SetColor(BaseColorId, color);
            statusSphereProps.SetColor(ColorId, color);
            statusSphereRenderer.SetPropertyBlock(statusSphereProps);
            lastSphereColor = color;
        }

        bool EnsureStatusSphere()
        {
            Camera cam = ResolveCamera();
            if (cam == null)
                return false;

            statusSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            statusSphere.name = "ImmersalStatusSphere";

            var collider = statusSphere.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            var t = statusSphere.transform;
            t.SetParent(cam.transform, false);
            t.localPosition = sphereLocalPosition;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one * sphereScale;

            statusSphereRenderer = statusSphere.GetComponent<Renderer>();
            statusSphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            statusSphereRenderer.receiveShadows = false;
            statusSphereRenderer.sharedMaterial = DebugVisualizationMaterials.CreateSolidColorMaterial(Grey);

            statusSphereProps = new MaterialPropertyBlock();
            lastSphereColor = new Color(-1f, -1f, -1f, -1f);
            return true;
        }

        static Camera ResolveCamera()
        {
            Camera cam = Camera.main;
            if (cam == null)
                cam = FindAnyObjectByType<Camera>();
            return cam;
        }
#endif
    }
}
