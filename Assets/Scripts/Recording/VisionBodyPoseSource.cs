using System.Collections.Generic;
using BodyTracking.Vision;
using UnityEngine;

namespace BodyTracking.Recording
{
    /// <summary>
    /// <see cref="IBodyPoseSource"/> backed by the native Apple Vision 3D body-pose runner.
    /// Reads the latest world-space pose from <see cref="VisionBodyPoseRunner"/> and maps the
    /// 17 Vision joints into the recorder's generic joint list. The hip anchor is Vision's
    /// root joint (pelvis / between hips).
    /// </summary>
    public class VisionBodyPoseSource : IBodyPoseSource
    {
        readonly VisionBodyPoseRunner runner;

        bool hasTrackedBody;
        int trackedJointCount;

        public VisionBodyPoseSource(VisionBodyPoseRunner runner)
        {
            this.runner = runner;
        }

        public string SourceName => "Vision";
        public bool IsAvailable => runner != null;
        public int HipJointIndex => VisionBodySkeleton.Root;
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

            var world = runner != null ? runner.LatestWorld : null;
            if (world == null || !world.valid)
                return false;

            int n = VisionBodySkeleton.JointCount;
            for (int i = 0; i < n; i++)
            {
                var j = world.joints[i];
                if (!j.tracked)
                    continue;
                jointsOut?.Add(new BodyPoseJoint(i, VisionBodySkeleton.Parents[i], j.worldPosition, true));
                trackedJointCount++;
            }

            var root = world.joints[VisionBodySkeleton.Root];
            if (root.tracked)
            {
                hipWorldPosition = root.worldPosition;
                hipTracked = true;
            }
            else
            {
                var left = world.joints[VisionBodySkeleton.LeftHip];
                var right = world.joints[VisionBodySkeleton.RightHip];
                if (left.tracked && right.tracked)
                {
                    hipWorldPosition = 0.5f * (left.worldPosition + right.worldPosition);
                    hipTracked = true;
                }
                else if (right.tracked) { hipWorldPosition = right.worldPosition; hipTracked = true; }
                else if (left.tracked) { hipWorldPosition = left.worldPosition; hipTracked = true; }
            }

            hasTrackedBody = trackedJointCount > 0;
            return hasTrackedBody;
        }
    }
}
