using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Real-ffmpeg assertions on the editor proxy: the encoded dimensions, the half-second GOP the
/// source's own frame rate dictates, the faststart box order, and the audio-argument rule. Mocks
/// are deliberately not used — the whole point of these tests is that real ffmpeg accepts the argv
/// this ticket builds and produces the shape the contract promises.
/// </summary>
public class MediaDerivativeProxyFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaDerivativeProxyFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task Proxy_is_540_high_for_a_sub_UHD_source_and_starts_with_moov()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 3, fps: 30, width: 640, height: 360, withAudio: true);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            // scale=-2:540 with a 640x360 source is exactly 960x540.
            result.Width.Should().Be(960);
            result.Height.Should().Be(540);
            MediaDerivativeFfmpegFixture.ProbeDimensions(result.Artifacts[0].LocalPath)
                .Should().Be((960, 540));

            result.DurationSec.Should().BeApproximately(3.0, 0.2);

            IReadOnlyList<string> boxes = MediaDerivativeFfmpegFixture
                .TopLevelBoxTypes(result.Artifacts[0].LocalPath)
                .ToList();

            boxes.Should().Contain("moov");
            boxes.Should().Contain("mdat");
            boxes.ToList().IndexOf("moov").Should().BeLessThan(
                boxes.ToList().IndexOf("mdat"),
                "-movflags +faststart must put the moov box before mdat so a browser can start playing early");

            _output.WriteLine(
                "MEASURED proxy: {0}x{1}, duration {2:F3}s, size {3} bytes, source 640x360@30fps, " +
                "cacheKey {4}, box order [{5}]",
                result.Width, result.Height, result.DurationSec, result.SizeBytes,
                result.CacheKey[..16], string.Join(",", boxes));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Proxy_gop_is_half_a_second_derived_from_the_sources_real_frame_rate()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 3, fps: 30, width: 320, height: 240, withAudio: false);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            spec.ParamsJson.Should().Contain("\"gopFrames\":15");
            spec.ParamsJson.Should().Contain("\"keyintMin\":15");

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            IReadOnlyList<double> keyframes = MediaDerivativeFfmpegFixture.ProbeKeyframeTimes(
                result.Artifacts[0].LocalPath);

            keyframes.Should().HaveCountGreaterThanOrEqualTo(4);
            keyframes[0].Should().BeApproximately(0, 0.001);

            _output.WriteLine(
                "MEASURED proxy GOP: keyframe times [{0}] (deltas must be 0.5 s at 30 fps with gop=15)",
                string.Join(", ", keyframes.Select(k => k.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));

            // 15 frames at 30 fps = 0.5 s; -sc_threshold 0 keeps scene cuts from adding extras.
            for (int i = 1; i < Math.Min(keyframes.Count, 5); i++)
            {
                (keyframes[i] - keyframes[i - 1]).Should().BeApproximately(
                    0.5, 0.02, $"keyframe {i} must sit half a second after keyframe {i - 1}");
            }
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_silent_source_produces_a_proxy_with_no_audio_stream_at_all()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string silent = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "silent.mp4", seconds: 2, fps: 25, width: 320, height: 240, withAudio: false);
            string loud = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "loud.mp4", seconds: 2, fps: 25, width: 320, height: 240, withAudio: true);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec silentSpec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, silent, CancellationToken.None);
            silentSpec.ParamsJson.Should().Contain("\"audioCodec\":false");

            MediaDerivativeResult silentResult = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, silentSpec, silent, progress: null, CancellationToken.None);

            MediaDerivativeFfmpegFixture.ProbeAudioStreamCount(silentResult.Artifacts[0].LocalPath)
                .Should().Be(0, "the AAC arguments must be dropped for a source with no audio stream");

            MediaDerivativeSpec loudSpec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, loud, CancellationToken.None);
            loudSpec.ParamsJson.Should().Contain("\"audioCodec\":true");

            MediaDerivativeResult loudResult = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, loudSpec, loud, progress: null, CancellationToken.None);

            MediaDerivativeFfmpegFixture.ProbeAudioStreamCount(loudResult.Artifacts[0].LocalPath)
                .Should().Be(1);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Two_runs_of_the_same_inputs_return_the_same_cache_key_and_params()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 25, width: 320, height: 240, withAudio: true);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);
            Guid projectId = Guid.NewGuid();

            MediaDerivativeSpec specA = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);
            MediaDerivativeSpec specB = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            specA.ParamsJson.Should().Be(specB.ParamsJson);

            MediaDerivativeResult first = await service.ProduceAsync(
                projectId, sourceKey, etag, specA, source, progress: null, CancellationToken.None);
            MediaDerivativeResult second = await service.ProduceAsync(
                projectId, sourceKey, etag, specB, source, progress: null, CancellationToken.None);

            first.CacheKey.Should().Be(second.CacheKey);
            first.ParamsJson.Should().Be(second.ParamsJson);
            first.CacheKey.Should().Be(MediaDerivativeCacheKey.ComputeCacheKey(sourceKey, etag, specA));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_proxy_spec_resolved_against_a_different_source_is_rejected_loudly()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string small = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "small.mp4", seconds: 1, fps: 25, width: 320, height: 240, withAudio: false);
            string other = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "other.mp4", seconds: 1, fps: 25, width: 320, height: 240, withAudio: true);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec specForOther = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, other, CancellationToken.None);

            Func<Task> act = () => service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, specForOther, small, progress: null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(e => e.Message.Contains("does not describe this source", StringComparison.Ordinal));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Artifacts_land_under_the_documented_storage_prefix_and_params_scoped_leaf()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 1, fps: 25, width: 320, height: 240, withAudio: false);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);
            Guid projectId = Guid.NewGuid();

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            MediaDerivativeResult result = await service.ProduceAsync(
                projectId, sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            string sourceHash = MediaDerivativeCacheKey.ComputeSourceHash(sourceKey, etag);
            string paramsHash = MediaDerivativeCacheKey.ComputeParamsHash(spec);

            result.StoragePrefix.Should().Be(
                $"projects/{projectId:D}/agentFiles/media-derivatives/{sourceHash}");
            result.Artifacts.Should().HaveCount(1);
            result.Artifacts[0].FileName.Should().Be($"proxy-{paramsHash[..16]}.mp4");
            result.Artifacts[0].ContentType.Should().Be("video/mp4");
            result.Artifacts[0].FileName.Should().NotContain("..");
            result.Artifacts[0].FileName.Should().NotContain("/");
            File.Exists(result.Artifacts[0].LocalPath).Should().BeTrue();
            result.SizeBytes.Should().BeGreaterThan(0);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Progress_reports_Queued_first_and_Done_last()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 1, fps: 25, width: 320, height: 240, withAudio: false);

            (_, MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            var stages = new List<MediaDerivativeStage>();
            var progress = new CollectingProgress(stages.Add);

            await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress, CancellationToken.None);

            stages.Should().NotBeEmpty();
            stages[0].Should().Be(MediaDerivativeStage.Queued);
            stages.Should().Contain(MediaDerivativeStage.Probing);
            stages.Should().Contain(MediaDerivativeStage.Proxy);
            stages[^1].Should().Be(MediaDerivativeStage.Done);
            stages.Should().NotContain(MediaDerivativeStage.Skipped);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    private static (FfmpegVideoToolRunner Runner, MediaDerivativeService Service, string SourceKey, string Etag)
        Build(string directory)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
        FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
        FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);
        MediaDerivativeService service = MediaDerivativeFfmpegFixture.BuildService(runner, probe, options);

        return (runner, service, "projects/11111111-1111-1111-1111-111111111111/agentFiles/source.mp4", "etag-proxy");
    }

    private sealed class CollectingProgress(Action<MediaDerivativeStage> onReport) : IProgress<MediaDerivativeStage>
    {
        public void Report(MediaDerivativeStage value) => onReport(value);
    }
}
