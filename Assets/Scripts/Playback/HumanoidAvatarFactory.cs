using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace BodyTracking.Glb
{
    /// <summary>
    /// Builds a Unity Humanoid <see cref="Avatar"/> at runtime from a generic skeleton (e.g. a GLB imported by
    /// glTFast, which always comes in as Generic). With a valid humanoid avatar on both the Move AI animation rig
    /// and the Avaturn character rig, motion can be transferred between them in muscle space via
    /// <see cref="HumanPoseHandler"/> — which carries fingers and is independent of proportion differences.
    ///
    /// Bone matching is name-based and tolerant of the common conventions (Mixamo "mixamorig:LeftHand",
    /// Ready-Player-Me / Avaturn "LeftHand", glTF "left_hand", etc.) by normalizing names (lowercase, strip
    /// separators) and matching on exact-or-suffix. Use <see cref="ReportBones"/> to dump a hierarchy and see
    /// which human bones resolved before relying on the result.
    /// </summary>
    public static class HumanoidAvatarFactory
    {
        /// <summary>Human bones that must resolve or the avatar is rejected as invalid by Unity.</summary>
        static readonly HumanBodyBones[] Required =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
        };

        /// <summary>
        /// Attempt to build a humanoid avatar rooted at <paramref name="root"/>. Returns null and fills
        /// <paramref name="report"/> when required bones are missing.
        /// </summary>
        public static Avatar Build(GameObject root, out string report)
        {
            var sb = new StringBuilder();
            if (root == null) { report = "No root."; return null; }

            // Normalized transform name -> transform. Last-wins is fine; duplicates are rare in these rigs.
            var byName = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                byName[Normalize(t.name)] = t;

            var human = new List<HumanBone>();
            var resolved = new Dictionary<HumanBodyBones, Transform>();

            foreach (var kv in Candidates)
            {
                if (TryMatch(byName, kv.Value, out var bone))
                {
                    resolved[kv.Key] = bone;
                    human.Add(new HumanBone
                    {
                        humanName = HumanTrait.BoneName[(int)kv.Key],
                        boneName = bone.name,
                        limit = new HumanLimit { useDefaultValues = true }
                    });
                }
            }

            var missing = new List<string>();
            foreach (var req in Required)
                if (!resolved.ContainsKey(req))
                    missing.Add(req.ToString());

            if (missing.Count > 0)
            {
                sb.AppendLine($"Humanoid build FAILED: {missing.Count} required bone(s) unmatched:");
                sb.AppendLine(" - " + string.Join(", ", missing));
                sb.AppendLine("Run 'Report Bones' to see the rig's actual names, then extend HumanoidAvatarFactory.Candidates.");
                report = sb.ToString();
                return null;
            }

            var skeleton = new List<SkeletonBone>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale
                });
            }

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            if (avatar == null || !avatar.isValid)
            {
                report = "Humanoid build FAILED: AvatarBuilder returned an invalid avatar (check bone hierarchy / T-pose).";
                return null;
            }

            avatar.name = root.name + "_HumanoidAvatar";
            sb.AppendLine($"Humanoid avatar built: {human.Count} bones mapped ({resolved.Count} human bones, fingers included where present).");
            report = sb.ToString();
            return avatar;
        }

        /// <summary>Dump the hierarchy and which human bones each transform would satisfy — for tuning the map.</summary>
        public static string ReportBones(GameObject root)
        {
            if (root == null) return "No root.";
            var sb = new StringBuilder();
            sb.AppendLine($"Bone report for '{root.name}':");
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                sb.AppendLine($"  {t.name}");
            return sb.ToString();
        }

        static bool TryMatch(Dictionary<string, Transform> byName, string[] candidates, out Transform match)
        {
            // Exact normalized match first.
            foreach (var c in candidates)
                if (byName.TryGetValue(c, out match))
                    return true;

            // Then suffix match (handles prefixes like "mixamorig:" or "Armature/").
            foreach (var c in candidates)
            {
                Transform best = null;
                foreach (var kv in byName)
                    if (kv.Key.EndsWith(c) && (best == null || kv.Key.Length < Normalize(best.name).Length))
                        best = kv.Value;
                if (best != null) { match = best; return true; }
            }

            match = null;
            return false;
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        // HumanBodyBones -> normalized candidate transform-name tokens, covering Mixamo / RPM-Avaturn / glTF naming.
        static readonly Dictionary<HumanBodyBones, string[]> Candidates = new Dictionary<HumanBodyBones, string[]>
        {
            { HumanBodyBones.Hips, new[]{ "hips", "pelvis", "root" } },
            { HumanBodyBones.Spine, new[]{ "spine", "spine1" } },
            { HumanBodyBones.Chest, new[]{ "spine1", "chest", "spine2" } },
            { HumanBodyBones.UpperChest, new[]{ "spine2", "upperchest", "spine3" } },
            { HumanBodyBones.Neck, new[]{ "neck" } },
            { HumanBodyBones.Head, new[]{ "head" } },
            { HumanBodyBones.LeftEye, new[]{ "lefteye", "eyel", "leye" } },
            { HumanBodyBones.RightEye, new[]{ "righteye", "eyer", "reye" } },

            { HumanBodyBones.LeftUpperLeg, new[]{ "leftupleg", "lefthip", "leftupperleg", "lthigh", "upperleg_l" } },
            { HumanBodyBones.LeftLowerLeg, new[]{ "leftleg", "leftknee", "leftlowerleg", "lshin", "lowerleg_l" } },
            { HumanBodyBones.LeftFoot, new[]{ "leftfoot", "leftankle", "foot_l" } },
            { HumanBodyBones.LeftToes, new[]{ "lefttoebase", "lefttoe", "lefttoes", "toe_l" } },
            { HumanBodyBones.RightUpperLeg, new[]{ "rightupleg", "righthip", "rightupperleg", "rthigh", "upperleg_r" } },
            { HumanBodyBones.RightLowerLeg, new[]{ "rightleg", "rightknee", "rightlowerleg", "rshin", "lowerleg_r" } },
            { HumanBodyBones.RightFoot, new[]{ "rightfoot", "rightankle", "foot_r" } },
            { HumanBodyBones.RightToes, new[]{ "righttoebase", "righttoe", "righttoes", "toe_r" } },

            { HumanBodyBones.LeftShoulder, new[]{ "leftshoulder", "leftclavicle", "shoulder_l" } },
            { HumanBodyBones.LeftUpperArm, new[]{ "leftarm", "leftupperarm", "leftshoulderrotation", "upperarm_l" } },
            { HumanBodyBones.LeftLowerArm, new[]{ "leftforearm", "leftelbow", "leftlowerarm", "lowerarm_l" } },
            { HumanBodyBones.LeftHand, new[]{ "lefthand", "leftwrist", "hand_l" } },
            { HumanBodyBones.RightShoulder, new[]{ "rightshoulder", "rightclavicle", "shoulder_r" } },
            { HumanBodyBones.RightUpperArm, new[]{ "rightarm", "rightupperarm", "rightshoulderrotation", "upperarm_r" } },
            { HumanBodyBones.RightLowerArm, new[]{ "rightforearm", "rightelbow", "rightlowerarm", "lowerarm_r" } },
            { HumanBodyBones.RightHand, new[]{ "righthand", "rightwrist", "hand_r" } },

            // Fingers (Dex hand tracking). Mixamo: LeftHandThumb1.. ; RPM/Avaturn: LeftHandThumb1 too.
            { HumanBodyBones.LeftThumbProximal, new[]{ "lefthandthumb1", "leftthumb1", "thumb1_l" } },
            { HumanBodyBones.LeftThumbIntermediate, new[]{ "lefthandthumb2", "leftthumb2", "thumb2_l" } },
            { HumanBodyBones.LeftThumbDistal, new[]{ "lefthandthumb3", "leftthumb3", "thumb3_l" } },
            { HumanBodyBones.LeftIndexProximal, new[]{ "lefthandindex1", "leftindex1", "index1_l" } },
            { HumanBodyBones.LeftIndexIntermediate, new[]{ "lefthandindex2", "leftindex2", "index2_l" } },
            { HumanBodyBones.LeftIndexDistal, new[]{ "lefthandindex3", "leftindex3", "index3_l" } },
            { HumanBodyBones.LeftMiddleProximal, new[]{ "lefthandmiddle1", "leftmiddle1", "middle1_l" } },
            { HumanBodyBones.LeftMiddleIntermediate, new[]{ "lefthandmiddle2", "leftmiddle2", "middle2_l" } },
            { HumanBodyBones.LeftMiddleDistal, new[]{ "lefthandmiddle3", "leftmiddle3", "middle3_l" } },
            { HumanBodyBones.LeftRingProximal, new[]{ "lefthandring1", "leftring1", "ring1_l" } },
            { HumanBodyBones.LeftRingIntermediate, new[]{ "lefthandring2", "leftring2", "ring2_l" } },
            { HumanBodyBones.LeftRingDistal, new[]{ "lefthandring3", "leftring3", "ring3_l" } },
            { HumanBodyBones.LeftLittleProximal, new[]{ "lefthandpinky1", "leftlittle1", "pinky1_l" } },
            { HumanBodyBones.LeftLittleIntermediate, new[]{ "lefthandpinky2", "leftlittle2", "pinky2_l" } },
            { HumanBodyBones.LeftLittleDistal, new[]{ "lefthandpinky3", "leftlittle3", "pinky3_l" } },

            { HumanBodyBones.RightThumbProximal, new[]{ "righthandthumb1", "rightthumb1", "thumb1_r" } },
            { HumanBodyBones.RightThumbIntermediate, new[]{ "righthandthumb2", "rightthumb2", "thumb2_r" } },
            { HumanBodyBones.RightThumbDistal, new[]{ "righthandthumb3", "rightthumb3", "thumb3_r" } },
            { HumanBodyBones.RightIndexProximal, new[]{ "righthandindex1", "rightindex1", "index1_r" } },
            { HumanBodyBones.RightIndexIntermediate, new[]{ "righthandindex2", "rightindex2", "index2_r" } },
            { HumanBodyBones.RightIndexDistal, new[]{ "righthandindex3", "rightindex3", "index3_r" } },
            { HumanBodyBones.RightMiddleProximal, new[]{ "righthandmiddle1", "rightmiddle1", "middle1_r" } },
            { HumanBodyBones.RightMiddleIntermediate, new[]{ "righthandmiddle2", "rightmiddle2", "middle2_r" } },
            { HumanBodyBones.RightMiddleDistal, new[]{ "righthandmiddle3", "rightmiddle3", "middle3_r" } },
            { HumanBodyBones.RightRingProximal, new[]{ "righthandring1", "rightring1", "ring1_r" } },
            { HumanBodyBones.RightRingIntermediate, new[]{ "righthandring2", "rightring2", "ring2_r" } },
            { HumanBodyBones.RightRingDistal, new[]{ "righthandring3", "rightring3", "ring3_r" } },
            { HumanBodyBones.RightLittleProximal, new[]{ "righthandpinky1", "rightlittle1", "pinky1_r" } },
            { HumanBodyBones.RightLittleIntermediate, new[]{ "righthandpinky2", "rightlittle2", "pinky2_r" } },
            { HumanBodyBones.RightLittleDistal, new[]{ "righthandpinky3", "rightlittle3", "pinky3_r" } },
        };
    }
}
