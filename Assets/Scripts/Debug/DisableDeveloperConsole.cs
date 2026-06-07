using UnityEngine;

namespace BodyTracking.DebugTools
{
    /// <summary>
    /// Hides Unity's built-in "Development Console" overlay on device development builds.
    /// Development Build can stay enabled for profiling and script debugging; this only removes
    /// the on-screen log window that pops up when errors are logged.
    /// </summary>
    static class DisableDeveloperConsole
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void DisableOnStartup()
        {
            Debug.developerConsoleEnabled = false;
            Debug.developerConsoleVisible = false;
        }
    }
}
