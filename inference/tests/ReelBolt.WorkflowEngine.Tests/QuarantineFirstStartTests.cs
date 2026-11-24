using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compositions;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Storage;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A run's first start is written once in the claim and every gate on earlier output judges the run by it: a Retry moves
/// StartedAt past the marker, and must not make an old run's output usable.
/// </summary>
public sealed class QuarantineFirstStartTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<Guid> SeedResultAsync(
        WorkflowEngineDbContext db, Guid projectId, DateTime? firstStartedAt, DateTime startedAt, DateTime executedAt,
        StepType stepType = StepType.VideoAnalyze, DateTime? supersededAt = null)
    {
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StartedAt = startedAt,
            FirstStartedAt = firstStartedAt
        };
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = stepType };
        WorkflowStepResult row = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowExecution = run,
            WorkflowStepId = step.Id,
            WorkflowStep = step,
            OutputStorageKey = $"projects/{projectId}/outputFiles/{Guid.NewGuid():D}/film.mp4",
            ArtifactStorageKey = $"projects/{projectId}/agentFiles/{Guid.NewGuid():D}/analysis.json",
            Status = StepStatus.Completed,
            ExecutedAt = executedAt,
            SupersededAt = supersededAt
        };
        db.Projects.Add(new Project { Id = projectId, Name = "p", ContentQuarantinedAt = Marker });
        db.WorkflowExecutions.Add(run);
        db.WorkflowStepResults.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    // ------------------------------------------------------------------ the claim

    private static async Task<WorkflowExecution> ClaimAsync(DateTime? firstStartedAt, DateTime? startedAt)
    {
        using WorkflowEngineDbContext db = NewDb();
        Guid projectId = Guid.NewGuid(), definitionId = Guid.NewGuid(), executionId = Guid.NewGuid();
        WorkflowDefinition definition = new() { Id = definitionId, Name = "t", ProjectId = projectId, Steps = [] };
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            WorkflowDefinition = definition,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            StartedAt = startedAt,
            FirstStartedAt = firstStartedAt
        });
        await db.SaveChangesAsync();

        WorkflowExecutorService service = new(
            new FakeScopeFactory(db), new NoOpEventPublisher(), NullLogger<WorkflowExecutorService>.Instance,
            Array.Empty<IStepExecutor>(), new RabbitMqHelper(new ConfigurationBuilder().Build()),
            Options.Create(new WorkflowHardeningOptions()), new ExecutionCancellationRegistry());
        await service.ExecuteAsync(executionId, "corr", CancellationToken.None);
        return await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == executionId);
    }

    [Fact]
    public async Task The_claim_stamps_the_first_start_of_a_run_that_has_none()
    {
        DateTime before = DateTime.UtcNow;
        WorkflowExecution run = await ClaimAsync(null, null);

        run.FirstStartedAt.Should().NotBeNull();
        run.FirstStartedAt!.Value.Should().BeOnOrAfter(before);
        run.FirstStartedAt.Should().Be(run.StartedAt);
    }

    [Fact]
    public async Task A_retried_run_keeps_its_first_start_while_StartedAt_moves()
    {
        DateTime first = Marker.AddDays(-2);
        WorkflowExecution run = await ClaimAsync(first, first);

        run.FirstStartedAt.Should().Be(first, "a Retry never touches the first start");
        run.StartedAt.Should().BeAfter(first);
    }

    // ------------------------------------------------------------------ the gates

    [Fact]
    public async Task A_step_after_the_marker_of_a_run_first_started_before_it_is_refused_but_a_new_run_is_allowed()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        // First started before the marker; Retry moved StartedAt after it; the step ran after it as well.
        Guid retried = await SeedResultAsync(db, projectId, Marker.AddDays(-1), Marker.AddHours(2), Marker.AddHours(3), StepType.Agent);
        Guid fresh = await SeedResultAsync(db, Guid.NewGuid(), Marker.AddHours(1), Marker.AddHours(1), Marker.AddHours(2), StepType.Agent);

        (await ProjectQuarantineState.ResultPredatesAsync(
            db, projectId, Marker.AddHours(3), Marker.AddDays(-1), CancellationToken.None)).Should().BeTrue();
        (await StepResultSourceResolver.ResolveAsync(db, projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: retried), allowImages: false, CancellationToken.None))
            .StorageKey.Should().BeNull();
        (string? key, _) = await TimelineRenderStepExecutor.ResolveStorageKeyAsync(
            db, projectId, new AssetRef(AssetRefKind.StepResult, StepResultId: retried), CancellationToken.None);
        key.Should().BeNull();

        Guid freshProject = await db.WorkflowStepResults.Where(r => r.Id == fresh).Select(r => r.WorkflowExecution.ProjectId).SingleAsync();
        (await StepResultSourceResolver.ResolveAsync(db, freshProject,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: fresh), allowImages: false, CancellationToken.None))
            .StorageKey.Should().NotBeNull("a run first started after the marker plus margin is allowed");
    }

    [Fact]
    public async Task A_run_with_no_first_start_is_refused()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        Guid id = await SeedResultAsync(db, projectId, null, Marker.AddHours(2), Marker.AddHours(3), StepType.Agent);

        (await StepResultSourceResolver.ResolveAsync(db, projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: id), allowImages: false, CancellationToken.None))
            .StorageKey.Should().BeNull();
    }

    [Fact]
    public async Task A_superseded_result_is_refused_once_the_project_is_marked()
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        Guid id = await SeedResultAsync(db, projectId, Marker.AddHours(2), Marker.AddHours(2), Marker.AddHours(3), StepType.Agent, supersededAt: Marker.AddHours(4));

        (await StepResultSourceResolver.ResolveAsync(db, projectId,
            new VideoSourceRef(VideoSourceKind.StepResult, StepResultId: id), allowImages: false, CancellationToken.None))
            .StorageKey.Should().BeNull();
    }

    // ------------------------------------------------------------------ timeline analysis artifacts

    [Theory]
    [InlineData(-86400, 7200, 10800, false)]  // first started before the marker, Retry moved StartedAt after it
    [InlineData(7200, 7200, 10800, true)]     // new run
    [InlineData(7200, 7200, -3600, false)]    // result itself predates the marker
    public async Task A_named_analysis_artifact_gets_the_result_gate(int firstOffset, int startedOffset, int executedOffset, bool allowed)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        Guid id = await SeedResultAsync(db, projectId, Marker.AddSeconds(firstOffset), Marker.AddSeconds(startedOffset), Marker.AddSeconds(executedOffset));

        var found = await TimelineRenderStepExecutor.ReadAnalysisArtifactsAsync(db, projectId, [id], CancellationToken.None);

        found.Select(f => f.Id).Should().BeEquivalentTo(allowed ? [id] : Array.Empty<Guid>());
    }

    // ------------------------------------------------------------------ registered outputs

    private static ProjectFile FileRow(
        Guid projectId, string category, string key, DateTime uploadedAt, string? sourceKind = null, DateTime? producedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "f.mp4",
            Category = category,
            StorageKey = key,
            SourceKind = sourceKind,
            UploadedAt = uploadedAt,
            OutputProducedAt = producedAt
        };

    [Fact]
    public async Task Output_rows_uploaded_at_or_before_the_marker_are_hidden_from_the_engine_but_uploads_are_not()
    {
        Guid p = Guid.NewGuid(), noMarker = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        db.Projects.Add(new Project { Id = p, Name = "marked", ContentQuarantinedAt = Marker });
        db.Projects.Add(new Project { Id = noMarker, Name = "plain" });
        DateTime old = Marker.AddDays(-1), within = Marker.AddMinutes(1), fresh = Marker.AddHours(1);
        Dictionary<string, ProjectFile> rows = new()
        {
            ["outputOld"] = FileRow(p, "outputFiles", $"projects/{p}/outputFiles/a/o.mp4", fresh, producedAt: old),
            ["outputWithinMargin"] = FileRow(p, "outputFiles", $"projects/{p}/outputFiles/b/o.mp4", fresh, producedAt: within),
            ["outputFresh"] = FileRow(p, "outputFiles", $"projects/{p}/outputFiles/c/o.mp4", fresh, producedAt: fresh),
            // An old output that a move turned into a plain-looking upload row: new upload time, new category and key.
            ["movedOldOutput"] = FileRow(p, "userFiles", $"projects/{p}/userFiles/moved.mp4", fresh, producedAt: old),
            ["copyOld"] = FileRow(p, "userFiles", $"projects/{p}/userFiles/copy.mp4", fresh, "output_copy", producedAt: old),
            ["copyFresh"] = FileRow(p, "userFiles", $"projects/{p}/userFiles/copy2.mp4", fresh, "output_copy", producedAt: fresh),
            ["takeOld"] = FileRow(p, "agentFiles", $"projects/{p}/agentFiles/generated/t.mp4", fresh, producedAt: old),
            ["agentFileOld"] = FileRow(p, "agentFiles", $"projects/{p}/agentFiles/notes.txt", old),
            ["uploadOld"] = FileRow(p, "userFiles", $"projects/{p}/userFiles/u.mp4", old),
            ["githubOld"] = FileRow(p, "userFiles", $"projects/{p}/userFiles/g.ts", old, "github"),
            ["unmarkedOutputOld"] = FileRow(noMarker, "outputFiles", $"projects/{noMarker}/outputFiles/a/o.mp4", old, producedAt: old)
        };
        db.ProjectFiles.AddRange(rows.Values);
        await db.SaveChangesAsync();

        HashSet<Guid> visible = [.. await db.ProjectFiles.Select(f => f.Id).ToListAsync()];

        string[] hidden = ["outputOld", "outputWithinMargin", "movedOldOutput", "copyOld", "takeOld"];
        foreach ((string name, ProjectFile row) in rows)
            visible.Contains(row.Id).Should().Be(!hidden.Contains(name), name);
    }

    private static (WorkflowExecution Run, WorkflowStep Step, WorkflowStepResult Result) RegistrationSeed(
        Guid projectId, DateTime firstStartedAt, DateTime executedAt)
    {
        WorkflowExecution run = new() { Id = Guid.NewGuid(), ProjectId = projectId, FirstStartedAt = firstStartedAt, StartedAt = firstStartedAt };
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, RegisterOutputAsProjectFile = true };
        WorkflowStepResult result = new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            ExecutedAt = executedAt,
            OutputStorageKey = $"projects/{projectId}/outputFiles/{run.Id}/film.mp4"
        };
        return (run, step, result);
    }

    [Theory]
    [InlineData(-3600, -3600, false)]
    [InlineData(-3600, 7200, false)]  // began before the marker, finished after it
    [InlineData(7200, -3600, false)]
    [InlineData(7200, 7200, true)]
    public async Task An_output_that_may_predate_the_marker_is_not_registered(int firstStartOffset, int executedOffset, bool registered)
    {
        Guid projectId = Guid.NewGuid();
        using WorkflowEngineDbContext db = NewDb();
        db.Projects.Add(new Project { Id = projectId, Name = "p", ContentQuarantinedAt = Marker });
        await db.SaveChangesAsync();
        Mock<IProjectFileWorkspace> workspace = new();
        workspace.Setup(w => w.RegisterStoredObjectAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(new ProjectWorkspaceFile(Guid.NewGuid(), projectId, "f", "f", "outputFiles", "k", "video/mp4", 1, DateTime.UtcNow, null));
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(workspace.Object);
        (WorkflowExecution run, WorkflowStep step, WorkflowStepResult result) =
            RegistrationSeed(projectId, Marker.AddSeconds(firstStartOffset), Marker.AddSeconds(executedOffset));

        await StepOutputRegistration.TryRegisterAsync(
            new SingleScopeFactory(services.BuildServiceProvider()), run, step, result, NullLogger.Instance, CancellationToken.None);

        workspace.Verify(w => w.RegisterStoredObjectAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()),
            registered ? Times.Once() : Times.Never());
    }

    private sealed class SingleScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(provider);

        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider => provider;
            public void Dispose() { }
        }
    }

    private sealed class NoOpEventPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PublishStepCompletedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            string stage, int? percentComplete, CancellationToken ct,
            int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            int turnIndex, int? totalTurns, string speaker, string speakerRole, string text,
            IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }
}
