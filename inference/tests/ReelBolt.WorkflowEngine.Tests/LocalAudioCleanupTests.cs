using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Cleanup;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class LocalAudioCleanupTests
{
    [Fact]
    public void Hum_50_builds_five_notches_byte_pinned()
    {
        CleanupFilterResult r = CleanupFilterBuilder.Build(LocalCleanupTarget.Hum, new CleanupMeasurements(50, null));
        r.FilterString.Should().Be(
            "bandreject=f=50:width_type=q:w=30,bandreject=f=100:width_type=q:w=30,bandreject=f=150:width_type=q:w=30,"
            + "bandreject=f=200:width_type=q:w=30,bandreject=f=250:width_type=q:w=30");
    }

    [Fact]
    public void Hum_60_starts_at_60_and_null_means_nothing_to_do()
    {
        CleanupFilterBuilder.Build(LocalCleanupTarget.Hum, new CleanupMeasurements(60, null)).Filters[4]
            .Should().Be("bandreject=f=300:width_type=q:w=30");
        CleanupFilterResult none = CleanupFilterBuilder.Build(LocalCleanupTarget.Hum, CleanupMeasurements.None);
        none.HasFilter.Should().BeFalse();
        none.Reason.Should().Be("hum_not_detected");
    }

    [Fact]
    public void Other_targets_are_byte_pinned()
    {
        CleanupFilterBuilder.Build(LocalCleanupTarget.Wind, CleanupMeasurements.None).FilterString
            .Should().Be("highpass=f=90:poles=2,lowshelf=f=200:g=-4");
        CleanupFilterBuilder.Build(LocalCleanupTarget.Hiss, CleanupMeasurements.None).FilterString
            .Should().Be("afftdn=nr=12:nf=-50:nt=w");
        CleanupFilterBuilder.Build(LocalCleanupTarget.Hiss, new CleanupMeasurements(null, -61.4)).FilterString
            .Should().Be("afftdn=nr=12:nf=-61:nt=w");
        CleanupFilterBuilder.Build(LocalCleanupTarget.Hiss, new CleanupMeasurements(null, -200)).FilterString
            .Should().Be("afftdn=nr=12:nf=-80:nt=w");
        CleanupFilterBuilder.Build(LocalCleanupTarget.Echo, CleanupMeasurements.None).FilterString
            .Should().Be("highpass=f=100:poles=2,agate=threshold=0.03:ratio=2:attack=5:release=200:range=0.25");
        CleanupFilterBuilder.Build(LocalCleanupTarget.VoiceBasic, CleanupMeasurements.None, "/app/assets/rnnoise/sh.rnnn")
            .FilterString.Should().Be("arnndn=m=/app/assets/rnnoise/sh.rnnn,highpass=f=80:poles=2");
        CleanupFilterBuilder.Build(LocalCleanupTarget.VoiceBasic, CleanupMeasurements.None).FilterString
            .Should().Be("afftdn=nr=12:nf=-50:nt=w,anlmdn=s=0.0001:p=0.002:r=0.006,highpass=f=80:poles=2");
    }

    [Fact]
    public void Model_path_with_filter_metacharacters_is_rejected()
    {
        Action act = () => CleanupFilterBuilder.Build(LocalCleanupTarget.VoiceBasic, CleanupMeasurements.None, "/x/a:b'c");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Full_argv_is_byte_pinned()
    {
        CleanupFilterResult f = CleanupFilterBuilder.Build(LocalCleanupTarget.Wind, CleanupMeasurements.None);
        LocalAudioCleaner.BuildArgs("/s/in.wav", "/s/out.wav", f, 12.5, 48000, 2).Should().Equal(
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/s/in.wav",
            "-vn",
            "-af", "highpass=f=90:poles=2,lowshelf=f=200:g=-4,apad=whole_dur=12.500000,atrim=end=12.500000",
            "-ar", "48000", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", "/s/out.wav");
    }

    [Fact]
    public void Probe_finds_50_and_60_and_null_on_clean_noise()
    {
        MainsFrequencyProbe.Analyze(Synth(50, 0.05, 1), 4000).Hz.Should().Be(50);
        MainsFrequencyProbe.Analyze(Synth(60, 0.05, 2), 4000).Hz.Should().Be(60);
        MainsFrequencyProbe.Analyze(Synth(null, 0.05, 3), 4000).Hz.Should().BeNull();
    }

    [Fact]
    public async Task The_mains_probe_deletes_its_scratch_pcm_when_ffmpeg_fails()
    {
        string directory = Path.Combine(Path.GetTempPath(), "reelbolt-mains-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string pcm = Path.Combine(directory, "mains.pcm");
        try
        {
            var runner = new Mock<IVideoToolRunner>();
            runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((argv, _, _) => File.WriteAllBytes(argv[^1], new byte[128]))
                .ReturnsAsync(new VideoToolResult(1, "", "boom", false));
            var probe = new MainsFrequencyProbe(runner.Object);

            Func<Task> act = () => probe.ProbeAsync("in.wav", pcm, CancellationToken.None);

            await act.Should().ThrowAsync<CleanupException>();
            File.Exists(pcm).Should().BeFalse("a failed decode leaves no customer audio on disk");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static double[] Synth(double? hz, double amp, int seed)
    {
        var rng = new Random(seed);
        var x = new double[4000 * 10];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (rng.NextDouble() * 2 - 1) * 0.1;
            if (hz is { } f)
            {
                x[i] += amp * Math.Sin(2 * Math.PI * f * i / 4000);
            }
        }

        return x;
    }

    [Fact]
    public async Task Forced_duration_mismatch_is_a_typed_failure()
    {
        var runner = new Mock<IVideoToolRunner>();
        int probes = 0;
        runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new VideoToolResult(
                0,
                probes++ == 0
                    ? "{\"streams\":[{\"sample_rate\":\"48000\",\"channels\":1}],\"format\":{\"duration\":\"10.000000\"}}"
                    : "{\"streams\":[{\"sample_rate\":\"48000\",\"channels\":1}],\"format\":{\"duration\":\"10.200000\"}}",
                "",
                false));
        runner.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "", "", false));
        var cleaner = new LocalAudioCleaner(runner.Object, Options.Create(new LocalCleanupOptions()));

        Func<Task> act = () => cleaner.CleanAsync("/s/in.wav", "/s/out.wav", LocalCleanupTarget.Wind, CancellationToken.None);

        (await act.Should().ThrowAsync<CleanupException>()).Which.Code.Should().Be("AUDIO_DURATION_MISMATCH");
    }

    [Fact]
    public void Vendored_model_matches_its_pinned_checksum()
    {
        string dir = AppContext.BaseDirectory;
        string? model = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "assets", "rnnoise", "sh.rnnn");
            if (File.Exists(candidate))
            {
                model = candidate;
                break;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        model.Should().NotBeNull();
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model!))).ToLowerInvariant()
            .Should().Be(LocalCleanupOptions.RnnoiseModelSha256)
            .And.Be("70bb6685eb0c2a1d18e2918dca3fbfbd39317010b1802eb1b6ea73a92f3fdec0");
    }

    [Fact]
    public async Task Wrong_model_checksum_is_a_typed_failure()
    {
        string bad = Path.Combine(Path.GetTempPath(), "rb-bad-" + Guid.NewGuid().ToString("N") + ".rnnn");
        await File.WriteAllBytesAsync(bad, [1, 2, 3]);
        try
        {
            var runner = new Mock<IVideoToolRunner>();
            runner.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(
                    0, "{\"streams\":[{\"sample_rate\":\"48000\",\"channels\":1}],\"format\":{\"duration\":\"1.0\"}}", "", false));
            var cleaner = new LocalAudioCleaner(runner.Object, Options.Create(new LocalCleanupOptions { RnnoiseModelPath = bad }));

            Func<Task> act = () => cleaner.CleanAsync("/s/in.wav", "/s/out.wav", LocalCleanupTarget.VoiceBasic, CancellationToken.None);

            (await act.Should().ThrowAsync<CleanupException>()).Which.Code.Should().Be("RNNOISE_MODEL_CHECKSUM_MISMATCH");
        }
        finally
        {
            File.Delete(bad);
        }
    }

    // ---- real ffmpeg spectral checks ----

    [FfmpegToolsFact]
    public async Task Hum_target_removes_50Hz_and_keeps_1kHz()
    {
        string dir = NewDir();
        try
        {
            string src = Path.Combine(dir, "in.wav");
            Gen(src, "sine=f=1000:d=6:r=48000,volume=0.3[a];sine=f=50:d=6:r=48000,volume=0.3[b];[a][b]amix=inputs=2:normalize=0");
            string outp = Path.Combine(dir, "out.wav");
            LocalCleanupResult res = await NewCleaner().CleanAsync(src, outp, LocalCleanupTarget.Hum, CancellationToken.None);

            res.Applied.Should().BeTrue();
            res.Measurements.MainsFrequencyHz.Should().Be(50);
            (Level(src, 50) - Level(outp, 50)).Should().BeGreaterThanOrEqualTo(20);
            Math.Abs(Level(src, 1000) - Level(outp, 1000)).Should().BeLessThanOrEqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [FfmpegToolsFact]
    public async Task Hum_target_on_clean_audio_does_nothing()
    {
        string dir = NewDir();
        try
        {
            string src = Path.Combine(dir, "in.wav");
            Gen(src, "sine=f=1000:d=6:r=48000,volume=0.3");
            LocalCleanupResult res = await NewCleaner().CleanAsync(
                src, Path.Combine(dir, "out.wav"), LocalCleanupTarget.Hum, CancellationToken.None);
            res.Applied.Should().BeFalse();
            res.Reason.Should().Be("hum_not_detected");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [FfmpegToolsFact]
    public async Task Wind_target_cuts_40Hz_rumble()
    {
        string dir = NewDir();
        try
        {
            string src = Path.Combine(dir, "in.wav");
            Gen(src, "sine=f=1000:d=6:r=48000,volume=0.3[a];sine=f=40:d=6:r=48000,volume=0.3[b];[a][b]amix=inputs=2:normalize=0");
            string outp = Path.Combine(dir, "out.wav");
            LocalCleanupResult res = await NewCleaner().CleanAsync(src, outp, LocalCleanupTarget.Wind, CancellationToken.None);

            res.Applied.Should().BeTrue();
            (Level(src, 40) - Level(outp, 40)).Should().BeGreaterThanOrEqualTo(15);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static LocalAudioCleaner NewCleaner()
    {
        var opts = Options.Create(new VideoEditingOptions());
        return new LocalAudioCleaner(
            new FfmpegVideoToolRunner(opts, NullLogger<FfmpegVideoToolRunner>.Instance),
            Options.Create(new LocalCleanupOptions()));
    }

    private static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "rb-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Gen(string path, string lavfi)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", lavfi, "-c:a", "pcm_s16le", path })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0);
    }

    // Goertzel power in dB of a mono/first-channel 16-bit WAV at freq.
    private static double Level(string wavPath, double freq)
    {
        byte[] b = File.ReadAllBytes(wavPath);
        int data = 12;
        while (data + 8 <= b.Length && System.Text.Encoding.ASCII.GetString(b, data, 4) != "data")
        {
            data += 8 + BitConverter.ToInt32(b, data + 4);
        }

        data += 8;
        int n = (b.Length - data) / 2;
        int skip = 48000; // ignore the first second (filter settling)
        double coeff = 2 * Math.Cos(2 * Math.PI * freq / 48000), s1 = 0, s2 = 0;
        for (int i = skip; i < n; i++)
        {
            double s0 = BitConverter.ToInt16(b, data + i * 2) / 32768.0 + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        double p = s1 * s1 + s2 * s2 - coeff * s1 * s2;
        return 10 * Math.Log10(p / (n - skip) + 1e-20);
    }
}
