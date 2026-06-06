using UnityEngine;

namespace BodyTracking.Vision
{
    /// <summary>
    /// Topology for the 17-joint skeleton produced by Apple Vision's
    /// VNDetectHumanBodyPose3DRequest. Index order must match the joint array in
    /// Assets/Plugins/iOS/VisionBody/VisionBodyBridge.mm.
    /// </summary>
    public static class VisionBodySkeleton
    {
        public const int JointCount = 17;

        public const int Root = 0;            // pelvis / between hips (hip anchor)
        public const int LeftHip = 1;
        public const int LeftKnee = 2;
        public const int LeftAnkle = 3;
        public const int RightHip = 4;
        public const int RightKnee = 5;
        public const int RightAnkle = 6;
        public const int Spine = 7;
        public const int CenterShoulder = 8;
        public const int LeftShoulder = 9;
        public const int LeftElbow = 10;
        public const int LeftWrist = 11;
        public const int RightShoulder = 12;
        public const int RightElbow = 13;
        public const int RightWrist = 14;
        public const int CenterHead = 15;
        public const int TopHead = 16;

        /// <summary>Parent index per joint for bone drawing; -1 for the root.</summary>
        public static readonly int[] Parents =
        {
            -1,             // 0  Root
            Root,           // 1  LeftHip
            LeftHip,        // 2  LeftKnee
            LeftKnee,       // 3  LeftAnkle
            Root,           // 4  RightHip
            RightHip,       // 5  RightKnee
            RightKnee,      // 6  RightAnkle
            Root,           // 7  Spine
            Spine,          // 8  CenterShoulder
            CenterShoulder, // 9  LeftShoulder
            LeftShoulder,   // 10 LeftElbow
            LeftElbow,      // 11 LeftWrist
            CenterShoulder, // 12 RightShoulder
            RightShoulder,  // 13 RightElbow
            RightElbow,     // 14 RightWrist
            CenterShoulder, // 15 CenterHead
            CenterHead,     // 16 TopHead
        };
    }

    /// <summary>A single Vision joint resolved into Unity world space.</summary>
    public struct VisionWorldJoint
    {
        public Vector3 worldPosition;
        public bool tracked;
    }

    /// <summary>
    /// Latest world-space Vision pose. <see cref="held"/> is true when the data is a
    /// retained last-valid pose (hold-last-valid window) rather than a fresh detection.
    /// </summary>
    public class VisionBodyWorldPose
    {
        public bool valid;
        public bool held;
        public float timestamp;
        public float bodyHeight;
        public int trackedJointCount;
        public readonly VisionWorldJoint[] joints = new VisionWorldJoint[VisionBodySkeleton.JointCount];
    }
}
