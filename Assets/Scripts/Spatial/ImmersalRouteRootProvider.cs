using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using BodyTracking.Data;

// The Immersal SDK Core package is NOT on a public registry; it must be downloaded from the Immersal
// developer portal and imported via the Package Manager. To keep this project compiling with AND without
// the SDK installed, all Immersal type references live behind the IMMERSAL_SDK_PRESENT scripting define.
//
// After importing the SDK, add IMMERSAL_SDK_PRESENT to:
//   Project Settings > Player > Other Settings > Scripting Define Symbols (iOS + Editor).
// Then verify the namespaces/members in the #if branch against your installed SDK version
// (Immersal SDK 2.x: ImmersalSDK / XRSpace / XRMap live in the Immersal.XR namespace).
#if IMMERSAL_SDK_PRESENT
using Immersal;
using Immersal.XR;
#endif

namespace BodyTracking.Spatial
{
    /// <summary>How the locked RouteRoot anchor behaves after the first confident localization.</summary>
    public enum AnchorStabilityMode
    {
        FreezeOnly,           // Lock once, never move (most stable). Manual re-align only.
        SlowConvergeAverage,  // Ease toward a running average of Immersal fixes (improves, no jumps).
        ContinuousRealign,    // Frequent Immersal corrections (original behaviour, least stable).
    }

    /// <summary>
    /// Primary <see cref="IRouteRootProvider"/> backed by the Immersal SDK. RouteRoot is created as a child
    /// of the Immersal <c>XR Space</c>; because Immersal moves XR Space on localization, a RouteRoot child
    /// stays wall-aligned automatically with no per-frame remapping. Convention (brief section 11):
    /// Y up, X along wall, Z out from wall, 1 unit = 1 m.
    ///
    /// When the SDK is not installed (IMMERSAL_SDK_PRESENT undefined) this behaves as an unavailable,
    /// never-localized stub so <see cref="RouteRootManager"/> transparently falls back to the image target.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class ImmersalRouteRootProvider : MonoBehaviour, IRouteRootProvider
    {
        [Tooltip("Immersal map id this RouteRoot is anchored to (the wall map). Surfaced into recordings.")]
        [SerializeField] private string mapId = "";
        [Tooltip("Optional logical route/problem id surfaced into recordings.")]
        [SerializeField] private string routeId = "";

        [Header("RouteRoot placement (in XR Space local space)")]
        [SerializeField] private Vector3 routeRootLocalPosition = Vector3.zero;
        [SerializeField] private Vector3 routeRootLocalEuler = Vector3.zero;

        [Header("Localization confidence gate")]
        [Tooltip("Minimum Immersal tracking quality (0-3) before RouteRoot counts as localized. Immersal raises " +
                 "this as more map localizations succeed and agree; 3 = its own 'tracking well' threshold. Keep at " +
                 "3 so we only lock on a strong, repeatable fix instead of the first marginal one.")]
        [SerializeField] private int minTrackingQuality = 3;
        [Tooltip("Tracking quality must stay at/above the minimum for this many consecutive frames before we " +
                 "report localized. Filters out brief low-confidence fixes that place RouteRoot slightly off.")]
        [SerializeField] private int requiredStableFrames = 5;
        [Tooltip("Minimum number of SUCCESSFUL Immersal map localizations (not ARKit tracking) required before we " +
                 "lock. 'Too few matches' attempts don't count. Higher = surer lock, slightly longer wait.")]
        [SerializeField] private int minSuccessfulLocalizations = 4;
        [Tooltip("Recent successful localizations must agree within this distance (metres) before locking, so we " +
                 "don't freeze while fixes are still jumping around. The lock pose is the AVERAGE of the agreeing fixes.")]
        [SerializeField] private float freezeAgreementMeters = 0.20f;
        [Tooltip("Recent successful localizations must agree within this rotation (degrees) before locking.")]
        [SerializeField] private float freezeAgreementDegrees = 6f;
        [Tooltip("How many of the most recent successful localizations to compare/average for the lock decision.")]
        [SerializeField] private int freezeSampleWindow = 5;

        [Tooltip("Lock RouteRoot to a fixed room anchor on the first confident localization instead of " +
                 "continuously following Immersal XR Space updates. ARKit world tracking holds the anchor, so " +
                 "recording and playback in the same session share ONE identical reference and localization " +
                 "error cancels out (the replay lands where it was recorded). Immersal is then used only for " +
                 "occasional re-alignment (below). Disable for raw continuous Immersal follow.")]
        [SerializeField] private bool freezeAnchorOnConfidentLocalization = true;

        [Header("Anchor stability mode (easy control)")]
        [Tooltip("FreezeOnly: lock to the ARKit anchor on the first confident fix and never move again (rock-steady, " +
                 "use manual Re-align to re-sync). SlowConvergeAverage: keep refining toward a running average of " +
                 "Immersal fixes so alignment slowly improves and settles, without visible jumps. ContinuousRealign: " +
                 "the original behaviour (frequent Immersal corrections — least stable).")]
        [SerializeField] private AnchorStabilityMode stabilityMode = AnchorStabilityMode.FreezeOnly;
        [Tooltip("SlowConvergeAverage: how fast the anchor eases toward the averaged pose (fraction per second). " +
                 "Lower = smoother/slower.")]
        [SerializeField] private float convergeRatePerSecond = 0.15f;
        [Tooltip("SlowConvergeAverage: weight of each new Immersal fix in the running average (0..1). Lower = a more " +
                 "stable average that trusts the long-term consensus over any single fix.")]
        [SerializeField] private float convergeSampleWeight = 0.04f;

        [Header("Hybrid re-align (ARKit-stable anchor + Immersal corrections)")]
        [Tooltip("Back the room anchor with a real ARAnchor so ARKit actively maintains it (more drift-" +
                 "resistant than a plain transform). Falls back to a plain frozen transform if no " +
                 "ARAnchorManager is present or anchor creation fails.")]
        [SerializeField] private bool useArAnchor = true;
        [Tooltip("ARAnchorManager used to create the room anchor. Auto-found if left empty.")]
        [SerializeField] private ARAnchorManager arAnchorManager;
        [Tooltip("Automatically pull a fresh Immersal correction into the anchor when it is safe: idle (not " +
                 "recording/playing), confidently localized, and the correction is meaningful but not wild.")]
        [SerializeField] private bool autoRealign = true;
        [Tooltip("Ignore auto-corrections smaller than this (m) to avoid constant micro-nudging.")]
        [SerializeField] private float autoRealignMinCorrectionMeters = 0.03f;
        [Tooltip("Ignore auto-corrections larger than this (m) — likely a bad fix or you moved to a new area; " +
                 "use the manual Re-align button for those.")]
        [SerializeField] private float autoRealignMaxCorrectionMeters = 0.5f;
        [Tooltip("Ignore auto-corrections that rotate the anchor more than this (deg).")]
        [SerializeField] private float autoRealignMaxCorrectionDegrees = 12f;
        [Tooltip("Minimum seconds between auto re-aligns.")]
        [SerializeField] private float autoRealignMinIntervalSeconds = 4f;
        [Tooltip("Seconds to smoothly blend the anchor to a corrected pose (0 = instant snap).")]
        [SerializeField] private float realignBlendSeconds = 0.3f;

        /// <summary>
        /// Set by the controller during recording/playback so an auto re-align never jumps the anchor
        /// mid-clip. Manual re-align still works (it overrides this).
        /// </summary>
        public bool SuppressAutoRealign { get; set; }

        private GameObject routeRootObject;
        private bool lastLocalizedState;
        private bool confidentlyLocalized;
        private int stableQualityFrames;
        private bool anchorFrozen;

        public Transform RouteRoot
        {
            get
            {
                EnsureRouteRoot();
                return routeRootObject != null ? routeRootObject.transform : null;
            }
        }

        public string MapId => mapId;
        public string RouteId => routeId;
        public SpatialSourceType Source => SpatialSourceType.Immersal;

        public event Action<bool> OnLocalizationChanged;

        public CoordinateFrame GetRouteRootFrame()
        {
            var t = RouteRoot;
            return t != null ? new CoordinateFrame(t) : default;
        }

        public void SetMapId(string id) => mapId = id;
        public void SetRouteId(string id) => routeId = id;

        void Awake()
        {
            EnsureRouteRoot();
        }

        void Update()
        {
            UpdateLocalizationState();
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

        void OnDestroy()
        {
            if (routeRootObject != null)
                Destroy(routeRootObject);
        }

#if IMMERSAL_SDK_PRESENT
        // ----- Real Immersal-backed implementation -----

        private XRSpace cachedXrSpace;

        public bool IsAvailable
        {
            get
            {
                var sdk = ImmersalSDK.Instance;
                if (sdk == null || GetXrSpace() == null)
                    return false;

                // Require at least one configured XR map with a valid map id. Note: ServerLocalization does
                // NOT need a local mapFile (it matches against the cloud map by id), so we must not require
                // mapFile here or cloud-based setups would never be considered available.
                foreach (var map in UnityEngine.Object.FindObjectsByType<XRMap>(FindObjectsSortMode.None))
                {
                    if (map != null && map.IsConfigured && map.mapId > 0)
                        return true;
                }

                return false;
            }
        }

        // Hybrid anchor runtime state.
        private ARAnchor roomAnchor;
        private bool anchorRequestInFlight;
        private float lastRealignTime;
        private bool manualRealignRequested;
        private bool blending;
        private float blendStartTime;
        private Pose blendFrom;
        private Pose blendTo;
        private Pose averagedPose; // running average of Immersal fixes (SlowConvergeAverage)
        private bool hasAveragedPose;
        private int lastSuccessCount = -1;                       // last seen LocalizationSuccessCount
        private readonly List<Pose> recentFixes = new List<Pose>(); // poses sampled at each new success

        /// <summary>
        /// Localized once the Immersal tracking quality has held at/above <see cref="minTrackingQuality"/>
        /// for <see cref="requiredStableFrames"/> consecutive frames (so we don't anchor on the first, least
        /// accurate fix). Once the anchor is locked it stays localized: ARKit world tracking holds the pose
        /// in the room even if Immersal momentarily loses its fix. Updated in <see cref="UpdateLocalizationState"/>.
        /// </summary>
        public bool IsLocalized => confidentlyLocalized || anchorFrozen;

        /// <summary>True once the RouteRoot has been pinned to a fixed room anchor for this session.</summary>
        public bool IsAnchorFrozen => anchorFrozen;

        /// <summary>
        /// Request a manual re-align: on the next update the anchor is moved to the latest Immersal fix
        /// (blended), ignoring the auto-correction size bands. Use after walking to a new wall/area.
        /// </summary>
        public void RequestRealign() => manualRealignRequested = true;

        /// <summary>Unlock the anchor so the next confident localization re-establishes it from scratch.</summary>
        public void ClearFrozenAnchor()
        {
            anchorFrozen = false;
            blending = false;
            manualRealignRequested = false;
            hasAveragedPose = false;
            stableQualityFrames = 0;
            recentFixes.Clear();
            if (roomAnchor != null)
            {
                if (arAnchorManager != null)
                    arAnchorManager.TryRemoveAnchor(roomAnchor);
                roomAnchor = null;
            }

            // Re-attach to XR Space so RouteRoot follows live Immersal again until it re-locks.
            if (routeRootObject != null)
            {
                var space = GetXrSpace();
                if (space != null)
                    routeRootObject.transform.SetParent(space.transform, false);
                routeRootObject.transform.localPosition = routeRootLocalPosition;
                routeRootObject.transform.localRotation = Quaternion.Euler(routeRootLocalEuler);
            }
        }

        private XRSpace GetXrSpace()
        {
            if (cachedXrSpace == null)
                cachedXrSpace = UnityEngine.Object.FindAnyObjectByType<XRSpace>();
            return cachedXrSpace;
        }

        private void UpdateLocalizationState()
        {
            int quality = GetCurrentTrackingQuality();
            int successCount = GetLocalizationSuccessCount();

            // Sample the Immersal target pose every time a NEW successful map localization lands. Failed
            // attempts ("Too few matches") do not increment successCount, so they are ignored here.
            if (lastSuccessCount >= 0 && successCount > lastSuccessCount)
            {
                recentFixes.Add(GetImmersalTargetPose());
                while (recentFixes.Count > Mathf.Max(1, freezeSampleWindow))
                    recentFixes.RemoveAt(0);
            }
            lastSuccessCount = successCount;

            if (quality >= minTrackingQuality)
                stableQualityFrames++;
            else
                stableQualityFrames = 0;

            // Confident only when ALL hold: quality high for enough frames, enough successful localizations have
            // happened, and the recent successful fixes AGREE (so we never lock while the pose is still jumping).
            bool qualityHeld = stableQualityFrames >= Mathf.Max(1, requiredStableFrames);
            bool enoughSuccess = successCount >= Mathf.Max(1, minSuccessfulLocalizations);
            bool fixesAgree = TryGetAgreedFix(out Pose agreedPose);
            confidentlyLocalized = qualityHeld && enoughSuccess && fixesAgree;

            // First confident fix locks the room anchor (ARKit holds it from here on) at the AVERAGE of the
            // agreeing fixes rather than a single noisy sample.
            if (confidentlyLocalized && freezeAnchorOnConfidentLocalization && !anchorFrozen)
                EstablishRoomAnchor(agreedPose);

            UpdateHybridAnchor();
        }

        /// <summary>
        /// True when we have enough recent successful localizations and they cluster tightly (within the
        /// agreement bands). Outputs the averaged pose of the agreeing samples to use as the lock pose.
        /// </summary>
        private bool TryGetAgreedFix(out Pose agreed)
        {
            agreed = default;
            int need = Mathf.Clamp(minSuccessfulLocalizations, 1, Mathf.Max(1, freezeSampleWindow));
            if (recentFixes.Count < need)
                return false;

            // Average position and rotation of the windowed samples.
            Vector3 avgPos = Vector3.zero;
            foreach (var p in recentFixes) avgPos += p.position;
            avgPos /= recentFixes.Count;

            Quaternion avgRot = recentFixes[0].rotation;
            for (int i = 1; i < recentFixes.Count; i++)
                avgRot = Quaternion.Slerp(avgRot, recentFixes[i].rotation, 1f / (i + 1));

            // Reject if any sample strays beyond the agreement bands (fixes are still unstable).
            foreach (var p in recentFixes)
            {
                if (Vector3.Distance(p.position, avgPos) > freezeAgreementMeters)
                    return false;
                if (Quaternion.Angle(p.rotation, avgRot) > freezeAgreementDegrees)
                    return false;
            }

            agreed = new Pose(avgPos, avgRot);
            return true;
        }

        /// <summary>
        /// The world pose RouteRoot would have if it followed Immersal live right now (XR Space pose composed
        /// with the configured local offset). This is the "fresh Immersal fix" we re-align toward.
        /// </summary>
        private Pose GetImmersalTargetPose()
        {
            var space = GetXrSpace();
            if (space == null)
                return new Pose(routeRootObject.transform.position, routeRootObject.transform.rotation);

            Vector3 pos = space.transform.TransformPoint(routeRootLocalPosition);
            Quaternion rot = space.transform.rotation * Quaternion.Euler(routeRootLocalEuler);
            return new Pose(pos, rot);
        }

        private void EstablishRoomAnchor() => EstablishRoomAnchor(GetImmersalTargetPose());

        private void EstablishRoomAnchor(Pose target)
        {
            EnsureRouteRoot();
            anchorFrozen = true;
            lastRealignTime = Time.time;

            // Detach from XR Space and hold the localized world pose immediately so recording can start now;
            // the ARAnchor (if enabled) is attached asynchronously and adopts this pose when it arrives.
            routeRootObject.transform.SetParent(null, true);
            SetRouteRootWorld(target);

            if (useArAnchor)
                RequestRoomAnchorAsync(target);

            Debug.Log($"[ImmersalRouteRootProvider] Room anchor locked at pos={target.position} rot={target.rotation.eulerAngles} " +
                      $"(ARAnchor={useArAnchor}, successes={GetLocalizationSuccessCount()}, agreedFrom={recentFixes.Count} fixes).");
        }

        private void UpdateHybridAnchor()
        {
            if (!anchorFrozen)
                return;

            // Drive an in-progress smooth blend to a corrected pose.
            if (blending)
            {
                float t = realignBlendSeconds > 0f ? (Time.time - blendStartTime) / realignBlendSeconds : 1f;
                t = Mathf.Clamp01(t);
                Vector3 p = Vector3.Lerp(blendFrom.position, blendTo.position, t);
                Quaternion r = Quaternion.Slerp(blendFrom.rotation, blendTo.rotation, t);
                SetRouteRootWorld(new Pose(p, r));
                if (t >= 1f)
                    blending = false;
                return;
            }

            // Keep RouteRoot parented to the ARKit anchor once it exists so it rides ARKit's maintenance.
            EnsureRoomAnchorParenting();

            bool manual = manualRealignRequested;
            manualRealignRequested = false;

            // Manual re-align always applies (no size band, any mode) so you can deliberately re-sync.
            if (manual)
            {
                Pose mtarget = GetImmersalTargetPose();
                BeginRealign(mtarget);
                averagedPose = mtarget;
                hasAveragedPose = true;
                Debug.Log("[ImmersalRouteRootProvider] Manual re-align applied.");
                return;
            }

            // Mode-gated automatic behaviour. FreezeOnly never moves on its own.
            if (stabilityMode == AnchorStabilityMode.FreezeOnly)
                return;
            if (SuppressAutoRealign || !confidentlyLocalized)
                return;

            Pose target = GetImmersalTargetPose();
            Pose current = new Pose(routeRootObject.transform.position, routeRootObject.transform.rotation);
            float dPos = Vector3.Distance(current.position, target.position);
            float dRot = Quaternion.Angle(current.rotation, target.rotation);

            // Reject wild fixes (bad localization / moved to a new area) in every auto mode.
            if (dPos > autoRealignMaxCorrectionMeters || dRot > autoRealignMaxCorrectionDegrees)
                return;

            if (stabilityMode == AnchorStabilityMode.SlowConvergeAverage)
            {
                // Maintain a running average of Immersal fixes and ease the anchor toward it. No interval gating,
                // no hard band — the slow easing makes corrections imperceptible while alignment keeps improving.
                float w = Mathf.Clamp01(convergeSampleWeight);
                if (!hasAveragedPose)
                {
                    averagedPose = target;
                    hasAveragedPose = true;
                }
                else
                {
                    averagedPose.position = Vector3.Lerp(averagedPose.position, target.position, w);
                    averagedPose.rotation = Quaternion.Slerp(averagedPose.rotation, target.rotation, w);
                }

                float k = Mathf.Clamp01(convergeRatePerSecond * Time.deltaTime);
                Vector3 p = Vector3.Lerp(current.position, averagedPose.position, k);
                Quaternion r = Quaternion.Slerp(current.rotation, averagedPose.rotation, k);
                SetRouteRootWorld(new Pose(p, r));
                return;
            }

            // ContinuousRealign: original behaviour — periodic, meaningful-but-not-wild corrections, blended.
            if (!autoRealign)
                return;
            if (Time.time - lastRealignTime < autoRealignMinIntervalSeconds)
                return;
            if (dPos < autoRealignMinCorrectionMeters && dRot < 1f)
                return;

            BeginRealign(target);
        }

        private void BeginRealign(Pose target)
        {
            lastRealignTime = Time.time;
            if (realignBlendSeconds > 0f)
            {
                blendFrom = new Pose(routeRootObject.transform.position, routeRootObject.transform.rotation);
                blendTo = target;
                blendStartTime = Time.time;
                blending = true;
            }
            else
            {
                SetRouteRootWorld(target);
            }
        }

        private void SetRouteRootWorld(Pose pose)
        {
            if (routeRootObject != null)
                routeRootObject.transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        private void EnsureRoomAnchorParenting()
        {
            if (!useArAnchor)
                return;

            if (arAnchorManager == null)
                arAnchorManager = UnityEngine.Object.FindAnyObjectByType<ARAnchorManager>();

            if (roomAnchor == null)
            {
                if (!anchorRequestInFlight && routeRootObject != null)
                    RequestRoomAnchorAsync(new Pose(routeRootObject.transform.position, routeRootObject.transform.rotation));
                return;
            }

            if (routeRootObject != null && routeRootObject.transform.parent != roomAnchor.transform)
                routeRootObject.transform.SetParent(roomAnchor.transform, true);
        }

        private async void RequestRoomAnchorAsync(Pose pose)
        {
            if (anchorRequestInFlight || roomAnchor != null)
                return;

            if (arAnchorManager == null)
                arAnchorManager = UnityEngine.Object.FindAnyObjectByType<ARAnchorManager>();
            if (arAnchorManager == null || !arAnchorManager.enabled)
                return;

            anchorRequestInFlight = true;
            try
            {
                var result = await arAnchorManager.TryAddAnchorAsync(pose);
                if (result.status.IsSuccess() && result.value != null)
                {
                    roomAnchor = result.value;
                    if (routeRootObject != null)
                        routeRootObject.transform.SetParent(roomAnchor.transform, true);
                    Debug.Log("[ImmersalRouteRootProvider] Room ARAnchor created; RouteRoot now ARKit-anchored.");
                }
                else
                {
                    Debug.LogWarning("[ImmersalRouteRootProvider] ARAnchor creation failed; using plain frozen transform.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ImmersalRouteRootProvider] ARAnchor creation threw: {e.Message}; using plain frozen transform.");
            }
            finally
            {
                anchorRequestInFlight = false;
            }
        }

        /// <summary>
        /// Best-effort read of the SDK's tracking quality. Kept defensive so minor SDK API differences don't
        /// break the build; falls back to "XR Space has moved" as a weak fix when the analyzer is unreadable.
        /// </summary>
        private int GetCurrentTrackingQuality()
        {
            var sdk = ImmersalSDK.Instance;
            if (sdk != null && sdk.TrackingAnalyzer != null)
                return sdk.TrackingStatus.TrackingQuality;

            var space = GetXrSpace();
            return (space != null && space.transform.hasChanged) ? minTrackingQuality : 0;
        }

        /// <summary>
        /// Number of SUCCESSFUL Immersal map localizations so far (distinct from ARKit tracking quality and from
        /// failed "Too few matches" attempts). This is the signal that actually reflects map confidence.
        /// </summary>
        private int GetLocalizationSuccessCount()
        {
            var sdk = ImmersalSDK.Instance;
            if (sdk != null && sdk.TrackingAnalyzer != null && sdk.TrackingAnalyzer.TrackingStatus != null)
                return sdk.TrackingAnalyzer.TrackingStatus.LocalizationSuccessCount;
            return 0;
        }

        private void EnsureRouteRoot()
        {
            if (routeRootObject != null) return;

            routeRootObject = new GameObject("RouteRoot_Immersal");
            // Before the anchor is locked, follow XR Space so RouteRoot stays roughly aligned during scanning.
            if (!anchorFrozen)
            {
                var space = GetXrSpace();
                if (space != null)
                    routeRootObject.transform.SetParent(space.transform, false);
            }

            routeRootObject.transform.localPosition = routeRootLocalPosition;
            routeRootObject.transform.localRotation = Quaternion.Euler(routeRootLocalEuler);
            routeRootObject.transform.localScale = Vector3.one;
        }
#else
        // ----- Stub used when the Immersal SDK is not installed -----

        public bool IsAvailable => false;
        public bool IsLocalized => false;
        public bool IsAnchorFrozen => false;

        public void RequestRealign() { }
        public void ClearFrozenAnchor() { }

        private void UpdateLocalizationState() { }

        private void EnsureRouteRoot()
        {
            if (routeRootObject != null) return;
            // RouteRoot still exists (so callers never NRE) but is never reported as localized.
            routeRootObject = new GameObject("RouteRoot_Immersal_Stub");
            routeRootObject.transform.localPosition = routeRootLocalPosition;
            routeRootObject.transform.localRotation = Quaternion.Euler(routeRootLocalEuler);
            routeRootObject.transform.localScale = Vector3.one;
        }
#endif
    }
}
