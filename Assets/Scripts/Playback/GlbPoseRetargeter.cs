using UnityEngine;

namespace BodyTracking.Glb
{
    /// <summary>
    /// Plays a Move AI animation (imported from a GLB, generic rig) and retargets it onto an Avaturn character in
    /// muscle space via <see cref="HumanPoseHandler"/>. Both rigs get a Humanoid avatar from
    /// <see cref="HumanoidAvatarFactory"/>, so body + fingers transfer regardless of proportion differences.
    ///
    /// On top of the retarget it reproduces the two adjustments we tuned against the orange skeleton:
    ///   • Height: uniformly scales the target so its foot->head extent matches the Move source's.
    ///   • Hip placement: pins the target hips to the source hips' world position each frame.
    ///
    /// This is the standalone "does it work" path (GLB-in, GLB-out). The ARKit-fused world placement can be layered
    /// back on later by feeding the recording's hip trajectory into <see cref="hipWorldOverride"/>.
    /// </summary>
    public class GlbPoseRetargeter : MonoBehaviour
    {
        [Header("Source (Move AI GLB instance)")]
        [Tooltip("Instantiated Move AI GLB. Must contain the animated skeleton; its AnimationClip is auto-found.")]
        public GameObject sourceInstance;
        [Tooltip("Optional explicit clip; if null the first clip found on the source is used.")]
        public AnimationClip sourceClip;

        [Header("Target (Avaturn character)")]
        [Tooltip("Avaturn character root. If it already has a valid Humanoid Animator that avatar is reused.")]
        public Animator targetAnimator;

        [Header("Fit")]
        [Tooltip("Match target height to the Move source (foot->head). Disable to keep the character's own size.")]
        public bool matchHeight = true;
        [Range(0.5f, 1.5f)] public float heightFineTune = 1f;
        [Tooltip("Pin the target hips to the Move source hips world position each frame.")]
        public bool pinHips = true;
        [Tooltip("Optional external hip world position (e.g. ARKit-fused). When set, overrides the source hips.")]
        public Vector3? hipWorldOverride = null;

        Avatar sourceAvatar;
        HumanPoseHandler sourceHandler;
        HumanPoseHandler targetHandler;
        HumanPose pose = new HumanPose();
        GameObject sourceRoot;
        float sourceTime;
        Transform sourceHips, targetHips;
        bool ready;

        void OnEnable() => TrySetup();

        void OnDisable()
        {
            sourceHandler?.Dispose();
            targetHandler?.Dispose();
            sourceHandler = null;
            targetHandler = null;
            ready = false;
        }

        public bool TrySetup()
        {
            ready = false;
            if (sourceInstance == null || targetAnimator == null)
            {
                Debug.LogWarning("[GlbPoseRetargeter] Assign sourceInstance and targetAnimator.");
                return false;
            }

            // --- Source humanoid avatar ---
            var srcAnimator = sourceInstance.GetComponentInChildren<Animator>(true);
            GameObject srcRoot = srcAnimator != null ? srcAnimator.gameObject : sourceInstance;

            if (srcAnimator != null && srcAnimator.isHuman && srcAnimator.avatar != null && srcAnimator.avatar.isValid)
            {
                sourceAvatar = srcAnimator.avatar;
            }
            else
            {
                sourceAvatar = HumanoidAvatarFactory.Build(srcRoot, out string srcReport);
                Debug.Log($"[GlbPoseRetargeter] Source avatar: {srcReport}");
                if (sourceAvatar == null) return false;
            }

            // --- Target humanoid avatar ---
            if (!(targetAnimator.isHuman && targetAnimator.avatar != null && targetAnimator.avatar.isValid))
            {
                var tgtAvatar = HumanoidAvatarFactory.Build(targetAnimator.gameObject, out string tgtReport);
                Debug.Log($"[GlbPoseRetargeter] Target avatar: {tgtReport}");
                if (tgtAvatar == null) return false;
                targetAnimator.avatar = tgtAvatar;
            }

            // --- Clip ---
            if (sourceClip == null)
                sourceClip = FindFirstClip(sourceInstance);
            if (sourceClip == null)
            {
                Debug.LogWarning("[GlbPoseRetargeter] No AnimationClip found on the Move source GLB.");
                return false;
            }

            // glTFast clips are legacy transform curves — SampleAnimation works; Playables reject them.
            sourceRoot = srcRoot;
            sourceTime = 0f;

            sourceHandler = new HumanPoseHandler(sourceAvatar, srcRoot.transform);
            targetHandler = new HumanPoseHandler(targetAnimator.avatar, targetAnimator.transform);

            sourceHips = FindHips(sourceAvatar, srcAnimator, srcRoot.transform);
            targetHips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);

            if (matchHeight) ApplyHeightMatch(srcRoot.transform);

            ready = true;
            return true;
        }

        void LateUpdate()
        {
            if (!ready || sourceClip == null || sourceRoot == null) return;

            float len = sourceClip.length;
            if (len > 0f)
            {
                sourceTime = Mathf.Repeat(sourceTime + Time.deltaTime, len);
                sourceClip.SampleAnimation(sourceRoot, sourceTime);
            }

            sourceHandler.GetHumanPose(ref pose);
            targetHandler.SetHumanPose(ref pose);

            if (pinHips && targetHips != null)
            {
                Vector3 hipTarget = hipWorldOverride ?? (sourceHips != null ? sourceHips.position : targetHips.position);
                Vector3 delta = hipTarget - targetHips.position;
                targetAnimator.transform.position += delta;
            }
        }

        void ApplyHeightMatch(Transform srcRoot)
        {
            float srcH = MeasureHeight(srcRoot);
            float tgtH = MeasureHeight(targetAnimator.transform);
            if (srcH > 0.1f && tgtH > 0.1f)
            {
                float s = Mathf.Clamp(srcH / tgtH * heightFineTune, 0.2f, 5f);
                targetAnimator.transform.localScale = Vector3.one * s;
                Debug.Log($"[GlbPoseRetargeter] Height match: source {srcH:F2}m / target {tgtH:F2}m -> scale {s:F3}");
            }
        }

        static float MeasureHeight(Transform root)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var b = r.bounds;
                minY = Mathf.Min(minY, b.min.y);
                maxY = Mathf.Max(maxY, b.max.y);
            }
            return maxY - minY;
        }

        static Transform FindHips(Avatar avatar, Animator animator, Transform root)
        {
            if (animator != null && animator.isHuman)
            {
                var h = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (h != null) return h;
            }
            return root;
        }

        static AnimationClip FindFirstClip(GameObject go)
        {
            var anim = go.GetComponentInChildren<UnityEngine.Animation>(true);
            if (anim != null)
                foreach (AnimationState st in anim)
                    if (st.clip != null) return st.clip;

#if UNITY_EDITOR
            // GLB import stores clips as sub-assets of the imported model.
            string path = UnityEditor.AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(path))
                foreach (var a in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
                    if (a is AnimationClip clip && !clip.name.StartsWith("__preview")) return clip;
#endif
            return null;
        }
    }
}
