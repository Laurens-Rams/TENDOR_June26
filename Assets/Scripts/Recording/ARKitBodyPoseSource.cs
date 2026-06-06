using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace BodyTracking.Recording
{
    /// <summary>
    /// <see cref="IBodyPoseSource"/> backed by ARKit human body tracking. Extracted verbatim from the original
    /// BodyTrackingRecorder logic (best-body selection, height-scale, hip joint fallback) so behaviour is
    /// unchanged when ARKit is the selected source.
    /// </summary>
    public class ARKitBodyPoseSource : IBodyPoseSource
    {
        readonly ARHumanBodyManager bodyManager;
        readonly int preferredHipJointIndex;

        bool hasTrackedBody;
        int trackedBodyCount;
        int trackedJointCount;

        public ARKitBodyPoseSource(ARHumanBodyManager bodyManager, int preferredHipJointIndex = 2)
        {
            this.bodyManager = bodyManager;
            this.preferredHipJointIndex = preferredHipJointIndex;
        }

        public string SourceName => "ARKit";
        public bool IsAvailable => bodyManager != null;
        public int HipJointIndex => preferredHipJointIndex;
        public bool HasTrackedBody => hasTrackedBody;
        public int TrackedBodyCount => trackedBodyCount;
        public int TrackedJointCount => trackedJointCount;

        public bool TryGetCurrentPose(List<BodyPoseJoint> jointsOut, out Vector3 hipWorldPosition, out bool hipTracked)
        {
            jointsOut?.Clear();
            hipWorldPosition = Vector3.zero;
            hipTracked = false;

            if (bodyManager == null || !TryGetBestTrackedBody(out ARHumanBody body))
                return false;

            var joints = body.joints;
            float heightScale = GetBodyHeightScale(body);

            if (jointsOut != null)
            {
                for (int i = 0; i < joints.Length; i++)
                {
                    var joint = joints[i];
                    if (!IsUsableJoint(joint))
                        continue;
                    Vector3 world = body.transform.TransformPoint(joint.anchorPose.position * heightScale);
                    jointsOut.Add(new BodyPoseJoint(i, joint.parentIndex, world, true));
                }
            }

            Vector3? hipAnchor = GetBestHipJointAnchorPosition(joints);
            if (hipAnchor.HasValue)
            {
                hipWorldPosition = body.transform.TransformPoint(hipAnchor.Value * heightScale);
                hipTracked = true;
            }

            return true;
        }

        bool TryGetBestTrackedBody(out ARHumanBody trackedBody)
        {
            trackedBody = null;
            hasTrackedBody = false;
            trackedBodyCount = 0;
            trackedJointCount = 0;

            foreach (var humanBody in bodyManager.trackables)
            {
                // Limited ARKit bodies can drift or lose joints, so recordings only accept stable tracking.
                if (humanBody == null || humanBody.trackingState != TrackingState.Tracking)
                    continue;

                var joints = humanBody.joints;
                if (!joints.IsCreated || joints.Length == 0)
                    continue;

                int count = CountTrackedJoints(joints);
                if (count == 0)
                    continue;

                trackedBodyCount++;
                if (trackedBody == null || count > trackedJointCount)
                {
                    trackedBody = humanBody;
                    trackedJointCount = count;
                }
            }

            hasTrackedBody = trackedBody != null;
            return hasTrackedBody;
        }

        int CountTrackedJoints(NativeArray<XRHumanBodyJoint> joints)
        {
            int count = 0;
            for (int i = 0; i < joints.Length; i++)
            {
                if (IsUsableJoint(joints[i]))
                    count++;
            }
            return count;
        }

        Vector3? GetBestHipJointAnchorPosition(NativeArray<XRHumanBodyJoint> joints)
        {
            int hipIndex = ResolveHipJointIndex(joints.Length);
            if (hipIndex >= 0 &&
                hipIndex < joints.Length &&
                IsUsableJoint(joints[hipIndex]))
            {
                return joints[hipIndex].anchorPose.position;
            }

            for (int i = 0; i < joints.Length; i++)
            {
                if (IsUsableJoint(joints[i]))
                    return joints[i].anchorPose.position;
            }

            return null;
        }

        /// <summary>3D hips (index 1) when available; 2D skeleton uses Root (16) as pelvis proxy.</summary>
        int ResolveHipJointIndex(int jointCount)
        {
            if (bodyManager != null && bodyManager.pose3DEnabled)
                return preferredHipJointIndex;

            // 2D ARKit skeleton (19 joints): Root = 16 is the stable torso anchor.
            const int twoDHipProxy = 16;
            return jointCount > twoDHipProxy ? twoDHipProxy : preferredHipJointIndex;
        }

        static bool IsUsableJoint(XRHumanBodyJoint joint)
        {
            return joint.tracked;
        }

        /// <summary>
        /// ARKit's estimated ratio of the detected person's height to the default body model. Joint anchor
        /// poses are in default-height model space, so we multiply by this to match the real person's size.
        /// </summary>
        static float GetBodyHeightScale(ARHumanBody body)
        {
            if (body == null) return 1f;
            float s = body.estimatedHeightScaleFactor;
            if (s <= 0.01f || float.IsNaN(s) || float.IsInfinity(s)) return 1f;
            return s;
        }
    }
}
