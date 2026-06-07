using UnityEngine;

namespace BodyTracking.Playback.PostProcess
{
    /// <summary>
    /// "1€ Filter" (Casiez, Roussel, Vogel — CHI 2012): a speed-adaptive low-pass filter. It removes jitter when a
    /// signal is slow/still (low cutoff) but stays low-latency when it moves fast (cutoff rises with speed), so it
    /// smooths a held pose without lagging a jump or a fast reach. Dependency-free; one scalar core reused for
    /// Vector3 (per-axis) and Quaternion (per-component + normalize).
    ///
    /// Tuning: lower <c>minCutoff</c> => more jitter removed but more lag while slow; raise <c>beta</c> => less lag
    /// while fast. Defaults are a good starting point for mocap joints in metres.
    /// </summary>
    public sealed class OneEuroFilterScalar
    {
        public float MinCutoff;   // Hz; jitter floor while slow.
        public float Beta;        // speed coefficient; reduces lag while fast.
        public float DCutoff;     // Hz; cutoff for the derivative used to drive Beta.

        bool _has;
        float _xPrev;
        float _dxPrev;

        public OneEuroFilterScalar(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            MinCutoff = minCutoff;
            Beta = beta;
            DCutoff = dCutoff;
        }

        public void Reset() => _has = false;

        public float Filter(float x, float dt)
        {
            if (!_has || dt <= 0f || float.IsNaN(x) || float.IsInfinity(x))
            {
                if (!float.IsNaN(x) && !float.IsInfinity(x))
                {
                    _xPrev = x;
                    _dxPrev = 0f;
                    _has = true;
                }
                return x;
            }

            float dx = (x - _xPrev) / dt;
            float edx = Lerp(_dxPrev, dx, Alpha(DCutoff, dt));
            float cutoff = MinCutoff + Beta * Mathf.Abs(edx);
            float xHat = Lerp(_xPrev, x, Alpha(cutoff, dt));

            _xPrev = xHat;
            _dxPrev = edx;
            return xHat;
        }

        static float Alpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(1e-4f, cutoff));
            return 1f / (1f + tau / dt);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>Per-axis 1€ filter for a position. The three axes share the same parameters but filter independently.</summary>
    public sealed class OneEuroFilterVector3
    {
        readonly OneEuroFilterScalar _x, _y, _z;

        public OneEuroFilterVector3(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            _x = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
            _y = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
            _z = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
        }

        public void SetParams(float minCutoff, float beta, float dCutoff = 1.0f)
        {
            _x.MinCutoff = _y.MinCutoff = _z.MinCutoff = minCutoff;
            _x.Beta = _y.Beta = _z.Beta = beta;
            _x.DCutoff = _y.DCutoff = _z.DCutoff = dCutoff;
        }

        public void Reset() { _x.Reset(); _y.Reset(); _z.Reset(); }

        public Vector3 Filter(Vector3 v, float dt) => new Vector3(_x.Filter(v.x, dt), _y.Filter(v.y, dt), _z.Filter(v.z, dt));
    }

    /// <summary>
    /// 1€ filter for a rotation: filters the four quaternion components (with sign alignment to dodge the
    /// double-cover flip), then renormalizes. Good enough for the small per-frame deltas of mocap rotations.
    /// </summary>
    public sealed class OneEuroFilterQuaternion
    {
        readonly OneEuroFilterScalar _x, _y, _z, _w;
        bool _has;
        Quaternion _prev = Quaternion.identity;

        public OneEuroFilterQuaternion(float minCutoff = 1.0f, float beta = 0.0f, float dCutoff = 1.0f)
        {
            _x = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
            _y = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
            _z = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
            _w = new OneEuroFilterScalar(minCutoff, beta, dCutoff);
        }

        public void SetParams(float minCutoff, float beta, float dCutoff = 1.0f)
        {
            _x.MinCutoff = _y.MinCutoff = _z.MinCutoff = _w.MinCutoff = minCutoff;
            _x.Beta = _y.Beta = _z.Beta = _w.Beta = beta;
            _x.DCutoff = _y.DCutoff = _z.DCutoff = _w.DCutoff = dCutoff;
        }

        public void Reset() { _has = false; _x.Reset(); _y.Reset(); _z.Reset(); _w.Reset(); }

        public Quaternion Filter(Quaternion q, float dt)
        {
            // Keep the same hemisphere as the last output so component filtering doesn't see a 360° flip.
            if (_has && Quaternion.Dot(_prev, q) < 0f)
                q = new Quaternion(-q.x, -q.y, -q.z, -q.w);

            var f = new Quaternion(
                _x.Filter(q.x, dt),
                _y.Filter(q.y, dt),
                _z.Filter(q.z, dt),
                _w.Filter(q.w, dt));

            float n = Mathf.Sqrt(f.x * f.x + f.y * f.y + f.z * f.z + f.w * f.w);
            if (n < 1e-6f) f = q; else f = new Quaternion(f.x / n, f.y / n, f.z / n, f.w / n);

            _prev = f;
            _has = true;
            return f;
        }
    }
}
