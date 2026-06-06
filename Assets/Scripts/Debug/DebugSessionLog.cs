// #region agent log
using System.Diagnostics;

namespace BodyTracking.DebugTools
{
    /// <summary>
    /// Lightweight debug logger for the active Cursor debug session. Routes to the Unity console (Debug.Log)
    /// so entries appear in device player logs — file I/O won't work because the app runs on the iPhone, not
    /// the Mac. Each line is prefixed with DBG5f9dd8 for easy filtering. Remove once debugging is complete.
    /// </summary>
    public static class DebugSessionLog
    {
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Log(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            UnityEngine.Debug.Log($"DBG5f9dd8 post-fix [{hypothesisId}] {location} | {message} | {dataJson}");
        }
    }
}
// #endregion
