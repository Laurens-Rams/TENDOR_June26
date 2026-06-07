using System.Collections.Generic;
using System.IO;
using System.Text;
using BodyTracking.Glb;
using BodyTracking.Playback;
using UnityEditor;
using UnityEngine;

namespace BodyTracking.EditorTools
{
    /// <summary>
    /// Automated, no-device smoke test for the GLB retarget pipeline (the new Move-GLB path). It exercises the
    /// pieces that can be verified in the editor against the real imported .glb assets:
    ///   • glTFast present.
    ///   • Each .glb builds a valid Humanoid <see cref="Avatar"/> via <see cref="HumanoidAvatarFactory"/>.
    ///   • The Move-source .glb carries an AnimationClip with a sane length.
    ///   • Body + finger bones resolve on both rigs.
    ///   • A muscle-space retarget (source clip -> HumanPose -> target) runs without exceptions and actually
    ///     moves the target's fingers (proving Dex hand tracking survives the transfer).
    ///   • The scene has the runtime wiring (FusedCharacterPlayer + MoveAIFusionCoordinator).
    ///
    /// Runtime-only behaviour (async glTFast load from persistentDataPath, ARKit world placement, the live
    /// Move->RouteRoot yaw alignment) can't run in edit mode — those are covered by the on-device checklist in
    /// <c>Assets/_Audit/test-plan.md</c>. Results are logged to the Console and summarised in a dialog.
    /// </summary>
    public static class GlbPipelineValidator
    {
        [MenuItem("TENDOR/GLB/Validate Pipeline", priority = 1)]
        public static void Validate()
        {
            var log = new StringBuilder();
            int pass = 0, fail = 0, warn = 0;

            void Pass(string m) { pass++; log.AppendLine("  [PASS] " + m); }
            void Fail(string m) { fail++; log.AppendLine("  [FAIL] " + m); }
            void Warn(string m) { warn++; log.AppendLine("  [WARN] " + m); }

            log.AppendLine("=== GLB Pipeline Validation ===\n");

            // 1) glTFast available.
            log.AppendLine("1. Packages");
            bool hasGltfast = System.Type.GetType("GLTFast.GltfImport, glTFast") != null;
            if (hasGltfast) Pass("glTFast runtime assembly is referenced.");
            else Fail("glTFast (com.unity.cloud.gltfast) not found — runtime GLB load will fail.");

            // 2) Discover and classify the .glb assets in the project.
            log.AppendLine("\n2. GLB assets");
            var glbs = FindGlbAssets();
            if (glbs.Count == 0)
                Warn("No .glb assets found under Assets/. Import the Move AI + Avaturn GLBs to validate them here.");

            GameObject movePrefab = null, characterPrefab = null;
            AnimationClip moveClip = null;
            foreach (var path in glbs)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var clip = LoadClip(path);
                bool hasSkin = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
                log.AppendLine($"  • {Path.GetFileName(path)} — clip:{(clip != null ? clip.name : "none")} skinned:{hasSkin}");
                if (clip != null && (movePrefab == null || clip.length > (moveClip != null ? moveClip.length : 0f)))
                {
                    movePrefab = prefab; moveClip = clip;            // the animated one = Move source
                }
                if (hasSkin && clip == null && characterPrefab == null)
                    characterPrefab = prefab;                        // skinned, no animation = character
            }
            if (characterPrefab == null) characterPrefab = movePrefab; // degrade gracefully

            // 3) Move source clip sanity.
            log.AppendLine("\n3. Move AI animation clip");
            if (moveClip == null) Fail("No AnimationClip found on any GLB — the Move source must contain animation.");
            else if (moveClip.length < 0.1f) Fail($"Move clip '{moveClip.name}' is too short ({moveClip.length:F2}s).");
            else Pass($"Move clip '{moveClip.name}' OK ({moveClip.length:F2}s).");

            // 4) Humanoid avatar build + body/finger coverage for both rigs.
            log.AppendLine("\n4. Humanoid avatar build");
            Avatar moveAvatar = ValidateRig(movePrefab, "Move source", log, Pass, Fail, Warn, out GameObject moveTemp);
            Avatar charAvatar = ValidateRig(characterPrefab, "Character", log, Pass, Fail, Warn, out GameObject charTemp);

            // 5) Muscle-space retarget sanity (fingers actually move on the target).
            log.AppendLine("\n5. Muscle-space retarget");
            if (moveAvatar != null && charAvatar != null && moveClip != null && moveTemp != null && charTemp != null)
                RunRetargetSample(moveTemp, moveAvatar, moveClip, charTemp, charAvatar, log, Pass, Fail, Warn);
            else
                Warn("Skipped retarget sample (need both a valid Move source avatar+clip and a character avatar).");

            if (moveTemp != null) Object.DestroyImmediate(moveTemp);
            if (charTemp != null && charTemp != moveTemp) Object.DestroyImmediate(charTemp);

            // 6) Scene runtime wiring.
            log.AppendLine("\n6. Scene wiring");
            var fused = Object.FindObjectOfType<FusedCharacterPlayer>(true);
            var coord = Object.FindObjectOfType<MoveAI.MoveAIFusionCoordinator>(true);
            if (fused != null) Pass("FusedCharacterPlayer present in the open scene.");
            else Warn("No FusedCharacterPlayer in the open scene (open the playback scene to validate wiring).");
            if (coord != null) Pass("MoveAIFusionCoordinator present in the open scene.");
            else Warn("No MoveAIFusionCoordinator in the open scene.");

            log.Insert(0, $"RESULT: {pass} passed, {fail} failed, {warn} warnings.\n\n");
            string full = log.ToString();
            if (fail > 0) Debug.LogError("[GlbPipelineValidator]\n" + full);
            else if (warn > 0) Debug.LogWarning("[GlbPipelineValidator]\n" + full);
            else Debug.Log("[GlbPipelineValidator]\n" + full);

            EditorUtility.DisplayDialog("GLB Pipeline Validation",
                $"{pass} passed, {fail} failed, {warn} warnings.\n\nFull report in the Console.",
                fail > 0 ? "Fix it" : "OK");
        }

        static Avatar ValidateRig(GameObject prefab, string label, StringBuilder log,
            System.Action<string> Pass, System.Action<string> Fail, System.Action<string> Warn, out GameObject temp)
        {
            temp = null;
            if (prefab == null) { Warn($"{label}: no prefab to validate."); return null; }

            temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var avatar = HumanoidAvatarFactory.Build(temp, out string report);
            if (avatar == null || !avatar.isValid)
            {
                Fail($"{label}: humanoid build failed — {report.Replace('\n', ' ')}");
                return null;
            }

            // Assign to an Animator so we can query bone coverage by HumanBodyBones.
            var anim = temp.GetComponentInChildren<Animator>(true) ?? temp.AddComponent<Animator>();
            anim.avatar = avatar;

            int fingers = 0;
            foreach (var b in FingerBones)
                if (anim.GetBoneTransform(b) != null) fingers++;

            bool hasToes = anim.GetBoneTransform(HumanBodyBones.LeftToes) != null ||
                           anim.GetBoneTransform(HumanBodyBones.RightToes) != null;

            Pass($"{label}: humanoid avatar valid. Fingers mapped: {fingers}/{FingerBones.Length}, toes:{hasToes}.");
            if (fingers == 0) Warn($"{label}: no finger bones mapped — Dex hand motion won't transfer for this rig.");
            return avatar;
        }

        static void RunRetargetSample(GameObject src, Avatar srcAvatar, AnimationClip clip,
            GameObject tgt, Avatar tgtAvatar, StringBuilder log,
            System.Action<string> Pass, System.Action<string> Fail, System.Action<string> Warn)
        {
            HumanPoseHandler srcHandler = null, tgtHandler = null;
            try
            {
                srcHandler = new HumanPoseHandler(srcAvatar, src.transform);
                tgtHandler = new HumanPoseHandler(tgtAvatar, tgt.transform);

                var tgtAnim = tgt.GetComponent<Animator>();
                Transform finger = tgtAnim != null ? tgtAnim.GetBoneTransform(HumanBodyBones.RightIndexProximal) : null;
                Quaternion before = finger != null ? finger.localRotation : Quaternion.identity;

                // Make the source GENERIC before sampling: ValidateRig assigned a humanoid avatar to count bones,
                // but a humanoid Animator would route this generic transform-curve clip through the muscle system
                // and bake out to rest. This mirrors MoveGlbSource, which keeps its playback Animator avatar-less.
                var srcAnim = src.GetComponent<Animator>();
                if (srcAnim != null) srcAnim.avatar = null;

                // Sample the source clip at a mid-frame (more likely a non-rest pose) and transfer it.
                var pose = new HumanPose();
                clip.SampleAnimation(src, clip.length * 0.5f);
                srcHandler.GetHumanPose(ref pose);

                if (pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount)
                {
                    Fail($"Source HumanPose has {pose.muscles?.Length ?? 0} muscles (expected {HumanTrait.MuscleCount}).");
                    return;
                }

                float maxMuscle = 0f;
                foreach (var m in pose.muscles) maxMuscle = Mathf.Max(maxMuscle, Mathf.Abs(m));
                if (maxMuscle < 1e-4f) Warn("Sampled Move pose is ~rest (all muscles ~0) — clip may be a T-pose at this frame.");
                else Pass($"Move pose sampled OK (max muscle {maxMuscle:F2}).");

                tgtHandler.SetHumanPose(ref pose);
                Pass("SetHumanPose applied to the character without exceptions (body + fingers).");

                if (finger != null)
                {
                    float delta = Quaternion.Angle(before, finger.localRotation);
                    if (delta > 0.5f) Pass($"Target finger responded to the retarget ({delta:F1}° change).");
                    else Warn($"Target finger barely moved ({delta:F1}°) — check finger mapping if fingers look static.");
                }
                else Warn("Character has no RightIndexProximal bone — finger transfer can't be verified.");
            }
            catch (System.Exception e)
            {
                Fail("Retarget threw: " + e.Message);
            }
            finally
            {
                srcHandler?.Dispose();
                tgtHandler?.Dispose();
            }
        }

        static List<string> FindGlbAssets()
        {
            var result = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:GameObject"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".glb", System.StringComparison.OrdinalIgnoreCase))
                    result.Add(path);
            }
            return result;
        }

        static AnimationClip LoadClip(string assetPath)
        {
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview"))
                    return clip;
            return null;
        }

        static readonly HumanBodyBones[] FingerBones =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftLittleProximal,
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal,
        };
    }
}
