using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if IMMERSAL_SDK_PRESENT
using Immersal;
using Immersal.REST;
using Immersal.XR;
#endif

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Runtime Immersal map switching for device builds. Removes the active XR map, downloads map data
    /// and the sparse point-cloud visualization from the Immersal cloud, registers the new map with the
    /// localizer, and updates <see cref="ImmersalRouteRootProvider.MapId"/> so recordings/fusion metadata
    /// stay consistent.
    /// </summary>
    public class ImmersalMapSwitcher : MonoBehaviour
    {
        public const string PlayerPrefsKey = "tendor_immersal_map_id";

        [SerializeField] private RouteRootManager routeRootManager;
        [Tooltip("On launch, if PlayerPrefs holds a different map id than the scene default, switch to it after the SDK is ready.")]
        [SerializeField] private bool restoreSavedMapOnStart = true;
        [Tooltip("Seconds to wait for ARSession.SessionTracking before map operations fail visibly.")]
        [SerializeField] private float arTrackingTimeoutSeconds = 45f;

        public enum StatusSeverity { Info, Working, Success, Error }

        public event Action<string> OnStatusChanged;
        public event Action<int> OnMapSwitched;

        public bool IsSwitching { get; private set; }
        /// <summary>Map id waiting to load after the current switch finishes (0 = none).</summary>
        public int PendingMapId { get; private set; }
        public int ActiveMapId { get; private set; } = -1;
        public string StatusMessage { get; private set; } = "";
        public StatusSeverity LastStatusSeverity { get; private set; } = StatusSeverity.Info;

        Coroutine switchQueueCoroutine;
        int queuedMapId;

        void Awake()
        {
            if (routeRootManager == null)
                routeRootManager = UnityEngine.Object.FindAnyObjectByType<RouteRootManager>();
        }

        void Start()
        {
#if IMMERSAL_SDK_PRESENT
            SyncActiveMapFromScene();
            if (restoreSavedMapOnStart && PlayerPrefs.HasKey(PlayerPrefsKey))
            {
                int saved = PlayerPrefs.GetInt(PlayerPrefsKey);
                if (saved > 0 && saved != ActiveMapId)
                    RequestSwitch(saved, "Restoring saved map");
            }
#endif
        }

        /// <summary>Parse a user-entered map id string and begin switching.</summary>
        public void SwitchToMapFromInput(string idText)
        {
            if (!int.TryParse(idText?.Trim(), out int id) || id <= 0)
            {
                SetStatus("Enter a valid numeric map ID", StatusSeverity.Error);
                return;
            }

            RequestSwitch(id, "User requested map");
        }

        /// <summary>Switch localization to a different Immersal map by id.</summary>
        public void SwitchToMap(int mapId) => RequestSwitch(mapId, "SwitchToMap");

        void RequestSwitch(int mapId, string reason)
        {
#if IMMERSAL_SDK_PRESENT
            if (mapId <= 0)
            {
                SetStatus("Enter a valid map ID", StatusSeverity.Error);
                return;
            }

            if (!IsSwitching && mapId == ActiveMapId && MapManager.HasMapEntry(mapId))
            {
                SetStatus($"Already using map {mapId}", StatusSeverity.Success);
                return;
            }

            queuedMapId = mapId;
            PendingMapId = mapId;

            if (IsSwitching)
            {
                SetStatus($"Queued map {mapId} (finishing current load)…", StatusSeverity.Working);
                Debug.Log($"[ImmersalMapSwitcher] Queued map {mapId} ({reason})");
                return;
            }

            if (switchQueueCoroutine != null)
                StopCoroutine(switchQueueCoroutine);
            switchQueueCoroutine = StartCoroutine(RunSwitchQueue());
#else
            SetStatus("Immersal SDK not available in this build", StatusSeverity.Error);
#endif
        }

#if IMMERSAL_SDK_PRESENT
        private IEnumerator RunSwitchQueue()
        {
            while (queuedMapId > 0)
            {
                int mapId = queuedMapId;
                queuedMapId = 0;
                PendingMapId = mapId;
                yield return SwitchToMapCoroutine(mapId);
            }

            PendingMapId = 0;
            switchQueueCoroutine = null;
        }

        private IEnumerator SwitchToMapCoroutine(int mapId)
        {
            IsSwitching = true;
            SetStatus($"Connecting to Immersal (map {mapId})…", StatusSeverity.Working);

            bool sdkReady = false;
            yield return EnsureSdkReadyCoroutine(ok => sdkReady = ok);
            if (!sdkReady)
            {
                IsSwitching = false;
                yield break;
            }

            var sdk = ImmersalSDK.Instance;
            if (sdk == null)
            {
                SetStatus("Immersal SDK not found", StatusSeverity.Error);
                IsSwitching = false;
                yield break;
            }

            var xrSpace = UnityEngine.Object.FindAnyObjectByType<XRSpace>();
            if (xrSpace == null)
            {
                SetStatus("XR Space not found in scene", StatusSeverity.Error);
                IsSwitching = false;
                yield break;
            }

            SetStatus($"Looking up map {mapId}…", StatusSeverity.Working);
            var metadataJob = new JobMapMetadataGetAsync { id = mapId };
            var metadataTask = metadataJob.RunJobAsync();
            while (!metadataTask.IsCompleted)
                yield return null;

            if (metadataTask.IsFaulted)
            {
                SetStatus($"Lookup failed: {metadataTask.Exception?.GetBaseException().Message}", StatusSeverity.Error);
                IsSwitching = false;
                yield break;
            }

            var metadata = metadataTask.Result;
            if (metadata.error != "none")
            {
                string detail = metadata.error == "not found"
                    ? "not found for this account/token"
                    : metadata.error;
                SetStatus($"Map {mapId} {detail} — kept map {(ActiveMapId > 0 ? ActiveMapId.ToString() : "—")}", StatusSeverity.Error);
                IsSwitching = false;
                yield break;
            }

            var immersal = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
            immersal?.ClearFrozenAnchor();

            SetStatus($"Removing current map…", StatusSeverity.Working);
            MapManager.RemoveAllMaps(true, true);
            yield return null;

            SetStatus($"Downloading map {mapId}…", StatusSeverity.Working);
            var mapLoading = new MapLoadingOption
            {
                DownloadVisualizationAtRuntime = true,
                m_SerializedDataSource = (int)MapDataSource.Download
            };

            var parameters = new MapCreationParameters
            {
                MetadataGetResult = metadata,
                LocalizationMethodType = typeof(DeviceLocalization),
                SceneParent = xrSpace,
                MapOptions = new IMapOption[] { mapLoading }
            };

            var createTask = MapManager.TryCreateMap(parameters);
            while (!createTask.IsCompleted)
                yield return null;

            if (createTask.IsFaulted || !createTask.Result.Success)
            {
                SetStatus($"Failed to load map {mapId}", StatusSeverity.Error);
                IsSwitching = false;
                yield break;
            }

            ActiveMapId = mapId;
            immersal?.SetMapId(mapId.ToString());
            PlayerPrefs.SetInt(PlayerPrefsKey, mapId);
            PlayerPrefs.Save();

            SetStatus($"Map {mapId} loaded — scan to localize", StatusSeverity.Success);
            Debug.Log($"[ImmersalMapSwitcher] Saved map {mapId} to PlayerPrefs ({PlayerPrefsKey})");
            OnMapSwitched?.Invoke(mapId);
            IsSwitching = false;
        }

        /// <summary>
        /// Wait for AR tracking, then ensure Immersal SDK is initialized. Map loads depend on both.
        /// </summary>
        private IEnumerator EnsureSdkReadyCoroutine(Action<bool> onComplete)
        {
            SetStatus("Waiting for AR tracking…", StatusSeverity.Working);
            float remaining = arTrackingTimeoutSeconds;
            while (ARSession.state != ARSessionState.SessionTracking && remaining > 0f)
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                SetStatus("AR not tracking — point camera at a wall, then tap Load again", StatusSeverity.Error);
                onComplete?.Invoke(false);
                yield break;
            }

                var sdk = ImmersalSDK.Instance;
            if (sdk == null)
            {
                SetStatus("Immersal SDK missing from scene", StatusSeverity.Error);
                onComplete?.Invoke(false);
                yield break;
            }

            if (!sdk.IsReady)
            {
                SetStatus("Starting Immersal SDK…", StatusSeverity.Working);
                var initTask = sdk.Initialize();
                while (!initTask.IsCompleted)
                    yield return null;

                if (initTask.IsFaulted)
                {
                    SetStatus($"Immersal init failed: {initTask.Exception?.GetBaseException().Message}", StatusSeverity.Error);
                    onComplete?.Invoke(false);
                    yield break;
                }
            }

            float readyWait = 30f;
            while (!sdk.IsReady && readyWait > 0f)
            {
                readyWait -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (!sdk.IsReady)
            {
                SetStatus("Immersal SDK not ready — check network and developer token", StatusSeverity.Error);
                onComplete?.Invoke(false);
                yield break;
            }

            onComplete?.Invoke(true);
        }

        private void SyncActiveMapFromScene()
        {
            foreach (var map in UnityEngine.Object.FindObjectsByType<XRMap>(FindObjectsSortMode.None))
            {
                if (map != null && map.IsConfigured && map.mapId > 0)
                {
                    ActiveMapId = map.mapId;
                    var immersal = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
                    if (immersal != null && string.IsNullOrEmpty(immersal.MapId))
                        immersal.SetMapId(map.mapId.ToString());
                    return;
                }
            }

            var provider = routeRootManager != null ? routeRootManager.ImmersalProvider : null;
            if (provider != null && int.TryParse(provider.MapId, out int parsed) && parsed > 0)
                ActiveMapId = parsed;
        }
#endif

        private void SetStatus(string message, StatusSeverity severity = StatusSeverity.Info)
        {
            StatusMessage = message;
            LastStatusSeverity = severity;
            OnStatusChanged?.Invoke(message);
            if (severity == StatusSeverity.Error)
                Debug.LogWarning($"[ImmersalMapSwitcher] {message}");
            else
                Debug.Log($"[ImmersalMapSwitcher] {message}");
        }
    }
}
