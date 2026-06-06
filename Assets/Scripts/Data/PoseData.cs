using UnityEngine;
using System.Collections.Generic;
using System;

namespace BodyTracking.Data
{
    /// <summary>
    /// Represents hip joint data in 3D space with tracking confidence
    /// </summary>
    [Serializable]
    public struct HipJointData
    {
        public Vector3 position;
        public float confidence;
        public bool isTracked;

        public HipJointData(Vector3 pos, float conf = 1.0f, bool tracked = true)
        {
            position = pos;
            confidence = conf;
            isTracked = tracked;
        }

        public static HipJointData Invalid => new HipJointData(Vector3.zero, 0f, false);
        public bool IsValid => isTracked && confidence > 0f;
    }

    [Serializable]
    public struct SkeletonJointData
    {
        public int jointIndex;
        public int parentIndex;
        public Vector3 position;
        public bool isTracked;

        public SkeletonJointData(int index, int parent, Vector3 pos, bool tracked)
        {
            jointIndex = index;
            parentIndex = parent;
            position = pos;
            isTracked = tracked;
        }
    }

    /// <summary>
    /// Per-joint sample in recording reference space (format v2+).
    /// Separate from <see cref="SkeletonJointData"/> so on-disk JSON has an explicit v2 layout.
    /// </summary>
    [Serializable]
    public class RecordedJointSample
    {
        public int jointIndex;
        public int parentIndex;
        public Vector3 positionReference;
        public bool isTracked;

        public RecordedJointSample(int index, int parent, Vector3 positionRef, bool tracked)
        {
            jointIndex = index;
            parentIndex = parent;
            positionReference = positionRef;
            isTracked = tracked;
        }
    }

    /// <summary>
    /// Hip position data for a single frame
    /// </summary>
    [Serializable]
    public struct HipFrame
    {
        public float timestamp;
        public HipJointData hipJoint;
        /// <summary>Legacy v1 joint list (same layout as older recordings).</summary>
        public List<SkeletonJointData> skeletonJoints;
        /// <summary>Format v2 joint samples in reference frame at record time.</summary>
        public List<RecordedJointSample> recordedJoints;

        public bool HasRecordedSkeleton => recordedJoints != null && recordedJoints.Count > 0;
        public bool HasLegacySkeleton => skeletonJoints != null && skeletonJoints.Count > 0;
        public bool HasSkeleton => HasRecordedSkeleton || HasLegacySkeleton;
        public bool IsValid => timestamp >= 0 && (hipJoint.IsValid || HasSkeleton);
    }

    /// <summary>
    /// Complete hip tracking session data
    /// </summary>
    [Serializable]
    public class HipRecording
    {
        /// <summary>1 = legacy skeletonJoints only; 2 = recordedJoints (smoothed at record time).</summary>
        public int recordingFormatVersion = 2;

        public List<HipFrame> frames = new List<HipFrame>();
        public float duration;
        public float frameRate;
        public Vector3 referenceImageTargetPosition;
        public Quaternion referenceImageTargetRotation;
        public Vector3 referenceImageTargetScale;
        public DateTime recordingTimestamp;
        
        public int FrameCount => frames.Count;
        public int ValidFrameCount
        {
            get
            {
                int count = 0;
                foreach (var frame in frames)
                {
                    if (frame.IsValid)
                    {
                        count++;
                    }
                }
                return count;
            }
        }
        public bool HasValidFrames => ValidFrameCount > 0;
        public bool IsValid => frames.Count > 0 && duration > 0 && HasValidFrames;

        /// <summary>Normalize fields after JsonUtility load (missing ints default to 0).</summary>
        public void NormalizeFormatAfterLoad()
        {
            if (recordingFormatVersion <= 0)
                recordingFormatVersion = 1;
        }
        
        /// <summary>
        /// Get frame at specific time
        /// </summary>
        public HipFrame GetFrameAtTime(float time)
        {
            if (frames.Count == 0) return default;
            
            // Clamp time to recording bounds
            time = Mathf.Clamp(time, 0, duration);
            
            // Find frame index
            int frameIndex = Mathf.FloorToInt(time * frameRate);
            frameIndex = Mathf.Clamp(frameIndex, 0, frames.Count - 1);
            
            return frames[frameIndex];
        }
    }

    /// <summary>
    /// Coordinate system transformation helper
    /// </summary>
    [Serializable]
    public struct CoordinateFrame
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
        
        public CoordinateFrame(Transform transform)
        {
            position = transform.position;
            rotation = transform.rotation;
            // Keep spatial frame rigid (translation + rotation only). AR trackable/local scales can vary
            // between sessions and would distort replay if baked into coordinate transforms.
            scale = Vector3.one;
        }
        
        public Matrix4x4 ToMatrix()
        {
            // Intentionally ignore non-uniform scale for stable world remapping across sessions.
            return Matrix4x4.TRS(position, rotation, Vector3.one);
        }
        
        public Vector3 TransformPoint(Vector3 point)
        {
            return ToMatrix().MultiplyPoint3x4(point);
        }
        
        public Vector3 InverseTransformPoint(Vector3 point)
        {
            return ToMatrix().inverse.MultiplyPoint3x4(point);
        }
    }
} 