using UnityEngine;

namespace BodyTracking.AI
{
    /// <summary>
    /// One BlazePose landmark in normalized image space plus the model's relative depth and confidences.
    /// </summary>
    public struct BlazeLandmark
    {
        /// <summary>Normalized image position in [0,1], origin bottom-left of the camera texture.</summary>
        public Vector2 imageUV;
        /// <summary>BlazePose depth (texture-pixel units, ~centered on the hips). Used as a depth fallback.</summary>
        public float zRelative;
        public float visibility;
        public float presence;
        public bool tracked;
    }

    /// <summary>
    /// Latest BlazePose inference result (33 keypoints). Reused/mutated each frame by <see cref="BlazePoseRunner"/>.
    /// </summary>
    public class BlazePoseResult
    {
        public bool valid;
        public float score;
        public float textureWidth;
        public float textureHeight;
        public int frameId;
        public readonly BlazeLandmark[] landmarks = new BlazeLandmark[BlazePoseSkeleton.NumKeypoints];
    }

    /// <summary>
    /// MediaPipe BlazePose 33-keypoint topology: stable indices, a parent tree for bone/skeleton drawing,
    /// and the hip landmarks used to derive the recording root.
    /// </summary>
    public static class BlazePoseSkeleton
    {
        public const int NumKeypoints = 33;

        // Named indices (MediaPipe Pose).
        public const int Nose = 0;
        public const int LeftShoulder = 11;
        public const int RightShoulder = 12;
        public const int LeftHip = 23;
        public const int RightHip = 24;

        /// <summary>
        /// Parent index per landmark forming a single tree rooted at the right hip (parent = -1). Bones are
        /// drawn from each joint to its parent. Not a strict anatomical hierarchy - chosen so the whole body
        /// renders as one connected skeleton.
        /// </summary>
        public static readonly int[] Parents =
        {
            12, // 0  nose -> right shoulder (attaches head to torso)
            0,  // 1  left eye inner -> nose
            1,  // 2  left eye -> left eye inner
            2,  // 3  left eye outer -> left eye
            0,  // 4  right eye inner -> nose
            4,  // 5  right eye -> right eye inner
            5,  // 6  right eye outer -> right eye
            3,  // 7  left ear -> left eye outer
            6,  // 8  right ear -> right eye outer
            0,  // 9  mouth left -> nose
            0,  // 10 mouth right -> nose
            23, // 11 left shoulder -> left hip
            24, // 12 right shoulder -> right hip
            11, // 13 left elbow -> left shoulder
            12, // 14 right elbow -> right shoulder
            13, // 15 left wrist -> left elbow
            14, // 16 right wrist -> right elbow
            15, // 17 left pinky -> left wrist
            16, // 18 right pinky -> right wrist
            15, // 19 left index -> left wrist
            16, // 20 right index -> right wrist
            15, // 21 left thumb -> left wrist
            16, // 22 right thumb -> right wrist
            24, // 23 left hip -> right hip
            -1, // 24 right hip (root)
            23, // 25 left knee -> left hip
            24, // 26 right knee -> right hip
            25, // 27 left ankle -> left knee
            26, // 28 right ankle -> right knee
            27, // 29 left heel -> left ankle
            28, // 30 right heel -> right ankle
            27, // 31 left foot index -> left ankle
            28, // 32 right foot index -> right ankle
        };
    }
}
