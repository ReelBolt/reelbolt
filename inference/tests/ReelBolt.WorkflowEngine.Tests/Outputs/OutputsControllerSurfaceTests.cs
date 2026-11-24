#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// The output routes (list, pin, download, delete, prune) against the three project states: an earlier output is
/// refused while the project keeps its outputs, a later one and an upload are served, a project with only the
/// moderation flag serves everything, and a failed marker read refuses.
/// </summary>
public sealed class OutputsControllerSurfaceTests
{
    private static (OutputsController Controller, Mock<IAmazonS3> S3, List<string> Deleted) NewController(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        Mock<IAmazonS3> s3 = new();
        List<string> deleted = [];
        s3.Setup(s => s.DeleteObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, key, _) => deleted.Add(key))
            .ReturnsAsync(new DeleteObjectResponse());
        s3.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 4 });
        s3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3, 4]) });
        Mock<IFileStorageService> storage = new();
        storage.SetupGet(s => s.BucketName).Returns("reelforge");
        OutputsController controller = new(
            db ?? w.Db, w.User, w.Access(), s3.Object, storage.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, s3, deleted);
    }

    private static void ShouldBeRefused(IActionResult? result)
    {
        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(404);
        refused.Value!.ToString().Should().Contain("OUTPUT_UNAVAILABLE");
    }

    private static List<OutputVideoResponse> Listed(ActionResult<List<OutputVideoResponse>> r) =>
        (List<OutputVideoResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    [Fact]
    public async Task List_leaves_out_the_earlier_output_while_the_project_keeps_its_outputs()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, _) = NewController(w);
        Listed(await c.ListOutputs(w.Project.Id, CancellationToken.None)).Select(o => o.StepResultId)
            .Should().BeEquivalentTo([w.LaterResult.Id]);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task List_serves_both_outputs_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _, _) = NewController(w);
        Listed(await c.ListOutputs(w.Project.Id, CancellationToken.None)).Select(o => o.StepResultId)
            .Should().BeEquivalentTo([w.EarlierResult.Id, w.LaterResult.Id]);
    }

    [Fact]
    public async Task List_on_a_failed_read_returns_no_outputs()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, _, _) = NewController(w, failing);
        Listed(await c.ListOutputs(w.Project.Id, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pin_of_the_earlier_output_is_refused_and_changes_nothing_while_the_project_keeps_its_outputs()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, _) = NewController(w);
        ShouldBeRefused(await c.PinTake(w.Project.Id, w.EarlierResult.Id, new PinTakeRequest(true), CancellationToken.None));
        w.Db.WorkflowStepResults.Single(r => r.Id == w.EarlierResult.Id).IsPinned.Should().BeFalse();
    }

    [Fact]
    public async Task Pin_of_a_later_output_is_served_while_the_project_keeps_earlier_ones()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, _) = NewController(w);
        (await c.PinTake(w.Project.Id, w.LaterResult.Id, new PinTakeRequest(true), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Pin_of_the_earlier_output_is_served_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _, _) = NewController(w);
        (await c.PinTake(w.Project.Id, w.EarlierResult.Id, new PinTakeRequest(true), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        w.Db.WorkflowStepResults.Single(r => r.Id == w.EarlierResult.Id).IsPinned.Should().BeTrue();
    }

    [Fact]
    public async Task Pin_on_a_failed_read_is_refused()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, _, _) = NewController(w, failing);
        ShouldBeRefused(await c.PinTake(w.Project.Id, w.EarlierResult.Id, new PinTakeRequest(true), CancellationToken.None));
    }

    [Fact]
    public async Task Download_of_the_earlier_output_is_refused_before_the_store_is_touched()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, s3, _) = NewController(w);
        ShouldBeRefused(await c.DownloadOutput(w.Project.Id, w.EarlierResult.Id, CancellationToken.None));
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Download_of_a_later_output_is_served_while_the_project_keeps_earlier_ones()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, _) = NewController(w);
        (await c.DownloadOutput(w.Project.Id, w.LaterResult.Id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Download_of_the_earlier_output_is_served_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _, _) = NewController(w);
        (await c.DownloadOutput(w.Project.Id, w.EarlierResult.Id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task Download_on_a_failed_read_is_refused()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, s3, _) = NewController(w, failing);
        ShouldBeRefused(await c.DownloadOutput(w.Project.Id, w.LaterResult.Id, CancellationToken.None));
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_of_the_earlier_take_keeps_its_object_and_rows_while_the_project_keeps_its_outputs()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, deleted) = NewController(w);
        await c.DeleteTake(w.Project.Id, w.EarlierResult.Id, CancellationToken.None);
        deleted.Should().BeEmpty();
        w.Db.ProjectFiles.Any(f => f.Id == w.EarlierFile.Id).Should().BeTrue();
        w.Db.WorkflowStepResults.Single(r => r.Id == w.EarlierResult.Id).OutputStorageKey.Should().Be(w.EarlierKey);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Delete_of_the_earlier_take_removes_its_object_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _, deleted) = NewController(w);
        await c.DeleteTake(w.Project.Id, w.EarlierResult.Id, CancellationToken.None);
        deleted.Should().Contain(w.EarlierKey);
    }

    [Fact]
    public async Task Delete_on_a_failed_read_keeps_the_object_and_rows()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, _, deleted) = NewController(w, failing);
        await c.DeleteTake(w.Project.Id, w.EarlierResult.Id, CancellationToken.None);
        deleted.Should().BeEmpty();
        w.Db.ProjectFiles.Any(f => f.Id == w.EarlierFile.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Prune_keeps_the_earlier_take_object_and_rows_while_the_project_keeps_its_outputs()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _, deleted) = NewController(w);
        string group = Listed(await c.ListOutputs(w.Project.Id, CancellationToken.None)).Single(o => o.StepResultId == w.LaterResult.Id).TakeGroupKey!;
        await c.PruneTakes(w.Project.Id, new PruneTakesRequest(group, 1), CancellationToken.None);
        deleted.Should().BeEmpty();
        w.Db.ProjectFiles.Any(f => f.Id == w.EarlierFile.Id).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Prune_removes_the_earlier_take_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _, deleted) = NewController(w);
        string group = Listed(await c.ListOutputs(w.Project.Id, CancellationToken.None)).Single(o => o.StepResultId == w.LaterResult.Id).TakeGroupKey!;
        await c.PruneTakes(w.Project.Id, new PruneTakesRequest(group, 1), CancellationToken.None);
        deleted.Should().Contain(w.EarlierKey);
    }

    [Fact]
    public async Task Prune_on_a_failed_read_keeps_every_object()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        var (list, _, _) = NewController(w);
        string group = Listed(await list.ListOutputs(w.Project.Id, CancellationToken.None)).Single(o => o.StepResultId == w.LaterResult.Id).TakeGroupKey!;
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, _, deleted) = NewController(w, failing);
        await c.PruneTakes(w.Project.Id, new PruneTakesRequest(group, 1), CancellationToken.None);
        deleted.Should().BeEmpty();
    }
}
