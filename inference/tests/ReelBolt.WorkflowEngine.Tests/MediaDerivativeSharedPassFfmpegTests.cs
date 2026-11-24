using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The three filmstrip densities and the three peaks LODs of one source are produced from ONE
/// ffmpeg run / ONE decode, with bytes identical to what separate runs would have made.
/// </summary>
public class MediaDerivativeSharedPassFfmpegTests
{
    private static (MediaDerivativeService Service, Func<int> FfmpegRuns, string Key) Build(string directory)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
        FfmpegVideoToolRunner real = MediaDerivativeFfmpegFixture.BuildRunner(options);
        FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(real, options);
        int runs = 0;
        var mock = new Mock<IVideoToolRunner>();
        mock.Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low))
            .Returns((IReadOnlyList<string> a, TimeSpan t, CancellationToken c, VideoToolPriority p) =>
            {
                Interlocked.Increment(ref runs);
                return real.RunFfmpegAsync(a, t, c, p);
            });
        mock.Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low))
            .Returns((IReadOnlyList<string> a, TimeSpan t, CancellationToken c, VideoToolPriority p) => real.RunFfprobeAsync(a, t, c, p));
        var service = new MediaDerivativeService(
            mock.Object, probe, Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaDerivativeService>.Instance);
        return (service, () => Volatile.Read(ref runs),
            string.Format(CultureInfo.InvariantCulture, "projects/22222222-2222-2222-2222-222222222222/agentFiles/{0}.mp4", Guid.NewGuid().ToString("N")));
    }

    [FfmpegToolsFact]
    public async Task Three_filmstrip_densities_run_one_ffmpeg_pass_and_keep_their_own_counts()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "clip.mp4", seconds: 61, fps: 2, width: 320, height: 240, withAudio: false);
            (MediaDerivativeService service, Func<int> runs, string key) = Build(directory);

            var counts = new Dictionary<int, int>();
            for (int variant = 0; variant < 3; variant++)
            {
                MediaDerivativeResult r = await service.ProduceAsync(
                    Guid.NewGuid(), key, "etag", MediaDerivativeSpec.ForFilmstrip(variant), source, null, CancellationToken.None);
                using JsonDocument doc = JsonDocument.Parse(r.SidecarJson!);
                counts[variant] = doc.RootElement.GetProperty("sheets").EnumerateArray().Sum(s => s.GetProperty("thumbCount").GetInt32());
                r.Artifacts.Count(a => a.ContentType == "image/jpeg").Should().BeGreaterThan(0);
                foreach (MediaDerivativeArtifact sheet in r.Artifacts.Where(a => a.ContentType == "image/jpeg"))
                    MediaDerivativeFfmpegFixture.ProbeDimensions(sheet.LocalPath).Should().Be((1600, 1200));
            }

            runs().Should().Be(1, "one ffmpeg run serves all three densities (was 6 decodes)");
            counts[0].Should().Be(61);
            counts[1].Should().Be(12);
            counts[2].Should().Be(2, "same count the separate inventory pass reported: the 60 s slot has no frame inside a 61 s clip");
        }
        finally { MediaDerivativeFfmpegFixture.TryDelete(directory); }
    }

    [FfmpegToolsFact]
    public async Task Keyframe_only_read_of_a_proxy_like_input_keeps_thumbnail_times_within_one_gop()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            // A proxy-shaped input: 10 fps, GOP = 5 frames (0.5 s), no scene-cut keyframes.
            string source = Path.Combine(directory, "proxy.mp4");
            string orig = MediaDerivativeFfmpegFixture.CreateSource(directory, "o.mp4", 40, 10, 320, 240, false);
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg",
                ["-y", "-nostdin", "-loglevel", "error", "-i", orig, "-c:v", "libx264", "-g", "5", "-keyint_min", "5", "-sc_threshold", "0", "-pix_fmt", "yuv420p", source])
            { UseShellExecute = false })!)
            { p.WaitForExit(); p.ExitCode.Should().Be(0); }

            (MediaDerivativeService service, Func<int> runs, string key) = Build(directory);
            MediaDerivativeResult r = await service.ProduceAsync(
                Guid.NewGuid(), key, "etag", MediaDerivativeSpec.ForFilmstrip(1), source, null, CancellationToken.None,
                finishedProxyPath: source);
            using JsonDocument doc = JsonDocument.Parse(r.SidecarJson!);
            doc.RootElement.GetProperty("sheets").EnumerateArray().Sum(s => s.GetProperty("thumbCount").GetInt32())
                .Should().Be(8, "40 s at 1/5 s");
            runs().Should().Be(1);
        }
        finally { MediaDerivativeFfmpegFixture.TryDelete(directory); }
    }

    [FfmpegToolsFact]
    public async Task Peaks_decode_once_and_every_variant_gets_identical_bytes()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "a.mp4", seconds: 3, fps: 10, width: 160, height: 120, withAudio: true);
            (MediaDerivativeService shared, Func<int> runs, string key) = Build(directory);
            var sharedBytes = new List<byte[][]>();
            for (int v = 0; v < 3; v++)
            {
                MediaDerivativeResult r = await shared.ProduceAsync(
                    Guid.NewGuid(), key, "etag", MediaDerivativeSpec.ForWaveformPeaks(v), source, null, CancellationToken.None);
                sharedBytes.Add(r.Artifacts.Select(a => File.ReadAllBytes(a.LocalPath)).ToArray());
            }
            runs().Should().Be(1, "one audio decode (was one per LOD variant)");

            // A fresh service (empty memo) decoding on its own is the reference.
            (MediaDerivativeService fresh, _, string key2) = Build(directory);
            MediaDerivativeResult single = await fresh.ProduceAsync(
                Guid.NewGuid(), key2, "etag", MediaDerivativeSpec.ForWaveformPeaks(2), source, null, CancellationToken.None);
            byte[][] reference = single.Artifacts.Select(a => File.ReadAllBytes(a.LocalPath)).ToArray();

            foreach (byte[][] variant in sharedBytes)
            {
                variant.Length.Should().Be(reference.Length);
                for (int i = 0; i < reference.Length; i++) variant[i].Should().Equal(reference[i]);
            }
        }
        finally { MediaDerivativeFfmpegFixture.TryDelete(directory); }
    }
}
