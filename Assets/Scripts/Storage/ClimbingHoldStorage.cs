using System.IO;
using System.Text;
using UnityEngine;
using BodyTracking.Playback.PostProcess;

namespace BodyTracking.Storage
{
    /// <summary>
    /// Persists an inferred <see cref="ClimbingHoldMap"/> to disk, one file per spatial map (Immersal map id /
    /// image-target name). Holds accumulate across climbs on the same wall: the map is loaded when localized and
    /// saved back after playback. Files live under <c>persistentDataPath/ClimbingHolds/{mapId}.json</c>.
    /// </summary>
    public static class ClimbingHoldStorage
    {
        private const string HOLDS_FOLDER = "ClimbingHolds";
        private const string FILE_EXTENSION = ".json";

        private static string HoldsPath => Path.Combine(Application.persistentDataPath, HOLDS_FOLDER);

        private static string SanitizeMapId(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return "default";
            var sb = new StringBuilder(mapId.Length);
            foreach (char c in mapId)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static string GetFilePath(string mapId)
            => Path.Combine(HoldsPath, SanitizeMapId(mapId) + FILE_EXTENSION);

        /// <summary>Write the hold map for <paramref name="mapId"/> to disk. Returns false on error.</summary>
        public static bool Save(string mapId, ClimbingHoldMap map)
        {
            if (map == null) return false;
            try
            {
                Directory.CreateDirectory(HoldsPath);
                File.WriteAllText(GetFilePath(mapId), map.ToJson());
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ClimbingHoldStorage] Failed to save holds for map '{mapId}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load the hold map for <paramref name="mapId"/>. Always returns a non-null map via <paramref name="map"/>
        /// (empty when no file exists). The bool indicates whether a stored file was actually loaded.
        /// </summary>
        public static bool TryLoad(string mapId, out ClimbingHoldMap map)
        {
            map = new ClimbingHoldMap();
            try
            {
                string path = GetFilePath(mapId);
                if (!File.Exists(path)) return false;
                return map.LoadFromJson(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ClimbingHoldStorage] Failed to load holds for map '{mapId}': {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Load the persisted holds for <paramref name="mapId"/> into an existing <see cref="ClimbingHoldMap"/>
        /// (cleared first). Returns whether a stored file was loaded.
        /// </summary>
        public static bool LoadInto(string mapId, ClimbingHoldMap map)
        {
            if (map == null) return false;
            map.Clear();
            try
            {
                string path = GetFilePath(mapId);
                if (!File.Exists(path)) return false;
                return map.LoadFromJson(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ClimbingHoldStorage] Failed to load holds for map '{mapId}': {e.Message}");
                return false;
            }
        }

        /// <summary>Delete the persisted holds file for a map (used by "Clear holds").</summary>
        public static void Delete(string mapId)
        {
            try
            {
                string path = GetFilePath(mapId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ClimbingHoldStorage] Failed to delete holds for map '{mapId}': {e.Message}");
            }
        }
    }
}
