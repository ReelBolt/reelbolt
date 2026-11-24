using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The proxy targets the SHORT side: landscape/square specs are frozen, portrait scales the width.</summary>
public class MediaDerivativeProxyOrientationVideoTests
{
    private const string Landscape1080Json =
        "{\"audioBitrateKbps\":128,\"audioChannels\":2,\"audioCodec\":true,\"audioSampleRate\":48000,\"crf\":23," +
        "\"faststart\":true,\"gopFrames\":15,\"keyintMin\":15,\"pixFmt\":\"yuv420p\",\"preset\":\"veryfast\"," +
        "\"profile\":\"main\",\"scThreshold\":0,\"scaleFlags\":\"bicubic\",\"targetHeight\":540,\"threads\":4," +
        "\"videoCodec\":\"libx264\",\"widthSentinel\":-2}";

    private const string Landscape2160Json =
        "{\"audioBitrateKbps\":128,\"audioChannels\":2,\"audioCodec\":true,\"audioSampleRate\":48000,\"crf\":23," +
        "\"faststart\":true,\"gopFrames\":15,\"keyintMin\":15,\"pixFmt\":\"yuv420p\",\"preset\":\"veryfast\"," +
        "\"profile\":\"main\",\"scThreshold\":0,\"scaleFlags\":\"bicubic\",\"targetHeight\":720,\"threads\":4," +
        "\"videoCodec\":\"libx264\",\"widthSentinel\":-2}";

    private const string Portrait1080Json =
        "{\"audioBitrateKbps\":128,\"audioChannels\":2,\"audioCodec\":true,\"audioSampleRate\":48000,\"crf\":23," +
        "\"faststart\":true,\"gopFrames\":15,\"heightSentinel\":-2,\"keyintMin\":15,\"pixFmt\":\"yuv420p\"," +
        "\"preset\":\"veryfast\",\"profile\":\"main\",\"scThreshold\":0,\"scaleFlags\":\"bicubic\"," +
        "\"targetWidth\":540,\"threads\":4,\"videoCodec\":\"libx264\"}";

    [Theory]
    [InlineData(1920, 1080, Landscape1080Json)]
    [InlineData(3840, 2160, Landscape2160Json)]
    [InlineData(1080, 1080, Landscape1080Json)]
    [InlineData(0, 1080, Landscape1080Json)]
    [InlineData(0, 1920, "")]
    public void Landscape_square_and_unknown_width_specs_are_byte_identical_to_the_legacy_height_spec(
        int width, int height, string expectedJson)
    {
        string json = MediaDerivativeSpec.ForProxy(height, 30, 1, true, 4, width).ParamsJson;

        if (expectedJson.Length > 0)
            json.Should().Be(expectedJson);
        else
            json.Should().Contain("\"targetHeight\":540").And.Contain("widthSentinel");

        json.Should().Be(MediaDerivativeSpec.ForProxy(height, 30, 1, true, 4).ParamsJson);
    }

    [Fact]
    public void Portrait_spec_scales_the_width_and_changes_the_params_hash()
    {
        MediaDerivativeSpec portrait = MediaDerivativeSpec.ForProxy(1920, 30, 1, true, 4, 1080);

        portrait.ParamsJson.Should().Be(Portrait1080Json);
        MediaDerivativeCacheKey.ComputeParamsHash(portrait).Should().NotBe(
            MediaDerivativeCacheKey.ComputeParamsHash(MediaDerivativeSpec.ForProxy(1920, 30, 1, true, 4)));
    }

    [Theory]
    [InlineData(1080, 1920, 540)]
    [InlineData(2160, 3840, 720)]
    [InlineData(2159, 3840, 540)]
    [InlineData(480, 854, 540)]
    public void Portrait_target_follows_the_short_side(int width, int height, int expected)
    {
        MediaDerivativeSpec.ForProxy(height, 30, 1, true, 4, width).ParamsJson
            .Should().Contain($"\"targetWidth\":{expected}");
    }

    [Fact]
    public void Portrait_argv_scales_the_width_with_the_height_sentinel_and_landscape_argv_is_unchanged()
    {
        string[] portrait = ProxyArgvBuilder.Build("/in", "/out", 540, 15, true, 4, scaleWidth: true);
        string[] landscape = ProxyArgvBuilder.Build("/in", "/out", 540, 15, true, 4);

        portrait.Should().ContainInOrder("-vf", "scale=540:-2:flags=bicubic");
        landscape.Should().ContainInOrder("-vf", "scale=-2:540:flags=bicubic");
    }

    [FfmpegToolsFact]
    public async Task A_1080x1920_source_yields_a_540x960_proxy_and_landscape_stays_960x540()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);
            MediaDerivativeService service = MediaDerivativeFfmpegFixture.BuildService(runner, probe, options);

            foreach ((int w, int h, int ew, int eh) in new[] { (1080, 1920, 540, 960), (1920, 1080, 960, 540) })
            {
                string source = MediaDerivativeFfmpegFixture.CreateSource(
                    directory, $"src{w}.mp4", seconds: 1, fps: 10, width: w, height: h, withAudio: false);

                MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                    MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);
                MediaDerivativeResult result = await service.ProduceAsync(
                    Guid.NewGuid(), Path.Combine("projects", "p", $"src{w}.mp4"), "etag", spec, source,
                    progress: null, CancellationToken.None);

                MediaDerivativeArtifact mp4 = result.Artifacts[0];
                MediaDerivativeFfmpegFixture.ProbeDimensions(mp4.LocalPath).Should().Be((ew, eh));
            }
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }
}
