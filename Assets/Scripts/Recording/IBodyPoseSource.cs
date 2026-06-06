using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.Recording
{
    /// <summary>
    /// A single body joint in world space, with the topology link used to draw bones and to store the
    /// recording's per-joint samples (parentIndex). World positions are pre-scaled by the source.
    /// </summary>
    public struct BodyPoseJoint
    {
        public int jointIndex;
        public int parentIndex;
        public Vector3 worldPosition;
        public bool isTracked;

        public BodyPoseJoint(int jointIndex, int parentIndex, Vector3 worldPosition, bool isTracked)
        {
            this.jointIndex = jointIndex;
            this.parentIndex = parentIndex;
            this.worldPosition = worldPosition;
            this.isTracked = isTracked;
        }
    }

    /// <summary>
    /// Abstraction over a per-frame skeleton provider. Implementations supply world-space joints and a hip
    /// position; the recorder is responsible for converting those into the RouteRoot-local recording frame.
    /// This lets different body-tracking backends be swapped without touching the recording math/format.
    /// </summary>
    public interface IBodyPoseSource
    {
        /// <summary>Human-readable id for logging/UI (e.g. "ARKit").</summary>
        string SourceName { get; }

        /// <summary>True once the source is ready to be queried.</summary>
        bool IsAvailable { get; }

        /// <summary>Nominal hip joint index in this source's topology (for reference; recorder uses the hip pose out param).</summary>
        int HipJointIndex { get; }

        /// <summary>
        /// Populate <paramref name="jointsOut"/> (cleared first) with the current frame's world-space joints and
        /// output the hip world position. Returns false if no body is tracked this frame.
        /// </summary>
        bool TryGetCurrentPose(List<BodyPoseJoint> jointsOut, out Vector3 hipWorldPosition, out bool hipTracked);

        // Diagnostics surfaced by the recorder UI.
        bool HasTrackedBody { get; }
        int TrackedBodyCount { get; }
        int TrackedJointCount { get; }
    }
}
