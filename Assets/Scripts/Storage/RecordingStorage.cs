using UnityEngine;
using BodyTracking.Data;
using System.IO;
using System.Collections.Generic;
using System;

namespace BodyTracking.Storage
{
    /// <summary>
    /// Handles persistent storage and retrieval of hip tracking recordings
    /// </summary>
    public static class RecordingStorage
    {
        private const string RECORDINGS_FOLDER = "BodyTrackingRecordings";
        private const string FILE_EXTENSION = ".json";
        private const string BINARY_EXTENSION = ".dat";
        
        private static string RecordingsPath => Path.Combine(Application.persistentDataPath, RECORDINGS_FOLDER);
        
        /// <summary>
        /// Storage format options
        /// </summary>
        public enum StorageFormat
        {
            JSON,    // Human readable, larger size
            Binary   // Compact, faster loading
        }

        /// <summary>
        /// Save a hip recording to persistent storage
        /// </summary>
        public static bool SaveRecording(HipRecording recording, string fileName, StorageFormat format = StorageFormat.JSON)
        {
            if (recording == null || !recording.IsValid)
            {
               UnityEngine.Debug.LogError("[RecordingStorage] Invalid hip recording provided");
                return false;
            }
            
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = $"hip_recording_{DateTime.Now:yyyyMMdd_HHmmss}";
            }
            
            // Ensure recordings directory exists
            Directory.CreateDirectory(RecordingsPath);
            
            try
            {
                string filePath = GetFilePath(fileName, format);
                
                switch (format)
                {
                    case StorageFormat.JSON:
                        return SaveAsJSON(recording, filePath);
                    case StorageFormat.Binary:
                        return SaveAsBinary(recording, filePath);
                    default:
                       UnityEngine.Debug.LogError($"[RecordingStorage] Unsupported format: {format}");
                        return false;
                }
            }
            catch (Exception e)
            {
               UnityEngine.Debug.LogError($"[RecordingStorage] Error saving hip recording: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load a hip recording from persistent storage
        /// </summary>
        public static HipRecording LoadRecording(string fileName, StorageFormat format = StorageFormat.JSON)
        {
            if (string.IsNullOrEmpty(fileName))
            {
               UnityEngine.Debug.LogError("[RecordingStorage] File name is required");
                return null;
            }
            
            try
            {
                string filePath = GetFilePath(fileName, format);
                
                if (!File.Exists(filePath))
                {
                   UnityEngine.Debug.LogWarning($"[RecordingStorage] File not found: {filePath}");
                    return null;
                }
                
                switch (format)
                {
                    case StorageFormat.JSON:
                        return LoadFromJSON(filePath, logResult: true);
                    case StorageFormat.Binary:
                        return LoadFromBinary(filePath);
                    default:
                       UnityEngine.Debug.LogError($"[RecordingStorage] Unsupported format: {format}");
                        return null;
                }
            }
            catch (Exception e)
            {
               UnityEngine.Debug.LogError($"[RecordingStorage] Error loading hip recording: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get list of available recordings. When <paramref name="mapId"/> is set, only recordings
        /// captured on that Immersal map (matching <see cref="HipRecording.mapId"/>) are returned.
        /// </summary>
        public static List<string> GetAvailableRecordings(StorageFormat format = StorageFormat.JSON, string mapId = null)
        {
            var recordings = new List<string>();
            
            if (!Directory.Exists(RecordingsPath))
                return recordings;
            
            string extension = format == StorageFormat.JSON ? FILE_EXTENSION : BINARY_EXTENSION;
            var files = Directory.GetFiles(RecordingsPath, $"*{extension}");
            
            foreach (var file in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (format == StorageFormat.JSON)
                {
                    var metadata = GetRecordingMetadata(fileName, format);
                    if (metadata == null || metadata.validFrameCount <= 0 || metadata.frameCount <= 0 || metadata.duration <= 0f)
                        continue;
                    if (!string.IsNullOrEmpty(mapId) && !MapIdMatches(metadata.mapId, mapId))
                        continue;
                        recordings.Add(fileName);
                }
                else
                {
                    if (!string.IsNullOrEmpty(mapId))
                        continue;
                    recordings.Add(fileName);
                }
            }
            
            return recordings;
        }

        /// <summary>Most recent valid recording for the given map id, or null if none.</summary>
        public static string GetLatestRecordingForMap(string mapId, StorageFormat format = StorageFormat.JSON)
        {
            if (string.IsNullOrEmpty(mapId))
                return null;

            var recordings = GetAvailableRecordings(format, mapId);
            if (recordings.Count == 0)
                return null;

            string bestFile = null;
            DateTime bestTimestamp = DateTime.MinValue;

            foreach (var fileName in recordings)
            {
                var metadata = GetRecordingMetadata(fileName, format);
                if (metadata != null && metadata.recordingTimestamp > bestTimestamp)
                {
                    bestTimestamp = metadata.recordingTimestamp;
                    bestFile = fileName;
                }
            }

            return bestFile ?? recordings[recordings.Count - 1];
        }

        private static bool MapIdMatches(string recordingMapId, string filterMapId)
        {
            return string.Equals(recordingMapId?.Trim(), filterMapId?.Trim(), StringComparison.Ordinal);
        }

        /// <summary>
        /// Delete a recording
        /// </summary>
        public static bool DeleteRecording(string fileName, StorageFormat format = StorageFormat.JSON)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            
            try
            {
                string filePath = GetFilePath(fileName, format);
                
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                   UnityEngine.Debug.Log($"[RecordingStorage] Deleted hip recording: {fileName}");
                    return true;
                }
                
                return false;
            }
            catch (Exception e)
            {
               UnityEngine.Debug.LogError($"[RecordingStorage] Error deleting hip recording: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get recording metadata without loading full data
        /// </summary>
        public static RecordingMetadata GetRecordingMetadata(string fileName, StorageFormat format = StorageFormat.JSON)
        {
            try
            {
                string filePath = GetFilePath(fileName, format);
                
                if (!File.Exists(filePath))
                    return null;
                
                var fileInfo = new FileInfo(filePath);
                
                // For JSON, we can quickly parse just the metadata
                if (format == StorageFormat.JSON)
                {
                    string json = File.ReadAllText(filePath);
                    var recording = JsonUtility.FromJson<HipRecording>(json);
                    recording?.NormalizeFormatAfterLoad();
                    
                    return new RecordingMetadata
                    {
                        fileName = fileName,
                        duration = recording.duration,
                        frameCount = recording.FrameCount,
                        validFrameCount = recording.ValidFrameCount,
                        frameRate = recording.frameRate,
                        recordingTimestamp = recording.recordingTimestamp,
                        fileSizeBytes = fileInfo.Length,
                        format = format,
                        mapId = recording.mapId ?? "",
                        spatialSource = recording.spatialSource ?? ""
                    };
                }
                
                // For binary, return basic file info
                return new RecordingMetadata
                {
                    fileName = fileName,
                    fileSizeBytes = fileInfo.Length,
                    format = format
                };
            }
            catch (Exception e)
            {
               UnityEngine.Debug.LogError($"[RecordingStorage] Error getting metadata: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get total storage used by recordings
        /// </summary>
        public static long GetTotalStorageUsed()
        {
            if (!Directory.Exists(RecordingsPath))
                return 0;
            
            long totalSize = 0;
            var files = Directory.GetFiles(RecordingsPath);
            
            foreach (var file in files)
            {
                totalSize += new FileInfo(file).Length;
            }
            
            return totalSize;
        }

        #region Private Methods

        private static string GetFilePath(string fileName, StorageFormat format)
        {
            string extension = format == StorageFormat.JSON ? FILE_EXTENSION : BINARY_EXTENSION;
            return Path.Combine(RecordingsPath, fileName + extension);
        }

        private static bool SaveAsJSON(HipRecording recording, string filePath)
        {
            string json = JsonUtility.ToJson(recording, true);
            File.WriteAllText(filePath, json);
            
           UnityEngine.Debug.Log($"[RecordingStorage] Saved {Path.GetFileName(filePath)} ({recording.ValidFrameCount}/{recording.FrameCount} valid frames)");
            return true;
        }

        private static bool SaveAsBinary(HipRecording recording, string filePath)
        {
            // For binary format, we'd implement custom serialization
            // For now, falling back to JSON for simplicity
           UnityEngine.Debug.LogWarning("[RecordingStorage] Binary format not yet implemented, using JSON");
            return SaveAsJSON(recording, filePath.Replace(BINARY_EXTENSION, FILE_EXTENSION));
        }

        private static HipRecording LoadFromJSON(string filePath, bool logResult = false)
        {
            string json = File.ReadAllText(filePath);
            var recording = JsonUtility.FromJson<HipRecording>(json);
            recording?.NormalizeFormatAfterLoad();

            if (logResult && recording != null)
            {
                UnityEngine.Debug.Log(
                    $"[RecordingStorage] Loaded {Path.GetFileName(filePath)}: {recording.ValidFrameCount}/{recording.FrameCount} valid, {recording.duration:F2}s");
            }

            return recording;
        }

        private static HipRecording LoadFromBinary(string filePath)
        {
            // For binary format, we'd implement custom deserialization
            // For now, falling back to JSON for simplicity
           UnityEngine.Debug.LogWarning("[RecordingStorage] Binary format not yet implemented, trying JSON");
            return LoadFromJSON(filePath.Replace(BINARY_EXTENSION, FILE_EXTENSION));
        }

        #endregion
    }

    /// <summary>
    /// Metadata about a recording file
    /// </summary>
    [Serializable]
    public class RecordingMetadata
    {
        public string fileName;
        public float duration;
        public int frameCount;
        public int validFrameCount;
        public float frameRate;
        public DateTime recordingTimestamp;
        public long fileSizeBytes;
        public RecordingStorage.StorageFormat format;
        public string mapId;
        public string spatialSource;
        
        public string FormattedFileSize
        {
            get
            {
                string[] sizes = { "B", "KB", "MB", "GB" };
                double len = fileSizeBytes;
                int order = 0;
                while (len >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    len = len / 1024;
                }
                return $"{len:0.##} {sizes[order]}";
            }
        }
        
        public string FormattedDuration => TimeSpan.FromSeconds(duration).ToString(@"mm\:ss");
    }
} 