using UnityEngine;

namespace BodyTracking.DebugTools
{
    /// <summary>
    /// Keeps Unity's built-in "Development Console" overlay hidden on device development builds.
    /// Development Build can stay enabled for profiling and script debugging; this only removes the
    /// on-screen log window that pops up at the top-left whenever an error/exception is logged.
    ///
    /// Disabling it once at startup is not enough: Unity re-shows (and re-enables) the developer console
    /// the next time a LogError/exception is emitted. So a persistent listener re-asserts the hidden state
    /// every time something is logged.
    /// </summary>
    static class DisableDeveloperConsole
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void DisableOnStartup()
        {
            Debug.developerConsoleEnabled = false;
            Debug.developerConsoleVisible = false;

            // Re-assert on every log so a later error can't pop the overlay back open.
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (Debug.developerConsoleVisible)
                Debug.developerConsoleVisible = false;
        }
    }
}
