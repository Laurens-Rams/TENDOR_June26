using System.Collections.Generic;
using UnityEngine;
using BodyTracking.Data;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using BodyTracking.DebugTools;
#endif

namespace BodyTracking.MoveAI
{
    /// <summary>
    /// Fuses a Move AI motion clip (smooth, gap-free pose + drifting root) with an ARKit recording
    /// (accurate, world-anchored hip with gaps) into a baked RouteRoot-local root trajectory + pose.
    ///
    /// Pipeline (all acausal / offline, so corrections are zero-lag and jitter-free):
    /// 1. Resample both onto the recording's fixed frame grid.
    /// 2. Rigidly align (yaw + translation) the Move root path to the ARKit hip path over valid frames.
    /// 3. Build the correction offset O = ARKit - Move where ARKit is valid; hold through gaps.
    /// 4. Zero-phase (forward-backward) smooth O, apply per-axis weights (X/Z moderate–strong, Y full for climbs).
    /// 5. Bake R[k] = MoveLocal[k] + weighted O[k]; compute a uniform head-to-feet character scale.
    /// </summary>
    public static class MoveAIFusionBaker
    {
        public struct Settings
        {
            public Vector3 axisWeights;      // RouteRoot-local x,y,z correction weights
            public float smoothingTau;       // seconds; larger = gentler re-convergence
            public float outlierMeters;      // reject ARKit samples this far from the aligned Move root

            /// <summary>Non-zero ARKit correction weights used when none are configured (rebake / legacy scenes).</summary>
            public static readonly Vector3 DefaultAxisWeights = new Vector3(0.6f, 1f, 1f);

            public static Settings Default => new Settings
            {
                axisWeights = DefaultAxisWeights,
                smoothingTau = 0.4f,
                outlierMeters = 0.6f,
            };
        }

        public static MoveAIFusionAsset Bake(HipRecording recording, MoveMotion motion, MoveJointMap map, Settings settings)
        {
            if (recording == null || motion == null || motion.FrameCount == 0)
            {
                Debug.LogError("[MoveAIFusionBaker] Missing recording or motion");
                return null;
            }

            float fps = recording.frameRate > 0 ? recording.frameRate : 30f;
            int n = Mathf.Max(1, Mathf.RoundToInt(recording.duration * fps));

            // 1) Resample ARKit hip (RouteRoot-local) and Move root onto the common grid.
            //
            // The Move motion clip is already on the SAME clock as the (leading-trimmed) ARKit recording: Move only
            // produces a pose once the climber is in frame, which is also where ARKit's t=0 was re-zeroed to. So
            // ARKit time t pairs with Move time t directly. The previous code subtracted videoStartTimeOffset here,
            // which shoved the baked pose + root path ~offset seconds EARLIER than the GLB body / ARKit — the legs
            // reached a chair/step before the pelvis rose (the body "floated up only half way"). Sample at t.
            var arkitHip = new Vector3[n];
            var arkitValid = new bool[n];
            var moveRootRaw = new Vector3[n];
            var poseResampled = new MoveMotion { fps = fps };
            poseResampled.jointNames.AddRange(motion.jointNames);
            poseResampled.jointParents.AddRange(motion.jointParents);

            for (int k = 0; k < n; k++)
            {
                float t = k / fps;

                HipFrame hf = recording.GetFrameAtTime(t);
                arkitValid[k] = hf.hipJoint.IsValid;
                arkitHip[k] = hf.hipJoint.position;

                var mf = motion.FrameAtTime(t);
                moveRootRaw[k] = mf != null ? mf.rootPosition : Vector3.zero;
                poseResampled.frames.Add(CloneFrame(mf, poseResampled.JointCount));
            }

            // 2) Rigid (yaw + translation) alignment over valid pairs.
            ComputeRigidAlignment(moveRootRaw, arkitHip, arkitValid, out Quaternion yaw, out Vector3 translation);

            var moveLocal = new Vector3[n];
            for (int k = 0; k < n; k++)
                moveLocal[k] = yaw * moveRootRaw[k] + translation;

            // 3) Correction offset where ARKit valid (with outlier rejection), held through gaps.
            var offsets = new Vector3[n];
            var hasOffset = new bool[n];
            for (int k = 0; k < n; k++)
            {
                if (!arkitValid[k]) continue;
                Vector3 o = arkitHip[k] - moveLocal[k];
                if (o.magnitude > settings.outlierMeters) continue; // reject jumps
                offsets[k] = o;
                hasOffset[k] = true;
            }
            HoldFillGaps(offsets, hasOffset);

            // 4) Zero-phase smoothing + per-axis weights.
            float alpha = Mathf.Exp(-1f / Mathf.Max(0.001f, settings.smoothingTau * fps));
            ZeroPhaseSmooth(offsets, alpha);

            // 5) Bake corrected root path.
            var rootPath = new List<Vector3>(n);
            for (int k = 0; k < n; k++)
            {
                Vector3 weighted = Vector3.Scale(offsets[k], settings.axisWeights);
                rootPath.Add(moveLocal[k] + weighted);
            }

            // Head-to-feet uniform scale: ARKit body extent vs Move body extent.
            float arkitHeight = EstimateArkitHeight(recording);
            float moveHeight = EstimateMoveHeight(motion);
            float scale = (arkitHeight > 0.1f && moveHeight > 0.1f) ? arkitHeight / moveHeight : 1f;

            var asset = new MoveAIFusionAsset
            {
                pose = poseResampled,
                rootPathLocal = rootPath,
                frameRate = fps,
                scale = scale,
                correctionWeights = settings.axisWeights,
                offsetCorrected = true, // sampled on the correct (ARKit-aligned) clock above
                mapId = recording.mapId,
                routeId = recording.routeId,
                spatialSource = recording.spatialSource,
                referencePosition = recording.referenceImageTargetPosition,
                referenceRotation = recording.referenceImageTargetRotation,
            };

            Debug.Log($"[MoveAIFusionBaker] Baked {n} frames; scale={scale:F3} (arkit {arkitHeight:F2}m / move {moveHeight:F2}m); validHip={CountTrue(arkitValid)}/{n}");
            return asset;
        }

        /// <summary>
        /// Re-fuse using an EXISTING baked asset's cached Move pose (which preserves per-frame
        /// <see cref="MoveMotionFrame.rootPosition"/>) instead of a freshly parsed Move clip — so settings like
        /// axis weights / smoothing / outlier rejection can be re-applied with NO Move API round-trip. The pose +
        /// topology are carried over unchanged; only the corrected root path and uniform scale are recomputed.
        /// Returns null if the inputs can't support a rebake (caller keeps the existing asset).
        /// </summary>
        public static MoveAIFusionAsset RebakeFromAsset(HipRecording recording, MoveAIFusionAsset existing, Settings settings)
        {
            if (recording == null || existing == null || existing.pose == null || existing.pose.FrameCount == 0)
            {
                Debug.LogError("[MoveAIFusionBaker] Rebake missing recording or cached pose");
                return null;
            }

            float fps = existing.frameRate > 0f ? existing.frameRate : (existing.pose.fps > 0f ? existing.pose.fps : 30f);

            // Re-time legacy assets (no API). Assets baked before the videoStartTimeOffset sign fix had every frame
            // sampled at (t - offset), i.e. ~offset seconds too early. Shift the cached frames forward by offset so
            // frame k once again represents Move time k/fps (aligned with the GLB body + ARKit). Idempotent: only
            // applied while offsetCorrected is false. Note: the original bake dropped the last ~offset seconds of
            // Move content, so the tail of a re-timed legacy asset holds its last real frame (a fresh Move re-bake
            // recovers it fully). Fresh/already-corrected assets are reused as-is.
            var pose = existing.offsetCorrected
                ? existing.pose
                : RetimePose(existing.pose, recording.videoStartTimeOffset, fps);
            int n = pose.FrameCount;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            TimingDebugLog.Log("H2", "MoveAIFusionBaker.RebakeFromAsset", "rebake pose source",
                "{\"wasOffsetCorrected\":" + (existing.offsetCorrected ? "true" : "false") +
                ",\"retimeApplied\":" + (existing.offsetCorrected ? "false" : "true") +
                ",\"videoOffset\":" + recording.videoStartTimeOffset.ToString("F2") +
                ",\"frameCount\":" + n + "}");
            // #endregion
#endif

            var arkitHip = new Vector3[n];
            var arkitValid = new bool[n];
            var moveRootRaw = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                float t = k / fps;
                HipFrame hf = recording.GetFrameAtTime(t);
                arkitValid[k] = hf.hipJoint.IsValid;
                arkitHip[k] = hf.hipJoint.position;
                moveRootRaw[k] = pose.frames[k] != null ? pose.frames[k].rootPosition : Vector3.zero;
            }

            ComputeRigidAlignment(moveRootRaw, arkitHip, arkitValid, out Quaternion yaw, out Vector3 translation);

            var moveLocal = new Vector3[n];
            for (int k = 0; k < n; k++)
                moveLocal[k] = yaw * moveRootRaw[k] + translation;

            var offsets = new Vector3[n];
            var hasOffset = new bool[n];
            for (int k = 0; k < n; k++)
            {
                if (!arkitValid[k]) continue;
                Vector3 o = arkitHip[k] - moveLocal[k];
                if (o.magnitude > settings.outlierMeters) continue;
                offsets[k] = o;
                hasOffset[k] = true;
            }
            HoldFillGaps(offsets, hasOffset);

            float alpha = Mathf.Exp(-1f / Mathf.Max(0.001f, settings.smoothingTau * fps));
            ZeroPhaseSmooth(offsets, alpha);

            var rootPath = new List<Vector3>(n);
            for (int k = 0; k < n; k++)
            {
                Vector3 weighted = Vector3.Scale(offsets[k], settings.axisWeights);
                rootPath.Add(moveLocal[k] + weighted);
            }

            float arkitHeight = EstimateArkitHeight(recording);
            float moveHeight = EstimateMoveHeight(pose);
            float scale = (arkitHeight > 0.1f && moveHeight > 0.1f) ? arkitHeight / moveHeight : existing.scale;

            var asset = new MoveAIFusionAsset
            {
                pose = pose, // topology + per-frame rotations/root (re-timed once for legacy assets)
                rootPathLocal = rootPath,
                frameRate = fps,
                scale = scale,
                correctionWeights = settings.axisWeights,
                offsetCorrected = true,
                mapId = existing.mapId,
                routeId = existing.routeId,
                spatialSource = existing.spatialSource,
                referencePosition = existing.referencePosition,
                referenceRotation = existing.referenceRotation,
            };

            Debug.Log($"[MoveAIFusionBaker] Rebaked {n} frames (no API); scale={scale:F3}; validHip={CountTrue(arkitValid)}/{n}; " +
                      $"weights={settings.axisWeights} tau={settings.smoothingTau} outlier={settings.outlierMeters}");
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // #region agent log
            int climbIdx = Mathf.Clamp(Mathf.RoundToInt(3.5f * fps), 0, n - 1);
            TimingDebugLog.Log("H7", "MoveAIFusionBaker.RebakeFromAsset", "rebake root sample",
                "{\"weights\":{\"x\":" + settings.axisWeights.x.ToString("F2") +
                ",\"y\":" + settings.axisWeights.y.ToString("F2") +
                ",\"z\":" + settings.axisWeights.z.ToString("F2") +
                "},\"rootY_t3.5\":" + rootPath[climbIdx].y.ToString("F3") +
                ",\"arkitY_t3.5\":" + arkitHip[climbIdx].y.ToString("F3") +
                ",\"moveLocalY_t3.5\":" + moveLocal[climbIdx].y.ToString("F3") + "}");
            // #endregion
#endif
            return asset;
        }

        // --- Alignment ----------------------------------------------------------------------------

        static void ComputeRigidAlignment(Vector3[] move, Vector3[] arkit, bool[] valid, out Quaternion yaw, out Vector3 translation)
        {
            yaw = Quaternion.identity;
            translation = Vector3.zero;

            // Collect valid pairs.
            var m = new List<Vector3>();
            var a = new List<Vector3>();
            for (int k = 0; k < valid.Length; k++)
            {
                if (!valid[k]) continue;
                m.Add(move[k]); a.Add(arkit[k]);
            }

            if (m.Count == 0)
                return; // no anchor; leave identity (caller can place manually)

            Vector3 mc = Centroid(m), ac = Centroid(a);

            if (m.Count >= 2)
            {
                // Best-fit yaw about Y over horizontal (XZ) displacement.
                float sumCross = 0f, sumDot = 0f;
                for (int i = 0; i < m.Count; i++)
                {
                    Vector3 p = m[i] - mc, q = a[i] - ac;
                    sumDot += p.x * q.x + p.z * q.z;
                    sumCross += p.x * q.z - p.z * q.x;
                }
                float theta = Mathf.Atan2(sumCross, sumDot) * Mathf.Rad2Deg;
                yaw = Quaternion.AngleAxis(theta, Vector3.up);
            }

            translation = ac - yaw * mc;
        }

        // --- Gap handling + smoothing -------------------------------------------------------------

        static void HoldFillGaps(Vector3[] values, bool[] has)
        {
            int n = values.Length;

            // Backward-fill leading gap from first known.
            int first = -1;
            for (int k = 0; k < n; k++) { if (has[k]) { first = k; break; } }
            if (first < 0) return; // nothing known
            for (int k = 0; k < first; k++) values[k] = values[first];

            // Forward-fill interior/trailing gaps by holding the last known value.
            Vector3 last = values[first];
            for (int k = first; k < n; k++)
            {
                if (has[k]) last = values[k];
                else values[k] = last;
            }
        }

        static void ZeroPhaseSmooth(Vector3[] values, float alpha)
        {
            int n = values.Length;
            if (n == 0) return;

            // Forward pass.
            Vector3 acc = values[0];
            for (int k = 0; k < n; k++)
            {
                acc = Vector3.Lerp(values[k], acc, alpha);
                values[k] = acc;
            }
            // Backward pass (cancels phase lag).
            acc = values[n - 1];
            for (int k = n - 1; k >= 0; k--)
            {
                acc = Vector3.Lerp(values[k], acc, alpha);
                values[k] = acc;
            }
        }

        // --- Height estimation --------------------------------------------------------------------

        /// <summary>Median over frames of the tracked-joint vertical extent (head highest, feet lowest).</summary>
        static float EstimateArkitHeight(HipRecording recording)
        {
            var heights = new List<float>();
            foreach (var f in recording.frames)
            {
                if (f.recordedJoints == null || f.recordedJoints.Count < 4) continue;
                float min = float.MaxValue, max = float.MinValue;
                foreach (var j in f.recordedJoints)
                {
                    if (!j.isTracked) continue;
                    min = Mathf.Min(min, j.positionReference.y);
                    max = Mathf.Max(max, j.positionReference.y);
                }
                if (max > min) heights.Add(max - min);
            }
            return Median(heights);
        }

        static float EstimateMoveHeight(MoveMotion motion)
        {
            if (motion.FrameCount == 0) return 0f;
            var world = motion.ForwardKinematics(motion.frames[0]);
            if (world == null || world.Length == 0) return 0f;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var p in world)
            {
                min = Mathf.Min(min, p.y);
                max = Mathf.Max(max, p.y);
            }
            return max > min ? max - min : 0f;
        }

        // --- small helpers ------------------------------------------------------------------------

        /// <summary>
        /// Shift a legacy (offset-shifted) pose forward by <paramref name="offsetSeconds"/> so frame k once more
        /// represents Move time k/fps. Frames past the end clamp to the last available frame (the original bake did
        /// not store the tail). Returns a new <see cref="MoveMotion"/>; the input is left untouched.
        /// </summary>
        static MoveMotion RetimePose(MoveMotion src, float offsetSeconds, float fps)
        {
            if (src == null) return null;
            int shift = Mathf.Max(0, Mathf.RoundToInt(offsetSeconds * fps));
            if (shift == 0) return src;

            int n = src.FrameCount;
            var dst = new MoveMotion { fps = src.fps };
            dst.jointNames.AddRange(src.jointNames);
            dst.jointParents.AddRange(src.jointParents);
            for (int k = 0; k < n; k++)
            {
                int srcIdx = Mathf.Min(k + shift, n - 1);
                dst.frames.Add(src.frames[srcIdx]);
            }
            Debug.Log($"[MoveAIFusionBaker] Re-timed legacy pose by +{shift} frames ({offsetSeconds:F2}s) to align with the ARKit/GLB clock.");
            return dst;
        }

        static MoveMotionFrame CloneFrame(MoveMotionFrame src, int jointCount)
        {
            var f = new MoveMotionFrame(jointCount);
            if (src == null) return f;
            f.time = src.time;
            f.rootPosition = src.rootPosition;
            int c = Mathf.Min(jointCount, src.localRotations.Length);
            for (int i = 0; i < c; i++)
            {
                f.localRotations[i] = src.localRotations[i];
                if (i < src.localPositions.Length) f.localPositions[i] = src.localPositions[i];
            }
            return f;
        }

        static Vector3 Centroid(List<Vector3> pts)
        {
            Vector3 sum = Vector3.zero;
            foreach (var p in pts) sum += p;
            return pts.Count > 0 ? sum / pts.Count : Vector3.zero;
        }

        static float Median(List<float> v)
        {
            if (v.Count == 0) return 0f;
            v.Sort();
            return v[v.Count / 2];
        }

        static int CountTrue(bool[] arr)
        {
            int c = 0;
            foreach (var b in arr) if (b) c++;
            return c;
        }
    }
}
