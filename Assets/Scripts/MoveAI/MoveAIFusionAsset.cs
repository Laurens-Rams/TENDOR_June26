using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Baked result of fusing a Move AI motion clip with an ARKit recording: the Move pose plus a corrected
    /// root path in RouteRoot-local space and a uniform character scale. Replays through the same RouteRoot
    /// mapping as the dot-skeleton recordings, so it inherits the existing Immersal/world-map drift correction.
    /// Serialized with <see cref="MiniJson"/> (the embedded MoveMotion uses arrays JsonUtility can't handle).
    /// </summary>
    public class MoveAIFusionAsset
    {
        public MoveMotion pose;                                   // joint rotations over time
        public List<Vector3> rootPathLocal = new List<Vector3>(); // RouteRoot-local, one per pose frame
        public float frameRate = 30f;
        public float scale = 1f;                                  // uniform head-to-feet scale
        public Vector3 correctionWeights = new Vector3(0.6f, 0.2f, 1f); // x,y,z weights actually applied
        // True once the baked pose/root sit on the correct (ARKit-aligned) clock. Older assets baked before the
        // videoStartTimeOffset sign fix were shifted ~offset seconds early; the no-API rebake re-times them once
        // and sets this so a second rebake can't shift them again. Fresh bakes are correct and set it directly.
        public bool offsetCorrected;

        // Spatial metadata mirrored from the source HipRecording (chooses the playback RouteRoot provider).
        public string mapId;
        public string routeId;
        public string spatialSource;
        public Vector3 referencePosition;
        public Quaternion referenceRotation = Quaternion.identity;

        public int FrameCount => rootPathLocal.Count;
        public float Duration => frameRate > 0 ? FrameCount / frameRate : 0f;

        // Persistence ------------------------------------------------------------------------------

        public string ToJson()
        {
            var root = new Dictionary<string, object>
            {
                { "frameRate", frameRate },
                { "scale", scale },
                { "correctionWeights", Vec(correctionWeights) },
                { "offsetCorrected", offsetCorrected },
                { "mapId", mapId },
                { "routeId", routeId },
                { "spatialSource", spatialSource },
                { "referencePosition", Vec(referencePosition) },
                { "referenceRotation", Quat(referenceRotation) },
                { "rootPathLocal", VecList(rootPathLocal) },
                { "pose", PoseToJson(pose) },
            };
            return MiniJson.Serialize(root);
        }

        public static MoveAIFusionAsset FromJson(string json)
        {
            var obj = MiniJson.AsObject(MiniJson.Parse(json));
            if (obj == null) return null;

            var asset = new MoveAIFusionAsset
            {
                frameRate = MiniJson.ToFloat(Val(obj, "frameRate"), 30f),
                scale = MiniJson.ToFloat(Val(obj, "scale"), 1f),
                correctionWeights = ReadVec(Val(obj, "correctionWeights")),
                offsetCorrected = Val(obj, "offsetCorrected") is bool oc && oc,
                mapId = MiniJson.ToStr(Val(obj, "mapId")),
                routeId = MiniJson.ToStr(Val(obj, "routeId")),
                spatialSource = MiniJson.ToStr(Val(obj, "spatialSource")),
                referencePosition = ReadVec(Val(obj, "referencePosition")),
                referenceRotation = ReadQuat(Val(obj, "referenceRotation")),
            };
            asset.rootPathLocal = ReadVecList(Val(obj, "rootPathLocal"));
            asset.pose = PoseFromJson(MiniJson.AsObject(Val(obj, "pose")));
            return asset;
        }

        public bool Save(string filePath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, ToJson());
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MoveAIFusionAsset] Save failed: {e.Message}");
                return false;
            }
        }

        public static MoveAIFusionAsset Load(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                return FromJson(File.ReadAllText(filePath));
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[MoveAIFusionAsset] Load failed: {e.Message}");
                return null;
            }
        }

        // Pose (MoveMotion) <-> JSON ---------------------------------------------------------------

        static Dictionary<string, object> PoseToJson(MoveMotion m)
        {
            if (m == null) return null;
            var names = new List<object>();
            foreach (var n in m.jointNames) names.Add(n);
            var parents = new List<object>();
            foreach (var p in m.jointParents) parents.Add(p);

            var frames = new List<object>();
            foreach (var f in m.frames)
            {
                var rots = new List<object>();
                foreach (var q in f.localRotations) rots.Add(Quat(q));
                var poss = new List<object>();
                if (f.localPositions != null)
                    foreach (var p in f.localPositions) poss.Add(Vec(p));
                frames.Add(new Dictionary<string, object>
                {
                    { "t", f.time },
                    { "root", Vec(f.rootPosition) },
                    { "rot", rots },
                    { "pos", poss },
                });
            }

            return new Dictionary<string, object>
            {
                { "fps", m.fps },
                { "names", names },
                { "parents", parents },
                { "frames", frames },
            };
        }

        static MoveMotion PoseFromJson(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            var m = new MoveMotion { fps = MiniJson.ToFloat(Val(obj, "fps"), 30f) };

            var names = MiniJson.AsArray(Val(obj, "names"));
            if (names != null) foreach (var n in names) m.jointNames.Add(MiniJson.ToStr(n));
            var parents = MiniJson.AsArray(Val(obj, "parents"));
            if (parents != null) foreach (var p in parents) m.jointParents.Add((int)MiniJson.ToDouble(p, -1));

            int jointCount = m.jointNames.Count;
            var frames = MiniJson.AsArray(Val(obj, "frames"));
            if (frames != null)
            {
                foreach (var fo in frames)
                {
                    var fd = MiniJson.AsObject(fo);
                    var frame = new MoveMotionFrame(jointCount)
                    {
                        time = MiniJson.ToFloat(Val(fd, "t")),
                        rootPosition = ReadVec(Val(fd, "root")),
                    };
                    var rots = MiniJson.AsArray(Val(fd, "rot"));
                    if (rots != null)
                    {
                        int c = Mathf.Min(rots.Count, jointCount);
                        for (int i = 0; i < c; i++) frame.localRotations[i] = ReadQuat(rots[i]);
                    }
                    var poss = MiniJson.AsArray(Val(fd, "pos"));
                    if (poss != null)
                    {
                        int c = Mathf.Min(poss.Count, jointCount);
                        for (int i = 0; i < c; i++) frame.localPositions[i] = ReadVec(poss[i]);
                    }
                    m.frames.Add(frame);
                }
            }
            return m;
        }

        // Small serialization helpers --------------------------------------------------------------

        static object Vec(Vector3 v) => new List<object> { v.x, v.y, v.z };
        static object Quat(Quaternion q) => new List<object> { q.x, q.y, q.z, q.w };

        static object VecList(List<Vector3> list)
        {
            var outer = new List<object>();
            foreach (var v in list) outer.Add(Vec(v));
            return outer;
        }

        static Vector3 ReadVec(object node)
        {
            var a = MiniJson.AsArray(node);
            if (a != null && a.Count >= 3) return new Vector3(MiniJson.ToFloat(a[0]), MiniJson.ToFloat(a[1]), MiniJson.ToFloat(a[2]));
            return Vector3.zero;
        }

        static Quaternion ReadQuat(object node)
        {
            var a = MiniJson.AsArray(node);
            if (a != null && a.Count >= 4) return new Quaternion(MiniJson.ToFloat(a[0]), MiniJson.ToFloat(a[1]), MiniJson.ToFloat(a[2]), MiniJson.ToFloat(a[3]));
            return Quaternion.identity;
        }

        static List<Vector3> ReadVecList(object node)
        {
            var list = new List<Vector3>();
            var a = MiniJson.AsArray(node);
            if (a != null) foreach (var v in a) list.Add(ReadVec(v));
            return list;
        }

        static object Val(Dictionary<string, object> obj, string key)
        {
            if (obj != null && obj.TryGetValue(key, out var v)) return v;
            return null;
        }
    }
}
