using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using BodyTracking.Glb;
using BodyTracking.Playback.PostProcess;

namespace BodyTracking.Playback
{
    /// <summary>
    /// Loads a Move AI GLB at runtime via glTFast, builds a Humanoid <see cref="Avatar"/> for its skeleton, and
    /// exposes the recorded animation as a per-frame muscle-space <see cref="HumanPose"/>. The mesh is instantiated
    /// hidden — only the skeleton + clip are used. The clip is sampled with <see cref="AnimationClip.SampleAnimation"/>
    /// on demand so any time can be evaluated deterministically and kept in sync with the fused playback clock.
    /// glTFast clips are legacy transform curves and cannot be driven through Playables.
    ///
    /// Loading is spread across frames (parse → yield → instantiate → yield → avatar build) so a ~3 MB GLB on device
    /// does not freeze the UI when glTFast resumes on the main thread.
    /// </summary>
    public class MoveGlbSource
    {
        public bool IsReady { get; private set; }
        public float Duration => clip != null ? clip.length : 0f;
        public string Error { get; private set; }
        public Transform Hips { get; private set; }

        GameObject root;
        AnimationClip clip;
        Avatar avatar;
        HumanPoseHandler handler;
        Dictionary<HumanBodyBones, Transform> humanBones;

        // --- Optional muscle-space jitter smoothing (Step 2b: removes GLB body/finger jitter that the world-joint
        // smoothing can't see, because body+fingers come from the muscle-space HumanPose) ---
        bool smoothEnabled;
        float smoothMinCutoff = 1f;
        float smoothBeta = 0.01f;
        OneEuroFilterScalar[] muscleFilters;
        readonly OneEuroFilterQuaternion bodyRotFilter = new OneEuroFilterQuaternion();

        /// <summary>Enable/disable speed-adaptive smoothing of the sampled muscles + body rotation.</summary>
        public void SetSmoothing(bool enabled, float minCutoff, float beta)
        {
            smoothEnabled = enabled;
            smoothMinCutoff = minCutoff;
            smoothBeta = beta;
            bodyRotFilter.SetParams(minCutoff, beta);
            if (muscleFilters != null)
                foreach (var f in muscleFilters) { f.MinCutoff = minCutoff; f.Beta = beta; }
        }

        /// <summary>Clear smoothing history (call when (re)starting playback to avoid a stale ease-in).</summary>
        public void ResetSmoothing()
        {
            bodyRotFilter.Reset();
            if (muscleFilters != null)
                foreach (var f in muscleFilters) f.Reset();
        }

        static readonly Dictionary<string, MoveGlbSource> cache = new Dictionary<string, MoveGlbSource>();

        // Coalesce WarmGlb + AttachGlbWhenReady (or double-tap Play) into one in-flight load.
        static readonly Dictionary<string, InFlight> inFlight = new Dictionary<string, InFlight>();

        sealed class InFlight
        {
            public bool done;
            public MoveGlbSource result;
            public readonly List<Action<MoveGlbSource>> waiters = new List<Action<MoveGlbSource>>();
        }

        /// <summary>Frame-spread load. Safe to call from multiple coroutines for the same path — only one load runs.</summary>
        public static IEnumerator LoadCoroutine(string glbPath, Transform parent, Action<MoveGlbSource> onComplete)
        {
            if (cache.TryGetValue(glbPath, out var cached) && cached.IsReady && cached.root != null)
            {
                onComplete?.Invoke(cached);
                yield break;
            }

            if (!inFlight.TryGetValue(glbPath, out var flight))
            {
                flight = new InFlight();
                inFlight[glbPath] = flight;
                yield return RunLoadCoroutine(glbPath, parent, flight);
                inFlight.Remove(glbPath);
                onComplete?.Invoke(flight.result);
                yield break;
            }

            if (flight.done)
            {
                onComplete?.Invoke(flight.result);
                yield break;
            }

            bool finished = false;
            MoveGlbSource joined = null;
            void OnDone(MoveGlbSource s) { joined = s; finished = true; }
            flight.waiters.Add(OnDone);
            while (!finished)
                yield return null;
            onComplete?.Invoke(joined);
        }

        static IEnumerator RunLoadCoroutine(string glbPath, Transform parent, InFlight flight)
        {
            MoveGlbSource src = null;
            if (string.IsNullOrEmpty(glbPath) || !File.Exists(glbPath))
            {
                Debug.LogWarning("[MoveGlbSource] GLB not found: " + glbPath);
            }
            else
            {
                src = new MoveGlbSource();
                yield return src.LoadSpread(glbPath, parent);
                if (src.IsReady)
                    cache[glbPath] = src;
                else
                {
                    src.Cleanup();
                    src = null;
                }
            }

            flight.result = src;
            flight.done = true;
            foreach (var w in flight.waiters)
                w(src);
            flight.waiters.Clear();
        }

        IEnumerator LoadSpread(string glbPath, Transform parent)
        {
            long kb = new FileInfo(glbPath).Length / 1024;
            Debug.Log($"[MoveGlbSource] Loading '{Path.GetFileName(glbPath)}' ({kb} KB)…");

            var gltf = new GltfImport();
            Task<bool> loadTask = null;
            bool loadStartFailed = false;
            try
            {
                loadTask = gltf.LoadFile(glbPath);
            }
            catch (Exception e)
            {
                Error = "glTFast load threw: " + e.Message;
                loadStartFailed = true;
            }
            if (loadStartFailed)
                yield break;

            float parseDeadline = Time.realtimeSinceStartup + 30f;
            while (!loadTask.IsCompleted)
            {
                if (Time.realtimeSinceStartup > parseDeadline)
                {
                    Error = "glTFast parse timed out after 30s";
                    yield break;
                }
                yield return null;
            }

            if (loadTask.IsFaulted)
            {
                Error = loadTask.Exception?.GetBaseException().Message ?? "glTFast faulted";
                yield break;
            }
            if (!loadTask.Result)
            {
                Error = "glTFast failed to load GLB (Draco/KTX compression not installed?)";
                yield break;
            }

            Debug.Log("[MoveGlbSource] glTF parsed — instantiating skeleton (next frame)…");
            yield return null;

            root = new GameObject("MoveGlbSource");
            if (parent != null)
                root.transform.SetParent(parent, false);

            if (!gltf.InstantiateMainScene(root.transform))
            {
                Error = "InstantiateMainScene failed";
                Cleanup();
                yield break;
            }

            Debug.Log("[MoveGlbSource] Instantiated — stripping renderers…");
            yield return null;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = false;
                if ((i & 63) == 63)
                    yield return null;
            }

            foreach (var legacy in root.GetComponentsInChildren<UnityEngine.Animation>(true))
                UnityEngine.Object.Destroy(legacy);
            foreach (var a in root.GetComponentsInChildren<Animator>(true))
                UnityEngine.Object.Destroy(a);

            yield return null;

            var clips = gltf.GetAnimationClips();
            if (clips == null || clips.Length == 0 || clips[0] == null)
            {
                Error = "GLB contains no animation clips";
                Cleanup();
                yield break;
            }
            clip = clips[0];

            Debug.Log($"[MoveGlbSource] Clip '{clip.name}' ({clip.length:F2}s) — building humanoid avatar (next frame)…");
            yield return null;

            avatar = HumanoidAvatarFactory.Build(root, out string report, out humanBones);
            if (avatar == null)
            {
                Error = "Humanoid build failed: " + report;
                Cleanup();
                yield break;
            }

            yield return null;

            handler = new HumanPoseHandler(avatar, root.transform);
            Hips = FindHipsTransform(root.transform) ?? root.transform;

            Debug.Log($"[MoveGlbSource] Loaded '{Path.GetFileName(glbPath)}' (clip '{clip.name}', {clip.length:F2}s). {report}");
            IsReady = true;
        }

        public bool SampleHumanPose(float seconds, ref HumanPose pose)
        {
            if (!IsReady || handler == null || clip == null || root == null)
                return false;

            float len = clip.length;
            float t = len > 0f ? Mathf.Repeat(seconds, len) : 0f;
            clip.SampleAnimation(root, t);
            handler.GetHumanPose(ref pose);

            if (smoothEnabled && pose.muscles != null)
            {
                float dt = Time.deltaTime > 0f ? Time.deltaTime : 1f / 60f;
                if (muscleFilters == null || muscleFilters.Length != pose.muscles.Length)
                {
                    muscleFilters = new OneEuroFilterScalar[pose.muscles.Length];
                    for (int i = 0; i < muscleFilters.Length; i++)
                        muscleFilters[i] = new OneEuroFilterScalar(smoothMinCutoff, smoothBeta);
                }
                for (int i = 0; i < pose.muscles.Length; i++)
                    pose.muscles[i] = muscleFilters[i].Filter(pose.muscles[i], dt);
                pose.bodyRotation = bodyRotFilter.Filter(pose.bodyRotation, dt);
            }

            return true;
        }

        /// <summary>
        /// Sample the GLB clip at <paramref name="seconds"/> and build root-relative joint offsets in the
        /// GLB skeleton's space (same layout as <see cref="MoveMotion.ForwardKinematics"/>). Used instead of the
        /// baked fusion pose when the cached MOTION_DATA pose is stale but the Move GLB matches ARKit timing.
        /// </summary>
        public bool TryBuildJointOffsets(float seconds, IReadOnlyList<string> jointNames, out Vector3[] offsets)
        {
            offsets = null;
            if (!IsReady || handler == null || clip == null || root == null || avatar == null ||
                jointNames == null || jointNames.Count == 0)
                return false;

            float len = clip.length;
            float t = len > 0f ? Mathf.Repeat(seconds, len) : 0f;
            clip.SampleAnimation(root, t);

            int n = jointNames.Count;
            offsets = new Vector3[n];
            Transform rootBone = ResolveMoveBone(jointNames[0]);
            Vector3 rootPos = rootBone != null ? rootBone.position : (Hips != null ? Hips.position : root.transform.position);
            int mapped = 0;
            for (int i = 0; i < n; i++)
            {
                Transform bone = ResolveMoveBone(jointNames[i]);
                if (bone != null)
                {
                    offsets[i] = bone.position - rootPos;
                    mapped++;
                }
                else
                {
                    offsets[i] = Vector3.zero;
                }
            }

            return mapped >= Mathf.Max(3, n / 3);
        }

        Transform ResolveMoveBone(string moveName)
        {
            if (string.IsNullOrEmpty(moveName) || humanBones == null) return null;
            if (!MoveToHumanBone.TryGetValue(moveName, out var hb)) return null;
            return humanBones.TryGetValue(hb, out var tr) ? tr : null;
        }

        static readonly Dictionary<string, HumanBodyBones> MoveToHumanBone =
            new Dictionary<string, HumanBodyBones>(System.StringComparer.OrdinalIgnoreCase)
        {
            { "Root", HumanBodyBones.Hips },
            { "Left_hip", HumanBodyBones.LeftUpperLeg }, { "Left_knee", HumanBodyBones.LeftLowerLeg },
            { "Left_ankle", HumanBodyBones.LeftFoot }, { "Left_toe", HumanBodyBones.LeftToes },
            { "Right_hip", HumanBodyBones.RightUpperLeg }, { "Right_knee", HumanBodyBones.RightLowerLeg },
            { "Right_ankle", HumanBodyBones.RightFoot }, { "Right_toe", HumanBodyBones.RightToes },
            { "Spine1", HumanBodyBones.Spine }, { "Spine2", HumanBodyBones.Chest }, { "Spine3", HumanBodyBones.UpperChest },
            { "Neck", HumanBodyBones.Neck }, { "Head", HumanBodyBones.Head },
            { "Left_clavicle", HumanBodyBones.LeftShoulder }, { "Left_shoulder", HumanBodyBones.LeftUpperArm },
            { "Left_shoulder_rotation", HumanBodyBones.LeftUpperArm },
            { "Left_elbow", HumanBodyBones.LeftLowerArm }, { "Left_wrist", HumanBodyBones.LeftHand },
            { "Right_clavicle", HumanBodyBones.RightShoulder }, { "Right_shoulder", HumanBodyBones.RightUpperArm },
            { "Right_shoulder_rotation", HumanBodyBones.RightUpperArm },
            { "Right_elbow", HumanBodyBones.RightLowerArm }, { "Right_wrist", HumanBodyBones.RightHand },
        };

        public void Dispose()
        {
            string key = KeyForThis();
            if (key != null)
                cache.Remove(key);
            Cleanup();
        }

        string KeyForThis()
        {
            foreach (var kv in cache)
                if (ReferenceEquals(kv.Value, this))
                    return kv.Key;
            return null;
        }

        static Transform FindHipsTransform(Transform rootTransform)
        {
            foreach (var t in rootTransform.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("hips") || n.Contains("pelvis"))
                    return t;
            }
            return null;
        }

        void Cleanup()
        {
            IsReady = false;
            handler?.Dispose();
            handler = null;
            if (root != null)
                UnityEngine.Object.Destroy(root);
            root = null;
        }
    }
}
