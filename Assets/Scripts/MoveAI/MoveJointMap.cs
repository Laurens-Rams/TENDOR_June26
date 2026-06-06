using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Maps Move AI joint names to character bone names, and lists candidate head/foot joint names used for
    /// height-based scaling. Defaults cover common Move AI / Mixamo / humanoid naming; override in the
    /// inspector once the real MOTION_DATA joint names are confirmed against a sample export.
    /// </summary>
    [Serializable]
    public class MoveJointMap
    {
        [Serializable]
        public struct Entry
        {
            [Tooltip("Joint name as it appears in the Move AI MOTION_DATA.")]
            public string moveJoint;
            [Tooltip("Bone name on the target character rig (searched under the character root).")]
            public string bone;
        }

        [Tooltip("Move-joint -> character-bone pairs. Only mapped joints are driven; others are ignored.")]
        public List<Entry> entries = new List<Entry>();

        [Tooltip("Candidate Move joint names for the head (first match wins) - used for head/feet scaling.")]
        public List<string> headJointNames = new List<string> { "Head", "Neck", "head", "mixamorig:Head", "head_end" };

        [Tooltip("Candidate Move joint names for a foot/toe (first match wins) - used for head/feet scaling.")]
        public List<string> footJointNames = new List<string>
        {
            "Left_toe", "Right_toe", "Left_ankle", "Right_ankle",
            "LeftToeBase", "RightToeBase", "LeftFoot", "RightFoot",
            "mixamorig:LeftToeBase", "left_foot", "right_foot"
        };

        [Tooltip("Candidate Move joint names for the root/hips (first match wins).")]
        public List<string> rootJointNames = new List<string> { "Root", "Hips", "hips", "mixamorig:Hips", "Pelvis", "root" };

        /// <summary>
        /// Default Mixamo-style mapping (Move-name -> Mixamo bone). Used when no explicit entries are provided.
        /// </summary>
        public static MoveJointMap CreateDefaultMixamo()
        {
            var map = new MoveJointMap();
            string[,] pairs =
            {
                // Move API biomechanical names (underscore) + legacy Mixamo-style fallbacks
                {"Root","mixamorig:Hips"},
                {"Left_hip","mixamorig:LeftUpLeg"}, {"Left_knee","mixamorig:LeftLeg"}, {"Left_ankle","mixamorig:LeftFoot"}, {"Left_toe","mixamorig:LeftToeBase"},
                {"Right_hip","mixamorig:RightUpLeg"}, {"Right_knee","mixamorig:RightLeg"}, {"Right_ankle","mixamorig:RightFoot"}, {"Right_toe","mixamorig:RightToeBase"},
                {"Spine1","mixamorig:Spine"}, {"Spine2","mixamorig:Spine1"}, {"Spine3","mixamorig:Spine2"}, {"Neck","mixamorig:Neck"}, {"Head","mixamorig:Head"},
                {"Left_clavicle","mixamorig:LeftShoulder"}, {"Left_shoulder","mixamorig:LeftArm"}, {"Left_shoulder_rotation","mixamorig:LeftArm"},
                {"Left_elbow","mixamorig:LeftForeArm"}, {"Left_wrist","mixamorig:LeftHand"},
                {"Right_clavicle","mixamorig:RightShoulder"}, {"Right_shoulder","mixamorig:RightArm"}, {"Right_shoulder_rotation","mixamorig:RightArm"},
                {"Right_elbow","mixamorig:RightForeArm"}, {"Right_wrist","mixamorig:RightHand"},
                // Legacy names
                {"Hips","mixamorig:Hips"},
                {"Spine","mixamorig:Spine"},
                {"LeftUpLeg","mixamorig:LeftUpLeg"},
                {"LeftLeg","mixamorig:LeftLeg"},
                {"LeftFoot","mixamorig:LeftFoot"},
                {"LeftToeBase","mixamorig:LeftToeBase"},
                {"RightUpLeg","mixamorig:RightUpLeg"},
                {"RightLeg","mixamorig:RightLeg"},
                {"RightFoot","mixamorig:RightFoot"},
                {"RightToeBase","mixamorig:RightToeBase"},
                {"LeftShoulder","mixamorig:LeftShoulder"},
                {"LeftArm","mixamorig:LeftArm"},
                {"LeftForeArm","mixamorig:LeftForeArm"},
                {"LeftHand","mixamorig:LeftHand"},
                {"RightShoulder","mixamorig:RightShoulder"},
                {"RightArm","mixamorig:RightArm"},
                {"RightForeArm","mixamorig:RightForeArm"},
                {"RightHand","mixamorig:RightHand"},
            };
            for (int i = 0; i < pairs.GetLength(0); i++)
                map.entries.Add(new Entry { moveJoint = pairs[i, 0], bone = pairs[i, 1] });
            return map;
        }

        public bool TryGetBone(string moveJoint, out string bone)
        {
            bone = null;
            for (int i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].moveJoint, moveJoint, StringComparison.OrdinalIgnoreCase))
                {
                    bone = entries[i].bone;
                    return !string.IsNullOrEmpty(bone);
                }
            }
            return false;
        }

        /// <summary>
        /// Move AI biomechanical joint names mapped to Unity humanoid bones. Used by fused playback and the
        /// character FBX setup tool to build a rig-specific <see cref="MoveJointMap"/>.
        /// </summary>
        public static IReadOnlyDictionary<string, HumanBodyBones> GetMoveToHumanBodyBoneMap()
        {
            return MoveToHumanBodyBone;
        }

        /// <summary>
        /// Builds a Move-joint → rig-bone-name map from a humanoid avatar on the FBX prefab root.
        /// Falls back to <see cref="CreateDefaultMixamo"/> when the rig is not humanoid.
        /// </summary>
        public static MoveJointMap BuildFromHumanoidRig(GameObject prefabRoot, out string report)
        {
            if (prefabRoot == null)
            {
                report = "No prefab root.";
                return CreateDefaultMixamo();
            }

            var animator = prefabRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.isHuman || !animator.avatar.isValid)
            {
                report = "Rig is not a valid humanoid avatar — using default Mixamo bone names as fallback.";
                return CreateDefaultMixamo();
            }

            var map = new MoveJointMap
            {
                headJointNames = CreateDefaultMixamo().headJointNames,
                footJointNames = CreateDefaultMixamo().footJointNames,
                rootJointNames = CreateDefaultMixamo().rootJointNames,
            };

            int mapped = 0;
            foreach (var kv in MoveToHumanBodyBone)
            {
                var bone = animator.GetBoneTransform(kv.Value);
                if (bone == null)
                    continue;

                map.entries.Add(new Entry { moveJoint = kv.Key, bone = bone.name });
                mapped++;
            }

            report = mapped > 0
                ? $"Mapped {mapped} Move joints to humanoid bones on '{prefabRoot.name}'."
                : "Humanoid avatar valid but no bones resolved — using default Mixamo map.";
            return mapped > 0 ? map : CreateDefaultMixamo();
        }

        /// <summary>Best hip/pelvis transform name for <see cref="Animation.FBXCharacterController"/> auto-find.</summary>
        public static string GetPrimaryHipBoneName(GameObject prefabRoot)
        {
            var animator = prefabRoot != null ? prefabRoot.GetComponentInChildren<Animator>(true) : null;
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                    return hips.name;
            }
            return "mixamorig:Hips";
        }

        static readonly Dictionary<string, HumanBodyBones> MoveToHumanBodyBone =
            new Dictionary<string, HumanBodyBones>(StringComparer.OrdinalIgnoreCase)
            {
                { "Root", HumanBodyBones.Hips },
                { "Hips", HumanBodyBones.Hips },
                { "Left_hip", HumanBodyBones.LeftUpperLeg },
                { "LeftUpLeg", HumanBodyBones.LeftUpperLeg },
                { "Left_knee", HumanBodyBones.LeftLowerLeg },
                { "LeftLeg", HumanBodyBones.LeftLowerLeg },
                { "Left_ankle", HumanBodyBones.LeftFoot },
                { "LeftFoot", HumanBodyBones.LeftFoot },
                { "Left_toe", HumanBodyBones.LeftToes },
                { "LeftToeBase", HumanBodyBones.LeftToes },
                { "Right_hip", HumanBodyBones.RightUpperLeg },
                { "RightUpLeg", HumanBodyBones.RightUpperLeg },
                { "Right_knee", HumanBodyBones.RightLowerLeg },
                { "RightLeg", HumanBodyBones.RightLowerLeg },
                { "Right_ankle", HumanBodyBones.RightFoot },
                { "RightFoot", HumanBodyBones.RightFoot },
                { "Right_toe", HumanBodyBones.RightToes },
                { "RightToeBase", HumanBodyBones.RightToes },
                { "Spine", HumanBodyBones.Spine },
                { "Spine1", HumanBodyBones.Spine },
                { "Spine2", HumanBodyBones.Chest },
                { "Spine3", HumanBodyBones.UpperChest },
                { "Neck", HumanBodyBones.Neck },
                { "Head", HumanBodyBones.Head },
                { "Left_clavicle", HumanBodyBones.LeftShoulder },
                { "LeftShoulder", HumanBodyBones.LeftShoulder },
                { "Left_shoulder", HumanBodyBones.LeftUpperArm },
                { "Left_shoulder_rotation", HumanBodyBones.LeftUpperArm },
                { "LeftArm", HumanBodyBones.LeftUpperArm },
                { "Left_elbow", HumanBodyBones.LeftLowerArm },
                { "LeftForeArm", HumanBodyBones.LeftLowerArm },
                { "Left_wrist", HumanBodyBones.LeftHand },
                { "LeftHand", HumanBodyBones.LeftHand },
                { "Right_clavicle", HumanBodyBones.RightShoulder },
                { "RightShoulder", HumanBodyBones.RightShoulder },
                { "Right_shoulder", HumanBodyBones.RightUpperArm },
                { "Right_shoulder_rotation", HumanBodyBones.RightUpperArm },
                { "RightArm", HumanBodyBones.RightUpperArm },
                { "Right_elbow", HumanBodyBones.RightLowerArm },
                { "RightForeArm", HumanBodyBones.RightLowerArm },
                { "Right_wrist", HumanBodyBones.RightHand },
                { "RightHand", HumanBodyBones.RightHand },
            };
    }
}
