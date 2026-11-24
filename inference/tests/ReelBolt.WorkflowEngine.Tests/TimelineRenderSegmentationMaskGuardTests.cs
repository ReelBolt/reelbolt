using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Segmentation masks (T-023): the render re-validation sees the same SegmentationTrackIds map as the API
/// (one shared lookup), and a mask that cannot be verified (T-015) refuses the render instead of drawing unmasked. A foreign or missing analysis stays a DOCUMENT_INVALID with the single mask violation.
/// </summary>
public class TimelineRenderSegmentationMaskGuardTests
{
    private static readonly Guid ProjectId = Guid.Parse("c4c4c4c4-0000-4000-8000-0000000000c4");
    private static readonly Guid OtherProjectId = Guid.Parse("c5c5c5c5-0000-4000-8000-0000000000c5");
    private static readonly Guid OrgId = Guid.Parse("0a0a0a0a-0000-4000-8000-00000000000a");
    private static readonly Guid FileId = Guid.Parse("f1f1f1f1-0000-4000-8000-0000000000f1");

    [Fact]
    public async Task A_segmentation_mask_whose_track_cannot_be_verified_is_refused_never_rendered_unmasked()
    {
        StepExecutionResult result = await RunAsync((db, id) => Seed(db, id, ProjectId));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith("DOCUMENT_INVALID").And.Contain("cannot be verified");
    }

    [Fact]
    public async Task A_foreign_or_missing_analysis_is_DOCUMENT_INVALID_with_the_same_violation()
    {
        StepExecutionResult foreign = await RunAsync((db, id) => Seed(db, id, OtherProjectId));
        StepExecutionResult missing = await RunAsync((_, _) => { });

        foreach (StepExecutionResult r in new[] { foreign, missing })
        {
            r.Status.Should().Be(StepStatus.Failed);
            r.ErrorDetails.Should().StartWith("DOCUMENT_INVALID").And.Contain("mask.analysisStepResultId");
        }
    }

    private static string ArtifactKey(Guid project) => $"projects/{project}/agentFiles/video-analysis/run/analysis.json";

    private static void Seed(WorkflowEngineDbContext db, Guid resultId, Guid resultProject)
    {
        Guid defId = Guid.NewGuid(), execId = Guid.NewGuid(), stepId = Guid.NewGuid();
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = defId, ProjectId = resultProject, Name = "a", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = execId, WorkflowDefinitionId = defId, ProjectId = resultProject, OrganizationId = OrgId });
        db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = defId, StepOrder = 1, StepType = StepType.VideoAnalyze });
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = resultId, WorkflowExecutionId = execId, WorkflowStepId = stepId, ArtifactStorageKey = ArtifactKey(resultProject) });
    }

    private static async Task<StepExecutionResult> RunAsync(Action<WorkflowEngineDbContext, Guid> seed)
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid analysisId = Guid.NewGuid(), timelineId = Guid.NewGuid(), workflowId = Guid.NewGuid();
        ShapeMask mask = new(ShapeMask.SegmentationKind, AnalysisStepResultId: analysisId, TrackIds: ["g1"], FeatherPx: 2);
        MediaClip clip = new(Guid.NewGuid(), "clip", 0, 60, new AssetRef(AssetRefKind.ProjectFile, FileId, null), 0, 1.0, true, null, null, Mask: mask);
        Track video = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip]);
        EditTimeline document = new(timelineId, ProjectId, "doc", new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0), [video], [], null, null);
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = ProjectId, Name = "w", Kind = WorkflowDefinitionKind.TimelineRender, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.ProjectFiles.Add(new ProjectFile { Id = FileId, ProjectId = ProjectId, OriginalFileName = "a.mp4", StorageKey = $"projects/{ProjectId:D}/files/a.mp4", MimeType = "video/mp4", SizeBytes = 100 });
        db.EditTimelines.Add(new EditTimelineEntity { Id = timelineId, ProjectId = ProjectId, Name = "doc", DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options), Revision = 1 });
        seed(db, analysisId);
        await db.SaveChangesAsync();

        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(WorkflowEngineDbContext))).Returns(db);
        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { Tier = "test", MaxOutputHeight = 2160 }));
        provider.Setup(p => p.GetService(typeof(IOrganizationEntitlements))).Returns(entitlements.Object);
        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IProjectFileWorkspace> workspace = new();
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, string dest, long _, CancellationToken _) =>
                File.WriteAllTextAsync(dest, """{"segmentationTracks":[{"id":"g1"},{"id":"g2"}]}"""));

        TimelineRenderStepExecutor executor = new(
            new Mock<IVideoToolRunner>().Object, new Mock<IMediaProbe>().Object, workspace.Object,
            Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(Path.GetTempPath(), "tr-seg-" + Guid.NewGuid().ToString("N")) }),
            scopeFactory.Object, NullLogger<TimelineRenderStepExecutor>.Instance);

        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(new TimelineRenderStepConfig(timelineId, Preset: TimelineRenderPreset.Hd1080p16x9, RegisterProjectFile: false), TimelineRenderStepExecutor.ConfigJsonOptions)
        };
        return await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, ProjectId = ProjectId, OrganizationId = OrgId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        });
    }
}
