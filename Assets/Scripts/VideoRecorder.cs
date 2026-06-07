using System;
using System.Collections;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.UI;
using TMPro;
using RenderHeads.Media.AVProMovieCapture;

/// <summary>
/// Records the AR camera feed to mp4 via AVPro Movie Capture (<see cref="CaptureFromTexture"/>), for pairing
/// with body recordings and Move AI upload. Writes under persistentDataPath so files are readable by
/// <see cref="BodyTracking.MoveAI.MoveAIFusionCoordinator"/>.
/// </summary>
public class VideoRecorder : MonoBehaviour
{
    const int DefaultWidth = 1280;
    const int DefaultHeight = 720;

    /// <summary>How to rotate the captured AR frame before encoding (camera image is landscape).</summary>
    public enum VideoRotation
    {
        None,
        CounterClockwise90,
        Clockwise90,
        Rotate180,
        /// <summary>Portrait upright on iPhone — matches on-screen AR view (sensor→portrait remap).</summary>
        PortraitUpright,
    }

    [Header("AVPro")]
    [SerializeField] private CaptureFromTexture capture;
    [Tooltip("When true, configures output folder, mp4 extension, and frame rate on Awake.")]
    [SerializeField] private bool configureCaptureDefaults = true;
    [SerializeField] private string outputSubfolder = "BodyTrackingVideos";
    [SerializeField] private string filenamePrefix = "tendor_climb";
    [SerializeField] private float captureFrameRate = 30f;
    [Tooltip("After the mp4 is written, also copy it into the iOS Photos app (device only). Original file stays in app storage for Move AI.")]
    [SerializeField] private bool exportToPhotoLibraryAfterCapture = true;

    [Header("Orientation")]
    [Tooltip("Rotate each captured frame before encoding. CounterClockwise90 is upright portrait on iPhone (Clockwise90 is the same image upside-down). Use PortraitUpright only if you need the full sensor→portrait remap.")]
    [SerializeField] private VideoRotation rotation = VideoRotation.PortraitUpright;

    [Header("AR")]
    [SerializeField] private ARCameraManager cameraManager;

    [Header("Encoder prewarm")]
    [Tooltip("Run a throwaway capture once at startup so the first real record tap is instant (~15s VideoToolbox init otherwise). Disable if the brief startup hitch is worse than a slow first record.")]
    [SerializeField] private bool enableEncoderPrewarm = false;
    [Tooltip("Seconds after the first AR camera frame before prewarm runs (lets map switches / localization settle first).")]
    [SerializeField] private float prewarmDelayAfterFirstFrameSeconds = 6f;
    [Tooltip("AVPro's first StartCapture blocks the main thread for several seconds (VideoToolbox init). Defer until the device is still so scanning/moving the phone stays responsive.")]
    [SerializeField] private bool deferPrewarmUntilStill = true;
    [Tooltip("Gyro rotation rate (rad/s) below which the device counts as still.")]
    [SerializeField] private float prewarmStillnessThresholdRadPerSec = 0.75f;
    [Tooltip("Seconds of continuous stillness required before the blocking encoder warm runs.")]
    [SerializeField] private float prewarmStillDurationSeconds = 0.5f;
    [Tooltip("If the phone never settles, run prewarm anyway after this many seconds (may hitch while moving).")]
    [SerializeField] private float prewarmStillnessTimeoutSeconds = 30f;

    [Header("Optional UI (not required for capture)")]
    [SerializeField] private TextMeshProUGUI tmpFrames;
    [SerializeField] private RawImage image;

    Texture2D tex;
    XRCpuImage cpuImage;
    int frameCount;
    Action<string> stopCallback;
    Coroutine waitForFileCoroutine;
    bool warnedInactiveHost;

    // Self-throttling so we feed AVPro exactly one frame per target interval (constant frame rate). The AR camera
    // delivers ~60fps; without throttling, manual-update capture would encode every AR frame and the clip would
    // play at the wrong speed (declared fps != frame count). Move's single-cam temporal solve needs evenly-timed
    // frames, so we pace feeding by wall-clock time.
    float captureStartRealtime;
    float nextCaptureRealtime;
    float CaptureInterval => captureFrameRate > 0f ? 1f / captureFrameRate : 1f / 30f;

    // Prewarm state: a throwaway capture is run once at init so the costly first-time native encoder/VideoToolbox
    // setup is paid during startup instead of stalling ~15s on the first real record tap.
    bool isWarmingUp;
    bool prewarmed;
    int prewarmedCaptureWidth;
    int prewarmedCaptureHeight;
    Coroutine prewarmCoroutine;
    Coroutine deferredPrewarmCoroutine;
    string warmupFilePath;

    // Set by OnFileWriteComplete when AVPro has fully flushed the real (non-prewarm) mp4 to disk. We wait for this
    // before invoking the stop callback so consumers (Photos export, Move AI upload) never read a partial file.
    bool fileWriteCompleted;
    string completedWritePath;

    /// <summary>True after a throwaway capture has warmed the native encoder at the current AR resolution.</summary>
    public bool IsEncoderPrewarmed => prewarmed;

    /// <summary>True while the throwaway encoder warm is running (including waiting for stillness).</summary>
    public bool IsPrewarming => isWarmingUp;

    // Landscape conversion buffer (raw AR image) used as the source for rotation into the portrait `tex`.
    NativeArray<byte> srcBuffer;
    int captureWidth;   // landscape source width  (e.g. 1920)
    int captureHeight;  // landscape source height (e.g. 1440)

    public bool IsRecording { get; private set; }

    /// <summary>Best-known path to the last finished capture (after async file write completes).</summary>
    public string LastFilePath { get; private set; }

    /// <summary>Fired when StopRecording finishes and the mp4 path is ready (may be null on failure).</summary>
    public event Action<string> OnRecordingFinished;

    /// <summary>Fired after optional Photos export completes (bool = success).</summary>
    public event Action<string, bool> OnPhotosExportFinished;

    /// <summary>Folder under persistentDataPath where mp4 files are stored.</summary>
    public string OutputFolderFullPath =>
        Path.Combine(Application.persistentDataPath, outputSubfolder);

    /// <summary>AVPro sometimes returns file:// URIs; File.Exists and File.ReadAllBytes need a local path.</summary>
    public static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(path).LocalPath;
            }
            catch
            {
                return path.Substring("file://".Length);
            }
        }

        return path;
    }

    static bool LocalFileExists(string path)
    {
        path = NormalizeLocalPath(path);
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    void Awake()
    {
        if (capture == null)
            capture = GetComponent<CaptureFromTexture>();
        ResolveCameraManager();
        if (configureCaptureDefaults && capture != null)
            ConfigureCaptureDefaults();
    }

    IEnumerator Start()
    {
        // Prefer persistentDataPath for Move AI upload; only use photo library if explicitly configured.
#if (UNITY_STANDALONE_OSX || UNITY_IOS) && !UNITY_EDITOR
        if (capture != null && capture.OutputFolder == CaptureBase.OutputPath.PhotoLibrary)
        {
            var level = CaptureBase.PhotoLibraryAccessLevel.AddOnly;
            switch (CaptureBase.HasUserAuthorisationToAccessPhotos(level))
            {
                case CaptureBase.PhotoLibraryAuthorisationStatus.Unavailable:
                case CaptureBase.PhotoLibraryAuthorisationStatus.Denied:
                    Debug.LogWarning("[VideoRecorder] Photo library unavailable/denied; using persistentDataPath");
                    capture.OutputFolder = CaptureBase.OutputPath.RelativeToPersistentData;
                    break;
                case CaptureBase.PhotoLibraryAuthorisationStatus.NotDetermined:
                    yield return CaptureBase.RequestUserAuthorisationToAccessPhotos(level);
                    if (CaptureBase.HasUserAuthorisationToAccessPhotos(level) != CaptureBase.PhotoLibraryAuthorisationStatus.Authorised)
                    {
                        Debug.LogWarning("[VideoRecorder] Photo library not authorised; using persistentDataPath");
                        capture.OutputFolder = CaptureBase.OutputPath.RelativeToPersistentData;
                    }
                    break;
            }
        }
#endif
        yield return null;

        if (SystemInfo.supportsGyroscope)
            Input.gyro.enabled = true;

        // AR intrinsics are not ready during BodyTrackingController.Initialize(); defer prewarm until the camera
        // is actually producing frames at its real resolution (1920x1440 etc.), otherwise the throwaway capture
        // runs at 1280x720 defaults and the first real record still pays the full CreateRecorderVideo cost.
        var cm = ResolveCameraManager();
        if (cm != null)
            cm.frameReceived += OnCameraFrameForPrewarm;
    }

    void OnEnable()
    {
        if (capture != null)
            capture.CompletedFileWritingAction += OnFileWriteComplete;
    }

    void OnDisable()
    {
        if (capture != null)
            capture.CompletedFileWritingAction -= OnFileWriteComplete;
    }

    void ConfigureCaptureDefaults()
    {
        capture.OutputFolder = CaptureBase.OutputPath.RelativeToPersistentData;
        capture.OutputFolderPath = outputSubfolder;
        capture.FilenamePrefix = filenamePrefix;
        capture.AppendFilenameTimestamp = true;
        capture.FilenameExtension = "mp4";
        capture.FrameRate = captureFrameRate;
        // iOS only supports real-time capture (AVPro force-reverts non-realtime to realtime, and offline mode sets
        // Time.timeScale=0 which would freeze the live AR session), so we stay in real-time + manual update and feed
        // one frame per kept AR camera frame, throttled to ~FrameRate/sec in RecordFrame for even pacing.
        capture.IsRealTime = true;
        capture.IsManualUpdate = true;
    }

    ARCameraManager ResolveCameraManager()
    {
        if (cameraManager != null)
            return cameraManager;
        if (Globals.CameraManager != null)
            cameraManager = Globals.CameraManager;
        else
            cameraManager = FindFirstObjectByType<ARCameraManager>();
        return cameraManager;
    }

    /// <summary>
    /// AVPro's <see cref="CaptureFromTexture.UpdateFrame"/> starts coroutines on this GameObject;
    /// Unity requires an active host or capture silently fails (see device log spam).
    /// </summary>
    bool EnsureHostActive()
    {
        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        if (capture != null && !capture.enabled)
            capture.enabled = true;

        if (!gameObject.activeInHierarchy)
        {
            if (!warnedInactiveHost)
            {
                warnedInactiveHost = true;
                Debug.LogError("[VideoRecorder] VideoCapture GameObject is inactive in hierarchy — enable it or activate parent objects.");
            }
            return false;
        }

        warnedInactiveHost = false;
        return true;
    }

    /// <summary>
    /// Pay the one-time setup cost ahead of time so the first <see cref="StartRecording"/> after launch is instant
    /// instead of stalling ~15s. The expensive part is AVPro's first <see cref="CaptureBase.StartCapture"/>
    /// (native <c>CreateRecorderVideo</c> — hardware H.264/VideoToolbox encoder + AVAssetWriter init), so this runs
    /// a short throwaway capture once and deletes the resulting file. Safe to call repeatedly; runs at most once per
    /// resolution and does nothing while recording.
    /// </summary>
    public void Prewarm()
    {
        if (!enableEncoderPrewarm)
            return;

        if (IsRecording || isWarmingUp)
            return;

        if (ARSession.state != ARSessionState.SessionTracking)
            return;

        if (!EnsureHostActive())
            return;

        if (capture == null)
            capture = GetComponent<CaptureFromTexture>();

        var cm = ResolveCameraManager();
        if (cm == null)
            return;

        // If already prewarmed at the current AR resolution, nothing to do.
        if (prewarmed && TryGetRealCaptureDimensions(cm, out int curW, out int curH) &&
            curW == prewarmedCaptureWidth && curH == prewarmedCaptureHeight)
            return;

        if (prewarmCoroutine != null)
            StopCoroutine(prewarmCoroutine);
        prewarmCoroutine = StartCoroutine(PrewarmRoutine());
    }

    void OnCameraFrameForPrewarm(ARCameraFrameEventArgs args)
    {
        if (!enableEncoderPrewarm || prewarmed || isWarmingUp || deferredPrewarmCoroutine != null)
            return;

        // Prewarm competes with Immersal for the AR camera; wait until tracking is stable.
        if (ARSession.state != ARSessionState.SessionTracking)
            return;

        var cm = ResolveCameraManager();
        if (cm == null || !TryGetRealCaptureDimensions(cm, out _, out _))
            return;

        cm.frameReceived -= OnCameraFrameForPrewarm;
        deferredPrewarmCoroutine = StartCoroutine(DeferredPrewarmRoutine());
    }

    IEnumerator DeferredPrewarmRoutine()
    {
        if (prewarmDelayAfterFirstFrameSeconds > 0f)
            yield return new WaitForSecondsRealtime(prewarmDelayAfterFirstFrameSeconds);

        deferredPrewarmCoroutine = null;
        Prewarm();
    }

    void AbortPrewarmCapture()
    {
        if (prewarmCoroutine != null)
        {
            StopCoroutine(prewarmCoroutine);
            prewarmCoroutine = null;
        }
        if (deferredPrewarmCoroutine != null)
        {
            StopCoroutine(deferredPrewarmCoroutine);
            deferredPrewarmCoroutine = null;
        }
        isWarmingUp = false;

        if (capture != null && capture.IsCapturing())
        {
            try { capture.StopCapture(); }
            catch (Exception e)
            {
                Debug.LogWarning($"[VideoRecorder] AbortPrewarm StopCapture failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Runs a brief throwaway capture so the native encoder subsystem is created (and torn down) once, up front.
    /// Handles AVPro queuing <see cref="CaptureBase.StartCapture"/> (when its own Start() hasn't run yet) by
    /// detecting the actual capture via <see cref="CaptureBase.IsCapturing"/> rather than the return value.
    /// </summary>
    IEnumerator PrewarmRoutine()
    {
        isWarmingUp = true;
        Debug.Log("[VideoRecorder] Prewarm starting — waiting for AR camera resolution...");

        var cm = ResolveCameraManager();
        int width = DefaultWidth;
        int height = DefaultHeight;

        // Wait until ARKit exposes real intrinsics (SetupComponents runs before the session is live).
        const float dimensionWaitSeconds = 45f;
        float waitElapsed = 0f;
        while (waitElapsed < dimensionWaitSeconds)
        {
            cm = ResolveCameraManager();
            if (cm != null && TryGetRealCaptureDimensions(cm, out width, out height))
                break;

            waitElapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (cm == null || !TryGetRealCaptureDimensions(cm, out width, out height))
        {
            Debug.LogWarning("[VideoRecorder] Prewarm: AR camera resolution not ready; using defaults (first record may still stall).");
            GetCaptureDimensions(cm, out width, out height);
        }
        else
        {
            Debug.Log($"[VideoRecorder] Prewarm: AR camera ready at {width}x{height}");
        }

        EnsureCaptureBuffers(width, height);
        if (tex == null)
        {
            isWarmingUp = false;
            prewarmCoroutine = null;
            yield break;
        }

        capture.SetSourceTexture(tex);

        // AVPro selects codecs and sets up its end-of-frame wait in its own Start(); give it a couple frames so the
        // StartCapture below actually creates the encoder instead of just queuing.
        yield return null;
        yield return null;

        if (deferPrewarmUntilStill)
        {
            Debug.Log("[VideoRecorder] Prewarm: waiting for device to settle before encoder init...");
            yield return WaitForDeviceStillness(prewarmStillDurationSeconds, prewarmStillnessTimeoutSeconds);
        }

        try
        {
            capture.StartCapture();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VideoRecorder] Prewarm StartCapture failed: {e.Message}");
        }

        // StartCapture may have queued (returns false) and started on AVPro's next Update — wait for the real state.
        float elapsed = 0f;
        while (!capture.IsCapturing() && elapsed < 5f)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (capture.IsCapturing())
        {
            warmupFilePath = NormalizeLocalPath(capture.LastFilePath);
            Debug.Log($"[VideoRecorder] Prewarm: encoder started ({width}x{height}), stopping throwaway capture...");

            // A few frames so the encoder is fully created before we tear it down.
            for (int i = 0; i < 3; i++)
                yield return null;

            try
            {
                capture.StopCapture();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VideoRecorder] Prewarm StopCapture failed: {e.Message}");
            }

            // OnFileWriteComplete normally deletes the throwaway file; this is a fallback if that doesn't fire.
            yield return new WaitForSecondsRealtime(3f);
            TryDeleteWarmupFile();

            prewarmedCaptureWidth = width;
            prewarmedCaptureHeight = height;
            prewarmed = true;
            Debug.Log($"[VideoRecorder] Prewarm complete (encoder warmed at {width}x{height})");
        }
        else
        {
            Debug.LogWarning("[VideoRecorder] Prewarm could not start a warmup capture; first record may still stall.");
        }

        isWarmingUp = false;
        prewarmCoroutine = null;
    }

    bool IsDeviceCurrentlyStill()
    {
        if (!SystemInfo.supportsGyroscope || !Input.gyro.enabled)
            return true;

        return Input.gyro.rotationRate.magnitude <= prewarmStillnessThresholdRadPerSec;
    }

    IEnumerator WaitForDeviceStillness(float stillDuration, float timeout)
    {
        float stillElapsed = 0f;
        float totalElapsed = 0f;

        while (totalElapsed < timeout)
        {
            if (IsDeviceCurrentlyStill())
                stillElapsed += Time.unscaledDeltaTime;
            else
                stillElapsed = 0f;

            if (stillElapsed >= stillDuration)
                yield break;

            totalElapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        Debug.LogWarning("[VideoRecorder] Prewarm: stillness timeout — running encoder init (expect a brief hitch).");
    }

    void TryDeleteWarmupFile()
    {
        if (string.IsNullOrEmpty(warmupFilePath))
            return;

        string path = warmupFilePath;
        warmupFilePath = null;
        try
        {
            if (LocalFileExists(path))
            {
                File.Delete(NormalizeLocalPath(path));
                Debug.Log($"[VideoRecorder] Deleted prewarm throwaway file: {path}");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VideoRecorder] Failed to delete prewarm file: {e.Message}");
        }
    }

    /// <summary>
    /// (Re)allocate the capture texture and rotation buffer for the given landscape source size. Reuses existing
    /// allocations when the size and rotation are unchanged so repeated records don't churn memory.
    /// </summary>
    void EnsureCaptureBuffers(int width, int height)
    {
        bool swap = rotation == VideoRotation.CounterClockwise90 ||
                    rotation == VideoRotation.Clockwise90 ||
                    rotation == VideoRotation.PortraitUpright;
        int outW = swap ? height : width;
        int outH = swap ? width : height;

        bool dimsChanged = captureWidth != width || captureHeight != height;
        captureWidth = width;
        captureHeight = height;

        if (tex == null || dimsChanged || tex.width != outW || tex.height != outH)
        {
            if (tex != null)
                Destroy(tex);
            tex = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            if (image != null)
                image.texture = tex;
        }

        int needed = width * height * 4;
        if (rotation == VideoRotation.None)
        {
            if (srcBuffer.IsCreated)
                srcBuffer.Dispose();
        }
        else if (!srcBuffer.IsCreated || srcBuffer.Length != needed)
        {
            if (srcBuffer.IsCreated)
                srcBuffer.Dispose();
            srcBuffer = new NativeArray<byte>(needed, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        }
    }

    /// <summary>Start capturing AR camera frames into an mp4.</summary>
    public bool StartRecording()
    {
        if (IsRecording)
        {
            Debug.LogWarning("[VideoRecorder] Already recording");
            return false;
        }

        if (!EnsureHostActive())
            return false;

        // User tapped record — cancel a pending background prewarm and tear down any throwaway capture.
        if (isWarmingUp || deferredPrewarmCoroutine != null)
            AbortPrewarmCapture();

        if (ARSession.state != ARSessionState.SessionTracking)
        {
            Debug.LogWarning($"[VideoRecorder] AR session is {ARSession.state}; wait for tracking before recording video.");
            return false;
        }

        if (capture == null)
        {
            Debug.LogError("[VideoRecorder] No CaptureFromTexture assigned");
            return false;
        }

        var cm = ResolveCameraManager();
        if (cm == null)
        {
            Debug.LogError("[VideoRecorder] ARCameraManager not found (wire Globals or assign cameraManager)");
            return false;
        }

        if (!GetCaptureDimensions(cm, out int width, out int height))
        {
            Debug.LogError("[VideoRecorder] Invalid camera resolution");
            return false;
        }

        EnsureCaptureBuffers(width, height);
        capture.SetSourceTexture(tex);

        bool started;
        try
        {
            started = capture.StartCapture();
        }
        catch (Exception e)
        {
            Debug.LogError($"[VideoRecorder] StartCapture failed: {e.Message}");
            return false;
        }

        if (!started)
        {
            // Most likely the encoder is still busy finishing the prewarm capture; the cost is already paid, so a
            // second tap will succeed instantly.
            Debug.LogWarning("[VideoRecorder] StartCapture returned false (encoder busy / still warming up) — try again.");
            return false;
        }

        frameCount = 0;
        fileWriteCompleted = false;
        completedWritePath = null;
        captureStartRealtime = Time.realtimeSinceStartup;
        nextCaptureRealtime = captureStartRealtime; // capture the first frame immediately
        if (tmpFrames != null)
            tmpFrames.text = "0";

        cm.frameReceived += RecordFrame;
        IsRecording = true;
        LastFilePath = capture.LastFilePath;
        Debug.Log($"[VideoRecorder] Started capture {width}x{height} -> {outputSubfolder} (path after encode: {LastFilePath})");
        return true;
    }

    /// <summary>Stop capture. <paramref name="onComplete"/> receives the final mp4 path when AVPro finishes writing.</summary>
    public void StopRecording(Action<string> onComplete = null)
    {
        if (!IsRecording)
        {
            onComplete?.Invoke(LastFilePath);
            return;
        }

        if (!EnsureHostActive())
        {
            FinishStop(null);
            return;
        }

        var cm = ResolveCameraManager();
        if (cm != null)
            cm.frameReceived -= RecordFrame;

        IsRecording = false;
        stopCallback = onComplete;

        if (capture != null)
        {
            try
            {
                capture.StopCapture();
            }
            catch (Exception e)
            {
                Debug.LogError($"[VideoRecorder] StopCapture failed: {e.Message}");
                FinishStop(null);
                return;
            }
        }

        if (waitForFileCoroutine != null)
            StopCoroutine(waitForFileCoroutine);
        waitForFileCoroutine = StartCoroutine(WaitForFinalFilePath());
    }

    IEnumerator WaitForFinalFilePath()
    {
        const float timeout = 45f;
        float elapsed = 0f;

        // Wait for AVPro's CompletedFileWritingAction (fileWriteCompleted) — the file may already exist on disk
        // while it's still being flushed, so existence alone is not enough; reading it early yields a partial mp4.
        while (!fileWriteCompleted && elapsed < timeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        string path = completedWritePath;

        if (!fileWriteCompleted)
        {
            // Fallback: the completion event didn't fire in time; use the best-known path if it exists on disk.
            if (!string.IsNullOrEmpty(CaptureBase.LastFileSaved))
                path = CaptureBase.LastFileSaved;
            else if (capture != null && !string.IsNullOrEmpty(capture.LastFilePath))
                path = capture.LastFilePath;

            if (string.IsNullOrEmpty(path) || !LocalFileExists(path))
                Debug.LogWarning($"[VideoRecorder] Timed out waiting for file-write completion (path='{path}')");
            else
            {
                bool fileStable = false;
                yield return WaitForStableFileSize(path, stable => fileStable = stable);
                if (!fileStable)
                    path = null;
            }
        }

        FinishStop(path);
        waitForFileCoroutine = null;
    }

    IEnumerator WaitForStableFileSize(string path, Action<bool> onComplete)
    {
        const float checkInterval = 0.5f;
        const float requiredStableSeconds = 1f;
        const float stabilityTimeout = 6f;
        string localPath = NormalizeLocalPath(path);
        long lastSize = -1;
        float stableFor = 0f;
        float elapsed = 0f;

        while (elapsed < stabilityTimeout)
        {
            long size = TryGetFileSize(localPath);
            if (size > 0 && size == lastSize)
                stableFor += checkInterval;
            else
                stableFor = 0f;

            if (stableFor >= requiredStableSeconds)
            {
                onComplete?.Invoke(true);
                yield break;
            }

            lastSize = size;
            elapsed += checkInterval;
            yield return new WaitForSecondsRealtime(checkInterval);
        }

        // Avoid uploading a file that still appears to be changing after AVPro's completion event timed out.
        Debug.LogWarning($"[VideoRecorder] File did not stabilize after write timeout; skipping fallback path '{localPath}'");
        onComplete?.Invoke(false);
    }

    static long TryGetFileSize(string path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    void OnFileWriteComplete(FileWritingHandler handler)
    {
        if (handler == null) return;
        string path = handler.FinalPath;
        if (string.IsNullOrEmpty(path)) return;

        string local = NormalizeLocalPath(path);

        // The throwaway prewarm capture writes a file too; delete it rather than treating it as the last recording.
        if (!string.IsNullOrEmpty(warmupFilePath) &&
            string.Equals(local, warmupFilePath, StringComparison.OrdinalIgnoreCase))
        {
            warmupFilePath = null;
            try
            {
                if (File.Exists(local))
                    File.Delete(local);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VideoRecorder] Failed to delete prewarm file: {e.Message}");
            }
            return;
        }

        LastFilePath = local;
        completedWritePath = local;
        fileWriteCompleted = true;
        Debug.Log($"[VideoRecorder] File write complete: {LastFilePath}");
    }

    void FinishStop(string path)
    {
        if (!string.IsNullOrEmpty(path))
            LastFilePath = NormalizeLocalPath(path);
        else if (!string.IsNullOrEmpty(LastFilePath))
            LastFilePath = NormalizeLocalPath(LastFilePath);

        // Fire callbacks immediately so Move AI upload can start without waiting on Photos export.
        CompleteStop(null);

        if (!string.IsNullOrEmpty(LastFilePath) && exportToPhotoLibraryAfterCapture)
            StartCoroutine(ExportToPhotosThenComplete());
    }

    IEnumerator ExportToPhotosThenComplete()
    {
#if (UNITY_IOS || UNITY_STANDALONE_OSX) && !UNITY_EDITOR
        var level = CaptureBase.PhotoLibraryAccessLevel.AddOnly;
        if (CaptureBase.HasUserAuthorisationToAccessPhotos(level) == CaptureBase.PhotoLibraryAuthorisationStatus.NotDetermined)
            yield return CaptureBase.RequestUserAuthorisationToAccessPhotos(level);
#endif
        bool photosOk = false;
        bool done = false;
        VideoPhotosExport.SaveToPhotos(LastFilePath, ok =>
        {
            photosOk = ok;
            done = true;
        });

        const float timeout = 30f;
        float elapsed = 0f;
        while (!done && elapsed < timeout)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        OnPhotosExportFinished?.Invoke(LastFilePath, photosOk);
    }

    void CompleteStop(bool? photosExportSuccess)
    {
        var cb = stopCallback;
        stopCallback = null;
        cb?.Invoke(LastFilePath);
        OnRecordingFinished?.Invoke(LastFilePath);
        if (photosExportSuccess.HasValue)
            OnPhotosExportFinished?.Invoke(LastFilePath, photosExportSuccess.Value);
    }

    void RecordFrame(ARCameraFrameEventArgs args)
    {
        if (!IsRecording || tex == null)
            return;

        // Capturing while ARKit is interrupted produces partial frames that cannot align with body data.
        if (ARSession.state != ARSessionState.SessionTracking)
            return;

        if (!gameObject.activeInHierarchy)
        {
            if (!EnsureHostActive())
                return;
        }

        // Throttle to the target frame rate so the encoded clip is constant-frame-rate (the AR camera runs faster,
        // typically ~60fps). Even pacing is what Move AI's single-cam temporal solve expects.
        float now = Time.realtimeSinceStartup;
        if (now < nextCaptureRealtime)
            return;
        // Advance by whole intervals so we don't drift if a frame is late; never schedule in the past.
        nextCaptureRealtime += CaptureInterval;
        if (nextCaptureRealtime < now)
            nextCaptureRealtime = now + CaptureInterval;

        var cm = ResolveCameraManager();
        if (cm == null || !cm.TryAcquireLatestCpuImage(out cpuImage))
            return;

        try
        {
            // PortraitUpright bakes the full orientation into RotateInto (sensor→portrait remap on iPhone).
            var transform = rotation == VideoRotation.PortraitUpright
                ? XRCpuImage.Transformation.None
                : XRCpuImage.Transformation.MirrorY;
            var conversionParams = new XRCpuImage.ConversionParams(
                cpuImage, TextureFormat.RGBA32, transform)
            {
                outputDimensions = new Vector2Int(captureWidth, captureHeight)
            };

            if (rotation == VideoRotation.None)
            {
                var data = tex.GetRawTextureData<byte>();
                cpuImage.Convert(conversionParams, data);
            }
            else
            {
                cpuImage.Convert(conversionParams, srcBuffer);
                RotateInto(srcBuffer, tex.GetRawTextureData<byte>());
            }
            tex.Apply();

            if (capture != null)
            {
                capture.UpdateSourceTexture();
                capture.UpdateFrame();
            }

            frameCount++;
            if (tmpFrames != null)
                tmpFrames.text = frameCount.ToString();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VideoRecorder] RecordFrame failed: {e.Message}");
        }
        finally
        {
            cpuImage.Dispose();
        }
    }

    /// <summary>
    /// Rotate the landscape source buffer (<paramref name="src"/>, captureWidth x captureHeight, RGBA32) into the
    /// destination texture buffer (<paramref name="dst"/>) using the configured <see cref="rotation"/>. Works on
    /// 32-bit pixels (one int per RGBA pixel) for speed.
    /// </summary>
    void RotateInto(NativeArray<byte> src, NativeArray<byte> dst)
    {
        var s = src.Reinterpret<int>(1);
        var d = dst.Reinterpret<int>(1);
        int w = captureWidth;
        int h = captureHeight;

        switch (rotation)
        {
            case VideoRotation.CounterClockwise90:
            {
                int dstW = h; // output width
                for (int nr = 0; nr < w; nr++)        // output rows  [0, w)
                {
                    int srcCol = w - 1 - nr;
                    int rowBase = nr * dstW;
                    for (int nc = 0; nc < h; nc++)    // output cols  [0, h)
                        d[rowBase + nc] = s[nc * w + srcCol];
                }
                break;
            }
            case VideoRotation.Clockwise90:
            {
                int dstW = h;
                for (int nr = 0; nr < w; nr++)
                {
                    int rowBase = nr * dstW;
                    for (int nc = 0; nc < h; nc++)
                        d[rowBase + nc] = s[(h - 1 - nc) * w + nr];
                }
                break;
            }
            case VideoRotation.Rotate180:
            {
                int count = w * h;
                for (int i = 0; i < count; i++)
                    d[i] = s[count - 1 - i];
                break;
            }
            case VideoRotation.PortraitUpright:
            {
                // Landscape sensor → portrait upright (swapXY + flipX + flipY).
                int dstW = h;
                for (int nr = 0; nr < w; nr++)
                {
                    int rowBase = nr * dstW;
                    for (int nc = 0; nc < h; nc++)
                        d[rowBase + nc] = s[(h - 1 - nc) * w + (w - 1 - nr)];
                }
                break;
            }
            default:
                d.CopyFrom(s);
                break;
        }
    }

    /// <summary>True when ARKit has reported actual camera dimensions (not the 1280x720 fallback).</summary>
    static bool TryGetRealCaptureDimensions(ARCameraManager cm, out int width, out int height)
    {
        width = height = 0;
        if (cm == null)
            return false;

        if (cm.TryGetIntrinsics(out XRCameraIntrinsics intrinsics) &&
            intrinsics.resolution.x > 0 && intrinsics.resolution.y > 0)
        {
            width = intrinsics.resolution.x;
            height = intrinsics.resolution.y;
            return true;
        }

        var config = cm.currentConfiguration;
        if (config.HasValue && config.Value.width > 0 && config.Value.height > 0)
        {
            width = config.Value.width;
            height = config.Value.height;
            return true;
        }

        return false;
    }

    static bool GetCaptureDimensions(ARCameraManager cm, out int width, out int height)
    {
        if (TryGetRealCaptureDimensions(cm, out width, out height))
            return true;

        width = DefaultWidth;
        height = DefaultHeight;
        return width > 0 && height > 0;
    }

    void OnDestroy()
    {
        var cm = ResolveCameraManager();
        if (cm != null)
            cm.frameReceived -= OnCameraFrameForPrewarm;

        if (isWarmingUp && capture != null && capture.IsCapturing())
        {
            try { capture.StopCapture(); } catch { /* tearing down */ }
            isWarmingUp = false;
            TryDeleteWarmupFile();
        }

        if (deferredPrewarmCoroutine != null)
        {
            StopCoroutine(deferredPrewarmCoroutine);
            deferredPrewarmCoroutine = null;
        }

        if (IsRecording)
        {
            cm = ResolveCameraManager();
            if (cm != null)
                cm.frameReceived -= RecordFrame;
            IsRecording = false;
        }
        if (tex != null)
            Destroy(tex);
        if (srcBuffer.IsCreated)
            srcBuffer.Dispose();
    }
}
