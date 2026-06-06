using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Copies a finished mp4 into the iOS Photos library (device builds only).
/// </summary>
public static class VideoPhotosExport
{
#if UNITY_IOS && !UNITY_EDITOR
    delegate void SaveVideoCallback(int success);

    [DllImport("__Internal")]
    static extern void TendorSaveVideoToPhotos(string path, SaveVideoCallback callback);

    static Action<bool> pendingCallback;
    static string pendingFileName;

    [AOT.MonoPInvokeCallback(typeof(SaveVideoCallback))]
    static void OnNativeComplete(int success)
    {
        bool ok = success != 0;
        if (ok)
            Debug.Log($"[VideoPhotosExport] Saved to Photos: {pendingFileName}");
        else
            Debug.LogWarning("[VideoPhotosExport] Could not save video to Photos (permission denied or export failed).");

        var cb = pendingCallback;
        pendingCallback = null;
        pendingFileName = null;
        cb?.Invoke(ok);
    }

    public static void SaveToPhotos(string filePath, Action<bool> onComplete)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            onComplete?.Invoke(false);
            return;
        }

        pendingCallback = onComplete;
        filePath = VideoRecorder.NormalizeLocalPath(filePath);
        pendingFileName = System.IO.Path.GetFileName(filePath);
        TendorSaveVideoToPhotos(filePath, OnNativeComplete);
    }
#else
    public static void SaveToPhotos(string filePath, Action<bool> onComplete)
    {
        Debug.Log("[VideoPhotosExport] Photos export is iOS device only.");
        onComplete?.Invoke(false);
    }
#endif
}
