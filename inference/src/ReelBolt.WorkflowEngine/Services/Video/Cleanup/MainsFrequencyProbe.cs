using Microsoft.Extensions.Options;

namespace ReelBolt.WorkflowEngine.Services.Video.Cleanup;

public sealed record MainsFrequencyResult(int? Hz, double ProminenceDb);

/// <summary>
/// Decides 50 vs 60 Hz mains hum from the audio itself: decodes at most the first 60 s as mono
/// 4 kHz PCM, then compares narrow-band energy at {50,100,150} against {60,120,180} Hz relative
/// to neighbouring frequencies (<see cref="Analyze"/>, pure).
/// </summary>
public sealed class MainsFrequencyProbe
{
    public const int MaxSeconds = 60;
    public const int SampleRate = 4000;

    /// <summary>A harmonic must stand this far above its neighbours to count as hum.</summary>
    public const double ThresholdDb = 12.0;

    private static readonly double[] NeighbourOffsetsHz = [-8, -5, 5, 8];
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly IVideoToolRunner _runner;

    public MainsFrequencyProbe(IVideoToolRunner runner) => _runner = runner;

    public async Task<MainsFrequencyResult> ProbeAsync(string sourcePath, string scratchPcmPath, CancellationToken ct)
    {
        string[] args =
        [
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", sourcePath,
            "-vn", "-t", MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "1", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", "-f", "s16le", scratchPcmPath
        ];
        VideoToolResult result = await _runner.RunFfmpegAsync(args, Timeout, ct).ConfigureAwait(false);

        // The scratch PCM is customer audio: whatever the decode did - succeeded, failed or was cancelled - it is
        // removed before this method returns. Moving the delete into this finally is what covers the ffmpeg-failure
        // arm, which used to throw before reaching the old inner try/finally.
        try
        {
            if (!result.Succeeded)
            {
                throw new CleanupException(CleanupErrorCodes.ProbeFailed, "Mains probe decode failed.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(scratchPcmPath, ct).ConfigureAwait(false);
            var samples = new double[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768.0;
            }

            return Analyze(samples, SampleRate);
        }
        finally
        {
            try
            {
                File.Delete(scratchPcmPath);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Pure analysis core, unit-testable on generated buffers.</summary>
    public static MainsFrequencyResult Analyze(ReadOnlySpan<double> samples, int sampleRate)
    {
        // Need ~2 s for the 5 Hz neighbour spacing to resolve.
        if (samples.Length < sampleRate * 2)
        {
            return new MainsFrequencyResult(null, 0);
        }

        int n = Math.Min(samples.Length, sampleRate * MaxSeconds);
        var windowed = new double[n];
        for (int i = 0; i < n; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            windowed[i] = samples[i] * w;
        }

        double p50 = Prominence(windowed, sampleRate, 50);
        double p60 = Prominence(windowed, sampleRate, 60);
        double best = Math.Max(p50, p60);
        if (best < ThresholdDb)
        {
            return new MainsFrequencyResult(null, best);
        }

        return new MainsFrequencyResult(p50 >= p60 ? 50 : 60, best);
    }

    // Best harmonic prominence (dB over the mean of its neighbours) among f, 2f, 3f.
    private static double Prominence(double[] x, int sampleRate, int baseHz)
    {
        double best = double.NegativeInfinity;
        for (int k = 1; k <= 3; k++)
        {
            double centre = Goertzel(x, sampleRate, baseHz * k);
            double neighbours = 0;
            foreach (double off in NeighbourOffsetsHz)
            {
                neighbours += Goertzel(x, sampleRate, baseHz * k + off);
            }

            neighbours /= NeighbourOffsetsHz.Length;
            double db = 10 * Math.Log10((centre + 1e-20) / (neighbours + 1e-20));
            best = Math.Max(best, db);
        }

        return best;
    }

    private static double Goertzel(double[] x, int sampleRate, double freqHz)
    {
        double coeff = 2 * Math.Cos(2 * Math.PI * freqHz / sampleRate);
        double s1 = 0, s2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double s0 = x[i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }
}
