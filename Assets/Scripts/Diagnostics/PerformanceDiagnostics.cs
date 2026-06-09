using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using BodyTracking.Playback;
using BodyTracking.UI;

namespace BodyTracking.Diagnostics
{
    /// <summary>
    /// Always-on lightweight performance HUD + logger for the device (Xcode) build. Auto-bootstraps at startup,
    /// so no scene wiring is needed. Each second it samples Unity's <see cref="ProfilerRecorder"/> counters
    /// (main-thread time, GC alloc, draw calls, batches, SetPass, tris/verts, used memory), the smoothed frame
    /// time / FPS, the number of live fused characters, and the per-section timings collected by
    /// <see cref="PerfSampler"/>, then:
    ///   • draws a compact on-screen overlay on the record screen (toggle with a 3-finger tap on device, or F1
    ///     in the Editor) — hidden during playback so the presentation view stays clean, and
    ///   • emits one aggregated record to the Xcode/Unity console + the debug NDJSON log.
    ///
    /// This is the "why does it get slow" instrument: watch how main-thread ms and the Penetration/GLB/PostProcess
    /// section times scale as you add characters to playback.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public class PerformanceDiagnostics : MonoBehaviour
    {
        public static PerformanceDiagnostics Instance { get; private set; }

        [Tooltip("Seconds between aggregated console/NDJSON reports.")]
        public float reportInterval = 1f;

        [Tooltip("Tag emitted with each record so initial vs post-fix runs can be compared.")]
        public string runId = "baseline";

        // On-screen debug HUD is hidden by default so the record screen stays clean (toggle with a 3-finger
        // tap on device, or F1 in the Editor). Console/NDJSON logging still runs for offline diagnostics.
        private bool hudVisible = false;

        // Frame timing (smoothed + min/max over the interval).
        private float intervalTime;
        private int intervalFrames;
        private float worstFrameMs;
        private float bestFrameMs = float.MaxValue;
        private float displayFps;
        private float displayAvgMs;
        private float displayWorstMs;

        // Latest snapshot for the HUD text.
        private string hudText = "(perf warming up...)";

        // ProfilerRecorders (real engine counters). Guarded — not every counter exists on every platform.
        private ProfilerRecorder mainThreadRecorder;
        private ProfilerRecorder gcAllocRecorder;
        private ProfilerRecorder drawCallsRecorder;
        private ProfilerRecorder batchesRecorder;
        private ProfilerRecorder setPassRecorder;
        private ProfilerRecorder trianglesRecorder;
        private ProfilerRecorder verticesRecorder;
        private ProfilerRecorder gcReservedRecorder;
        private ProfilerRecorder systemUsedRecorder;

        private GUIStyle hudStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            var go = new GameObject("PerformanceDiagnostics");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PerformanceDiagnostics>();
        }

        void OnEnable()
        {
            Instance = this;
            mainThreadRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
            gcAllocRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            gcReservedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Reserved Memory");
            systemUsedRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            drawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            batchesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            setPassRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            trianglesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            verticesRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Vertices Count");

            // Announce where the NDJSON lands so it can be pulled from the Xcode app container / Finder.
            PerfSampler.LogPathOnce();

            // #region agent log
            bool fileOk = PerfSampler.TryWriteHealthProbe(out string logPath, out string fileErr);
            PerfSampler.Emit("PerformanceDiagnostics.cs:OnEnable", "logging-health",
                new Dictionary<string, object>
                {
                    { "logPath", logPath },
                    { "persistentDataPath", Application.persistentDataPath },
                    { "fileWriteOk", fileOk },
                    { "fileWriteError", fileErr ?? "" },
                    { "platform", Application.platform.ToString() },
                }, "LOG-B", runId);
            // #endregion
        }

        void OnDisable()
        {
            mainThreadRecorder.Dispose();
            gcAllocRecorder.Dispose();
            gcReservedRecorder.Dispose();
            systemUsedRecorder.Dispose();
            drawCallsRecorder.Dispose();
            batchesRecorder.Dispose();
            setPassRecorder.Dispose();
            trianglesRecorder.Dispose();
            verticesRecorder.Dispose();
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            float frameMs = dt * 1000f;
            intervalTime += dt;
            intervalFrames++;
            if (frameMs > worstFrameMs) worstFrameMs = frameMs;
            if (frameMs < bestFrameMs) bestFrameMs = frameMs;

            HandleToggleInput();

            if (intervalTime >= reportInterval)
                FlushReport();
        }

        private void HandleToggleInput()
        {
            // 3-finger tap toggles the overlay on device.
            if (Input.touchCount >= 3)
            {
                bool anyBegan = false;
                for (int i = 0; i < Input.touchCount; i++)
                    if (Input.GetTouch(i).phase == TouchPhase.Began) anyBegan = true;
                if (anyBegan) hudVisible = !hudVisible;
            }
#if UNITY_EDITOR || UNITY_STANDALONE
            if (Input.GetKeyDown(KeyCode.F1)) hudVisible = !hudVisible;
#endif
        }

        private void FlushReport()
        {
            displayFps = intervalFrames / Mathf.Max(1e-4f, intervalTime);
            displayAvgMs = (intervalTime / Mathf.Max(1, intervalFrames)) * 1000f;
            displayWorstMs = worstFrameMs;

            int characterCount = CountActiveCharacters(out int overlayCount);
            var sectionData = PerfSampler.DrainSections();

            double mainThreadMs = mainThreadRecorder.Valid ? RecentAverageNs(ref mainThreadRecorder) / 1e6 : -1;
            long gcAlloc = gcAllocRecorder.Valid ? gcAllocRecorder.LastValue : -1;
            long gcReserved = gcReservedRecorder.Valid ? gcReservedRecorder.LastValue : -1;
            long sysUsed = systemUsedRecorder.Valid ? systemUsedRecorder.LastValue : -1;
            long drawCalls = drawCallsRecorder.Valid ? drawCallsRecorder.LastValue : -1;
            long batches = batchesRecorder.Valid ? batchesRecorder.LastValue : -1;
            long setPass = setPassRecorder.Valid ? setPassRecorder.LastValue : -1;
            long tris = trianglesRecorder.Valid ? trianglesRecorder.LastValue : -1;
            long verts = verticesRecorder.Valid ? verticesRecorder.LastValue : -1;

            var data = new Dictionary<string, object>
            {
                { "fps", (float)System.Math.Round(displayFps, 1) },
                { "avgFrameMs", (float)System.Math.Round(displayAvgMs, 2) },
                { "worstFrameMs", (float)System.Math.Round(displayWorstMs, 2) },
                { "bestFrameMs", (float)System.Math.Round(bestFrameMs, 2) },
                { "mainThreadMs", (float)System.Math.Round(mainThreadMs, 2) },
                { "characters", characterCount },
                { "overlays", overlayCount },
                { "gcAllocPerFrameB", gcAlloc },
                { "gcReservedB", gcReserved },
                { "systemUsedB", sysUsed },
                { "drawCalls", drawCalls },
                { "batches", batches },
                { "setPass", setPass },
                { "triangles", tris },
                { "vertices", verts },
            };

            // Per-section timings: total ms over the interval, per-frame ms, call count, and per-character ms.
            var sectionsObj = new Dictionary<string, object>();
            foreach (var kv in sectionData)
            {
                double totalMs = PerfSampler.ToMs(kv.Value.Ticks);
                var entry = new Dictionary<string, object>
                {
                    { "totalMs", (float)System.Math.Round(totalMs, 2) },
                    { "perFrameMs", (float)System.Math.Round(totalMs / Mathf.Max(1, intervalFrames), 3) },
                    { "calls", kv.Value.Calls },
                    { "perCharMs", (float)System.Math.Round(totalMs / Mathf.Max(1, characterCount), 3) },
                };
                sectionsObj[kv.Key] = entry;
            }
            data["sections"] = sectionsObj;

            PerfSampler.Emit("PerformanceDiagnostics.cs:FlushReport", "perf-report", data, "PERF", runId);
            BuildHudText(data, sectionData, characterCount);

            intervalTime = 0f;
            intervalFrames = 0;
            worstFrameMs = 0f;
            bestFrameMs = float.MaxValue;
        }

        private static double RecentAverageNs(ref ProfilerRecorder recorder)
        {
            int count = recorder.Count;
            if (count == 0) return 0;
            double sum = 0;
            var samples = new List<ProfilerRecorderSample>(count);
            recorder.CopyTo(samples, false);
            for (int i = 0; i < count; i++) sum += samples[i].Value;
            return sum / count;
        }

        private int CountActiveCharacters(out int overlayCount)
        {
            overlayCount = 0;
            var players = Object.FindObjectsByType<FusedCharacterPlayer>(FindObjectsSortMode.None);
            int active = 0;
            for (int i = 0; i < players.Length; i++)
                if (players[i] != null && players[i].isActiveAndEnabled) active++;

            var multi = Object.FindFirstObjectByType<MultiRecordingPlayback>(FindObjectsInactive.Include);
            if (multi != null) overlayCount = multi.OverlayCount;
            return active;
        }

        private void BuildHudText(Dictionary<string, object> data, Dictionary<string, PerfSampler.Section> sectionData,
            int characterCount)
        {
            var sb = new StringBuilder(512);
            sb.Append("FPS ").Append(displayFps.ToString("0.0"))
              .Append("  frame ").Append(displayAvgMs.ToString("0.0")).Append("ms (worst ")
              .Append(displayWorstMs.ToString("0.0")).Append(")\n");
            if (data["mainThreadMs"] is float mt && mt >= 0)
                sb.Append("main ").Append(mt.ToString("0.0")).Append("ms  ");
            sb.Append("chars ").Append(characterCount)
              .Append(" (ovl ").Append(data["overlays"]).Append(")\n");

            long gcAlloc = (long)data["gcAllocPerFrameB"];
            if (gcAlloc >= 0) sb.Append("GC/frame ").Append((gcAlloc / 1024f).ToString("0.0")).Append("KB  ");
            long sysUsed = (long)data["systemUsedB"];
            if (sysUsed >= 0) sb.Append("mem ").Append((sysUsed / (1024f * 1024f)).ToString("0")).Append("MB\n");

            long dc = (long)data["drawCalls"];
            if (dc >= 0) sb.Append("draws ").Append(dc).Append("  setpass ").Append(data["setPass"])
                           .Append("  tris ").Append(((long)data["triangles"]).ToString("N0")).Append('\n');

            if (sectionData.Count > 0)
            {
                sb.Append("— sections (ms/frame) —\n");
                foreach (var kv in sectionData)
                {
                    double perFrame = PerfSampler.ToMs(kv.Value.Ticks) / Mathf.Max(1, intervalFrames);
                    sb.Append(kv.Key).Append(' ').Append(perFrame.ToString("0.00"))
                      .Append("  x").Append(kv.Value.Calls).Append('\n');
                }
            }
            hudText = sb.ToString();
        }

        /// <summary>True when the user is on a playback-oriented screen or actively playing a recording.</summary>
        private static bool IsPlaybackExperience()
        {
            var switcher = Object.FindFirstObjectByType<BodyTrackingUISwitcher>();
            if (switcher != null && switcher.ActiveScreen != AppScreen.Record)
                return true;

            var controller = Object.FindFirstObjectByType<BodyTrackingController>();
            return controller != null && controller.IsPlaying;
        }

        void OnGUI()
        {
            if (!hudVisible || IsPlaybackExperience()) return;
            if (hudStyle == null)
            {
                hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = false,
                };
                hudStyle.normal.textColor = Color.green;
            }

            float w = 460f, h = 360f;
            var rect = new Rect(12f, 60f, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, w - 16f, h - 12f), hudText, hudStyle);

            // Copy-off-device controls: dump the whole NDJSON to the console (then copy from Xcode), or print
            // the on-disk path so you can download the file from the app container.
            float by = rect.yMax + 6f;
            if (GUI.Button(new Rect(12f, by, 150f, 44f), "Dump log → console"))
                PerfSampler.DumpFileToConsole();
            if (GUI.Button(new Rect(170f, by, 110f, 44f), "Log path"))
                PerfSampler.LogPathOnce();
        }
    }
}
