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
            // Stillness is measured GEOMETRICALLY (does the limb stay parked in a small along-wall radius?) instead of
            // from frame-to-frame speed, because a lurchy recording inflates speed and was hiding real rests.
            public Vector3 stillAnchor;   // candidate rest centre we are testing for stillness (pre-contact)
            public float stillTime;       // how long the limb has stayed within stillRadius of stillAnchor
            public bool hasStillAnchor;

            public bool inContact;
            public Vector3 lockedLocal;   // running-averaged contact point (depth pinned to the wall surface)
            public int sampleCount;       // samples averaged into lockedLocal
            public float dwellTime;
            public float contactStartTime;
            public float releaseGrace;    // time spent currently OFF the locked point (bridges brief jitter/slips)
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
            // This OFFLINE pass scans the whole recording for still rests and turns them into holds. Playback never
            // looks at motion again — it just snaps to the holds found here. So detection is allowed to be broad
            // (gather likely candidates, then merge/reinforce them) since a few false holds are cheap: at playback a
            // limb only snaps when it is actually over one and close to the wall.
            //
            // Stillness is decided by POSITION, not speed: a limb that stays inside a small along-wall radius for a
            // short time is resting, even if the raw recording lurches frame-to-frame (which inflates speed and used
            // to hide real rests). Release is distance-based with a grace window, so a single jitter spike no longer
            // fragments one 1s rest into several sub-dwell slivers that all get discarded.
            float depthBand = Mathf.Max(surfaceDepth + 0.01f, settings.holdDetectionDepthBand);
            float dwellNeeded = Mathf.Max(0f, settings.holdDwellSeconds);
            float mergeRadius = Mathf.Max(0.01f, settings.holdMergeRadius);
            float floorBand = Mathf.Max(0f, settings.holdFloorExclusionBand);

            // How tightly the limb must stay (along the wall) to be counted as resting, and how far it must wander to
            // be considered to have left the hold. Derived from the hold size so there are no new inspector knobs.
            float stillRadius = Mathf.Max(0.04f, mergeRadius * 0.5f);
            float releaseRadius = Mathf.Max(stillRadius * 2f, mergeRadius);
            // Confirm a rest quickly (low latency) but still long enough to reject a fly-through, then credit that
            // confirmation time toward dwell so a ~1s grip easily clears holdDwellSeconds.
            float stillEnterTime = Mathf.Clamp(dwellNeeded, 0.08f, 0.2f);
            // Bridge brief excursions (jitter, a momentary re-grip) up to this long without dropping the rest.
            float releaseGraceTime = 0.3f;

            bool hasFloor = TryEstimateFloorLocalY(asset, recording, contactJoints, anchorSettings, invertFacing,
                routeRootLocalOffset, out float floorLocalY);

            var states = new LimbScanState[4];
            var solverState = default(FusedPoseSolver.AnchorState);

            // Per-limb detection diagnostics (logged at the end so you can see WHY a grip did or didn't become a hold).
            var statRegistered = new int[4];   // rests long enough to register/reinforce a hold
            var statDiscarded = new int[4];     // rests dropped because dwell < holdDwellSeconds (too brief)
            var statBridged = new int[4];       // jitter/slip excursions that were bridged instead of splitting a rest
            var statBestDwell = new float[4];   // longest single rest seen per limb (incl. discarded ones)

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

                    float depth = p.z - wallZ;
                    // A joint AT or BEHIND the wall surface is clearly on the wall; depthBand is intentionally wide and
                    // includes negative depth, so "inside the wall no matter how far" already counts as eligible.
                    bool nearWall = depth <= depthBand;
                    bool floorStand = !isHand && hasFloor && (p.y - floorLocalY) < floorBand;
                    bool eligible = nearWall && !floorStand;

                    // Stillness/lookup happen in the along-wall (X up, Y up) plane only — depth is the noisiest axis on
                    // a flat wall, so ignoring it here makes detection robust to the recording's depth shake.
                    float ux = p.x, uy = p.y;

                    if (!st.inContact)
                    {
                        if (!eligible)
                        {
                            st.hasStillAnchor = false;
                            st.stillTime = 0f;
                        }
                        else
                        {
                            float da = st.hasStillAnchor
                                ? Mathf.Sqrt(Sqr(ux - st.stillAnchor.x) + Sqr(uy - st.stillAnchor.y))
                                : float.MaxValue;
                            if (st.hasStillAnchor && da <= stillRadius)
                            {
                                st.stillTime += dt;
                            }
                            else
                            {
                                st.stillAnchor = p;
                                st.stillTime = 0f;
                                st.hasStillAnchor = true;
                            }

                            if (st.stillTime >= stillEnterTime)
                            {
                                st.inContact = true;
                                st.lockedLocal = new Vector3(st.stillAnchor.x, st.stillAnchor.y, surfaceZ);
                                st.sampleCount = 1;
                                st.dwellTime = st.stillTime;          // credit the confirmation time
                                st.contactStartTime = t - st.stillTime;
                                st.releaseGrace = 0f;
                                st.hasStillAnchor = false;
                                st.stillTime = 0f;
                            }
                        }
                    }
                    else
                    {
                        float dl = Mathf.Sqrt(Sqr(ux - st.lockedLocal.x) + Sqr(uy - st.lockedLocal.y));
                        bool onHold = eligible && dl <= releaseRadius;
                        bool liftedOff = depth > depthBand * 1.6f;

                        if (onHold)
                        {
                            st.releaseGrace = 0f;
                            st.dwellTime += dt;
                            // Refine the locked point toward the rest centroid (along the wall; depth pinned to skin).
                            st.sampleCount++;
                            float w = 1f / st.sampleCount;
                            st.lockedLocal = new Vector3(
                                Mathf.Lerp(st.lockedLocal.x, p.x, w),
                                Mathf.Lerp(st.lockedLocal.y, p.y, w),
                                surfaceZ);
                        }
                        else if (liftedOff || floorStand)
                        {
                            // Clear departure (off the surface / onto the floor): end the rest immediately.
                            FlushState(ref st, li, map, isHand, mergeRadius);
                            st.stillAnchor = p;
                            st.stillTime = 0f;
                            st.hasStillAnchor = eligible;
                        }
                        else
                        {
                            // Brief along-wall excursion: hold the lock through a grace window so jitter / a momentary
                            // slip doesn't split one grip into several short holds. Dwell is NOT counted while away.
                            if (st.releaseGrace <= 0f) statBridged[li]++;
                            st.releaseGrace += dt;
                            if (st.releaseGrace > releaseGraceTime)
                            {
                                FlushState(ref st, li, map, isHand, mergeRadius);
                                st.stillAnchor = p;
                                st.stillTime = 0f;
                                st.hasStillAnchor = eligible;
                            }
                        }
                    }
                }
            }

            FlushStates(states, map, mergeRadius);

            // ---- detection diagnostics --------------------------------------------------------------
            // One line per limb: how many rests became holds, how many were dropped for being too brief (and the
            // longest rest seen — if that's < holdDwellSeconds the grip just wasn't held long enough / was too shaky),
            // and how many jitter excursions were bridged (high numbers = a lurchy recording around that limb).
            string[] limbNames = { "L-hand", "R-hand", "L-foot", "R-foot" };
            var sb = new System.Text.StringBuilder();
            sb.Append($"[ClimbingHoldDetector] {map.Count} holds from {asset.FrameCount} frames @ {frameRate:F0}fps. ");
            sb.Append($"(stillR={stillRadius:F2}m releaseR={releaseRadius:F2}m enterT={stillEnterTime:F2}s dwell>={dwellNeeded:F2}s depthBand={depthBand:F2}m)\n");
            for (int i = 0; i < 4; i++)
            {
                sb.Append($"  {limbNames[i]}: registered={statRegistered[i]} discarded(short)={statDiscarded[i]} " +
                          $"bridged={statBridged[i]} longestRest={statBestDwell[i]:F2}s");
                if (statRegistered[i] == 0 && statBestDwell[i] > 0f && statBestDwell[i] < dwellNeeded)
                    sb.Append("  <-- rests too brief: lower holdDwellSeconds or hold longer/steadier");
                sb.Append('\n');
            }
            Debug.Log(sb.ToString());

            return true;

            void FlushStates(LimbScanState[] scanStates, ClimbingHoldMap holdMap, float radius)
            {
                for (int i = 0; i < scanStates.Length; i++)
                {
                    bool isHand = i == (int)WallProjectionResolver.ClimbLimb.LeftHand ||
                                  i == (int)WallProjectionResolver.ClimbLimb.RightHand;
                    FlushState(ref scanStates[i], i, holdMap, isHand, radius);
                    scanStates[i] = default;
                }
            }

            void FlushState(ref LimbScanState st, int limb, ClimbingHoldMap holdMap, bool isHand, float radius)
            {
                if (st.inContact)
                {
                    if (st.dwellTime > statBestDwell[limb]) statBestDwell[limb] = st.dwellTime;
                    if (st.dwellTime >= dwellNeeded)
                    {
                        holdMap.Observe(st.lockedLocal, isHand, st.dwellTime, radius, st.contactStartTime + st.dwellTime);
                        statRegistered[limb]++;
                    }
                    else
                    {
                        statDiscarded[limb]++;
                    }
                }
                st.inContact = false;
                st.dwellTime = 0f;
                st.contactStartTime = 0f;
                st.sampleCount = 0;
                st.releaseGrace = 0f;
            }

            static float Sqr(float x) => x * x;
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
