using System.Collections.Generic;
using BodyTracking.Data;
using NUnit.Framework;
using UnityEngine;

namespace BodyTracking.Tests
{
    /// <summary>
    /// EditMode tests for the pure, deterministic playback/alignment logic in <see cref="HipRecording"/>.
    ///
    /// These cover the highest-risk fixes from the perf/logic audit:
    ///   - Timestamp-based frame lookup (<see cref="HipRecording.GetFrameAtTime"/>) that must pick the
    ///     nearest frame by *real recorded timestamp*, not by nominal frame index / frame rate. This is
    ///     what keeps playback correct after frames are dropped or trimmed.
    ///   - Leading-trim alignment helpers (<see cref="HipRecording.GetFirstValidFrameTime"/>,
    ///     valid-frame accounting, format/source flags) used to compute videoStartTimeOffset.
    ///
    /// AR / Immersal / AVPro / coroutine behaviour is intentionally out of scope here; it cannot be
    /// exercised without a device and is validated by the on-device test plan instead.
    /// </summary>
    public class PoseDataTests
    {
        private const float Eps = 1e-4f;

        // --- helpers -------------------------------------------------------

        /// <summary>A tracked hip frame whose hip X encodes a unique id so we can assert which frame was returned.</summary>
        private static HipFrame ValidFrame(float timestamp, float id)
        {
            return new HipFrame
            {
                timestamp = timestamp,
                hipJoint = new HipJointData(new Vector3(id, 0f, 0f), 1.0f, true),
                skeletonJoints = null,
                recordedJoints = null
            };
        }

        private static HipFrame InvalidFrame(float timestamp)
        {
            return new HipFrame
            {
                timestamp = timestamp,
                hipJoint = HipJointData.Invalid,
                skeletonJoints = null,
                recordedJoints = null
            };
        }

        private static HipRecording Recording(IEnumerable<HipFrame> frames, float duration)
        {
            var rec = new HipRecording
            {
                recordingFormatVersion = 3,
                frames = new List<HipFrame>(frames),
                duration = duration,
                frameRate = 30f
            };
            return rec;
        }

        /// <summary>Returns the hip-X id of the frame chosen for the given time.</summary>
        private static float IdAt(HipRecording rec, float time) => rec.GetFrameAtTime(time).hipJoint.position.x;

        // --- GetFrameAtTime: degenerate inputs -----------------------------

        [Test]
        public void GetFrameAtTime_EmptyRecording_ReturnsDefault()
        {
            var rec = Recording(new HipFrame[0], 0f);
            var frame = rec.GetFrameAtTime(0f);
            Assert.AreEqual(default(HipFrame).timestamp, frame.timestamp);
            Assert.IsFalse(frame.IsValid, "Default frame from an empty recording should not be valid.");
        }

        [Test]
        public void GetFrameAtTime_SingleFrame_AlwaysReturnsThatFrame()
        {
            var rec = Recording(new[] { ValidFrame(0.5f, 7f) }, 0.5f);
            Assert.AreEqual(7f, IdAt(rec, -10f), Eps);
            Assert.AreEqual(7f, IdAt(rec, 0f), Eps);
            Assert.AreEqual(7f, IdAt(rec, 0.5f), Eps);
            Assert.AreEqual(7f, IdAt(rec, 100f), Eps);
        }

        // --- GetFrameAtTime: clamping --------------------------------------

        [Test]
        public void GetFrameAtTime_NegativeTime_ClampsToFirstFrame()
        {
            // First real frame at 0.2s (e.g. after a leading trim): t<=0 must map to it, not to a wrong frame.
            var rec = Recording(new[]
            {
                ValidFrame(0.2f, 1f),
                ValidFrame(1.0f, 2f),
                ValidFrame(2.0f, 3f)
            }, 2.0f);

            Assert.AreEqual(1f, IdAt(rec, -5f), Eps);
            Assert.AreEqual(1f, IdAt(rec, 0f), Eps);
        }

        [Test]
        public void GetFrameAtTime_BeyondDuration_ClampsToLastFrame()
        {
            var rec = Recording(new[]
            {
                ValidFrame(0f, 1f),
                ValidFrame(1.0f, 2f),
                ValidFrame(2.0f, 3f)
            }, 2.0f);

            Assert.AreEqual(3f, IdAt(rec, 2.0f), Eps);
            Assert.AreEqual(3f, IdAt(rec, 999f), Eps);
        }

        // --- GetFrameAtTime: exact + nearest -------------------------------

        [Test]
        public void GetFrameAtTime_ExactTimestamp_ReturnsMatchingFrame()
        {
            var rec = Recording(new[]
            {
                ValidFrame(0f, 10f),
                ValidFrame(1.0f, 20f),
                ValidFrame(2.0f, 30f)
            }, 2.0f);

            Assert.AreEqual(20f, IdAt(rec, 1.0f), Eps);
        }

        [Test]
        public void GetFrameAtTime_BetweenFrames_PicksNearestByRealTimestamp()
        {
            // Irregular gap: 0.5s then a jump to 2.0s. The fix must choose by real timestamp distance,
            // NOT by (time * frameRate) index, which would be wrong across the gap.
            var rec = Recording(new[]
            {
                ValidFrame(0.0f, 1f),
                ValidFrame(0.5f, 2f),
                ValidFrame(2.0f, 3f)
            }, 2.0f);

            // 1.2s: |1.2-0.5|=0.7 < |2.0-1.2|=0.8  -> frame @0.5 (id 2)
            Assert.AreEqual(2f, IdAt(rec, 1.2f), Eps);
            // 1.4s: |1.4-0.5|=0.9 > |2.0-1.4|=0.6  -> frame @2.0 (id 3)
            Assert.AreEqual(3f, IdAt(rec, 1.4f), Eps);
        }

        [Test]
        public void GetFrameAtTime_Equidistant_PrefersEarlierFrame()
        {
            // Tie-break: beforeDelta <= afterDelta returns the earlier frame.
            var rec = Recording(new[]
            {
                ValidFrame(0.0f, 1f),
                ValidFrame(1.0f, 2f),
                ValidFrame(2.0f, 3f)
            }, 2.0f);

            Assert.AreEqual(1f, IdAt(rec, 0.5f), Eps, "0.5s is equidistant to 0.0 and 1.0; earlier frame expected.");
            Assert.AreEqual(2f, IdAt(rec, 1.5f), Eps, "1.5s is equidistant to 1.0 and 2.0; earlier frame expected.");
        }

        [Test]
        public void GetFrameAtTime_BeforeFirstTimestamp_ReturnsFirst()
        {
            var rec = Recording(new[]
            {
                ValidFrame(0.3f, 11f),
                ValidFrame(1.0f, 22f)
            }, 1.0f);

            Assert.AreEqual(11f, IdAt(rec, 0.1f), Eps);
        }

        [Test]
        public void GetFrameAtTime_DenseScrub_IsMonotonicAndStable()
        {
            // Simulate scrubbing a 30fps clip; the chosen frame index must be non-decreasing as time advances.
            var frames = new List<HipFrame>();
            for (int i = 0; i < 60; i++)
                frames.Add(ValidFrame(i / 30f, i));
            var rec = Recording(frames, 59 / 30f);

            float lastId = -1f;
            for (float t = 0f; t <= rec.duration; t += 0.005f)
            {
                float id = IdAt(rec, t);
                Assert.GreaterOrEqual(id, lastId, $"Frame selection went backwards while scrubbing forward at t={t}.");
                lastId = id;
            }
        }

        // --- Trim / first-valid-frame --------------------------------------

        [Test]
        public void GetFirstValidFrameTime_LeadingInvalidFrames_ReturnsFirstValidTimestamp()
        {
            var rec = Recording(new[]
            {
                InvalidFrame(0.0f),
                InvalidFrame(0.1f),
                ValidFrame(0.2f, 1f),
                ValidFrame(0.3f, 2f)
            }, 0.3f);

            Assert.AreEqual(0.2f, rec.GetFirstValidFrameTime(), Eps);
        }

        [Test]
        public void GetFirstValidFrameTime_NoValidFrames_ReturnsZero()
        {
            var rec = Recording(new[] { InvalidFrame(0.0f), InvalidFrame(0.1f) }, 0.1f);
            Assert.AreEqual(0f, rec.GetFirstValidFrameTime(), Eps);
        }

        [Test]
        public void GetFirstValidFrameTime_EmptyRecording_ReturnsZero()
        {
            var rec = Recording(new HipFrame[0], 0f);
            Assert.AreEqual(0f, rec.GetFirstValidFrameTime(), Eps);
        }

        // --- Valid-frame accounting ----------------------------------------

        [Test]
        public void ValidFrameCount_CountsOnlyValidFrames()
        {
            var rec = Recording(new[]
            {
                InvalidFrame(0.0f),
                ValidFrame(0.1f, 1f),
                InvalidFrame(0.2f),
                ValidFrame(0.3f, 2f),
                ValidFrame(0.4f, 3f)
            }, 0.4f);

            Assert.AreEqual(3, rec.ValidFrameCount);
            Assert.IsTrue(rec.HasValidFrames);
        }

        [Test]
        public void IsValid_RequiresFramesDurationAndValidData()
        {
            Assert.IsFalse(Recording(new HipFrame[0], 0f).IsValid, "No frames -> invalid.");
            Assert.IsFalse(Recording(new[] { ValidFrame(0f, 1f) }, 0f).IsValid, "Zero duration -> invalid.");
            Assert.IsFalse(Recording(new[] { InvalidFrame(0f) }, 1f).IsValid, "No valid frames -> invalid.");
            Assert.IsTrue(Recording(new[] { ValidFrame(0f, 1f) }, 1f).IsValid, "Frames + duration + valid data -> valid.");
        }

        // --- Frame / joint validity ----------------------------------------

        [Test]
        public void HipFrame_IsValid_NegativeTimestampIsInvalid()
        {
            var frame = ValidFrame(-0.001f, 1f);
            Assert.IsFalse(frame.IsValid, "A frame with a negative timestamp must be rejected.");
        }

        [Test]
        public void HipFrame_IsValid_SkeletonOnlyFrameIsValid()
        {
            var frame = new HipFrame
            {
                timestamp = 0.1f,
                hipJoint = HipJointData.Invalid,
                recordedJoints = new List<RecordedJointSample>
                {
                    new RecordedJointSample(0, -1, Vector3.zero, true)
                }
            };
            Assert.IsTrue(frame.IsValid, "A frame with skeleton data but no hip should still be valid.");
            Assert.IsTrue(frame.HasSkeleton);
        }

        [Test]
        public void HipJointData_IsValid_RequiresTrackingAndConfidence()
        {
            Assert.IsTrue(new HipJointData(Vector3.zero, 0.5f, true).IsValid);
            Assert.IsFalse(new HipJointData(Vector3.zero, 0.5f, false).IsValid, "Untracked -> invalid.");
            Assert.IsFalse(new HipJointData(Vector3.zero, 0f, true).IsValid, "Zero confidence -> invalid.");
            Assert.IsFalse(HipJointData.Invalid.IsValid);
        }

        // --- Format / source metadata (alignment + fusion guards) ----------

        [Test]
        public void NormalizeFormatAfterLoad_DefaultsLegacyVersionToOne()
        {
            var rec = new HipRecording { recordingFormatVersion = 0 };
            rec.NormalizeFormatAfterLoad();
            Assert.AreEqual(1, rec.recordingFormatVersion);
        }

        [Test]
        public void IsLegacyFormat_TrueBelowV3()
        {
            Assert.IsTrue(new HipRecording { recordingFormatVersion = 1 }.IsLegacyFormat);
            Assert.IsTrue(new HipRecording { recordingFormatVersion = 2 }.IsLegacyFormat);
            Assert.IsFalse(new HipRecording { recordingFormatVersion = 3 }.IsLegacyFormat);
        }

        [Test]
        public void IsImmersalSourced_IsCaseInsensitiveAndDefaultsFalse()
        {
            Assert.IsFalse(new HipRecording().IsImmersalSourced, "Empty source defaults to image-target (not Immersal).");
            Assert.IsFalse(new HipRecording { spatialSource = "imageTarget" }.IsImmersalSourced);
            Assert.IsTrue(new HipRecording { spatialSource = "immersal" }.IsImmersalSourced);
            Assert.IsTrue(new HipRecording { spatialSource = "Immersal" }.IsImmersalSourced);
        }

        [Test]
        public void VideoStartTimeOffset_DefaultsToZero()
        {
            // With no leading trim the offset must be 0 (the wait-for-body case from on-device session 2).
            Assert.AreEqual(0f, new HipRecording().videoStartTimeOffset, Eps);
        }
    }
}
