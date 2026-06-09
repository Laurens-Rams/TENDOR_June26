using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace BodyTracking.Diagnostics
{
    /// <summary>
    /// Ultra-light per-section timing for the playback hot paths. Call <see cref="Begin"/>/<see cref="End"/>
    /// (or wrap a block in a <see cref="Scope"/>) around suspected-expensive work; the sampler accumulates total
    /// time + call count per named section, then <see cref="PerformanceDiagnostics"/> flushes an aggregated
    /// per-second report to the Xcode console (via <c>Debug.Log</c>) and to the debug NDJSON log file.
    ///
    /// Designed to add negligible overhead: a couple of <see cref="Stopwatch.GetTimestamp"/> reads and a dictionary
    /// lookup per sample. No allocations on the hot path once the section dictionary is warm.
    /// </summary>
    public static class PerfSampler
    {
        public struct Section
        {
            public long Ticks;   // total ticks spent in this section during the interval
            public int Calls;    // number of times this section ran during the interval
        }

        private static readonly Dictionary<string, Section> sections = new Dictionary<string, Section>(32);
        private static readonly Dictionary<string, long> openStarts = new Dictionary<string, long>(32);
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>Master switch. When false, Begin/End are near-zero cost no-ops.</summary>
        public static bool Enabled = true;

        public static void Begin(string name)
        {
            if (!Enabled) return;
            openStarts[name] = Stopwatch.GetTimestamp();
        }

        public static void End(string name)
        {
            if (!Enabled) return;
            if (!openStarts.TryGetValue(name, out long start)) return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            sections.TryGetValue(name, out Section s);
            s.Ticks += elapsed;
            s.Calls += 1;
            sections[name] = s;
        }

        /// <summary>Disposable scope so callers can do <c>using (PerfSampler.Scope("X")) { ... }</c> (no boxing).</summary>
        public static ScopeHandle Scope(string name) => new ScopeHandle(name);

        public readonly struct ScopeHandle : System.IDisposable
        {
            private readonly string name;
            private readonly long start;
            public ScopeHandle(string n)
            {
                name = n;
                start = Enabled ? Stopwatch.GetTimestamp() : 0;
            }
            public void Dispose()
            {
                if (!Enabled || name == null) return;
                long elapsed = Stopwatch.GetTimestamp() - start;
                sections.TryGetValue(name, out Section s);
                s.Ticks += elapsed;
                s.Calls += 1;
                sections[name] = s;
            }
        }

        /// <summary>Snapshot the accumulated sections and clear them for the next interval.</summary>
        public static Dictionary<string, Section> DrainSections()
        {
            var copy = new Dictionary<string, Section>(sections);
            sections.Clear();
            openStarts.Clear();
            return copy;
        }

        public static double ToMs(long ticks) => ticks * TicksToMs;

        // ============================================================================================
        // SHARED LOGGING (Xcode console + NDJSON debug log)
        // ============================================================================================

        // Debug session NDJSON sink. On a device this workspace path won't exist, so we fall back to
        // persistentDataPath; on the dev machine (Editor / standalone) it writes where the debug session reads.
        private const string SessionId = "3a7bc6";
        private const string WorkspaceLogPath =
            "/Users/laurensart/Desktop/TENDOR-climbing copy/TENDOR-climbing/.cursor/debug-3a7bc6.log";

        private static string resolvedLogPath;
        private static bool logPathResolved;
        private static bool fileWriteFailedLogged;

        /// <summary>Absolute path the NDJSON log is being written to (workspace path on the dev machine, the
        /// app's Documents folder on device so it can be downloaded from Xcode / Finder).</summary>
        public static string ResolvedLogPath => LogPath();

        private static string LogPath()
        {
            if (logPathResolved) return resolvedLogPath;
            logPathResolved = true;
            try
            {
                string dir = Path.GetDirectoryName(WorkspaceLogPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    resolvedLogPath = WorkspaceLogPath;
                else
                    resolvedLogPath = Path.Combine(Application.persistentDataPath, "debug-" + SessionId + ".log");
            }
            catch
            {
                resolvedLogPath = Path.Combine(Application.persistentDataPath, "debug-" + SessionId + ".log");
            }
            return resolvedLogPath;
        }

        /// <summary>
        /// Print the entire NDJSON log file to the Xcode/Unity console in copy-friendly chunks, bracketed by clear
        /// markers so you can select everything between them and paste it back. Use when you can't pull the file
        /// off the device directly.
        /// </summary>
        public static void DumpFileToConsole()
        {
            string path = LogPath();
            string text;
            try
            {
                text = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogWarning("[PERF] Could not read log file at " + path + ": " + e.Message);
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                UnityEngine.Debug.Log("[PERF] Log file is empty or missing at " + path);
                return;
            }

            UnityEngine.Debug.Log("[PERF] ===== LOG DUMP BEGIN (" + path + ") =====");
            const int chunk = 8000; // keep each console line well under platform truncation limits
            for (int i = 0; i < text.Length; i += chunk)
                UnityEngine.Debug.Log(text.Substring(i, System.Math.Min(chunk, text.Length - i)));
            UnityEngine.Debug.Log("[PERF] ===== LOG DUMP END =====");
        }

        /// <summary>Log where the file lives so it can be located in the Xcode app container.</summary>
        public static void LogPathOnce()
        {
            UnityEngine.Debug.Log("[PERF] Writing diagnostics NDJSON to: " + LogPath());
        }

        /// <summary>Best-effort probe that the NDJSON file sink is writable (device Documents folder).</summary>
        public static bool TryWriteHealthProbe(out string path, out string error)
        {
            path = LogPath();
            error = null;
            try
            {
                File.AppendAllText(path, "");
                return true;
            }
            catch (System.Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        /// <summary>
        /// Emit one diagnostic record: always to the Xcode/Unity console (so it shows up while debugging the
        /// device build), and best-effort as an NDJSON line to the debug log file (so the debug session can read
        /// it when running in the Editor).
        /// </summary>
        public static void Emit(string location, string message, IDictionary<string, object> data,
            string hypothesisId, string runId)
        {
            string json = BuildNdjson(location, message, data, hypothesisId, runId);
            UnityEngine.Debug.Log("[PERF] " + message + " " + json);

            try
            {
                File.AppendAllText(LogPath(), json + "\n");
            }
            catch (System.Exception e)
            {
                // Console line above is the primary channel on device; warn once if the file sink fails too.
                if (!fileWriteFailedLogged)
                {
                    fileWriteFailedLogged = true;
                    UnityEngine.Debug.LogWarning("[PERF] NDJSON file write failed at " + LogPath() + ": " + e.Message);
                }
            }
        }

        private static string BuildNdjson(string location, string message, IDictionary<string, object> data,
            string hypothesisId, string runId)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendKv(sb, "sessionId", SessionId); sb.Append(',');
            AppendKv(sb, "location", location); sb.Append(',');
            AppendKv(sb, "message", message); sb.Append(',');
            if (!string.IsNullOrEmpty(hypothesisId)) { AppendKv(sb, "hypothesisId", hypothesisId); sb.Append(','); }
            if (!string.IsNullOrEmpty(runId)) { AppendKv(sb, "runId", runId); sb.Append(','); }
            sb.Append("\"timestamp\":").Append(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Append(',');
            sb.Append("\"data\":");
            AppendValue(sb, data);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendKv(StringBuilder sb, string key, string val)
        {
            sb.Append('"').Append(Escape(key)).Append("\":\"").Append(Escape(val)).Append('"');
        }

        private static void AppendValue(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    sb.Append('"').Append(Escape(s)).Append('"');
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    sb.Append(f.ToString("0.####", CultureInfo.InvariantCulture));
                    break;
                case double d:
                    sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> map:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(Escape(kv.Key)).Append("\":");
                        AppendValue(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                default:
                    sb.Append('"').Append(Escape(v.ToString())).Append('"');
                    break;
            }
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
