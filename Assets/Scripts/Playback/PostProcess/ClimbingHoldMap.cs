using System;
using System.Collections.Generic;
using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// A single inferred climbing hold. Position is stored in RouteRoot-local space (the same wall-aligned frame the
    /// recorder stores joints in: X = along the wall, Y = up the wall, Z = depth out from the wall), so the hold is
    /// drift-stable and survives wall-source changes (RouteRoot vs AR plane). World positions are reconstructed on
    /// demand from the live RouteRoot transform.
    /// </summary>
    [Serializable]
    public sealed class ClimbingHold
    {
        public float u;     // RouteRoot-local X (along the wall)
        public float v;     // RouteRoot-local Y (up the wall)
        public float depth; // RouteRoot-local Z (out from the wall)
        public bool isHand; // hand-hold (true) vs foot-hold (false)
        public int observationCount;
        public float totalDwell;  // accumulated seconds rested on this hold
        public float firstSeen;
        public float lastSeen;

        /// <summary>
        /// Normalized 0..1 certainty that this is a real hold. Grows with the number of independent rests and the
        /// total dwell time, saturating so a single long touch can't read as fully confident.
        /// </summary>
        public float Confidence
        {
            get
            {
                // Each rest is worth ~1 unit, plus a smaller dwell contribution; saturate via 1 - e^-x.
                float score = observationCount + Mathf.Min(totalDwell, 6f) * 0.5f;
                return 1f - Mathf.Exp(-score / 2.5f);
            }
        }

        public Vector2 WallUV => new Vector2(u, v);

        /// <summary>Reconstruct the world position from the live RouteRoot transform.</summary>
        public Vector3 ToWorld(Transform routeRoot)
            => routeRoot != null ? routeRoot.TransformPoint(new Vector3(u, v, depth)) : new Vector3(u, v, depth);
    }

    /// <summary>
    /// A learned set of climbing holds inferred from where hands/feet rest still on the wall. Hand-holds and
    /// foot-holds are kept distinct. New observations within a merge radius reinforce an existing hold (running
    /// average + confidence bump) instead of creating duplicates, so repeated grabs converge on a stable point.
    /// A future ML/image-recognition detector can feed the very same <see cref="Observe"/> entry point.
    /// </summary>
    public sealed class ClimbingHoldMap
    {
        readonly List<ClimbingHold> holds = new List<ClimbingHold>();

        public IReadOnlyList<ClimbingHold> Holds => holds;
        public int Count => holds.Count;

        public void Clear() => holds.Clear();

        /// <summary>
        /// Record (or reinforce) a hold at a RouteRoot-local position. If an existing hold of the same category
        /// (hand vs foot) lies within <paramref name="mergeRadius"/> (measured in the along-wall/up plane), it is
        /// reinforced; otherwise a new hold is added.
        /// </summary>
        public ClimbingHold Observe(Vector3 routeLocal, bool isHand, float dwell, float mergeRadius, float now)
        {
            float u = routeLocal.x, v = routeLocal.y, depth = routeLocal.z;
            if (TryFindNearest(u, v, mergeRadius, isHand, out var existing))
            {
                int n = existing.observationCount + 1;
                float w = 1f / n;
                existing.u = Mathf.Lerp(existing.u, u, w);
                existing.v = Mathf.Lerp(existing.v, v, w);
                existing.depth = Mathf.Lerp(existing.depth, depth, w);
                existing.observationCount = n;
                existing.totalDwell += Mathf.Max(0f, dwell);
                existing.lastSeen = now;
                return existing;
            }

            var hold = new ClimbingHold
            {
                u = u,
                v = v,
                depth = depth,
                isHand = isHand,
                observationCount = 1,
                totalDwell = Mathf.Max(0f, dwell),
                firstSeen = now,
                lastSeen = now,
            };
            holds.Add(hold);
            return hold;
        }

        /// <summary>Nearest hold of the requested category within <paramref name="radius"/> (along-wall/up plane).</summary>
        public bool TryFindNearest(float u, float v, float radius, bool isHand, out ClimbingHold nearest)
        {
            nearest = null;
            float bestSqr = radius * radius;
            for (int i = 0; i < holds.Count; i++)
            {
                var h = holds[i];
                if (h.isHand != isHand) continue;
                float du = h.u - u, dv = h.v - v;
                float sqr = du * du + dv * dv;
                if (sqr <= bestSqr)
                {
                    bestSqr = sqr;
                    nearest = h;
                }
            }
            return nearest != null;
        }

        // ---- Serialization -----------------------------------------------------------------------

        [Serializable]
        private class HoldList
        {
            public List<ClimbingHold> holds = new List<ClimbingHold>();
        }

        /// <summary>Serialize the current holds to JSON (used by per-map persistence).</summary>
        public string ToJson()
        {
            var wrapper = new HoldList { holds = holds };
            return JsonUtility.ToJson(wrapper);
        }

        /// <summary>Replace the current holds with those parsed from JSON. Returns false on empty/invalid input.</summary>
        public bool LoadFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            HoldList wrapper;
            try { wrapper = JsonUtility.FromJson<HoldList>(json); }
            catch { return false; }
            if (wrapper == null || wrapper.holds == null) return false;
            holds.Clear();
            holds.AddRange(wrapper.holds);
            return true;
        }
    }
}
