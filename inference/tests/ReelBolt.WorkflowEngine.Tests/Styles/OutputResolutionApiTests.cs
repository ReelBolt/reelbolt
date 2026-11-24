using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// The Inference API half of the output-resolution run choice: the resolver, the execute plan gate
/// (402 before any row), the row and event, retry, list/detail, the estimate's pixel factor, the
/// assistant tool and its REST twin, and ListStyles.
/// </summary>
public class OutputResolutionApiTests
{
    private static readonly OrganizationEntitlementSet Creator = new() { Tier = "creator", MaxOutputHeight = 1080 };
    private static readonly OrganizationEntitlementSet Pro = new() { Tier = "pro", MaxOutputHeight = 2160 };

    // --- Resolver ---------------------------------------------------------------

    [Fact]
    public async Task The_resolver_parses_a_resolution_into_the_run_choices()
    {
        using InferenceApiDbContext db = NewContext();
        RunChoicesResolution result = await new RunStyleResolver(db).ResolveAsync(
            Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, "4K", default);

        result.Error.Should().BeNull();
        result.Choices!.OutputResolution.Should().Be(2160);
        result.Choices.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task The_resolver_rejects_junk_with_the_plain_language_message()
    {
        using InferenceApiDbContext db = NewContext();
        RunChoicesResolution result = await new RunStyleResolver(db).ResolveAsync(
            Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, "1440p", default);

        result.Error.Should().Be("Unknown output resolution '1440p'. Use Auto, 720p, 1080p or 2160p.");
    }

    [Fact]
    public async Task Auto_is_no_choice()
    {
        using InferenceApiDbContext db = NewContext();
        RunChoicesResolution result = await new RunStyleResolver(db).ResolveAsync(
            Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, "Auto", default);

        result.Choices!.OutputResolution.Should().BeNull();
        result.Choices.IsEmpty.Should().BeTrue();
    }

    // --- REST execute ------------------------------------------------------------

    [Fact]
    public async Task Execute_as_Creator_at_2160p_is_402_PLAN_NOT_ENTITLED_and_writes_no_row()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.RootElement.GetProperty("feature").GetString().Should().Be("resolution");
        body.RootElement.GetProperty("limit").GetInt64().Should().Be(1080);
        body.RootElement.GetProperty("current").GetInt64().Should().Be(2160);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        h.Publish.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Execute_as_Pro_at_2160p_stores_and_publishes_the_short_side()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(201);
        ((WorkflowExecutionResponse)obj.Value!).OutputResolution.Should().Be("2160p");
        WorkflowExecution row = await db.WorkflowExecutions.SingleAsync();
        row.OutputResolution.Should().Be(2160);
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.OutputResolution == 2160), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_without_a_resolution_leaves_the_row_and_the_event_null()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        ((WorkflowExecutionResponse)((ObjectResult)result.Result!).Value!).OutputResolution.Should().BeNull();
        (await db.WorkflowExecutions.SingleAsync()).OutputResolution.Should().BeNull();
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.OutputResolution == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_with_an_unknown_resolution_is_400_before_the_credit_check()
    {
        using InferenceApiDbContext db = NewContext();
        // A zero balance would be refused 402 by the credit preflight: the 400 must win.
        Harness h = await HarnessAsync(db, Pro, balanceMillis: 0);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "1440p"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_credit_preflight_prices_the_chosen_resolution()
    {
        using InferenceApiDbContext db = NewContext();
        // Exactly the cold-start hold of the run at Auto: affordable at Auto, not at 4K.
        long autoRequired = StepCreditDefaults.MillisFor(StepType.Agent).Low + StepCreditDefaults.MillisFor(StepType.VideoCompile).Low;
        Harness h = await HarnessAsync(db, Pro, balanceMillis: autoRequired);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        obj.Value!.ToString().Should().Contain("INSUFFICIENT_CREDITS");
    }

    [Fact]
    public async Task Retry_republishes_the_rows_resolution()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            OutputResolution = 720
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.RetryExecution(h.Project.Id, h.Workflow.Id, run.Id, null, default);

        ((WorkflowExecutionResponse)((ObjectResult)result.Result!).Value!).OutputResolution.Should().Be("720p");
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == run.Id && m.OutputResolution == 720), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>T-015: a retry re-queues the same row, so a free-admitted run keeps its stamp (and the engine's re-check).</summary>
    [Fact]
    public async Task Retry_keeps_the_rows_admitted_run_rate_bucket()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            AdmittedRunRateBucket = "free"
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        await h.Controller.RetryExecution(h.Project.Id, h.Workflow.Id, run.Id, null, default);

        (await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run.Id)).AdmittedRunRateBucket.Should().Be("free");
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == run.Id && m.AdmittedRunRateBucket == "free"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_and_detail_expose_the_label()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow,
            OutputResolution = 2160
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        var list = (OkObjectResult)(await h.Controller.ListExecutions(h.Project.Id, h.Workflow.Id, default)).Result!;
        ((IEnumerable<WorkflowExecutionResponse>)list.Value!).Single().OutputResolution.Should().Be("2160p");

        var detail = (OkObjectResult)(await h.Controller.GetExecution(h.Project.Id, run.Id, default)).Result!;
        ((WorkflowExecutionResponse)detail.Value!).OutputResolution.Should().Be("2160p");
    }

    // --- Estimate ------------------------------------------------------------------

    [Fact]
    public async Task The_estimate_endpoint_prices_the_chosen_clips_into_an_open_source_slot_and_ignores_foreign_ones()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        const string key = "projects/p/userFiles/long.mp4";
        Guid file = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        db.AddRange(
            new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = h.Workflow.Id,
                StepOrder = 3,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = "{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"}}"
            },
            new ProjectFile { Id = file, ProjectId = h.Project.Id, StorageKey = key, SizeBytes = 1_920_000_000, OriginalFileName = "long.mp4" },
            new Project { Id = otherProject, Name = "x", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() },
            new ProjectFile { Id = foreign, ProjectId = otherProject, StorageKey = "k/x.mp4", SizeBytes = 1_920_000_000, OriginalFileName = "x.mp4" },
            new MediaDerivative
            {
                Id = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                SourceStorageKey = key,
                SourceProbeJson = new MediaSourceProbe(960, 30, 1, 3840, 2160, "h264", "aac", "mov", 2, 48000, null, true).ToJson()
            });
        await db.SaveChangesAsync();

        double cold = Value(await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default)).RequiredCredits;
        double chosen = Value(await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default, videoFileIds: [file])).RequiredCredits;
        double stranger = Value(await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default, videoFileIds: [foreign])).RequiredCredits;

        chosen.Should().BeGreaterThan(cold, "a 16-minute 4K clip chosen for the open slot is held for what it is");
        stranger.Should().Be(cold, "a file outside the project is unknown and ignored");
    }

    [Fact]
    public async Task The_estimate_endpoint_scales_render_steps_by_the_pixel_factor()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        WorkflowCostEstimateResponse auto = Value(await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default));
        WorkflowCostEstimateResponse uhd = Value(await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default, outputResolution: "2160p"));

        WorkflowStepCostEstimateResponse compileAuto = auto.Steps.Single(s => s.StepType == "VideoCompile");
        WorkflowStepCostEstimateResponse compileUhd = uhd.Steps.Single(s => s.StepType == "VideoCompile");
        compileUhd.LowCredits.Should().Be(compileAuto.LowCredits * 4);
        compileUhd.HighCredits.Should().Be(compileAuto.HighCredits * 4);

        // The Author agent renders (SandboxRender): only its high bound moves.
        WorkflowStepCostEstimateResponse agentAuto = auto.Steps.Single(s => s.StepType == "Agent");
        WorkflowStepCostEstimateResponse agentUhd = uhd.Steps.Single(s => s.StepType == "Agent");
        agentUhd.LowCredits.Should().Be(agentAuto.LowCredits);
        agentUhd.HighCredits.Should().Be(agentAuto.HighCredits * 4);
    }

    [Fact]
    public async Task A_non_rendering_agent_and_the_no_choice_estimate_are_unchanged()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        AgentDefinition director = new() { Id = Guid.NewGuid(), Name = "Director", Description = "d", SystemPrompt = "p", AgentType = AgentType.DirectorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        db.AgentDefinitions.Add(director);
        await db.SaveChangesAsync();
        WorkflowStep[] steps =
        [
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = director.Id },
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.TimelineRender, AgentDefinitionId = director.Id }
        ];

        WorkflowCostEstimate plain = await h.Estimator.EstimateAsync(h.Project.OrganizationId, steps, default);
        WorkflowCostEstimate auto = await h.Estimator.EstimateAsync(h.Project.OrganizationId, steps, null, default);
        WorkflowCostEstimate uhd = await h.Estimator.EstimateAsync(h.Project.OrganizationId, steps, 2160, default);
        WorkflowCostEstimate hd = await h.Estimator.EstimateAsync(h.Project.OrganizationId, steps, 720, default);

        auto.Should().BeEquivalentTo(plain);
        uhd.Steps[0].HighMillis.Should().Be(plain.Steps[0].HighMillis);
        uhd.Steps[0].LowMillis.Should().Be(plain.Steps[0].LowMillis);
        uhd.Steps[1].LowMillis.Should().Be(plain.Steps[1].LowMillis * 4);
        uhd.Steps[1].HighMillis.Should().Be(plain.Steps[1].HighMillis * 4);
        // 720p is cheaper to encode, and the range says so; the hold never shrinks.
        hd.Steps[1].HighMillis.Should().BeLessThan(plain.Steps[1].HighMillis);
        hd.Steps[1].HoldMillis.Should().Be(plain.Steps[1].HoldMillis);
    }

    [Fact]
    public async Task The_estimate_endpoint_refuses_an_unknown_resolution()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);

        (await h.Controller.Estimate(h.Project.Id, h.Workflow.Id, default, outputResolution: "1440p"))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // --- Assistant REST twin and tool ----------------------------------------------

    [Fact]
    public async Task The_assistant_rest_execute_refuses_Creator_at_2160p_with_402()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();
        var controller = new WorkflowAssistantController(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db),
            new AssistantExecutionGuard(db, config), h.ExecutionService, costEstimator: h.Estimator);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        obj.Value!.ToString().Should().Contain("PLAN_NOT_ENTITLED");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_assistant_tool_returns_error_and_planGate_for_Creator_at_2160p()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator);
        AssistantToolProvider provider = ToolProvider(db, h);

        JsonElement result = await InvokeToolAsync(provider, new AssistantToolContext(h.Project.OwnerId, h.Project.Id), "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("userRequest", "go"), ("regenerateVideoClips", false), ("outputResolution", "2160p"));

        result.GetProperty("error").GetString().Should().Contain("1080p");
        result.GetProperty("planGate").GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        result.GetProperty("planGate").GetProperty("feature").GetString().Should().Be("resolution");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_assistant_tool_rejects_junk_and_starts_a_Pro_4K_run()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        AssistantToolProvider provider = ToolProvider(db, h);
        var context = new AssistantToolContext(h.Project.OwnerId, h.Project.Id);

        JsonElement junk = await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("userRequest", "go"), ("regenerateVideoClips", false), ("outputResolution", "1440p"));
        junk.GetProperty("error").GetString().Should().Contain("Unknown output resolution");

        await InvokeToolAsync(provider, context, "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("userRequest", "go"), ("regenerateVideoClips", false), ("outputResolution", "4K"));
        (await db.WorkflowExecutions.SingleAsync()).OutputResolution.Should().Be(2160);
    }

    [Fact]
    public async Task The_assistant_tool_describes_the_parameter()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        AIFunction tool = ToolProvider(db, h).GetTools(new AssistantToolContext(h.Project.OwnerId, h.Project.Id))
            .Single(t => t.Name == "ExecuteWorkflow");

        tool.JsonSchema.GetProperty("properties").TryGetProperty("outputResolution", out _).Should().BeTrue();
        tool.Description.Should().Contain("outputResolution").And.Contain("2160p");
    }

    [Fact]
    public async Task ListStyles_lists_the_output_resolutions()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Pro);
        JsonElement result = await InvokeToolAsync(ToolProvider(db, h), new AssistantToolContext(h.Project.OwnerId, h.Project.Id), "ListStyles");

        result.GetProperty("outputResolutions").EnumerateArray().Select(r => r.GetProperty("value").GetString())
            .Should().Equal("Auto", "720p", "1080p", "2160p");
    }

    // --- QA-RR-01: the plan's resolution cap answers before the credit preflight ------

    /// <summary>Affordable at Auto, not at 4K: the cold-start hold of the harness workflow at Auto.</summary>
    private static long AutoRequiredMillis =>
        StepCreditDefaults.MillisFor(StepType.Agent).Low + StepCreditDefaults.MillisFor(StepType.VideoCompile).Low;

    [Fact]
    public async Task Execute_as_a_low_balance_Creator_at_2160p_is_PLAN_NOT_ENTITLED_not_INSUFFICIENT_CREDITS()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator, balanceMillis: AutoRequiredMillis);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.RootElement.GetProperty("feature").GetString().Should().Be("resolution");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_assistant_rest_execute_as_a_low_balance_Creator_at_2160p_is_PLAN_NOT_ENTITLED()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator, balanceMillis: AutoRequiredMillis);
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();
        var controller = new WorkflowAssistantController(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db),
            new AssistantExecutionGuard(db, config), h.ExecutionService, planGate: h.Gate, costEstimator: h.Estimator);

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_assistant_tool_as_a_low_balance_Creator_at_2160p_answers_planGate()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, Creator, balanceMillis: AutoRequiredMillis);
        AssistantToolProvider provider = ToolProvider(db, h, planGate: h.Gate);

        JsonElement result = await InvokeToolAsync(provider, new AssistantToolContext(h.Project.OwnerId, h.Project.Id), "ExecuteWorkflow",
            ("workflowId", h.Workflow.Id), ("userRequest", "go"), ("regenerateVideoClips", false), ("outputResolution", "2160p"));

        result.TryGetProperty("code", out _).Should().BeFalse("the credit preflight must not answer a plan refusal");
        result.GetProperty("error").GetString().Should().Contain("1080p");
        result.GetProperty("planGate").GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    // --- helpers -------------------------------------------------------------------

    private sealed record Harness(
        WorkflowsController Controller,
        Project Project,
        WorkflowDefinition Workflow,
        Mock<IPublishEndpoint> Publish,
        IWorkflowExecutionService ExecutionService,
        WorkflowCostEstimator Estimator,
        PlanGateService Gate);

    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    private static WorkflowCostEstimateResponse Value(ActionResult<WorkflowCostEstimateResponse> result)
        => (WorkflowCostEstimateResponse)((OkObjectResult)result.Result!).Value!;

    /// <summary>A project with an Author agent step and a VideoCompile step, a funded account, and the plan's gate.</summary>
    private static async Task<Harness> HarnessAsync(InferenceApiDbContext db, OrganizationEntitlementSet plan, long balanceMillis = 100_000_000)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
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
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balanceMillis });
        await db.SaveChangesAsync();

        var publish = new Mock<IPublishEndpoint>();
        var gate = new PlanGateService(db, Fixed(plan));
        var execution = new WorkflowExecutionService(db, publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate);
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        var user = new FakeCurrentUser(owner);
        var controller = new WorkflowsController(
            db, user, TestTenancy.Access(db, user), publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            execution, NullLogger<WorkflowsController>.Instance, planGate: gate, costEstimator: estimator);
        return new Harness(controller, project, workflow, publish, execution, estimator, gate);
    }

    private static AssistantToolProvider ToolProvider(InferenceApiDbContext db, Harness h, IPlanGateService? planGate = null)
    {
        var user = new FakeCurrentUser(h.Project.OwnerId);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();
        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            h.ExecutionService,
            new AssistantExecutionGuard(db, config),
            user,
            new AssistantProjectTools(db, tokens,
                new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
                user, TestTenancy.Access(db, user)),
            new AssistantFileTools(db, tokens, new ReelBolt.WorkflowEngine.Tests.Agents.FakeFileStorage(), Mock.Of<IPublishEndpoint>(), new ReelBolt.WorkflowEngine.Tests.Agents.RecordingSummarizationQueue(), TestTenancy.Access(db, user)),
            new AssistantWorkflowTools(db, tokens, TestTenancy.Access(db, user)),
            new AssistantAdminTools(db, tokens, user, Mock.Of<ISecretProtector>()),
            new AssistantKnowledgeTools(db, new ReelBolt.WorkflowEngine.Tests.Agents.FakePlatformDocsSearch(),
                new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build())),
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
            planGate,
            null,
            new WorkflowExecutionPreflight(db, h.Estimator));
    }

    private static async Task<JsonElement> InvokeToolAsync(
        AssistantToolProvider provider, AssistantToolContext context, string toolName, params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        object? raw = await tool.InvokeAsync(args);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

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
