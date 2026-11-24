using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests.Workflows;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.ExecutionResumeTests;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Product decision "VideoGenerate orphan": on a new attempt of a run (a manual
/// Resume or a Retry), a take an EARLIER attempt of the same step left <c>Reserved</c> with no
/// <c>RemoteJobId</c> is marked <see cref="ExternalGenerationJobStatus.Abandoned"/>, its budget reservation
/// released, and the take is reserved again and submitted under a fresh idempotency key (the new row id).
/// Inside one attempt (in-step retries) the old <c>RESERVATION_ORPHANED</c> rule is unchanged; a take with
/// a <c>RemoteJobId</c> is re-attached (polled), never re-submitted. No vendor is ever reachable: every
/// client here is a mock.
/// </summary>
public class VideoGenerateOrphanAbandonTests
{
    private static readonly decimal TakeEstimate = CreditCurrency.CreditsFromUsd(0.48m);

    private const string Cfg =
        """{"maxSpendCredits": 100000.0, "takes": 1, "maxClips": 1, "inlinePrompt": "Create a video", "timeoutSeconds": 60, "pollIntervalSeconds": 1}""";

    private static readonly string Hash1 = VideoGenerateStepExecutorTests.DefaultHash(1);

    // ------------------------------------------------------------------ seeding (gate and executor level)

    /// <summary>The rows the gate reads to decide "earlier attempt": the run, its step, an earlier result and this attempt's result.</summary>
    private sealed record Attempts(Guid ExecutionId, Guid ProjectId, Guid StepId, Guid EarlierResultId, Guid CurrentResultId);

    private static async Task<Attempts> SeedAttemptsAsync(
        SharedInMemoryDbFactory factory,
        int resumeCount,
        StepStatus earlierStatus = StepStatus.Failed,
        bool earlierSuperseded = false,
        Guid? executionId = null,
        Guid? projectId = null,
        Guid? stepId = null,
        Guid? currentResultId = null)
    {
        Attempts a = new(
            executionId ?? Guid.NewGuid(), projectId ?? Guid.NewGuid(), stepId ?? Guid.NewGuid(), Guid.NewGuid(), currentResultId ?? Guid.NewGuid());
        await using var db = factory.CreateDbContext();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = a.ExecutionId,
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = a.ProjectId,
            Status = ExecutionStatus.Running,
            ResumeCount = resumeCount
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = a.StepId,
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate
        });
        DateTime earlier = DateTime.UtcNow.AddMinutes(-30);
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = a.EarlierResultId,
            WorkflowExecutionId = a.ExecutionId,
            WorkflowStepId = a.StepId,
            Status = earlierStatus,
            ExecutedAt = earlier,
            SupersededAt = earlierSuperseded ? DateTime.UtcNow.AddMinutes(-1) : null
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = a.CurrentResultId,
            WorkflowExecutionId = a.ExecutionId,
            WorkflowStepId = a.StepId,
            Status = StepStatus.Running
        });
        await db.SaveChangesAsync();
        return a;
    }

    private static async Task<Guid> SeedLedgerRowAsync(
        SharedInMemoryDbFactory factory, Attempts a, Guid ownerResultId, string? remoteJobId = null,
        ExternalGenerationJobStatus status = ExternalGenerationJobStatus.Reserved, string? requestHash = null)
    {
        Guid id = Guid.NewGuid();
        DateTime createdAt = DateTime.UtcNow.AddMinutes(-20);
        await using var db = factory.CreateDbContext();
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = id,
            ExecutionId = a.ExecutionId,
            StepResultId = ownerResultId,
            ProjectId = a.ProjectId,
            ClipId = "clip-1",
            RequestHash = requestHash ?? Hash1,
            RemoteJobId = remoteJobId,
            Status = status,
            CostEstimate = TakeEstimate,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static VideoGenerationBudgetGate Gate(
        IDbContextFactory<WorkflowEngineDbContext> factory, decimal daily = 1_000_000m, decimal global = 1_000_000m, int maxAbandons = 1) =>
        new(factory, Options.Create(new VideoGenerationOptions
        {
            DailyBudgetCredits = daily,
            GlobalDailyBudgetCredits = global,
            MaxAbandonsPerTake = maxAbandons
        }));

    private static VideoGenerationReservationRequest Request(Attempts a, decimal stepCap = 100_000m) =>
        new(a.ProjectId, a.ExecutionId, a.CurrentResultId, Guid.NewGuid(), stepCap,
            [new VideoGenerationTakeRequest("clip-1", Hash1, TakeEstimate)]);

    private static ExternalGenerationJob Job(SharedInMemoryDbFactory factory, Guid id)
    {
        using var db = factory.CreateDbContext();
        return db.ExternalGenerationJobs.AsNoTracking().Single(j => j.Id == id);
    }

    private static List<ExternalGenerationJob> Jobs(SharedInMemoryDbFactory factory)
    {
        using var db = factory.CreateDbContext();
        return db.ExternalGenerationJobs.AsNoTracking().OrderBy(j => j.CreatedAt).ToList();
    }

    // ------------------------------------------------------------------ gate: the abandon rule

    [Fact]
    public async Task Resume_abandons_an_earlier_failed_attempts_orphan_and_reserves_a_fresh_row()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        ReservedTake take = reservation.Takes.Should().ContainSingle().Subject;
        take.Existing.Should().BeFalse("the orphan was abandoned, so this take is bought again");
        take.JobId.Should().NotBe(orphan, "the fresh row id is the fresh idempotency key");
        take.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        reservation.Estimated.Should().Be(TakeEstimate);

        ExternalGenerationJob old = Job(factory, orphan);
        old.Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
        old.RemoteJobId.Should().BeNull();
        old.ActualCost.Should().BeNull("an abandoned row is never billed");
        ExternalGenerationJob fresh = Job(factory, take.JobId);
        fresh.StepResultId.Should().Be(a.CurrentResultId);
        fresh.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
    }

    [Fact]
    public async Task Retry_abandons_an_orphan_whose_step_result_was_superseded()
    {
        var factory = new SharedInMemoryDbFactory();
        // A Retry does not count as a resume; its earlier rows are superseded (Retry). The earlier result
        // need not be Failed: Retry supersedes whatever the run left.
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 0, earlierStatus: StepStatus.Running, earlierSuperseded: true);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        reservation.Takes.Single().Existing.Should().BeFalse();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
        Jobs(factory).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_in_step_retry_of_the_same_attempt_keeps_the_orphan_as_existing()
    {
        var factory = new SharedInMemoryDbFactory();
        // Even on a resumed run: the row belongs to THIS attempt's result (WorkflowHardening:MaxStepRetries
        // re-runs the executor against the same step result), so it is not an earlier attempt's.
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.CurrentResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        ReservedTake take = reservation.Takes.Single();
        take.Existing.Should().BeTrue();
        take.JobId.Should().Be(orphan);
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_first_attempt_never_abandons_even_beside_a_failed_earlier_dispatch()
    {
        var factory = new SharedInMemoryDbFactory();
        // No resume, nothing superseded: this is the run's first attempt, so no earlier attempt exists.
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 0);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Takes.Single().Existing.Should().BeTrue();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
    }

    [Fact]
    public async Task An_earlier_attempts_take_with_a_remote_job_id_is_re_attached_not_abandoned()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid submitted = await SeedLedgerRowAsync(factory, a, a.EarlierResultId, remoteJobId: "remote-prior",
            status: ExternalGenerationJobStatus.Submitted);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        ReservedTake take = reservation.Takes.Single();
        take.Existing.Should().BeTrue("the vendor has the job: poll it, never pay for it again");
        take.JobId.Should().Be(submitted);
        Job(factory, submitted).Status.Should().Be(ExternalGenerationJobStatus.Submitted);
        Jobs(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task The_abandoned_amount_keeps_counting_toward_the_daily_and_global_caps()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        // Caps fit exactly TWO takes: the orphan (possibly paid at the vendor) and its one re-buy.
        var gate = Gate(factory, daily: 2 * TakeEstimate, global: 2 * TakeEstimate);
        var reservation = await gate.ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        reservation.ProjectSpentToday.Should().Be(TakeEstimate, "the orphan may have reached the vendor: it counts as spent");
        reservation.GlobalSpentToday.Should().Be(TakeEstimate);
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);

        // Afterwards today's ledger holds both: the Abandoned row at its reserved amount and the fresh row.
        var other = await gate.ReserveAsync(
            new VideoGenerationReservationRequest(a.ProjectId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100_000m,
                [new VideoGenerationTakeRequest("clip-1", "other-hash", 1m)]),
            CancellationToken.None);
        other.Allowed.Should().BeFalse();
        other.ProjectSpentToday.Should().Be(2 * TakeEstimate);
        other.GlobalSpentToday.Should().Be(2 * TakeEstimate);
    }

    [Theory]
    [InlineData("daily")]
    [InlineData("global")]
    public async Task A_re_buy_the_abandoned_amount_pushes_over_a_daily_cap_is_refused_and_abandons_nothing(string cap)
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        // One take fits; orphan + re-buy does not.
        var gate = cap == "daily"
            ? Gate(factory, daily: TakeEstimate)
            : Gate(factory, global: TakeEstimate);
        var reservation = await gate.ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeFalse();
        reservation.DenyReason.Should().StartWith("BUDGET_EXCEEDED");
        // QA-P0-17/S-01: each cap has its own sentence, and the platform one never yields a global figure.
        reservation.DenyReason.Should().Contain(cap == "daily" ? "the project's daily video budget" : "the platform's daily limit");
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_re_buy_the_abandoned_amount_pushes_over_MaxSpend_is_refused()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        // The step cap fits the take once, not the abandoned amount plus the re-buy.
        var reservation = await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate - 1m), CancellationToken.None);

        reservation.Allowed.Should().BeFalse();
        reservation.DenyReason.Should().StartWith("BUDGET_EXCEEDED").And.Contain("step");
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        // At exactly orphan + re-buy it fits.
        (await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate), CancellationToken.None)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_take_already_abandoned_MaxAbandonsPerTake_times_is_not_abandoned_again()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 2);
        // The first cycle: abandoned once already, and its re-buy was orphaned again by a failed attempt.
        await SeedLedgerRowAsync(factory, a, a.EarlierResultId, status: ExternalGenerationJobStatus.Abandoned);
        Guid secondOrphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        ReservedTake take = reservation.Takes.Single();
        take.Existing.Should().BeTrue("the default MaxAbandonsPerTake (1) is used up: the take stays orphaned");
        take.JobId.Should().Be(secondOrphan);
        Job(factory, secondOrphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_gpu_seconds_orphan_keeps_counting_toward_the_daily_GPU_cap()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = orphan,
                ExecutionId = a.ExecutionId,
                StepResultId = a.EarlierResultId,
                ProjectId = a.ProjectId,
                ClipId = "clip-1",
                RequestHash = Hash1,
                Status = ExternalGenerationJobStatus.Reserved,
                CostEstimate = 60m,
                CostUnit = ExternalGenerationJob.GpuSecondsUnit,
                CreatedAt = DateTime.UtcNow.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
        }

        VideoGenerationReservationRequest GpuRequest() => new(a.ProjectId, a.ExecutionId, a.CurrentResultId, Guid.NewGuid(), 100_000m,
            [new VideoGenerationTakeRequest("clip-1", Hash1, 60m)], VideoGenerationCostUnit.GpuSeconds);
        VideoGenerationBudgetGate GpuGate(decimal dailyMinutes) => new(factory, Options.Create(new VideoGenerationOptions
        {
            DailyGpuMinutes = dailyMinutes,
            GlobalDailyGpuMinutes = 1_000m
        }));

        // One minute of GPU fits the orphan only, not its re-buy.
        var denied = await GpuGate(1m).ReserveAsync(GpuRequest(), CancellationToken.None);
        denied.Allowed.Should().BeFalse();
        denied.DenyReason.Should().StartWith("BUDGET_EXCEEDED: ").And.Contain("the daily GPU-minutes budget");
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);

        var allowed = await GpuGate(2m).ReserveAsync(GpuRequest(), CancellationToken.None);
        allowed.Allowed.Should().BeTrue(allowed.DenyReason);
        allowed.ProjectSpentToday.Should().Be(60m);
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
    }

    [Fact]
    public async Task MaxAbandonsPerTake_zero_never_abandons()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory, maxAbandons: 0).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Takes.Single().Existing.Should().BeTrue();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
    }

    [Fact]
    public async Task A_denied_reservation_leaves_the_orphan_untouched()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);
        DateTime updatedAt = Job(factory, orphan).UpdatedAt;

        var reservation = await Gate(factory).ReserveAsync(Request(a, stepCap: TakeEstimate - 1m), CancellationToken.None);

        reservation.Allowed.Should().BeFalse();
        reservation.DenyReason.Should().StartWith("BUDGET_EXCEEDED");
        ExternalGenerationJob row = Job(factory, orphan);
        row.Status.Should().Be(ExternalGenerationJobStatus.Reserved, "all or nothing: a denial abandons nothing");
        row.UpdatedAt.Should().Be(updatedAt);
        Jobs(factory).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ gate: a changed request hash (Security MEDIUM)

    [Fact]
    public async Task A_changed_hash_retry_still_counts_the_old_orphan_toward_MaxSpend_and_abandons_it()
    {
        var factory = new SharedInMemoryDbFactory();
        // A Retry from an earlier step re-planned the prompt: same step, a different hash than the orphan's.
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 0, earlierStatus: StepStatus.Failed, earlierSuperseded: true);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId, requestHash: "old-plan-hash");

        var refused = await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate - 1m), CancellationToken.None);
        refused.Allowed.Should().BeFalse();
        refused.DenyReason.Should().StartWith("BUDGET_EXCEEDED: ").And.Contain("this step's spending limit");
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved, "a denial abandons nothing");

        var allowed = await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate), CancellationToken.None);
        allowed.Allowed.Should().BeTrue(allowed.DenyReason);
        allowed.Takes.Single().Existing.Should().BeFalse();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Abandoned,
            "given up under the same lock, so the sweeper no longer sees it in flight");
    }

    [Fact]
    public async Task A_changed_hash_abandoned_row_of_an_earlier_cycle_still_counts_toward_MaxSpend()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        await SeedLedgerRowAsync(factory, a, a.EarlierResultId, status: ExternalGenerationJobStatus.Abandoned, requestHash: "old-plan-hash");

        (await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate - 1m), CancellationToken.None)).Allowed.Should().BeFalse();
        (await Gate(factory).ReserveAsync(Request(a, stepCap: 2 * TakeEstimate), CancellationToken.None)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_changed_hash_abandon_does_not_use_up_the_new_takes_abandon_limit()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        await SeedLedgerRowAsync(factory, a, a.EarlierResultId, status: ExternalGenerationJobStatus.Abandoned, requestHash: "old-plan-hash");
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var reservation = await Gate(factory).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Takes.Single().Existing.Should().BeFalse();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
    }

    [Fact]
    public async Task Another_steps_orphan_does_not_count_toward_this_steps_MaxSpend()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid otherStepResult = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            Guid otherStep = Guid.NewGuid();
            db.WorkflowSteps.Add(new WorkflowStep { Id = otherStep, WorkflowDefinitionId = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoGenerate });
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = otherStepResult,
                WorkflowExecutionId = a.ExecutionId,
                WorkflowStepId = otherStep,
                Status = StepStatus.Failed
            });
            await db.SaveChangesAsync();
        }

        Guid otherOrphan = await SeedLedgerRowAsync(factory, a, otherStepResult, requestHash: "step-2-hash");

        var reservation = await Gate(factory).ReserveAsync(Request(a, stepCap: TakeEstimate), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        Job(factory, otherOrphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved, "it is step 2's to abandon");
    }

    [Fact]
    public async Task With_MaxAbandonsPerTake_zero_a_requested_take_is_never_abandoned_but_an_orphan_of_a_hash_no_longer_requested_still_is()
    {
        var factory = new SharedInMemoryDbFactory();
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid sameHash = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);
        Guid changedHash = await SeedLedgerRowAsync(factory, a, a.EarlierResultId, requestHash: "hash-this-attempt-no-longer-asks-for");

        var reservation = await Gate(factory, maxAbandons: 0).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeTrue(reservation.DenyReason);
        ReservedTake take = reservation.Takes.Single();
        take.Existing.Should().BeTrue("≤ 0 never abandons a take this attempt asks for: it stays existing (RESERVATION_ORPHANED, no submit)");
        take.JobId.Should().Be(sameHash);
        Job(factory, sameHash).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Job(factory, changedHash).Status.Should().Be(ExternalGenerationJobStatus.Abandoned,
            "an orphan of a hash no longer requested is never re-bought, so it is abandoned whatever the limit; it keeps counting toward the caps");
        Jobs(factory).Should().HaveCount(2, "nothing was re-bought");
    }

    // ------------------------------------------------------------------ executor level

    private static Mock<IVideoGenerationClient> SucceedingClient(params string[] remoteIds)
    {
        var client = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        int i = 0;
        client
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ExternalJobHandle(remoteIds[i++]));
        foreach (string id in remoteIds)
        {
            client
                .Setup(c => c.PollAsync(It.Is<ExternalJobHandle>(h => h.RemoteJobId == id), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6));
        }

        client
            .Setup(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        return client;
    }

    private static Mock<IProjectFileWorkspace> UploadingWorkspace()
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "video/mp4", It.IsAny<CancellationToken>(), "agentFiles"))
            // The production key shape (ProjectFileWorkspace -> ProjectFilePath.BuildStorageKey): a resume only reuses
            // a row whose keys sit under this project's prefix (SQFU-S5).
            .ReturnsAsync((Guid projectId, string path, string name, string _, CancellationToken _, string category) =>
                ReelBolt.Shared.ProjectFilePath.BuildStorageKey(projectId, category, path, name));
        return workspace;
    }

    private static Mock<IVideoToolRunner> WorkingRunner()
    {
        const string probeJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
              "r_frame_rate": "30/1", "duration": "6" } ], "format": { "duration": "6" } }
            """;
        var runner = new Mock<IVideoToolRunner>();
        runner
            .Setup(r => r.RunFfprobeAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: probeJson, StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        runner
            .Setup(r => r.RunFfmpegAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new VideoToolResult(ExitCode: 0, StdOut: "", StdErr: "", TimedOut: false));
        return runner;
    }

    private static JsonNode Clip(StepExecutionResult result) => JsonNode.Parse(result.Output)!["clips"]![0]!;

    [Fact]
    public async Task A_manual_resume_meeting_an_orphan_re_submits_under_the_new_rows_key_and_completes()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SucceedingClient("r-new");
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running, ResumeCount = 1 };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            Gate(factory), client.Object, factory, Cfg, execution, UploadingWorkspace().Object, WorkingRunner().Object);
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1, executionId: execution.Id, projectId: execution.ProjectId,
            stepId: context.Step.Id, currentResultId: context.StepResultId);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, result.Output);
        Clip(result)["failureReason"].Should().BeNull("no RESERVATION_ORPHANED on a new attempt");
        Guid fresh = Jobs(factory).Single(j => j.Id != orphan).Id;
        client.Verify(c => c.SubmitAsync(
            It.Is<VideoGenerationRequest>(r => r.IdempotencyKey == fresh.ToString("D")), It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        ExternalGenerationJob old = Job(factory, orphan);
        old.Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
        old.ActualCost.Should().BeNull();
        ExternalGenerationJob row = Job(factory, fresh);
        row.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
        row.RemoteJobId.Should().Be("r-new");
        row.StepResultId.Should().Be(context.StepResultId);
    }

    [Fact]
    public async Task An_in_step_retry_meeting_its_own_orphan_is_still_RESERVATION_ORPHANED()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SucceedingClient("should-not-happen");
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running, ResumeCount = 1 };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            Gate(factory), client.Object, factory, Cfg, execution);
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1, executionId: execution.Id, projectId: execution.ProjectId,
            stepId: context.Step.Id, currentResultId: context.StepResultId);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.CurrentResultId);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_resume_whose_re_buy_would_exceed_the_daily_cap_fails_BUDGET_EXCEEDED_with_no_submit()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SucceedingClient("should-not-happen");
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running, ResumeCount = 1 };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            Gate(factory, daily: TakeEstimate), client.Object, factory, Cfg, execution);
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1, executionId: execution.Id, projectId: execution.ProjectId,
            stepId: context.Step.Id, currentResultId: context.StepResultId);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("BUDGET_EXCEEDED");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_abandon_of_the_same_take_beyond_the_limit_fails_RESERVATION_ORPHANED_with_no_submit()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = SucceedingClient("should-not-happen");
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running, ResumeCount = 2 };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            Gate(factory), client.Object, factory, Cfg, execution);
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 2, executionId: execution.Id, projectId: execution.ProjectId,
            stepId: context.Step.Id, currentResultId: context.StepResultId);
        await SeedLedgerRowAsync(factory, a, a.EarlierResultId, status: ExternalGenerationJobStatus.Abandoned);
        Guid secondOrphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        result.Status.Should().Be(StepStatus.Failed);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        Job(factory, secondOrphan).Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        Jobs(factory).Should().HaveCount(2);
    }

    // ------------------------------------------------------------------ executor: a zombie attempt never overwrites an Abandoned row (Security LOW)

    private static async Task AbandonAsync(SharedInMemoryDbFactory factory, Guid jobId)
    {
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalGenerationJobs.SingleAsync(j => j.Id == jobId);
        row.Status = ExternalGenerationJobStatus.Abandoned;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_zombie_whose_row_was_abandoned_before_submit_never_submits()
    {
        var factory = new SharedInMemoryDbFactory();
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        Guid jobId = Guid.NewGuid();
        // The zombie's own gate call reserved the row; a newer attempt abandoned it before the zombie reached the submit.
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using (var db = factory.CreateDbContext())
                {
                    db.ExternalGenerationJobs.Add(new ExternalGenerationJob
                    {
                        Id = jobId,
                        ExecutionId = execution.Id,
                        ProjectId = execution.ProjectId,
                        ClipId = "clip-1",
                        RequestHash = Hash1,
                        Status = ExternalGenerationJobStatus.Reserved,
                        CostEstimate = TakeEstimate
                    });
                    await db.SaveChangesAsync();
                }

                await AbandonAsync(factory, jobId);
                return new VideoGenerationReservation(true, TakeEstimate, 0m, 0m, null,
                    [new ReservedTake(jobId, "clip-1", Hash1, TakeEstimate, ExternalGenerationJobStatus.Reserved, Existing: false)]);
            });
        var client = SucceedingClient("should-not-happen");
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(gate.Object, client.Object, factory, Cfg, execution);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        Job(factory, jobId).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
    }

    [Fact]
    public async Task A_zombie_cannot_record_a_submit_on_a_row_abandoned_while_it_submitted()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await AbandonAsync(factory, Jobs(factory).Single().Id);
                return new ExternalJobHandle("r-zombie");
            });
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(Gate(factory), client.Object, factory, Cfg, execution);

        var result = await executor.ExecuteAsync(context);

        ExternalGenerationJob row = Jobs(factory).Single();
        row.Status.Should().Be(ExternalGenerationJobStatus.Abandoned, "the lost update is logged, never applied");
        row.RemoteJobId.Should().BeNull();
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        client.Verify(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_zombie_cannot_flip_an_abandoned_row_to_Failed_and_release_it_from_the_caps()
    {
        var factory = new SharedInMemoryDbFactory();
        var client = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalJobHandle("r-zombie"));
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await AbandonAsync(factory, Jobs(factory).Single().Id);
                return new ExternalJobStatus(ExternalJobState.Failed, null, "vendor said no");
            });
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(Gate(factory), client.Object, factory, Cfg, execution);

        await executor.ExecuteAsync(context);

        Jobs(factory).Single().Status.Should().Be(ExternalGenerationJobStatus.Abandoned,
            "Failed would release it from every cap; the zombie's write is refused");
    }

    [Fact]
    public async Task A_zombie_whose_row_was_abandoned_while_it_polled_does_not_deliver_or_record_the_clip()
    {
        // Orphan LOW 2: DeliverAsync must stop on a refused write, or a stale attempt overwrites a newer StorageKey.
        var factory = new SharedInMemoryDbFactory();
        var client = SucceedingClient("r-zombie");
        client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await AbandonAsync(factory, Jobs(factory).Single().Id);
                return new ExternalJobStatus(ExternalJobState.Succeeded, "https://example.com/v.mp4", null, UsageTotalSeconds: 6);
            });
        var workspace = UploadingWorkspace();
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(
            Gate(factory), client.Object, factory, Cfg, execution, workspace: workspace.Object);

        var result = await executor.ExecuteAsync(context);

        ExternalGenerationJob row = Jobs(factory).Single();
        row.Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
        row.StorageKey.Should().BeNull("the zombie never writes a StorageKey over the row a newer attempt owns");
        row.ActualCost.Should().BeNull();
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        client.Verify(c => c.DownloadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        workspace.Verify(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "video/mp4",
            It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never, "no clip is uploaded for a take this attempt lost");
    }

    [Fact]
    public async Task An_unexpected_error_on_an_abandoned_row_leaves_it_abandoned()
    {
        // Orphan LOW 3: Abandoned is terminal. Releasing it (Cancelled) would take it out of every cap.
        var factory = new SharedInMemoryDbFactory();
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, _) = new VideoGenerateStepExecutorTests().BuildExecutor(Gate(factory), SucceedingClient("x").Object, factory, Cfg, execution);
        Guid jobId = Guid.NewGuid();
        await using (var seed = factory.CreateDbContext())
        {
            seed.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = jobId,
                ExecutionId = execution.Id,
                ProjectId = execution.ProjectId,
                ClipId = "clip-1",
                RequestHash = Hash1,
                Status = ExternalGenerationJobStatus.Abandoned,
                CostEstimate = TakeEstimate
            });
            await seed.SaveChangesAsync();
        }

        await using var db = factory.CreateDbContext();
        var state = new VideoGenerateStepExecutor.TakeState { Job = await db.ExternalGenerationJobs.SingleAsync(j => j.Id == jobId) };
        var outcome = await executor.HandleUnexpectedAsync(
            new InvalidOperationException("boom"),
            new ReservedTake(jobId, "clip-1", Hash1, TakeEstimate, ExternalGenerationJobStatus.Reserved, Existing: false),
            state, db);

        outcome.Code.Should().Be("UNEXPECTED_ERROR");
        Job(factory, jobId).Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
    }

    /// <summary>Contexts over one InMemory database whose first save that abandons a row first lets a zombie write it.</summary>
    private sealed class ZombieWritesDuringAbandon(string databaseName, Func<Task> zombieWrite) : IDbContextFactory<WorkflowEngineDbContext>
    {
        private sealed class Interceptor(Func<Task> zombieWrite) : SaveChangesInterceptor
        {
            private int _fired;

            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                bool abandons = eventData.Context!.ChangeTracker.Entries<ExternalGenerationJob>()
                    .Any(e => e.State == EntityState.Modified && e.Entity.Status == ExternalGenerationJobStatus.Abandoned);
                if (abandons && Interlocked.Exchange(ref _fired, 1) == 0)
                {
                    await zombieWrite();
                }

                return result;
            }
        }

        private readonly Interceptor _interceptor = new(zombieWrite);

        public WorkflowEngineDbContext CreateDbContext() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(databaseName).AddInterceptors(_interceptor).Options);
    }

    [Fact]
    public async Task A_ledger_row_changed_under_the_abandon_is_refused_RESERVATION_ORPHANED_and_reserves_nothing()
    {
        // Orphan LOW 1: the gate's save is conditional on each row's status; a concurrent writer's change must map to
        // a defined failure code, not escape as UNEXPECTED_ERROR.
        string name = Guid.NewGuid().ToString();
        var factory = new SharedInMemoryDbFactory(name);
        Attempts a = await SeedAttemptsAsync(factory, resumeCount: 1);
        Guid orphan = await SeedLedgerRowAsync(factory, a, a.EarlierResultId);
        var racing = new ZombieWritesDuringAbandon(name, async () =>
        {
            await using var db = factory.CreateDbContext();
            var row = await db.ExternalGenerationJobs.SingleAsync(j => j.Id == orphan);
            row.Status = ExternalGenerationJobStatus.Submitted;
            row.RemoteJobId = "r-zombie";
            await db.SaveChangesAsync();
        });

        var reservation = await Gate(racing).ReserveAsync(Request(a), CancellationToken.None);

        reservation.Allowed.Should().BeFalse();
        reservation.DenyReason.Should().StartWith("RESERVATION_ORPHANED:");
        reservation.Takes.Should().BeEmpty();
        Job(factory, orphan).Status.Should().Be(ExternalGenerationJobStatus.Submitted, "the zombie's write stands; nothing is abandoned over it");
        // That nothing else was saved is the transaction's job: proven on Postgres in ExecutionLeaseOwnershipPostgresTests
        // (the InMemory provider is not transactional).
    }

    [Fact]
    public async Task A_gate_refusal_for_a_changed_ledger_row_fails_the_step_RESERVATION_ORPHANED_not_BUDGET_EXCEEDED()
    {
        var factory = new SharedInMemoryDbFactory();
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationReservation(false, 0m, 0m, 0m, "RESERVATION_ORPHANED: a ledger row changed", []));
        var client = SucceedingClient("should-not-happen");
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(gate.Object, client.Object, factory, Cfg, execution);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        JsonNode.Parse(result.Output)!["meta"]!["failed"]!.GetValue<string>().Should().Be("RESERVATION_ORPHANED");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ------------------------------------------------------------------ executor: a run whose lease was lost never reserves or submits (SQFU-S1)

    private static (ExecutionLeaseService Leases, WorkflowExecution Execution) LeasedRun(SharedInMemoryDbFactory factory)
    {
        ExecutionLeaseService leases = new(
            new Scopes(factory.CreateDbContext),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-a" }).Build(),
            NullLogger<ExecutionLeaseService>.Instance);
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Status = ExecutionStatus.Running };
        leases.StampLease(execution);
        using var db = factory.CreateDbContext();
        db.WorkflowExecutions.Add(execution);
        db.SaveChanges();
        return (leases, execution);
    }

    private static async Task HandOverAsync(SharedInMemoryDbFactory factory, Guid executionId)
    {
        await using var db = factory.CreateDbContext();
        var row = await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
        row.LeaseOwner = "engine-b";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_run_whose_lease_was_lost_reserves_nothing_and_submits_nothing()
    {
        var factory = new SharedInMemoryDbFactory();
        (ExecutionLeaseService leases, WorkflowExecution execution) = LeasedRun(factory);
        await HandOverAsync(factory, execution.Id);
        var client = SucceedingClient("should-not-happen");
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(Gate(factory), client.Object, factory, Cfg, execution, leases: leases);

        var result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        JsonNode.Parse(result.Output)!["meta"]!["failed"]!.GetValue<string>().Should().Be("NOT_SUBMITTED");
        Jobs(factory).Should().BeEmpty("no ledger row is reserved for a run this instance no longer owns");
        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_lease_lost_between_reserve_and_submit_releases_the_take_without_submitting()
    {
        var factory = new SharedInMemoryDbFactory();
        (ExecutionLeaseService leases, WorkflowExecution execution) = LeasedRun(factory);
        var realGate = Gate(factory);
        var gate = new Mock<IVideoGenerationBudgetGate>();
        gate.Setup(g => g.ReserveAsync(It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (VideoGenerationReservationRequest r, CancellationToken ct) =>
            {
                var reservation = await realGate.ReserveAsync(r, ct);
                await HandOverAsync(factory, execution.Id);
                return reservation;
            });
        var client = SucceedingClient("should-not-happen");
        var (executor, context) = new VideoGenerateStepExecutorTests().BuildExecutor(gate.Object, client.Object, factory, Cfg, execution, leases: leases);

        var result = await executor.ExecuteAsync(context);

        client.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Clip(result)["failureReason"]!.GetValue<string>().Should().Be("NOT_SUBMITTED");
        Jobs(factory).Single().Status.Should().Be(ExternalGenerationJobStatus.Cancelled, "never sent, so released");
    }

    // ------------------------------------------------------------------ engine level: resume / retry, charged once


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_run_whose_take_was_orphaned_resumes_or_retries_once_paid_once_and_completes(bool resume)
    {
        string dbName = "orphan-" + Guid.NewGuid().ToString("N");
        WorkflowEngineDbContext NewDb() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(dbName).Options);
        var factory = new SharedInMemoryDbFactory(dbName);
        Seeded s;
        using (WorkflowEngineDbContext seed = NewDb())
            s = Seed(seed, new StepSpec(StepType.VideoGenerate, "Clips", st => st.VideoGenerateConfigJson = Cfg));
        Scopes scopes = new(NewDb);
        var harness = new VideoGenerateStepExecutorTests();

        // Attempt 1: the submit's outcome is unknown (network error after the vendor may have accepted it), so
        // the row stays Reserved with no RemoteJobId. The engine's in-step retries meet that orphan inside the
        // SAME attempt and must not re-submit it.
        var lossy = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        lossy
            .Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var placeholder = new WorkflowExecution { Id = s.ExecutionId, ProjectId = s.ProjectId };
        var (firstExecutor, _) = harness.BuildExecutor(Gate(factory), lossy.Object, factory, Cfg, placeholder);
        RecordingGate firstCredits = new();
        Func<Task> first = () => Engine(scopes, firstCredits, firstExecutor).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        (await first.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*RESERVATION_ORPHANED*");

        lossy.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once,
            "in-step retries keep RESERVATION_ORPHANED: one attempt pays the vendor at most once");
        ExternalGenerationJob orphan = Jobs(factory).Should().ContainSingle().Subject;
        orphan.Status.Should().Be(ExternalGenerationJobStatus.Reserved);
        orphan.RemoteJobId.Should().BeNull();
        using (WorkflowEngineDbContext db = NewDb())
        {
            db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId).Status.Should().Be(ExecutionStatus.Failed);
            db.WorkflowStepResults.AsNoTracking().Single().Status.Should().Be(StepStatus.Failed);
        }

        firstCredits.Settled.Should().BeEmpty("a failed attempt settles no credits");

        // Attempt 2: a manual Resume (intent on the row) or a Retry (superseded rows), as the routes write them.
        await RequeueAsync(NewDb, s.ExecutionId, resume, supersede: !resume);
        var good = SucceedingClient("r-new");
        var (secondExecutor, _) = harness.BuildExecutor(Gate(factory), good.Object, factory, Cfg, placeholder,
            UploadingWorkspace().Object, WorkingRunner().Object);
        RecordingGate credits = new();
        await Engine(scopes, credits, secondExecutor).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        using (WorkflowEngineDbContext db = NewDb())
        {
            WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId);
            run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
            WorkflowStepResult live = db.WorkflowStepResults.AsNoTracking().Single(r => r.Id != orphan.StepResultId);
            live.Status.Should().Be(StepStatus.Completed);

            // The user's credit charge for the step: one reserve -> settle, on the new step result, and nothing else.
            credits.Reserved.Should().Equal([live.Id]);
            credits.Settled.Should().Equal([live.Id]);
            credits.Released.Should().BeEmpty();
            firstCredits.Settled.Concat(credits.Settled).Should().ContainSingle("the user is charged once for the clip");

            List<ExternalGenerationJob> rows = db.ExternalGenerationJobs.AsNoTracking().ToList();
            rows.Should().HaveCount(2);
            ExternalGenerationJob old = rows.Single(j => j.Id == orphan.Id);
            old.Status.Should().Be(ExternalGenerationJobStatus.Abandoned);
            old.ActualCost.Should().BeNull("an abandoned row is never billed");
            ExternalGenerationJob fresh = rows.Single(j => j.Id != orphan.Id);
            fresh.StepResultId.Should().Be(live.Id);
            fresh.Status.Should().Be(ExternalGenerationJobStatus.Succeeded);
            good.Verify(c => c.SubmitAsync(
                It.Is<VideoGenerationRequest>(r => r.IdempotencyKey == fresh.Id.ToString("D")), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // ------------------------------------------------------------------ sweeper level: auto-resume never reaches an orphan

    private sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The recovery sweeper (T-005) over the test's own store, its publish seams mocked.</summary>
    private sealed class SweeperRig : IDisposable
    {
        private readonly ServiceProvider _services;
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public Mock<IWorkflowEventPublisher> Events { get; } = new();
        public FixedTime Time { get; } = new();
        public ExecutionRecoverySweeper Sweeper { get; }

        public SweeperRig(Func<WorkflowEngineDbContext> newDb)
        {
            ServiceCollection services = new();
            services.AddScoped(_ => newDb());
            services.AddSingleton(Events.Object);
            services.AddSingleton(new Mock<IPlatformFailureHook>().Object);
            services.AddSingleton(Publish.Object);
            _services = services.BuildServiceProvider();
            IServiceScopeFactory scopes = _services.GetRequiredService<IServiceScopeFactory>();
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-a" })
                .Build();
            ExecutionLeaseService leases = new(scopes, config, NullLogger<ExecutionLeaseService>.Instance, Time);
            Sweeper = new ExecutionRecoverySweeper(scopes, leases, NullLogger<ExecutionRecoverySweeper>.Instance, Time);
        }

        public void Requeued(Guid executionId, Times times) =>
            Publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == executionId), It.IsAny<CancellationToken>()), times);

        public void Dispose() => _services.Dispose();
    }

    /// <summary>One InMemory store every context of a test shares (engine, gate, executor, sweeper). The sweeper opens a transaction.</summary>
    private static Func<WorkflowEngineDbContext> SharedStore(out IDbContextFactory<WorkflowEngineDbContext> factory)
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("orphan-" + Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        factory = new OptionsFactory(options);
        return () => new WorkflowEngineDbContext(options);
    }

    private sealed class OptionsFactory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    /// <summary>A lost Running run with an ENGINE-TAKEN snapshot and auto_resume_count 0 (so T-005 would resume it), whose lost
    /// VideoGenerate step holds one ledger row in <paramref name="ledger"/> (none when null). Its lease expired 5 s before
    /// <paramref name="sweeperNow"/>, the sweeper's own clock, so "lost" never depends on how much wall time has passed.</summary>
    private static Guid SeedLostRun(Func<WorkflowEngineDbContext> newDb, ExternalGenerationJobStatus? ledger, DateTimeOffset sweeperNow)
    {
        using WorkflowEngineDbContext db = newDb();
        Guid projectId = Guid.NewGuid();
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = Guid.NewGuid(),
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-20),
            QueuedAt = DateTime.UtcNow.AddMinutes(-21),
            LeaseOwner = "engine-dead",
            LeaseExpiresAt = sweeperNow.UtcDateTime.AddSeconds(-5)
        };
        db.WorkflowExecutions.Add(run);
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "snapshot", ProjectId = projectId, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
        db.WorkflowDefinitions.Add(snapshot);
        db.Entry(snapshot).Property<Guid?>(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = run.Id;
        run.SnapshotDefinitionId = snapshot.Id;
        Guid lost = Guid.NewGuid();
        db.WorkflowStepResults.Add(new WorkflowStepResult { Id = lost, WorkflowExecutionId = run.Id, WorkflowStepId = Guid.NewGuid(), Status = StepStatus.Running });
        if (ledger is { } status)
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = run.Id,
                StepResultId = lost,
                ProjectId = projectId,
                ClipId = "clip-1",
                RequestHash = Hash1,
                Status = status,
                CostEstimate = TakeEstimate
            });
        }

        db.SaveChanges();
        return run.Id;
    }

    /// <summary>
    /// The engine abandons on ANY new attempt (no manual/auto flag), so "auto-resume never re-submits an orphan" rests
    /// on T-005: a lost run that would otherwise be auto-resumed (engine snapshot, auto_resume_count 0) is failed and left
    /// for a manual Resume while its ledger holds a Reserved take. The Abandoned and no-row cases are the controls: the
    /// same run IS re-queued, so the refusal is the ledger's, and an Abandoned row does not block.
    /// </summary>
    [Theory]
    [InlineData(ExternalGenerationJobStatus.Reserved, false)]
    [InlineData(ExternalGenerationJobStatus.Abandoned, true)]
    [InlineData(null, true)]
    public async Task The_sweeper_auto_resumes_a_lost_run_only_when_no_reserved_take_is_left(ExternalGenerationJobStatus? ledger, bool requeued)
    {
        Func<WorkflowEngineDbContext> newDb = SharedStore(out _);
        using SweeperRig rig = new(newDb);
        Guid executionId = SeedLostRun(newDb, ledger, rig.Time.GetUtcNow());

        await rig.Sweeper.SweepOnceAsync(CancellationToken.None);

        using WorkflowEngineDbContext db = newDb();
        WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == executionId);
        if (requeued)
        {
            run.Status.Should().Be(ExecutionStatus.Queued);
            run.PendingResume.Should().BeTrue();
            run.AutoResumeCount.Should().Be(1);
            rig.Requeued(executionId, Times.Once());
        }
        else
        {
            run.Status.Should().Be(ExecutionStatus.Failed, "it waits for a manual Resume");
            run.PendingResume.Should().BeFalse();
            run.AutoResumeCount.Should().Be(0);
            rig.Requeued(executionId, Times.Never());
            db.ExternalGenerationJobs.AsNoTracking().Single().Status.Should().Be(ExternalGenerationJobStatus.Reserved,
                "the sweeper never abandons; only a new attempt the user asked for does");
        }
    }

    /// <summary>
    /// What a dead engine leaves: the run Running under a lease that expired 5 s before <paramref name="sweeperNow"/> (the
    /// sweeper's own clock, NOT the wall clock: the rig's clock is fixed when it is built, so a wall-clock lease stamped
    /// after more than 5 s of engine work looked live to it and the run was never recovered), the step it was on still Running.
    /// </summary>
    private static async Task LoseEngineAsync(Func<WorkflowEngineDbContext> newDb, Guid executionId, Guid lostResultId, DateTimeOffset sweeperNow)
    {
        await using WorkflowEngineDbContext db = newDb();
        WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
        run.Status = ExecutionStatus.Running;
        run.CompletedAt = null;
        run.ErrorMessage = null;
        run.LeaseOwner = "engine-dead";
        run.LeaseExpiresAt = sweeperNow.UtcDateTime.AddSeconds(-5);
        WorkflowStepResult step = await db.WorkflowStepResults.SingleAsync(r => r.Id == lostResultId);
        step.Status = StepStatus.Running;
        step.CompletedAt = null;
        step.ErrorDetails = null;
        await db.SaveChangesAsync();
    }

    private static async Task<WorkflowStepResult> LatestResultAsync(Func<WorkflowEngineDbContext> newDb, Guid executionId, StepType type)
    {
        await using WorkflowEngineDbContext db = newDb();
        return (await db.WorkflowStepResults.AsNoTracking().Include(r => r.WorkflowStep)
                .Where(r => r.WorkflowExecutionId == executionId && r.WorkflowStep.StepType == type)
                .ToListAsync())
            .OrderBy(r => r.ExecutedAt).Last();
    }

    private static WorkflowExecutorService Engine(IServiceScopeFactory scopes, ICreditGate gate, params IStepExecutor[] executors) => new(
        scopeFactory: scopes,
        eventPublisher: new NoOpPublisher(),
        logger: NullLogger<WorkflowExecutorService>.Instance,
        executors: executors,
        rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
        hardeningOptions: Options.Create(new WorkflowHardeningOptions { RetryBaseDelaySeconds = 0 }),
        cancellationRegistry: new ExecutionCancellationRegistry(),
        creditGate: gate)
    {
        CreditCloseRetryDelays = []
    };

    /// <summary>
    /// T-005 x T-015, end to end on the real engine, budget gate, executor and sweeper: a run lost with an orphaned take is
    /// refused its auto-resume; the user's manual Resume abandons the orphan and buys the clip once; the engine is lost again
    /// at a later step with nothing in flight, and THEN the sweeper auto-resumes it (auto_resume_count still 0), the resume
    /// reuses the delivered clip and the run passes. The vendor is asked twice in all (the accepted double pay), never a third
    /// time, and the user's credits for the clip step are settled once.
    /// </summary>
    [Fact]
    public async Task A_run_refused_auto_resume_for_an_orphan_is_resumed_by_hand_then_auto_resumed_after_a_later_loss()
    {
        Func<WorkflowEngineDbContext> newDb = SharedStore(out IDbContextFactory<WorkflowEngineDbContext> factory);
        Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
            s = Seed(seed,
                new StepSpec(StepType.VideoGenerate, "Clips", st => st.VideoGenerateConfigJson = Cfg),
                new StepSpec(StepType.Agent, "After"));
        Scopes scopes = new(newDb);
        var harness = new VideoGenerateStepExecutorTests();
        var placeholder = new WorkflowExecution { Id = s.ExecutionId, ProjectId = s.ProjectId };
        using SweeperRig rig = new(newDb);
        bool agentFails = false;
        int agentRuns = 0;
        var agent = new Executor(StepType.Agent, c =>
        {
            agentRuns++;
            return agentFails ? throw new InvalidOperationException("engine about to be lost") : Task.FromResult(Done(c, "{}"));
        });

        // 1. The vendor may have accepted the submit, but no remote id was recorded; then the engine is lost.
        var lossy = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        lossy.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var (lossyExecutor, _) = harness.BuildExecutor(Gate(factory), lossy.Object, factory, Cfg, placeholder);
        RecordingGate credits1 = new();
        Func<Task> attempt1 = () => Engine(scopes, credits1, lossyExecutor, agent).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await attempt1.Should().ThrowAsync<InvalidOperationException>();
        WorkflowStepResult lostClips = await LatestResultAsync(newDb, s.ExecutionId, StepType.VideoGenerate);
        await LoseEngineAsync(newDb, s.ExecutionId, lostClips.Id, rig.Time.GetUtcNow());
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId);
            run.AutoResumeCount.Should().Be(0);
            (await ResumeCursor.IsEngineTakenSnapshotAsync(db, run.Id, run.SnapshotDefinitionId!.Value, CancellationToken.None))
                .Should().BeTrue("so the only thing that can refuse the auto-resume is the ledger");
        }

        // T-005 refuses: a Reserved take is in the ledger.
        await rig.Sweeper.SweepOnceAsync(CancellationToken.None);
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId);
            run.Status.Should().Be(ExecutionStatus.Failed);
            run.AutoResumeCount.Should().Be(0);
            run.PendingResume.Should().BeFalse();
        }

        rig.Requeued(s.ExecutionId, Times.Never());

        // 2. The user's manual Resume (as T-004 writes it): the orphan is abandoned and the clip bought once; then the
        // engine is lost again during the later Agent step.
        await RequeueAsync(newDb, s.ExecutionId, resume: true);
        var good = SucceedingClient("r-new");
        var (goodExecutor, _) = harness.BuildExecutor(Gate(factory), good.Object, factory, Cfg, placeholder,
            UploadingWorkspace().Object, WorkingRunner().Object);
        agentFails = true;
        RecordingGate credits2 = new();
        Func<Task> attempt2 = () => Engine(scopes, credits2, goodExecutor, agent).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
        await attempt2.Should().ThrowAsync<InvalidOperationException>();
        good.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        WorkflowStepResult clips = await LatestResultAsync(newDb, s.ExecutionId, StepType.VideoGenerate);
        clips.Status.Should().Be(StepStatus.Completed);
        using (WorkflowEngineDbContext db = newDb())
        {
            List<ExternalGenerationJob> rows = db.ExternalGenerationJobs.AsNoTracking().ToList();
            rows.Select(j => j.Status).Should().BeEquivalentTo([ExternalGenerationJobStatus.Abandoned, ExternalGenerationJobStatus.Succeeded]);
        }

        WorkflowStepResult lostAfter = await LatestResultAsync(newDb, s.ExecutionId, StepType.Agent);
        await LoseEngineAsync(newDb, s.ExecutionId, lostAfter.Id, rig.Time.GetUtcNow());

        // 3. Nothing Reserved/Submitted/Running is left (the Abandoned row does not block) and auto_resume_count is 0:
        // T-005 auto-resumes now.
        await rig.Sweeper.SweepOnceAsync(CancellationToken.None);
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId);
            run.Status.Should().Be(ExecutionStatus.Queued);
            run.PendingResume.Should().BeTrue();
            run.AutoResumeCount.Should().Be(1);
            run.ResumeCount.Should().Be(2, "one manual resume, one automatic");
        }

        rig.Requeued(s.ExecutionId, Times.Once());

        // The auto-resume reuses the delivered clip (no executor call, no submit, no charge) and finishes the run.
        var strict = VideoGenerateStepExecutorTests.MiniMaxDescribingClient();
        var (strictExecutor, _) = harness.BuildExecutor(Gate(factory), strict.Object, factory, Cfg, placeholder);
        agentFails = false;
        RecordingGate credits3 = new();
        await Engine(scopes, credits3, strictExecutor, agent).ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        strict.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        lossy.Verify(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        using (WorkflowEngineDbContext db = newDb())
        {
            WorkflowExecution run = db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == s.ExecutionId);
            run.Status.Should().Be(ExecutionStatus.Passed, run.ErrorMessage);
            db.ExternalGenerationJobs.AsNoTracking().Count().Should().Be(2);
        }

        new[] { credits1, credits2, credits3 }.SelectMany(g => g.Settled).Count(id => id == clips.Id).Should().Be(1,
            "the user is charged once for the clip step");
        new[] { credits1, credits2, credits3 }.SelectMany(g => g.Settled).Should().NotContain(lostClips.Id);
    }
}
