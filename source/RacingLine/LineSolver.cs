using System;

namespace RacingLine
{
    /// <summary>
    /// The racing-line maths, in plain .NET with no Unity or game types, so it runs fast (no IL2CPP calls in the hot loop)
    /// and can be tested outside the game.
    ///
    /// Minimise sum |Q(i-1) - 2 Q(i) + Q(i+1)|^2 over offsets e(i), where Q(i) = P(i) + e(i) N(i) and |e(i)| &lt;= limit:
    /// make the line as straight as the road allows (outside, apex, outside). Heights are ignored: lane offsets are horizontal.
    /// </summary>
    internal static class LineSolver
    {
        internal const float Omega = 1.6f;   // over-relaxation

        /// <summary>
        /// One projected over-relaxed Gauss-Seidel sweep over e[1..n-2] (ends stay put). Returns the largest change in
        /// metres, or NaN if any value became non-finite.
        /// </summary>
        internal static float Sweep(float[] px, float[] pz, float[] nx, float[] nz, float[] e, int n, float limit)
        {
            float maxChange = 0f;
            for (int i = 1; i < n - 1; i++)
            {
                float nxi = nx[i], nzi = nz[i];
                float qmx = px[i - 1] + nx[i - 1] * e[i - 1], qmz = pz[i - 1] + nz[i - 1] * e[i - 1];
                float qpx = px[i + 1] + nx[i + 1] * e[i + 1], qpz = pz[i + 1] + nz[i + 1] * e[i + 1];

                // the three second differences containing Q(i), evaluated with Q(i) = P(i); e(i)N(i) enters them with +1, -2, +1
                float rx = qmx - 2f * px[i] + qpx, rz = qmz - 2f * pz[i] + qpz;
                float num = -2f * (rx * nxi + rz * nzi), den = 4f;
                if (i >= 2)
                {
                    float q2x = px[i - 2] + nx[i - 2] * e[i - 2], q2z = pz[i - 2] + nz[i - 2] * e[i - 2];
                    rx = q2x - 2f * qmx + px[i]; rz = q2z - 2f * qmz + pz[i];
                    num += rx * nxi + rz * nzi; den += 1f;
                }
                if (i <= n - 3)
                {
                    float q2x = px[i + 2] + nx[i + 2] * e[i + 2], q2z = pz[i + 2] + nz[i + 2] * e[i + 2];
                    rx = px[i] - 2f * qpx + q2x; rz = pz[i] - 2f * qpz + q2z;
                    num += rx * nxi + rz * nzi; den += 1f;
                }

                float next = e[i] + Omega * (-num / den - e[i]);
                if (float.IsNaN(next) || float.IsInfinity(next)) return float.NaN;   // bad input: the caller fails the build
                if (next > limit) next = limit; else if (next < -limit) next = -limit;
                float change = Math.Abs(next - e[i]);
                if (change > maxChange) maxChange = change;
                e[i] = next;
            }
            return maxChange;
        }

        /// <summary>Linear upsample of a coarse line (every stride-th sample) to n fine samples.</summary>
        internal static void Upsample(float[] coarse, int coarseN, int stride, float[] fine, int n)
        {
            for (int i = 0; i < n; i++)
            {
                float c = (float)i / stride;
                int c0 = Math.Min((int)c, coarseN - 1), c1 = Math.Min(c0 + 1, coarseN - 1);
                fine[i] = coarse[c0] + (coarse[c1] - coarse[c0]) * (c - c0);
            }
        }
    }
}
