using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using BodyTracking.Data;
using BodyTracking.Playback;
using BodyTracking.Spatial;
using BodyTracking.Storage;
using BodyTracking.Diagnostics;

namespace BodyTracking.MoveAI
{
        /// <summary>
        /// Orchestrates the Move AI fusion path end to end: after a recording is saved it submits the paired video
    /// to <see cref="MoveApiClient"/>, parses the returned MOTION_DATA, bakes a <see cref="MoveAIFusionAsset"/>
    /// (fused with the ARKit recording), and stores it next to the recording. At playback time it can route a
    /// recording to <see cref="FusedCharacterPlayer"/> when a fused asset exists.
    ///
    /// This sits alongside BodyTrackingController so the existing dot-skeleton record/playback keeps working as
    /// the fallback; the controller calls into this coordinator opportunistically.
    /// </summary>
    public class MoveAIFusionCoordinator : MonoBehaviour
    {
        [SerializeField] private MoveApiClient moveApiClient;
        [SerializeField] private FusedCharacterPlayer fusedPlayer;
        [SerializeField] private PlaybackCompareVisualizer compareVisualizer;
        [SerializeField] private MoveJointMap jointMap = MoveJointMap.CreateDefaultMixamo();

        [Header("Fusion settings")]
        [Tooltip("Per-axis ARKit correction weights applied at bake time (X/Z moderate, Y full for climbs). Zero in scene = use default (0.6, 1, 1).")]
        [SerializeField] private Vector3 axisWeights = MoveAIFusionBaker.Settings.DefaultAxisWeights;
        [SerializeField] private float smoothingTau = 0.4f;
        [SerializeField] private float outlierMeters = 0.6f;
        [Tooltip("During fused replay, show ARKit (cyan) and Move (orange) skeletons side by side alongside the character.")]
        [SerializeField] private bool showCompareSkeletons = true;
        [Tooltip("When a Move AI GLB exists for the recording, retarget body + fingers from it (muscle space). " +
                 "Positioning/scale still come from the fused trajectory. Disable to force the procedural retarget.")]
        [SerializeField] private bool preferGlbArticulation = true;

        [Header("Status (read-only)")]
        [SerializeField] private string lastStatus = "";

        private const string FusionFolder = "MoveAIFusion";

        public string LastStatus => lastStatus;
        public bool IsConfigured => moveApiClient != null && moveApiClient.HasApiKey;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnFusionReady; // recordingFileName

        /// <summary>Live-tunable fusion bake settings (applied by <see cref="RebakeLatest"/>, no API call).</summary>
        public MoveAIFusionBaker.Settings BakeSettings
        {
            get => new MoveAIFusionBaker.Settings
            {
                axisWeights = axisWeights,
                smoothingTau = smoothingTau,
                outlierMeters = outlierMeters,
            };
            set
            {
                axisWeights = value.axisWeights;
                smoothingTau = value.smoothingTau;
                outlierMeters = value.outlierMeters;
            }
        }

        static string FusionDir => Path.Combine(Application.persistentDataPath, FusionFolder);
        static string FusionPath(string recordingFileName) => Path.Combine(FusionDir, recordingFileName + ".fusion.json");

        public static bool HasFusionAsset(string recordingFileName) =>
            !string.IsNullOrEmpty(recordingFileName) && File.Exists(FusionPath(recordingFileName));

        void Awake()
        {
            // Legacy scenes serialized axisWeights as zero (pure Move root); migrate so rebake applies ARKit Y correction.
            if (IsZeroWeights(axisWeights))
                axisWeights = MoveAIFusionBaker.Settings.DefaultAxisWeights;
        }

        static bool IsZeroWeights(Vector3 w) => w.sqrMagnitude < 1e-8f;

        /// <summary>
        /// Effective bake settings: non-zero serialized weights, else weights stored on the asset, else defaults.
        /// </summary>
        MoveAIFusionBaker.Settings ResolveBakeSettings(MoveAIFusionAsset existing = null)
        {
            var weights = axisWeights;
            if (IsZeroWeights(weights))
            {
                if (existing != null && !IsZeroWeights(existing.correctionWeights))
                    weights = existing.correctionWeights;
                else
                    weights = MoveAIFusionBaker.Settings.DefaultAxisWeights;
            }

            return new MoveAIFusionBaker.Settings
            {
                axisWeights = weights,
                smoothingTau = smoothingTau,
                outlierMeters = outlierMeters,
            };
        }

        // --- Upload queue (disk-backed, serial) ---------------------------------------------------
        // The queue is the single source of truth for pending fusion jobs. It is persisted on every state
        // change (and on app pause) so a submitted climb keeps uploading across app sleep and resumes after a
        // relaunch — and so the user can immediately record + submit another climb without the two jobs
        // interfering: jobs are processed one at a time (only one mp4 in memory at once), each keyed to its own
        // recording + video + Move job id.
        private MoveQueueData queue;
        private bool processing;
        private readonly Dictionary<int, string> attachedGlbPathByTarget = new Dictionary<int, string>();
        private readonly HashSet<string> glbAttachInFlight = new HashSet<string>();

        void EnsureQueueLoaded()
        {
            if (queue == null)
                queue = MoveJobQueueStore.Load();
        }

        void SaveQueue()
        {
            if (queue != null)
                MoveJobQueueStore.Save(queue);
        }

        /// <summary>
        /// Enqueue the Move AI fusion job for a freshly saved recording and start (or continue) serial
        /// processing. <paramref name="videoFilePath"/> is the mp4 captured alongside the recording; if
        /// null/missing, fusion is skipped (the recording still replays via the existing skeleton player).
        /// Returns immediately — the upload runs in the background so a new recording can begin right away.
        /// </summary>
        public void SubmitForFusion(HipRecording recording, string recordingFileName, string videoFilePath)
        {
            if (recording == null || string.IsNullOrEmpty(recordingFileName))
            {
                SetStatus("Fusion skipped: no recording");
                return;
            }
            if (moveApiClient == null || !moveApiClient.HasApiKey)
            {
                SetStatus("Fusion skipped: Move API not configured");
                return;
            }
            if (string.IsNullOrEmpty(videoFilePath) || !File.Exists(VideoRecorder.NormalizeLocalPath(videoFilePath)))
            {
                SetStatus("Fusion skipped: no paired video file");
                return;
            }

            EnsureQueueLoaded();
            videoFilePath = VideoRecorder.NormalizeLocalPath(videoFilePath);

            // The recording is already trimmed to the in-frame body window; process only that span so the Move
            // motion matches the saved ARKit recording length.
            float clipEnd = recording.duration > 0f
                ? recording.videoStartTimeOffset + recording.duration
                : 0f;

            // De-dupe: if this recording is already queued (and not terminal), refresh its inputs instead of
            // adding a second job for the same recording.
            var entry = queue.entries.Find(e => e.recordingFileName == recordingFileName && !e.IsTerminal);
            if (entry == null)
            {
                entry = new MoveQueueEntry { recordingFileName = recordingFileName };
                queue.entries.Add(entry);
            }
            entry.videoFilePath = videoFilePath;
            entry.clipEndSeconds = clipEnd;
            entry.jobId = null;
            entry.attempts = 0;
            entry.error = null;
            entry.State = MoveQueueState.Queued;
            SaveQueue();

            int pending = CountPending();
            SetStatus(pending > 1 ? $"Move AI: queued ({pending} pending)" : "Move AI: queued");

            KickProcessor();
        }

        int CountPending()
        {
            if (queue == null) return 0;
            int n = 0;
            foreach (var e in queue.entries)
                if (!e.IsTerminal) n++;
            return n;
        }

        int CountActionable()
        {
            if (queue == null) return 0;
            int n = 0;
            foreach (var e in queue.entries)
            {
                if (e.IsTerminal) continue;
                if (HasFusionAsset(e.recordingFileName)) continue;
                if (e.attempts >= MoveJobQueueStore.MaxRetryAttempts) continue;
                n++;
            }
            return n;
        }

        int CountFailed()
        {
            if (queue == null) return 0;
            int n = 0;
            foreach (var e in queue.entries)
                if (e.State == MoveQueueState.Failed) n++;
            return n;
        }

        /// <summary>Start the serial processor if it isn't already running and there is work to do.</summary>
        void KickProcessor()
        {
            if (processing)
                return;
            EnsureQueueLoaded();
            if (CountActionable() == 0)
                return;
            if (!EnsureFusionHostActive())
            {
                SetStatus("Fusion paused: enable MoveAIFusion to upload");
                return;
            }
            StartCoroutine(ProcessQueue());
        }

        MoveQueueEntry NextActionable(HashSet<MoveQueueEntry> attempted)
        {
            if (queue == null) return null;
            foreach (var e in queue.entries)
            {
                if (e.IsTerminal) continue;
                if (attempted.Contains(e)) continue;
                if (e.attempts >= MoveJobQueueStore.MaxRetryAttempts)
                {
                    e.State = MoveQueueState.Failed;
                    if (string.IsNullOrEmpty(e.error)) e.error = "Retry limit reached";
                    continue;
                }
                // Already fused (e.g. baked by another path or a previous run) — close it out.
                if (HasFusionAsset(e.recordingFileName))
                {
                    e.State = MoveQueueState.Done;
                    continue;
                }
                return e;
            }
            return null;
        }

        /// <summary>
        /// Serial job pump. Each entry is attempted at most once per pass (tracked in <c>attempted</c>) so a
        /// retryable failure never busy-loops; retryable jobs are retried on the next kick (new submit / app
        /// resume / relaunch). Only one video is held in memory at a time.
        /// </summary>
        IEnumerator ProcessQueue()
        {
            processing = true;
            var attempted = new HashSet<MoveQueueEntry>();

            MoveQueueEntry entry;
            while ((entry = NextActionable(attempted)) != null)
            {
                attempted.Add(entry);
                entry.attempts++;
                SaveQueue();

                bool done = false;
                MoveJobResult result = null;
                Action<MoveJobResult> onComplete = r => { result = r; done = true; };
                Action<MoveJobProgress> onProgress = p => SetStatus(FormatProgress(p));

                if (entry.NeedsUpload)
                {
                    string videoPath = VideoRecorder.NormalizeLocalPath(entry.videoFilePath);
                    if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                    {
                        FailEntry(entry, "paired video file missing");
                        continue;
                    }

                    entry.State = MoveQueueState.Uploading;
                    SaveQueue();
                    SetStatus(FormatStatus("reading video…"));

                    // Read the mp4 off the main thread — a large file would otherwise freeze the UI.
                    byte[] videoBytes = null;
                    string readError = null;
                    bool readDone = false;
                    var thread = new System.Threading.Thread(() =>
                    {
                        try { videoBytes = File.ReadAllBytes(videoPath); }
                        catch (Exception ex) { readError = ex.Message; }
                        finally { readDone = true; }
                    }) { IsBackground = true };
                    thread.Start();
                    while (!readDone)
                        yield return null;

                    if (readError != null || videoBytes == null || videoBytes.Length == 0)
                    {
                        FailEntry(entry, $"video read failed ({readError ?? "empty file"})");
                        continue;
                    }

                    Debug.Log($"[MoveAIFusionCoordinator] Uploading '{entry.recordingFileName}': " +
                              $"{videoBytes.Length / 1024}KB, clipWindow end {entry.clipEndSeconds:F2}s");
                    SetStatus(FormatStatus("uploading…"));

                    moveApiClient.SubmitVideo(videoBytes,
                        onComplete: onComplete,
                        onProgress: onProgress,
                        clipEndSeconds: entry.clipEndSeconds,
                        onJobCreated: jobId =>
                        {
                            // Persist the job id the moment it exists so a sleep/kill resumes via re-poll
                            // instead of re-uploading the whole video.
                            entry.jobId = jobId;
                            entry.State = MoveQueueState.Processing;
                            PersistJobStateToRecording(entry.recordingFileName, jobId, "RUNNING");
                            SaveQueue();
                        });
                }
                else
                {
                    // Resume: the job already exists server-side; just re-poll + download (+ GLB).
                    entry.State = MoveQueueState.Processing;
                    SaveQueue();
                    SetStatus(FormatStatus("resuming…"));
                    moveApiClient.RedownloadMotionData(entry.jobId,
                        onComplete: onComplete,
                        onProgress: onProgress);
                }

                while (!done)
                    yield return null;

                HandleJobResult(entry, result);
                SaveQueue();
            }

            PruneDone();
            SaveQueue();
            processing = false;

            if (CountPending() == 0)
            {
                int failed = CountFailed();
                SetStatus(failed > 0 ? $"Move AI: {failed} job(s) failed" : "Move AI: all jobs complete");
            }
        }

        void HandleJobResult(MoveQueueEntry entry, MoveJobResult result)
        {
            if (result == null)
            {
                entry.error = "no result";
                if (entry.attempts >= MoveJobQueueStore.MaxRetryAttempts)
                    FailEntry(entry, "no result (retry limit)");
                return;
            }

            if (!result.success)
            {
                bool serverFailed = !string.IsNullOrEmpty(result.error) &&
                                    result.error.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0;
                if (serverFailed)
                {
                    // Move rejected the job — terminal, re-polling won't help.
                    FailEntry(entry, result.error);
                    PersistJobStateToRecording(entry.recordingFileName, entry.jobId, "FAILED");
                }
                else if (!string.IsNullOrEmpty(entry.jobId))
                {
                    // Timeout / network / download glitch but the job exists server-side — keep it resumable.
                    entry.State = MoveQueueState.Processing;
                    entry.error = result.error;
                    SetStatus(FormatStatus($"will retry ({result.error})"));
                }
                else
                {
                    // Upload never produced a job id; leave queued to retry (capped by attempts).
                    entry.State = MoveQueueState.Queued;
                    entry.error = result.error;
                    SetStatus(FormatStatus($"upload retry ({result.error})"));
                }
                return;
            }

            if (!string.IsNullOrEmpty(result.jobId))
                entry.jobId = result.jobId;

            BakeFromResult(entry, result);
        }

        /// <summary>
        /// Parse + fuse a finished Move result into the recording it belongs to. The recording is loaded fresh
        /// from disk by file name (never a stale in-memory reference) so the Move motion always fuses with the
        /// correct ARKit recording, even with several jobs in flight or on a post-relaunch resume.
        /// </summary>
        void BakeFromResult(MoveQueueEntry entry, MoveJobResult result)
        {
            entry.State = MoveQueueState.Baking;
            SaveQueue();

            var recording = RecordingStorage.LoadRecording(entry.recordingFileName);
            if (recording == null)
            {
                FailEntry(entry, "recording JSON missing for bake");
                return;
            }

            // Save the raw Move AI GLB (if returned) so it can be pulled off and previewed before fusion.
            SaveGlbToDevice(entry.recordingFileName, result.glbBytes);

            SetStatus(FormatStatus("parsing motion…"));
            var motion = MoveMotionParser.ParseMotionDataZip(result.motionDataZip, jointMap);
            if (motion == null || motion.FrameCount == 0)
            {
                FailEntry(entry, "Move returned no usable motion");
                return;
            }

            SetStatus(FormatStatus("baking fusion…"));
            var asset = MoveAIFusionBaker.Bake(recording, motion, jointMap, ResolveBakeSettings());
            if (asset == null)
            {
                FailEntry(entry, "fusion bake failed");
                return;
            }

            if (!asset.Save(FusionPath(entry.recordingFileName)))
            {
                FailEntry(entry, "fusion save failed");
                return;
            }

            entry.State = MoveQueueState.Done;
            entry.error = null;
            PersistJobStateToRecording(entry.recordingFileName, entry.jobId, "FINISHED");
            SaveQueue();
            SetStatus($"Fused replay ready · {entry.recordingFileName}");
            OnFusionReady?.Invoke(entry.recordingFileName);
        }

        void FailEntry(MoveQueueEntry entry, string error)
        {
            entry.State = MoveQueueState.Failed;
            entry.error = error;
            Debug.LogWarning($"[MoveAIFusionCoordinator] Job failed for '{entry.recordingFileName}': {error}");
            SetStatus($"Move AI failed ({entry.recordingFileName}): {error}");
            SaveQueue();
        }

        void PruneDone()
        {
            if (queue == null) return;
            queue.entries.RemoveAll(e => e.State == MoveQueueState.Done);
        }

        /// <summary>Mirror the job id/state onto the recording JSON so legacy resume + recording menus stay in sync.</summary>
        void PersistJobStateToRecording(string recordingFileName, string jobId, string state)
        {
            try
            {
                var recording = RecordingStorage.LoadRecording(recordingFileName);
                if (recording == null) return;
                recording.moveJobId = jobId;
                recording.moveJobState = state;
                RecordingStorage.SaveRecording(recording, recordingFileName);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MoveAIFusionCoordinator] Could not persist job state to '{recordingFileName}': {e.Message}");
            }
        }

        string FormatStatus(string phase)
        {
            int pending = CountPending();
            return pending > 1 ? $"Move AI ({pending} pending): {phase}" : $"Move AI: {phase}";
        }

        string FormatProgress(MoveJobProgress p)
        {
            int pending = CountPending();
            string prefix = pending > 1 ? $"Move AI ({pending} pending)" : "Move AI";
            return $"{prefix}: {p.message} ({p.percent:F0}%)";
        }

        static string GlbDir => Path.Combine(Application.persistentDataPath, "MoveAIGLB");
        static string GlbPath(string recordingFileName) => Path.Combine(GlbDir, recordingFileName + ".glb");

        /// <summary>True when a raw Move AI GLB has been downloaded for this recording (used for GLB retargeting).</summary>
        public static bool HasGlb(string recordingFileName) =>
            !string.IsNullOrEmpty(recordingFileName) && File.Exists(GlbPath(recordingFileName));

        // Hidden host for the runtime-loaded Move GLB skeleton(s).
        private Transform glbHost;
        Transform GlbHost()
        {
            if (glbHost == null)
            {
                var go = new GameObject("MoveGlbSources");
                go.transform.SetParent(transform, false);
                go.SetActive(true);
                glbHost = go.transform;
            }
            return glbHost;
        }

        /// <summary>
        /// Writes the raw Move AI GLB to persistentDataPath/MoveAIGLB/&lt;recording&gt;.glb so the raw rigged Move
        /// result can be copied off the device (Xcode device container / Files / share) and previewed in any GLB
        /// viewer before fusion.
        /// </summary>
        void SaveGlbToDevice(string recordingFileName, byte[] glbBytes)
        {
            if (glbBytes == null || glbBytes.Length == 0)
                return;
            try
            {
                Directory.CreateDirectory(GlbDir);
                string path = Path.Combine(GlbDir, recordingFileName + ".glb");
                File.WriteAllBytes(path, glbBytes);
                Debug.Log($"[MoveAIFusionCoordinator] Saved Move AI GLB ({glbBytes.Length} bytes) to {path}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MoveAIFusionCoordinator] Failed to save Move AI GLB: {e.Message}");
            }
        }

        /// <summary>
        /// On startup, rebuild the upload queue from disk and resume any unfinished Move AI fusion jobs that were
        /// still uploading/processing when the app was last backgrounded or closed. Jobs with a server job id
        /// re-poll + download; jobs that never finished uploading re-upload from their saved video path. Also
        /// migrates legacy recordings that stored a moveJobId on the recording JSON but predate the queue. Safe
        /// to call when Move is unconfigured or there is nothing to resume.
        /// </summary>
        public void ResumeInterruptedJobs()
        {
            if (moveApiClient == null || !moveApiClient.HasApiKey)
                return;

            EnsureQueueLoaded();

            // Close out jobs that are already fused (asset on disk).
            foreach (var e in queue.entries)
            {
                if (HasFusionAsset(e.recordingFileName))
                    e.State = MoveQueueState.Done;
            }
            PruneDone();

            // Migrate legacy recordings (job id stored on the recording, no queue entry, not yet fused).
            MigrateLegacyJobs();
            SaveQueue();

            int actionable = CountActionable();
            if (actionable > 0)
            {
                SetStatus($"Resuming {actionable} Move AI job(s)…");
                KickProcessor();
            }
        }

        void MigrateLegacyJobs()
        {
            var recordings = RecordingStorage.GetAvailableRecordings();
            if (recordings == null) return;
            foreach (var fileName in recordings)
            {
                if (HasFusionAsset(fileName)) continue;
                if (queue.entries.Exists(e => e.recordingFileName == fileName && !e.IsTerminal)) continue;

                if (!RecordingStorage.TryGetMoveJobInfo(fileName, out string jobId, out string jobState)) continue;
                if (jobState == "FAILED") continue;

                queue.entries.Add(new MoveQueueEntry
                {
                    recordingFileName = fileName,
                    jobId = jobId,
                    videoFilePath = null,
                    State = MoveQueueState.Processing,
                });
            }
        }

        /// <summary>Persist the queue across app suspension and re-kick processing when the app returns to the
        /// foreground (an in-flight upload/poll may have failed while the device was asleep).</summary>
        void OnApplicationPause(bool paused)
        {
            SaveQueue();
            if (!paused)
                KickProcessor();
        }

        // If this host is disabled mid-job the ProcessQueue coroutine stops; clear the guard so a later
        // KickProcessor() can restart it (resumable jobs are picked back up from their persisted state).
        void OnDisable()
        {
            processing = false;
            SaveQueue();
        }

        /// <summary>The scene's primary fused player (timeline-driving). Overlay players read its tuning.</summary>
        public FusedCharacterPlayer PrimaryPlayer => fusedPlayer;

        /// <summary>True when a fused asset exists for the recording AND its source JSON can be loaded.</summary>
        public static bool CanPlayFused(string recordingFileName) => HasFusionAsset(recordingFileName);

        /// <summary>
        /// Try to start fused playback for a recording. Returns false if no fused asset exists (caller should
        /// fall back to the existing skeleton player).
        /// </summary>
        public bool TryStartFusedPlayback(string recordingFileName, IRouteRootProvider provider, HipRecording recording)
        {
            if (fusedPlayer == null || !HasFusionAsset(recordingFileName))
                return false;

            // The fused player and compare visualizer rely on per-frame Update(), which only pumps on an active
            // GameObject. This host object can be left disabled in the scene, so activate it before playback.
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            if (fusedPlayer.gameObject != gameObject && !fusedPlayer.gameObject.activeSelf)
                fusedPlayer.gameObject.SetActive(true);

            if (!ConfigureAndStartFusedOn(fusedPlayer, recordingFileName, provider, recording, out var asset))
                return false;

            if (showCompareSkeletons && recording != null && asset != null)
            {
                EnsureCompareVisualizer();
                compareVisualizer.Begin(recording, asset, provider, fusedPlayer);
            }

            SetStatus($"Playing fused replay '{recordingFileName}'");
            return true;
        }

        /// <summary>
        /// Configure and start fused playback on an arbitrary <see cref="FusedCharacterPlayer"/> (used by the
        /// multi-recording overlap engine to drive additional overlay characters). Loads the fused asset and
        /// the source recording, attaches the Move GLB muscle retarget when available, and starts playback.
        /// No compare overlay / status text is produced — that is reserved for the primary player.
        /// </summary>
        public bool ConfigureAndStartFusedOn(FusedCharacterPlayer targetPlayer, string recordingFileName,
            IRouteRootProvider provider, HipRecording recording)
        {
            return ConfigureAndStartFusedOn(targetPlayer, recordingFileName, provider, recording, out _);
        }

        bool ConfigureAndStartFusedOn(FusedCharacterPlayer targetPlayer, string recordingFileName,
            IRouteRootProvider provider, HipRecording recording, out MoveAIFusionAsset loadedAsset)
        {
            using var _ = PerfSampler.Scope("Fusion.ConfigureStart");
            loadedAsset = null;
            if (targetPlayer == null || !HasFusionAsset(recordingFileName))
                return false;

            var asset = MoveAIFusionAsset.Load(FusionPath(recordingFileName));
            if (asset == null) return false;

            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            targetPlayer.SetRouteRootProvider(provider);
            targetPlayer.SetSourceRecording(recording);
            if (!targetPlayer.LoadAsset(asset)) return false;

            // Precedence: if a Move AI GLB exists for this recording, retarget body + fingers from it (muscle space)
            // while positioning/scale stay on the fused trajectory. The GLB loads asynchronously, so attach it when
            // ready and keep playing the procedural fallback meanwhile — a slow glTFast load never blocks the UI.
            bool glbMode = targetPlayer.ArticulationMode == FusedCharacterPlayer.BodyArticulationSource.MoveGlb;
            if (glbMode && preferGlbArticulation && HasGlb(recordingFileName))
                AttachGlbIfNeeded(GlbPath(recordingFileName), recordingFileName, targetPlayer);
            else
            {
                targetPlayer.SetMoveGlbSource(null);
                attachedGlbPathByTarget.Remove(targetPlayer.GetInstanceID());
            }

            targetPlayer.RefreshGlbArticulation();
            targetPlayer.StartPlayback();
            loadedAsset = asset;
            return true;
        }

        /// <summary>
        /// Re-fuse the latest recording using the CACHED Move pose from the existing fused asset and the current
        /// <see cref="BakeSettings"/> — no Move API call. Saves back over the same .fusion.json and restarts fused
        /// playback so the change is visible immediately. Returns false (with a status) if there's nothing to rebake.
        /// </summary>
        public bool RebakeLatest(string recordingFileName, IRouteRootProvider provider, HipRecording recording)
        {
            if (fusedPlayer == null || string.IsNullOrEmpty(recordingFileName) || recording == null)
            {
                SetStatus("Rebake skipped: no recording loaded");
                return false;
            }
            if (!HasFusionAsset(recordingFileName))
            {
                SetStatus("No fused asset yet — submit to Move first");
                return false;
            }

            var existing = MoveAIFusionAsset.Load(FusionPath(recordingFileName));
            if (existing == null || existing.pose == null || existing.pose.FrameCount == 0)
            {
                SetStatus("Rebake failed: cached pose unreadable");
                return false;
            }

            SetStatus("Rebaking (no API)…");
            var rebaked = MoveAIFusionBaker.RebakeFromAsset(recording, existing, ResolveBakeSettings(existing));
            if (rebaked == null)
            {
                SetStatus("Rebake failed");
                return false;
            }

            if (!rebaked.Save(FusionPath(recordingFileName)))
            {
                SetStatus("Rebake save failed");
                return false;
            }

            // Restart fused playback from the freshly saved asset (reuses provider/GLB/compare wiring).
            bool restarted = TryStartFusedPlayback(recordingFileName, provider, recording);
            SetStatus(restarted ? $"Rebaked '{recordingFileName}'" : "Rebaked; press Play to view");
            return true;
        }

        /// <summary>Preload the Move GLB for a recording so Play can switch to muscle retarget instantly.</summary>
        public void WarmGlb(string recordingFileName)
        {
            if (!preferGlbArticulation || fusedPlayer == null) return;
            if (fusedPlayer.ArticulationMode != FusedCharacterPlayer.BodyArticulationSource.MoveGlb) return;
            if (!HasGlb(recordingFileName)) return;
            if (!EnsureFusionHostActive())
                return;
            AttachGlbIfNeeded(GlbPath(recordingFileName), recordingFileName, fusedPlayer);
        }

        void AttachGlbIfNeeded(string glbPath, string recordingFileName, FusedCharacterPlayer target)
        {
            if (target == null || string.IsNullOrEmpty(glbPath))
                return;

            int targetId = target.GetInstanceID();
            if (attachedGlbPathByTarget.TryGetValue(targetId, out string attachedPath) && attachedPath == glbPath)
            {
                target.RefreshGlbArticulation();
                return;
            }

            string key = GlbAttachKey(target, glbPath);
            if (glbAttachInFlight.Contains(key))
                return;

            StartCoroutine(AttachGlbWhenReady(glbPath, recordingFileName, target));
        }

        static string GlbAttachKey(FusedCharacterPlayer target, string glbPath)
        {
            return target.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + glbPath;
        }

        /// <summary>Coroutines require an active host (MoveAIFusion is often left inactive in the scene).</summary>
        bool EnsureFusionHostActive()
        {
            if (moveApiClient != null && !moveApiClient.gameObject.activeSelf)
                moveApiClient.gameObject.SetActive(true);
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            if (!isActiveAndEnabled)
            {
                Debug.LogWarning("[MoveAIFusionCoordinator] MoveAIFusion is inactive in hierarchy — enable it (or its parents) for GLB preload / fusion.");
                return false;
            }
            return true;
        }

        IEnumerator AttachGlbWhenReady(string glbPath, string recordingFileName, FusedCharacterPlayer target)
        {
            if (target == null) yield break;
            string key = GlbAttachKey(target, glbPath);
            if (!glbAttachInFlight.Add(key))
                yield break;

            Debug.Log($"[MoveAIFusionCoordinator] GLB load started for '{recordingFileName}'…");
            MoveGlbSource source = null;
            float deadline = Time.realtimeSinceStartup + 45f;
            var load = MoveGlbSource.LoadCoroutine(glbPath, GlbHost(), r => source = r);
            while (load.MoveNext())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Debug.LogWarning("[MoveAIFusionCoordinator] Move GLB load timed out after 45s; staying on procedural articulation.");
                    glbAttachInFlight.Remove(key);
                    yield break;
                }
                yield return load.Current;
            }

            string loadError = source != null ? source.Error : "unknown";
            if (target != null && source != null && source.IsReady)
            {
                target.SetMoveGlbSource(source);
                attachedGlbPathByTarget[target.GetInstanceID()] = glbPath;
                if (target == fusedPlayer)
                    SetStatus($"Move GLB articulation active for '{recordingFileName}'");
            }
            else
            {
                Debug.LogWarning("[MoveAIFusionCoordinator] Move GLB load failed; using procedural articulation. " + loadError);
            }
            glbAttachInFlight.Remove(key);
        }

        public bool IsFusedPlaying => fusedPlayer != null && fusedPlayer.IsPlaying;
        public bool IsFusedPaused => fusedPlayer != null && fusedPlayer.IsPaused;
        public float FusedCurrentTime => fusedPlayer != null ? fusedPlayer.CurrentTime : 0f;
        public float FusedDuration => fusedPlayer != null ? fusedPlayer.Duration : 0f;

        public void PauseFusedPlayback()
        {
            if (fusedPlayer != null) fusedPlayer.PausePlayback();
        }

        public void ResumeFusedPlayback()
        {
            if (fusedPlayer != null) fusedPlayer.ResumePlayback();
        }

        public void SeekFusedPlayback(float time)
        {
            if (fusedPlayer != null) fusedPlayer.SeekToTime(time);
        }

        public void SetFusedSegmentLoop(float start, float end, bool enabled)
        {
            if (fusedPlayer != null) fusedPlayer.SetSegmentLoop(start, end, enabled);
        }

        public void StepFusedFrames(int deltaFrames)
        {
            if (fusedPlayer != null) fusedPlayer.StepFrames(deltaFrames);
        }

        public float FusedPlaybackSpeed
        {
            get => fusedPlayer != null ? fusedPlayer.PlaybackSpeed : 1f;
            set { if (fusedPlayer != null) fusedPlayer.PlaybackSpeed = value; }
        }

        public bool FusedLoopPlayback
        {
            get => fusedPlayer != null && fusedPlayer.LoopPlayback;
            set { if (fusedPlayer != null) fusedPlayer.LoopPlayback = value; }
        }

        void EnsureCompareVisualizer()
        {
            if (compareVisualizer == null)
                compareVisualizer = GetComponent<PlaybackCompareVisualizer>();
            if (compareVisualizer == null)
                compareVisualizer = gameObject.AddComponent<PlaybackCompareVisualizer>();
        }

        public void StopFusedPlayback()
        {
            if (compareVisualizer != null)
                compareVisualizer.Stop();
            if (fusedPlayer != null)
                fusedPlayer.StopPlayback();
        }

        void SetStatus(string status)
        {
            if (string.IsNullOrEmpty(status) || status == lastStatus)
            {
                OnStatusChanged?.Invoke(status);
                return;
            }

            lastStatus = status;
            Debug.Log($"[MoveAIFusionCoordinator] {status}");
            OnStatusChanged?.Invoke(status);
        }
    }
}
