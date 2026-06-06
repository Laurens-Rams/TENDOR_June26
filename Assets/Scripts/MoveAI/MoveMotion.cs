using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// One animation frame from Move AI: a root (hips) world-ish position plus a local rotation per joint,
    /// indexed to <see cref="MoveMotion.jointNames"/>.
    /// </summary>
    [Serializable]
    public class MoveMotionFrame
    {
        public float time;
        public Vector3 rootPosition;
        public Quaternion[] localRotations;
        public Vector3[] localPositions; // optional; bone offsets when provided by the source

        public MoveMotionFrame(int jointCount)
        {
            localRotations = new Quaternion[jointCount];
            localPositions = new Vector3[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                localRotations[i] = Quaternion.identity;
                localPositions[i] = Vector3.zero;
            }
        }
    }

    /// <summary>
    /// Parsed Move AI motion clip: a joint topology (names + parents) and per-frame local rotations.
    /// Coordinate convention is normalized to Unity (left-handed, Y-up, meters) by the parser; see
    /// <see cref="MoveMotionParser"/> for the exact assumptions, which must be verified against a real
    /// MOTION_DATA sample.
    /// </summary>
    [Serializable]
    public class MoveMotion
    {
        public float fps = 30f;
        public List<string> jointNames = new List<string>();
        public List<int> jointParents = new List<int>();
        public List<MoveMotionFrame> frames = new List<MoveMotionFrame>();

        public int JointCount => jointNames.Count;
        public int FrameCount => frames.Count;
        public float Duration => frames.Count > 0 && fps > 0 ? frames.Count / fps : 0f;

        public int IndexOfJoint(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < jointNames.Count; i++)
            {
                if (string.Equals(jointNames[i], name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Frame at a given time (seconds), clamped. Nearest-frame (no interpolation) - the fusion baker and
        /// player resample on a fixed grid so blended frames aren't needed here.
        /// </summary>
        public MoveMotionFrame FrameAtTime(float t)
        {
            if (frames.Count == 0) return null;
            int idx = Mathf.Clamp(Mathf.RoundToInt(t * fps), 0, frames.Count - 1);
            return frames[idx];
        }

        /// <summary>
        /// Rest-pose head-to-feet height in the clip's own space, used to compute the uniform scale that
        /// matches the real climber. Falls back to scanning the first frame's joint world offsets. Returns 0
        /// when head/feet joints can't be resolved.
        /// </summary>
        public float EstimateRestHeight(MoveJointMap map)
        {
            if (frames.Count == 0 || map == null) return 0f;

            // Build forward-kinematics world positions for frame 0 to measure head vs feet.
            var world = ForwardKinematics(frames[0]);
            if (world == null) return 0f;

            int headIdx = ResolveAny(map.headJointNames);
            int footIdx = ResolveAny(map.footJointNames);
            if (headIdx < 0 || footIdx < 0) return 0f;

            return Mathf.Abs(world[headIdx].y - world[footIdx].y);
        }

        int ResolveAny(IEnumerable<string> names)
        {
            if (names == null) return -1;
            foreach (var n in names)
            {
                int i = IndexOfJoint(n);
                if (i >= 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// Compute world-space joint positions for a frame by walking the parent hierarchy. The parser stores
        /// <see cref="MoveMotionFrame.localPositions"/> as already-converted world-frame deltas
        /// (childWorld - parentWorld), so positions are reconstructed by accumulating those deltas WITHOUT
        /// re-applying parent rotations — doing so would double-rotate the offsets and bend the skeleton.
        /// Used for height estimation and the compare overlay, not for runtime rig posing (the rig is rotation
        /// driven separately).
        /// </summary>
        public Vector3[] ForwardKinematics(MoveMotionFrame frame)
        {
            if (frame == null) return null;
            int n = JointCount;
            var pos = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                int parent = i < jointParents.Count ? jointParents[i] : -1;
                Vector3 localPos = i < frame.localPositions.Length ? frame.localPositions[i] : Vector3.zero;

                if (parent >= 0 && parent < i)
                    pos[i] = pos[parent] + localPos;
                else
                    pos[i] = frame.rootPosition + localPos;
            }
            return pos;
        }
    }
}
