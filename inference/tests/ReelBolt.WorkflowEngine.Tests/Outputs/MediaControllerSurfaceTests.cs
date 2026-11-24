#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.WorkflowEngine.Tests.GitHub;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// The media pool, filmstrip, derivative list and derivative bytes for an earlier output, a later output and an
/// upload, in a project that keeps earlier outputs, one that keeps nothing, and one with only the moderation flag.
/// </summary>
public sealed class MediaControllerSurfaceTests
{
    public enum Source { EarlierResult, LaterResult, EarlierFile, LaterFile, Upload }

    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public InMemoryFileStorage Storage { get; } = new();
        public WorkflowStepResult PoolEarlier { get; }
        public WorkflowStepResult PoolLater { get; }
        public Dictionary<Source, MediaDerivative> Derivative { get; } = [];

        public Rig()
        {
            // Two renders that exist only as step results (no file row), so the pool lists them from the result.
            PoolEarlier = PoolResult(W.EarlierRun, "pool-earlier.mp4", SurfaceWorld.Marker.AddHours(-2));
            PoolLater = PoolResult(W.LaterRun, "pool-later.mp4", SurfaceWorld.Marker.AddDays(2));
            W.Db.WorkflowStepResults.AddRange(PoolEarlier, PoolLater);
            Add(Source.EarlierResult, MediaSourceKind.StepResult, PoolEarlier.Id, null, PoolEarlier.OutputStorageKey!);
            Add(Source.LaterResult, MediaSourceKind.StepResult, PoolLater.Id, null, PoolLater.OutputStorageKey!);
            Add(Source.EarlierFile, MediaSourceKind.ProjectFile, null, W.EarlierFile.Id, W.EarlierFile.StorageKey);
            Add(Source.LaterFile, MediaSourceKind.ProjectFile, null, W.LaterFile.Id, W.LaterFile.StorageKey);
            Add(Source.Upload, MediaSourceKind.ProjectFile, null, W.Upload.Id, W.Upload.StorageKey);
            W.Db.SaveChanges();
        }

        private WorkflowStepResult PoolResult(WorkflowExecution run, string name, DateTime at) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = W.Step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{W.Project.Id}/outputFiles/{name}",
            ExecutedAt = at,
            CompletedAt = at,
        };

        private void Add(Source which, MediaSourceKind kind, Guid? resultId, Guid? fileId, string sourceKey)
        {
            string dir = $"projects/{W.Project.Id}/agentFiles/media-derivatives/{which}";
            MediaDerivative d = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                SourceKind = kind,
                SourceStepResultId = resultId,
                SourceProjectFileId = fileId,
                SourceStorageKey = sourceKey,
                Kind = MediaDerivativeKind.Filmstrip,
                StorageKey = $"{dir}/sheet-0.jpg",
                StorageBucket = "reelforge",
                IndexStorageKey = $"{dir}/index.json",
                MimeType = "image/jpeg",
                DurationSec = 10,
                CreatedAt = SurfaceWorld.Marker.AddDays(3),
            };
            Derivative[which] = d;
            W.Db.MediaDerivatives.Add(d);
            Storage.Objects[d.StorageKey] = [1, 2, 3];
            Storage.Objects[d.IndexStorageKey!] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                densitySec = 2.0,
                thumbWidth = 160,
                thumbHeight = 90,
                cols = 5,
                rows = 4,
                sheetCount = 1,
                sheets = new object[] { new { storageLeaf = "sheet-0.jpg", firstThumbIndex = 0, thumbCount = 5, firstTimeSec = 0.0 } },
            }));
        }

        public string RefOf(Source s) => s switch
        {
            Source.EarlierResult => $"step_result:{PoolEarlier.Id:D}",
            Source.LaterResult => $"step_result:{PoolLater.Id:D}",
            Source.EarlierFile => $"file:{W.EarlierFile.Id:D}",
            Source.LaterFile => $"file:{W.LaterFile.Id:D}",
            _ => $"file:{W.Upload.Id:D}",
        };

        public MediaController Controller(InferenceApiDbContext? db = null)
        {
            Mock<IAmazonS3> s3 = new();
            s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GetObjectMetadataResponse { ContentLength = 3 });
            s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });
            return new MediaController(db ?? W.Db, W.Access(), Storage, s3.Object, Mock.Of<IPublishEndpoint>(), NullLogger<MediaController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }
    }

    private static void ShouldBeRefused(IActionResult? result)
    {
        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(404);
        refused.Value!.ToString().Should().Contain("OUTPUT_UNAVAILABLE");
    }

    private static void ShouldBeRefused<T>(ActionResult<T> result) => ShouldBeRefused(result.Result);

    private static readonly Source[] Withheld = [Source.EarlierResult, Source.EarlierFile];
    private static readonly Source[] Kept = [Source.LaterResult, Source.LaterFile, Source.Upload];

    public static IEnumerable<object[]> WithheldSources() => Withheld.Select(s => new object[] { s });
    public static IEnumerable<object[]> KeptSources() => Kept.Select(s => new object[] { s });

    public static IEnumerable<object[]> AllServed()
    {
        foreach (Source s in Enum.GetValues<Source>())
        {
            yield return [s, SurfaceState.Clear];
            yield return [s, SurfaceState.ModerationOnly];
        }
    }

    private static List<string> RefsOf(ActionResult<List<MediaPoolItemResponse>> r) =>
        ((List<MediaPoolItemResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).Select(i => i.MediaRef).ToList();

    [Fact]
    public async Task The_pool_leaves_out_earlier_outputs_and_lists_later_outputs_and_uploads()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        List<string> refs = RefsOf(await r.Controller().ListMedia(r.W.Project.Id, CancellationToken.None));
        refs.Should().Contain(Kept.Select(r.RefOf));
        refs.Should().NotContain(Withheld.Select(r.RefOf));
    }

    [Theory]
    [InlineData(SurfaceState.Clear)]
    [InlineData(SurfaceState.ModerationOnly)]
    public async Task The_pool_lists_everything_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        List<string> refs = RefsOf(await r.Controller().ListMedia(r.W.Project.Id, CancellationToken.None));
        refs.Should().Contain(Enum.GetValues<Source>().Select(r.RefOf));
    }

    [Fact]
    public async Task The_pool_on_a_failed_read_lists_no_output()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = r.W.FailingDb();
        List<string> refs = RefsOf(await r.Controller(failing).ListMedia(r.W.Project.Id, CancellationToken.None));
        refs.Should().NotContain(Withheld.Select(r.RefOf)).And.NotContain(r.RefOf(Source.LaterResult)).And.NotContain(r.RefOf(Source.LaterFile));
    }

    [Theory]
    [MemberData(nameof(WithheldSources))]
    public async Task The_filmstrip_and_derivative_list_of_an_earlier_output_are_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        ShouldBeRefused(await r.Controller().GetFilmstrip(r.W.Project.Id, r.RefOf(s), CancellationToken.None));
        ShouldBeRefused(await r.Controller().ListDerivatives(r.W.Project.Id, r.RefOf(s), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(KeptSources))]
    public async Task The_filmstrip_and_derivative_list_of_a_later_output_or_an_upload_are_served(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        (await r.Controller().GetFilmstrip(r.W.Project.Id, r.RefOf(s), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await r.Controller().ListDerivatives(r.W.Project.Id, r.RefOf(s), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [MemberData(nameof(AllServed))]
    public async Task The_filmstrip_and_derivative_list_are_served_when_the_project_keeps_nothing(Source s, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        (await r.Controller().GetFilmstrip(r.W.Project.Id, r.RefOf(s), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await r.Controller().ListDerivatives(r.W.Project.Id, r.RefOf(s), CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData(Source.LaterResult)]
    [InlineData(Source.LaterFile)]
    public async Task The_filmstrip_and_derivative_list_on_a_failed_read_are_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = r.W.FailingDb();
        ShouldBeRefused(await r.Controller(failing).GetFilmstrip(r.W.Project.Id, r.RefOf(s), CancellationToken.None));
        ShouldBeRefused(await r.Controller(failing).ListDerivatives(r.W.Project.Id, r.RefOf(s), CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(WithheldSources))]
    public async Task The_bytes_of_an_earlier_outputs_derivative_are_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Guid id = r.Derivative[s].Id;
        ShouldBeRefused(await r.Controller().GetDerivativeContent(r.W.Project.Id, id, CancellationToken.None));
        ShouldBeRefused(await r.Controller().GetFilmstripSheetContent(r.W.Project.Id, id, 0, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(KeptSources))]
    public async Task The_bytes_of_a_later_outputs_or_an_uploads_derivative_are_served(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Guid id = r.Derivative[s].Id;
        (await r.Controller().GetDerivativeContent(r.W.Project.Id, id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await r.Controller().GetFilmstripSheetContent(r.W.Project.Id, id, 0, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [MemberData(nameof(AllServed))]
    public async Task The_bytes_of_a_derivative_are_served_when_the_project_keeps_nothing(Source s, SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Guid id = r.Derivative[s].Id;
        (await r.Controller().GetDerivativeContent(r.W.Project.Id, id, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await r.Controller().GetFilmstripSheetContent(r.W.Project.Id, id, 0, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [InlineData(Source.LaterResult)]
    [InlineData(Source.LaterFile)]
    public async Task The_bytes_of_a_derivative_on_a_failed_read_are_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = r.W.FailingDb();
        Guid id = r.Derivative[s].Id;
        ShouldBeRefused(await r.Controller(failing).GetDerivativeContent(r.W.Project.Id, id, CancellationToken.None));
        ShouldBeRefused(await r.Controller(failing).GetFilmstripSheetContent(r.W.Project.Id, id, 0, CancellationToken.None));
    }

    private static bool IsRefused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    private static UpsertMediaAnnotationRequest Annotation() => new(["b-roll"], "note", default, true, null);

    [Theory]
    [MemberData(nameof(WithheldSources))]
    public async Task Annotating_an_earlier_output_is_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        IsRefused((await r.Controller().UpsertAnnotation(r.W.Project.Id, r.RefOf(s), Annotation(), CancellationToken.None)).Result).Should().BeTrue();
        r.W.Db.MediaAnnotations.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(KeptSources))]
    public async Task Annotating_a_later_output_or_an_upload_is_not_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        IsRefused((await r.Controller().UpsertAnnotation(r.W.Project.Id, r.RefOf(s), Annotation(), CancellationToken.None)).Result).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(WithheldSources))]
    public async Task Requesting_derivatives_of_an_earlier_output_is_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        IsRefused(await r.Controller().RequestDerivatives(r.W.Project.Id, new RequestMediaDerivativesRequest(r.RefOf(s)), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(KeptSources))]
    public async Task Requesting_derivatives_of_a_later_output_or_an_upload_is_not_refused(Source s)
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        IsRefused(await r.Controller().RequestDerivatives(r.W.Project.Id, new RequestMediaDerivativesRequest(r.RefOf(s)), CancellationToken.None)).Should().BeFalse();
    }
}
