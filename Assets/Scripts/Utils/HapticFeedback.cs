using System.Runtime.InteropServices;
using UnityEngine;

namespace BodyTracking.Utils
{
    /// <summary>
    /// Lightweight device haptics for AR scanning feedback (iOS Taptic Engine).
    /// </summary>
    public static class HapticFeedback
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void TendorHapticLight();
        [DllImport("__Internal")] static extern void TendorHapticSuccess();
#endif

        /// <summary>Very subtle tick when Immersal registers a successful map localization.</summary>
        public static void PlayScanPoint()
        {
#if UNITY_IOS && !UNITY_EDITOR
            TendorHapticLight();
#endif
        }

        /// <summary>Stronger confirmation when the RouteRoot anchor locks.</summary>
        public static void PlayAnchorLocked()
        {
#if UNITY_IOS && !UNITY_EDITOR
            TendorHapticSuccess();
#endif
        }
    }
}
