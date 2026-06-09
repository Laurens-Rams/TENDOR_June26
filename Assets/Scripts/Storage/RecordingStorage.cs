using UnityEngine;
using BodyTracking.Data;
using System.IO;
using System.Collections.Generic;
using System;
using BodyTracking.Diagnostics;

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

        struct CachedRecording
        {
            public long Length;
            public long WriteTicks;
            public HipRecording Recording;
        }

        struct CachedMetadata
        {
            public long Length;
            public long WriteTicks;
            public RecordingMetadata Metadata;
        }

        private static readonly Dictionary<string, CachedRecording> recordingCache = new Dictionary<string, CachedRecording>();
        private static readonly Dictionary<string, CachedMetadata> metadataCache = new Dictionary<string, CachedMetadata>();
        
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
            using var _ = PerfSampler.Scope("Storage.LoadRecording");
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
            using var _ = PerfSampler.Scope("Storage.ListRecordings");
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
                    Invalidate(filePath);
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
            using var _ = PerfSampler.Scope("Storage.GetMetadata");
            try
            {
                string filePath = GetFilePath(fileName, format);
                
                if (!File.Exists(filePath))
                    return null;
                
                var fileInfo = new FileInfo(filePath);
                
                // For JSON, we can quickly parse just the metadata
                if (format == StorageFormat.JSON)
                {
                    if (TryGetCachedMetadata(filePath, fileName, format, out var cached))
                        return cached;

                    return LoadMetadataFast(filePath, fileName, fileInfo, format);
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
        /// Read Move job fields without deserializing the full frame array. Used only for legacy queue migration.
        /// </summary>
        public static bool TryGetMoveJobInfo(string fileName, out string jobId, out string jobState,
            StorageFormat format = StorageFormat.JSON)
        {
            using var _ = PerfSampler.Scope("Storage.ScanMoveJobInfo");
            jobId = null;
            jobState = null;
            if (string.IsNullOrEmpty(fileName) || format != StorageFormat.JSON)
                return false;

            try
            {
                string filePath = GetFilePath(fileName, format);
                if (!File.Exists(filePath))
                    return false;

                string json = File.ReadAllText(filePath);
                jobId = ExtractString(json, "moveJobId");
                jobState = ExtractString(json, "moveJobState");
                return !string.IsNullOrEmpty(jobId);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"[RecordingStorage] Error scanning Move job info: {e.Message}");
                return false;
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
            Invalidate(filePath);
            
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
            if (TryGetCachedRecording(filePath, out var cachedRecording))
            {
                if (logResult)
                    UnityEngine.Debug.Log($"[RecordingStorage] Cache hit {Path.GetFileName(filePath)}");
                return cachedRecording;
            }

            using var _ = PerfSampler.Scope("Storage.ParseRecordingJson");
            string json = File.ReadAllText(filePath);
            var recording = JsonUtility.FromJson<HipRecording>(json);
            recording?.NormalizeFormatAfterLoad();
            if (recording != null)
                StoreRecording(filePath, recording);

            if (logResult && recording != null)
            {
                UnityEngine.Debug.Log(
                    $"[RecordingStorage] Loaded {Path.GetFileName(filePath)}: {recording.ValidFrameCount}/{recording.FrameCount} valid, {recording.duration:F2}s");
            }

            return recording;
        }

        static RecordingMetadata LoadMetadataFast(string filePath, string fileName, FileInfo fileInfo, StorageFormat format)
        {
            using var _ = PerfSampler.Scope("Storage.ScanMetadataJson");
            string json = File.ReadAllText(filePath);
            int frameCount = CountOccurrences(json, "\"timestamp\"");
            var metadata = new RecordingMetadata
            {
                fileName = fileName,
                duration = ExtractFloat(json, "duration"),
                frameCount = frameCount,
                // Exact validity requires full frame deserialization; for list/filter purposes, a frame count
                // above zero is enough to avoid parsing every saved joint array while opening the UI.
                validFrameCount = frameCount,
                frameRate = ExtractFloat(json, "frameRate"),
                recordingTimestamp = DateTime.MinValue,
                fileSizeBytes = fileInfo.Length,
                format = format,
                mapId = ExtractString(json, "mapId") ?? "",
                spatialSource = ExtractString(json, "spatialSource") ?? ""
            };
            StoreMetadata(filePath, metadata);
            return metadata;
        }

        static int CountOccurrences(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
                return 0;
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }

        static float ExtractFloat(string json, string key)
        {
            string token = "\"" + key + "\"";
            int keyIndex = json.IndexOf(token, StringComparison.Ordinal);
            if (keyIndex < 0) return 0f;
            int colon = json.IndexOf(':', keyIndex + token.Length);
            if (colon < 0) return 0f;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            int end = start;
            while (end < json.Length && ("0123456789+-.eE").IndexOf(json[end]) >= 0) end++;
            if (end <= start) return 0f;
            return float.TryParse(json.Substring(start, end - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value)
                ? value
                : 0f;
        }

        static string ExtractString(string json, string key)
        {
            string token = "\"" + key + "\"";
            int keyIndex = json.IndexOf(token, StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colon = json.IndexOf(':', keyIndex + token.Length);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '"') return null;
            start++;
            var chars = new List<char>();
            bool escaped = false;
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (escaped)
                {
                    chars.Add(c);
                    escaped = false;
                    continue;
                }
                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (c == '"')
                    break;
                chars.Add(c);
            }
            return chars.Count > 0 ? new string(chars.ToArray()) : "";
        }

        static bool TryGetCachedRecording(string filePath, out HipRecording recording)
        {
            recording = null;
            if (!recordingCache.TryGetValue(filePath, out var cached))
                return false;
            if (!CacheStillValid(filePath, cached.Length, cached.WriteTicks))
            {
                recordingCache.Remove(filePath);
                metadataCache.Remove(filePath);
                return false;
            }
            recording = cached.Recording;
            return recording != null;
        }

        static bool TryGetCachedMetadata(string filePath, string fileName, StorageFormat format, out RecordingMetadata metadata)
        {
            metadata = null;
            if (!metadataCache.TryGetValue(filePath, out var cached))
                return false;
            if (!CacheStillValid(filePath, cached.Length, cached.WriteTicks))
            {
                recordingCache.Remove(filePath);
                metadataCache.Remove(filePath);
                return false;
            }
            metadata = cached.Metadata;
            return metadata != null && metadata.fileName == fileName && metadata.format == format;
        }

        static void StoreRecording(string filePath, HipRecording recording)
        {
            var info = new FileInfo(filePath);
            recordingCache[filePath] = new CachedRecording
            {
                Length = info.Length,
                WriteTicks = info.LastWriteTimeUtc.Ticks,
                Recording = recording
            };
        }

        static void StoreMetadata(string filePath, RecordingMetadata metadata)
        {
            var info = new FileInfo(filePath);
            metadataCache[filePath] = new CachedMetadata
            {
                Length = info.Length,
                WriteTicks = info.LastWriteTimeUtc.Ticks,
                Metadata = metadata
            };
        }

        static bool CacheStillValid(string filePath, long length, long writeTicks)
        {
            if (!File.Exists(filePath))
                return false;
            var info = new FileInfo(filePath);
            return info.Length == length && info.LastWriteTimeUtc.Ticks == writeTicks;
        }

        static void Invalidate(string filePath)
        {
            recordingCache.Remove(filePath);
            metadataCache.Remove(filePath);
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