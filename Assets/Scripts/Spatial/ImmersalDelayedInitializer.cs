using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

#if IMMERSAL_SDK_PRESENT
using Immersal;
using Immersal.XR;
#endif

namespace BodyTracking.Spatial
{
    /// <summary>
    /// Defers Immersal SDK initialization until ARKit is in SessionTracking. Starting Immersal while the
    /// AR session is still reconfiguring (world-map load, body tracking, etc.) causes repeated
    /// "Could not acquire camera intrinsics" failures and can stop the Immersal session.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class ImmersalDelayedInitializer : MonoBehaviour
    {
        [SerializeField] private float extraDelayAfterTrackingSeconds = 0.75f;
        [SerializeField] private float waitForTrackingTimeoutSeconds = 20f;

        void Awake()
        {
#if IMMERSAL_SDK_PRESENT
            // Seed the orientation cache before any localization runs (app is portrait by default).
            ExtensionMethods.CachedScreenOrientation = Screen.orientation;
            DisableAutomaticSdkInit();
#endif
        }

        void Start()
        {
#if IMMERSAL_SDK_PRESENT
            StartCoroutine(InitializeWhenArReady());
#endif
        }

#if IMMERSAL_SDK_PRESENT
        void Update()
        {
            // Keep the main-thread orientation cache fresh so the patched AdjustForScreenOrientation
            // (called on the background localization thread) never touches Screen.orientation off-thread.
            ExtensionMethods.CachedScreenOrientation = Screen.orientation;
        }

        private static void DisableAutomaticSdkInit()
        {
            var sdk = Object.FindAnyObjectByType<ImmersalSDK>();
            if (sdk == null) return;

            var field = typeof(ImmersalSDK).GetField(
                "m_InitializeAutomatically",
                BindingFlags.NonPublic | BindingFlags.Instance);
            field?.SetValue(sdk, false);
        }

        private IEnumerator InitializeWhenArReady()
        {
            var sdk = Object.FindAnyObjectByType<ImmersalSDK>();
            if (sdk == null)
                yield break;

            if (sdk.IsReady)
                yield break;

            if (!HasLocalizableMap())
            {
                Debug.Log("[ImmersalDelayedInitializer] No configured XR map in scene — skipping Immersal init (image-target fallback active).");
                yield break;
            }

            float remaining = waitForTrackingTimeoutSeconds;
            while (ARSession.state != ARSessionState.SessionTracking && remaining > 0f)
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (ARSession.state != ARSessionState.SessionTracking)
            {
                Debug.LogWarning("[ImmersalDelayedInitializer] ARSession did not reach SessionTracking yet; will retry when tracking starts.");
                StartCoroutine(InitializeWhenTrackingEventually(sdk));
                yield break;
            }

            if (extraDelayAfterTrackingSeconds > 0f)
                yield return new WaitForSeconds(extraDelayAfterTrackingSeconds);

            var initTask = sdk.Initialize();
            while (!initTask.IsCompleted)
                yield return null;

            if (initTask.IsFaulted)
                Debug.LogError("[ImmersalDelayedInitializer] Immersal Initialize failed: " + initTask.Exception);
            else
                Debug.Log("[ImmersalDelayedInitializer] Immersal SDK initialized after AR tracking was ready.");
        }

        private IEnumerator InitializeWhenTrackingEventually(ImmersalSDK sdk)
        {
            const float retryTimeout = 120f;
            float remaining = retryTimeout;
            while (!sdk.IsReady && remaining > 0f)
            {
                if (ARSession.state == ARSessionState.SessionTracking)
                {
                    if (extraDelayAfterTrackingSeconds > 0f)
                        yield return new WaitForSeconds(extraDelayAfterTrackingSeconds);

                    var initTask = sdk.Initialize();
                    while (!initTask.IsCompleted)
                        yield return null;

                    if (initTask.IsFaulted)
                        Debug.LogError("[ImmersalDelayedInitializer] Immersal Initialize failed (retry): " + initTask.Exception);
                    else
                        Debug.Log("[ImmersalDelayedInitializer] Immersal SDK initialized after AR tracking became ready.");
                    yield break;
                }

                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private static bool HasLocalizableMap()
        {
            // Configured map with a valid id is enough. ServerLocalization needs no local mapFile.
            foreach (var map in Object.FindObjectsByType<XRMap>(FindObjectsSortMode.None))
            {
                if (map != null && map.IsConfigured && map.mapId > 0)
                    return true;
            }
            return false;
        }
#endif
    }
}
