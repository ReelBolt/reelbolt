#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Single-purpose output routes: inline file stream, segmentation tracks, timeline list.</summary>
public sealed class SmallRoutesSurfaceTests
{
    private static void ShouldBeRefused(IActionResult? result)
    {
        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(404);
        refused.Value!.ToString().Should().Contain("OUTPUT_UNAVAILABLE");
    }

    // ---- inline file stream -------------------------------------------------------------------

    private static ProjectFileMediaController FileMedia(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 3 });
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });
        return new ProjectFileMediaController(db ?? w.Db, w.User, w.Access(), s3.Object, new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task Inline_stream_of_an_earlier_output_file_is_refused()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        ShouldBeRefused(await FileMedia(w).Stream(w.Project.Id, w.EarlierFile.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inline_stream_of_a_later_output_file_or_an_upload_is_served_while_earlier_outputs_are_kept(bool later)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await FileMedia(w).Stream(w.Project.Id, (later ? w.LaterFile : w.Upload).Id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Inline_stream_of_the_earlier_output_file_is_served_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await FileMedia(w).Stream(w.Project.Id, w.EarlierFile.Id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task Inline_stream_on_a_failed_read_is_refused()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        ShouldBeRefused(await FileMedia(w, failing).Stream(w.Project.Id, w.EarlierFile.Id, CancellationToken.None));
    }

    // ---- segmentation tracks ------------------------------------------------------------------

    private static SegmentationTracksController Tracks(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{\"segmentationTracks\":[{\"id\":\"t1\",\"prompt\":\"a person\",\"startSec\":0,\"endSec\":1}]}")));
        return new SegmentationTracksController(db ?? w.Db, w.Access(), storage.Object, NullLogger<SegmentationTracksController>.Instance);
    }

    private static SurfaceWorld AnalysisWorld(SurfaceState state)
    {
        SurfaceWorld w = new();
        w.Step.StepType = StepType.VideoAnalyze;
        return w.In(state);
    }

    [Fact]
    public async Task Segmentation_tracks_of_an_earlier_result_are_refused()
    {
        SurfaceWorld w = AnalysisWorld(SurfaceState.Held);
        ShouldBeRefused((await Tracks(w).Get(w.Project.Id, w.EarlierResult.Id, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Segmentation_tracks_of_a_later_result_are_served_while_earlier_outputs_are_kept()
    {
        SurfaceWorld w = AnalysisWorld(SurfaceState.Held);
        (await Tracks(w).Get(w.Project.Id, w.LaterResult.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Segmentation_tracks_of_the_earlier_result_are_served_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = AnalysisWorld(state);
        (await Tracks(w).Get(w.Project.Id, w.EarlierResult.Id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Segmentation_tracks_on_a_failed_read_are_refused()
    {
        SurfaceWorld w = AnalysisWorld(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        ShouldBeRefused((await Tracks(w, failing).Get(w.Project.Id, w.LaterResult.Id, CancellationToken.None)).Result);
    }

    // ---- timeline list ------------------------------------------------------------------------

    private static async Task<List<Guid>> ListedTimelines(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        TimelinesController c = new(db ?? w.Db, w.User, w.Access());
        ActionResult<List<ProjectTimelineResponse>> r = await c.ListTimelines(w.Project.Id, CancellationToken.None);
        return ((List<ProjectTimelineResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).Select(t => t.StepResultId).ToList();
    }

    [Fact]
    public async Task The_timeline_list_leaves_out_an_earlier_result()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await ListedTimelines(w)).Should().BeEquivalentTo([w.LaterResult.Id]);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task The_timeline_list_has_both_results_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await ListedTimelines(w)).Should().BeEquivalentTo([w.EarlierResult.Id, w.LaterResult.Id]);
    }

    [Fact]
    public async Task The_timeline_list_on_a_failed_read_is_empty()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        (await ListedTimelines(w, failing)).Should().BeEmpty();
    }
}
