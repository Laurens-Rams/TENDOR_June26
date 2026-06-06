using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Parses the Move API MOTION_DATA payload (a ZIP of per-actor JSON files) into <see cref="MoveMotion"/>.
    ///
    /// IMPORTANT: The exact MOTION_DATA JSON schema is not fully documented publicly. This parser targets a
    /// general layout and is intentionally tolerant of key-name variants, but the field names, rotation order,
    /// and axis/handedness convention MUST be verified against a real export and adjusted in
    /// <see cref="ParseActor"/> / <see cref="ConvertRotation"/>. The rest of the pipeline (baker, player)
    /// depends only on <see cref="MoveMotion"/>, so only this file changes once the schema is confirmed.
    /// </summary>
    public static class MoveMotionParser
    {
        /// <summary>Unzip a MOTION_DATA archive and parse the first actor's JSON into a MoveMotion.</summary>
        public static MoveMotion ParseMotionDataZip(byte[] zipBytes, MoveJointMap map = null)
        {
            if (zipBytes == null || zipBytes.Length == 0)
            {
                Debug.LogError("[MoveMotionParser] Empty MOTION_DATA archive");
                return null;
            }

            try
            {
                using var ms = new MemoryStream(zipBytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

                string dumpDir = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Debug dumps are large, so keep schema inspection out of production device builds.
                try
                {
                    dumpDir = Path.Combine(Application.persistentDataPath, "MoveAIDebug",
                        DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(dumpDir);
                    // Keep the raw archive too, so it can be replayed offline through the Editor parser tool
                    // (Tools/Move AI/Parse MOTION_DATA Zip…) without re-recording or re-submitting to Move.
                    File.WriteAllBytes(Path.Combine(dumpDir, "motion_data.zip"), zipBytes);
                }
                catch { dumpDir = null; }

                var entrySummary = new StringBuilder();
#endif
                bool looksLikeXcappdata = false;
                foreach (var entry in archive.Entries)
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    entrySummary.Append("\n  - ").Append(entry.FullName).Append(" (").Append(entry.Length).Append(" bytes)");
#endif
                    if (entry.FullName.IndexOf(".xcappdata", StringComparison.OrdinalIgnoreCase) >= 0)
                        looksLikeXcappdata = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    if (dumpDir != null && !string.IsNullOrEmpty(entry.Name))
                    {
                        try
                        {
                            string outPath = Path.Combine(dumpDir, entry.Name);
                            using var es = entry.Open();
                            using var fs = File.Create(outPath);
                            es.CopyTo(fs);
                        }
                        catch { /* best-effort dump */ }
                    }
#endif
                }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.Log($"[MoveMotionParser] MOTION_DATA archive entries:{entrySummary}" +
                          (dumpDir != null ? $"\n  (extracted to: {dumpDir})" : ""));
#endif

                // Prefer *motion_data*.json entries; only accept JSON that contains top-level mocap_data.
                var jsonEntries = new List<ZipArchiveEntry>();
                foreach (var entry in archive.Entries)
                {
                    if (IsMoveMotionJsonEntry(entry.FullName))
                        jsonEntries.Add(entry);
                }
                jsonEntries.Sort((a, b) => MotionJsonEntryPriority(b.FullName).CompareTo(MotionJsonEntryPriority(a.FullName)));

                foreach (var entry in jsonEntries)
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    string json = reader.ReadToEnd();
                    if (!LooksLikeMoveMocapJson(json))
                    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                        LogJsonShape(entry.FullName, json);
#endif
                        continue;
                    }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    LogJsonShape(entry.FullName, json);
#endif
                    var motion = ParseActorJson(json, map);
                    if (motion != null && motion.FrameCount > 0 && motion.JointCount > 0)
                        return motion;
                }

                if (looksLikeXcappdata)
                {
                    Debug.LogError("[MoveMotionParser] This zip looks like an Xcode app container (.xcappdata), not a Move MOTION_DATA export. " +
                                   "Use TENDOR → Move AI → Parse MOTION_DATA JSON and pick:\n" +
                                   "  .../MoveAIDebug/<timestamp>/track00_motion_data.json\n" +
                                   "Or use TENDOR → Move AI → Parse Bundled AppData Sample.");
                }
                else
                {
                    Debug.LogError("[MoveMotionParser] No JSON with top-level 'mocap_data' found in archive. " +
                                   "Move MOTION_DATA zips contain track00_motion_data.json — not hip recordings or manifests.");
                }
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MoveMotionParser] Failed to read MOTION_DATA zip: {e.Message}");
                return null;
            }
        }

        public static MoveMotion ParseActorJson(string json, MoveJointMap map = null)
        {
            object root = MiniJson.Parse(json);
            var obj = MiniJson.AsObject(root);
            if (obj != null)
            {
                // Reject app recording / manifest JSON mistaken for Move motion.
                if (obj.ContainsKey("recordingFormatVersion") ||
                    (obj.ContainsKey("recordingFile") && obj.ContainsKey("worldMapFile")))
                    return null;
                return ParseActor(obj, map);
            }

            // Some exports are a top-level array. It can be either a list of actors (objects) or a flat list of
            // frame objects. Try the first element as an actor object; otherwise treat the array as the frames.
            var arr = MiniJson.AsArray(root);
            if (arr != null && arr.Count > 0)
            {
                var firstObj = MiniJson.AsObject(arr[0]);
                if (firstObj != null && FindArray(firstObj, "frames", "animation", "poses", "keyframes") != null)
                    return ParseActor(firstObj, map);

                // Treat the whole array as a frames list under a synthetic actor object.
                var wrapper = new Dictionary<string, object> { { "frames", arr } };
                return ParseActor(wrapper, map);
            }

            Debug.LogError("[MoveMotionParser] Top-level MOTION_DATA JSON is neither an object nor a non-empty array");
            return null;
        }

        static void LogJsonShape(string fileName, string json)
        {
            try
            {
                object root = MiniJson.Parse(json);
                string shape;
                var obj = MiniJson.AsObject(root);
                if (obj != null)
                {
                    shape = "object keys: [" + string.Join(", ", obj.Keys) + "]";
                }
                else
                {
                    var arr = MiniJson.AsArray(root);
                    if (arr != null)
                    {
                        var firstObj = arr.Count > 0 ? MiniJson.AsObject(arr[0]) : null;
                        shape = $"array (len={arr.Count})" +
                                (firstObj != null ? ", element0 keys: [" + string.Join(", ", firstObj.Keys) + "]" : "");
                    }
                    else shape = "scalar";
                }

                string preview = json.Length > 1200 ? json.Substring(0, 1200) + "…" : json;
                Debug.Log($"[MoveMotionParser] '{fileName}' {shape}\n  preview: {preview}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MoveMotionParser] Could not introspect '{fileName}': {e.Message}");
            }
        }

        static MoveMotion ParseActor(Dictionary<string, object> obj, MoveJointMap map)
        {
            // Move API MOTION_DATA (confirmed schema): { "mocap_data": [ { "timestamp", "joint_data": { ... } } ] }
            if (obj.TryGetValue("mocap_data", out var mocapNode))
            {
                var mocapFrames = MiniJson.AsArray(mocapNode);
                if (mocapFrames != null && mocapFrames.Count > 0)
                    return ParseMocapData(mocapFrames, map);
            }

            // Legacy / alternate layouts: descend into nested actor containers, then look for frames arrays.
            if (FindArray(obj, "frames", "animation", "poses", "keyframes") == null)
            {
                var inner = FindActorObject(obj);
                if (inner != null) obj = inner;
            }

            var motion = new MoveMotion();

            // fps / frame rate
            if (MiniJson.TryGet(obj, "fps", out var fpsNode) || MiniJson.TryGet(obj, "frameRate", out fpsNode) ||
                MiniJson.TryGet(obj, "frame_rate", out fpsNode))
            {
                float fps = MiniJson.ToFloat(fpsNode, 30f);
                if (fps > 0) motion.fps = fps;
            }

            // Joint topology: names + parents. Accept a few common key spellings.
            var names = FindArray(obj, "joints", "jointNames", "joint_names", "bones", "names");
            if (names != null)
            {
                foreach (var n in names)
                {
                    // names may be plain strings or objects like { "name": "Hips", "parent": 0 }
                    if (n is string s)
                    {
                        motion.jointNames.Add(s);
                        motion.jointParents.Add(-1);
                    }
                    else
                    {
                        var jo = MiniJson.AsObject(n);
                        motion.jointNames.Add(MiniJson.ToStr(Get(jo, "name", "joint", "id")) ?? $"joint{motion.jointNames.Count}");
                        motion.jointParents.Add((int)MiniJson.ToDouble(Get(jo, "parent", "parentIndex", "parent_index"), -1));
                    }
                }
            }

            var parents = FindArray(obj, "parents", "jointParents", "parent_indices");
            if (parents != null && parents.Count == motion.jointNames.Count)
            {
                for (int i = 0; i < parents.Count; i++)
                    motion.jointParents[i] = (int)MiniJson.ToDouble(parents[i], -1);
            }

            // Frames: an array of per-frame objects.
            var frames = FindArray(obj, "frames", "animation", "poses", "keyframes");
            if (frames == null)
            {
                Debug.LogError("[MoveMotionParser] Could not locate a frames array in MOTION_DATA JSON " +
                               $"(schema mismatch). Object keys present: [{string.Join(", ", obj.Keys)}]. " +
                               "Map the correct frames key in ParseActor / FindActorObject.");
                return null;
            }

            int jointCount = Mathf.Max(motion.jointNames.Count, 1);
            float t = 0f;
            float dt = motion.fps > 0 ? 1f / motion.fps : 1f / 30f;

            foreach (var f in frames)
            {
                var fo = MiniJson.AsObject(f);
                if (fo == null) continue;

                var frame = new MoveMotionFrame(jointCount);
                frame.time = (float)MiniJson.ToDouble(Get(fo, "time", "t", "timestamp"), t);

                // Root / hips translation
                var rootNode = Get(fo, "root", "rootPosition", "root_position", "hips", "translation", "position");
                frame.rootPosition = ConvertPosition(ReadVector3(rootNode));

                // Per-joint rotations: either a flat array aligned with jointNames, or named entries.
                var rotations = FindArray(fo, "rotations", "localRotations", "local_rotations", "quaternions");
                if (rotations != null)
                {
                    int count = Mathf.Min(rotations.Count, jointCount);
                    for (int i = 0; i < count; i++)
                        frame.localRotations[i] = ConvertRotation(ReadQuaternion(rotations[i]));
                }

                var positions = FindArray(fo, "localPositions", "local_positions", "offsets", "translations");
                if (positions != null)
                {
                    int count = Mathf.Min(positions.Count, jointCount);
                    for (int i = 0; i < count; i++)
                        frame.localPositions[i] = ConvertPosition(ReadVector3(positions[i]));
                }

                // Named per-joint entries fallback: { "joints": [ { "name":..., "rotation":[...] } ] }
                var jointEntries = FindArray(fo, "joints", "bones");
                if (jointEntries != null)
                {
                    foreach (var je in jointEntries)
                    {
                        var jo = MiniJson.AsObject(je);
                        if (jo == null) continue;
                        string name = MiniJson.ToStr(Get(jo, "name", "joint", "id"));
                        int idx = motion.IndexOfJoint(name);
                        if (idx < 0) continue;
                        frame.localRotations[idx] = ConvertRotation(ReadQuaternion(Get(jo, "rotation", "localRotation", "quaternion")));
                        var posNode = Get(jo, "position", "localPosition", "offset");
                        if (posNode != null) frame.localPositions[idx] = ConvertPosition(ReadVector3(posNode));
                    }
                }

                motion.frames.Add(frame);
                t = frame.time + dt;
            }

            if (motion.frames.Count == 0 || motion.jointNames.Count == 0)
                return null;

            Debug.Log($"[MoveMotionParser] Parsed motion: {motion.JointCount} joints, {motion.FrameCount} frames, {motion.fps} fps");
            return motion;
        }

        /// <summary>
        /// Parse Move API biomechanical MOTION_DATA: mocap_data[] with per-frame joint_data dictionaries.
        /// Coordinate system: Move Z-up RH → Unity Y-up LH. Rotations: local euler (radians) per joint type field.
        /// </summary>
        static MoveMotion ParseMocapData(List<object> mocapFrames, MoveJointMap map)
        {
            var motion = new MoveMotion();

            // Joint topology from the first frame's joint_data keys.
            var firstFrame = MiniJson.AsObject(mocapFrames[0]);
            var firstJointData = MiniJson.AsObject(Get(firstFrame, "joint_data"));
            if (firstJointData == null || firstJointData.Count == 0)
            {
                Debug.LogError("[MoveMotionParser] mocap_data frame 0 has no joint_data");
                return null;
            }

            BuildJointTopology(firstJointData.Keys, motion);

            // Infer fps from timestamp deltas when not explicitly provided.
            var timestamps = new List<float>(mocapFrames.Count);
            foreach (var f in mocapFrames)
            {
                var fo = MiniJson.AsObject(f);
                if (fo == null) continue;
                timestamps.Add((float)MiniJson.ToDouble(Get(fo, "timestamp", "time", "t"), timestamps.Count > 0 ? timestamps[timestamps.Count - 1] + 1f / 30f : 0f));
            }
            if (timestamps.Count >= 2)
            {
                float totalDt = timestamps[timestamps.Count - 1] - timestamps[0];
                if (totalDt > 1e-4f)
                    motion.fps = (timestamps.Count - 1) / totalDt;
            }

            int jointCount = motion.JointCount;
            foreach (var f in mocapFrames)
            {
                var fo = MiniJson.AsObject(f);
                if (fo == null) continue;

                var jointData = MiniJson.AsObject(Get(fo, "joint_data"));
                if (jointData == null) continue;

                var frame = new MoveMotionFrame(jointCount);
                frame.time = (float)MiniJson.ToDouble(Get(fo, "timestamp", "time", "t"), frame.time);

                // Root world position (Move "Root" pelvis joint).
                int rootIdx = motion.IndexOfJoint("Root");
                if (rootIdx < 0) rootIdx = 0;
                string rootName = motion.jointNames[rootIdx];
                if (jointData.TryGetValue(rootName, out var rootNode))
                {
                    var rootObj = MiniJson.AsObject(rootNode);
                    frame.rootPosition = ConvertPosition(ReadVector3(Get(rootObj, "position")));
                }

                foreach (var kv in jointData)
                {
                    int idx = motion.IndexOfJoint(kv.Key);
                    if (idx < 0) continue;

                    var jo = MiniJson.AsObject(kv.Value);
                    if (jo == null) continue;

                    string rotType = MiniJson.ToStr(Get(jo, "type")) ?? "euler_xyz";
                    var rotValues = MiniJson.AsArray(Get(jo, "rotations"));
                    frame.localRotations[idx] = ConvertRotation(EulerToQuaternion(rotType, rotValues));

                    // World positions are stored as parent-local offsets when parent is known (used by FK height estimate).
                    Vector3 worldPos = ConvertPosition(ReadVector3(Get(jo, "position")));
                    int parent = idx < motion.jointParents.Count ? motion.jointParents[idx] : -1;
                    if (parent >= 0 && jointData.TryGetValue(motion.jointNames[parent], out var parentNode))
                    {
                        Vector3 parentWorld = ConvertPosition(ReadVector3(Get(MiniJson.AsObject(parentNode), "position")));
                        frame.localPositions[idx] = worldPos - parentWorld;
                    }
                }

                motion.frames.Add(frame);
            }

            Debug.Log($"[MoveMotionParser] Parsed mocap_data: {motion.JointCount} joints, {motion.FrameCount} frames, {motion.fps:F1} fps");

            // Report which Move joints won't drive a character bone, so the bone map can be extended to use them.
            if (map != null)
            {
                var unmapped = new List<string>();
                foreach (var n in motion.jointNames)
                    if (!map.TryGetBone(n, out _)) unmapped.Add(n);
                if (unmapped.Count > 0)
                    Debug.Log("[MoveMotionParser] Move joints with no character-bone mapping (add to MoveJointMap to use them): " +
                              string.Join(", ", unmapped));
            }
            return motion;
        }

        // Move API biomechanical skeleton (https://developers.move.ai/docs/motion-data-format/).
        // The arm chain is listed as the FULL canonical hierarchy clavicle -> shoulder -> shoulder_rotation ->
        // elbow -> wrist. Real exports only contain a subset (s1 has just "shoulder_rotation"; s2 has
        // "clavicle" + "shoulder"), so ResolveParentIndex walks UP this chain to the nearest joint that is
        // actually present. That way the arm never detaches regardless of which shoulder joints the model emits.
        static readonly Dictionary<string, string> MoveSkeletonParents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Root", null },
            { "Left_hip", "Root" }, { "Left_knee", "Left_hip" }, { "Left_ankle", "Left_knee" }, { "Left_toe", "Left_ankle" },
            { "Right_hip", "Root" }, { "Right_knee", "Right_hip" }, { "Right_ankle", "Right_knee" }, { "Right_toe", "Right_ankle" },
            { "Spine1", "Root" }, { "Spine2", "Spine1" }, { "Spine3", "Spine2" }, { "Neck", "Spine2" }, { "Head", "Neck" },
            { "Left_clavicle", "Spine2" }, { "Left_shoulder", "Left_clavicle" }, { "Left_shoulder_rotation", "Left_shoulder" },
            { "Left_elbow", "Left_shoulder_rotation" }, { "Left_wrist", "Left_elbow" },
            { "Right_clavicle", "Spine2" }, { "Right_shoulder", "Right_clavicle" }, { "Right_shoulder_rotation", "Right_shoulder" },
            { "Right_elbow", "Right_shoulder_rotation" }, { "Right_wrist", "Right_elbow" },
        };

        /// <summary>
        /// Nearest present ancestor of <paramref name="name"/> by walking up the canonical skeleton. Skips
        /// joints the export omitted (e.g. missing clavicle/shoulder), so children stay attached.
        /// </summary>
        static int ResolveParentIndex(string name, MoveMotion motion)
        {
            if (!MoveSkeletonParents.TryGetValue(name, out string parent)) return -1;
            while (!string.IsNullOrEmpty(parent))
            {
                int idx = motion.IndexOfJoint(parent);
                if (idx >= 0) return idx;
                if (!MoveSkeletonParents.TryGetValue(parent, out parent)) break;
            }
            return -1;
        }

        /// <summary>Build joint name list (Root first) and parent indices from Move's biomechanical skeleton.</summary>
        static void BuildJointTopology(IEnumerable<string> jointNamesFromFrame, MoveMotion motion)
        {
            var ordered = new List<string>();
            if (jointNamesFromFrame != null)
            {
                foreach (var n in jointNamesFromFrame)
                {
                    if (string.Equals(n, "Root", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!ordered.Exists(s => string.Equals(s, n, StringComparison.OrdinalIgnoreCase)))
                            ordered.Insert(0, n);
                    }
                    else if (!ordered.Exists(s => string.Equals(s, n, StringComparison.OrdinalIgnoreCase)))
                    {
                        ordered.Add(n);
                    }
                }
            }

            foreach (var name in ordered)
            {
                motion.jointNames.Add(name);
                motion.jointParents.Add(-1); // filled below
            }

            for (int i = 0; i < motion.jointNames.Count; i++)
                motion.jointParents[i] = ResolveParentIndex(motion.jointNames[i], motion);

            // Surface any joint the export contains but our skeleton table doesn't know (e.g. new s2/Dex joints),
            // so we can extend MoveSkeletonParents + MoveJointMap from a single device run.
            var unknown = new List<string>();
            for (int i = 0; i < motion.jointNames.Count; i++)
            {
                string n = motion.jointNames[i];
                bool isRoot = string.Equals(n, "Root", StringComparison.OrdinalIgnoreCase);
                if (!isRoot && motion.jointParents[i] < 0)
                    unknown.Add(n);
            }
            if (unknown.Count > 0)
                Debug.LogWarning("[MoveMotionParser] Joints present but not in skeleton table (add to MoveSkeletonParents): " +
                                 string.Join(", ", unknown));
        }

        static Quaternion EulerToQuaternion(string rotType, List<object> values)
        {
            if (values == null || values.Count == 0) return Quaternion.identity;

            float r0 = MiniJson.ToFloat(values[0]);
            float r1 = values.Count > 1 ? MiniJson.ToFloat(values[1]) : 0f;
            float r2 = values.Count > 2 ? MiniJson.ToFloat(values[2]) : 0f;
            float r3 = values.Count > 3 ? MiniJson.ToFloat(values[3]) : 0f;

            switch ((rotType ?? "").ToLowerInvariant())
            {
                case "quaternion":
                    return Normalize(new Quaternion(r0, r1, r2, r3));
                case "euler_x":
                    return Quaternion.AngleAxis(r0 * Mathf.Rad2Deg, Vector3.right);
                case "euler_z":
                    return Quaternion.AngleAxis(r0 * Mathf.Rad2Deg, Vector3.forward);
                case "euler_yz":
                    return Quaternion.AngleAxis(r1 * Mathf.Rad2Deg, Vector3.up) *
                           Quaternion.AngleAxis(r0 * Mathf.Rad2Deg, Vector3.forward);
                case "euler_zyx":
                    // Intrinsic ZYX: q = qx * qy * qz applied as Rz then Ry then Rx on vector → q = qx*qy*qz
                    return Quaternion.AngleAxis(r0 * Mathf.Rad2Deg, Vector3.forward) *
                           Quaternion.AngleAxis(r1 * Mathf.Rad2Deg, Vector3.up) *
                           Quaternion.AngleAxis(r2 * Mathf.Rad2Deg, Vector3.right);
                case "euler_xyz":
                default:
                    // Intrinsic XYZ (Move default): apply X then Y then Z → q = qz * qy * qx
                    return Quaternion.AngleAxis(r2 * Mathf.Rad2Deg, Vector3.forward) *
                           Quaternion.AngleAxis(r1 * Mathf.Rad2Deg, Vector3.up) *
                           Quaternion.AngleAxis(r0 * Mathf.Rad2Deg, Vector3.right);
            }
        }

        // Coordinate conversion --------------------------------------------------------------------
        // Move API: right-handed, Z-up (X=lateral, Y=forward, Z=vertical). Unity: left-handed, Y-up.
        // Z-up → Y-up: (x, z, y). Handedness: negate forward axis (Move +Y → Unity -Z).

        // Move is Z-up right-handed; Unity is Y-up left-handed. Mapping (x, y, z) -> (x, z, y) swaps Y/Z (Z-up
        // to Y-up) AND flips handedness (determinant -1), so forward/back (sagittal) motion replays the correct
        // way. Negating Y instead (det +1) left the body mirrored front-back, making forward bends look backward.
        static Vector3 ConvertPosition(Vector3 v) => new Vector3(v.x, v.z, v.y);
        static Quaternion ConvertRotation(Quaternion q) => q;

        // Reading helpers --------------------------------------------------------------------------

        static Vector3 ReadVector3(object node)
        {
            var arr = MiniJson.AsArray(node);
            if (arr != null && arr.Count >= 3)
                return new Vector3(MiniJson.ToFloat(arr[0]), MiniJson.ToFloat(arr[1]), MiniJson.ToFloat(arr[2]));

            var obj = MiniJson.AsObject(node);
            if (obj != null)
                return new Vector3(MiniJson.ToFloat(Get(obj, "x")), MiniJson.ToFloat(Get(obj, "y")), MiniJson.ToFloat(Get(obj, "z")));

            return Vector3.zero;
        }

        static Quaternion ReadQuaternion(object node)
        {
            var arr = MiniJson.AsArray(node);
            if (arr != null && arr.Count >= 4)
            {
                // Assume [x, y, z, w] order; verify against a real sample.
                var q = new Quaternion(MiniJson.ToFloat(arr[0]), MiniJson.ToFloat(arr[1]), MiniJson.ToFloat(arr[2]), MiniJson.ToFloat(arr[3]));
                return Normalize(q);
            }

            var obj = MiniJson.AsObject(node);
            if (obj != null)
            {
                var q = new Quaternion(
                    MiniJson.ToFloat(Get(obj, "x")), MiniJson.ToFloat(Get(obj, "y")),
                    MiniJson.ToFloat(Get(obj, "z")), MiniJson.ToFloat(Get(obj, "w"), 1f));
                return Normalize(q);
            }

            return Quaternion.identity;
        }

        static Quaternion Normalize(Quaternion q)
        {
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 1e-6f) return Quaternion.identity;
            return new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
        }

        /// <summary>
        /// Find a nested per-actor object under a common container key. Handles both a single nested object and an
        /// array of actors (returns the first actor object).
        /// </summary>
        static Dictionary<string, object> FindActorObject(Dictionary<string, object> obj)
        {
            string[] containerKeys =
            {
                "actors", "subjects", "people", "characters", "skeletons",
                "takes", "take", "result", "results", "motion", "animation",
                "data", "output", "outputs", "body"
            };

            foreach (var key in containerKeys)
            {
                if (!obj.TryGetValue(key, out var node)) continue;

                var asObj = MiniJson.AsObject(node);
                if (asObj != null) return asObj;

                var asArr = MiniJson.AsArray(node);
                if (asArr != null && asArr.Count > 0)
                {
                    var first = MiniJson.AsObject(asArr[0]);
                    if (first != null) return first;
                }
            }
            return null;
        }

        static bool LooksLikeMoveMocapJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            var obj = MiniJson.AsObject(MiniJson.Parse(json));
            return obj != null && obj.ContainsKey("mocap_data");
        }

        static bool IsMoveMotionJsonEntry(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return false;
            if (!fullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            if (fullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) return false;
            if (fullName.Contains("/._") || fullName.Contains("\\._")) return false;
            string file = Path.GetFileName(fullName);
            if (file.StartsWith("._")) return false;
            if (file.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase)) return false;
            if (file.StartsWith("hip_recording_", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static int MotionJsonEntryPriority(string fullName)
        {
            string file = Path.GetFileName(fullName ?? "").ToLowerInvariant();
            if (file.Contains("motion_data")) return 100;
            if (fullName.IndexOf("moveaidebug", StringComparison.OrdinalIgnoreCase) >= 0) return 50;
            return 0;
        }

        static List<object> FindArray(Dictionary<string, object> obj, params string[] keys)
        {
            if (obj == null) return null;
            foreach (var k in keys)
            {
                if (obj.TryGetValue(k, out var node))
                {
                    var arr = MiniJson.AsArray(node);
                    if (arr != null) return arr;
                }
            }
            return null;
        }

        static object Get(Dictionary<string, object> obj, params string[] keys)
        {
            if (obj == null) return null;
            foreach (var k in keys)
            {
                if (obj.TryGetValue(k, out var node))
                    return node;
            }
            return null;
        }
    }
}
