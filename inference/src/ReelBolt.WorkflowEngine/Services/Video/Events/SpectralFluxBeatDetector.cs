namespace ReelBolt.WorkflowEngine.Services.Video.Events;

/// <summary>
/// First-party onset detector for beats: spectral flux (the half-wave-rectified frame-to-frame rise of the
/// log-compressed magnitude spectrum) on 16 kHz mono 16-bit PCM, peak-picked against a local median. Deterministic
/// and dependency free. The flux curve is one float per 10 ms; the PCM itself is read in chunks.
/// Onsets alone fire on speech (syllables, plosives), so a beat is only reported inside a music-present region: a
/// stretch where the onset envelope shows a steady pulse (see <see cref="Pulse"/>), which speech, silence and noise
/// never establish.
/// </summary>
public static class SpectralFluxBeatDetector
{
    public const int SampleRate = 16000;
    private const int Window = 512;
    private const int Hop = 160; // 10 ms
    private const int Bins = Window / 2;
    /// <summary>Local-median half window (frames): 0.3 s each side, so the threshold follows the groove, not the whole track.</summary>
    private const int LocalFrames = 30;
    /// <summary>Peak must exceed the local median by this many mean deviations; below it the curve is just texture.</summary>
    private const double MadFactor = 4.0;
    /// <summary>And rise clearly above the local median level, so steady noise (whose flux wobbles around its median) is not a beat.</summary>
    private const double MedianFactor = 1.8;
    private const double MinFlux = 1.0;
    /// <summary>A peak must be the largest within ±3 frames (30 ms) and at least 120 ms after the previous beat.</summary>
    private const int PeakRadius = 3;
    private const int MinSpacingFrames = 12;

    /// <summary>Analysis window for the pulse estimate (6 s: at least 6 beats even at 60 bpm).</summary>
    private const int PulseWindowFrames = 600;
    /// <summary>A shorter clip is analysed as one window, but never below 3 s of audio.</summary>
    private const int MinPulseWindowFrames = 300;
    /// <summary>Music state is decided per 1 s cell, from the window centred on that cell.</summary>
    private const int CellFrames = 100;
    /// <summary>Candidate beat period 0.3 s .. 1.0 s (60..200 bpm); speech syllables (about 0.2 s) fall outside it.</summary>
    private const int MinLagFrames = 30;
    private const int MaxLagFrames = 100;
    /// <summary>The onset envelope is the flux minus its own moving average (±0.25 s), half-wave rectified.</summary>
    private const int EnvelopeRadius = 25;
    /// <summary>
    /// Pulse confidence (mean normalised autocorrelation of the onset envelope at the multiples of the best period) needed to call a
    /// cell music. Measured: speech, plosive-heavy speech and recorded interview audio top out at 0.19; noise 0.09;
    /// kick-and-pad music 0.75+ and music under loud speech 0.26+ (low band). 0.28 sits between with a margin on each side.
    /// </summary>
    internal const double PulseThreshold = 0.28;
    /// <summary>Low band for the kick pulse: FFT bins 2..6 (62..187 Hz), where music under speech still pulses.</summary>
    private const int LowBandFirst = 1;
    private const int LowBandLast = 5;
    /// <summary>Windows with a mean frame rms below this (about -50 dBFS) are silence, never music.</summary>
    private const double SilenceRms = 0.003;
    /// <summary>
    /// A frame is near-silent below 10% of the window's 90th-percentile frame rms; a window with more than 20% such frames
    /// has dead air between phrases (speech with pauses, a bare click track), which music with any sustained bed never has.
    /// Measured: voice-over with pauses 0.38..0.87, music with or without speech 0.
    /// </summary>
    private const double SilentFrameRatio = 0.1;
    private const double MaxSilentFraction = 0.2;
    /// <summary>Music runs shorter than this many cells are dropped: a pulse needs a few seconds to be called steady.</summary>
    private const int MinMusicCells = 3;

    /// <summary>Beat times (seconds), strengths (0..1) and the pulse confidence (0..1) of their music region, found in raw s16le mono 16 kHz PCM read from <paramref name="pcm"/>.</summary>
    public static IReadOnlyList<(double TimeSec, double Strength, double Pulse)> Detect(Stream pcm)
    {
        Features features = ComputeFeatures(pcm);
        double[] cellPulse = MusicCells(features);
        List<(double TimeSec, double Strength, double Pulse)> beats = [];
        foreach ((double timeSec, double strength) in PickPeaks(features.Flux))
        {
            int cell = (int)(timeSec * SampleRate / Hop) / CellFrames;
            if (cell < cellPulse.Length && cellPulse[cell] > 0)
                beats.Add((timeSec, strength, cellPulse[cell]));
        }

        return beats;
    }

    /// <summary>Per-frame curves: broadband flux, low-band flux and frame rms, one float each per 10 ms.</summary>
    internal sealed record Features(List<float> Flux, List<float> LowFlux, List<float> Rms);

    /// <summary>
    /// One pulse confidence per 1 s cell, 0 where the cell is not music. A cell is music when the 6 s window around it holds
    /// audible sound with a steady pulse (broadband or kick-band onset envelope) and it belongs to a run of at least
    /// <see cref="MinMusicCells"/> such cells.
    /// </summary>
    internal static double[] MusicCells(Features features)
    {
        int n = features.Flux.Count;
        double[] cells = new double[(n + CellFrames - 1) / CellFrames];
        int window = Math.Min(PulseWindowFrames, n);
        if (window < MinPulseWindowFrames)
            return cells;

        Dictionary<int, double> byStart = [];
        for (int c = 0; c < cells.Length; c++)
        {
            int start = Math.Clamp(c * CellFrames + CellFrames / 2 - window / 2, 0, n - window);
            if (!byStart.TryGetValue(start, out double pulse))
            {
                pulse = WindowPulse(features, start, window);
                byStart[start] = pulse;
            }

            cells[c] = pulse >= PulseThreshold ? pulse : 0;
        }

        for (int c = 0; c < cells.Length;)
        {
            if (cells[c] == 0)
            {
                c++;
                continue;
            }

            int end = c;
            while (end < cells.Length && cells[end] > 0)
                end++;
            if (end - c < MinMusicCells)
                Array.Clear(cells, c, end - c);
            c = end;
        }

        return cells;
    }

    private static double WindowPulse(Features features, int start, int length)
    {
        double rms = 0;
        float[] sorted = new float[length];
        for (int i = 0; i < length; i++)
        {
            sorted[i] = features.Rms[start + i];
            rms += sorted[i];
        }

        if (rms / length < SilenceRms)
            return 0;
        Array.Sort(sorted);
        float silent = (float)SilentFrameRatio * sorted[(int)(0.9 * (length - 1))];
        if (sorted.Count(v => v < silent) > MaxSilentFraction * length)
            return 0;

        return Math.Max(Pulse(features.Flux, start, length), Pulse(features.LowFlux, start, length));
    }

    /// <summary>
    /// Pulse confidence of a flux curve slice: the best candidate period's mean normalised autocorrelation at every multiple of
    /// the period up to half the window (at least 3). A steady beat repeats at every multiple; speech and noise
    /// correlate with themselves at one lag at most.
    /// </summary>
    internal static double Pulse(List<float> flux, int start, int length)
    {
        double[] envelope = new double[length];
        double[] prefix = new double[length + 1];
        for (int i = 0; i < length; i++)
            prefix[i + 1] = prefix[i] + flux[start + i];
        double mean = 0;
        for (int i = 0; i < length; i++)
        {
            int lo = Math.Max(0, i - EnvelopeRadius), hi = Math.Min(length, i + EnvelopeRadius + 1);
            double local = (prefix[hi] - prefix[lo]) / (hi - lo);
            envelope[i] = Math.Max(0, flux[start + i] - local);
            mean += envelope[i];
        }

        mean /= length;
        for (int i = 0; i < length; i++)
            envelope[i] -= mean;

        // Multiples of the period are tried up to half the window: a phrase of periodic syllables fades out of the
        // correlation after a couple of periods, a beat that runs through the window does not.
        int maxLag = length / 2;
        double[] ac = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = lag; i < length; i++)
                sum += envelope[i] * envelope[i - lag];
            ac[lag] = sum;
        }

        if (ac[0] <= 1e-9)
            return 0;

        double best = 0;
        for (int lag = MinLagFrames; lag <= MaxLagFrames; lag++)
        {
            double total = 0;
            int multiples = 0;
            for (int m = 1; m * lag + 1 <= maxLag; m++, multiples++)
                total += Math.Max(ac[m * lag - 1], Math.Max(ac[m * lag], ac[m * lag + 1])) / ac[0];
            if (multiples >= 3)
                best = Math.Max(best, total / multiples);
        }

        return Math.Clamp(best, 0.0, 1.0);
    }

    internal static Features ComputeFeatures(Stream pcm)
    {
        float[] hann = new float[Window];
        for (int i = 0; i < Window; i++)
            hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (Window - 1));

        float[] ring = new float[Window];
        int filled = 0, sinceHop = 0;
        float[] previous = new float[Bins];
        bool havePrevious = false;
        float[] re = new float[Window], im = new float[Window];
        List<float> flux = [], lowFlux = [], rms = [];
        byte[] chunk = new byte[8192];

        while (true)
        {
            int read = pcm.Read(chunk, 0, chunk.Length);
            if (read <= 0)
                break;
            for (int i = 0; i + 1 < read; i += 2)
            {
                float sample = (short)(chunk[i] | chunk[i + 1] << 8) / 32768f;
                // Shift in one sample; the ring is rotated lazily when a window is analysed.
                ring[filled % Window] = sample;
                filled++;
                sinceHop++;
                if (filled >= Window && sinceHop >= Hop)
                {
                    sinceHop = 0;
                    int start = filled % Window;
                    float energy = 0f;
                    for (int k = 0; k < Window; k++)
                    {
                        float raw = ring[(start + k) % Window];
                        energy += raw * raw;
                        re[k] = raw * hann[k];
                        im[k] = 0f;
                    }

                    Fft(re, im);
                    float sum = 0f, lowSum = 0f;
                    for (int b = 0; b < Bins; b++)
                    {
                        float magnitude = MathF.Sqrt(re[b + 1] * re[b + 1] + im[b + 1] * im[b + 1]);
                        float compressed = MathF.Log(1f + 1000f * magnitude);
                        if (havePrevious)
                        {
                            float rise = MathF.Max(0f, compressed - previous[b]);
                            sum += rise;
                            if (b >= LowBandFirst && b <= LowBandLast)
                                lowSum += rise;
                        }

                        previous[b] = compressed;
                    }

                    flux.Add(havePrevious ? sum : 0f);
                    lowFlux.Add(havePrevious ? lowSum : 0f);
                    rms.Add(MathF.Sqrt(energy / Window));
                    havePrevious = true;
                }
            }
        }

        return new Features(flux, lowFlux, rms);
    }

    internal static IReadOnlyList<(double TimeSec, double Strength)> PickPeaks(List<float> flux)
    {
        List<(double, double)> beats = [];
        int n = flux.Count;
        if (n == 0)
            return beats;

        int lastKept = -MinSpacingFrames;
        float[] window = new float[2 * LocalFrames + 1];
        for (int t = 0; t < n; t++)
        {
            float value = flux[t];
            if (value < MinFlux)
                continue;

            bool isPeak = true;
            for (int d = -PeakRadius; d <= PeakRadius && isPeak; d++)
            {
                int k = t + d;
                if (d != 0 && k >= 0 && k < n && (flux[k] > value || (flux[k] == value && d < 0)))
                    isPeak = false;
            }

            if (!isPeak || t - lastKept < MinSpacingFrames)
                continue;

            int count = 0;
            for (int k = Math.Max(0, t - LocalFrames); k <= Math.Min(n - 1, t + LocalFrames); k++)
                window[count++] = flux[k];
            Array.Sort(window, 0, count);
            float median = window[count / 2];
            float deviation = 0f;
            for (int k = 0; k < count; k++)
                deviation += MathF.Abs(window[k] - median);
            deviation /= count;

            double threshold = median + MadFactor * deviation + 0.25;
            if (value < threshold || value < MedianFactor * median)
                continue;

            lastKept = t;
            double strength = Math.Clamp((value - median) / Math.Max(value, 1e-6), 0.0, 1.0);
            beats.Add(((t * Hop + Window / 2.0) / SampleRate, strength));
        }

        return beats;
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2f * MathF.PI / len;
            float wRe = MathF.Cos(angle), wIm = MathF.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float curRe = 1f, curIm = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float tRe = re[b] * curRe - im[b] * curIm;
                    float tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    float nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
