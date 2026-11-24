using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="GeneratedClipNormalizer"/>: ensures downloaded MiniMax clips are
/// validated via ffprobe and re-encoded to canonical format matching VideoCompileStepExecutor's
/// EncodeReencodeAsync defaults (libx264, CRF=20, preset=veryfast, pix_fmt=yuv420p, CFR).
/// </summary>
public class VideoGeneratedClipNormalizerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);
    private const string SourceLocalPath = "/tmp/generated.mp4";
    private const string DestLocalPath = "/tmp/normalized.mp4";

    [Fact]
    public async Task NormalizeAsync_returns_destination_path_on_success()
    {
        // Arrange: Mock a successful ffprobe and ffmpeg
        var mockRunner = new Mock<IVideoToolRunner>();
        var mockOptions = Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 });
        var normalizer = new GeneratedClipNormalizer(mockRunner.Object, mockOptions, NullLogger<GeneratedClipNormalizer>.Instance);

        var ffprobeJson = @"{
            ""streams"": [{
                ""codec_type"": ""video"",
                ""codec_name"": ""h264"",
                ""width"": 1920,
                ""height"": 1080,
                ""r_frame_rate"": ""30/1"",
                ""pix_fmt"": ""yuv420p"",
                ""duration"": ""10.0""
            }],
            ""format"": {""duration"": ""10.0""}
        }";

        mockRunner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: ffprobeJson, StdErr: "", TimedOut: false));

        mockRunner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));

        // Act
        var result = await normalizer.NormalizeAsync(SourceLocalPath, DestLocalPath, CancellationToken.None);

        // Assert
        result.Should().Be(DestLocalPath);
    }

    [Fact]
    public async Task NormalizeAsync_fails_on_ffprobe_failure_with_typed_failure()
    {
        // Arrange: Mock ffprobe failure
        var mockRunner = new Mock<IVideoToolRunner>();
        var mockOptions = Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 });
        var normalizer = new GeneratedClipNormalizer(mockRunner.Object, mockOptions, NullLogger<GeneratedClipNormalizer>.Instance);

        mockRunner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 1, StdOut: "", StdErr: "Invalid input", TimedOut: false));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GeneratedClipNormalizationException>(
            async () => await normalizer.NormalizeAsync(SourceLocalPath, DestLocalPath, CancellationToken.None));

        exception.Reason.Should().Be(GeneratedClipNormalizationFailureReason.FfprobeValidationFailed);
    }

    [Fact]
    public async Task NormalizeAsync_fails_when_input_has_no_video_stream()
    {
        // Arrange: Mock ffprobe success but no video stream
        var mockRunner = new Mock<IVideoToolRunner>();
        var mockOptions = Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 });
        var normalizer = new GeneratedClipNormalizer(mockRunner.Object, mockOptions, NullLogger<GeneratedClipNormalizer>.Instance);

        var ffprobeJson = @"{
            ""streams"": [{
                ""codec_type"": ""audio"",
                ""codec_name"": ""aac""
            }],
            ""format"": {""duration"": ""10.0""}
        }";

        mockRunner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: ffprobeJson, StdErr: "", TimedOut: false));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GeneratedClipNormalizationException>(
            async () => await normalizer.NormalizeAsync(SourceLocalPath, DestLocalPath, CancellationToken.None));

        exception.Reason.Should().Be(GeneratedClipNormalizationFailureReason.NoVideoStream);
    }

    [Fact]
    public async Task NormalizeAsync_builds_ffmpeg_args_with_canonical_encoding_defaults()
    {
        // Arrange: Mock ffprobe success, capture ffmpeg args
        var mockRunner = new Mock<IVideoToolRunner>();
        var mockOptions = Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 });
        var normalizer = new GeneratedClipNormalizer(mockRunner.Object, mockOptions, NullLogger<GeneratedClipNormalizer>.Instance);

        var ffprobeJson = @"{
            ""streams"": [{
                ""codec_type"": ""video"",
                ""codec_name"": ""h264"",
                ""width"": 1920,
                ""height"": 1080,
                ""r_frame_rate"": ""30/1"",
                ""pix_fmt"": ""yuv420p"",
                ""duration"": ""10.0""
            }],
            ""format"": {""duration"": ""10.0""}
        }";

        mockRunner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: ffprobeJson, StdErr: "", TimedOut: false));

        IReadOnlyList<string> capturedArgs = null;
        mockRunner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => capturedArgs = args)
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));

        // Act
        await normalizer.NormalizeAsync(SourceLocalPath, DestLocalPath, CancellationToken.None);

        // Assert
        capturedArgs.Should().NotBeNull();

        // Should contain protocol whitelist
        capturedArgs.Should().Contain("-protocol_whitelist").And.Contain("file");

        // Untrusted bytes + extensionless scratch paths: demuxer and muxer must be forced.
        capturedArgs.Should().ContainInOrder("-f", "mov", "-i", SourceLocalPath);
        capturedArgs.Should().ContainInOrder("-f", "mp4", DestLocalPath);

        // Should contain input and output
        capturedArgs.Should().Contain(SourceLocalPath);
        capturedArgs.Should().Contain(DestLocalPath);

        // Should contain canonical codec/crf/preset/pix_fmt
        var argsList = (List<string>)capturedArgs;
        var codecIndex = argsList.IndexOf("-c:v");
        argsList[codecIndex + 1].Should().Be("libx264");

        var crfIndex = argsList.IndexOf("-crf");
        argsList[crfIndex + 1].Should().Be("20");

        var presetIndex = argsList.IndexOf("-preset");
        argsList[presetIndex + 1].Should().Be("veryfast");

        // Should have video filter for fps and format
        var argsList2 = capturedArgs.ToList();
        argsList2.Should().Contain("-vf", "should have video filter argument");
        var vfIndex = argsList2.IndexOf("-vf");
        var filterString = argsList2[vfIndex + 1];
        filterString.Should().Contain("fps=30/1", "should enforce constant frame rate");
        filterString.Should().Contain("format=yuv420p", "should enforce yuv420p pixel format");
    }

    [Fact]
    public async Task NormalizeAsync_fails_when_ffmpeg_encode_fails()
    {
        // Arrange: Mock ffprobe success, ffmpeg failure
        var mockRunner = new Mock<IVideoToolRunner>();
        var mockOptions = Options.Create(new VideoEditingOptions { AnalyzeTimeoutSeconds = 10, CompileTimeoutSeconds = 60 });
        var normalizer = new GeneratedClipNormalizer(mockRunner.Object, mockOptions, NullLogger<GeneratedClipNormalizer>.Instance);

        var ffprobeJson = @"{
            ""streams"": [{
                ""codec_type"": ""video"",
                ""codec_name"": ""h264"",
                ""width"": 1920,
                ""height"": 1080,
                ""r_frame_rate"": ""30/1"",
                ""pix_fmt"": ""yuv420p"",
                ""duration"": ""10.0""
            }],
            ""format"": {""duration"": ""10.0""}
        }";

        mockRunner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: ffprobeJson, StdErr: "", TimedOut: false));

        mockRunner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 1, StdOut: "", StdErr: "Encoding error", TimedOut: false));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GeneratedClipNormalizationException>(
            async () => await normalizer.NormalizeAsync(SourceLocalPath, DestLocalPath, CancellationToken.None));

        exception.Reason.Should().Be(GeneratedClipNormalizationFailureReason.EncodingFailed);
    }
}
