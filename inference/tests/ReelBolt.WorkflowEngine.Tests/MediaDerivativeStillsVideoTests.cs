#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The <c>Stills</c> derivative (T-101): up to twelve full-size JPEG frames of a video — evenly spaced
/// with the first and last frame, or at requested times — produced on demand only, keyed by
/// (source, times) so a repeat ask never re-extracts.
/// </summary>
public class MediaDerivativeStillsVideoTests
{
    // ---------------------------------------------------------------- requested-time normalization

    [Fact]
    public void Requested_times_are_rounded_to_a_tenth_deduplicated_sorted_and_capped_at_twelve()
    {
        int[] ms = MediaDerivativeStillTimes.NormalizeToMs([3.04, -2, 3.0, double.NaN, 100, double.PositiveInfinity, 1.26]);

        ms.Should().Equal(0, 1300, 3000, 100000);

        MediaDerivativeStillTimes.NormalizeToMs(Enumerable.Range(0, 30).Select(i => (double)i).ToArray())
            .Should().HaveCount(MediaDerivativeStillTimes.MaxStills);
        MediaDerivativeStillTimes.NormalizeToMs(null).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- time planning

    [Fact]
    public void Even_stills_are_twelve_evenly_spaced_frames_from_the_first_to_the_last()
    {
        IReadOnlyList<int> times = StillsArgvBuilder.PlanTimesMs(durationSec: 35, requestedMs: []);

        times.Should().HaveCount(12);
        times[0].Should().Be(0);
        times[^1].Should().Be(34900, "the last still is the last frame, a tenth of a second before the end");
        times.Should().BeInAscendingOrder();
        times.Zip(times.Skip(1), (a, b) => b - a).Should().OnlyContain(gap => gap >= 3100 && gap <= 3200);
    }

    [Fact]
    public void Requested_times_are_clamped_to_the_duration_and_deduplicated_after_clamping()
    {
        StillsArgvBuilder.PlanTimesMs(durationSec: 35, requestedMs: [5000, 99000, 120000])
            .Should().Equal(5000, 34900);
    }

    [Fact]
    public void A_source_with_no_measured_duration_gets_its_first_frame_only()
    {
        StillsArgvBuilder.PlanTimesMs(durationSec: 0, requestedMs: []).Should().Equal(0);
    }

    // ---------------------------------------------------------------- argv

    [Fact]
    public void One_still_is_one_input_seek_one_frame_long_side_1280_quality_3()
    {
        string[] args = StillsArgvBuilder.Build("/scratch/source.mp4", "/scratch/still-01.jpg", atMs: 12500);

        int seek = Array.IndexOf(args, "-ss");
        int input = Array.IndexOf(args, "-i");
        seek.Should().BeGreaterThan(-1);
        seek.Should().BeLessThan(input, "an input seek lands on the frame without decoding from the start");
        args[seek + 1].Should().Be("12.5");
        args[input + 1].Should().Be("/scratch/source.mp4");
        args.Should().ContainInConsecutiveOrder("-frames:v", "1");
        args.Should().ContainInConsecutiveOrder("-q:v", "3");
        args.Should().ContainInConsecutiveOrder("-protocol_whitelist", "file");
        string filter = args[Array.IndexOf(args, "-vf") + 1];
        filter.Should().Contain("min(1280,iw)").And.Contain("min(1280,ih)").And.Contain("force_original_aspect_ratio=decrease");
        args[^1].Should().Be("/scratch/still-01.jpg");
    }

    // ---------------------------------------------------------------- plan + identity

    [Fact]
    public void Stills_are_on_demand_only_never_part_of_the_default_set()
    {
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", null)
            .Should().NotContain(p => p.Slug == MediaDerivativeKindSlug.Stills);
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", MediaDerivativeRequests.DefaultKinds)
            .Should().NotContain(p => p.Slug == MediaDerivativeKindSlug.Stills);
        MediaDerivativeRequests.DefaultKinds.Should().NotContain(MediaDerivativeKind.Stills);

        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", [MediaDerivativeKind.Stills])
            .Should().Equal((MediaDerivativeKindSlug.Stills, 0));
        MediaDerivativeDispatcher.ResolveSpecPlan("audio/wav", [MediaDerivativeKind.Stills]).Should().BeEmpty();
        MediaDerivativeDispatcher.ResolveSpecPlan("image/png", [MediaDerivativeKind.Stills]).Should().BeEmpty();
    }

    [Fact]
    public void The_cache_key_is_a_function_of_the_source_and_the_times()
    {
        const string sourceObject = "projects/p/outputFiles/e/final.mp4";
        string even = MediaDerivativeCacheKey.ComputeCacheKey(sourceObject, "e1", MediaDerivativeSpec.ForStills([]));
        string evenAgain = MediaDerivativeCacheKey.ComputeCacheKey(sourceObject, "e1", MediaDerivativeSpec.ForStills([]));
        string at5 = MediaDerivativeCacheKey.ComputeCacheKey(sourceObject, "e1", MediaDerivativeSpec.ForStills([5000]));
        string at5Again = MediaDerivativeCacheKey.ComputeCacheKey(sourceObject, "e1", MediaDerivativeSpec.ForStills([5000]));
        string at6 = MediaDerivativeCacheKey.ComputeCacheKey(sourceObject, "e1", MediaDerivativeSpec.ForStills([6000]));

        even.Should().Be(evenAgain);
        at5.Should().Be(at5Again);
        new[] { even, at5, at6 }.Distinct().Should().HaveCount(3);

        MediaDerivativeSpec.ForStills([]).Variant.Should().Be(0);
        MediaDerivativeSpec.ForStills([5000]).Variant.Should().Be(1);
        MediaDerivativeSpec.StillTimesMsOf(MediaDerivativeSpec.ForStills([5000, 7500])).Should().Equal(5000, 7500);
        MediaDerivativeSpec.StillTimesMsOf(MediaDerivativeSpec.ForStills([])).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the dispatcher, real ffmpeg

    [FfmpegToolsFact]
    public async Task Even_stills_are_one_row_with_twelve_jpeg_objects_and_an_index_and_a_repeat_is_a_cache_hit()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 6, fps: 25, width: 1920, height: 1080, withAudio: false);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string sourceObject = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            MediaDerivativesRequested request = fixture.Request(projectId, sourceObject, "\"etag-1\"", [MediaDerivativeKind.Stills]);

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            IReadOnlyList<IReadOnlyList<string>> stillArgv = fixture.Runner.FfmpegArgvs
                .Where(a => a.Contains("-frames:v")).ToList();
            stillArgv.Should().HaveCount(12, "twelve evenly spaced stills, one input seek each");
            stillArgv.Select(a => a[a.ToList().IndexOf("-ss") + 1]).First().Should().Be("0");
            stillArgv.Select(a => a[a.ToList().IndexOf("-ss") + 1]).Last().Should().Be("5.9");

            MediaDerivative row = fixture.ReadRows().Should().ContainSingle().Subject;
            row.Kind.Should().Be(MediaDerivativeKind.Stills);
            row.MimeType.Should().Be("image/jpeg");
            row.IndexStorageKey.Should().NotBeNullOrWhiteSpace();
            row.Width.Should().Be(1280, "the long side is capped at 1280");
            row.Height.Should().Be(720);

            string sourceHash = MediaDerivativeCacheKey.ComputeSourceHash(sourceObject, "\"etag-1\"");
            string prefix = $"projects/{projectId:D}/agentFiles/media-derivatives/{sourceHash}/";
            fixture.Workspace.UploadedKeys.Should().OnlyContain(k => k.StartsWith(prefix, StringComparison.Ordinal));
            fixture.Workspace.UploadedKeys.Count(k => k.EndsWith(".jpg", StringComparison.Ordinal)).Should().Be(12);
            fixture.Workspace.UploadedKeys.Should().Contain(row.IndexStorageKey!);

            int ffmpegBefore = fixture.Runner.FfmpegCount;
            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);
            fixture.Runner.FfmpegCount.Should().Be(ffmpegBefore, "the same (source, times) is a cache hit and extracts nothing");
            fixture.ReadRows().Should().HaveCount(1);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Requested_times_are_extracted_at_those_times_and_cached_separately_from_the_even_set()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 4, fps: 25, width: 320, height: 240, withAudio: false);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string sourceObject = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            MediaDerivativesRequested request = fixture.Request(projectId, sourceObject, "\"etag-1\"", [MediaDerivativeKind.Stills])
                with
            { StillTimesSec = [2.5, 1.0, 60] };

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            fixture.Runner.FfmpegArgvs.Where(a => a.Contains("-frames:v"))
                .Select(a => a[a.ToList().IndexOf("-ss") + 1])
                .Should().Equal("1", "2.5", "3.9");

            MediaDerivative row = fixture.ReadRows().Should().ContainSingle().Subject;
            row.Width.Should().Be(320, "a small source is never upscaled");
            row.ParamsJson.Should().Contain("\"timesMs\":[1000,2500,60000]");

            int before = fixture.Runner.FfmpegCount;
            await fixture.Dispatcher.RunAsync(request with { StillTimesSec = [1.0, 2.5, 60.0] }, CancellationToken.None);
            fixture.Runner.FfmpegCount.Should().Be(before, "the same times in another order are the same request");

            await fixture.Dispatcher.RunAsync(request with { StillTimesSec = null }, CancellationToken.None);
            fixture.ReadRows().Should().HaveCount(2, "the even set is its own cache entry");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }
}
