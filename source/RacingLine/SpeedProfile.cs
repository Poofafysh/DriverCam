using System;

namespace RacingLine
{
    /// <summary>
    /// Reference speed along the racing line: what a tidy driver could carry at each point (design doc v2, after
    /// Kapania/Gerdes' two-pass speed profile). Plain .NET, testable outside the game.
    ///   1. cornering limit: v = min(vTop, sqrt(aGrip / |curvature|))
    ///   2. backward pass: you must be able to brake down to every limit ahead (aBrake)
    ///   3. forward pass: you can only accelerate so fast out of each limit (aAccel)
    /// </summary>
    internal static class SpeedProfile
    {
        internal static float[] Build(float[] lineCurvature, int n, float step, float vTop, float aGrip, float aBrake, float aAccel)
        {
            var v = new float[n];
            for (int i = 0; i < n; i++)
            {
                float k = Math.Abs(lineCurvature[i]);
                v[i] = k < 1e-5f ? vTop : Math.Min(vTop, (float)Math.Sqrt(aGrip / k));
            }
            for (int i = n - 2; i >= 0; i--) v[i] = Math.Min(v[i], (float)Math.Sqrt(v[i + 1] * v[i + 1] + 2f * aBrake * step));
            for (int i = 1; i < n; i++) v[i] = Math.Min(v[i], (float)Math.Sqrt(v[i - 1] * v[i - 1] + 2f * aAccel * step));
            return v;
        }

        /// <summary>
        /// Tracks a running 95th percentile of the car's cornering load (m/s^2) without storing samples: each sample nudges
        /// the estimate up by 0.95 * rate if above, down by 0.05 * rate if below. Settles where 5% of samples are higher.
        /// </summary>
        internal sealed class GripEstimate
        {
            public float Value;
            private readonly float _min, _max, _rate;
            public GripEstimate(float start, float min = 5f, float max = 40f, float rate = 0.05f) { Value = start; _min = min; _max = max; _rate = rate; }
            public void Add(float sample)
            {
                if (float.IsNaN(sample) || sample <= 0f) return;
                Value += sample > Value ? 0.95f * _rate : -0.05f * _rate;
                if (Value < _min) Value = _min; else if (Value > _max) Value = _max;
            }
        }
    }
}
