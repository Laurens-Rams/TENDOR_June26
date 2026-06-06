using System.Collections.Generic;
using BodyTracking.AI;
using UnityEngine;

namespace BodyTracking.Recording
{
    /// <summary>
    /// <see cref="IBodyPoseSource"/> backed by the BlazePose -> LiDAR depth-lift pipeline. Reads the latest
    /// world-space pose from <see cref="BlazePoseDepthLift"/> and maps the 33 MediaPipe landmarks into the
    /// recorder's generic joint list. The hip is the midpoint of landmarks 23 (left) and 24 (right).
    /// </summary>
    public class BlazePoseBodyPoseSource : IBodyPoseSource
    {
        readonly BlazePoseDepthLift depthLift;

        bool hasTrackedBody;
        int trackedJointCount;

        public BlazePoseBodyPoseSource(BlazePoseDepthLift depthLift)
        {
            this.depthLift = depthLift;
        }

        public string SourceName => "BlazePose";
        public bool IsAvailable => depthLift != null && depthLift.LatestWorld != null;
        // Hip is synthetic (mid 23/24); report right-hip as the nominal index for reference.
        public int HipJointIndex => BlazePoseSkeleton.RightHip;
        public bool HasTrackedBody => hasTrackedBody;
        public int TrackedBodyCount => hasTrackedBody ? 1 : 0;
        public int TrackedJointCount => trackedJointCount;

        public bool TryGetCurrentPose(List<BodyPoseJoint> jointsOut, out Vector3 hipWorldPosition, out bool hipTracked)
        {
            jointsOut?.Clear();
            hipWorldPosition = Vector3.zero;
            hipTracked = false;
            hasTrackedBody = false;
            trackedJointCount = 0;

            var world = depthLift != null ? depthLift.LatestWorld : null;
            if (world == null || !world.valid)
                return false;

            int n = BlazePoseSkeleton.NumKeypoints;
            if (jointsOut != null)
            {
                for (int i = 0; i < n; i++)
                {
                    var j = world.joints[i];
                    if (!j.tracked)
                        continue;
                    jointsOut.Add(new BodyPoseJoint(i, BlazePoseSkeleton.Parents[i], j.worldPosition, true));
                    trackedJointCount++;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                    if (world.joints[i].tracked)
                        trackedJointCount++;
            }

            var left = world.joints[BlazePoseSkeleton.LeftHip];
            var right = world.joints[BlazePoseSkeleton.RightHip];
            if (left.tracked && right.tracked)
            {
                hipWorldPosition = 0.5f * (left.worldPosition + right.worldPosition);
                hipTracked = true;
            }
            else if (right.tracked)
            {
                hipWorldPosition = right.worldPosition;
                hipTracked = true;
            }
            else if (left.tracked)
            {
                hipWorldPosition = left.worldPosition;
                hipTracked = true;
            }
            else if (trackedJointCount > 0)
            {
                // Fallback: average of any tracked joints when hips are occluded.
                Vector3 sum = Vector3.zero;
                int count = 0;
                for (int i = 0; i < n; i++)
                {
                    if (!world.joints[i].tracked)
                        continue;
                    sum += world.joints[i].worldPosition;
                    count++;
                }
                if (count > 0)
                {
                    hipWorldPosition = sum / count;
                    hipTracked = true;
                }
            }

            hasTrackedBody = trackedJointCount > 0;
            return hasTrackedBody;
        }
    }
}
