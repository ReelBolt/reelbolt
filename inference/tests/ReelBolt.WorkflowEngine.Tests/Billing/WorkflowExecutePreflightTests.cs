using ReelBolt.Shared.Workflows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WP B7's execute preflight, at the controller boundary: 200 with a range, 402 with
/// <c>{ required, balance }</c> when the workspace cannot cover the run, and no refusal at all when
/// billing is off. The assistant execute path is covered too, because "the assistant bypasses the
/// credit check" would be a hole rather than a feature.
/// </summary>
public sealed class WorkflowExecutePreflightTests
{
    // -----------------------------------------------------------------
    // The REST execute path
    // -----------------------------------------------------------------

    [Fact]
    public async Task Execute_refuses_with_402_when_the_only_credits_are_a_lapsed_grant_no_reserve_has_swept()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 5_000_000);
        CreditAccount account = db.CreditAccounts.Single();
        db.CreditGrants.Add(new CreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            OrganizationId = account.OrganizationId,
            Source = CreditGrantSource.Plan,
            AmountMillis = 5_000_000,
            RemainingMillis = 5_000_000,
            ExpiresAt = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, publish: publish.Object)
            .Execute(project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        var refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        body.RootElement.GetProperty("balance").GetDouble().Should().Be(0);

        db.WorkflowExecutions.Should().BeEmpty();
        publish.VerifyNoOtherCalls();
        db.CreditLedgerEntries.Should().BeEmpty(); // a read-only preflight writes no Expire row
        db.CreditAccounts.Single().BalanceMillis.Should().Be(5_000_000);
    }

    [Fact]
    public async Task Execute_refuses_with_402_when_the_balance_cannot_cover_the_run()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 0);

        WorkflowsController controller = CreateController(db, owner);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        var refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);

        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        body.RootElement.GetProperty("required").GetDouble().Should().Be(WorkflowsController.ToCredits(SeededRequiredMillis));
        body.RootElement.GetProperty("balance").GetDouble().Should().Be(0);
        AssertExplainsTheHold(body.RootElement, 0);

        // Nothing was queued: the refusal happens before the execution row exists.
        db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_starts_the_run_when_the_balance_covers_the_required_credits()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);

        WorkflowsController controller = CreateController(db, owner);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Execute_is_never_gated_when_billing_is_off()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        // No credit account at all: a self-host install has no ledger to consult.
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);

        WorkflowsController controller = CreateController(db, owner, billingEnabled: false);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task The_preflight_never_refuses_more_than_the_engine_would_hold()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        // Exactly the cold-start hold of the seeded workflow's steps, in millicredits.
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: SeededRequiredMillis);

        WorkflowsController controller = CreateController(db, owner);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        // A run the caller can exactly afford must start: the preflight's "required" is the sum of the
        // same holds the engine will take, so the two cannot disagree on the boundary.
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // -----------------------------------------------------------------
    // GET {id}/estimate
    // -----------------------------------------------------------------

    [Fact]
    public async Task Estimate_answers_the_range_the_required_credits_and_the_per_step_breakdown()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);

        WorkflowsController controller = CreateController(db, owner);

        ActionResult<WorkflowCostEstimateResponse> result = await controller.Estimate(project.Id, workflow.Id, CancellationToken.None);

        WorkflowCostEstimateResponse estimate = result.Result.Should().BeOfType<OkObjectResult>()
            .Subject.Value.Should().BeOfType<WorkflowCostEstimateResponse>().Subject;

        estimate.BillingEnabled.Should().BeTrue();
        estimate.HasHistory.Should().BeFalse();
        estimate.Steps.Should().HaveCount(2);
        estimate.Steps.Should().OnlyContain(s => s.Basis == nameof(CreditEstimateBasis.Default));
        estimate.LowCredits.Should().BeGreaterThan(0);
        estimate.HighCredits.Should().BeGreaterThan(estimate.LowCredits);
        // the cold-start hold re-plan: on a cold start each step holds the larger of its old default and its share
        // of the low edge of the usual range, so the required figure is never below the displayed low.
        estimate.RequiredCredits.Should().Be(WorkflowsController.ToCredits(SeededRequiredMillis));
        estimate.RequiredCredits.Should().BeGreaterThanOrEqualTo(estimate.LowCredits);
        estimate.BalanceCredits.Should().Be(1_000);
        estimate.Affordable.Should().BeTrue();
    }

    // SEC-EF-1: a preflight that could not read the balance now keeps its estimate (for the approval card).
    // The estimate endpoint still answers its empty "nothing to show" rather than real figures against a
    // balance of zero, and the execute path still fails open.
    [Fact]
    public async Task Estimate_and_execute_when_the_balance_cannot_be_read()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 0);
        WorkflowCostEstimator throwing = new(
            new ThrowingScopes(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

        WorkflowsController controller = CreateController(db, owner, estimator: throwing);

        WorkflowCostEstimateResponse estimate = (await controller.Estimate(project.Id, workflow.Id, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<WorkflowCostEstimateResponse>().Subject;
        estimate.BillingEnabled.Should().BeFalse();
        estimate.Affordable.Should().BeTrue();

        ActionResult<WorkflowExecutionResponse> run = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);
        run.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201, "the preflight fails open");
    }

    [Fact]
    public async Task Estimate_is_404_for_a_project_in_another_workspace()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        Guid stranger = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);

        WorkflowsController controller = CreateController(db, stranger);

        ActionResult<WorkflowCostEstimateResponse> result = await controller.Estimate(project.Id, workflow.Id, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
    }

    // -----------------------------------------------------------------
    // The assistant execute path
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_assistant_execute_is_refused_by_the_same_402()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 0);

        WorkflowAssistantController controller = CreateAssistantController(db, owner);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        var refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);

        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Value));
        body.RootElement.GetProperty("required").GetDouble().Should().BeGreaterThan(0);
        AssertExplainsTheHold(body.RootElement, 0);
        db.WorkflowExecutions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // The assistant's in-chat ExecuteWorkflow TOOL — the one execute path that used to skip the
    // preflight. It must now behave identically to the HTTP paths above.
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_records_credits_exhausted_for_an_unaffordable_run()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 0);
        var log = new ReelBolt.WorkflowEngine.Tests.Services.Telemetry.RecordingProductEventLog();
        var emitter = new ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter(
            log, NullLogger<ReelBolt.Inference.Api.Services.Telemetry.ProductEventEmitter>.Instance);
        AssistantToolProvider provider = CreateToolProvider(
            db, owner, new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true),
            new RecordingExecutionService(db), CreateEstimator(db), emitter);

        for (int i = 0; i < 2; i++)
        {
            await InvokeToolAsync(
                provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
                ("workflowId", workflow.Id), ("userRequest", "make the video"), ("regenerateVideoClips", false));
        }

        log.Rows.Should().ContainSingle().Which.Event.Should().Be("billing.credits_exhausted");
        log.Rows[0].UserId.Should().Be(owner);
    }

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_refuses_an_unaffordable_run_before_anything_is_queued()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 0);

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        // The tool is invoked by name, with the same argument shape the model would send —
        // regenerateVideoClips set, so no paid-clips question stands between the call and the guard.
        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(owner, project.Id),
            "ExecuteWorkflow",
            ("workflowId", workflow.Id),
            ("userRequest", "make the video"),
            ("regenerateVideoClips", false));

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("code").GetString().Should().Be("INSUFFICIENT_CREDITS");
        doc.RootElement.GetProperty("required").GetDouble().Should().Be(WorkflowsController.ToCredits(SeededRequiredMillis));
        doc.RootElement.GetProperty("balance").GetDouble().Should().Be(0);
        doc.RootElement.GetProperty("message").GetString().Should().Contain("credits");
        AssertExplainsTheHold(doc.RootElement, 0);

        // Nothing was queued: the rate-limit slot was never even asked for, and no execution row exists.
        guard.Calls.Should().BeEmpty("an unaffordable run must be refused before a rate-limit slot is spent");
        executionService.Calls.Should().BeEmpty();
        db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_still_starts_a_run_the_balance_covers()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(owner, project.Id),
            "ExecuteWorkflow",
            ("workflowId", workflow.Id),
            ("userRequest", "make the video"),
            ("regenerateVideoClips", false));

        // The run proceeded: the guard consulted once and the recording service (which stands in for
        // the real execution service, which throws in this harness without a bus) was called.
        guard.Calls.Should().ContainSingle();
        executionService.Calls.Should().ContainSingle();
        executionService.Calls[0].Initiator.Should().Be(ExecutionInitiator.Assistant);

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.TryGetProperty("code", out _).Should().BeFalse(
            "an affordable run must not carry the 402 code");
        doc.RootElement.TryGetProperty("INSUFFICIENT_CREDITS", out _).Should().BeFalse();
        // The tool returns a compact receipt with camelCase property names.
        doc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_is_never_gated_when_billing_is_off()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        // No credit account at all: a self-host install has no ledger to consult.
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db, billingEnabled: false));

        string text = await InvokeToolAsync(
            provider,
            new AssistantToolContext(owner, project.Id),
            "ExecuteWorkflow",
            ("workflowId", workflow.Id),
            ("userRequest", "make the video"),
            ("regenerateVideoClips", false));

        // Billing off: the preflight refuses nothing, the run proceeds, and the refusal code is absent.
        guard.Calls.Should().ContainSingle();
        executionService.Calls.Should().ContainSingle();
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.TryGetProperty("code", out _).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // T-012: the assistant never blindly repeats a run that lost narration or overflowed it
    // -----------------------------------------------------------------

    [Fact]
    public async Task ExecuteWorkflow_refuses_a_blind_restart_after_a_narration_incomplete_run()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);
        await SeedFinishedRunAsync(db, project, workflow, ExecutionStatus.Passed,
            attentionJson: ExecutionAttention.ToNarrationJson(1, 8, 5, 2, spokenLines: 2, declaredLines: 4, reanchoredLines: 0),
            userRequest: "make the video");

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("userRequest", "make the video"), ("regenerateVideoClips", false));

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("code").GetString().Should().Be("NARRATION_PROBLEM_PREVIOUS_RUN");
        string message = doc.RootElement.GetProperty("message").GetString()!;
        message.Should()
            .Contain("Only 2 of 4 narration lines were spoken", "the refusal names what went wrong")
            .And.Contain("anchors").And.Contain("change request").And.Contain("different length");
        guard.Calls.Should().BeEmpty("a refusal must not spend a rate-limit slot");
        executionService.Calls.Should().BeEmpty("a refusal must start nothing");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1, "the check writes nothing");
    }

    [Fact]
    public async Task ExecuteWorkflow_refuses_a_blind_restart_after_a_narration_overflow_failure()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);
        await SeedFinishedRunAsync(db, project, workflow, ExecutionStatus.Failed,
            userRequest: "make the video", failedStepCode: "NARRATION_DOES_NOT_FIT");

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("userRequest", "make the video"), ("regenerateVideoClips", false));

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("NARRATION_PROBLEM_PREVIOUS_RUN");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("did not fit its scenes");
        executionService.Calls.Should().BeEmpty();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteWorkflow_allows_a_repeat_after_a_changed_brief()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);
        await SeedFinishedRunAsync(db, project, workflow, ExecutionStatus.Passed,
            attentionJson: ExecutionAttention.ToNarrationJson(1, 8, 5, 2, spokenLines: 2, declaredLines: 4, reanchoredLines: 0),
            runBriefJson: RunBriefJson.Serialize(new StoredRunBrief(LengthSec: 24)));

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("regenerateVideoClips", false),
            ("brief", JsonDocument.Parse("""{"lengthSec":30}""").RootElement.Clone()));

        // A changed length is a deliberate change, so the guard lets it run.
        executionService.Calls.Should().ContainSingle();
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
        doc.RootElement.TryGetProperty("code", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteWorkflow_is_unaffected_when_the_last_run_has_no_narration_problem()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);
        await SeedFinishedRunAsync(db, project, workflow, ExecutionStatus.Passed, userRequest: "make the video");

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("userRequest", "make the video"), ("regenerateVideoClips", false));

        executionService.Calls.Should().ContainSingle();
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task ExecuteWorkflow_allows_an_identical_call_that_names_the_fix()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: 1_000_000);
        await SeedFinishedRunAsync(db, project, workflow, ExecutionStatus.Passed,
            attentionJson: ExecutionAttention.ToNarrationJson(1, 8, 5, 2, spokenLines: 2, declaredLines: 4, reanchoredLines: 0),
            userRequest: "make the video");

        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", workflow.Id), ("userRequest", "re-run and keep every line spoken"), ("regenerateVideoClips", false));

        executionService.Calls.Should().ContainSingle("an instruction naming the fix is a deliberate repeat, not a blind one");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // Retry: scoped to the route's project, and never a back door for revision runs
    // -----------------------------------------------------------------

    private static WorkflowExecution FailedRun(Project project, WorkflowDefinition workflow) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowDefinitionId = workflow.Id,
        ProjectId = project.Id,
        OrganizationId = project.OrganizationId,
        Status = ExecutionStatus.Failed,
        UserRequest = "r",
        QueuedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Retry_of_a_run_from_another_project_is_not_found()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project mine, _) = await SeedAsync(db, owner, balanceMillis: null);
        (Project other, WorkflowDefinition otherWorkflow) = await SeedAsync(db, Guid.NewGuid(), balanceMillis: null);
        WorkflowExecution theirs = FailedRun(other, otherWorkflow);
        db.WorkflowExecutions.Add(theirs);
        await db.SaveChangesAsync();

        // The caller may edit `mine` and names the other project's workflow + run under it.
        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, billingEnabled: false)
            .RetryExecution(mine.Id, otherWorkflow.Id, theirs.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundResult>();
        (await db.WorkflowExecutions.SingleAsync(e => e.Id == theirs.Id)).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_of_a_revision_run_is_refused_so_the_revision_limits_cannot_be_bypassed()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);
        workflow.Kind = WorkflowDefinitionKind.CompositionRevision;
        WorkflowExecution run = FailedRun(project, workflow);
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, billingEnabled: false)
            .RetryExecution(project.Id, workflow.Id, run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        (await db.WorkflowExecutions.SingleAsync(e => e.Id == run.Id)).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_while_the_previous_run_holds_its_lease_is_409_RUN_STILL_STOPPING()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);
        WorkflowExecution run = FailedRun(project, workflow);
        run.Status = ExecutionStatus.Cancelled;
        run.ErrorMessage = "Stopped by user x";
        run.LeaseOwner = "engine-a";
        run.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(60);
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, billingEnabled: false, publish: publish.Object)
            .RetryExecution(project.Id, workflow.Id, run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new { code = "RUN_STILL_STOPPING", message = "This run is still stopping. Try again in a moment." });
        WorkflowExecution after = await db.WorkflowExecutions.SingleAsync(e => e.Id == run.Id);
        after.Status.Should().Be(ExecutionStatus.Cancelled);
        after.LeaseOwner.Should().Be("engine-a");
        publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_after_the_lease_lapsed_is_accepted()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);
        WorkflowExecution run = FailedRun(project, workflow);
        run.Status = ExecutionStatus.Cancelled;
        run.ErrorMessage = "Stopped by user x";
        run.LeaseOwner = "engine-a";
        run.LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-5);
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, billingEnabled: false, publish: publish.Object)
            .RetryExecution(project.Id, workflow.Id, run.Id, null, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(200);
        WorkflowExecution after = await db.WorkflowExecutions.SingleAsync(e => e.Id == run.Id);
        after.Status.Should().Be(ExecutionStatus.Queued);
        after.LeaseOwner.Should().BeNull();
        after.LeaseExpiresAt.Should().BeNull();
        after.ErrorMessage.Should().BeNull();
        publish.Verify(p => p.Publish(It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == run.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retry_of_a_running_run_is_400_RUN_ALREADY_ACTIVE_as_json()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition workflow) = await SeedAsync(db, owner, balanceMillis: null);
        WorkflowExecution run = FailedRun(project, workflow);
        run.Status = ExecutionStatus.Running;
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();
        Mock<IPublishEndpoint> publish = new();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, billingEnabled: false, publish: publish.Object)
            .RetryExecution(project.Id, workflow.Id, run.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new { code = "RUN_ALREADY_ACTIVE", message = "This run is already running or queued." });
        (await db.WorkflowExecutions.SingleAsync(e => e.Id == run.Id)).Status.Should().Be(ExecutionStatus.Running);
        publish.Verify(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------
    // QA T-102: a stale v1 copy of the profile template is refused by every generic execute path, before the
    // preflight and before anything is queued (a v1 product-only fallback also runs, and charges, the style
    // extractor). The balance is 0, so a refusal that came after the preflight would be its 402.
    // -----------------------------------------------------------------

    private static async Task<(Project Project, WorkflowDefinition Profile)> SeedProfileAsync(
        InferenceApiDbContext db, Guid owner, bool stale, long balanceMillis = 0)
    {
        (Project project, WorkflowDefinition other) = await SeedAsync(db, owner, balanceMillis: balanceMillis);
        Guid agentId = other.Steps.First().AgentDefinitionId;
        WorkflowDefinition profile = stale
            ? DesignSystems.Templates.ProfileWorkflowFixtures.V1(project.Id, agentId)
            : DesignSystems.Templates.ProfileWorkflowFixtures.Current(project.Id, agentId);
        if (!stale)
        {
            // The real analyzer agents of the template (CodeStructureAnalyzer, DependencyAnalyzer, ComponentInventoryAnalyzer,
            // RouteAndApiAnalyzer, StyleAndThemeExtractor, ExtractTransform), step for step, not AuthorAgent x5: the
            // estimate is then the reference's, not a 742-credit artifact of the heaviest profile.
            WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
            Dictionary<AgentType, Guid> agents = [];
            foreach (AgentType type in template.Steps.Select(t => t.AgentType).Distinct())
            {
                AgentDefinition definition = new()
                {
                    Id = Guid.NewGuid(),
                    Name = type.ToString(),
                    Description = "d",
                    SystemPrompt = "p",
                    AgentType = type,
                    IsBuiltIn = true,
                    CreatedAt = DateTime.UtcNow
                };
                agents[type] = definition.Id;
                db.AgentDefinitions.Add(definition);
            }

            foreach (WorkflowStep step in profile.Steps)
            {
                step.AgentDefinitionId = agents[template.Steps[step.StepOrder - 1].AgentType];
            }
        }

        db.WorkflowDefinitions.Add(profile);
        await db.SaveChangesAsync();
        return (project, profile);
    }

    private static void ShouldBeOutdated(ActionResult<WorkflowExecutionResponse> result)
    {
        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("PROFILE_WORKFLOW_OUTDATED");
        body.RootElement.GetProperty("message").GetString().Should()
            .Be("This profile workflow is out of date. Use Build profile to run the current version.");
    }

    [Fact]
    public async Task Execute_refuses_a_stale_v1_profile_workflow_with_409_before_the_preflight()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: true);

        ShouldBeOutdated(await CreateController(db, owner).Execute(project.Id, profile.Id, new ExecuteWorkflowRequest(), CancellationToken.None));
        db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_refuses_a_run_of_a_stale_v1_profile_workflow()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: true);
        WorkflowExecution past = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = profile.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow
        };
        db.WorkflowExecutions.Add(past);
        await db.SaveChangesAsync();

        ShouldBeOutdated(await CreateController(db, owner).RetryExecution(project.Id, profile.Id, past.Id, new ExecuteWorkflowRequest(), CancellationToken.None));
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1, "the past run stays visible and nothing new is queued");
    }

    [Fact]
    public async Task The_assistant_execute_refuses_a_stale_v1_profile_workflow()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: true);

        ShouldBeOutdated(await CreateAssistantController(db, owner).Execute(project.Id, profile.Id, new ExecuteWorkflowRequest(), CancellationToken.None));
        db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_assistant_ExecuteWorkflow_tool_refuses_a_stale_v1_profile_workflow_before_anything_is_queued()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: true);
        var guard = new RecordingGuard(db, config: new ConfigurationBuilder().Build(), grant: true);
        var executionService = new RecordingExecutionService(db);
        AssistantToolProvider provider = CreateToolProvider(db, owner, guard, executionService, CreateEstimator(db));

        string text = await InvokeToolAsync(
            provider, new AssistantToolContext(owner, project.Id), "ExecuteWorkflow",
            ("workflowId", profile.Id), ("userRequest", "build it"), ("regenerateVideoClips", false));

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("code").GetString().Should().Be("PROFILE_WORKFLOW_OUTDATED");
        doc.RootElement.GetProperty("error").GetString().Should().Contain("out of date");
        guard.Calls.Should().BeEmpty();
        executionService.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_without_the_estimate_accepted_refuses_the_current_profile_workflow_with_409()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: false, balanceMillis: 10_000_000);

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, designBuildGate: ProfileGate(db)).Execute(
            project.Id, profile.Id, new ExecuteWorkflowRequest(), CancellationToken.None);

        ConflictObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(conflict.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().Contain("ESTIMATE_NOT_ACCEPTED");
        db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_runs_the_current_profile_workflow_normally()
    {
        using InferenceApiDbContext db = NewContext();
        Guid owner = Guid.NewGuid();
        (Project project, WorkflowDefinition profile) = await SeedProfileAsync(db, owner, stale: false, balanceMillis: 10_000_000);

        // The profile build must have its estimate accepted: read the high figure from the refusal, then accept it.
        ActionResult<WorkflowExecutionResponse> refused = await CreateController(db, owner, designBuildGate: ProfileGate(db)).Execute(
            project.Id, profile.Id, new ExecuteWorkflowRequest(), CancellationToken.None);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(
            refused.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        double high = body.RootElement.GetProperty("estimate").GetProperty("high").GetDouble();

        ActionResult<WorkflowExecutionResponse> result = await CreateController(db, owner, designBuildGate: ProfileGate(db)).Execute(
            project.Id, profile.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: Math.Ceiling(high)), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    /// <summary>Product decision 5: the 402 keeps the p90 hold as <c>required</c> and explains it, with the shortfall as <c>missing</c>.</summary>
    private static void AssertExplainsTheHold(JsonElement body, double balance)
    {
        double required = body.GetProperty("required").GetDouble();
        double missing = CreditShortfallMessage.MissingCreditsFromCredits(required, balance);
        body.GetProperty("missing").GetDouble().Should().Be(missing);
        string message = body.GetProperty("message").GetString()!;
        message.Should().StartWith($"This run needs up to {CreditCostWording.Up(required)} credits to start")
            .And.Contain($"so {missing:0.#} more are needed")
            .And.EndWith("Credits that aren't used are returned when the run finishes.");
    }

    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>
    /// The cold-start hold of the seeded workflow (the cold-start hold re-plan): each of its one AuthorAgent
    /// step and one VideoCompile step holds the larger of B0's default low and its share of the low edge
    /// of the run's usual range, priced at the seed constants (this harness registers no provider
    /// resolver or rate-card service).
    /// </summary>
    private static long SeededRequiredMillis
    {
        get
        {
            RunStepInput[] cold =
            [
                new RunStepInput(1, ColdStartStepPricing.Price(StepUsageProfiles.For(StepType.Agent, AgentType.AuthorAgent), null, SeedProfilePricer.Instance).Cost, 0),
                new RunStepInput(2, ColdStartStepPricing.Price(StepUsageProfiles.For(StepType.VideoCompile, AgentType.AuthorAgent), null, SeedProfilePricer.Instance).Cost, 0)
            ];
            IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(cold, []);
            return Math.Max(StepCreditDefaults.MillisFor(StepType.Agent).Low, shares[1])
                + Math.Max(StepCreditDefaults.MillisFor(StepType.VideoCompile).Low, shares[2]);
        }
    }

    /// <summary>A finished run already on record, as the assistant's restart guard reads it.</summary>
    private static async Task<WorkflowExecution> SeedFinishedRunAsync(
        InferenceApiDbContext db,
        Project project,
        WorkflowDefinition workflow,
        ExecutionStatus status,
        string? attentionJson = null,
        string? userRequest = null,
        string? runBriefJson = null,
        string? failedStepCode = null)
    {
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            UserRequest = userRequest,
            AttentionJson = attentionJson,
            RunBriefJson = runBriefJson
        };
        db.WorkflowExecutions.Add(execution);
        if (failedStepCode is not null)
        {
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                WorkflowStepId = workflow.Steps.First().Id,
                Status = StepStatus.Failed,
                FailureCode = failedStepCode,
                ExecutedAt = DateTime.UtcNow.AddMinutes(-2)
            });
        }

        await db.SaveChangesAsync();
        return execution;
    }

    /// <summary>A project owned by <paramref name="owner"/> with a two-step workflow, and a credit account.</summary>
    private static async Task<(Project Project, WorkflowDefinition Workflow)> SeedAsync(
        InferenceApiDbContext db, Guid owner, long? balanceMillis)
    {
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = owner,
            OrganizationId = org,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = "AuthorAgent",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = true,
            CreatedAt = DateTime.UtcNow
        };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "w",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = agent.Id }
            ]
        };

        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        if (balanceMillis is { } balance)
        {
            db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balance });
        }

        await db.SaveChangesAsync();
        return (project, workflow);
    }

    private static WorkflowsController CreateController(
        InferenceApiDbContext db, Guid ownerId, bool billingEnabled = true, IWorkflowCostEstimator? estimator = null,
        IPublishEndpoint? publish = null, IDesignBuildEstimateGate? designBuildGate = null)
    {
        var user = new FakeCurrentUser(ownerId);
        WorkflowTemplateProvisioningService templateService = new(
            db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());
        IWorkflowExecutionService executionService = new WorkflowExecutionService(
            db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance);

        return new WorkflowsController(
            db,
            user,
            TestTenancy.Access(db, user),
            publish ?? Mock.Of<IPublishEndpoint>(),
            templateService,
            executionService,
            NullLogger<WorkflowsController>.Instance,
            costEstimator: estimator ?? CreateEstimator(db, billingEnabled),
            designBuildGate: designBuildGate);
    }

    /// <summary>
    /// The shared profile-build gate over the real estimator, priced at the real catalog rows of a deepseek-flash
    /// default Chat model, with the shipped dynamic cap (the reference high x 1.3). The seeded profile workflow
    /// uses the template's own agents, so its estimate is the reference's and sits under that cap.
    /// </summary>
    private static IDesignBuildEstimateGate ProfileGate(InferenceApiDbContext db) => new DesignBuildEstimateGate(
        db,
        new WorkflowCostEstimator(
            DesignSystems.Api.CatalogScopes.For(db, InferenceProviderKind.DeepSeek, "deepseek-flash"),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance),
        new ConfigurationBuilder().Build());

    private static WorkflowAssistantController CreateAssistantController(
        InferenceApiDbContext db, Guid ownerId, bool billingEnabled = true)
    {
        var user = new FakeCurrentUser(ownerId);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();

        return new WorkflowAssistantController(
            db,
            user,
            TestTenancy.Access(db, user),
            new WorkflowEditDiffService(db),
            new AssistantExecutionGuard(db, config),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            costEstimator: CreateEstimator(db, billingEnabled));
    }

    /// <summary>
    /// The real estimator, over the same in-memory context and with billing's real config section, so
    /// these tests exercise the shipped code path rather than a stub standing in for it.
    /// </summary>
    private static WorkflowCostEstimator CreateEstimator(InferenceApiDbContext db, bool billingEnabled = true) => new(
        new SingleContextScopes(db),
        Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = billingEnabled }),
        Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
        NullLogger<WorkflowCostEstimator>.Instance);

    /// <summary>
    /// Builds the assistant tool provider the way the chat transport does, but over the REAL
    /// estimator (or a stub) rather than a mock execution service — the same in-memory context the
    /// test seeded, so the preflight reads the workflow's steps and the credit account the test put
    /// there. The execution service is a recording stub (the real one publishes to a bus these
    /// tests do not stand up); the guard is a stub that always grants, so the preflight — not the
    /// rate limit — is the only thing that can stop the run.
    /// </summary>
    private static AssistantToolProvider CreateToolProvider(
        InferenceApiDbContext db,
        Guid ownerId,
        IAssistantExecutionGuard guard,
        IWorkflowExecutionService executionService,
        IWorkflowCostEstimator? costEstimator,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        var user = new FakeCurrentUser(ownerId);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();

        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            executionService,
            guard,
            user,
            new AssistantProjectTools(db, tokens,
                new WorkflowTemplateProvisioningService(
                    db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
                user, TestTenancy.Access(db, user)),
            new AssistantFileTools(db, tokens, new ReelBolt.WorkflowEngine.Tests.Agents.FakeFileStorage(), Mock.Of<IPublishEndpoint>(), new ReelBolt.WorkflowEngine.Tests.Agents.RecordingSummarizationQueue(), TestTenancy.Access(db, user)),
            new AssistantWorkflowTools(db, tokens, TestTenancy.Access(db, user)),
            new AssistantAdminTools(db, tokens, user, Mock.Of<ISecretProtector>()),
            new AssistantKnowledgeTools(db, new ReelBolt.WorkflowEngine.Tests.Agents.FakePlatformDocsSearch(),
                new WorkflowTemplateProvisioningService(
                    db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())),
            new AssistantStyleTools(db, tokens, TestTenancy.Access(db, user)),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.DesignSystemTools(db, user, tokens),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.PictureTools(db, user, tokens),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Outputs,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Uploads,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Runs,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).Revisions,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(db, user, tokens).ChangeRequests,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).Patch,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).PictureDocuments,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(db, user, tokens).GreenScreen,
            TestTenancy.Access(db, user),
            null,
            null,
            new WorkflowExecutionPreflight(db, costEstimator),
            productEvents: productEvents);
    }

    /// <summary>Invokes one registered tool BY NAME, exactly as the model's tool call would.</summary>
    private static async Task<string> InvokeToolAsync(
        AssistantToolProvider provider,
        AssistantToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);

        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        object? raw = await tool.InvokeAsync(args);

        return raw switch
        {
            null => string.Empty,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
            JsonElement element => element.GetRawText(),
            _ => raw.ToString()!
        };
    }

    /// <summary>
    /// A recording <see cref="IAssistantExecutionGuard"/> stub. On grant it runs the callback it was
    /// handed — the only thing that may cause an execution — so "the guard owns the insert" is
    /// observable rather than assumed.
    /// </summary>
    private sealed class RecordingGuard(InferenceApiDbContext db, IConfiguration config, bool grant) : IAssistantExecutionGuard
    {
        public List<(Guid UserId, Guid ProjectId)> Calls { get; } = new();

        public int CallbackInvocations { get; private set; }

        public async Task<RunRateDecision> TryAcquireAsync(
            Guid userId,
            Guid projectId,
            RunRateBucket bucket,
            string source,
            Func<Task<Guid?>> insertCallback,
            CancellationToken ct)
        {
            Calls.Add((userId, projectId));

            if (!grant)
                return new RunRateDecision(false, bucket, 10, 10, 3600, 60);

            CallbackInvocations++;
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    /// <summary>
    /// A recording <see cref="IWorkflowExecutionService"/> stub. The real one publishes a bus event
    /// and reads rate cards these tests do not stand up, so it throws here — which is exactly the
    /// shape of the tool's own `catch (Exception)` around the guard's insert callback: the refusal
    /// the test asserts on (the 402 code) happens BEFORE the guard is ever asked, so a thrown
    /// insert surfaces as the tool's own error string, never as the credit code.
    /// </summary>
    private sealed class RecordingExecutionService(InferenceApiDbContext db) : IWorkflowExecutionService
    {

        // Only system-workflow owners (the editor's render endpoint) queue by kind; no execute path
        // under test here does.
        public Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips,
            RunChoices? runChoices,
            WorkflowDefinitionKind kind,
            Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
            => throw new NotSupportedException("The execute paths under test never queue by workflow kind.");
        public List<(Guid ProjectId, Guid WorkflowId, Guid InitiatedByUserId, string? UserRequest, ExecutionInitiator Initiator)> Calls { get; } = new();

        public async Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId,
            Guid workflowId,
            Guid initiatedByUserId,
            string? userRequest,
            ExecutionInitiator initiator,
            CancellationToken ct,
            bool regenerateVideoClips = false,
            RunChoices? runChoices = null)
        {
            Calls.Add((projectId, workflowId, initiatedByUserId, userRequest, initiator));

            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Initiator = initiator,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                UserRequest = userRequest
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);

            // The real service would publish a bus event here; this harness has no bus, so it
            // reports the fact the caller can observe the run happened.
            return new WorkflowExecutionResponse(
                execution.Id,
                workflowId,
                execution.Status.ToString(),
                execution.StartedAt,
                null,
                0,
                null,
                execution.CorrelationId,
                "no bus in this harness",
                new List<StepResultResponse>(),
                new List<ReviewScoreResponse>(),
                userRequest);
        }
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    /// <summary>A scope factory that cannot open a scope: the estimate degrades to cold start, the balance read throws.</summary>
    private sealed class ThrowingScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("the database is unreachable");
    }

    /// <summary>A scope factory handing every caller the one context the test seeded.</summary>
    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);
            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
