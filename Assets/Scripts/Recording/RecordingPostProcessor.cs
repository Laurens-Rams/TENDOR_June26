using System.Collections.Generic;
using BodyTracking.Data;
using UnityEngine;

namespace BodyTracking.Recording
{
    public struct RecordingPostProcessSettings
    {
        public bool enabled;
        public float smoothingStrength;
        public int smoothingPasses;
        public float maxJointSpeedMetersPerSecond;
        public float maxHipSpeedMetersPerSecond;
        public float maxSingleFrameJumpMeters;
        public float maxGapFillSeconds;
        public bool keepFirstGoodSample;
    }

    public struct RecordingPostProcessSummary
    {
        public int hipOutliersRemoved;
        public int hipSamplesFilled;
        public int jointOutliersRemoved;
        public int jointSamplesFilled;
    }

    /// <summary>
    /// Offline recording cleanup. This is intentionally heavier than the live filter so playback
    /// reads already-stable joint data without doing expensive correction every frame.
    /// </summary>
    public static class RecordingPostProcessor
    {
        public static RecordingPostProcessSummary Process(HipRecording recording, RecordingPostProcessSettings settings)
        {
            var summary = new RecordingPostProcessSummary();
            if (!settings.enabled || recording == null || recording.frames == null || recording.frames.Count == 0)
                return summary;

            float[] times = BuildTimes(recording);
            ProcessHipTrack(recording, times, settings, ref summary);
            ProcessJointTracks(recording, times, settings, ref summary);
            return summary;
        }

        private static float[] BuildTimes(HipRecording recording)
        {
            int count = recording.frames.Count;
            float[] times = new float[count];
            float fallbackFrameTime = 1f / Mathf.Max(1f, recording.frameRate);
            for (int i = 0; i < count; i++)
            {
                float t = recording.frames[i].timestamp;
                times[i] = t > 0f || i == 0 ? t : i * fallbackFrameTime;
            }
            return times;
        }

        private static void ProcessHipTrack(HipRecording recording, float[] times, RecordingPostProcessSettings settings, ref RecordingPostProcessSummary summary)
        {
            int count = recording.frames.Count;
            Vector3[] positions = new Vector3[count];
            bool[] valid = new bool[count];

            for (int i = 0; i < count; i++)
            {
                HipFrame frame = recording.frames[i];
                valid[i] = frame.hipJoint.IsValid;
                positions[i] = frame.hipJoint.position;
            }

            int removed;
            int filled;
            CleanTrack(positions, valid, times, settings, settings.maxHipSpeedMetersPerSecond, out removed, out filled);
            summary.hipOutliersRemoved += removed;
            summary.hipSamplesFilled += filled;

            for (int i = 0; i < count; i++)
            {
                HipFrame frame = recording.frames[i];
                frame.hipJoint = valid[i]
                    ? new HipJointData(positions[i], 1f, true)
                    : HipJointData.Invalid;
                recording.frames[i] = frame;
            }
        }

        private static void ProcessJointTracks(HipRecording recording, float[] times, RecordingPostProcessSettings settings, ref RecordingPostProcessSummary summary)
        {
            var jointIndices = new HashSet<int>();
            var parentByJoint = new Dictionary<int, int>();
            for (int i = 0; i < recording.frames.Count; i++)
            {
                var joints = recording.frames[i].recordedJoints;
                if (joints == null) continue;
                for (int j = 0; j < joints.Count; j++)
                {
                    var sample = joints[j];
                    if (sample == null || !sample.isTracked) continue;
                    jointIndices.Add(sample.jointIndex);
                    parentByJoint[sample.jointIndex] = sample.parentIndex;
                }
            }

            foreach (int jointIndex in jointIndices)
            {
                ProcessSingleJointTrack(recording, times, settings, jointIndex, parentByJoint, ref summary);
            }
        }

        private static void ProcessSingleJointTrack(
            HipRecording recording,
            float[] times,
            RecordingPostProcessSettings settings,
            int jointIndex,
            Dictionary<int, int> parentByJoint,
            ref RecordingPostProcessSummary summary)
        {
            int count = recording.frames.Count;
            Vector3[] positions = new Vector3[count];
            bool[] valid = new bool[count];

            for (int i = 0; i < count; i++)
            {
                if (TryGetJoint(recording.frames[i], jointIndex, out RecordedJointSample sample))
                {
                    positions[i] = sample.positionReference;
                    valid[i] = sample.isTracked;
                }
            }

            int removed;
            int filled;
            CleanTrack(positions, valid, times, settings, settings.maxJointSpeedMetersPerSecond, out removed, out filled);
            summary.jointOutliersRemoved += removed;
            summary.jointSamplesFilled += filled;

            int parentIndex = parentByJoint.TryGetValue(jointIndex, out int parent) ? parent : -1;
            for (int i = 0; i < count; i++)
            {
                HipFrame frame = recording.frames[i];
                if (frame.recordedJoints == null)
                    frame.recordedJoints = new List<RecordedJointSample>();

                if (valid[i])
                    UpsertJoint(frame.recordedJoints, jointIndex, parentIndex, positions[i]);
                else
                    RemoveJoint(frame.recordedJoints, jointIndex);

                recording.frames[i] = frame;
            }
        }

        private static void CleanTrack(
            Vector3[] positions,
            bool[] valid,
            float[] times,
            RecordingPostProcessSettings settings,
            float maxSpeedMetersPerSecond,
            out int outliersRemoved,
            out int samplesFilled)
        {
            outliersRemoved = ClampOutliers(positions, valid, times, settings, maxSpeedMetersPerSecond);
            samplesFilled = FillShortGaps(positions, valid, times, settings.maxGapFillSeconds);

            if (CountValid(valid) < 2)
                return;

            int passes = Mathf.Clamp(settings.smoothingPasses, 1, 12);
            float blend = Mathf.Lerp(1f, 0.04f, Mathf.Clamp01(settings.smoothingStrength));
            for (int pass = 0; pass < passes; pass++)
            {
                SmoothForward(positions, valid, blend, settings.keepFirstGoodSample);
                SmoothBackward(positions, valid, blend, settings.keepFirstGoodSample);
            }
        }

        private static int ClampOutliers(
            Vector3[] positions,
            bool[] valid,
            float[] times,
            RecordingPostProcessSettings settings,
            float maxSpeedMetersPerSecond)
        {
            int corrected = 0;
            int lastGood = -1;
            float maxSpeed = Mathf.Max(0.01f, maxSpeedMetersPerSecond);
            float maxJump = Mathf.Max(0.01f, settings.maxSingleFrameJumpMeters);

            for (int i = 0; i < positions.Length; i++)
            {
                if (!valid[i])
                    continue;

                if (lastGood < 0)
                {
                    lastGood = i;
                    continue;
                }

                float dt = Mathf.Max(times[i] - times[lastGood], 1e-5f);
                float distance = Vector3.Distance(positions[i], positions[lastGood]);
                float speed = distance / dt;
                if (distance > maxJump || speed > maxSpeed)
                {
                    float allowedStep = Mathf.Min(maxJump, maxSpeed * dt);
                    Vector3 direction = positions[i] - positions[lastGood];
                    positions[i] = positions[lastGood] + direction.normalized * allowedStep;
                    corrected++;
                }

                lastGood = i;
            }

            return corrected;
        }

        private static int FillShortGaps(Vector3[] positions, bool[] valid, float[] times, float maxGapSeconds)
        {
            int filled = 0;
            int i = 0;
            while (i < valid.Length)
            {
                if (valid[i])
                {
                    i++;
                    continue;
                }

                int gapStart = i;
                while (i < valid.Length && !valid[i])
                    i++;
                int gapEnd = i - 1;
                int prev = gapStart - 1;
                int next = i;
                if (prev < 0 || next >= valid.Length || !valid[prev] || !valid[next])
                    continue;

                float gapSeconds = times[next] - times[prev];
                if (gapSeconds > maxGapSeconds)
                    continue;

                for (int g = gapStart; g <= gapEnd; g++)
                {
                    float t = Mathf.InverseLerp(times[prev], times[next], times[g]);
                    positions[g] = Vector3.Lerp(positions[prev], positions[next], t);
                    valid[g] = true;
                    filled++;
                }
            }

            return filled;
        }

        private static void SmoothForward(Vector3[] positions, bool[] valid, float blend, bool keepFirstGoodSample)
        {
            int first = FirstValidIndex(valid);
            if (first < 0)
                return;

            Vector3 state = positions[first];
            for (int i = first + (keepFirstGoodSample ? 1 : 0); i < positions.Length; i++)
            {
                if (!valid[i])
                    continue;
                state = Vector3.Lerp(state, positions[i], blend);
                positions[i] = state;
            }
        }

        private static void SmoothBackward(Vector3[] positions, bool[] valid, float blend, bool keepLastGoodSample)
        {
            int last = LastValidIndex(valid);
            if (last < 0)
                return;

            Vector3 state = positions[last];
            for (int i = last - (keepLastGoodSample ? 1 : 0); i >= 0; i--)
            {
                if (!valid[i])
                    continue;
                state = Vector3.Lerp(state, positions[i], blend);
                positions[i] = state;
            }
        }

        private static bool TryGetJoint(HipFrame frame, int jointIndex, out RecordedJointSample sample)
        {
            var joints = frame.recordedJoints;
            if (joints != null)
            {
                for (int i = 0; i < joints.Count; i++)
                {
                    if (joints[i] != null && joints[i].jointIndex == jointIndex)
                    {
                        sample = joints[i];
                        return true;
                    }
                }
            }

            sample = null;
            return false;
        }

        private static void UpsertJoint(List<RecordedJointSample> joints, int jointIndex, int parentIndex, Vector3 position)
        {
            for (int i = 0; i < joints.Count; i++)
            {
                if (joints[i] != null && joints[i].jointIndex == jointIndex)
                {
                    joints[i].parentIndex = parentIndex;
                    joints[i].positionReference = position;
                    joints[i].isTracked = true;
                    return;
                }
            }

            joints.Add(new RecordedJointSample(jointIndex, parentIndex, position, true));
        }

        private static void RemoveJoint(List<RecordedJointSample> joints, int jointIndex)
        {
            for (int i = joints.Count - 1; i >= 0; i--)
            {
                if (joints[i] != null && joints[i].jointIndex == jointIndex)
                    joints.RemoveAt(i);
            }
        }

        private static int CountValid(bool[] valid)
        {
            int count = 0;
            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i])
                    count++;
            }
            return count;
        }

        private static int FirstValidIndex(bool[] valid)
        {
            for (int i = 0; i < valid.Length; i++)
            {
                if (valid[i])
                    return i;
            }
            return -1;
        }

        private static int LastValidIndex(bool[] valid)
        {
            for (int i = valid.Length - 1; i >= 0; i--)
            {
                if (valid[i])
                    return i;
            }
            return -1;
        }
    }
}