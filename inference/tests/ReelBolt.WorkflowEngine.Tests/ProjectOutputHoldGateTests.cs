#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The derivation and reuse gates that read a project's output marker: the derivatives consumer, the project-file
/// source and the timeline analysis artifacts. Synthetic rows only.
/// </summary>
public class ProjectOutputHoldGateTests
{
    private static readonly DateTime Marker = DateTime.UtcNow.AddHours(-3);

    /// <summary>A factory whose first context is real and every later one is already disposed (a failed read).</summary>
    private sealed class FailingAfterFirstFactory(IDbContextFactory<WorkflowEngineDbContext> inner) : IDbContextFactory<WorkflowEngineDbContext>
    {
        private int _calls;

        public WorkflowEngineDbContext CreateDbContext()
        {
            WorkflowEngineDbContext db = inner.CreateDbContext();
            if (Interlocked.Increment(ref _calls) > 1)
                db.Dispose();
            return db;
        }
    }

    private static MediaDerivativeDispatchFixture NewFixture() =>
        MediaDerivativeDispatchFixture.Create(MediaDerivativeFfmpegFixture.NewDirectory());

    private static async Task SeedProjectAsync(IDbContextFactory<WorkflowEngineDbContext> factory, Guid projectId, DateTime? holdAt, bool moderationOnly = false)
    {
        await using WorkflowEngineDbContext db = factory.CreateDbContext();
        db.Projects.Add(new Project { Id = projectId, Name = "p", CsamOutputHoldAt = holdAt });
        if (moderationOnly)
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                StorageKey = $"projects/{projectId}/userFiles/other.png",
                OriginalFileName = "other.png",
                MimeType = "image/png",
                ModerationStatus = FileModerationStatus.Quarantined
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedStepResultAsync(
        IDbContextFactory<WorkflowEngineDbContext> factory, Guid projectId, string key, DateTime executedAt, DateTime? startedAt)
    {
        await using WorkflowEngineDbContext db = factory.CreateDbContext();
        Guid executionId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            WorkflowDefinitionId = Guid.NewGuid(),
            StartedAt = startedAt
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = resultId,
            WorkflowExecutionId = executionId,
            WorkflowStepId = Guid.NewGuid(),
            OutputStorageKey = key,
            ExecutedAt = executedAt
        });
        await db.SaveChangesAsync();
        return resultId;
    }

    private static MediaDerivativesRequested StepRequest(Guid projectId, Guid resultId, string key) => new(
        projectId,
        new MediaSourceRef(MediaSourceKind.StepResult, resultId),
        SourceProjectFileId: null,
        SourceStorageKey: key,
        SourceEtag: "\"e\"",
        MimeType: "video/mp4",
        Kinds: [MediaDerivativeKind.KeyframeIndex],
        RequestedBy: "render-output",
        RequestedAt: DateTime.UtcNow);

    private static MediaDerivativesConsumer Consumer(MediaDerivativeDispatchFixture f, IDbContextFactory<WorkflowEngineDbContext>? factory = null) =>
        new(factory ?? f.Db, f.Dispatcher, f.Sink, NullLogger<MediaDerivativesConsumer>.Instance);

    private static Task ConsumeAsync(MediaDerivativesConsumer consumer, MediaDerivativesRequested message)
    {
        var context = new Moq.Mock<MassTransit.ConsumeContext<MediaDerivativesRequested>>();
        context.SetupGet(c => c.Message).Returns(message);
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return consumer.Consume(context.Object);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeout.Token);
        }
    }

    // ---- the consumer -------------------------------------------------------------------------

    [Fact]
    public async Task A_step_result_made_before_the_marker_produces_nothing_and_reports_a_generic_failure()
    {
        using MediaDerivativeDispatchFixture f = NewFixture();
        Guid projectId = Guid.NewGuid();
        string key = $"projects/{projectId}/outputFiles/x/render.mp4";
        await SeedProjectAsync(f.Db, projectId, Marker);
        Guid id = await SeedStepResultAsync(f.Db, projectId, key, Marker.AddHours(-1), Marker.AddHours(-1));

        f.Workspace.DownloadGate = new TaskCompletionSource().Task;
        await ConsumeAsync(Consumer(f), StepRequest(projectId, id, key));
        await Task.Delay(500);

        f.Workspace.DownloadCount.Should().Be(0);
        f.ReadRows().Should().BeEmpty();
        f.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Failed);
    }

    [Fact]
    public async Task A_step_result_made_well_after_the_marker_is_dispatched()
    {
        using MediaDerivativeDispatchFixture f = NewFixture();
        Guid projectId = Guid.NewGuid();
        string key = $"projects/{projectId}/outputFiles/x/render.mp4";
        await SeedProjectAsync(f.Db, projectId, Marker);
        Guid id = await SeedStepResultAsync(f.Db, projectId, key, Marker.AddHours(2), Marker.AddHours(2));
        f.Workspace.DownloadGate = new TaskCompletionSource().Task;

        await ConsumeAsync(Consumer(f), StepRequest(projectId, id, key));

        await WaitForAsync(() => f.Workspace.DownloadCount == 1);
    }

    [Fact]
    public async Task A_project_with_only_a_moderation_flag_is_not_held()
    {
        using MediaDerivativeDispatchFixture f = NewFixture();
        Guid projectId = Guid.NewGuid();
        string key = $"projects/{projectId}/outputFiles/x/render.mp4";
        await SeedProjectAsync(f.Db, projectId, holdAt: null, moderationOnly: true);
        Guid id = await SeedStepResultAsync(f.Db, projectId, key, Marker.AddHours(-1), Marker.AddHours(-1));
        f.Workspace.DownloadGate = new TaskCompletionSource().Task;

        await ConsumeAsync(Consumer(f), StepRequest(projectId, id, key));

        await WaitForAsync(() => f.Workspace.DownloadCount == 1);
    }

    [Fact]
    public async Task A_failed_read_produces_nothing_and_does_not_throw()
    {
        using MediaDerivativeDispatchFixture f = NewFixture();
        Guid projectId = Guid.NewGuid();
        string key = $"projects/{projectId}/outputFiles/x/render.mp4";
        await SeedProjectAsync(f.Db, projectId, holdAt: null);
        Guid id = await SeedStepResultAsync(f.Db, projectId, key, DateTime.UtcNow, DateTime.UtcNow);

        // The first context (the staleness check) is real; the marker read then fails.
        f.Workspace.DownloadGate = new TaskCompletionSource().Task;
        Func<Task> act = () => ConsumeAsync(Consumer(f, new FailingAfterFirstFactory(f.Db)), StepRequest(projectId, id, key));

        await act.Should().NotThrowAsync("a throw would put the message back on the queue");
        await Task.Delay(500);
        f.Workspace.DownloadCount.Should().Be(0);
        f.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Failed);
    }

    [Fact]
    public async Task A_derived_output_file_made_before_the_marker_produces_nothing_and_an_upload_still_does()
    {
        using MediaDerivativeDispatchFixture f = NewFixture();
        Guid projectId = Guid.NewGuid();
        await SeedProjectAsync(f.Db, projectId, Marker);
        Guid derived = Guid.NewGuid();
        Guid upload = Guid.NewGuid();
        string derivedKey = $"projects/{projectId}/outputFiles/x/render.mp4";
        string uploadKey = $"projects/{projectId}/userFiles/clip.mp4";
        await using (WorkflowEngineDbContext db = f.Db.CreateDbContext())
        {
            db.ProjectFiles.Add(new ProjectFile { Id = derived, ProjectId = projectId, StorageKey = derivedKey, OriginalFileName = "r.mp4", MimeType = "video/mp4", UploadedAt = Marker.AddHours(-1), CsamStatus = CsamScanStatus.Clean });
            db.ProjectFiles.Add(new ProjectFile { Id = upload, ProjectId = projectId, StorageKey = uploadKey, OriginalFileName = "c.mp4", MimeType = "video/mp4", UploadedAt = Marker.AddHours(-1), CsamStatus = CsamScanStatus.Clean });
            await db.SaveChangesAsync();
        }

        f.Workspace.DownloadGate = new TaskCompletionSource().Task;
        await ConsumeAsync(Consumer(f), f.Request(projectId, derivedKey, "\"e\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: derived));
        await Task.Delay(500);
        f.Workspace.DownloadCount.Should().Be(0);

        await ConsumeAsync(Consumer(f), f.Request(projectId, uploadKey, "\"e\"", [MediaDerivativeKind.Proxy], sourceProjectFileId: upload));
        await WaitForAsync(() => f.Workspace.DownloadCount == 1);
    }

    // ---- the project-file source --------------------------------------------------------------

    [Theory]
    [InlineData(-1, true)]
    [InlineData(2, false)]
    public async Task A_derived_output_file_predating_the_marker_is_absent_and_a_later_one_present(int hoursFromMarker, bool absent)
    {
        SharedInMemoryDbFactory factory = new();
        Guid projectId = Guid.NewGuid();
        await SeedProjectAsync(factory, projectId, Marker);
        await using WorkflowEngineDbContext db = factory.CreateDbContext();

        Func<string, DateTime, DateTime?, bool> withheld = await ProjectFileWorkspace.BuildWithheldAsync(db, projectId, CancellationToken.None);

        withheld($"projects/{projectId}/outputFiles/x/r.mp4", Marker.AddHours(hoursFromMarker), null).Should().Be(absent);
        withheld($"projects/{projectId}/userFiles/u.mp4", Marker.AddHours(-1), null).Should().BeFalse("uploads are not judged here");
    }

    [Fact]
    public async Task A_derived_output_file_is_absent_when_the_markers_cannot_be_read()
    {
        SharedInMemoryDbFactory factory = new();
        WorkflowEngineDbContext db = factory.CreateDbContext();
        db.Dispose();

        Guid p = Guid.NewGuid();
        Func<string, DateTime, DateTime?, bool> withheld = await ProjectFileWorkspace.BuildWithheldAsync(db, p, CancellationToken.None);

        withheld($"projects/{p}/outputFiles/x/r.mp4", DateTime.UtcNow.AddDays(1), null).Should().BeTrue();
        withheld($"projects/{p}/userFiles/u.mp4", DateTime.UtcNow.AddDays(-1), null).Should().BeFalse("uploads are not judged here");
    }

    [Fact]
    public async Task The_other_quarantine_kind_does_not_leave_a_predating_derived_file_out()
    {
        SharedInMemoryDbFactory factory = new();
        Guid projectId = Guid.NewGuid();
        await SeedProjectAsync(factory, projectId, holdAt: null);
        await using (WorkflowEngineDbContext seed = factory.CreateDbContext())
        {
            Project p = await seed.Projects.SingleAsync(x => x.Id == projectId);
            p.ContentQuarantinedAt = Marker;
            await seed.SaveChangesAsync();
        }

        await using WorkflowEngineDbContext db = factory.CreateDbContext();
        Func<string, DateTime, DateTime?, bool> withheld = await ProjectFileWorkspace.BuildWithheldAsync(db, projectId, CancellationToken.None);

        withheld($"projects/{projectId}/outputFiles/x/r.mp4", Marker.AddHours(-1), null).Should().BeFalse("this layer applies only the output marker");
        withheld($"projects/{projectId}/outputFiles/x/r.mp4", Marker.AddHours(2), null).Should().BeFalse();
    }

    // ---- timeline analysis artifacts ----------------------------------------------------------

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public async Task An_analysis_artifact_predating_the_marker_is_left_out(int hoursFromMarker, int expected)
    {
        SharedInMemoryDbFactory factory = new();
        Guid projectId = Guid.NewGuid();
        await SeedProjectAsync(factory, projectId, holdAt: null);
        Guid stepId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        await using (WorkflowEngineDbContext seed = factory.CreateDbContext())
        {
            Project p = await seed.Projects.SingleAsync(x => x.Id == projectId);
            p.ContentQuarantinedAt = Marker;
            Guid executionId = Guid.NewGuid();
            seed.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                ProjectId = projectId,
                WorkflowDefinitionId = Guid.NewGuid(),
                StartedAt = Marker.AddHours(hoursFromMarker),
                FirstStartedAt = Marker.AddHours(hoursFromMarker)
            });
            seed.WorkflowSteps.Add(new WorkflowStep { Id = stepId, StepType = StepType.VideoAnalyze });
            seed.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                ArtifactStorageKey = $"projects/{projectId}/agentFiles/analysis.json",
                ExecutedAt = Marker.AddHours(hoursFromMarker)
            });
            await seed.SaveChangesAsync();
        }

        await using WorkflowEngineDbContext db = factory.CreateDbContext();
        IReadOnlyList<(Guid Id, string? ArtifactKey)> rows =
            await TimelineRenderStepExecutor.ReadAnalysisArtifactsAsync(db, projectId, [resultId], CancellationToken.None);

        rows.Should().HaveCount(expected);
    }

    [Fact]
    public async Task An_analysis_artifact_is_allowed_when_nothing_is_flagged()
    {
        SharedInMemoryDbFactory factory = new();
        Guid projectId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        Guid resultId = Guid.NewGuid();
        await using (WorkflowEngineDbContext seed = factory.CreateDbContext())
        {
            Guid executionId = Guid.NewGuid();
            seed.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId, WorkflowDefinitionId = Guid.NewGuid(), StartedAt = DateTime.UtcNow });
            seed.WorkflowSteps.Add(new WorkflowStep { Id = stepId, StepType = StepType.VideoAnalyze });
            seed.WorkflowStepResults.Add(new WorkflowStepResult { Id = resultId, WorkflowExecutionId = executionId, WorkflowStepId = stepId, ArtifactStorageKey = "k", ExecutedAt = DateTime.UtcNow });
            await seed.SaveChangesAsync();
        }

        await using WorkflowEngineDbContext db = factory.CreateDbContext();
        // Sanity: with nothing flagged the artifact is allowed.
        (await TimelineRenderStepExecutor.ReadAnalysisArtifactsAsync(db, projectId, [resultId], CancellationToken.None))
            .Should().HaveCount(1);
    }
}
