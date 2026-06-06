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
        [SerializeField] private Vector3 axisWeights = new Vector3(0.6f, 0.2f, 1.0f);
        [SerializeField] private float smoothingTau = 0.4f;
        [SerializeField] private float outlierMeters = 0.6f;
        [Tooltip("During fused replay, show ARKit (cyan) and Move (orange) skeletons side by side alongside the character.")]
        [SerializeField] private bool showCompareSkeletons = true;

        [Header("Status (read-only)")]
        [SerializeField] private string lastStatus = "";

        private const string FusionFolder = "MoveAIFusion";

        public string LastStatus => lastStatus;
        public bool IsConfigured => moveApiClient != null && moveApiClient.HasApiKey;
        public event Action<string> OnStatusChanged;
        public event Action<string> OnFusionReady; // recordingFileName

        static string FusionDir => Path.Combine(Application.persistentDataPath, FusionFolder);
        static string FusionPath(string recordingFileName) => Path.Combine(FusionDir, recordingFileName + ".fusion.json");

        void Awake()
        {
            // Move processing is server-side; the device only uploads/polls/downloads. Keeping the player running
            // while the app is merely backgrounded (not suspended/killed by iOS) lets the poll loop continue.
            Application.runInBackground = true;
        }

        public static bool HasFusionAsset(string recordingFileName) =>
            !string.IsNullOrEmpty(recordingFileName) && File.Exists(FusionPath(recordingFileName));

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

            // The fusion stack (this coordinator + MoveApiClient) lives on the MoveAIFusion object, which can be
            // left inactive in the scene. Activate it so the upload/poll coroutine can run.
            if (!moveApiClient.gameObject.activeSelf)
                moveApiClient.gameObject.SetActive(true);
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
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
            float clipStart = Mathf.Max(0f, recording.videoStartTimeOffset);
            float clipEnd = recording.duration > 0f ? clipStart + recording.duration : 0f;
            Debug.Log($"[MoveAIFusionCoordinator] Uploading video: {videoBytes.Length / 1024}KB, " +
                      $"recording duration {recording.duration:F2}s, clipWindow [0, {clipEnd:F2}]s, " +
                      $"fusion offset {recording.videoStartTimeOffset:F2}s");

            SetStatus("Submitting climb to Move AI...");
            // Include leading trimmed video time so the baker can sample Move motion at recordingTime + offset.
            string persistedJobId = null;
            moveApiClient.SubmitVideo(videoBytes,
                onComplete: result => OnMoveJobComplete(recording, recordingFileName, result),
                onProgress: p =>
                {
                    // Persist the jobId the moment Move assigns one. The mocap runs server-side, so once this is
                    // saved the app can be backgrounded or hard-closed and still resume/download on next launch.
                    if (!string.IsNullOrEmpty(p.jobId) && p.jobId != persistedJobId)
                    {
                        persistedJobId = p.jobId;
                        PersistJobState(recording, recordingFileName, p.jobId, "RUNNING");
                    }
                    SetStatus($"Move AI: {p.message} ({p.percent:F0}%)");
                },
                clipEndSeconds: clipEnd);
        }

        /// <summary>
        /// Write the current Move job id/state back onto the recording's JSON so an interrupted job (app
        /// backgrounded or killed) can be resumed on the next launch via <see cref="ResumeInterruptedJobs"/>.
        /// </summary>
        void PersistJobState(HipRecording recording, string recordingFileName, string jobId, string state)
        {
            if (recording == null || string.IsNullOrEmpty(recordingFileName))
                return;

            recording.moveJobId = jobId;
            recording.moveJobState = state;
            if (RecordingStorage.SaveRecording(recording, recordingFileName))
                Debug.Log($"[MoveAIFusionCoordinator] Persisted Move job {jobId} ({state}) on '{recordingFileName}' for resume.");
        }

        /// <summary>
        /// On launch, find recordings whose Move job was submitted but never finished (app backgrounded/killed
        /// mid-processing) and reconnect to them — re-polling Move and downloading the result with no re-upload.
        /// Safe to call once during startup; no-ops when nothing is pending.
        /// </summary>
        public void ResumeInterruptedJobs()
        {
            if (moveApiClient == null || !moveApiClient.HasApiKey)
                return;

            if (!moveApiClient.gameObject.activeSelf)
                moveApiClient.gameObject.SetActive(true);
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);

            StartCoroutine(ResumeInterruptedJobsRoutine());
        }

        IEnumerator ResumeInterruptedJobsRoutine()
        {
            var files = RecordingStorage.GetAvailableRecordings(RecordingStorage.StorageFormat.JSON);
            foreach (var fileName in files)
            {
                // Already fused — nothing to resume.
                if (HasFusionAsset(fileName))
                    continue;

                var recording = RecordingStorage.LoadRecording(fileName);
                if (recording == null || string.IsNullOrEmpty(recording.moveJobId))
                    continue;

                // Skip jobs that already reached a terminal state.
                if (string.Equals(recording.moveJobState, "FINISHED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(recording.moveJobState, "FAILED", StringComparison.OrdinalIgnoreCase))
                    continue;

                SetStatus($"Resuming Move AI job for '{fileName}'…");
                Debug.Log($"[MoveAIFusionCoordinator] Resuming Move job {recording.moveJobId} for '{fileName}'.");

                bool resumeDone = false;
                var capturedRecording = recording;
                var capturedFileName = fileName;
                moveApiClient.RedownloadMotionData(recording.moveJobId,
                    onComplete: result =>
                    {
                        OnMoveJobComplete(capturedRecording, capturedFileName, result);
                        resumeDone = true;
                    },
                    onProgress: p => SetStatus($"Move AI (resume): {p.message} ({p.percent:F0}%)"));

                // Resume one job at a time so we don't fire several long polls in parallel.
                while (!resumeDone)
                    yield return null;
            }
        }

        void OnMoveJobComplete(HipRecording recording, string recordingFileName, MoveJobResult result)
        {
            // Persist the terminal state so resume-on-launch knows this job is done (and won't re-poll it).
            string jobId = !string.IsNullOrEmpty(result.jobId) ? result.jobId : recording.moveJobId;
            PersistJobState(recording, recordingFileName, jobId, result.success ? "FINISHED" : "FAILED");

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
            var settings = new MoveAIFusionBaker.Settings
            {
                axisWeights = axisWeights,
                smoothingTau = smoothingTau,
                outlierMeters = outlierMeters,
            };
            var asset = MoveAIFusionBaker.Bake(recording, motion, jointMap, settings);
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
            fusedPlayer.StartPlayback();

            if (showCompareSkeletons && recording != null)
            {
                EnsureCompareVisualizer();
                compareVisualizer.Begin(recording, asset, provider, fusedPlayer);
            }

            SetStatus($"Playing fused replay '{recordingFileName}'");
            return true;
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
