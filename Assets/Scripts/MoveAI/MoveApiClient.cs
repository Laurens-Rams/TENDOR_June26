using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace BodyTracking.MoveAI
{
    public enum MoveJobState { NotStarted, Uploading, Submitted, Running, Finished, Failed }

    public struct MoveJobProgress
    {
        public MoveJobState state;
        public float percent;     // 0..100 (server-reported when available)
        public string message;
        public string jobId;
    }

    public class MoveJobResult
    {
        public bool success;
        public string error;
        public string jobId;
        public byte[] motionDataZip;
        public byte[] glbBytes; // optional GLB output (null if not requested / unavailable)
    }

    /// <summary>
    /// Drives the Move API single-camera pipeline from Unity using UnityWebRequest (no SDK):
    /// createFile -> PUT upload -> createSingleCamTake -> createSingleCamJob(MOTION_DATA) -> poll getJob ->
    /// download MOTION_DATA. Polling is used for simplicity (webhooks would require a backend); processing can
    /// take several minutes, so callers should treat this as a background "submit now, ready later" job.
    ///
    /// API key handling: embedding a raw key is acceptable for internal/MVP builds only. For production, route
    /// these calls through a thin backend proxy and leave <see cref="apiKey"/> empty here.
    /// </summary>
    public class MoveApiClient : MonoBehaviour
    {
        const int GraphQlTimeoutSeconds = 60;
        const int DownloadTimeoutSeconds = 600;
        const int UploadTimeoutSeconds = 600;
        const int MaxTransientRetries = 3;
        const float RetryBackoffBaseSeconds = 1.5f;
        const float RetryBackoffMaxSeconds = 12f;

        MoveJobState lastLoggedState = MoveJobState.NotStarted;
        float lastLoggedPercent = -1f;

        [Header("Credentials")]
        [Tooltip("Move API key. Leave empty in production and proxy via a backend instead.")]
        [SerializeField] private string apiKey = "";
        [SerializeField] private string endpoint = "https://api.move.ai/ugc/graphql";
        [Tooltip("Send the key as 'Bearer <key>' (some endpoints) instead of the raw key.")]
        [SerializeField] private bool useBearerPrefix = false;

        [Header("Job options")]
        [Tooltip("Move mocap model. s2 = Gen-2 single-cam (fuller skeleton: clavicles + shoulders, better quality). s1 = legacy (collapses to shoulder_rotation only).")]
        [SerializeField] private string mocapModel = "s2";
        [SerializeField] private bool trackFingers = true;
        [SerializeField] private string deviceLabel = "tendor-phone";
        [Tooltip("Also request the Move AI GLB output and save it to the device, so the raw rigged result can be pulled off and previewed before fusion.")]
        [SerializeField] private bool requestGlbOutput = true;

        [Header("Polling")]
        [SerializeField] private float pollIntervalSeconds = 15f;
        [SerializeField] private float timeoutSeconds = 2400f; // 40 min

        [Header("Debug replay")]
        [Tooltip("A previously submitted Move jobId. Use the context-menu action (Play mode) to re-download and " +
                 "parse its MOTION_DATA without re-recording or re-uploading.")]
        [SerializeField] private string debugReplayJobId = "";

        public bool HasApiKey => !string.IsNullOrEmpty(apiKey);

        /// <summary>
        /// Editor convenience: re-download the configured <see cref="debugReplayJobId"/> and run it through the
        /// parser, logging the result. Enter Play mode, set the jobId in the inspector, then invoke this from the
        /// component's context menu (the ⋮ menu). The raw archive is also dumped to MoveAIDebug for offline reuse.
        /// </summary>
        [ContextMenu("Debug: Re-download + parse jobId")]
        void DebugRedownloadAndParse()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[MoveApiClient] Enter Play mode first — the re-download uses coroutines/UnityWebRequest.");
                return;
            }
            if (string.IsNullOrEmpty(debugReplayJobId))
            {
                Debug.LogWarning("[MoveApiClient] Set 'debugReplayJobId' in the inspector first.");
                return;
            }

            RedownloadMotionData(debugReplayJobId,
                onComplete: r =>
                {
                    if (!r.success)
                    {
                        Debug.LogError($"[MoveApiClient] Debug re-download failed: {r.error}");
                        return;
                    }
                    var m = MoveMotionParser.ParseMotionDataZip(r.motionDataZip, null);
                    Debug.Log(m != null && m.FrameCount > 0
                        ? $"[MoveApiClient] Debug parse OK: {m.JointCount} joints, {m.FrameCount} frames, {m.fps} fps"
                        : "[MoveApiClient] Debug parse produced no usable motion — see the [MoveMotionParser] schema logs.");
                },
                onProgress: p => Debug.Log($"[MoveApiClient] Debug replay: {p.message} ({p.percent:F0}%)"));
        }

        /// <summary>
        /// Run the full pipeline for a recorded mp4. Invokes <paramref name="onProgress"/> as state changes and
        /// <paramref name="onComplete"/> exactly once at the end (success or failure).
        /// </summary>
        /// <param name="clipEndSeconds">
        /// When &gt; 0, only the clip [0, clipEndSeconds] of the video is processed (server-side, no re-encode).
        /// Used to drop the tail where the climber steps out of frame to tap stop.
        /// </param>
        public void SubmitVideo(byte[] videoBytes, Action<MoveJobResult> onComplete, Action<MoveJobProgress> onProgress = null, float clipEndSeconds = 0f)
        {
            // Coroutines can't start on an inactive GameObject; activate the host first (MoveAIFusion root).
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            if (!enabled)
                enabled = true;

            if (!isActiveAndEnabled)
            {
                Debug.LogError("[MoveApiClient] Host GameObject is inactive in hierarchy — cannot submit to Move AI. Activate the MoveAIFusion object (or its parents).");
                onComplete?.Invoke(new MoveJobResult { success = false, error = "MoveApiClient host inactive" });
                return;
            }

            StartCoroutine(RunPipeline(videoBytes, onComplete, onProgress, clipEndSeconds));
        }

        /// <summary>
        /// Re-download the MOTION_DATA for a job that was already submitted/finished, skipping the upload and
        /// processing. Use this to iterate on parsing/baking against a real Move result without re-recording or
        /// re-uploading (pass a <c>moveJobId</c> stored on a previous recording). If the job isn't finished yet it
        /// polls until it is (or times out).
        /// </summary>
        public void RedownloadMotionData(string jobId, Action<MoveJobResult> onComplete, Action<MoveJobProgress> onProgress = null)
        {
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            if (!enabled) enabled = true;
            if (!isActiveAndEnabled)
            {
                onComplete?.Invoke(new MoveJobResult { success = false, error = "MoveApiClient host inactive" });
                return;
            }
            if (string.IsNullOrEmpty(jobId))
            {
                onComplete?.Invoke(new MoveJobResult { success = false, error = "No jobId provided" });
                return;
            }
            StartCoroutine(RedownloadPipeline(jobId, onComplete, onProgress));
        }

        IEnumerator RedownloadPipeline(string jobId, Action<MoveJobResult> onComplete, Action<MoveJobProgress> onProgress)
        {
            var result = new MoveJobResult { jobId = jobId };
            if (!HasApiKey) { Fail(result, onComplete, "No Move API key configured"); yield break; }

            lastLoggedState = MoveJobState.NotStarted;
            lastLoggedPercent = -1f;

            float elapsed = 0f;
            string motionUrl = null;
            while (elapsed < timeoutSeconds && string.IsNullOrEmpty(motionUrl))
            {
                string state = null;
                float percent = 0f;
                string failMessage = null;
                // Poll status first because Move output file URLs can error until processing has finished.
                string statusQuery =
                    "query GetJob { getJob(jobId: \"" + Escape(jobId) +
                    "\") { id progress { state percentageComplete } } }";
                Dictionary<string, object> pollResponse = null;
                yield return GraphQLRaw(statusQuery, r => pollResponse = r);

                if (pollResponse != null)
                {
                    var job = ObjAt(pollResponse, "data", "getJob");
                    if (job != null)
                    {
                        var progress = MiniJson.AsObject(Field(job, "progress"));
                        state = MiniJson.ToStr(Field(progress, "state"));
                        percent = MiniJson.ToFloat(Field(progress, "percentageComplete"));
                    }

                    if (pollResponse.TryGetValue("errors", out var errs) &&
                        TryParseJobFromGraphQLErrors(errs, out var errState, out var errPercent, out failMessage))
                    {
                        state = errState;
                        if (errPercent > 0f)
                            percent = errPercent;
                    }
                }

                if (!string.IsNullOrEmpty(state))
                {
                    if (state.Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
                    {
                        Report(onProgress, MoveJobState.Running, 95, "Job finished", jobId);
                        string outputsQuery =
                            "query GetJobOutputs { getJob(jobId: \"" + Escape(jobId) +
                            "\") { outputs { key file { presignedUrl } } } }";
                        yield return GraphQL(outputsQuery,
                            resp =>
                            {
                                var job = ObjAt(resp, "data", "getJob");
                                motionUrl = FindOutputUrl(job, "MOTION_DATA");
                            },
                            err => Debug.LogWarning($"[MoveApiClient] redownload outputs error (will retry): {err}"));
                    }
                    else if (state.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        Fail(result, onComplete, failMessage ?? "Move job FAILED");
                        yield break;
                    }
                    else
                    {
                        Report(onProgress, MoveJobState.Running, Mathf.Clamp(percent, 25, 90), $"Processing ({state})", jobId);
                    }
                }
                else if (pollResponse != null && pollResponse.TryGetValue("errors", out var unknownErrs))
                {
                    Debug.LogWarning($"[MoveApiClient] redownload poll error (will retry): {MiniJson.Serialize(unknownErrs)}");
                }

                if (!string.IsNullOrEmpty(motionUrl)) break;
                yield return new WaitForSeconds(pollIntervalSeconds);
                elapsed += pollIntervalSeconds;
            }

            if (string.IsNullOrEmpty(motionUrl))
            {
                Fail(result, onComplete, "No MOTION_DATA output available for that jobId (not finished?)");
                yield break;
            }

            Report(onProgress, MoveJobState.Running, 97, "Downloading motion data", jobId);
            byte[] motionBytes = null;
            string motionDownloadError = null;
            yield return DownloadBytesWithRetry(motionUrl, DownloadTimeoutSeconds, "MOTION_DATA",
                data => motionBytes = data, err => motionDownloadError = err);
            if (motionDownloadError != null)
            {
                Fail(result, onComplete, motionDownloadError);
                yield break;
            }
            result.motionDataZip = motionBytes;

            result.success = true;
            Report(onProgress, MoveJobState.Finished, 100, "Done", jobId);
            onComplete?.Invoke(result);
        }

        IEnumerator RunPipeline(byte[] videoBytes, Action<MoveJobResult> onComplete, Action<MoveJobProgress> onProgress, float clipEndSeconds = 0f)
        {
            var result = new MoveJobResult();

            if (!HasApiKey)
            {
                Fail(result, onComplete, "No Move API key configured");
                yield break;
            }
            if (videoBytes == null || videoBytes.Length == 0)
            {
                Fail(result, onComplete, "No video bytes to upload");
                yield break;
            }

            lastLoggedState = MoveJobState.NotStarted;
            lastLoggedPercent = -1f;

            // 1) createFile -> id + presignedUrl
            Report(onProgress, MoveJobState.Uploading, 0, "Creating file", null);
            string fileId = null, presignedUrl = null;
            yield return GraphQL(
                "mutation CreateFile { createFile(type: \"mp4\") { id presignedUrl } }",
                resp =>
                {
                    var data = ObjAt(resp, "data", "createFile");
                    fileId = MiniJson.ToStr(Field(data, "id"));
                    presignedUrl = MiniJson.ToStr(Field(data, "presignedUrl"));
                },
                err => Fail(result, onComplete, $"createFile failed: {err}"));
            if (result.error != null) yield break;
            if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(presignedUrl))
            {
                Fail(result, onComplete, "createFile returned no id/presignedUrl");
                yield break;
            }

            // 2) PUT upload to S3 presigned url (no Content-Type — Move API docs; extra headers break the signature)
            Report(onProgress, MoveJobState.Uploading, 10, "Uploading video", null);
            string uploadError = null;
            yield return PutPresignedUpload(presignedUrl, videoBytes, err => uploadError = err);
            if (uploadError != null)
            {
                Fail(result, onComplete, uploadError);
                yield break;
            }

            // 3) createSingleCamTake
            Report(onProgress, MoveJobState.Submitted, 20, "Creating take", null);
            string takeId = null;
            string takeQuery =
                "mutation CreateTake { take: createSingleCamTake(sources: [{ deviceLabel: \"" + Escape(deviceLabel) +
                "\", fileId: \"" + Escape(fileId) + "\", format: MP4 }]) { id } }";
            yield return GraphQL(takeQuery,
                resp => takeId = MiniJson.ToStr(Field(ObjAt(resp, "data", "take"), "id")),
                err => Fail(result, onComplete, $"createSingleCamTake failed: {err}"));
            if (result.error != null) yield break;
            if (string.IsNullOrEmpty(takeId))
            {
                Fail(result, onComplete, "createSingleCamTake returned no id");
                yield break;
            }

            // 4) createSingleCamJob with MOTION_DATA output.
            // mocapModel is the MocapModelOptionsInput GraphQL enum (M1/M2/S1/S2) — unquoted, uppercase.
            Report(onProgress, MoveJobState.Submitted, 25, "Submitting job", null);
            string mocapEnum = NormalizeMocapModel(mocapModel);
            string options = "{ mocapModel: " + mocapEnum + ", trackFingers: " + (trackFingers ? "true" : "false") + " }";
            // Trim the tail (climber stepping out of frame to tap stop) by only processing [0, clipEndSeconds].
            string clipWindowArg = clipEndSeconds > 0f
                ? ", clipWindow: { startTime: 0, endTime: " + clipEndSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " }"
                : "";
            string outputsList = requestGlbOutput ? "[MOTION_DATA, MAIN_GLB]" : "[MOTION_DATA]";
            string jobQuery =
                "mutation CreateJob { job: createSingleCamJob(takeId: \"" + Escape(takeId) +
                "\", outputs: " + outputsList + ", options: " + options + clipWindowArg + ") { id progress { state percentageComplete } } }";
            string jobId = null;
            yield return GraphQL(jobQuery,
                resp => jobId = MiniJson.ToStr(Field(ObjAt(resp, "data", "job"), "id")),
                err => Fail(result, onComplete, $"createSingleCamJob failed: {err}"));
            if (result.error != null) yield break;
            if (string.IsNullOrEmpty(jobId))
            {
                Fail(result, onComplete, "createSingleCamJob returned no id");
                yield break;
            }
            result.jobId = jobId;

            // 5) poll getJob until FINISHED/FAILED
            float elapsed = 0f;
            string motionUrl = null;
            string glbUrl = null;
            while (elapsed < timeoutSeconds)
            {
                string state = null;
                float percent = 0f;
                string failMessage = null;
                // Poll STATUS ONLY. Requesting outputs[].file.presignedUrl before the job finishes makes Move
                // return a top-level GraphQL error ("File object does not exist"), which would mask the job state
                // and prevent us from ever detecting FINISHED. The download URL is fetched separately below.
                string getQuery =
                    "query GetJob { getJob(jobId: \"" + Escape(jobId) +
                    "\") { id progress { state percentageComplete } } }";
                Dictionary<string, object> pollResponse = null;
                yield return GraphQLRaw(getQuery, r => pollResponse = r);

                if (pollResponse != null)
                {
                    var job = ObjAt(pollResponse, "data", "getJob");
                    if (job != null)
                    {
                        var progress = MiniJson.AsObject(Field(job, "progress"));
                        state = MiniJson.ToStr(Field(progress, "state"));
                        percent = MiniJson.ToFloat(Field(progress, "percentageComplete"));
                    }

                    // Move returns FAILED jobs as GraphQL errors with partial data embedded in each error object.
                    if (pollResponse.TryGetValue("errors", out var errs) &&
                        TryParseJobFromGraphQLErrors(errs, out var errState, out var errPercent, out failMessage))
                    {
                        state = errState;
                        if (errPercent > 0f)
                            percent = errPercent;
                    }
                }

                if (!string.IsNullOrEmpty(state))
                {
                    if (state.Equals("FINISHED", StringComparison.OrdinalIgnoreCase))
                    {
                        Report(onProgress, MoveJobState.Running, 95, "Job finished", jobId);
                        // Now that the job is done, the output file exists; fetch its presigned download URL.
                        string outputsQuery =
                            "query GetJobOutputs { getJob(jobId: \"" + Escape(jobId) +
                            "\") { outputs { key file { presignedUrl } } } }";
                        yield return GraphQL(outputsQuery,
                            resp =>
                            {
                                var job = ObjAt(resp, "data", "getJob");
                                motionUrl = FindOutputUrl(job, "MOTION_DATA");
                                if (requestGlbOutput) glbUrl = FindOutputUrl(job, "GLB");
                            },
                            err => Debug.LogWarning($"[MoveApiClient] getJob outputs error (will retry): {err}"));
                        if (!string.IsNullOrEmpty(motionUrl))
                            break;
                        // Output URL not ready on this attempt; keep polling briefly until it materializes.
                    }
                    else if (state.Equals("FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        Fail(result, onComplete, failMessage ?? "Move job FAILED");
                        yield break;
                    }
                    else
                    {
                        Report(onProgress, MoveJobState.Running, Mathf.Clamp(percent, 25, 90), $"Processing ({state})", jobId);
                    }
                }
                else if (pollResponse != null && pollResponse.TryGetValue("errors", out var unknownErrs))
                {
                    Debug.LogWarning($"[MoveApiClient] getJob poll error (will retry): {MiniJson.Serialize(unknownErrs)}");
                }

                yield return new WaitForSeconds(pollIntervalSeconds);
                elapsed += pollIntervalSeconds;
            }

            if (string.IsNullOrEmpty(motionUrl))
            {
                Fail(result, onComplete, "Timed out waiting for Move job / no MOTION_DATA output");
                yield break;
            }

            // 6) download MOTION_DATA zip
            Report(onProgress, MoveJobState.Running, 97, "Downloading motion data", jobId);
            byte[] motionBytes = null;
            string motionDownloadError = null;
            yield return DownloadBytesWithRetry(motionUrl, DownloadTimeoutSeconds, "MOTION_DATA",
                data => motionBytes = data, err => motionDownloadError = err);
            if (motionDownloadError != null)
            {
                Fail(result, onComplete, motionDownloadError);
                yield break;
            }
            result.motionDataZip = motionBytes;

            // 6b) optional GLB download (best-effort — never fail the job if the GLB is missing/slow).
            if (requestGlbOutput && !string.IsNullOrEmpty(glbUrl))
            {
                Report(onProgress, MoveJobState.Running, 99, "Downloading GLB", jobId);
                yield return DownloadBytesWithRetry(glbUrl, DownloadTimeoutSeconds, "GLB",
                    data => result.glbBytes = data,
                    err => Debug.LogWarning($"[MoveApiClient] GLB download failed (continuing): {err}"));
            }
            else if (requestGlbOutput)
            {
                Debug.LogWarning("[MoveApiClient] No GLB output URL on finished job (continuing with motion data only).");
            }

            result.success = true;
            Report(onProgress, MoveJobState.Finished, 100, "Done", jobId);
            onComplete?.Invoke(result);
        }

        /// <summary>
        /// Upload bytes to Move's S3 presigned PUT URL. Must not add headers beyond what the URL was signed with
        /// (Move's examples use a bare PUT with no Content-Type).
        /// </summary>
        static IEnumerator PutPresignedUpload(string presignedUrl, byte[] data, Action<string> onError)
        {
            if (string.IsNullOrEmpty(presignedUrl) || data == null || data.Length == 0)
            {
                onError?.Invoke("Presigned upload: missing url or data");
                yield break;
            }

            string lastError = null;
            for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Debug.LogWarning($"[MoveApiClient] Retrying video upload ({attempt}/{MaxTransientRetries})");
                    yield return WaitRetryBackoff(attempt - 1);
                }

                // Move's presigned URLs are AWS SigV2 (AWSAccessKeyId/Signature/Expires query params). In SigV2 the
                // Content-Type header is part of the signed StringToSign, and Move's docs show a bare PUT. Unity's
                // UploadHandlerRaw defaults to "application/octet-stream", which changes the signature, so force the
                // request header itself to an empty value. Do not retry with other content types; they are guaranteed
                // to produce SignatureDoesNotMatch for Move's current URLs and hide the useful first response.
                using var put = new UnityWebRequest(presignedUrl, UnityWebRequest.kHttpVerbPUT);
                var upload = new UploadHandlerRaw(data);
                upload.contentType = "";
                put.uploadHandler = upload;
                put.downloadHandler = new DownloadHandlerBuffer();
                put.timeout = UploadTimeoutSeconds;
                put.SetRequestHeader("Content-Type", "");

                yield return put.SendWebRequest();

                if (put.result == UnityWebRequest.Result.Success)
                    yield break;

                lastError = FormatUploadError(put, "(empty)");
                if (!IsTransientNetworkFailure(put) || attempt >= MaxTransientRetries)
                {
                    onError?.Invoke(lastError);
                    yield break;
                }
            }

            if (!string.IsNullOrEmpty(lastError))
                onError?.Invoke(lastError);
        }

        static string FormatUploadError(UnityWebRequest put, string contentType)
        {
            string body = put.downloadHandler?.text;
            if (!string.IsNullOrEmpty(body) && body.Length > 400)
                body = body.Substring(0, 400) + "…";
            string ct = string.IsNullOrEmpty(contentType) ? "(empty)" : contentType;
            return $"Video upload failed: HTTP/{put.responseCode} {put.error} [Content-Type={ct}] {body}";
        }

        // GraphQL POST helper ----------------------------------------------------------------------

        /// <summary>Returns the full parsed JSON body (including partial <c>data</c> alongside <c>errors</c>).</summary>
        IEnumerator GraphQLRaw(string query, Action<Dictionary<string, object>> onResult)
        {
            for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Debug.LogWarning($"[MoveApiClient] Retrying GraphQL poll ({attempt}/{MaxTransientRetries})");
                    yield return WaitRetryBackoff(attempt - 1);
                }

                var bodyDict = new Dictionary<string, object> { { "query", query } };
                string body = MiniJson.Serialize(bodyDict);
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(body);

                using var req = new UnityWebRequest(endpoint, "POST");
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = GraphQlTimeoutSeconds;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", useBearerPrefix ? $"Bearer {apiKey}" : apiKey);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (IsTransientNetworkFailure(req) && attempt < MaxTransientRetries)
                        continue;
                    onResult?.Invoke(null);
                    yield break;
                }

                onResult?.Invoke(MiniJson.AsObject(MiniJson.Parse(req.downloadHandler.text)));
                yield break;
            }
        }

        IEnumerator GraphQL(string query, Action<Dictionary<string, object>> onOk, Action<string> onError)
        {
            for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Debug.LogWarning($"[MoveApiClient] Retrying GraphQL ({attempt}/{MaxTransientRetries})");
                    yield return WaitRetryBackoff(attempt - 1);
                }

                var bodyDict = new Dictionary<string, object> { { "query", query } };
                string body = MiniJson.Serialize(bodyDict);
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(body);

                using var req = new UnityWebRequest(endpoint, "POST");
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = GraphQlTimeoutSeconds;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", useBearerPrefix ? $"Bearer {apiKey}" : apiKey);

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string transportError = $"{req.error} :: {req.downloadHandler?.text}";
                    if (IsTransientNetworkFailure(req) && attempt < MaxTransientRetries)
                        continue;
                    onError?.Invoke(transportError);
                    yield break;
                }

                var parsed = MiniJson.AsObject(MiniJson.Parse(req.downloadHandler.text));
                if (parsed == null)
                {
                    onError?.Invoke("Unparseable GraphQL response");
                    yield break;
                }
                if (parsed.TryGetValue("errors", out var errs) && errs != null)
                {
                    onError?.Invoke($"GraphQL errors: {MiniJson.Serialize(errs)}");
                    yield break;
                }
                onOk?.Invoke(parsed);
                yield break;
            }
        }

        IEnumerator DownloadBytesWithRetry(string url, int timeoutSeconds, string label,
            Action<byte[]> onSuccess, Action<string> onError)
        {
            if (string.IsNullOrEmpty(url))
            {
                onError?.Invoke($"{label} download: missing url");
                yield break;
            }

            string lastError = null;
            for (int attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                if (attempt > 0)
                {
                    Debug.LogWarning($"[MoveApiClient] Retrying {label} download ({attempt}/{MaxTransientRetries})");
                    yield return WaitRetryBackoff(attempt - 1);
                }

                using var dl = UnityWebRequest.Get(url);
                dl.timeout = timeoutSeconds;
                yield return dl.SendWebRequest();
                if (dl.result == UnityWebRequest.Result.Success)
                {
                    onSuccess?.Invoke(dl.downloadHandler.data);
                    yield break;
                }

                lastError = $"{label} download failed: {dl.error}";
                if (!IsTransientNetworkFailure(dl) || attempt >= MaxTransientRetries)
                {
                    onError?.Invoke(lastError);
                    yield break;
                }
            }

            if (!string.IsNullOrEmpty(lastError))
                onError?.Invoke(lastError);
        }

        static bool IsTransientNetworkFailure(UnityWebRequest req)
        {
            if (req.result == UnityWebRequest.Result.ConnectionError ||
                req.result == UnityWebRequest.Result.DataProcessingError)
                return true;
            if (req.result != UnityWebRequest.Result.ProtocolError)
                return false;

            long code = req.responseCode;
            return code == 408 || code == 429 || code >= 500;
        }

        static IEnumerator WaitRetryBackoff(int attempt)
        {
            float delay = Mathf.Min(RetryBackoffBaseSeconds * Mathf.Pow(2f, attempt), RetryBackoffMaxSeconds);
            yield return new WaitForSecondsRealtime(delay);
        }

        // Response helpers -------------------------------------------------------------------------

        static Dictionary<string, object> ObjAt(Dictionary<string, object> root, params string[] path)
        {
            object node = root;
            foreach (var p in path)
            {
                var o = MiniJson.AsObject(node);
                if (o == null || !o.TryGetValue(p, out node)) return null;
            }
            return MiniJson.AsObject(node);
        }

        static object Field(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out var v)) return v;
            return null;
        }

        static string FindOutputUrl(Dictionary<string, object> job, string key)
        {
            var outputs = MiniJson.AsArray(Field(job, "outputs"));
            if (outputs == null) return null;
            foreach (var o in outputs)
            {
                var oo = MiniJson.AsObject(o);
                string k = MiniJson.ToStr(Field(oo, "key"));
                if (k != null && k.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var file = MiniJson.AsObject(Field(oo, "file"));
                    return MiniJson.ToStr(Field(file, "presignedUrl"));
                }
            }
            return null;
        }

        static string Escape(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        /// <summary>
        /// Move returns failed jobs as GraphQL errors with partial job data in each error's <c>data.progress</c>.
        /// </summary>
        static bool TryParseJobFromGraphQLErrors(object errorsNode, out string state, out float percent, out string message)
        {
            state = null;
            percent = 0f;
            message = null;

            var errors = MiniJson.AsArray(errorsNode);
            if (errors == null || errors.Count == 0)
                return false;

            foreach (var errObj in errors)
            {
                var err = MiniJson.AsObject(errObj);
                if (err == null)
                    continue;

                message = MiniJson.ToStr(Field(err, "message"));

                var errorInfo = MiniJson.AsObject(Field(err, "errorInfo"));
                var suggestions = MiniJson.AsArray(Field(errorInfo, "suggestions"));
                if (suggestions != null && suggestions.Count > 0)
                {
                    var sb = new StringBuilder(message ?? "Move job failed");
                    sb.Append(": ");
                    sb.Append(MiniJson.ToStr(suggestions[0]));
                    message = sb.ToString();
                }

                var data = MiniJson.AsObject(Field(err, "data"));
                var progress = MiniJson.AsObject(Field(data, "progress"));
                state = MiniJson.ToStr(Field(progress, "state"));
                percent = MiniJson.ToFloat(Field(progress, "percentageComplete"));

                if (!string.IsNullOrEmpty(state))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Map the configured model string to a valid MocapModelOptionsInput enum literal (M1/M2/S1/S2). The enum
        /// is sent unquoted in the GraphQL query; defaults to S2 if unrecognized.
        /// </summary>
        static string NormalizeMocapModel(string model)
        {
            string m = (model ?? "").Trim().ToUpperInvariant();
            switch (m)
            {
                case "M1":
                case "M2":
                case "S1":
                case "S2":
                    return m;
                default:
                    return "S2";
            }
        }

        void Report(Action<MoveJobProgress> cb, MoveJobState state, float percent, string message, string jobId)
        {
            bool finishedOrFailed = state == MoveJobState.Finished || state == MoveJobState.Failed;
            bool stateChanged = state != lastLoggedState;
            bool percentStep = percent < 0f || lastLoggedPercent < 0f || Mathf.Abs(percent - lastLoggedPercent) >= 10f;
            if (finishedOrFailed || stateChanged || percentStep)
            {
                lastLoggedState = state;
                lastLoggedPercent = percent;
                Debug.Log($"[MoveApiClient] {state} {percent:F0}% - {message}");
            }

            cb?.Invoke(new MoveJobProgress { state = state, percent = percent, message = message, jobId = jobId });
        }

        void Fail(MoveJobResult result, Action<MoveJobResult> onComplete, string error)
        {
            Debug.LogError($"[MoveApiClient] {error}");
            result.success = false;
            result.error = error;
            onComplete?.Invoke(result);
        }
    }
}
