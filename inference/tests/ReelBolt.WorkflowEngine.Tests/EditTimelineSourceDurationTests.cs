#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// R01.6: the server fills the validator's source-duration map from the ingest probe (or the
/// VideoAnalyze artifact for a step result), refuses a clip that runs past its source with a plain
/// message naming the clip, and fails open when the duration is unknown.
/// </summary>
public class EditTimelineSourceDurationTests
{
    private const string FileKey = "projects/p/uploads/interview.mp4";

    [Fact]
    public async Task A_clip_past_the_probed_source_end_is_refused_naming_the_clip_never_a_guid()
    {
        using Fixture f = await Fixture.CreateAsync(probeSeconds: 4);

        // 4 s at 30 fps = 120 frames; the clip wants 300.
        ObjectResult refused = (await f.PatchAsync(f.Document(recordOut: 300))).Result.Should().BeOfType<BadRequestObjectResult>().Subject;

        List<EditTimelineViolation> violations = refused.Value.Should().BeOfType<List<EditTimelineViolation>>().Subject;
        violations.Should().ContainSingle();
        violations[0].Path.Should().Be("tracks[0].items[0].sourceIn");
        violations[0].Message.Should().Contain("\"Interview\"").And.Contain("4 s").And.Contain("10 s");
        violations[0].Message.Should().NotMatchRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", "a person never sees an id");
        (await f.Db.EditTimelines.AsNoTracking().SingleAsync()).Revision.Should().Be(1, "a refused document is never saved");
    }

    [Fact]
    public async Task A_clip_inside_the_probed_source_is_saved()
    {
        using Fixture f = await Fixture.CreateAsync(probeSeconds: 12);

        (await f.PatchAsync(f.Document(recordOut: 300))).Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task An_unknown_source_duration_fails_open()
    {
        using Fixture f = await Fixture.CreateAsync(probeSeconds: null);

        (await f.PatchAsync(f.Document(recordOut: 300_000))).Result.Should().BeOfType<OkObjectResult>(
            "no probe means nothing is proven, so nothing is refused");
    }

    [Theory]
    [InlineData(0.5, 100, true)]   // slow motion consumes 50 of the 120 source frames
    [InlineData(2.0, 100, false)]  // 200 frames needed > 120
    [InlineData(2.0, 50, true)]    // 100 frames needed <= 120
    public async Task The_playback_rate_scales_the_source_a_clip_consumes(double rate, long recordOut, bool accepted)
    {
        using Fixture f = await Fixture.CreateAsync(probeSeconds: 4);
        ActionResult<EditTimelineResponse> result = await f.PatchAsync(f.Document(recordOut: recordOut, rate: rate));

        (result.Result is OkObjectResult).Should().Be(accepted);
    }

    [Fact]
    public async Task A_step_result_source_falls_back_to_the_analysis_artifact_and_the_artifact_is_read_once()
    {
        using Fixture f = await Fixture.CreateAsync(probeSeconds: null);
        Guid projectId = f.ProjectId;
        WorkflowExecution execution = new() { Id = Guid.NewGuid(), ProjectId = projectId, WorkflowDefinitionId = Guid.NewGuid(), Status = ExecutionStatus.Passed };
        WorkflowStep analyzeStep = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = execution.WorkflowDefinitionId, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze };
        string artifactKey = $"projects/{projectId:D}/agentFiles/video-analysis/{execution.Id:D}/step-1-analysis.json";
        WorkflowStepResult analysis = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = analyzeStep.Id,
            Status = StepStatus.Completed,
            ArtifactStorageKey = artifactKey,
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        WorkflowStepResult source = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = analyzeStep.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = "projects/p/outputFiles/take.mp4",
            ExecutedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow
        };
        f.Db.WorkflowExecutions.Add(execution);
        f.Db.WorkflowSteps.Add(analyzeStep);
        f.Db.WorkflowStepResults.AddRange(analysis, source);
        await f.Db.SaveChangesAsync();

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.DownloadAsync(projectId, artifactKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(
                """{"sources":[{"storageKey":"projects/p/outputFiles/take.mp4","media":{"durationSec":4,"fpsNum":30,"fpsDen":1,"width":1920,"height":1080}}]}""")));
        SourceDurationResolver resolver = new(f.Db, storage.Object, NullLogger.Instance);
        AssetRef reference = new(AssetRefKind.StepResult, StepResultId: source.Id);

        for (int i = 0; i < 2; i++)
        {
            Dictionary<AssetRef, long> durations = await resolver.ResolveAsync(
                projectId, [reference], new Dictionary<Guid, string>(), new Dictionary<Guid, string> { [source.Id] = source.OutputStorageKey! }, 30, default);
            durations[reference].Should().Be(120);
        }

        storage.Verify(s => s.DownloadAsync(projectId, artifactKey, It.IsAny<CancellationToken>()), Times.Once, "one artifact read per request");
    }

    private sealed class Fixture : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid TimelineId { get; init; }
        public Guid FileId { get; init; }

        public static async Task<Fixture> CreateAsync(double? probeSeconds)
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"srcdur-{Guid.NewGuid()}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            Guid ownerId = Guid.NewGuid();
            Project project = new()
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = TestTenancy.OrgOf(ownerId),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = project.Id, StorageKey = FileKey, OriginalFileName = "interview.mp4" };
            db.Projects.Add(project);
            db.ProjectFiles.Add(file);
            if (probeSeconds is { } seconds)
            {
                db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    SourceProjectFileId = file.Id,
                    SourceStorageKey = FileKey,
                    Kind = MediaDerivativeKind.Proxy,
                    StorageKey = "d/proxy.mp4",
                    CacheKey = "k",
                    SourceProbeJson = new MediaSourceProbe(seconds, 30, 1, 1920, 1080, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
                });
            }

            Guid timelineId = Guid.NewGuid();
            Fixture fixture = new() { Db = db, ProjectId = project.Id, OwnerId = ownerId, TimelineId = timelineId, FileId = file.Id };
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = "Cut",
                Revision = 1,
                DocumentJson = JsonSerializer.Serialize(fixture.Document(recordOut: 30), EditTimelineJson.Options),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public EditTimeline Document(long recordOut, double rate = 1.0) => new(
            id: TimelineId,
            projectId: ProjectId,
            name: "Cut",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                    [new MediaClip(Guid.NewGuid(), "Interview", 0, recordOut, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: FileId), PlaybackRate: rate)])
            ],
            markers: []);

        public Task<ActionResult<EditTimelineResponse>> PatchAsync(EditTimeline document)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            EditTimelinesController controller = new(
                Db, user.Object, TestTenancy.Access(Db, user.Object), new Mock<IFileStorageService>().Object,
                new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);
            return controller.PatchEditTimeline(ProjectId, TimelineId, new PatchEditTimelineRequest(1, null, document), default);
        }

        public void Dispose() => Db.Dispose();
    }
}
