using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A VideoGenerate step that runs again (ReviewLoop loop-back, retry, re-run) reuses an identical,
/// already-delivered clip instead of paying again, unless the user (or the step config on a
/// loop-back) asked for fresh clips. Fake clients only: no provider is ever reachable.
/// </summary>
public class VideoGenerateReuseTests
{
    private static readonly Guid ProviderId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private const string ReusedKey = "projects/p/agentFiles/generated/old/step-1-v1.mp4";

    private static string Cfg(string extra = "") =>
        "{\"maxSpendCredits\": 100.0, \"takes\": 1, \"maxClips\": 1, \"inlinePrompt\": \"Create a video\", \"timeoutSeconds\": 60, \"pollIntervalSeconds\": 1" + extra + "}";

    private static async Task<ExternalGenerationJob> SeedSucceeded(
        SharedInMemoryDbFactory factory, WorkflowExecution execution, Guid executionId, string? key = ReusedKey)
    {
        var job = new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepResultId = Guid.NewGuid(),
            ProjectId = execution.ProjectId,
            ClipId = "clip-1",
            RequestHash = VideoGenerateStepExecutorTests.DefaultHash(1),
            ProviderId = ProviderId,
            RemoteJobId = "old-remote",
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 0.48m,
            ActualCost = 0.48m,
            StorageKey = key,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static Mock<IProjectFileWorkspace> WorkspaceWithObject(bool exists)
    {
        var ws = new Mock<IProjectFileWorkspace>();
        ws.Setup(w => w.ArtifactExistsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(exists);
        ws.Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), "agentFiles"))
            .ReturnsAsync("stored/manifest.json");
        return ws;
    }

    private async Task<(ReelBolt.WorkflowEngine.Execution.StepExecutionResult Result, Mock<IVideoGenerationClient> Client, SharedInMemoryDbFactory Factory)> Run(
        string config, bool objectExists = true, bool regenerateFlag = false, int iteration = 0,
        bool seedSameExecution = true, bool seedSucceeded = true, Action<ProjectFile>? seedFile = null,
        DateTime? seedMarker = null, bool failStateRead = false,
        Guid? priorRunId = null, Action<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext, Guid>? seedProject = null)
    {
        var failure = new FailProjectFileReads { Armed = false };
        // Only the failing-read case gets an interceptor: each one costs EF a service provider, and past 20 of those EF throws.
        var factory = new SharedInMemoryDbFactory(interceptor: failStateRead ? failure : null);
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        if (seedMarker is { } marker)
        {
            await using var projectDb = factory.CreateDbContext();
            projectDb.Projects.Add(new Project { Id = execution.ProjectId, Name = "p", ContentQuarantinedAt = marker });
            await projectDb.SaveChangesAsync();
        }

        if (seedFile is not null)
        {
            await using var fileDb = factory.CreateDbContext();
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = execution.ProjectId,
                OriginalFileName = "frame.png",
                StorageKey = "projects/p/userFiles/frame.png",
                MimeType = "image/png",
                UploadedAt = DateTime.UtcNow
            };
            seedFile(file);
            fileDb.ProjectFiles.Add(file);
            await fileDb.SaveChangesAsync();
        }

        if (seedProject is not null)
        {
            await using var projectDb = factory.CreateDbContext();
            seedProject(projectDb, execution.ProjectId);
            await projectDb.SaveChangesAsync();
        }

        if (seedSucceeded)
        {
            await SeedSucceeded(factory, execution, seedSameExecution ? execution.Id : priorRunId ?? Guid.NewGuid());
        }

        failure.Armed = failStateRead;
        var flags = new ExecutionRunFlags();
        flags.SetRegenerateVideoClips(execution.Id, regenerateFlag);

        var client = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobHandle("fresh-remote"));
        // A fresh take that "fails at the vendor" keeps the test free of ffmpeg/download wiring.
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Failed, null, "fake vendor"));

        var gate = new Mock<IVideoGenerationBudgetGate>();
        var real = VideoGenerateStepExecutorTests.RealGate(factory);
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns((VideoGenerationReservationRequest r, CancellationToken ct) => real.ReserveAsync(r, ct));
        _gate = gate;

        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            gate.Object, client.Object, factory, config, execution,
            workspace: WorkspaceWithObject(objectExists).Object,
            providerId: ProviderId, runFlags: flags, iterationCount: iteration);
        var result = await executor.ExecuteAsync(context);
        return (result, client, factory);
    }

    private Mock<IVideoGenerationBudgetGate>? _gate;

    [Fact]
    public async Task DefaultReuse_ServesSucceededClip_WithoutReservingOrSubmitting()
    {
        var (result, client, factory) = await Run(Cfg());

        result.Status.Should().Be(StepStatus.Completed);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _gate!.Verify(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        var clip = JsonNode.Parse(result.Output)!["clips"]![0]!;
        clip["reused"]!.GetValue<bool>().Should().BeTrue();
        clip["status"]!.GetValue<string>().Should().Be("succeeded");
        clip["cost"]!.GetValue<decimal>().Should().Be(0m);
        JsonNode.Parse(result.Output)!["meta"]!["clipsReused"]!.GetValue<int>().Should().Be(1);
        factory.CreateDbContext().ExternalGenerationJobs.Count().Should().Be(1, "reuse writes no ledger row");
        result.OutputStorageKey.Should().Be(ReusedKey);
    }

    [Fact]
    public async Task ReuseAcrossExecutions_ServesPriorExecutionsClip()
    {
        var (result, client, _) = await Run(Cfg(), seedSameExecution: false);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        result.OutputStorageKey.Should().Be(ReusedKey);
    }

    [Fact]
    public async Task NoClipIsReused_WhenAFileOfTheProjectIsQuarantined()
    {
        foreach (Action<ProjectFile> quarantine in new Action<ProjectFile>[]
        {
            f => f.CsamStatus = CsamScanStatus.Quarantined,
            f => f.ModerationStatus = FileModerationStatus.Quarantined
        })
        {
            var (_, client, _) = await Run(Cfg(), seedFile: quarantine);
            client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task NoClipIsReused_WhenTheQuarantinedRowIsGoneButTheProjectKeepsItsMarker()
    {
        var (_, client, _) = await Run(Cfg(), seedMarker: DateTime.UtcNow);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoClipIsReused_WhenTheProjectsFileStateCannotBeRead()
    {
        var (_, client, _) = await Run(Cfg(), failStateRead: true);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnlyClipsOfARunThatBeganAfterTheMarkerAreReused()
    {
        foreach ((int runOffsetHours, bool reused) in new[] { (-1, false), (1, true) })
        {
            DateTime marker = DateTime.UtcNow.AddHours(-3);
            Guid priorRun = Guid.NewGuid();
            var (_, client, _) = await Run(Cfg(), seedSameExecution: false, priorRunId: priorRun, seedProject: (db, projectId) =>
            {
                db.Projects.Add(new Project { Id = projectId, Name = "p", ContentQuarantinedAt = marker });
                db.WorkflowExecutions.Add(new WorkflowExecution { Id = priorRun, ProjectId = projectId, Status = ExecutionStatus.Passed, FirstStartedAt = marker.AddHours(runOffsetHours) });
            });
            client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), reused ? Times.Never() : Times.Once());
        }
    }

    [Fact]
    public async Task AnOrdinaryFileDoesNotStopReuse()
    {
        var (result, client, _) = await Run(Cfg(), seedFile: _ => { });
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        result.OutputStorageKey.Should().Be(ReusedKey);
    }

    [Fact]
    public async Task RegenerateFlag_ForcesFreshSubmit()
    {
        var (_, client, factory) = await Run(Cfg(), regenerateFlag: true);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        factory.CreateDbContext().ExternalGenerationJobs.Count().Should().Be(2, "a fresh take reserves its own row");
    }

    [Fact]
    public async Task LoopBackRegenerate_OnlyAppliesOnLoopBackIterations()
    {
        var cfg = Cfg(", \"loopBackBehavior\": \"Regenerate\"");

        var (_, firstClient, _) = await Run(cfg, iteration: 0);
        firstClient.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var (_, loopClient, _) = await Run(cfg, iteration: 1);
        loopClient.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoopBackDefault_ReusesOnIteration()
    {
        var (_, client, _) = await Run(Cfg(), iteration: 2);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingObject_IsNotReused()
    {
        var (_, client, _) = await Run(Cfg(), objectExists: false);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoPriorSuccess_GeneratesFresh()
    {
        var (_, client, _) = await Run(Cfg(), seedSucceeded: false);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
