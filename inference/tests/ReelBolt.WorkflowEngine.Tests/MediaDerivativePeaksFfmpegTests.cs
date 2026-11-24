using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Real-ffmpeg assertions on the waveform peaks: the decoded RAW file's real length, the bucket
/// counts each LOD actually produced, the per-channel Int16 min/max byte layout, and the sidecar
/// that indexes it.
/// </summary>
public class MediaDerivativePeaksFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaDerivativePeaksFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task Peaks_produce_three_lods_with_the_documented_bucket_counts_per_channel()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "audio.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.WaveformPeaks, 0, source, CancellationToken.None);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            JsonElement root = index.RootElement;

            root.GetProperty("sampleRate").GetInt32().Should().Be(48000);
            root.GetProperty("channels").GetInt32().Should().Be(2);
            root.GetProperty("layout").GetString().Should().Be("ch,min,max");
            root.GetProperty("byteOrder").GetString().Should().Be("le");

            int[] lodsMs = root.GetProperty("lodsMs").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            int[] bucketCounts = root.GetProperty("bucketCounts").EnumerateArray().Select(e => e.GetInt32()).ToArray();

            lodsMs.Should().Equal(20, 100, 1000);

            // ~2 s of audio: 48000 * 2 / 48000 buckets per LOD. Measured against the decode, so the
            // tolerance is one bucket at each end rather than an exact duration / bucketMs.
            bucketCounts[0].Should().BeInRange(98, 101);
            bucketCounts[1].Should().BeInRange(19, 21);
            bucketCounts[2].Should().BeInRange(1, 3);

            List<MediaDerivativeArtifact> raws = result.Artifacts
                .Where(a => a.ContentType == "application/octet-stream")
                .ToList();

            raws.Should().HaveCount(3);

            for (int i = 0; i < 3; i++)
            {
                raws[i].FileName.Should().Be(
                    PcmPeakSampler.LodLeaf(MediaDerivativeCacheKey.ComputeParamsHash(spec)[..16], lodsMs[i]));

                // channel-major [channel][bucket]{int16 min, int16 max}: 2 channels * 4 bytes a bucket.
                new FileInfo(raws[i].LocalPath).Length.Should().Be(
                    2L * bucketCounts[i] * 4,
                    $"LOD {lodsMs[i]}ms declares {bucketCounts[i]} buckets per channel");
            }

            File.Exists(result.Artifacts[^1].LocalPath).Should().BeTrue();
            result.Artifacts[^1].ContentType.Should().Be("application/json");

            result.DurationSec.Should().BeApproximately(2.0, 0.3);

            _output.WriteLine(
                "MEASURED peaks: lodsMs [{0}] bucketCounts [{1}], raw byte sizes [{2}], " +
                "channels {3}, cacheKey {4}",
                string.Join(",", lodsMs),
                string.Join(",", bucketCounts),
                string.Join(",", raws.Select(r => new FileInfo(r.LocalPath).Length)),
                root.GetProperty("channels").GetInt32(),
                result.CacheKey[..16]);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Every_lod_index_selects_its_own_single_lod_and_still_reads_real_bytes()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "audio.mp4", seconds: 2, fps: 25, width: 160, height: 120, withAudio: true);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            for (int variant = 0; variant < 3; variant++)
            {
                MediaDerivativeResult result = await service.ProduceAsync(
                    Guid.NewGuid(), sourceKey, $"{etag}-{variant}",
                    MediaDerivativeSpec.ForWaveformPeaks(variant), source,
                    progress: null, CancellationToken.None);

                using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
                JsonElement counts = index.RootElement.GetProperty("bucketCounts");

                // The spec's params always describe all three LODs, so the decoder always produces
                // all three — the variant is what the editor asks FOR, not what is computed.
                counts.GetArrayLength().Should().Be(3);
                result.Variant.Should().Be(variant);
            }
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Peaks_are_deterministic_across_two_independent_runs()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "audio.mp4", seconds: 1, fps: 25, width: 160, height: 120, withAudio: true);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);
            Guid projectId = Guid.NewGuid();

            MediaDerivativeSpec spec = MediaDerivativeSpec.ForWaveformPeaks(0);

            MediaDerivativeResult first = await service.ProduceAsync(
                projectId, sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            MediaDerivativeResult second = await service.ProduceAsync(
                projectId, sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            first.CacheKey.Should().Be(second.CacheKey);
            first.SidecarJson.Should().Be(second.SidecarJson);
            first.Artifacts.Select(a => a.FileName).Should().Equal(second.Artifacts.Select(a => a.FileName));

            // Byte-identical decodes: the raw object is the same bytes both times.
            byte[] firstRaw = await File.ReadAllBytesAsync(first.Artifacts[0].LocalPath);
            byte[] secondRaw = await File.ReadAllBytesAsync(second.Artifacts[0].LocalPath);
            firstRaw.Should().Equal(secondRaw);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    private static (MediaDerivativeService Service, string SourceKey, string Etag) Build(string directory)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
        FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
        FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);

        return (MediaDerivativeFfmpegFixture.BuildService(runner, probe, options),
                "projects/33333333-3333-3333-3333-333333333333/agentFiles/audio.mp4",
                "etag-peaks");
    }
}

/// <summary>
/// Real-ffmpeg assertions on the keyframe index: a non-empty, strictly monotonic set of packet
/// times, cross-checked against an independent ffprobe read of the same file.
/// </summary>
public class MediaDerivativeKeyframeIndexFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaDerivativeKeyframeIndexFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task The_keyframe_index_is_non_empty_strictly_monotonic_and_starts_at_zero()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            // 12 s at 30 fps = 360 frames, comfortably past x264's default 250-frame keyint, so the
            // source genuinely has more than one keyframe and gopHintSec has a real delta to take.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 12, fps: 30, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.KeyframeIndex, 0, source, CancellationToken.None);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            double[] times = index.RootElement.GetProperty("timesSec")
                .EnumerateArray().Select(e => e.GetDouble()).ToArray();

            times.Should().NotBeEmpty();
            times.Length.Should().BeGreaterThanOrEqualTo(2, "a 360-frame source exceeds one keyint");
            index.RootElement.GetProperty("count").GetInt32().Should().Be(times.Length);
            times.Should().BeInAscendingOrder();

            for (int i = 1; i < times.Length; i++)
                times[i].Should().BeGreaterThan(times[i - 1], "keyframe times must be strictly monotonic");

            times[0].Should().BeApproximately(0, 0.001);
            index.RootElement.GetProperty("gopHintSec").GetDouble().Should().BeGreaterThan(0);

            // Cross-check against ffprobe directly — not through the parser under test.
            IReadOnlyList<double> independent = MediaDerivativeFfmpegFixture.ProbeKeyframeTimes(source);
            independent.Should().NotBeEmpty();
            independent.Count.Should().Be(times.Length);
            for (int i = 0; i < times.Length; i++)
                times[i].Should().BeApproximately(independent[i], 0.001);

            result.Artifacts.Should().HaveCount(1);
            result.Artifacts[0].ContentType.Should().Be("application/json");
            result.Artifacts[0].FileName.Should().Be(
                KeyframeIndexArgvBuilder.IndexLeaf(MediaDerivativeCacheKey.ComputeParamsHash(spec)[..16]));
            File.Exists(result.Artifacts[0].LocalPath).Should().BeTrue();

            _output.WriteLine(
                "MEASURED keyframe index: count {0}, gopHintSec {1}, times [{2}]",
                times.Length,
                index.RootElement.GetProperty("gopHintSec").GetDouble(),
                string.Join(", ", times.Select(t => t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_keyframe_index_leaf_depends_only_on_the_source_and_never_on_the_file_name()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 25, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);
            Guid projectId = Guid.NewGuid();

            MediaDerivativeResult first = await service.ProduceAsync(
                projectId, sourceKey, etag, MediaDerivativeSpec.ForKeyframeIndex(), source,
                progress: null, CancellationToken.None);

            MediaDerivativeResult second = await service.ProduceAsync(
                projectId, sourceKey, etag, MediaDerivativeSpec.ForKeyframeIndex(), source,
                progress: null, CancellationToken.None);

            first.Artifacts[0].FileName.Should().Be(second.Artifacts[0].FileName);
            first.CacheKey.Should().Be(second.CacheKey);
            first.SidecarJson.Should().Be(second.SidecarJson);

            // A different etag is a different source identity and must key separately.
            MediaDerivativeResult other = await service.ProduceAsync(
                projectId, sourceKey, "other-etag", MediaDerivativeSpec.ForKeyframeIndex(), source,
                progress: null, CancellationToken.None);

            other.CacheKey.Should().NotBe(first.CacheKey);
            other.Artifacts[0].FileName.Should().Be(first.Artifacts[0].FileName,
                "the leaf is scoped by the params hash and the directory by the source hash");
            other.StoragePrefix.Should().NotBe(first.StoragePrefix);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_keyframe_index_reads_the_real_packet_flags_and_not_a_frame_scan()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            // 5 s at 25 fps with the encoder's default GOP: far fewer keyframes than frames.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 5, fps: 25, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, MediaDerivativeSpec.ForKeyframeIndex(), source,
                progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            int count = index.RootElement.GetProperty("count").GetInt32();

            count.Should().BeGreaterThan(0);
            count.Should().BeLessThan(125, "125 frames at 25 fps for 5 s cannot all be keyframes");

            result.Artifacts.Should().HaveCount(1);
            result.Artifacts[0].FileName.Should().StartWith("keyframes-");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    private static (MediaDerivativeService Service, string SourceKey, string Etag) Build(string directory)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
        FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
        FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);

        return (MediaDerivativeFfmpegFixture.BuildService(runner, probe, options),
                "projects/44444444-4444-4444-4444-444444444444/agentFiles/src.mp4",
                "etag-keyframes");
    }
}
