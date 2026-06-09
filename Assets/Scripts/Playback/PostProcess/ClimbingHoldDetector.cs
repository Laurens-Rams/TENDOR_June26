using System.Collections.Generic;
using BodyTracking.Data;
using BodyTracking.MoveAI;
using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// Offline hold detector: scans a complete fused motion and builds the same hold map that a future ML/image
    /// detector can provide. This deliberately runs outside live playback, so pausing/scrubbing cannot add duplicate
    /// holds. Playback only consumes the generated <see cref="ClimbingHoldMap"/> for overlay/snap.
    /// </summary>
    public static class ClimbingHoldDetector
    {
        struct LimbScanState
        {
            public Vector3 prevLocal;
            public bool hasPrev;
            public float speedEma;
            public bool inContact;
            public Vector3 lockedLocal;
            public float dwellTime;
            public float contactStartTime;
        }

        public static bool GenerateFromRecording(MoveAIFusionAsset asset, HipRecording recording,
            WallProjectionResolver.ContactJoint[] contactJoints, in WallProjectionSettings settings,
            FusedPoseSolver.AnchorSettings anchorSettings, bool invertFacing, Vector3 routeRootLocalOffset,
            out ClimbingHoldMap map)
        {
            map = new ClimbingHoldMap();
            if (asset == null || asset.pose == null || asset.FrameCount <= 0 || contactJoints == null)
                return false;

            float frameRate = Mathf.Max(1f, asset.frameRate > 0f ? asset.frameRate : asset.pose.fps);
            float dt = 1f / frameRate;
            float wallZ = settings.wallDepthOffset;
            float surfaceZ = wallZ + Mathf.Max(0f, settings.wallSurfaceDepth);
            float surfaceDepth = Mathf.Max(0f, settings.wallSurfaceDepth);
            // This OFFLINE pass is the ONLY place limb speed is used: it scans the whole recording for still rests and
            // turns them into holds. Playback never looks at speed again — it just snaps to the holds found here. So
            // detection is allowed to be broad (gather likely candidates, then merge/reinforce them) since a few false
            // holds are cheap: at playback a limb only snaps when it is actually over one and close to the wall.
            float enterSpeed = Mathf.Max(0.18f, settings.contactStillnessSpeed);
            float exitSpeed = Mathf.Max(enterSpeed * 1.75f, settings.contactReleaseSpeed);
            float depthBand = Mathf.Max(surfaceDepth + 0.01f, settings.holdDetectionDepthBand);
            float dwellNeeded = Mathf.Max(0f, settings.holdDwellSeconds);
            float mergeRadius = Mathf.Max(0.01f, settings.holdMergeRadius);
            float floorBand = Mathf.Max(0f, settings.holdFloorExclusionBand);

            bool hasFloor = TryEstimateFloorLocalY(asset, recording, contactJoints, anchorSettings, invertFacing,
                routeRootLocalOffset, out float floorLocalY);

            var states = new LimbScanState[4];
            var solverState = default(FusedPoseSolver.AnchorState);

            for (int frame = 0; frame < asset.FrameCount; frame++)
            {
                float t = frame / frameRate;
                Vector3[] local = FusedPoseSolver.ComputeLocalJoints(asset, recording, t, ref solverState,
                    out _, invertFacing, anchorSettings, null);
                if (local == null) continue;

                for (int i = 0; i < local.Length; i++)
                    local[i] += routeRootLocalOffset;

                foreach (var contact in contactJoints)
                {
                    if (contact.worldIndex < 0 || contact.worldIndex >= local.Length) continue;
                    int li = (int)contact.limb;
                    bool isHand = contact.limb == WallProjectionResolver.ClimbLimb.LeftHand ||
                                  contact.limb == WallProjectionResolver.ClimbLimb.RightHand;
                    Vector3 p = local[contact.worldIndex];
                    ref LimbScanState st = ref states[li];

                    float rawSpeed = st.hasPrev ? (p - st.prevLocal).magnitude / dt : 0f;
                    float aSpeed = dt / (0.12f + dt);
                    st.speedEma = st.hasPrev ? st.speedEma + aSpeed * (rawSpeed - st.speedEma) : 0f;
                    st.prevLocal = p;
                    st.hasPrev = true;

                    float depth = p.z - wallZ;
                    bool floorStand = !isHand && hasFloor && (p.y - floorLocalY) < floorBand;

                    if (!st.inContact)
                    {
                        bool still = depth <= depthBand && st.speedEma <= enterSpeed && !floorStand;
                        if (still)
                        {
                            st.inContact = true;
                            st.lockedLocal = new Vector3(p.x, p.y, surfaceZ);
                            st.dwellTime = 0f;
                            st.contactStartTime = t;
                        }
                    }
                    else
                    {
                        bool movedAway = st.speedEma >= exitSpeed || depth > depthBand * 1.6f || floorStand;
                        if (movedAway)
                        {
                            FlushState(ref st, map, isHand, mergeRadius);
                            continue;
                        }
                    }

                    if (st.inContact)
                        st.dwellTime += dt;
                }
            }

            FlushStates(states, map, mergeRadius);
            return true;

            void FlushStates(LimbScanState[] scanStates, ClimbingHoldMap holdMap, float radius)
            {
                for (int i = 0; i < scanStates.Length; i++)
                {
                    bool isHand = i == (int)WallProjectionResolver.ClimbLimb.LeftHand ||
                                  i == (int)WallProjectionResolver.ClimbLimb.RightHand;
                    FlushState(ref scanStates[i], holdMap, isHand, radius);
                    scanStates[i] = default;
                }
            }

            void FlushState(ref LimbScanState st, ClimbingHoldMap holdMap, bool isHand, float radius)
            {
                if (st.inContact && st.dwellTime >= dwellNeeded)
                    holdMap.Observe(st.lockedLocal, isHand, st.dwellTime, radius, st.contactStartTime + st.dwellTime);
                st.inContact = false;
                st.dwellTime = 0f;
                st.contactStartTime = 0f;
            }
        }

        static bool TryEstimateFloorLocalY(MoveAIFusionAsset asset, HipRecording recording,
            WallProjectionResolver.ContactJoint[] contactJoints, FusedPoseSolver.AnchorSettings anchorSettings,
            bool invertFacing, Vector3 routeRootLocalOffset, out float floorLocalY)
        {
            floorLocalY = 0f;
            var footYs = new List<float>();
            float frameRate = Mathf.Max(1f, asset.frameRate > 0f ? asset.frameRate : asset.pose.fps);
            var state = default(FusedPoseSolver.AnchorState);

            for (int frame = 0; frame < asset.FrameCount; frame++)
            {
                float t = frame / frameRate;
                Vector3[] local = FusedPoseSolver.ComputeLocalJoints(asset, recording, t, ref state,
                    out _, invertFacing, anchorSettings, null);
                if (local == null) continue;

                foreach (var contact in contactJoints)
                {
                    bool isFoot = contact.limb == WallProjectionResolver.ClimbLimb.LeftFoot ||
                                  contact.limb == WallProjectionResolver.ClimbLimb.RightFoot;
                    if (!isFoot || contact.worldIndex < 0 || contact.worldIndex >= local.Length)
                        continue;
                    footYs.Add(local[contact.worldIndex].y + routeRootLocalOffset.y);
                }
            }

            if (footYs.Count == 0) return false;
            footYs.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt(0.05f * (footYs.Count - 1)), 0, footYs.Count - 1);
            floorLocalY = footYs[idx];
            return true;
        }
    }
}
