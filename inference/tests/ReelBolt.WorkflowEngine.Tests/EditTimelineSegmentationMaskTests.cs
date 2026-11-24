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
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Segmentation masks (T-023): PATCH fills the validator's SegmentationTrackIds from the project's own
/// VideoAnalyze artifact. A foreign, missing, non-VideoAnalyze or wrong-prefix result is the same 400.
/// </summary>
public class EditTimelineSegmentationMaskTests
{
    [Fact]
    public async Task A_mask_naming_tracks_of_a_same_project_video_analysis_is_accepted()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid result = await f.AddStepResultAsync(f.ProjectId, StepType.VideoAnalyze, f.Prefix(f.ProjectId) + "run/analysis.json");

        ActionResult<EditTimelineResponse> response = await f.PatchAsync(result, "g1", "g2");

        response.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_foreign_projects_analysis_a_missing_one_and_a_wrong_step_type_give_the_same_400()
    {
        using Fixture f = await Fixture.CreateAsync();
        Guid foreign = await f.AddStepResultAsync(f.OtherProjectId, StepType.VideoAnalyze, f.Prefix(f.OtherProjectId) + "run/analysis.json");
        Guid wrongType = await f.AddStepResultAsync(f.ProjectId, StepType.Agent, f.Prefix(f.ProjectId) + "run/analysis.json");
        Guid outsidePrefix = await f.AddStepResultAsync(f.ProjectId, StepType.VideoAnalyze, f.Prefix(f.OtherProjectId) + "run/analysis.json");
        Guid unknownTrack = await f.AddStepResultAsync(f.ProjectId, StepType.VideoAnalyze, f.Prefix(f.ProjectId) + "run/analysis.json");

        List<string> messages = [];
        foreach (Guid id in new[] { foreign, Guid.NewGuid(), wrongType, outsidePrefix })
        {
            ActionResult<EditTimelineResponse> response = await f.PatchAsync(id, "g1");
            messages.Add(JsonSerializer.Serialize(response.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Replace(id.ToString(), "ID", StringComparison.Ordinal));
        }

        messages.Distinct().Should().HaveCount(1);
        messages[0].Should().Contain("mask.analysisStepResultId");

        // An unknown track on a good analysis shares that one violation.
        ActionResult<EditTimelineResponse> unknown = await f.PatchAsync(unknownTrack, "g9");
        JsonSerializer.Serialize(unknown.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain("mask.analysisStepResultId");
    }

    private sealed class Fixture : IDisposable
    {
        public required InferenceApiDbContext Db { get; init; }
        public Guid ProjectId { get; init; }
        public Guid OtherProjectId { get; init; }
        public Guid OwnerId { get; init; }
        public Guid TimelineId { get; init; }
        private Guid _definitionId;
        private readonly Guid _fileId = Guid.NewGuid();

        public string Prefix(Guid projectId) => $"projects/{projectId}/agentFiles/video-analysis/";

        public static async Task<Fixture> CreateAsync()
        {
            InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            Guid ownerId = Guid.NewGuid();
            Project Make() => new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = TestTenancy.OrgOf(ownerId), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Project project = Make();
            Project other = Make();
            db.Projects.AddRange(project, other);
            Guid timelineId = Guid.NewGuid();
            Fixture fixture = new() { Db = db, ProjectId = project.Id, OtherProjectId = other.Id, OwnerId = ownerId, TimelineId = timelineId };
            db.ProjectFiles.Add(new ProjectFile { Id = fixture._fileId, ProjectId = project.Id, StorageKey = "projects/p/a.mp4", OriginalFileName = "a.mp4" });
            db.EditTimelines.Add(new EditTimelineEntity
            {
                Id = timelineId,
                ProjectId = project.Id,
                Name = "Cut",
                Revision = 1,
                DocumentJson = JsonSerializer.Serialize(fixture.Document(null), EditTimelineJson.Options),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return fixture;
        }

        public async Task<Guid> AddStepResultAsync(Guid projectId, StepType type, string artifactKey)
        {
            if (_definitionId == Guid.Empty)
            {
                _definitionId = Guid.NewGuid();
                Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = _definitionId, ProjectId = ProjectId, Name = "w", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            }

            Guid executionId = Guid.NewGuid(), stepId = Guid.NewGuid(), resultId = Guid.NewGuid();
            Db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, WorkflowDefinitionId = _definitionId, ProjectId = projectId, OrganizationId = TestTenancy.OrgOf(OwnerId) });
            Db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = _definitionId, StepOrder = 1, StepType = type });
            Db.WorkflowStepResults.Add(new WorkflowStepResult { Id = resultId, WorkflowExecutionId = executionId, WorkflowStepId = stepId, ArtifactStorageKey = artifactKey });
            await Db.SaveChangesAsync();
            return resultId;
        }

        public EditTimeline Document(ShapeMask? mask) => new(
            id: TimelineId,
            projectId: ProjectId,
            name: "Cut",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                    [new MediaClip(Guid.NewGuid(), "Clip", 0, 30, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: _fileId), Mask: mask)])
            ],
            markers: []);

        public Task<ActionResult<EditTimelineResponse>> PatchAsync(Guid analysisStepResultId, params string[] trackIds)
        {
            Mock<ICurrentUser> user = new();
            user.SetupGet(u => u.UserId).Returns(OwnerId);
            user.SetupGet(u => u.Email).Returns("t@example.com");
            user.SetupGet(u => u.IsAuthenticated).Returns(true);
            user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(OwnerId));
            user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (Stream)new MemoryStream(Encoding.UTF8.GetBytes($$"""{"sources":[{"sourceIndex":0,"storageKey":"x","projectFileId":"{{_fileId}}"}],"segmentationTracks":[{"id":"g1","sourceIndex":0,"frameCount":3000,"fps":30},{"id":"g2","sourceIndex":0,"frameCount":3000,"fps":30},{"id":"g3","sourceIndex":0,"frameCount":3000,"fps":30}]}""")));
            EditTimelinesController controller = new(
                Db, user.Object, TestTenancy.Access(Db, user.Object), storage.Object,
                new Mock<ReelBolt.Inference.Api.Services.Workflows.IWorkflowExecutionService>().Object);
            ShapeMask mask = new(ShapeMask.SegmentationKind, AnalysisStepResultId: analysisStepResultId, TrackIds: trackIds, FeatherPx: 2);
            return controller.PatchEditTimeline(ProjectId, TimelineId, new PatchEditTimelineRequest(1, null, Document(mask)), default);
        }

        public void Dispose() => Db.Dispose();
    }
}
