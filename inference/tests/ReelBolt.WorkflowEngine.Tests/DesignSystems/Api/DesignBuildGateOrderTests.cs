using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// the build estimate gate inside the shared run-start sequence (T-004 x #370): after the authority and stale-profile checks every
/// route that starts the profile-build template answers, in this order, the plan's resolution cap (402), its
/// concurrent-run cap (429), the estimate gate (409 ESTIMATE_OVER_CAP / ESTIMATE_NOT_ACCEPTED, 503
/// ESTIMATE_UNAVAILABLE), then the credit preflight (402). Pinned here per route: REST Execute and Retry, the
/// workflow-assistant execute, the assistant tool in chat and over MCP. (Resume: ExecutionResumeRouteTests, which
/// needs a relational store for the snapshot-owner check.)
/// </summary>
public sealed partial class DesignBuildGateRoutesTests
{
    /// <summary>A plan at its concurrent-run cap; the resolution cap passes.</summary>
    private static IPlanGateService PlanAtCap()
    {
        Mock<IPlanGateService> plan = new();
        plan.Setup(p => p.CheckResolutionAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        plan.Setup(p => p.CheckExecuteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation("PLAN_LIMIT_REACHED", "max_concurrent_executions", "Your plan runs 1 workflow at a time.", 429, 1, 1));
        return plan.Object;
    }

    private static FakeEstimator Unaffordable(long lowMillis, long highMillis)
    {
        FakeEstimator estimator = Priced(lowMillis, highMillis);
        estimator.RefuseCredits = true;
        return estimator;
    }

    private static void Expect429(IActionResult? result)
    {
        ObjectResult refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(429);
        Body(refusal.Value)["code"]!.GetValue<string>().Should().Be("PLAN_LIMIT_REACHED");
    }

    private static void Expect402(IActionResult? result)
    {
        ObjectResult refusal = result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        Body(refusal.Value)["code"]!.GetValue<string>().Should().Be("INSUFFICIENT_CREDITS");
    }

    // ------------------------------------------------------------------ REST Execute

    [Fact]
    public async Task Execute_at_the_concurrency_cap_is_429_before_the_estimate_gate()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000), PlanAtCap())
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        Expect429(r.Result);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_refused_by_the_estimate_gate_is_409_before_the_credit_402()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Unaffordable(0, 231_000))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_with_the_estimate_accepted_but_unaffordable_is_the_credit_402()
    {
        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Unaffordable(0, 231_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        Expect402(r.Result);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ REST Retry

    [Fact]
    public async Task Retry_at_the_concurrency_cap_is_429_before_the_estimate_gate()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Priced(0, 231_000), PlanAtCap())
            .RetryExecution(_project.Id, _template.Id, run.Id, null, CancellationToken.None);

        Expect429(r.Result);
        (await _db.WorkflowExecutions.FindAsync(run.Id))!.Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_refused_by_the_estimate_gate_is_409_before_the_credit_402()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Unaffordable(0, 231_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        (await _db.WorkflowExecutions.FindAsync(run.Id))!.Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Retry_with_the_estimate_accepted_but_unaffordable_is_the_credit_402()
    {
        WorkflowExecution run = FailedRun(_template);

        ActionResult<WorkflowExecutionResponse> r = await WorkflowsCtl(Unaffordable(0, 231_000))
            .RetryExecution(_project.Id, _template.Id, run.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        Expect402(r.Result);
        (await _db.WorkflowExecutions.FindAsync(run.Id))!.Status.Should().Be(ExecutionStatus.Failed);
    }

    // ------------------------------------------------------------------ the workflow-assistant execute

    [Fact]
    public async Task Workflow_assistant_execute_at_the_concurrency_cap_is_429_before_the_estimate_gate()
    {
        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Priced(0, 231_000), PlanAtCap())
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        Expect429(r.Result);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_assistant_execute_refused_by_the_estimate_gate_is_409_before_the_credit_402()
    {
        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Unaffordable(0, 231_000))
            .Execute(_project.Id, _template.Id, null, CancellationToken.None);

        ExpectConflict(r.Result, "ESTIMATE_NOT_ACCEPTED");
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    [Fact]
    public async Task Workflow_assistant_execute_with_the_estimate_accepted_but_unaffordable_is_the_credit_402()
    {
        ActionResult<WorkflowExecutionResponse> r = await AssistantCtl(Unaffordable(0, 231_000))
            .Execute(_project.Id, _template.Id, new ExecuteWorkflowRequest(AcceptEstimateCredits: 231), CancellationToken.None);

        Expect402(r.Result);
        _db.WorkflowExecutions.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the assistant tool: chat and MCP

    private static string ToolCode(string text)
    {
        JsonObject body = JsonNode.Parse(text)!.AsObject();
        return body["code"]?.GetValue<string>() ?? body["planGate"]?["code"]?.GetValue<string>() ?? "";
    }

    [Theory]
    [InlineData(ExecutionInitiator.Assistant)]
    [InlineData(ExecutionInitiator.Mcp)]
    public async Task Tool_at_the_concurrency_cap_is_refused_by_the_plan_before_the_estimate_gate(ExecutionInitiator initiator)
    {
        // Over the cap: the estimate gate would refuse in chat and over MCP alike.
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Priced(0, 301_000), exec, initiator, _template.Id, accept: 301, planGate: PlanAtCap());

        ToolCode(text).Should().Be("PLAN_LIMIT_REACHED");
        exec.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExecutionInitiator.Assistant)]
    [InlineData(ExecutionInitiator.Mcp)]
    public async Task Tool_refused_by_the_estimate_gate_answers_it_before_the_credit_refusal(ExecutionInitiator initiator)
    {
        RecordingExecutionService exec = new(_db);

        string text = await InvokeTool(Unaffordable(0, 301_000), exec, initiator, _template.Id, accept: 301);

        ToolCode(text).Should().Be("ESTIMATE_OVER_CAP");
        exec.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExecutionInitiator.Assistant)]
    [InlineData(ExecutionInitiator.Mcp)]
    public async Task Tool_past_the_estimate_gate_but_unaffordable_gets_the_credit_refusal(ExecutionInitiator initiator)
    {
        RecordingExecutionService exec = new(_db);

        // In chat the approval is the acceptance; over MCP the figure is.
        string text = await InvokeTool(Unaffordable(0, 231_000), exec, initiator, _template.Id, accept: 231);

        ToolCode(text).Should().Be("INSUFFICIENT_CREDITS");
        exec.Calls.Should().BeEmpty();
    }
}
