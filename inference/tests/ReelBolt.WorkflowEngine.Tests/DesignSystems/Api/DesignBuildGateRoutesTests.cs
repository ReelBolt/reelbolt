using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// Build estimate gate: the SAME dynamic cap (the reference high x 1.3; the fakes here make it 231 x 1.3 = 300.3 credits) and accept-the-estimate check on EVERY route that starts
/// the product-profile-from-code template. One group per route: generic Execute, Retry, the
/// workflow-assistant execute, the assistant tool in chat and over MCP, and the approval card. Each
/// refusal asserts the status/code or refusal text AND that no execution row was created or re-queued;
/// each accepted run proceeds. An ordinary workflow is untouched on every route.
/// </summary>
public sealed partial class DesignBuildGateRoutesTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase($"design-build-gate-{Guid.NewGuid():N}")
        .Options);

    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;
    private readonly WorkflowDefinition _template;
    private readonly WorkflowDefinition _ordinary;

    public DesignBuildGateRoutesTests()
    {
        Guid org = TestTenancy.OrgOf(_owner);
        _project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Spring promo",
            OwnerId = _owner,
            OrganizationId = org,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(_project);

        WorkflowTemplateDefinition def = WorkflowTemplateCatalog
            .GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
        IEnumerable<AgentType> types = def.Steps.Select(s => s.AgentType)
            .Concat(def.Steps.Where(s => s.ParallelAgentTypes is { Count: > 0 }).SelectMany(s => s.ParallelAgentTypes!))
            .Distinct();
        foreach (AgentType type in types)
        {
            _db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = type.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = type,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        _db.SaveChanges();

        _template = new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())
            .CreateFromTemplateAsync(_project.Id, def.Key, skipIfExists: true, CancellationToken.None).GetAwaiter().GetResult()!;

        Guid agentId = _db.AgentDefinitions.First().Id;
        _ordinary = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Ordinary",
            ProjectId = _project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps = [new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = global::ReelBolt.Shared.Data.Models.StepType.Agent, AgentDefinitionId = agentId }],
        };
        _db.WorkflowDefinitions.Add(_ordinary);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------
    // The gate itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_does_not_apply_to_an_ordinary_workflow_and_never_prices_it()
    {
        FakeEstimator estimator = Priced(0, 999_000);

        DesignBuildGateResult result = await Gate(estimator).CheckAsync(
            _project.OrganizationId, _project.Id, _ordinary.Id, null, CancellationToken.None);

        result.Applies.Should().BeFalse();
        result.Refused.Should().BeFalse();
        estimator.EstimateCalls.Should().Be(0);
    }

    [Fact]
    public async Task Gate_applies_to_the_template_and_refuses_over_the_cap_with_the_numbers()
    {
        DesignBuildGateResult result = await Gate(Priced(0, 301_000)).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 1000, CancellationToken.None);

        result.Applies.Should().BeTrue();
        result.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        result.High.Should().Be(301);
        result.CapCredits.Should().BeApproximately(300.3, 0.001);
        result.Code.Should().Be("ESTIMATE_OVER_CAP");
        result.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Gate_passes_a_figure_of_231_under_the_reference_cap_of_300_3_once_accepted()
    {
        IDesignBuildEstimateGate gate = Gate(Priced(0, 231_000));

        (await gate.CheckAsync(_project.OrganizationId, _project.Id, _template.Id, 230, CancellationToken.None))
            .Outcome.Should().Be(DesignBuildGateOutcome.NotAccepted);
        (await gate.CheckAsync(_project.OrganizationId, _project.Id, _template.Id, 231, CancellationToken.None))
            .Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task Gate_fails_closed_when_billing_is_on_and_the_estimate_is_unavailable()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;

        DesignBuildGateResult result = await Gate(estimator).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 10, CancellationToken.None);

        result.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
        result.StatusCode.Should().Be(503);
        result.Code.Should().Be("ESTIMATE_UNAVAILABLE");
    }

    [Fact]
    public async Task Gate_fails_closed_when_the_reference_is_priced_from_the_seed_constants()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReferencePricedFromSeed = true;

        DesignBuildGateResult result = await Gate(estimator).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 10, CancellationToken.None);

        result.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
        result.StatusCode.Should().Be(503);
        result.Code.Should().Be("ESTIMATE_UNAVAILABLE");
    }

    [Fact]
    public async Task Execute_with_the_reference_priced_from_the_seed_constants_returns_503_and_queues_nothing()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReferencePricedFromSeed = true;

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(estimator)
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult result = r.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(503);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Gate_is_ok_when_billing_is_off()
    {
        FakeEstimator estimator = Priced(0, 999_000);
        estimator.BillingEnabled = false;

        DesignBuildGateResult result = await Gate(estimator).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, null, CancellationToken.None);

        result.Applies.Should().BeTrue();
        result.Outcome.Should().Be(DesignBuildGateOutcome.Ok);
    }

    [Fact]
    public async Task Gate_reads_the_cap_from_configuration()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DesignSystem:Build:MaxFallbackCreditsHigh"] = "100" }).Build();

        DesignBuildGateResult result = await new DesignBuildEstimateGate(_db, Priced(0, 150_000), config).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 150, CancellationToken.None);

        result.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        result.CapCredits.Should().Be(100);
    }

    // ---------------------------------------------------------------------------------------------
    // Generic Execute
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Execute_over_cap_returns_409_ESTIMATE_OVER_CAP_and_queues_nothing()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 301_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_OVER_CAP");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_without_acceptance_returns_409_ESTIMATE_NOT_ACCEPTED_with_the_estimate()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(5_000, 231_000))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        JsonObject body = ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        body["estimate"]!["high"]!.GetValue<double>().Should().Be(231);
        body["capCredits"]!.GetValue<double>().Should().BeApproximately(300.3, 0.001);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_with_the_estimator_unavailable_returns_503_ESTIMATE_UNAVAILABLE()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(estimator)
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult obj = r.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(503);
        Body(obj.Value)["code"]!.GetValue<string>().Should().Be("ESTIMATE_UNAVAILABLE");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_with_acceptEstimateCredits_at_least_the_high_figure_starts_the_run()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        _db.WorkflowExecutions.Should().ContainSingle();
    }

    [Fact]
    public async Task Execute_of_an_ordinary_workflow_ignores_the_gate_even_with_a_huge_estimate()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 999_000))
            .Execute(_project.Id, _ordinary.Id, null, CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        _db.WorkflowExecutions.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------------------------
    // Retry
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_over_cap_returns_409_ESTIMATE_OVER_CAP_and_leaves_the_run_failed()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 301_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_OVER_CAP");
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_without_acceptance_returns_409_ESTIMATE_NOT_ACCEPTED_and_leaves_the_run_failed()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_with_the_estimator_unavailable_returns_503_and_leaves_the_run_failed()
    {
        WorkflowExecution run = FailedRun(_template);
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(estimator)
            .RetryExecution(_project.Id, _template.Id, run.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_with_acceptEstimateCredits_at_least_the_high_figure_requeues_the_run()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        r.Result.Should().NotBeOfType<ConflictObjectResult>();
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Queued);
    }

    [Fact]
    public async Task Retry_of_an_ordinary_workflow_ignores_the_gate()
    {
        WorkflowExecution run = FailedRun(_ordinary);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 999_000))
            .RetryExecution(_project.Id, _ordinary.Id, run.Id, null, CancellationToken.None);

        r.Result.Should().NotBeOfType<ConflictObjectResult>();
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Queued);
    }

    // ---------------------------------------------------------------------------------------------
    // The workflow-assistant REST execute (initiator Assistant, no card)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Workflow_assistant_execute_without_acceptance_returns_409_and_queues_nothing()
    {
        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Priced(0, 231_000))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_assistant_execute_with_acceptance_runs_and_an_ordinary_workflow_is_untouched()
    {
        FakeEstimator estimator = Priced(0, 231_000);

        (await AssistantCtl(estimator).Execute(_project.Id, _template.Id,
                new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        (await AssistantCtl(estimator).Execute(_project.Id, _ordinary.Id, null, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    // ---------------------------------------------------------------------------------------------
    // Assistant tool in CHAT (approving the card / auto mode is acceptance; the cap still applies)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Chat_tool_runs_the_template_without_a_figure_because_approval_is_acceptance()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 231_000), exec, ExecutionInitiator.Assistant, _template.Id);

        exec.Calls.Should().ContainSingle().Which.Initiator.Should().Be(ExecutionInitiator.Assistant);
        JsonDocument.Parse(text).RootElement.GetProperty("status").GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task Chat_tool_over_cap_refuses_with_the_numbers_and_runs_nothing()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 301_000), exec, ExecutionInitiator.Assistant, _template.Id);

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_OVER_CAP");
        doc.RootElement.GetProperty("message").GetString().Should().Contain("301").And.Contain("300");
        exec.Calls.Should().BeEmpty();
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_tool_refuses_when_the_estimate_is_unavailable()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(estimator, exec, ExecutionInitiator.Assistant, _template.Id);

        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_UNAVAILABLE");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_approval_card_for_the_template_shows_the_estimate_range_and_the_cap()
    {
        FakeEstimator estimator = Priced(5_000, 231_000);
        var builder = new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, new FakeUser(_owner)), new FakeUser(_owner), estimator,
            designBuildGate: Gate(estimator));

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ExecuteWorkflow", Args(_template.Id), new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        string changes = string.Join("\n", verdict.Card!.Changes!.Select(c => c.Text));
        changes.Should().Contain("5").And.Contain("231").And.Contain("300").And.Contain("approving");
    }

    [Fact]
    public async Task Chat_approval_card_for_an_ordinary_workflow_carries_no_estimate_line()
    {
        FakeEstimator estimator = Priced(5_000, 231_000);
        var builder = new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, new FakeUser(_owner)), new FakeUser(_owner), estimator,
            designBuildGate: Gate(estimator));

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ExecuteWorkflow", Args(_ordinary.Id), new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        (verdict.Card!.Changes ?? []).Select(c => c.Text).Should().NotContain(t => t.Contains("cap"));
    }

    [Fact]
    public async Task Chat_tool_leaves_an_ordinary_workflow_untouched()
    {
        RecordingExecutionService exec = new(_db);

        await InvokeTool(Priced(0, 999_000), exec, ExecutionInitiator.Assistant, _ordinary.Id);

        exec.Calls.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------------------------
    // Assistant tool over MCP (no card: explicit acceptance required)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mcp_tool_without_a_figure_is_refused_with_the_estimate_and_how_to_accept()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(5_000, 231_000), exec, ExecutionInitiator.Mcp, _template.Id);

        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_NOT_ACCEPTED");
        doc.RootElement.GetProperty("message").GetString().Should()
            .Contain("estimate 5-231 credits").And.Contain("acceptEstimateCredits=231");
        exec.Calls.Should().BeEmpty();
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_tool_with_a_figure_at_least_the_high_estimate_runs_as_Mcp()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 231_000), exec, ExecutionInitiator.Mcp, _template.Id, accept: 231);

        exec.Calls.Should().ContainSingle().Which.Initiator.Should().Be(ExecutionInitiator.Mcp);
        JsonDocument.Parse(text).RootElement.GetProperty("status").GetString().Should().Be("Queued");
    }

    [Fact]
    public async Task Mcp_tool_over_cap_refuses_even_with_a_figure()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 301_000), exec, ExecutionInitiator.Mcp, _template.Id, accept: 301);

        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_OVER_CAP");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_tool_with_a_low_figure_is_refused_as_not_accepted()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 231_000), exec, ExecutionInitiator.Mcp, _template.Id, accept: 100);

        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_NOT_ACCEPTED");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_tool_leaves_an_ordinary_workflow_untouched()
    {
        RecordingExecutionService exec = new(_db);

        await InvokeTool(Priced(0, 999_000), exec, ExecutionInitiator.Mcp, _ordinary.Id);

        exec.Calls.Should().ContainSingle();
    }

    // ---------------------------------------------------------------------------------------------
    // The build is recognised by its step SHAPE, never by an editable name
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_still_applies_to_a_renamed_template_and_a_user_edited_clone()
    {
        Rename(_template);
        WorkflowDefinition clone = Shape("My own build", compose: true, agents: 2);

        IDesignBuildEstimateGate gate = Gate(Priced(0, 301_000));

        (await gate.CheckAsync(_project.OrganizationId, _project.Id, _template.Id, 1000, CancellationToken.None))
            .Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        (await gate.CheckAsync(_project.OrganizationId, _project.Id, clone.Id, 1000, CancellationToken.None))
            .Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
    }

    [Fact]
    public async Task Gate_does_not_apply_to_compose_without_an_agent_step()
    {
        WorkflowDefinition w = Shape("Compose only", compose: true, agents: 0);

        (await Gate(Priced(0, 999_000)).CheckAsync(_project.OrganizationId, _project.Id, w.Id, null, CancellationToken.None))
            .Applies.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_does_not_apply_to_agent_steps_without_a_compose_step()
    {
        WorkflowDefinition noDs = Shape("Agents only", compose: false, agents: 3);
        WorkflowDefinition parseOnly = Shape("Parse and agents", compose: false, agents: 3, parse: true);
        IDesignBuildEstimateGate gate = Gate(Priced(0, 999_000));

        (await gate.CheckAsync(_project.OrganizationId, _project.Id, noDs.Id, null, CancellationToken.None)).Applies.Should().BeFalse();
        (await gate.CheckAsync(_project.OrganizationId, _project.Id, parseOnly.Id, null, CancellationToken.None)).Applies.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_never_applies_to_a_hidden_system_kind_even_with_the_build_shape()
    {
        WorkflowDefinition w = Shape("Hidden", compose: true, agents: 2, kind: WorkflowDefinitionKind.TimelineRender);

        (await Gate(Priced(0, 999_000)).CheckAsync(_project.OrganizationId, _project.Id, w.Id, null, CancellationToken.None))
            .Applies.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_treats_an_invalid_compose_config_as_not_a_build()
    {
        WorkflowDefinition w = Shape("Broken", compose: true, agents: 2);
        foreach (WorkflowStep step in _db.WorkflowSteps.Where(x => x.WorkflowDefinitionId == w.Id && x.StepType == global::ReelBolt.Shared.Data.Models.StepType.DesignSystem))
            step.DesignSystemConfigJson = "{not json";
        _db.SaveChanges();

        (await Gate(Priced(0, 999_000)).CheckAsync(_project.OrganizationId, _project.Id, w.Id, null, CancellationToken.None))
            .Applies.Should().BeFalse();
    }

    [Fact]
    public async Task Renamed_template_is_still_gated_on_Execute()
    {
        Rename(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 301_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_OVER_CAP");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Renamed_template_is_still_gated_on_Retry()
    {
        Rename(_template);
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        (await _db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Renamed_template_is_still_gated_on_the_workflow_assistant_execute()
    {
        Rename(_template);

        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Priced(0, 231_000))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Renamed_template_is_still_gated_in_chat_and_over_mcp()
    {
        Rename(_template);
        RecordingExecutionService exec = new(_db);

        string chat = await InvokeTool(Priced(0, 301_000), exec, ExecutionInitiator.Assistant, _template.Id);
        string mcp = await InvokeTool(Priced(0, 231_000), exec, ExecutionInitiator.Mcp, _template.Id);

        JsonDocument.Parse(chat).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_OVER_CAP");
        JsonDocument.Parse(mcp).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_NOT_ACCEPTED");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Renamed_template_card_still_shows_the_estimate_and_the_cap()
    {
        Rename(_template);
        FakeEstimator estimator = Priced(5_000, 231_000);
        var builder = new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, new FakeUser(_owner)), new FakeUser(_owner), estimator,
            designBuildGate: Gate(estimator));

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ExecuteWorkflow", Args(_template.Id), new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        string.Join("\n", verdict.Card!.Changes!.Select(c => c.Text)).Should().Contain("231").And.Contain("300");
    }

    [Fact]
    public async Task Assistant_initiator_without_the_chat_entry_point_flag_is_not_silently_accepted()
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 231_000), exec, ExecutionInitiator.Assistant, _template.Id, chatApproved: false);

        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_NOT_ACCEPTED");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_refusal_prints_the_figure_rounded_up_so_the_retry_is_accepted()
    {
        RecordingExecutionService exec = new(_db);
        FakeEstimator estimator = Priced(0, 231_504);

        string refused = await InvokeTool(estimator, exec, ExecutionInitiator.Mcp, _template.Id);
        JsonDocument.Parse(refused).RootElement.GetProperty("message").GetString().Should().Contain("acceptEstimateCredits=231.51");

        await InvokeTool(estimator, exec, ExecutionInitiator.Mcp, _template.Id, accept: 231.51);
        exec.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Rest_message_prints_the_figure_rounded_up_and_that_figure_is_accepted()
    {
        FakeEstimator estimator = Priced(0, 231_504);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(estimator)
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);
        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED")["message"]!.GetValue<string>().Should().Contain("231.51");

        (await WorkflowsCtl(estimator).Execute(
                _project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231.51), CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task Gate_rejects_NaN_and_negative_figures_and_accepts_infinity()
    {
        IDesignBuildEstimateGate gate = Gate(Priced(0, 10_000));

        async Task<DesignBuildGateOutcome> Check(double v) =>
            (await gate.CheckAsync(_project.OrganizationId, _project.Id, _template.Id, v, CancellationToken.None)).Outcome;

        (await Check(double.NaN)).Should().Be(DesignBuildGateOutcome.NotAccepted);
        (await Check(-5)).Should().Be(DesignBuildGateOutcome.NotAccepted);
        (await Check(double.PositiveInfinity)).Should().Be(DesignBuildGateOutcome.Ok);
        (await Check(9.996)).Should().Be(DesignBuildGateOutcome.Ok);
    }

    // ---------------------------------------------------------------------------------------------
    // QA follow-ups: fail closed on a seed-priced ACTUAL estimate, refusals on every route, ceiling and margin edges
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_fails_closed_when_the_actual_estimate_is_priced_from_the_seed_constants()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ActualPricedFromSeed = true;

        DesignBuildGateResult result = await Gate(estimator).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 10, CancellationToken.None);

        result.Outcome.Should().Be(DesignBuildGateOutcome.Unavailable);
        result.StatusCode.Should().Be(503);
        result.Code.Should().Be("ESTIMATE_UNAVAILABLE");
    }

    [Fact]
    public async Task Execute_with_the_actual_estimate_priced_from_the_seed_constants_returns_503_and_queues_nothing()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ActualPricedFromSeed = true;

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(estimator)
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        r.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_assistant_execute_over_cap_returns_409_ESTIMATE_OVER_CAP_and_queues_nothing()
    {
        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Priced(0, 301_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 301), CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_OVER_CAP");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_assistant_execute_with_the_estimator_unavailable_returns_503_and_queues_nothing()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;

        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(estimator)
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 10), CancellationToken.None);

        ObjectResult obj = r.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(503);
        Body(obj.Value)["code"]!.GetValue<string>().Should().Be("ESTIMATE_UNAVAILABLE");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_tool_refuses_with_ESTIMATE_UNAVAILABLE_when_the_estimator_cannot_price_the_build()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(estimator, exec, ExecutionInitiator.Mcp, _template.Id, accept: 10);

        JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().Should().Be("ESTIMATE_UNAVAILABLE");
        exec.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_approval_card_says_the_run_will_not_start_when_over_the_cap()
    {
        FakeEstimator estimator = Priced(5_000, 301_000);
        var builder = new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, new FakeUser(_owner)), new FakeUser(_owner), estimator,
            designBuildGate: Gate(estimator));

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ExecuteWorkflow", Args(_template.Id), new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        string changes = string.Join("\n", verdict.Card!.Changes!.Select(c => c.Text));
        changes.Should().Contain("This run will not start").And.Contain("300");
        changes.Should().NotContain("approving this run accepts it");
    }

    [Fact]
    public async Task Chat_approval_card_says_the_run_will_not_start_when_the_estimate_is_unavailable()
    {
        FakeEstimator estimator = Priced(0, 10_000);
        estimator.ReturnDisabledEstimate = true;
        var builder = new AssistantApprovalCardBuilder(
            _db, TestTenancy.Access(_db, new FakeUser(_owner)), new FakeUser(_owner), estimator,
            designBuildGate: Gate(estimator));

        ApprovalVerdict verdict = await builder.BuildAsync(
            "ExecuteWorkflow", Args(_template.Id), new AssistantToolContext(_owner, _project.Id), null, CancellationToken.None);

        string.Join("\n", verdict.Card!.Changes!.Select(c => c.Text)).Should().Contain("This run will not start");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("NaN")]
    public async Task An_unreadable_or_negative_ceiling_caps_at_zero_and_refuses_over_cap(string ceiling)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DesignSystem:Build:MaxFallbackCreditsHigh"] = ceiling }).Build();

        DesignBuildGateResult result = await new DesignBuildEstimateGate(_db, Priced(0, 1_000), config).CheckAsync(
            _project.OrganizationId, _project.Id, _template.Id, 1_000, CancellationToken.None);

        result.Outcome.Should().Be(DesignBuildGateOutcome.OverCap);
        result.CapCredits.Should().Be(0);
    }

    [Theory]
    [InlineData("1e308")]
    [InlineData("1000000")]
    public void A_huge_margin_is_clamped_to_ten_so_the_cap_cannot_overflow_or_vanish(string margin)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DesignSystem:Build:FallbackCapMargin"] = margin }).Build();

        double cap = DesignBuildEstimateGate.CapFor(231, config);

        cap.Should().Be(2310);
    }

    private void Rename(WorkflowDefinition workflow)
    {
        _db.WorkflowDefinitions.Single(w => w.Id == workflow.Id).Name = "Renamed by the user";
        _db.SaveChanges();
    }

    private WorkflowDefinition Shape(
        string name, bool compose, int agents, bool parse = false, WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard)
    {
        Guid agentId = _db.AgentDefinitions.First().Id;
        var w = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProjectId = _project.Id,
            Kind = kind,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        int order = 1;
        if (parse)
            w.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = order++,
                StepType = global::ReelBolt.Shared.Data.Models.StepType.DesignSystem,
                AgentDefinitionId = agentId,
                DesignSystemConfigJson = """{"op":"Parse","version":1,"parse":{}}""",
            });
        for (int i = 0; i < agents; i++)
            w.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = order++, StepType = global::ReelBolt.Shared.Data.Models.StepType.Agent, AgentDefinitionId = agentId });
        if (compose)
            w.Steps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = order++,
                StepType = global::ReelBolt.Shared.Data.Models.StepType.DesignSystem,
                AgentDefinitionId = agentId,
                DesignSystemConfigJson = """{"op":"Compose","version":1,"compose":{"parseStepOrder":1}}""",
            });
        _db.WorkflowDefinitions.Add(w);
        _db.SaveChanges();
        return w;
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private static FakeEstimator Priced(long lowMillis, long highMillis) => new()
    {
        LowMillis = lowMillis,
        HighMillis = highMillis,
        RequiredMillis = lowMillis,
    };

    private IDesignBuildEstimateGate Gate(FakeEstimator estimator) =>
        new DesignBuildEstimateGate(_db, estimator, new ConfigurationBuilder().Build());

    private WorkflowExecution FailedRun(WorkflowDefinition workflow)
    {
        var run = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = _project.Id,
            OrganizationId = _project.OrganizationId,
            Status = ExecutionStatus.Failed,
            UserRequest = "r",
            QueuedAt = DateTime.UtcNow,
        };
        _db.WorkflowExecutions.Add(run);
        _db.SaveChanges();
        return run;
    }

    private WorkflowsController WorkflowsCtl(FakeEstimator estimator, IPlanGateService? planGate = null)
    {
        var user = new FakeUser(_owner);
        return new WorkflowsController(
            _db, user, TestTenancy.Access(_db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance,
            planGate: planGate,
            costEstimator: estimator,
            designBuildGate: Gate(estimator));
    }

    private WorkflowAssistantController AssistantCtl(FakeEstimator estimator, IPlanGateService? planGate = null)
    {
        var user = new FakeUser(_owner);
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Assistant:MaxExecutionsPerHour"] = "10" }).Build();
        return new WorkflowAssistantController(
            _db, user, TestTenancy.Access(_db, user), new WorkflowEditDiffService(_db),
            new AssistantExecutionGuard(_db, config),
            new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            planGate: planGate,
            costEstimator: estimator,
            designBuildGate: Gate(estimator));
    }

    private async Task<string> InvokeTool(
        FakeEstimator estimator, IWorkflowExecutionService exec, ExecutionInitiator initiator, Guid workflowId, double? accept = null,
        bool? chatApproved = null, IPlanGateService? planGate = null)
    {
        var user = new FakeUser(_owner);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        var templates = new WorkflowTemplateProvisioningService(
            _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());
        var p0 = ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P0Tools(_db, user, tokens);
        var provider = new AssistantToolProvider(
            _db, new WorkflowEditDiffService(_db), exec, new GrantingGuard(), user,
            new AssistantProjectTools(_db, tokens, templates, user, TestTenancy.Access(_db, user)),
            new AssistantFileTools(_db, tokens, new ReelBolt.WorkflowEngine.Tests.Agents.FakeFileStorage(),
                Mock.Of<IPublishEndpoint>(), new ReelBolt.WorkflowEngine.Tests.Agents.RecordingSummarizationQueue(), TestTenancy.Access(_db, user)),
            new AssistantWorkflowTools(_db, tokens, TestTenancy.Access(_db, user)),
            new AssistantAdminTools(_db, tokens, user, Mock.Of<ISecretProtector>()),
            new AssistantKnowledgeTools(_db, new ReelBolt.WorkflowEngine.Tests.Agents.FakePlatformDocsSearch(), templates),
            new AssistantStyleTools(_db, tokens, TestTenancy.Access(_db, user)),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.DesignSystemTools(_db, user, tokens),
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.PictureTools(_db, user, tokens),
            p0.Outputs,
            p0.Uploads,
            p0.Runs,
            p0.Revisions,
            p0.ChangeRequests,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(_db, user, tokens).Patch,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(_db, user, tokens).PictureDocuments,
            ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory.P2Tools(_db, user, tokens).GreenScreen,
            TestTenancy.Access(_db, user),
            planGate,
            null,
            new WorkflowExecutionPreflight(_db, estimator),
            Gate(estimator));

        var context = new AssistantToolContext(
            _owner, _project.Id, Initiator: initiator, ChatApproved: chatApproved ?? initiator == ExecutionInitiator.Assistant);
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == "ExecuteWorkflow");
        var args = new AIFunctionArguments
        {
            ["workflowId"] = workflowId,
            ["userRequest"] = "build it",
            ["regenerateVideoClips"] = false,
        };
        if (accept is { } figure)
            args["acceptEstimateCredits"] = figure;

        object? raw = await tool.InvokeAsync(args);
        return raw switch
        {
            null => string.Empty,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString()!,
            JsonElement el => el.GetRawText(),
            _ => raw.ToString()!,
        };
    }

    private static AIFunctionArguments Args(Guid workflowId) => new()
    {
        ["workflowId"] = workflowId,
        ["regenerateVideoClips"] = false,
    };

    private static JsonObject ExpectConflict(IActionResult? result, string code)
    {
        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        JsonObject body = Body(conflict.Value);
        body["code"]!.GetValue<string>().Should().Be(code);
        return body;
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private sealed class GrantingGuard : IAssistantExecutionGuard
    {
        public async Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            await insertCallback();
            return new RunRateDecision(true, bucket, 1, 10, 3600, 0);
        }
    }

    private sealed class RecordingExecutionService(InferenceApiDbContext db) : IWorkflowExecutionService
    {
        public List<(Guid WorkflowId, ExecutionInitiator Initiator)> Calls { get; } = [];

        public Task<WorkflowExecutionResponse> QueueAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips, RunChoices? runChoices, WorkflowDefinitionKind kind, Guid? executionId,
            Func<CancellationToken, Task>? beforePublish = null)
            => throw new NotSupportedException();

        public async Task<WorkflowExecutionResponse> ExecuteAsync(
            Guid projectId, Guid workflowId, Guid initiatedByUserId, string? userRequest, ExecutionInitiator initiator,
            CancellationToken ct, bool regenerateVideoClips = false, RunChoices? runChoices = null)
        {
            Calls.Add((workflowId, initiator));
            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                ProjectId = projectId,
                InitiatedByUserId = initiatedByUserId,
                Initiator = initiator,
                Status = ExecutionStatus.Queued,
                StartedAt = DateTime.UtcNow,
                UserRequest = userRequest,
            };
            db.WorkflowExecutions.Add(execution);
            await db.SaveChangesAsync(ct);
            return new WorkflowExecutionResponse(
                execution.Id, workflowId, execution.Status.ToString(), execution.StartedAt, null, 0, null,
                execution.CorrelationId, "no bus in this harness", new List<StepResultResponse>(), new List<ReviewScoreResponse>(), userRequest);
        }
    }

    private sealed class FakeEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled { get; set; } = true;
        public bool ReturnDisabledEstimate { get; set; }
        public long LowMillis { get; set; }
        public long HighMillis { get; set; }
        public long RequiredMillis { get; set; }

        /// <summary>The balance cannot cover the run: the credit preflight refuses (402).</summary>
        public bool RefuseCredits { get; set; }

        /// <summary>The cold-start HIGH of the reference template the gate prices with a null organization (231 = the cap's 300 credits).</summary>
        public long ReferenceHighMillis { get; set; } = 231_000;

        /// <summary>The reference had to be priced from the seed constants.</summary>
        public bool ReferencePricedFromSeed { get; set; }

        /// <summary>The actual (organization) estimate had to be priced from the seed constants.</summary>
        public bool ActualPricedFromSeed { get; set; }
        public int EstimateCalls { get; private set; }

        public Task<WorkflowCostEstimate> EstimateAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
        {
            EstimateCalls++;
            return Task.FromResult(organizationId is null ? Reference() : Current());
        }

        public Task<WorkflowPreflightResult> PreflightAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default)
            => Task.FromResult(RefuseCredits
                ? new WorkflowPreflightResult(WorkflowPreflightOutcome.InsufficientCredits, Current(), 0)
                : new WorkflowPreflightResult(WorkflowPreflightOutcome.Allowed, Current(), HighMillis));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => EstimateAsync(organizationId, runSteps, ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(
            Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default)
            => PreflightAsync(organizationId, runSteps, ct);

        private WorkflowCostEstimate Reference() => BillingEnabled && !ReturnDisabledEstimate
            ? new WorkflowCostEstimate(true, false, 0, ReferenceHighMillis, 0, []) { PricedFromSeedFallback = ReferencePricedFromSeed }
            : WorkflowCostEstimate.Disabled;

        private WorkflowCostEstimate Current() => BillingEnabled && !ReturnDisabledEstimate
            ? new WorkflowCostEstimate(true, false, LowMillis, HighMillis, RequiredMillis, []) { PricedFromSeedFallback = ActualPricedFromSeed }
            : WorkflowCostEstimate.Disabled;
    }
}
