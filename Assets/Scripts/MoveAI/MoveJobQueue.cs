using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Lifecycle of a single queued Move AI fusion job. Persisted as a string so the on-disk queue survives
    /// app suspension/relaunch and unknown future states load without throwing.
    /// </summary>
    public enum MoveQueueState
    {
        Queued,      // enqueued, video not yet uploaded
        Uploading,   // reading/uploading the mp4 and creating the Move job
        Processing,  // job created server-side, polling for completion (resumable via jobId)
        Downloading, // motion/GLB downloading
        Baking,      // parsing + fusing with the ARKit recording
        Done,        // fused asset written
        Failed       // terminal failure (server FAILED or retries exhausted)
    }

    /// <summary>
    /// One Move AI fusion job. Each entry is fully self-describing (recording + video + clip window + job id)
    /// so jobs can be processed serially in any order and resumed after the app sleeps or restarts without
    /// relying on any in-memory controller state.
    /// </summary>
    [Serializable]
    public class MoveQueueEntry
    {
        /// <summary>Recording file name (no extension) this job fuses into.</summary>
        public string recordingFileName;
        /// <summary>Normalized local path of the paired mp4 to upload.</summary>
        public string videoFilePath;
        /// <summary>Server-side clip window end (videoStartTimeOffset + duration); 0 = whole clip.</summary>
        public float clipEndSeconds;
        /// <summary>Move job id once createSingleCamJob has returned (empty until then).</summary>
        public string jobId;
        /// <summary>Serialized <see cref="MoveQueueState"/>.</summary>
        public string state = MoveQueueState.Queued.ToString();
        /// <summary>Last error message for a failed/retrying job.</summary>
        public string error;
        /// <summary>How many times processing has been attempted (retry cap guard).</summary>
        public int attempts;
        /// <summary>ISO-8601 UTC enqueue time, for ordering/diagnostics.</summary>
        public string enqueuedUtc = DateTime.UtcNow.ToString("o");
        /// <summary>Last reported pipeline progress (0–100), or -1 when unknown/not in flight.</summary>
        public float progressPercent = -1f;

        public MoveQueueState State
        {
            get => Enum.TryParse(state, out MoveQueueState s) ? s : MoveQueueState.Queued;
            set => state = value.ToString();
        }

        /// <summary>True once the job reached a terminal state and should not be processed again.</summary>
        public bool IsTerminal => State == MoveQueueState.Done || State == MoveQueueState.Failed;

        /// <summary>True when this job still needs to upload (no server job id yet).</summary>
        public bool NeedsUpload => string.IsNullOrEmpty(jobId);
    }

    [Serializable]
    public class MoveQueueData
    {
        public List<MoveQueueEntry> entries = new List<MoveQueueEntry>();
    }

    /// <summary>
    /// Disk-backed persistence for the Move AI upload queue. The queue is the single source of truth for which
    /// recordings still need fusion, so it must be written on every state transition (and on app pause) — a
    /// recording can be submitted, the app can sleep mid-upload, and processing must resume from exactly here.
    /// </summary>
    public static class MoveJobQueueStore
    {
        const string Folder = "MoveAIQueue";
        const string FileName = "queue.json";
        const int MaxAttempts = 5;

        public static int MaxRetryAttempts => MaxAttempts;

        static string Dir => Path.Combine(Application.persistentDataPath, Folder);
        static string FilePath => Path.Combine(Dir, FileName);

        public static MoveQueueData Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new MoveQueueData();
                string json = File.ReadAllText(FilePath);
                var data = JsonUtility.FromJson<MoveQueueData>(json);
                if (data == null)
                    return new MoveQueueData();
                if (data.entries == null)
                    data.entries = new List<MoveQueueEntry>();
                return data;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MoveJobQueueStore] Failed to load queue (starting empty): {e.Message}");
                return new MoveQueueData();
            }
        }

        public static bool Save(MoveQueueData data)
        {
            if (data == null)
                return false;
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonUtility.ToJson(data, true);
                // Write to a temp file then move, so a kill mid-write can't corrupt the queue.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(tmp, FilePath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MoveJobQueueStore] Failed to save queue: {e.Message}");
                return false;
            }
        }
    }
}
