using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Services.Marketplace;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplacePreviewEngineTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "mp-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, true);
    }

    private const string SourceProbe =
        """{"format":{"duration":"{D}","format_name":"mov,mp4"},"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30/1","duration":"{D}"},{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}]}""";

    private const string AudioOnlyProbe =
        """{"format":{"duration":"20"},"streams":[{"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":2}]}""";

    private sealed class FakeStorage : IMarketplacePreviewStorage
    {
        public readonly List<string> Uploaded = [];
        public string? Refuse;
        public int Downloads;

        public Task DownloadSourceAsync(string sourceKey, string destinationPath, long maxBytes, CancellationToken ct)
        {
            Downloads++;
            if (Refuse is not null)
                throw new MarketplaceSourceException(Refuse);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, [1, 2, 3]);
            return Task.CompletedTask;
        }

        public Task UploadAsync(string platformKey, string localPath, string contentType, CancellationToken ct)
        {
            Uploaded.Add(platformKey);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunner(string sourceProbe) : IVideoToolRunner
    {
        public readonly List<IReadOnlyList<string>> Ffmpeg = [];
        public readonly List<VideoToolPriority> Priorities = [];
        public bool FailEncode;

        private Task<VideoToolResult> Probe(IReadOnlyList<string> args, VideoToolPriority p)
        {
            Priorities.Add(p);
            string json = args[^1].EndsWith("preview.mp4", StringComparison.Ordinal)
                ? SourceProbe.Replace("{D}", "30")
                : sourceProbe;
            return Task.FromResult(new VideoToolResult(0, json, string.Empty, false));
        }

        private Task<VideoToolResult> Encode(IReadOnlyList<string> args, VideoToolPriority p)
        {
            Priorities.Add(p);
            Ffmpeg.Add(args.ToList());
            if (FailEncode)
                return Task.FromResult(new VideoToolResult(1, string.Empty, "x", false));
            File.WriteAllBytes(args[^1], [9, 9, 9, 9]);
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Encode(args, VideoToolPriority.Normal);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Encode(args, VideoToolPriority.Normal);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Probe(args, VideoToolPriority.Normal);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Encode(args, priority);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) => Encode(args, priority);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Probe(args, priority);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Encode(args, VideoToolPriority.Normal);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Encode(args, VideoToolPriority.Normal);
    }

    private (MarketplacePreviewService Service, FakeStorage Storage, FakeRunner Runner) Make(string probe, bool enabled = true)
    {
        FakeStorage storage = new();
        FakeRunner runner = new(probe);
        MarketplacePreviewService service = new(
            storage, runner,
            Options.Create(new VideoEditingOptions { ScratchPath = _scratch }),
            Options.Create(new MarketplacePreviewOptions { Enabled = enabled }),
            NullLogger<MarketplacePreviewService>.Instance);
        return (service, storage, runner);
    }

    private static MarketplacePreviewRequested Request(string? key = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), key ?? $"projects/{Guid.NewGuid()}/outputFiles/a.mp4", Guid.NewGuid(), DateTime.UtcNow, MarketplacePreviewKeys.NewNonce());

    // ---------------------------------------------------------------- argv (golden)

    [Fact]
    public void Preview_argv_is_golden()
    {
        string[] argv = MarketplacePreviewArgvBuilder.BuildPreview("/s/source.bin", "/s/preview.mp4", 30);
        string.Join(' ', argv).Should().Be(
            "-y -nostdin -hide_banner -protocol_whitelist file -format_whitelist " + MediaDerivativeInputFormats.Whitelist +
            " -i /s/source.bin -t 30 -map 0:v:0 -map 0:a:0? -sn -dn -map_metadata -1 -map_chapters -1" +
            " -vf scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p" +
            " -c:v libx264 -preset veryfast -crf 23 -c:a aac -b:a 128k -movflags +faststart -f mp4 /s/preview.mp4");
        argv[Array.IndexOf(argv, "-protocol_whitelist") + 1].Should().Be("file");
    }

    [Fact]
    public void Poster_argv_is_golden_and_the_poster_frame_is_one_second_in_or_the_middle()
    {
        string.Join(' ', MarketplacePreviewArgvBuilder.BuildPoster("/s/preview.mp4", "/s/poster.jpg", 1)).Should().Be(
            "-y -nostdin -hide_banner -protocol_whitelist file -format_whitelist " + MediaDerivativeInputFormats.Whitelist +
            " -ss 1 -i /s/preview.mp4 -an -frames:v 1" +
            " -vf scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2" +
            " -q:v 3 -update 1 -f image2 /s/poster.jpg");
        MarketplacePreviewArgvBuilder.PosterAt(30).Should().Be(1);
        MarketplacePreviewArgvBuilder.PosterAt(1.5).Should().Be(0.75);
    }

    [Fact]
    public void The_scale_box_never_enlarges()
    {
        MarketplacePreviewArgvBuilder.ScaleFilter.Should().Contain("min(1280,iw)").And.Contain("min(720,ih)").And.Contain("decrease");
    }

    // ---------------------------------------------------------------- probe refusals

    [Fact]
    public async Task A_source_over_ninety_seconds_is_refused_before_any_encode()
    {
        var (service, storage, runner) = Make(SourceProbe.Replace("{D}", "91"));
        MarketplacePreviewFinished result = await service.RunAsync(Request(), default);
        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be(MarketplacePreviewFailureCodes.SourceTooLong);
        runner.Ffmpeg.Should().BeEmpty();
        storage.Uploaded.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_without_a_video_stream_is_refused()
    {
        var (service, storage, runner) = Make(AudioOnlyProbe);
        (await service.RunAsync(Request(), default)).FailureCode.Should().Be(MarketplacePreviewFailureCodes.NotVideo);
        runner.Ffmpeg.Should().BeEmpty();
        storage.Uploaded.Should().BeEmpty();
    }

    [Theory]
    [InlineData(MarketplacePreviewFailureCodes.SourceTooLarge)]
    [InlineData(MarketplacePreviewFailureCodes.SourceMissing)]
    [InlineData(MarketplacePreviewFailureCodes.SourceInvalid)]
    public async Task A_refused_download_ends_in_a_failed_event_with_its_code(string code)
    {
        var (service, storage, _) = Make(SourceProbe.Replace("{D}", "10"));
        storage.Refuse = code;
        MarketplacePreviewFinished result = await service.RunAsync(Request(), default);
        result.Succeeded.Should().BeFalse();
        result.FailureCode.Should().Be(code);
    }

    [Fact]
    public async Task An_encode_failure_is_a_failed_event_and_the_scratch_is_removed()
    {
        var (service, storage, runner) = Make(SourceProbe.Replace("{D}", "10"));
        runner.FailEncode = true;
        (await service.RunAsync(Request(), default)).FailureCode.Should().Be(MarketplacePreviewFailureCodes.EncodeFailed);
        storage.Uploaded.Should().BeEmpty();
        Directory.Exists(Path.Combine(_scratch, MarketplacePreviewService.ScratchSubdirectory)).Should().BeTrue();
        Directory.GetDirectories(Path.Combine(_scratch, MarketplacePreviewService.ScratchSubdirectory)).Should().BeEmpty();
    }

    [Fact]
    public async Task When_the_marketplace_is_off_nothing_runs()
    {
        var (service, storage, runner) = Make(SourceProbe.Replace("{D}", "10"), enabled: false);
        (await service.RunAsync(Request(), default)).FailureCode.Should().Be(MarketplacePreviewFailureCodes.Disabled);
        storage.Downloads.Should().Be(0);
        runner.Ffmpeg.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- success, keys, lane

    [Fact]
    public async Task A_success_uploads_both_objects_under_the_platform_prefix_and_runs_on_the_low_priority_lane()
    {
        var (service, storage, runner) = Make(SourceProbe.Replace("{D}", "45"));
        MarketplacePreviewRequested request = Request();
        MarketplacePreviewFinished result = await service.RunAsync(request, default);

        result.Succeeded.Should().BeTrue();
        result.PreviewKey.Should().Be($"marketplace/{request.RecipeId}/{request.VersionId}/staging/{request.RunNonce}/preview.mp4");
        result.PosterKey.Should().Be($"marketplace/{request.RecipeId}/{request.VersionId}/staging/{request.RunNonce}/poster.jpg");
        result.RunNonce.Should().Be(request.RunNonce);
        storage.Uploaded.Should().NotContain(MarketplacePreviewKeys.Preview(request.RecipeId, request.VersionId), "the engine never writes a final key");
        result.PreviewSha256.Should().HaveLength(64);
        result.Seconds.Should().Be(30);
        result.SizeBytes.Should().Be(4);
        storage.Uploaded.Should().Equal(result.PreviewKey, result.PosterKey);
        storage.Uploaded.Should().OnlyContain(k => k.StartsWith("marketplace/", StringComparison.Ordinal) && !k.StartsWith("projects/", StringComparison.Ordinal));
        runner.Priorities.Should().OnlyContain(p => p == VideoToolPriority.Low);
        runner.Ffmpeg.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------- keys

    [Fact]
    public void Keys_are_built_only_from_two_guids_and_cannot_collide_with_a_project_key()
    {
        Guid recipe = Guid.NewGuid();
        Guid version = Guid.NewGuid();
        MarketplacePreviewKeys.Preview(recipe, version).Should().Be($"marketplace/{recipe:D}/{version:D}/preview.mp4");
        MarketplacePreviewKeys.Poster(recipe, version).Should().StartWith("marketplace/").And.NotStartWith("projects/");
        MarketplacePreviewKeys.IsVersionObject(recipe, version, MarketplacePreviewKeys.Preview(recipe, version)).Should().BeTrue();
        MarketplacePreviewKeys.IsVersionObject(recipe, version, $"marketplace/{recipe}/{Guid.NewGuid()}/preview.mp4").Should().BeFalse();
        MarketplacePreviewKeys.IsVersionObject(recipe, version, $"projects/{Guid.NewGuid()}/x.mp4").Should().BeFalse();
    }

    [Theory]
    [InlineData("marketplace/a/b/preview.mp4")]
    [InlineData("projects/not-a-guid/x.mp4")]
    [InlineData("projects/00000000-0000-0000-0000-000000000001/../../marketplace/x")]
    [InlineData("projects/00000000-0000-0000-0000-000000000001/")]
    [InlineData("")]
    public void A_source_key_must_be_a_clean_project_key(string key)
    {
        MarketplacePreviewKeys.IsProjectSourceKey(key).Should().BeFalse();
    }

    [Fact]
    public void A_project_key_is_accepted_as_a_source()
    {
        MarketplacePreviewKeys.IsProjectSourceKey($"projects/{Guid.NewGuid()}/outputFiles/a.mp4").Should().BeTrue();
    }

    [Fact]
    public async Task A_superseded_run_uploads_nothing_and_answers_superseded()
    {
        var (service, storage, _) = Make(SourceProbe.Replace("{D}", "10"));
        MarketplacePreviewFinished result = await service.RunAsync(Request(), default, () => true);
        result.FailureCode.Should().Be(MarketplacePreviewFailureCodes.Superseded);
        storage.Uploaded.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_without_a_valid_nonce_is_refused()
    {
        var (service, storage, _) = Make(SourceProbe.Replace("{D}", "10"));
        (await service.RunAsync(Request() with { RunNonce = "../x" }, default)).Succeeded.Should().BeFalse();
        storage.Downloads.Should().Be(0);
    }

    [Fact]
    public async Task The_s3_storage_refuses_a_final_key()
    {
        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        var storage = new S3MarketplacePreviewStorage(s3.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        string path = Path.Combine(_scratch, "f");
        Directory.CreateDirectory(_scratch);
        File.WriteAllBytes(path, [1]);
        Func<Task> act = () => storage.UploadAsync(MarketplacePreviewKeys.Preview(Guid.NewGuid(), Guid.NewGuid()), path, "video/mp4", default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---------------------------------------------------------------- dispatcher idempotency

    [Fact]
    public async Task A_repeat_of_a_version_in_flight_or_done_is_dropped_and_publishes_once()
    {
        var (service, storage, _) = Make(SourceProbe.Replace("{D}", "10"));
        List<MarketplacePreviewFinished> published = [];
        Mock<IBus> bus = new();
        bus.Setup(b => b.Publish(It.IsAny<MarketplacePreviewFinished>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplacePreviewFinished, CancellationToken>((m, _) => { lock (published) published.Add(m); })
            .Returns(Task.CompletedTask);
        MarketplacePreviewDispatcher dispatcher = new(
            service, bus.Object, Options.Create(new VideoEditingOptions { ScratchPath = _scratch }), NullLogger<MarketplacePreviewDispatcher>.Instance);
        MarketplacePreviewRequested request = Request();

        dispatcher.Dispatch(request).Should().BeTrue();
        dispatcher.Dispatch(request).Should().BeFalse("the first run is still in flight or done");
        for (int i = 0; i < 200 && (published.Count == 0 || dispatcher.InFlightCount != 0); i++)
            await Task.Delay(25);

        dispatcher.Dispatch(request).Should().BeFalse("it already finished in this process");
        await Task.Delay(100);
        published.Should().ContainSingle(f => f.VersionId == request.VersionId && f.Succeeded);
        storage.Uploaded.Should().HaveCount(2);
    }
}
