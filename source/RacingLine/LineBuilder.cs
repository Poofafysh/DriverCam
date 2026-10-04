using System;
using System.Diagnostics;
using UnityEngine;

namespace RacingLine
{
    /// <summary>A finished racing line: one sideways offset per sample along the road's centre line.</summary>
    internal sealed class Line
    {
        public IntPtr PathPtr;
        public float Step, Length, HalfWidth, Limit;
        public int N;
        public float[] Px, Py, Pz, Nx, Nz;   // centre line and right-hand normal per sample
        public float[] E;                     // offset from the centre, metres, + = right

        public Vector3 Point(int i, float lift) => new Vector3(Px[i] + Nx[i] * E[i], Py[i] + lift, Pz[i] + Nz[i] * E[i]);
        public Vector3 Edge(int i, float side, float lift) => new Vector3(Px[i] + Nx[i] * side, Py[i] + lift, Pz[i] + Nz[i] * side);
    }

    /// <summary>
    /// Builds the minimum-curvature line from the run's centre line (design doc: "Where the optimal line comes from").
    ///
    /// Work is spread over frames within a time budget (Safety rules 5 and 6): sampling calls the game on the main thread
    /// a slice at a time; the solve is plain C# on our own arrays. Two levels: a coarse line (every 4th sample, 10 m by
    /// default) converges the long corner shapes fast, then the full-resolution line refines it. Each level is projected over-relaxed Gauss-Seidel on
    ///   minimise sum |Q(i-1) - 2 Q(i) + Q(i+1)|^2,  Q(i) = P(i) + e(i) N(i),  |e(i)| &lt;= road half width - margin
    /// i.e. make the line as straight as the road allows: outside, apex, outside.
    /// </summary>
    internal sealed class LineBuilder
    {
        private enum Stage { Sampling, Coarse, Fine, Done, Failed }

        private const int CoarseStride = 4;
        private const int CoarseMaxSweeps = 3000, FineMaxSweeps = 800;
        private const float CoarseTol = 0.002f, FineTol = 0.001f;

        public readonly IntPtr PathPtr;
        public readonly float PathLength;
        public string FailReason { get; private set; }
        public bool Finished => _stage == Stage.Done || _stage == Stage.Failed;
        public Line Result => _stage == Stage.Done ? _line : null;

        private readonly Line _line;
        private Stage _stage;
        private int _sampled;
        private float[] _cx, _cz, _cnx, _cnz, _ce;   // coarse level
        private int _coarseN, _coarseSweeps, _fineSweeps, _frames;
        private readonly Stopwatch _work = new Stopwatch();

        public LineBuilder(IntPtr pathPtr, float pathLength, float roadWidth, float margin, float step)
        {
            PathPtr = pathPtr;
            PathLength = pathLength;
            step = Mathf.Clamp(step, 1f, 10f);
            int n = Mathf.FloorToInt(pathLength / step) + 1;
            _line = new Line
            {
                PathPtr = pathPtr, Step = step, Length = pathLength, N = n,
                HalfWidth = roadWidth * 0.5f, Limit = Mathf.Max(0f, roadWidth * 0.5f - margin),
                Px = new float[n], Py = new float[n], Pz = new float[n], Nx = new float[n], Nz = new float[n], E = new float[n],
            };
        }

        /// <summary>Does up to budgetMs of work. Returns true when finished (check Result / FailReason).</summary>
        public bool Step(float budgetMs)
        {
            if (Finished) return true;
            _frames++;
            var sw = Stopwatch.StartNew();
            _work.Start();
            try
            {
                while (sw.Elapsed.TotalMilliseconds < budgetMs && !Finished)
                {
                    switch (_stage)
                    {
                        case Stage.Sampling: SampleSlice(); break;
                        case Stage.Coarse: CoarseSweeps(8); break;
                        case Stage.Fine: FineSweeps(2); break;
                    }
                }
            }
            finally { _work.Stop(); }
            return Finished;
        }

        public string Summary()
        {
            var l = _line;
            int atLimit = 0; float maxE = 0f;
            for (int i = 0; i < l.N; i++) { float a = Mathf.Abs(l.E[i]); maxE = Mathf.Max(maxE, a); if (a > l.Limit - 0.05f) atLimit++; }
            return $"{l.Length / 1000f:0.00} km, {l.N} samples every {l.Step:0.#} m, road {l.HalfWidth * 2f:0.#} m (line within ±{l.Limit:0.#} m), " +
                   $"coarse {_coarseSweeps} + fine {_fineSweeps} sweeps, {_work.Elapsed.TotalMilliseconds:0} ms of work over {_frames} frames, " +
                   $"max offset {maxE:0.0} m, {100f * atLimit / Mathf.Max(1, l.N):0}% of samples at the edge limit";
        }

        private void Fail(string reason) { FailReason = reason; _stage = Stage.Failed; }

        private void SampleSlice()
        {
            int count = Mathf.Min(200, _line.N - _sampled);
            if (!GameApi.Sample(PathPtr, _line.Step, _sampled, count, _line.Px, _line.Py, _line.Pz, _line.Nx, _line.Nz))
            { Fail("path gone, replaced, or returned non-finite values while sampling"); return; }
            _sampled += count;
            if (_sampled >= _line.N) StartCoarse();
        }

        private void StartCoarse()
        {
            var l = _line;
            _coarseN = (l.N - 1) / CoarseStride + 1;
            _cx = new float[_coarseN]; _cz = new float[_coarseN];
            _cnx = new float[_coarseN]; _cnz = new float[_coarseN]; _ce = new float[_coarseN];
            for (int c = 0; c < _coarseN; c++)
            {
                int i = c * CoarseStride;
                _cx[c] = l.Px[i]; _cz[c] = l.Pz[i]; _cnx[c] = l.Nx[i]; _cnz[c] = l.Nz[i];
            }
            _stage = _coarseN >= 5 ? Stage.Coarse : Stage.Fine;
        }

        private void CoarseSweeps(int sweeps)
        {
            for (int s = 0; s < sweeps; s++)
            {
                float change = LineSolver.Sweep(_cx, _cz, _cnx, _cnz, _ce, _coarseN, _line.Limit);
                _coarseSweeps++;
                if (float.IsNaN(change)) { Fail("solver diverged (coarse)"); return; }
                if (change < CoarseTol || _coarseSweeps >= CoarseMaxSweeps) { StartFine(); return; }
            }
        }

        private void StartFine()
        {
            var l = _line;
            LineSolver.Upsample(_ce, _coarseN, CoarseStride, l.E, l.N);   // initialise the fine line from the coarse one
            _cx = _cz = _cnx = _cnz = _ce = null;
            _stage = Stage.Fine;
        }

        private void FineSweeps(int sweeps)
        {
            var l = _line;
            for (int s = 0; s < sweeps; s++)
            {
                float change = LineSolver.Sweep(l.Px, l.Pz, l.Nx, l.Nz, l.E, l.N, l.Limit);
                _fineSweeps++;
                if (float.IsNaN(change)) { Fail("solver diverged (fine)"); return; }
                if (change < FineTol || _fineSweeps >= FineMaxSweeps) { _stage = Stage.Done; return; }
            }
        }

    }
}
