#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Output links to a render, a file and a derivative: an output the project keeps back answers exactly as an ended
/// link does (status, headers and body bytes as written to the wire), and a later output or an upload is served.
/// </summary>
public sealed class OutputLinksSurfaceTests
{
    public enum Kind { Render, File, Derivative }

    private static readonly DateTime Marker = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();

    private sealed record Wire(int Status, string[] Headers, byte[] Body);

    private sealed class Rig : IDisposable
    {
        public OutputsWorld W { get; } = new();
        public ProjectFile EarlierFile { get; }
        public ProjectFile LaterFile { get; }
        public MediaDerivative EarlierDerivative { get; }
        public MediaDerivative LaterDerivative { get; }
        public WorkflowStepResult LaterRender { get; }

        public Rig()
        {
            // The earlier render: run and result both older than the marker.
            W.Execution.StartedAt = Marker.AddDays(-9);
            W.Execution.FirstStartedAt = Marker.AddDays(-9);
            W.Render.ExecutedAt = Marker.AddDays(-9);
            (MediaDerivative thumb, _) = W.AddRenderDerivatives();
            EarlierDerivative = thumb;

            WorkflowExecution later = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = W.Execution.WorkflowDefinitionId, ProjectId = W.Project.Id, Status = ExecutionStatus.Passed, StartedAt = Marker.AddDays(2), FirstStartedAt = Marker.AddDays(2) };
            string laterKey = $"projects/{W.Project.Id}/outputFiles/{later.Id}/second-cut.mp4";
            LaterRender = new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = later.Id, WorkflowStepId = W.Step.Id, Status = StepStatus.Completed, OutputStorageKey = laterKey, ExecutedAt = Marker.AddDays(2) };
            W.Db.WorkflowExecutions.Add(later);
            W.Db.WorkflowStepResults.Add(LaterRender);
            W.Storage.Objects[laterKey] = OutputsWorld.Bytes(500);
            LaterDerivative = new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                SourceKind = MediaSourceKind.StepResult,
                SourceStepResultId = LaterRender.Id,
                SourceStorageKey = laterKey,
                Kind = MediaDerivativeKind.Thumbnail,
                StorageKey = $"projects/{W.Project.Id}/agentFiles/media-derivatives/later/thumb.jpg",
                StorageBucket = OutputsWorld.Bucket,
                MimeType = "image/jpeg",
                CreatedAt = Marker.AddDays(2),
            };
            W.Db.MediaDerivatives.Add(LaterDerivative);
            W.Storage.Objects[LaterDerivative.StorageKey] = OutputsWorld.Bytes(30);

            EarlierFile = OutputFile("earlier-cut.mp4", Marker.AddDays(-9));
            LaterFile = OutputFile("later-cut.mp4", Marker.AddDays(2));
            W.Db.SaveChanges();
        }

        private ProjectFile OutputFile(string name, DateTime producedAt)
        {
            ProjectFile f = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                OriginalFileName = name,
                Category = "outputFiles",
                StorageKey = $"projects/{W.Project.Id}/outputFiles/{name}",
                StorageBucket = OutputsWorld.Bucket,
                MimeType = "video/mp4",
                SizeBytes = 100,
                UploadedAt = producedAt,
                OutputProducedAt = producedAt,
            };
            W.Db.ProjectFiles.Add(f);
            W.Storage.Objects[f.StorageKey] = OutputsWorld.Bytes(100);
            return f;
        }

        public Rig In(SurfaceState state)
        {
            if (state == SurfaceState.Held) W.Project.CsamOutputHoldAt = Marker;
            if (state == SurfaceState.ModerationOnly)
                W.Db.ProjectFiles.Add(new ProjectFile
                {
                    Id = Guid.NewGuid(),
                    ProjectId = W.Project.Id,
                    OriginalFileName = "flagged.mp4",
                    Category = "userFiles",
                    StorageKey = $"projects/{W.Project.Id}/userFiles/flagged.mp4",
                    StorageBucket = OutputsWorld.Bucket,
                    MimeType = "video/mp4",
                    SizeBytes = 8,
                    UploadedAt = Marker.AddDays(-6),
                    ModerationStatus = FileModerationStatus.Quarantined,
                });
            W.Db.SaveChanges();
            return this;
        }

        public OutputLinkTarget Earlier(Kind kind) => kind switch
        {
            Kind.Render => new(W.Project.Id, OutputLinkKind.StepResult, W.Render.Id, null),
            Kind.File => new(W.Project.Id, OutputLinkKind.ProjectFile, EarlierFile.Id, null),
            _ => new(W.Project.Id, OutputLinkKind.Derivative, EarlierDerivative.Id, null),
        };

        public OutputLinkTarget Later(Kind kind) => kind switch
        {
            Kind.Render => new(W.Project.Id, OutputLinkKind.StepResult, LaterRender.Id, null),
            Kind.File => new(W.Project.Id, OutputLinkKind.ProjectFile, LaterFile.Id, null),
            _ => new(W.Project.Id, OutputLinkKind.Derivative, LaterDerivative.Id, null),
        };

        public void Dispose() => W.Dispose();
    }

    private OutputLinkService Links(Rig r) => new(_tokens, new ConfigurationBuilder().Build(), r.W.Clock);

    private string Mint(Rig r, OutputLinkTarget target)
    {
        string url = Links(r).Create(target, r.W.UserId, null).Url;
        return url[(url.LastIndexOf('/') + 1)..];
    }

    private async Task<Wire> Fetch(Rig r, string token, InferenceApiDbContext? db = null)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = OutputsWorld.Bucket }).Build();
        DefaultHttpContext http = new();
        http.Response.Body = new MemoryStream();
        OutputLinksController controller = new(Links(r), new OutputLinkResolver(db ?? r.W.Db, r.W.Storage), r.W.S3.Object, config, r.W.Clock)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        IActionResult result = await controller.Get(token, CancellationToken.None);
        ActionContext ctx = new(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        http.RequestServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddLogging().AddSingleton<Microsoft.AspNetCore.Mvc.Infrastructure.IActionResultExecutor<FileStreamResult>, Microsoft.AspNetCore.Mvc.Infrastructure.FileStreamResultExecutor>()
            .BuildServiceProvider();
        await result.ExecuteResultAsync(ctx);
        return new Wire(
            http.Response.StatusCode,
            http.Response.Headers.Where(h => h.Key != "Date").Select(h => $"{h.Key}={h.Value}").Order().ToArray(),
            ((MemoryStream)http.Response.Body).ToArray());
    }

    private async Task AssertEndedLike(Rig r, OutputLinkTarget target, InferenceApiDbContext? db = null)
    {
        Wire ended = await Fetch(r, "not-a-live-link");
        Wire answer = await Fetch(r, Mint(r, target), db);
        answer.Status.Should().Be(ended.Status).And.Be(404);
        answer.Headers.Should().Equal(ended.Headers);
        answer.Body.Should().Equal(ended.Body).And.BeEmpty();
    }

    private async Task AssertServed(Rig r, OutputLinkTarget target)
    {
        Wire served = await Fetch(r, Mint(r, target));
        served.Status.Should().Be(200);
        served.Body.Should().NotBeEmpty();
    }

    public static IEnumerable<object[]> Kinds() => [[Kind.Render], [Kind.File], [Kind.Derivative]];

    public static IEnumerable<object[]> ServedByKind()
    {
        foreach (Kind k in Enum.GetValues<Kind>())
        {
            yield return [k, SurfaceState.Clear];
            yield return [k, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_link_to_an_earlier_output_answers_exactly_like_an_ended_link(Kind kind)
    {
        using Rig r = new Rig().In(SurfaceState.Held);
        await AssertEndedLike(r, r.Earlier(kind));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_link_to_a_later_output_is_served_while_the_project_keeps_earlier_ones(Kind kind)
    {
        using Rig r = new Rig().In(SurfaceState.Held);
        await AssertServed(r, r.Later(kind));
    }

    [Theory]
    [MemberData(nameof(ServedByKind))]
    public async Task A_link_to_the_earlier_output_is_served_when_the_project_keeps_nothing(Kind kind, SurfaceState state)
    {
        using Rig r = new Rig().In(state);
        await AssertServed(r, r.Earlier(kind));
    }

    [Fact]
    public async Task A_link_to_an_upload_is_served_while_the_project_keeps_earlier_outputs()
    {
        using Rig r = new Rig().In(SurfaceState.Held);
        await AssertServed(r, new OutputLinkTarget(r.W.Project.Id, OutputLinkKind.ProjectFile, r.W.File.Id, null));
    }

    [Theory]
    [InlineData(Kind.Render)]
    [InlineData(Kind.Derivative)]
    public async Task A_failed_read_answers_exactly_like_an_ended_link(Kind kind)
    {
        using Rig r = new Rig().In(SurfaceState.Clear);
        // The same store as the world's own context, with every other read working.
        DbContextOptions<InferenceApiDbContext> sameStore = new(r.W.Db.GetService<IDbContextOptions>().Extensions.ToDictionary(e => e.GetType()));
        using ProjectReadFailsContext failing = new(sameStore, typeof(ProjectFile));
        await AssertEndedLike(r, r.Later(kind), failing);
    }
}
