using System;
using System.Collections;
using System.IO;
using UnityEngine;
using BodyTracking.Data;
using BodyTracking.Playback;
using BodyTracking.Spatial;
using BodyTracking.Storage;

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

        /// <summary>
        /// Kick off the Move AI fusion job for a freshly saved recording. <paramref name="videoFilePath"/> is the
        /// mp4 captured alongside the recording; if null/missing, fusion is skipped (the recording still replays
        /// via the existing skeleton player).
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

            if (!EnsureFusionHostActive())
            {
                SetStatus("Fusion skipped: MoveAIFusion inactive");
                return;
            }
            if (string.IsNullOrEmpty(videoFilePath) || !File.Exists(VideoRecorder.NormalizeLocalPath(videoFilePath)))
            {
                SetStatus("Fusion skipped: no paired video file");
                return;
            }

            videoFilePath = VideoRecorder.NormalizeLocalPath(videoFilePath);
            // Read the mp4 off the main thread — a multi-second/large file would otherwise freeze the UI right
            // after the user taps stop. Submission continues on the main thread once the bytes are ready.
            StartCoroutine(ReadFileThenSubmit(recording, recordingFileName, videoFilePath));
        }

        IEnumerator ReadFileThenSubmit(HipRecording recording, string recordingFileName, string videoFilePath)
        {
            SetStatus("Reading video…");

            byte[] videoBytes = null;
            string error = null;
            bool done = false;

            var thread = new System.Threading.Thread(() =>
            {
                try { videoBytes = File.ReadAllBytes(videoFilePath); }
                catch (Exception e) { error = e.Message; }
                finally { done = true; }
            }) { IsBackground = true };
            thread.Start();

            while (!done)
                yield return null;

            if (error != null || videoBytes == null || videoBytes.Length == 0)
            {
                SetStatus($"Fusion skipped: video read failed ({error ?? "empty file"})");
                yield break;
            }

            // Surface the uploaded file size + the processing window. If the mp4 is truncated (read before AVPro
            // finished writing) Move fails ingest with a generic "unable to synchronize" error — this log lets us
            // confirm the file is complete and that the clip window is sane.
            float clipEnd = recording.duration > 0f
                ? recording.videoStartTimeOffset + recording.duration
                : 0f;
            Debug.Log($"[MoveAIFusionCoordinator] Uploading video: {videoBytes.Length / 1024}KB, " +
                      $"recording duration {recording.duration:F2}s, clipWindow end {clipEnd:F2}s");

            SetStatus("Submitting climb to Move AI...");
            // The recording has already been trimmed to the in-frame body window; process only that span so the
            // Move motion matches the saved ARKit recording length.
            moveApiClient.SubmitVideo(videoBytes,
                onComplete: result => OnMoveJobComplete(recording, recordingFileName, result),
                onProgress: p => SetStatus($"Move AI: {p.message} ({p.percent:F0}%)"),
                clipEndSeconds: clipEnd);
        }

        void OnMoveJobComplete(HipRecording recording, string recordingFileName, MoveJobResult result)
        {
            recording.moveJobId = result.jobId;
            recording.moveJobState = result.success ? "FINISHED" : "FAILED";

            if (!result.success)
            {
                SetStatus($"Move AI failed: {result.error}");
                return;
            }

            // Save the raw Move AI GLB (if returned) to the device so it can be pulled off and previewed
            // in any 3D/GLB viewer before fusion.
            SaveGlbToDevice(recordingFileName, result.glbBytes);

            SetStatus("Parsing motion data...");
            var motion = MoveMotionParser.ParseMotionDataZip(result.motionDataZip, jointMap);
            if (motion == null || motion.FrameCount == 0)
            {
                SetStatus("Move AI returned no usable motion");
                return;
            }

            SetStatus("Baking fusion...");
            var asset = MoveAIFusionBaker.Bake(recording, motion, jointMap, ResolveBakeSettings());
            if (asset == null)
            {
                SetStatus("Fusion bake failed");
                return;
            }

            if (asset.Save(FusionPath(recordingFileName)))
            {
                SetStatus($"Fused replay ready for '{recordingFileName}'");
                OnFusionReady?.Invoke(recordingFileName);
            }
            else
            {
                SetStatus("Fusion save failed");
            }
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
        /// On startup, reconnect to any Move AI fusion jobs that were still processing (or finished server-side
        /// but never downloaded) when the app was last closed. For each saved recording that has a Move job id
        /// but no fused asset yet, re-poll Move and — when the motion is ready — parse + bake + save it exactly
        /// like a fresh job. Safe to call when Move is unconfigured or there is nothing to resume.
        /// </summary>
        public void ResumeInterruptedJobs()
        {
            if (moveApiClient == null || !moveApiClient.HasApiKey)
                return;

            var recordings = RecordingStorage.GetAvailableRecordings();
            if (recordings == null || recordings.Count == 0)
                return;

            int resumed = 0;
            foreach (var fileName in recordings)
            {
                // Already fused, nothing to resume.
                if (HasFusionAsset(fileName))
                    continue;

                var recording = RecordingStorage.LoadRecording(fileName);
                if (recording == null || string.IsNullOrEmpty(recording.moveJobId))
                    continue;
                // A failed job won't recover by re-polling.
                if (recording.moveJobState == "FAILED")
                    continue;

                ResumeJob(recording, fileName);
                resumed++;
            }

            if (resumed > 0)
                SetStatus($"Resuming {resumed} Move AI job(s)…");
        }

        void ResumeJob(HipRecording recording, string recordingFileName)
        {
            // The fusion stack can be left inactive in the scene; activate it so the poll/download coroutine runs.
            if (!moveApiClient.gameObject.activeSelf)
                moveApiClient.gameObject.SetActive(true);
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            string jobId = recording.moveJobId;
            Debug.Log($"[MoveAIFusionCoordinator] Resuming Move AI job {jobId} for '{recordingFileName}'");
            moveApiClient.RedownloadMotionData(jobId,
                onComplete: result => OnMoveJobComplete(recording, recordingFileName, result),
                onProgress: p => SetStatus($"Move AI (resume): {p.message} ({p.percent:F0}%)"));
        }

        /// <summary>
        /// Try to start fused playback for a recording. Returns false if no fused asset exists (caller should
        /// fall back to the existing skeleton player).
        /// </summary>
        public bool TryStartFusedPlayback(string recordingFileName, IRouteRootProvider provider, HipRecording recording)
        {
            if (fusedPlayer == null || !HasFusionAsset(recordingFileName))
                return false;

            var asset = MoveAIFusionAsset.Load(FusionPath(recordingFileName));
            if (asset == null) return false;

            // The fused player and compare visualizer rely on per-frame Update(), which only pumps on an active
            // GameObject. This host object can be left disabled in the scene, so activate it before playback.
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            if (fusedPlayer.gameObject != gameObject && !fusedPlayer.gameObject.activeSelf)
                fusedPlayer.gameObject.SetActive(true);

            fusedPlayer.SetRouteRootProvider(provider);
            fusedPlayer.SetSourceRecording(recording);
            if (!fusedPlayer.LoadAsset(asset)) return false;

            // Precedence: if a Move AI GLB exists for this recording, retarget body + fingers from it (muscle space)
            // while positioning/scale stay on the fused trajectory. The GLB loads asynchronously, so defer the
            // actual StartPlayback until it's ready (or failed -> procedural). The fused JSON path is otherwise
            // unchanged, and the dot-skeleton player remains the outer fallback in BodyTrackingController.
            // Only take the GLB path when the player is actually in GLB articulation mode. In FBX mode the player
            // runs the original position-based procedural retarget, so we must NOT load/attach a Move GLB — that
            // keeps the FBX path identical to how it worked before the GLB feature existed.
            bool glbMode = fusedPlayer.ArticulationMode == FusedCharacterPlayer.BodyArticulationSource.MoveGlb;
            if (glbMode && preferGlbArticulation && HasGlb(recordingFileName))
            {
                // Start playback immediately (procedural fallback); attach the GLB muscle retarget when ready so a
                // slow glTFast load on device never blocks the UI or leaves the app feeling frozen.
                StartCoroutine(AttachGlbWhenReady(GlbPath(recordingFileName), recordingFileName));
            }
            else
            {
                fusedPlayer.SetMoveGlbSource(null);
            }

            StartFusedNow(recordingFileName, asset, provider, recording);
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
            StartCoroutine(AttachGlbWhenReady(GlbPath(recordingFileName), recordingFileName));
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

        IEnumerator AttachGlbWhenReady(string glbPath, string recordingFileName)
        {
            Debug.Log($"[MoveAIFusionCoordinator] GLB load started for '{recordingFileName}'…");
            MoveGlbSource source = null;
            float deadline = Time.realtimeSinceStartup + 45f;
            var load = MoveGlbSource.LoadCoroutine(glbPath, GlbHost(), r => source = r);
            while (load.MoveNext())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Debug.LogWarning("[MoveAIFusionCoordinator] Move GLB load timed out after 45s; staying on procedural articulation.");
                    yield break;
                }
                yield return load.Current;
            }

            string loadError = source != null ? source.Error : "unknown";
            if (source != null && source.IsReady)
            {
                fusedPlayer.SetMoveGlbSource(source);
                SetStatus($"Move GLB articulation active for '{recordingFileName}'");
            }
            else
            {
                Debug.LogWarning("[MoveAIFusionCoordinator] Move GLB load failed; using procedural articulation. " + loadError);
            }
        }

        void StartFusedNow(string recordingFileName, MoveAIFusionAsset asset,
                           IRouteRootProvider provider, HipRecording recording)
        {
            fusedPlayer.RefreshGlbArticulation();
            fusedPlayer.StartPlayback();

            if (showCompareSkeletons && recording != null)
            {
                EnsureCompareVisualizer();
                compareVisualizer.Begin(recording, asset, provider, fusedPlayer);
            }

            SetStatus($"Playing fused replay '{recordingFileName}'");
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
