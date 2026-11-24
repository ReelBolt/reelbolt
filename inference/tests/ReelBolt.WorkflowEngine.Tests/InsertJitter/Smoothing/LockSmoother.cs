#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Smoothing;

/// <summary>
/// K6: stationary lock with hysteresis. A run of frames whose raw coordinates all lie within <c>kEnter</c> sigma peak-to-peak
/// over <c>minRun</c> frames starts a lock; the lock extends while each next sample stays within <c>kExit</c> sigma of the running
/// median of the segment (loose exit, strict entry); a locked segment is output as its median (a hard hold), with a 3-frame
/// cross-fade into the base smoother's output at both ends. Frames outside a lock go to the <c>base</c> smoother (raw if null).
/// The bound on a lock is by construction: every locked sample is within kExit sigma of the median, so a slow drift cannot be erased.
/// </summary>
internal sealed class LockSmoother : ITrackSmoother
{
    private readonly ITrackSmoother? _base;
    private readonly double _kEnter, _kExit;
    private readonly SigmaPolicy _sigma;
    private readonly int _minRun;

    public LockSmoother(ITrackSmoother? @base, double kEnter = 4.0, double kExit = 6.0, int minRun = 8, SigmaPolicy? sigma = null)
    {
        _base = @base; _kEnter = kEnter; _kExit = kExit; _minRun = minRun; _sigma = sigma ?? new SigmaPolicy(0.05);
    }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"K6 stationary lock enter {_kEnter:0.#} exit {_kExit:0.#} sigma, min {_minRun} over [{_base?.Name ?? "raw"}]");

    public IReadOnlyList<Quad> Smooth(IReadOnlyList<Quad> track, double fps, double noisePx)
    {
        int n = track.Count;
        IReadOnlyList<Quad> baseOut = _base is null ? track : _base.Smooth(track, fps, noisePx);
        if (n < _minRun)
            return baseOut;
        double[][] s = TrackSeries.ToSeries(track);
        double[][] b = TrackSeries.ToSeries(baseOut);
        double sigma = _sigma.Resolve(s, noisePx);
        var o = TrackSeries.Clone(b);
        var segments = new List<(int From, int To)>();
        int i = 0;
        while (i + _minRun <= n)
        {
            if (PeakToPeak(s, i, i + _minRun - 1) > _kEnter * sigma)
            {
                i++;
                continue;
            }

            int to = i + _minRun - 1;
            double[] med = Medians(s, i, to);
            while (to + 1 < n && MaxDeviation(s, to + 1, med) <= _kExit * sigma)
            {
                to++;
                if ((to - i) % 4 == 0)
                    med = Medians(s, i, to);
            }

            segments.Add((i, to));
            i = to + 1;
        }

        foreach ((int from, int to) in segments)
        {
            double[] med = Medians(s, from, to);
            for (int f = from; f <= to; f++)
            {
                // Distance to a lock boundary that has unlocked frames behind it; the track's own ends need no fade.
                double edge = Math.Min(from > 0 ? f - from : double.MaxValue, to < n - 1 ? to - f : double.MaxValue);
                double alpha = edge >= 3 ? 1.0 : (edge + 1) / 4.0;
                for (int c = 0; c < 8; c++)
                    o[c][f] = b[c][f] + alpha * (med[c] - b[c][f]);
            }
        }

        return TrackSeries.Build(track, o);
    }

    private static double PeakToPeak(double[][] s, int from, int to)
    {
        double worst = 0;
        foreach (double[] x in s)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int j = from; j <= to; j++)
            {
                lo = Math.Min(lo, x[j]);
                hi = Math.Max(hi, x[j]);
            }

            worst = Math.Max(worst, hi - lo);
        }

        return worst;
    }

    private static double[] Medians(double[][] s, int from, int to) =>
        s.Select(x => TrackSeries.Median(Enumerable.Range(from, to - from + 1).Select(j => x[j]))).ToArray();

    private static double MaxDeviation(double[][] s, int j, double[] med)
    {
        double worst = 0;
        for (int c = 0; c < s.Length; c++)
            worst = Math.Max(worst, Math.Abs(s[c][j] - med[c]));
        return worst;
    }
}
