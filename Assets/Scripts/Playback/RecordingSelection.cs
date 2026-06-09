using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using BodyTracking.Storage;
using BodyTracking.MoveAI;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Single source of truth for which recordings (of the active map) are currently selected for playback.
    ///
    /// This is the future-proof structure behind the Recordings menu and the playback "cycle recordings"
    /// button: both mutate this model, and the playback engine (<see cref="MultiRecordingPlayback"/>) plus
    /// <see cref="BodyTrackingController"/> react to <see cref="OnChanged"/>. The model itself is UI- and
    /// engine-agnostic — it only knows the file list, short labels, and the per-recording enabled flags.
    ///
    /// One recording enabled = single playback (legacy path). Two or more enabled = overlapped playback
    /// (the first/primary drives the timeline; the rest follow as overlay characters).
    /// </summary>
    public class RecordingSelection : MonoBehaviour
    {
        /// <summary>One selectable recording for the active map.</summary>
        public class Entry
        {
            public string fileName;
            public DateTime timestamp;
            public float duration;
            public bool hasFusion;
            public bool enabled;
            /// <summary>Chosen GLB character index for this recording (-1 = use the switcher's current/default).</summary>
            public int characterIndex = -1;

            /// <summary>Short, list-friendly label (date/time only, never the raw file name).</summary>
            public string ShortLabel => FormatShortLabel(this);
        }

        private static RecordingSelection s_instance;

        /// <summary>Process-wide instance, created on demand so UI/engine can always reach the model.</summary>
        public static RecordingSelection Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = FindFirstObjectByType<RecordingSelection>();
                    if (s_instance == null)
                    {
                        var go = new GameObject("RecordingSelection");
                        s_instance = go.AddComponent<RecordingSelection>();
                    }
                }
                return s_instance;
            }
        }

        private readonly List<Entry> entries = new List<Entry>();
        private string mapId = "";
        private int cycleIndex = -1;

        /// <summary>Fired whenever the entry list or any enabled flag changes.</summary>
        public event Action OnChanged;

        /// <summary>Read-only snapshot of all entries for the active map (most recent first).</summary>
        public IReadOnlyList<Entry> Entries => entries;
        public string MapId => mapId;
        public int EnabledCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].enabled) n++;
                return n;
            }
        }

        private void Awake()
        {
            if (s_instance == null)
                s_instance = this;
        }

        /// <summary>The primary (timeline-driving) recording = first enabled, or null when none enabled.</summary>
        public string PrimaryFileName
        {
            get
            {
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].enabled)
                        return entries[i].fileName;
                return null;
            }
        }

        /// <summary>Enabled recordings minus the primary (the ones that play overlapped on top).</summary>
        public List<string> OverlayFileNames
        {
            get
            {
                var result = new List<string>();
                bool primarySkipped = false;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!entries[i].enabled) continue;
                    if (!primarySkipped) { primarySkipped = true; continue; }
                    result.Add(entries[i].fileName);
                }
                return result;
            }
        }

        /// <summary>All currently enabled recordings (primary first).</summary>
        public List<string> EnabledFileNames
        {
            get
            {
                var result = new List<string>();
                for (int i = 0; i < entries.Count; i++)
                    if (entries[i].enabled)
                        result.Add(entries[i].fileName);
                return result;
            }
        }

        /// <summary>
        /// Rebuild the entry list for a map id from storage, preserving any existing enabled flags by file
        /// name. Pass null/empty to list every recording. Fires <see cref="OnChanged"/>.
        /// </summary>
        public void Refresh(string newMapId)
        {
            mapId = newMapId ?? "";

            // Remember which files were enabled + their chosen character so a refresh (e.g. after a map switch
            // back) keeps both the toggles and the per-recording character choice.
            var previouslyEnabled = new HashSet<string>();
            var previousCharacter = new Dictionary<string, int>();
            foreach (var e in entries)
            {
                if (e.enabled)
                    previouslyEnabled.Add(e.fileName);
                if (e.characterIndex >= 0)
                    previousCharacter[e.fileName] = e.characterIndex;
            }

            entries.Clear();

            var files = RecordingStorage.GetAvailableRecordings(mapId: string.IsNullOrEmpty(mapId) ? null : mapId);
            if (files != null)
            {
                foreach (var fileName in files)
                {
                    var meta = RecordingStorage.GetRecordingMetadata(fileName);
                    var entry = new Entry
                    {
                        fileName = fileName,
                        timestamp = ResolveTimestamp(meta, fileName),
                        duration = meta != null ? meta.duration : 0f,
                        hasFusion = MoveAIFusionCoordinator.HasFusionAsset(fileName),
                        enabled = previouslyEnabled.Contains(fileName),
                        characterIndex = previousCharacter.TryGetValue(fileName, out int ci) ? ci : -1
                    };
                    entries.Add(entry);
                }
            }

            // Most recent first.
            entries.Sort((a, b) => b.timestamp.CompareTo(a.timestamp));

            cycleIndex = -1;
            OnChanged?.Invoke();
        }

        /// <summary>Toggle one recording on/off by file name.</summary>
        public void SetEnabled(string fileName, bool enabled)
        {
            var entry = Find(fileName);
            if (entry == null || entry.enabled == enabled)
                return;
            entry.enabled = enabled;
            OnChanged?.Invoke();
        }

        public bool IsEnabled(string fileName)
        {
            var entry = Find(fileName);
            return entry != null && entry.enabled;
        }

        /// <summary>Chosen GLB character index for a recording (-1 = switcher default).</summary>
        public int GetCharacterIndex(string fileName)
        {
            var entry = Find(fileName);
            return entry != null ? entry.characterIndex : -1;
        }

        /// <summary>1-based character number to display in the list (defaults to 1 when unset).</summary>
        public int GetCharacterNumber(string fileName)
        {
            int idx = GetCharacterIndex(fileName);
            return (idx < 0 ? 0 : idx) + 1;
        }

        /// <summary>Set the chosen GLB character index for a recording.</summary>
        public void SetCharacterIndex(string fileName, int index)
        {
            var entry = Find(fileName);
            if (entry == null || entry.characterIndex == index)
                return;
            entry.characterIndex = index;
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Advance a recording's chosen GLB character to the next one (wrapping over <paramref name="characterCount"/>).
        /// Returns the new index, or -1 when there are no characters to cycle.
        /// </summary>
        public int CycleCharacter(string fileName, int characterCount)
        {
            var entry = Find(fileName);
            if (entry == null || characterCount <= 0)
                return -1;
            int current = entry.characterIndex < 0 ? 0 : entry.characterIndex;
            entry.characterIndex = (current + 1) % characterCount;
            OnChanged?.Invoke();
            return entry.characterIndex;
        }

        /// <summary>Disable everything, then enable only the given recording (used by the cycle button).</summary>
        public void EnableOnly(string fileName)
        {
            bool changed = false;
            foreach (var e in entries)
            {
                bool target = e.fileName == fileName;
                if (e.enabled != target) { e.enabled = target; changed = true; }
            }
            if (changed)
                OnChanged?.Invoke();
        }

        /// <summary>
        /// Cycle: turn every recording off and enable the next single one in list order. Returns the new
        /// primary file name (or null when there are no recordings). Starts from the currently enabled one.
        /// </summary>
        public string CycleSingle()
        {
            if (entries.Count == 0)
                return null;

            // Anchor the cycle on whatever single recording is currently enabled, if any.
            string current = PrimaryFileName;
            if (!string.IsNullOrEmpty(current))
                cycleIndex = entries.FindIndex(e => e.fileName == current);

            cycleIndex = (cycleIndex + 1) % entries.Count;
            string next = entries[cycleIndex].fileName;
            EnableOnly(next);
            return next;
        }

        private Entry Find(string fileName)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].fileName == fileName)
                    return entries[i];
            return null;
        }

        private static DateTime ResolveTimestamp(RecordingMetadata meta, string fileName)
        {
            if (meta != null && meta.recordingTimestamp > DateTime.MinValue)
                return meta.recordingTimestamp;

            // Fall back to the timestamp baked into the file name: hip_recording_yyyyMMdd_HHmmss.
            if (!string.IsNullOrEmpty(fileName))
            {
                int idx = fileName.LastIndexOf("recording_", StringComparison.OrdinalIgnoreCase);
                string stamp = idx >= 0 ? fileName.Substring(idx + "recording_".Length) : fileName;
                if (DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed))
                    return parsed;
            }
            return DateTime.MinValue;
        }

        private static string FormatShortLabel(Entry entry)
        {
            if (entry == null)
                return "—";
            if (entry.timestamp > DateTime.MinValue)
                return entry.timestamp.ToString("MMM d · HH:mm", CultureInfo.InvariantCulture);

            // No parseable date — show a trimmed tail of the file name as a last resort, never the full path.
            string name = entry.fileName ?? "";
            return name.Length > 12 ? name.Substring(name.Length - 12) : name;
        }
    }
}
