namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Real per-beat times of one music track, tracked through tempo drift (see <see cref="MusicBeatTracker"/>).
/// </summary>
/// <param name="Bpm">Median tempo over the track.</param>
/// <param name="BeatSec"><c>60 / Bpm</c>.</param>
/// <param name="BeatPhaseSec">The best-fitting steady grid's phase, in <c>[0, BeatSec)</c> (the editor's grid fallback).</param>
/// <param name="BarPhaseSec">The first downbeat of the best-fitting steady grid, in <c>[0, 4 * BeatSec)</c>.</param>
/// <param name="BeatsPerBar">Always 4 in v1.</param>
/// <param name="BeatTimesSec">Tracked beats, strictly increasing, seconds, rounded to 0.001.</param>
/// <param name="FirstDownbeatIndex">Index into <paramref name="BeatTimesSec"/> of the first downbeat (0..3); downbeats are every 4th from it.</param>
/// <param name="DurationSec">The analysed track's length.</param>
/// <param name="DropSec">The drop, on a tracked beat (same meaning as <see cref="MusicBeatAnalysis.DropSec"/>), or null.</param>
/// <param name="OutroSec">The outro, on a tracked beat, or null.</param>
/// <param name="Confidence">0..1, same scale idea as <see cref="MusicBeatAnalysis.Confidence"/>.</param>
/// <param name="Steady">True when the local tempo stays within +-2 % of <paramref name="Bpm"/> over the whole track.</param>
/// <param name="HalfTempoScore">Relative support (0..1) for Bpm / 2, so the UI can offer a switch.</param>
/// <param name="DoubleTempoScore">Relative support (0..1) for Bpm * 2.</param>
public sealed record MusicBeatTrack(
    double Bpm,
    double BeatSec,
    double BeatPhaseSec,
    double BarPhaseSec,
    int BeatsPerBar,
    IReadOnlyList<double> BeatTimesSec,
    int FirstDownbeatIndex,
    double DurationSec,
    double? DropSec,
    double? OutroSec,
    double Confidence,
    bool Steady,
    double HalfTempoScore,
    double DoubleTempoScore)
{
    /// <summary>The tempo as a word an editor can reason with (same thresholds as <see cref="MusicBeatAnalysis.TempoWord"/>).</summary>
    public string TempoWord => Bpm switch
    {
        < 90 => "slow",
        < 120 => "medium",
        < 150 => "fast",
        _ => "very fast"
    };

    /// <summary>Every <see cref="BeatsPerBar"/>-th tracked beat from <see cref="FirstDownbeatIndex"/>.</summary>
    public IReadOnlyList<double> BarTimesSec
    {
        get
        {
            var bars = new List<double>();
            for (int i = FirstDownbeatIndex; i < BeatTimesSec.Count; i += BeatsPerBar)
                bars.Add(BeatTimesSec[i]);
            return bars;
        }
    }
}

/// <summary>
/// Pure-C# dynamic-programming beat tracker (Ellis 2007) that follows tempo drift:
/// <list type="number">
/// <item>The same onset envelope as <see cref="MusicBeatAnalyzer"/> (100 fps positive log-energy flux, local mean removed).</item>
/// <item>Global tempo: the autocorrelation peak over 70..180 BPM weighted by a log-Gaussian prior centred
/// on 120 BPM (sigma 1.5 octaves); a faster lag whose own autocorrelation is nearly as strong wins, so a
/// click track at 180 BPM is not reported as 90. The half- and double-tempo supports are kept.</item>
/// <item>Local tempo: an 8 s tempogram every 1 s over -30 %..+40 % of the global period, smoothed by a
/// Viterbi pass whose transition cost grows with the period change.</item>
/// <item>Beats: score(t) = onset(t) + max over tau of score(tau) - alpha * ln((t - tau) / P(t))^2, backtracked
/// from the best frame of the last period, each beat refined to its onset peak (parabolic).</item>
/// <item>Downbeat: the drop's beat when there is one, else the beat class with the most onset strength.</item>
/// </list>
/// Deterministic, never throws; null for noise, a drone, a lone transient, silence or a track under 4 s.
/// </summary>
public static class MusicBeatTracker
{
    private const int EnvelopeFps = 100;
    private const double MinDurationSec = 4.0;
    private const double PriorCentreBpm = 120;
    private const double PriorSigmaOctaves = 1.5;
    private const int WindowFrames = 800;
    private const int WindowHopFrames = 100;
    private const double Alpha = 100;
    private const double TransitionCost = 2.0;
    private const double MinTempogramCoefficient = 0.15;
    private const double MinStrongOnsetDb = 2.0;
    private const int MinStrongOnsets = 8;

    public static MusicBeatTrack? Track(float[] samples, int sampleRate)
    {
        try
        {
            return TrackCore(samples, sampleRate);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    private static MusicBeatTrack? TrackCore(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0)
            return null;
        double durationSec = samples.Length / (double)sampleRate;
        if (durationSec < MinDurationSec)
            return null;

        int hop = Math.Max(1, sampleRate / EnvelopeFps);
        int win = hop * 4;
        double fps = sampleRate / (double)hop;
        double offsetSec = (win - hop / 2.0) / sampleRate;
        double[]? onset = Envelope(samples, hop, win);
        if (onset is null)
            return null;
        int frames = onset.Length;
        double onsetMean = onset.Average();
        if (onsetMean <= 0)
            return null;

        // Beatless audio gate: a drone or one isolated transient has too few distinct onsets to carry a pulse.
        var strongFrames = new List<int>();
        for (int i = 1; i < frames - 1; i++)
        {
            if (onset[i] >= MinStrongOnsetDb && onset[i] >= onset[i - 1] && onset[i] > onset[i + 1]
                && (strongFrames.Count == 0 || i - strongFrames[^1] >= 5))
                strongFrames.Add(i);
        }

        if (strongFrames.Count < MinStrongOnsets)
            return null;

        var centered = new double[frames];
        for (int i = 0; i < frames; i++)
            centered[i] = onset[i] - onsetMean;

        // ---- Global tempo ----
        int minLag = Math.Max(2, (int)Math.Floor(fps * 60 / MusicBeatAnalyzer.MaxBpm));
        int maxLag = Math.Min(frames / 2 - 2, (int)Math.Ceiling(fps * 60 / MusicBeatAnalyzer.MinBpm));
        if (maxLag <= minLag + 1)
            return null;

        double ac0 = AutoCorr(centered, 0, frames, 0);
        if (ac0 <= 1e-12)
            return null;

        int bestLag = BestWeightedLag(centered, frames, minLag, maxLag, fps);
        // Prefer a faster lag that is nearly as periodic (a 180 BPM click track also repeats every 2 beats).
        while (bestLag / 2 >= minLag)
        {
            int half = bestLag / 2;
            double bestHalf = double.NegativeInfinity;
            int halfLag = half;
            for (int l = Math.Max(minLag, half - 1); l <= Math.Min(maxLag, half + 1); l++)
            {
                double v = AutoCorr(centered, 0, frames, l);
                if (v > bestHalf)
                {
                    bestHalf = v;
                    halfLag = l;
                }
            }

            if (bestHalf >= 0.8 * AutoCorr(centered, 0, frames, bestLag))
                bestLag = halfLag;
            else
                break;
        }

        double refinedLag = RefineLag(centered, frames, bestLag);
        double bestAc = Math.Max(1e-12, PeakNear(centered, frames, refinedLag));
        double halfTempoScore = Math.Clamp(PeakNear(centered, frames, refinedLag * 2) / bestAc, 0, 1);
        double doubleTempoScore = Math.Clamp(PeakNear(centered, frames, refinedLag / 2) / bestAc, 0, 1);

        // ---- Local tempo: tempogram + Viterbi ----
        double[] period = LocalPeriods(centered, frames, refinedLag, fps, out double medianCoefficient);
        if (medianCoefficient < MinTempogramCoefficient)
            return null;

        double[] periodAt = new double[frames];
        int windows = period.Length;
        for (int t = 0; t < frames; t++)
        {
            double pos = (t - WindowFrames / 2.0) / WindowHopFrames;
            if (frames <= WindowFrames)
                periodAt[t] = period[0];
            else if (pos <= 0)
                periodAt[t] = period[0];
            else if (pos >= windows - 1)
                periodAt[t] = period[windows - 1];
            else
            {
                int w = (int)pos;
                double f = pos - w;
                periodAt[t] = period[w] * (1 - f) + period[w + 1] * f;
            }
        }

        // ---- Beat DP ----
        double sd = Math.Sqrt(onset.Sum(v => (v - onsetMean) * (v - onsetMean)) / frames);
        double scale = sd > 1e-12 ? 1 / sd : 1;
        var score = new double[frames];
        var back = new int[frames];
        for (int t = 0; t < frames; t++)
        {
            double p = periodAt[t];
            int lo = Math.Max(0, (int)Math.Ceiling(t - 2 * p));
            int hi = (int)Math.Floor(t - p / 2);
            double best = 0;
            int arg = -1;
            for (int tau = lo; tau <= hi; tau++)
            {
                double ln = Math.Log((t - tau) / p);
                double v = score[tau] - Alpha * ln * ln;
                if (v > best)
                {
                    best = v;
                    arg = tau;
                }
            }

            score[t] = onset[t] * scale + best;
            back[t] = arg;
        }

        int lastStart = Math.Max(0, frames - (int)Math.Ceiling(periodAt[frames - 1]));
        int end = lastStart;
        for (int t = lastStart; t < frames; t++)
        {
            if (score[t] > score[end])
                end = t;
        }

        var framesList = new List<int>();
        for (int t = end; t >= 0; t = back[t])
        {
            framesList.Add(t);
            if (back[t] < 0)
                break;
        }

        framesList.Reverse();

        var beats = new List<double>();
        var strengths = new List<double>();
        foreach (int f in framesList)
        {
            int a = Math.Max(0, f - 2), b = Math.Min(frames - 1, f + 2);
            int peak = f;
            for (int i = a; i <= b; i++)
            {
                if (onset[i] > onset[peak])
                    peak = i;
            }

            double x = peak;
            if (peak > 0 && peak < frames - 1)
            {
                double y0 = onset[peak - 1], y1 = onset[peak], y2 = onset[peak + 1];
                double denom = y0 - 2 * y1 + y2;
                if (Math.Abs(denom) > 1e-12)
                    x += Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
            }

            double time = Math.Round((x * hop + win - hop / 2.0) / sampleRate, 3);
            if (beats.Count > 0 && time <= beats[^1])
                continue;
            beats.Add(time);
            strengths.Add(onset[peak]);
        }

        // No beats in leading/trailing silence: keep those within one period of the first/last strong onset.
        double periodSec = periodAt[frames / 2] / fps;
        double firstOnsetSec = (strongFrames[0] * hop + win - hop / 2.0) / sampleRate;
        double lastOnsetSec = (strongFrames[^1] * hop + win - hop / 2.0) / sampleRate;
        for (int i = beats.Count - 1; i >= 0; i--)
        {
            if (beats[i] < firstOnsetSec - periodSec || beats[i] > lastOnsetSec + periodSec)
            {
                beats.RemoveAt(i);
                strengths.RemoveAt(i);
            }
        }

        if (beats.Count < 4)
            return null;

        // ---- Tempo, steadiness, steady-grid phase ----
        // Median of long-span local tempos: the beat times sit on a 10 ms grid, so a single interval is
        // too coarse for the tempo but a 32-beat span is not.
        int tempoSpan = Math.Min(32, beats.Count - 1);
        var spanTempo = new double[beats.Count - tempoSpan];
        for (int i = 0; i < spanTempo.Length; i++)
            spanTempo[i] = (beats[i + tempoSpan] - beats[i]) / tempoSpan;
        double medianInterval = Median(spanTempo);
        double bpm = Math.Round(60.0 / medianInterval, 2);
        double beatSec = 60.0 / bpm;

        int span = Math.Min(8, beats.Count - 1);
        bool steady = true;
        for (int i = 0; i + span < beats.Count; i++)
        {
            double local = (beats[i + span] - beats[i]) / span;
            if (Math.Abs(local - medianInterval) > 0.02 * medianInterval)
            {
                steady = false;
                break;
            }
        }

        // A steady track: the least-squares line through (index, time) is the best grid.
        double gridStep = beatSec;
        double gridOrigin = 0;
        bool fitted = false;
        if (steady)
        {
            double n = beats.Count, sumI = n * (n - 1) / 2, sumII = (n - 1) * n * (2 * n - 1) / 6;
            double sumT = beats.Sum(), sumIT = 0;
            for (int i = 0; i < beats.Count; i++)
                sumIT += i * beats[i];
            double slope = (n * sumIT - sumI * sumT) / (n * sumII - sumI * sumI);
            if (slope > 0)
            {
                gridStep = slope;
                gridOrigin = (sumT - slope * sumI) / n;
                bpm = Math.Round(60.0 / slope, 2);
                beatSec = 60.0 / bpm;
                fitted = true;
            }
        }

        double beatPhase = fitted ? Mod(gridOrigin, gridStep) : CircularMean(beats, beatSec);
        double confidence = Math.Clamp((strengths.Average() / onsetMean - 1) / 4, 0, 1);

        // ---- Energy per tracked beat, drop and outro (as the analyzer does on its grid) ----
        var beatDb = new double[beats.Count];
        double lead = beatSec / 4;
        for (int k = 0; k < beats.Count; k++)
        {
            int a = Math.Max(0, (int)((beats[k] - lead) * sampleRate));
            int b = Math.Min(samples.Length, (int)(((k + 1 < beats.Count ? beats[k + 1] : durationSec + lead) - lead) * sampleRate));
            double sum = 0;
            for (int s = a; s < b; s++)
                sum += samples[s] * (double)samples[s];
            beatDb[k] = b > a ? 10 * Math.Log10(sum / (b - a) + 1e-12) : -120;
        }

        int window = Math.Max(4, (int)Math.Round(4.0 / beatSec));
        int? dropBeat = MusicBeatAnalyzer.FindDrop(beatDb, window);
        int? outroBeat = MusicBeatAnalyzer.FindOutro(beatDb, window, dropBeat);

        // ---- Downbeat ----
        int firstDownbeat;
        if (dropBeat is { } db)
        {
            firstDownbeat = db % 4;
        }
        else
        {
            var offsetScore = new double[4];
            for (int k = 0; k < beats.Count; k++)
                offsetScore[k % 4] += strengths[k];
            firstDownbeat = Array.IndexOf(offsetScore, offsetScore.Max());
        }

        var downbeats = new List<double>();
        for (int k = firstDownbeat; k < beats.Count; k += 4)
            downbeats.Add(beats[k]);
        double barPhase = fitted
            ? Mod(gridOrigin + firstDownbeat * gridStep, gridStep * 4)
            : CircularMean(downbeats, beatSec * 4);

        return new MusicBeatTrack(
            Bpm: bpm,
            BeatSec: Math.Round(beatSec, 5),
            BeatPhaseSec: Math.Round(beatPhase, 4),
            BarPhaseSec: Math.Round(barPhase, 4),
            BeatsPerBar: 4,
            BeatTimesSec: beats,
            FirstDownbeatIndex: firstDownbeat,
            DurationSec: Math.Round(durationSec, 3),
            DropSec: dropBeat is { } d ? beats[d] : null,
            OutroSec: outroBeat is { } o ? beats[o] : null,
            Confidence: Math.Round(confidence, 3),
            Steady: steady,
            HalfTempoScore: Math.Round(halfTempoScore, 3),
            DoubleTempoScore: Math.Round(doubleTempoScore, 3));
    }

    /// <summary>The analyzer's onset envelope (same operations, same order); null on digital silence or a track too short.</summary>
    private static double[]? Envelope(float[] samples, int hop, int win)
    {
        double fps = (double)EnvelopeFps;
        int frames = (samples.Length - win) / hop + 1;
        if (frames < 8)
            return null;

        var energy = new double[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            int start = i * hop;
            for (int s = start; s < start + win; s++)
                sum += samples[s] * (double)samples[s];
            energy[i] = 10 * Math.Log10(sum / win + 1e-12);
        }

        double maxEnergy = energy.Max();
        if (maxEnergy < -70)
            return null;
        for (int i = 0; i < frames; i++)
            energy[i] = Math.Max(energy[i], maxEnergy - 60);

        var flux = new double[frames];
        for (int i = 1; i < frames; i++)
            flux[i] = Math.Max(0, energy[i] - energy[i - 1]);

        int half = (int)Math.Round(fps * 0.25);
        var onset = new double[frames];
        var prefix = new double[frames + 1];
        for (int i = 0; i < frames; i++)
            prefix[i + 1] = prefix[i] + flux[i];
        for (int i = 0; i < frames; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(frames, i + half + 1);
            double localMean = (prefix[b] - prefix[a]) / (b - a);
            onset[i] = Math.Max(0, flux[i] - localMean);
        }

        return onset;
    }

    /// <summary>Mean lagged product over <c>[start, start + length)</c>; zero when the lag does not fit.</summary>
    private static double AutoCorr(double[] x, int start, int length, int lag)
    {
        if (lag < 0 || lag >= length)
            return 0;
        double sum = 0;
        int end = start + length - lag;
        for (int i = start; i < end; i++)
            sum += x[i] * x[i + lag];
        return sum / (length - lag);
    }

    private static int BestWeightedLag(double[] x, int frames, int minLag, int maxLag, double fps)
    {
        int best = minLag;
        double bestScore = double.NegativeInfinity;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double octaves = Math.Log2(60 * fps / lag / PriorCentreBpm);
            double weight = Math.Exp(-0.5 * octaves * octaves / (PriorSigmaOctaves * PriorSigmaOctaves));
            double v = AutoCorr(x, 0, frames, lag) * weight;
            if (v > bestScore)
            {
                bestScore = v;
                best = lag;
            }
        }

        return best;
    }

    private static double RefineLag(double[] x, int frames, int lag)
    {
        double y0 = AutoCorr(x, 0, frames, lag - 1), y1 = AutoCorr(x, 0, frames, lag), y2 = AutoCorr(x, 0, frames, lag + 1);
        double denom = y0 - 2 * y1 + y2;
        return Math.Abs(denom) > 1e-12 ? lag + Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5) : lag;
    }

    /// <summary>The autocorrelation maximum within one frame of a fractional lag.</summary>
    private static double PeakNear(double[] x, int frames, double lag)
    {
        int centre = (int)Math.Round(lag);
        double best = 0;
        for (int l = centre - 1; l <= centre + 1; l++)
            best = Math.Max(best, AutoCorr(x, 0, frames, l));
        return best;
    }

    /// <summary>Per-window beat period (fractional frames): an 8 s tempogram smoothed by a Viterbi pass.</summary>
    private static double[] LocalPeriods(double[] centered, int frames, double globalLag, double fps, out double medianCoefficient)
    {
        int lagLo = Math.Max(4, (int)Math.Ceiling(globalLag * 0.7));
        int lagHi = Math.Max(lagLo + 1, (int)Math.Floor(globalLag * 1.4));
        int lagCount = lagHi - lagLo + 1;

        int windowLen = Math.Min(WindowFrames, frames);
        int windows = frames <= WindowFrames ? 1 : (frames - WindowFrames) / WindowHopFrames + 1;
        var emission = new double[windows, lagCount];
        var peakCoefficient = new double[windows];
        var ac = new double[2 * lagHi + 3];

        for (int w = 0; w < windows; w++)
        {
            int start = Math.Min(w * WindowHopFrames, frames - windowLen);
            double a0 = AutoCorr(centered, start, windowLen, 0);
            if (a0 <= 1e-12)
                continue;
            for (int l = lagLo - 1; l <= lagHi + 1; l++)
                ac[l] = AutoCorr(centered, start, windowLen, l);
            for (int l = 2 * lagLo - 1; l <= 2 * lagHi + 1; l++)
                ac[l] = AutoCorr(centered, start, windowLen, l);

            double peak = 0;
            for (int i = 0; i < lagCount; i++)
            {
                int l = lagLo + i;
                double e = Math.Max(0, (ac[l] + 0.5 * ac[2 * l]) / (1.5 * a0));
                emission[w, i] = e;
                peak = Math.Max(peak, ac[l] / a0);
            }

            peakCoefficient[w] = peak;
        }

        medianCoefficient = Median(peakCoefficient);

        // Viterbi over lags: maximize emission minus a cost proportional to the log period change.
        var total = new double[windows, lagCount];
        var from = new int[windows, lagCount];
        for (int i = 0; i < lagCount; i++)
            total[0, i] = emission[0, i];
        for (int w = 1; w < windows; w++)
        {
            for (int i = 0; i < lagCount; i++)
            {
                double best = double.NegativeInfinity;
                int arg = i;
                for (int j = 0; j < lagCount; j++)
                {
                    double v = total[w - 1, j] - TransitionCost * Math.Abs(Math.Log((double)(lagLo + i) / (lagLo + j)));
                    if (v > best)
                    {
                        best = v;
                        arg = j;
                    }
                }

                total[w, i] = best + emission[w, i];
                from[w, i] = arg;
            }
        }

        int cur = 0;
        for (int i = 1; i < lagCount; i++)
        {
            if (total[windows - 1, i] > total[windows - 1, cur])
                cur = i;
        }

        var chosen = new int[windows];
        for (int w = windows - 1; w >= 0; w--)
        {
            chosen[w] = cur;
            cur = from[w, cur];
        }

        var period = new double[windows];
        for (int w = 0; w < windows; w++)
        {
            int i = chosen[w];
            double p = lagLo + i;
            if (i > 0 && i < lagCount - 1)
            {
                double y0 = emission[w, i - 1], y1 = emission[w, i], y2 = emission[w, i + 1];
                double denom = y0 - 2 * y1 + y2;
                if (Math.Abs(denom) > 1e-12)
                    p += Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
            }

            period[w] = p;
        }

        return period;
    }

    private static double Mod(double value, double step)
    {
        double r = value % step;
        return r < 0 ? r + step : r;
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>The mean phase of <paramref name="times"/> modulo <paramref name="step"/>, in <c>[0, step)</c>.</summary>
    private static double CircularMean(IReadOnlyList<double> times, double step)
    {
        if (times.Count == 0 || step <= 0)
            return 0;
        double sx = 0, sy = 0;
        foreach (double t in times)
        {
            double angle = 2 * Math.PI * (t % step) / step;
            sx += Math.Cos(angle);
            sy += Math.Sin(angle);
        }

        double phase = Math.Atan2(sy, sx) / (2 * Math.PI) * step;
        if (phase < 0)
            phase += step;
        return phase >= step ? 0 : phase;
    }
}
