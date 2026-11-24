namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The measured beat grid and energy landmarks of one music track (see docs/video-editing.md
/// "Cutting to the beat"). Every time is in seconds from the start of the track, which is also the
/// start of the program: background music always starts playing at output time 0.
/// </summary>
/// <param name="Bpm">Tempo in beats per minute.</param>
/// <param name="BeatSec">One beat, in seconds (<c>60 / Bpm</c>).</param>
/// <param name="BeatPhaseSec">The first beat, in <c>[0, BeatSec)</c>.</param>
/// <param name="BarPhaseSec">The first bar start (downbeat), in <c>[0, BarSec)</c>. A bar is four beats.</param>
/// <param name="DurationSec">The analysed track's length.</param>
/// <param name="DropSec">The drop — the biggest sustained rise in loudness — on a beat, or null when the track has none.</param>
/// <param name="OutroSec">Where the track winds down — the biggest sustained fall in loudness after the drop — on a beat, or null.</param>
/// <param name="Confidence">How strongly the onsets line up on the grid, 0..1. Below ~0.2 the grid is a guess.</param>
public sealed record MusicBeatAnalysis(
    double Bpm,
    double BeatSec,
    double BeatPhaseSec,
    double BarPhaseSec,
    double DurationSec,
    double? DropSec,
    double? OutroSec,
    double Confidence)
{
    /// <summary>One bar (four beats), in seconds.</summary>
    public double BarSec => BeatSec * 4;

    /// <summary>Every beat from the first one up to (not past) <paramref name="untilSec"/>.</summary>
    public IReadOnlyList<double> BeatTimes(double untilSec) => Grid(BeatPhaseSec, BeatSec, untilSec);

    /// <summary>Every bar start from the first downbeat up to (not past) <paramref name="untilSec"/>.</summary>
    public IReadOnlyList<double> BarTimes(double untilSec) => Grid(BarPhaseSec, BarSec, untilSec);

    /// <summary>The tempo as a word an editor can reason with: slow, medium, fast or very fast.</summary>
    public string TempoWord => Bpm switch
    {
        < 90 => "slow",
        < 120 => "medium",
        < 150 => "fast",
        _ => "very fast"
    };

    /// <summary>The 1-based bar the drop starts, or null.</summary>
    public int? DropBar => DropSec is { } d ? (int)Math.Round((d - BarPhaseSec) / BarSec) + 1 : null;

    /// <summary>The 1-based bar the outro starts, or null.</summary>
    public int? OutroBar => OutroSec is { } o ? (int)Math.Round((o - BarPhaseSec) / BarSec) + 1 : null;

    private static IReadOnlyList<double> Grid(double phase, double step, double untilSec)
    {
        var times = new List<double>();
        if (step <= 0)
            return times;
        for (double t = phase; t <= untilSec + 1e-9; t += step)
            times.Add(Math.Round(t, 4));
        return times;
    }
}

/// <summary>
/// Pure-C# tempo, beat-phase and energy analysis of a mono PCM music track — no ffmpeg, no model,
/// no external library (see docs/video-editing.md "Cutting to the beat"):
/// <list type="number">
/// <item>Onset strength: the positive frame-to-frame flux of log energy (10 ms hop, 40 ms window),
/// with its local mean removed so a crescendo is not mistaken for a run of onsets.</item>
/// <item>Tempo: the autocorrelation peak of the onset envelope between 70 and 180 BPM (parabolic
/// interpolation between lags), then refined, together with the beat phase, by maximizing the
/// mean onset strength sampled on the beat grid (0.01 BPM steps, one-hop phase steps).</item>
/// <item>Energy: the RMS level of every beat. The drop is the beat where the mean level of the
/// following ~4 s exceeds that of the preceding ~4 s by the most (the earliest such jump within
/// 80% of the biggest one, and at least 3 dB); the outro is the beat after the drop where the
/// level of everything that follows falls furthest below the preceding ~4 s (at least 4 dB).
/// A bar is four beats; bars are aligned so the drop lands on a downbeat, or, with no drop, on
/// the beat offset whose beats carry the most onset strength.</item>
/// </list>
/// Returns null for a track too short or too quiet to carry a grid.
/// </summary>
public static class MusicBeatAnalyzer
{
    public const double MinBpm = 70;
    public const double MaxBpm = 180;

    /// <summary>Onset-envelope frame rate (frames per second).</summary>
    private const int EnvelopeFps = 100;

    /// <summary>Shortest track worth gridding.</summary>
    private const double MinDurationSec = 4.0;

    public static MusicBeatAnalysis? Analyze(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0)
            return null;

        double durationSec = samples.Length / (double)sampleRate;
        if (durationSec < MinDurationSec)
            return null;

        int hop = Math.Max(1, sampleRate / EnvelopeFps);
        int win = hop * 4;
        double fps = sampleRate / (double)hop;
        int frames = (samples.Length - win) / hop + 1;
        if (frames < 8)
            return null;

        // ---- Log energy per frame, floored 60 dB under the loudest frame (silence is not an onset) ----
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
            return null; // digital silence
        for (int i = 0; i < frames; i++)
            energy[i] = Math.Max(energy[i], maxEnergy - 60);

        // ---- Onset strength: positive log-energy flux, local mean removed ----
        var flux = new double[frames];
        for (int i = 1; i < frames; i++)
            flux[i] = Math.Max(0, energy[i] - energy[i - 1]);

        int half = (int)Math.Round(fps * 0.25);
        double[] onset = new double[frames];
        double[] prefix = new double[frames + 1];
        for (int i = 0; i < frames; i++)
            prefix[i + 1] = prefix[i] + flux[i];
        for (int i = 0; i < frames; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(frames, i + half + 1);
            double localMean = (prefix[b] - prefix[a]) / (b - a);
            onset[i] = Math.Max(0, flux[i] - localMean);
        }

        double onsetMean = onset.Average();
        if (onsetMean <= 0)
            return null;

        // ---- Coarse tempo: autocorrelation over 70..180 BPM ----
        int minLag = Math.Max(1, (int)Math.Floor(fps * 60 / MaxBpm));
        int maxLag = Math.Min(frames - 2, (int)Math.Ceiling(fps * 60 / MinBpm));
        if (maxLag <= minLag + 1)
            return null;

        var centered = new double[frames];
        for (int i = 0; i < frames; i++)
            centered[i] = onset[i] - onsetMean;

        var ac = new double[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            if (lag <= 0 || lag >= frames)
                continue;
            double sum = 0;
            for (int i = 0; i + lag < frames; i++)
                sum += centered[i] * centered[i + lag];
            ac[lag] = sum / (frames - lag);
        }

        int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (ac[lag] > ac[bestLag])
                bestLag = lag;
        }

        double refinedLag = bestLag;
        if (bestLag > minLag - 1 && bestLag + 1 < ac.Length && bestLag - 1 > 0)
        {
            double y0 = ac[bestLag - 1], y1 = ac[bestLag], y2 = ac[bestLag + 1];
            double denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-12)
                refinedLag = bestLag + Math.Clamp(0.5 * (y0 - y2) / denom, -0.5, 0.5);
        }

        double coarseBpm = Math.Clamp(60 * fps / refinedLag, MinBpm, MaxBpm);

        // ---- Fine tempo + phase: maximize the mean onset on the beat grid ----
        double bestScore = double.NegativeInfinity;
        double bestBpm = coarseBpm;
        double bestPhase = 0;
        for (double bpm = Math.Max(MinBpm, coarseBpm - 1.5); bpm <= Math.Min(MaxBpm, coarseBpm + 1.5) + 1e-9; bpm += 0.01)
        {
            double periodFrames = fps * 60 / bpm;
            int phaseSteps = (int)Math.Ceiling(periodFrames);
            for (int p = 0; p < phaseSteps; p++)
            {
                double score = GridScore(onset, p, periodFrames);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBpm = bpm;
                    bestPhase = p / fps;
                }
            }
        }

        bestBpm = Math.Round(bestBpm, 2);
        double beatSec = 60.0 / bestBpm;

        // Frame i's flux measures what ENTERED the window, i.e. its last hop: the onset it reports
        // happened near the window's end, not its start.
        bestPhase = (bestPhase + (win - hop / 2.0) / sampleRate) % beatSec;
        double confidence = Math.Clamp((bestScore / onsetMean - 1) / 4, 0, 1);

        // ---- Energy per beat (RMS dB). Each beat's window opens a quarter beat EARLY: an attack
        // that lands a few milliseconds before the measured grid line belongs to its own beat, not
        // to the quiet one before it. ----
        List<double> beats = [];
        for (double t = bestPhase; t < durationSec; t += beatSec)
            beats.Add(t);
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
        int? dropBeat = FindDrop(beatDb, window);
        int? outroBeat = FindOutro(beatDb, window, dropBeat);

        // ---- Bars: the drop is a downbeat; otherwise the strongest of the four beat offsets ----
        int barOffset;
        if (dropBeat is { } db)
        {
            barOffset = db % 4;
        }
        else
        {
            var offsetScore = new double[4];
            for (int k = 0; k < beats.Count; k++)
                offsetScore[k % 4] += Interpolate(onset, beats[k] * fps);
            barOffset = Array.IndexOf(offsetScore, offsetScore.Max());
        }

        double barPhase = bestPhase + barOffset * beatSec;

        return new MusicBeatAnalysis(
            Bpm: bestBpm,
            BeatSec: Math.Round(beatSec, 5),
            BeatPhaseSec: Math.Round(bestPhase, 4),
            BarPhaseSec: Math.Round(barPhase, 4),
            DurationSec: Math.Round(durationSec, 3),
            DropSec: dropBeat is { } d ? Math.Round(beats[d], 3) : null,
            OutroSec: outroBeat is { } o ? Math.Round(beats[o], 3) : null,
            Confidence: Math.Round(confidence, 3));
    }

    /// <summary>The earliest beat whose following window outweighs its preceding window within 80% of the biggest such jump (and by at least 3 dB).</summary>
    internal static int? FindDrop(double[] beatDb, int window)
    {
        if (beatDb.Length < 2 * window + 1)
            return null;

        var jumps = new double[beatDb.Length];
        double maxJump = double.NegativeInfinity;
        for (int k = window; k + window <= beatDb.Length; k++)
        {
            jumps[k] = Mean(beatDb, k, k + window) - Mean(beatDb, k - window, k);
            maxJump = Math.Max(maxJump, jumps[k]);
        }

        if (maxJump < 3.0)
            return null;

        for (int k = window; k + window <= beatDb.Length; k++)
        {
            if (jumps[k] >= 0.8 * maxJump)
            {
                // Settle on the steepest beat of this rise, never later than its peak jump.
                int best = k;
                for (int j = k; j + window <= beatDb.Length && jumps[j] >= 0.8 * maxJump; j++)
                {
                    if (jumps[j] > jumps[best])
                        best = j;
                }

                return best;
            }
        }

        return null;
    }

    /// <summary>The beat after <paramref name="afterBeat"/> where everything following sits furthest under the preceding window (at least 4 dB, at least a window before the end).</summary>
    internal static int? FindOutro(double[] beatDb, int window, int? afterBeat)
    {
        int from = Math.Max(window, (afterBeat ?? beatDb.Length / 3) + window);
        int? best = null;
        double bestFall = 4.0;
        for (int k = from; k + window <= beatDb.Length; k++)
        {
            double fall = Mean(beatDb, k - window, k) - Mean(beatDb, k, beatDb.Length);
            if (fall > bestFall + 1e-9)
            {
                bestFall = fall;
                best = k;
            }
        }

        return best;
    }

    private static double Mean(double[] values, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++)
            sum += values[i];
        return to > from ? sum / (to - from) : 0;
    }

    private static double GridScore(double[] onset, double phaseFrames, double periodFrames)
    {
        double sum = 0;
        int count = 0;
        for (double x = phaseFrames; x < onset.Length - 1; x += periodFrames)
        {
            sum += Interpolate(onset, x);
            count++;
        }

        return count > 0 ? sum / count : 0;
    }

    private static double Interpolate(double[] values, double x)
    {
        if (x <= 0)
            return values[0];
        int i = (int)x;
        if (i >= values.Length - 1)
            return values[^1];
        double f = x - i;
        return values[i] * (1 - f) + values[i + 1] * f;
    }
}
