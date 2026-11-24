using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>first-film-funnel-fixes T-001: activation.run_failed, once per execution, closed detail.</summary>
public class RunFailedProductEventConsumerTests
{
    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ConsumeContext<WorkflowExecutionFailed> Msg(Guid execution, Guid? user)
    {
        var ctx = new Mock<ConsumeContext<WorkflowExecutionFailed>>();
        ctx.SetupGet(c => c.Message).Returns(new WorkflowExecutionFailed { ExecutionId = execution, InitiatedByUserId = user });
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return ctx.Object;
    }

    private static async Task<Guid> SeedFailedStep(InferenceApiDbContext db, string? code)
    {
        Guid executionId = Guid.NewGuid();
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            Status = StepStatus.Failed,
            FailureCode = code,
            CompletedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return executionId;
    }

    [Fact]
    public async Task A_failed_run_appends_one_row_with_the_failure_code_and_a_redelivery_adds_none()
    {
        InferenceApiDbContext db = NewDb();
        Guid execution = await SeedFailedStep(db, "INSERT_CLIP_TOO_SHORT");
        var log = new RecordingProductEventLog();
        var consumer = new RunFailedProductEventConsumer(db, log, null, NullLogger<RunFailedProductEventConsumer>.Instance);
        Guid user = Guid.NewGuid();

        await consumer.Consume(Msg(execution, user));
        await consumer.Consume(Msg(execution, user));

        ProductEvent row = log.Rows.Should().ContainSingle().Subject;
        row.Event.Should().Be("activation.run_failed");
        row.Detail.Should().Be("INSERT_CLIP_TOO_SHORT");
        row.UserId.Should().Be(user);
        row.Source.Should().Be("server");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has spaces and a path /tmp/x")]
    public async Task A_missing_or_unclean_code_becomes_other(string? code)
    {
        InferenceApiDbContext db = NewDb();
        Guid execution = await SeedFailedStep(db, code);
        var log = new RecordingProductEventLog();
        await new RunFailedProductEventConsumer(db, log).Consume(Msg(execution, Guid.NewGuid()));
        log.Rows.Should().ContainSingle().Which.Detail.Should().Be("other");
    }

    [Fact]
    public async Task No_initiating_user_records_nothing_and_a_failing_log_never_throws()
    {
        InferenceApiDbContext db = NewDb();
        var log = new RecordingProductEventLog();
        await new RunFailedProductEventConsumer(db, log).Consume(Msg(Guid.NewGuid(), null));
        log.Rows.Should().BeEmpty();

        var failing = new RecordingProductEventLog { Throw = true };
        Func<Task> act = () => new RunFailedProductEventConsumer(db, failing).Consume(Msg(Guid.NewGuid(), Guid.NewGuid()));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Closed_token_and_names_are_stable()
    {
        ProductEventNames.PlanGateHit.Should().Be("billing.plan_gate_hit");
        ProductEventNames.RunFailed.Should().Be("activation.run_failed");
        ProductEventNames.UpgradePromptShown.Should().Be("billing.upgrade_prompt_shown");
        ProductEventNames.UpgradeClicked.Should().Be("billing.upgrade_clicked");
        ProductEventNames.ClosedToken(new string('a', 65)).Should().Be("other");
    }
}
