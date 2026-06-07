#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using UnityEngine;

namespace BodyTracking.DebugTools
{
    /// <summary>
    /// Timing-alignment debug logger (session 6445bc). All lines go to the Unity console → Xcode device log.
    /// Filter Xcode for <c>DBG6445bc</c> and copy/paste those lines back for analysis.
    /// </summary>
    public static class TimingDebugLog
    {
        const string Tag = "DBG6445bc";

#if UNITY_EDITOR
        const string LogPath = "/Users/laurensart/Desktop/TENDOR-climbing copy/TENDOR-climbing/.cursor/debug-6445bc.log";
#endif

        public static void Log(string hypothesisId, string location, string message, string dataJson, string runId = "pre-fix")
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string json = "{\"sessionId\":\"6445bc\",\"runId\":\"" + runId +
                          "\",\"hypothesisId\":\"" + hypothesisId + "\",\"location\":\"" + location +
                          "\",\"message\":\"" + Escape(message) + "\",\"data\":" + dataJson + ",\"timestamp\":" + ts + "}";

            // One line for Xcode: easy to filter + copy. JSON appended after " | " for automated parsing.
            UnityEngine.Debug.Log("[" + Tag + "][" + hypothesisId + "] " + message + " | " + json);
#if UNITY_EDITOR
            try { System.IO.File.AppendAllText(LogPath, json + "\n"); } catch { }
#endif
        }

        /// <summary>Call once when fused playback starts so Xcode shows logging is active.</summary>
        public static void LogSessionStart(string context, string dataJson)
        {
            Log("START", "TimingDebugLog", "timing debug active — filter Xcode for " + Tag + " (" + context + ")", dataJson);
        }

        static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
#endif
