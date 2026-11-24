using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// The honest run-limit refusal on the two model-facing execute paths (assistant REST twin and the
/// ExecuteWorkflow tool the chat and the MCP share), with the REAL guard over the real ledger table.
/// </summary>
public class WorkflowAssistantRunRateLimitTests
{
    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase("run-rate-" + Guid.NewGuid().ToString("N")).Options);

        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Mock<IWorkflowExecutionService> Executions { get; } = new();
        public AssistantExecutionGuard Guard { get; }

        /// <summary>The guard the controller and the tool are handed; the real one unless a test wraps it.</summary>
        public IAssistantExecutionGuard? GuardOverride { get; set; }

        /// <summary>What <see cref="RunRateAdmission.Current"/> said each time a run was queued.</summary>
        public List<RunRateBucket?> AdmittedAtQueue { get; } = new();

        public Rig()
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            Guard = new AssistantExecutionGuard(Db, new ConfigurationBuilder().Build());
            Executions
                .Setup(s => s.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
                    It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()))
                .ReturnsAsync((Guid p, Guid w, Guid u, string? req, ExecutionInitiator i, CancellationToken _, bool _, RunChoices? _) =>
                {
                    AdmittedAtQueue.Add(RunRateAdmission.Current);
                    return new WorkflowExecutionResponse(Guid.NewGuid(), w, "Queued", DateTime.UtcNow, null, 0, null, null, null, [], [], req);
                });
        }

        public Guid Workflow(params StepType[] steps)
        {
            var id = Guid.NewGuid();
            Db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = id, Name = "Promo", ProjectId = Project.Id });
            int order = 1;
            foreach (StepType type in steps)
                Db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = id, StepOrder = order++, StepType = type });
            Db.SaveChanges();
            return id;
        }

        public void SeedEvents(string bucket, int count, TimeSpan age)
        {
            for (int i = 0; i < count; i++)
                Db.RunRateEvents.Add(new RunRateEvent { Id = Guid.NewGuid(), UserId = User, ProjectId = Project.Id, Bucket = bucket, CreatedAt = DateTime.UtcNow - age });
            Db.SaveChanges();
        }

        public WorkflowAssistantController Controller()
        {
            var user = new FakeCurrentUser(User);
            return new WorkflowAssistantController(
                Db, user, TestTenancy.Access(Db, user), new WorkflowEditDiffService(Db), GuardOverride ?? Guard, Executions.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        public async Task<JsonElement> ToolAsync(Guid workflowId)
        {
            var user = new FakeCurrentUser(User);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(Db, user, executionGuard: GuardOverride ?? Guard, executionService: Executions.Object);
            AIFunction tool = provider.GetTools(new AssistantToolContext(User, Project.Id)).Single(t => t.Name == "ExecuteWorkflow");
            object? raw = await tool.InvokeAsync(new AIFunctionArguments
            {
                ["workflowId"] = workflowId,
                ["userRequest"] = "a very long brief the model should not get back",
                ["regenerateVideoClips"] = false,
            });
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : raw!.ToString()!;
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public void AddStep(Guid workflowId, int order, StepType type)
        {
            Db.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, StepOrder = order, StepType = type });
            Db.SaveChanges();
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>The real guard, with an edit landing after the route called it and before it classified.</summary>
    private sealed class EditUnderLockGuard(IAssistantExecutionGuard inner, Action edit) : IAssistantExecutionGuard
    {
        public Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct) =>
            throw new InvalidOperationException("an execute route must classify under the lock, not pass a bucket it computed before it");

        public Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct) =>
            inner.TryAcquireAsync(userId, projectId, () => { edit(); return classify(); }, source, insertCallback, ct);
    }

    /// <summary>A guard that could not take its lock in time: every call is refused as Busy.</summary>
    private sealed class BusyGuard : IAssistantExecutionGuard
    {
        private static readonly RunRateDecision Busy = new(false, RunRateBucket.Paid, 3, 10, 3600, 5, Busy: true);

        public Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct) =>
            Task.FromResult(Busy);

        public Task<RunRateDecision> TryAcquireAsync(Guid userId, Guid projectId, Func<Task<RunRateBucket>> classify, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct) =>
            Task.FromResult(Busy);
    }

    [Fact]
    public async Task The_tool_refusal_says_busy_when_the_limiter_could_not_take_its_lock()
    {
        using var rig = new Rig { GuardOverride = new BusyGuard() };
        Guid workflow = rig.Workflow(StepType.Extract);

        JsonElement refusal = await rig.ToolAsync(workflow);

        refusal.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        refusal.GetProperty("busy").GetBoolean().Should().BeTrue();
        refusal.GetProperty("error").GetString().Should().Contain("busy");
    }

    [Fact]
    public async Task The_rest_refusal_says_busy_when_the_limiter_could_not_take_its_lock()
    {
        using var rig = new Rig { GuardOverride = new BusyGuard() };
        Guid workflow = rig.Workflow(StepType.Extract);

        ActionResult<WorkflowExecutionResponse> result = await rig.Controller().Execute(rig.Project.Id, workflow, null, CancellationToken.None);

        ObjectResult refused = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(429);
        JsonElement body = JsonSerializer.SerializeToElement(refused.Value);
        body.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        body.GetProperty("busy").GetBoolean().Should().BeTrue();
        body.GetProperty("message").GetString().Should().Contain("busy");
    }

    [Fact]
    public async Task A_limit_reached_refusal_is_not_busy()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Agent);
        rig.SeedEvents("paid", 10, TimeSpan.FromMinutes(5));

        JsonElement refusal = await rig.ToolAsync(workflow);

        refusal.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        refusal.GetProperty("busy").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task The_rest_twin_classifies_the_steps_as_they_are_under_the_lock()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Extract);
        rig.SeedEvents("paid", 10, TimeSpan.FromMinutes(5));
        rig.GuardOverride = new EditUnderLockGuard(rig.Guard, () => rig.AddStep(workflow, 2, StepType.Agent));

        ActionResult<WorkflowExecutionResponse> result = await rig.Controller().Execute(rig.Project.Id, workflow, null, CancellationToken.None);

        // Free when the request arrived, paid by the time the lock was held: refused in the full paid bucket.
        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(429);
        rig.AdmittedAtQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task The_tool_classifies_the_steps_as_they_are_under_the_lock()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Extract);
        rig.GuardOverride = new EditUnderLockGuard(rig.Guard, () => rig.AddStep(workflow, 2, StepType.Agent));

        JsonElement receipt = await rig.ToolAsync(workflow);

        receipt.GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("paid");
        rig.AdmittedAtQueue.Should().Equal(RunRateBucket.Paid);
        (await rig.Db.RunRateEvents.Select(e => e.Bucket).ToListAsync()).Should().Equal("paid");
    }

    [Fact]
    public async Task Both_routes_queue_a_free_run_with_its_admitted_bucket()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Extract, StepType.VideoCompile);

        (await rig.Controller().Execute(rig.Project.Id, workflow, null, CancellationToken.None))
            .Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        (await rig.ToolAsync(workflow)).GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("free");

        rig.AdmittedAtQueue.Should().Equal(RunRateBucket.Free, RunRateBucket.Free);
    }

    [Fact]
    public async Task The_rest_twin_answers_429_with_a_retry_after_header_and_the_full_body()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Agent);
        rig.SeedEvents("paid", 9, TimeSpan.FromMinutes(5));
        rig.SeedEvents("paid", 1, TimeSpan.FromMinutes(48));
        WorkflowAssistantController controller = rig.Controller();

        ActionResult<WorkflowExecutionResponse> result = await controller.Execute(rig.Project.Id, workflow, new ExecuteWorkflowRequest(UserRequest: "go"), CancellationToken.None);

        ObjectResult obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(429);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(obj.Value));
        JsonElement root = body.RootElement;
        root.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        root.GetProperty("bucket").GetString().Should().Be("paid");
        root.GetProperty("used").GetInt32().Should().Be(10);
        root.GetProperty("limit").GetInt32().Should().Be(10);
        root.GetProperty("windowMinutes").GetInt32().Should().Be(60);
        int retry = root.GetProperty("retryAfterSeconds").GetInt32();
        retry.Should().BeInRange(12 * 60 - 2, 12 * 60 + 1);
        root.GetProperty("message").GetString().Should().Contain("10 of 10 paid runs");
        controller.Response.Headers.RetryAfter.ToString().Should().Be(retry.ToString());
        rig.Executions.Verify(s => s.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(),
            It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>()), Times.Never);
    }

    [Fact]
    public async Task A_free_deterministic_workflow_is_not_stopped_by_a_full_paid_bucket()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Extract, StepType.VideoCompile);
        rig.SeedEvents("paid", 10, TimeSpan.FromMinutes(5));

        ActionResult<WorkflowExecutionResponse> result = await rig.Controller().Execute(rig.Project.Id, workflow, null, CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        (await rig.Db.RunRateEvents.CountAsync(e => e.Bucket == "free")).Should().Be(1);
    }

    [Fact]
    public async Task The_tool_refusal_states_used_limit_and_how_long_to_wait()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Agent);
        rig.SeedEvents("paid", 10, TimeSpan.FromMinutes(48));

        JsonElement refusal = await rig.ToolAsync(workflow);

        refusal.GetProperty("code").GetString().Should().Be("RUN_RATE_LIMITED");
        refusal.GetProperty("bucket").GetString().Should().Be("paid");
        refusal.GetProperty("used").GetInt32().Should().Be(10);
        refusal.GetProperty("limit").GetInt32().Should().Be(10);
        refusal.GetProperty("windowMinutes").GetInt32().Should().Be(60);
        refusal.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(12 * 60 - 2, 12 * 60 + 1);
        refusal.GetProperty("error").GetString().Should().Be(
            "run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 12 minutes");
        AssistantToolRefusal.IsRefusal(refusal.GetRawText()).Should().BeTrue();
    }

    [Fact]
    public async Task The_tool_receipt_is_compact_and_does_not_echo_the_brief()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Agent);

        JsonElement receipt = await rig.ToolAsync(workflow);

        receipt.GetProperty("executionId").GetGuid().Should().NotBeEmpty();
        receipt.GetProperty("status").GetString().Should().Be("Queued");
        receipt.GetProperty("workflowName").GetString().Should().Be("Promo");
        receipt.GetProperty("projectName").GetString().Should().Be("Launch");
        receipt.GetProperty("rateLimit").GetProperty("bucket").GetString().Should().Be("paid");
        receipt.GetProperty("rateLimit").GetProperty("used").GetInt32().Should().Be(1);
        receipt.GetProperty("rateLimit").GetProperty("limit").GetInt32().Should().Be(10);
        receipt.GetRawText().Should().NotContain("very long brief");
        foreach (string gone in new[] { "userRequest", "UserRequest", "resultJson", "ResultJson", "stepResults", "StepResults", "reviewScores", "ReviewScores" })
            receipt.TryGetProperty(gone, out _).Should().BeFalse(gone);
    }

    /// <summary>MKT-01: the receipt of a run of a copied community recipe gives its name fenced; a user's own stays plain (above).</summary>
    [Fact]
    public async Task The_tool_receipt_of_a_copied_recipe_fences_its_name()
    {
        using var rig = new Rig();
        Guid workflow = rig.Workflow(StepType.Agent);
        WorkflowDefinition row = rig.Db.WorkflowDefinitions.Single(w => w.Id == workflow);
        row.CreatedVia = WorkflowCreatedVia.Marketplace;
        rig.Db.SaveChanges();

        JsonElement receipt = await rig.ToolAsync(workflow);

        receipt.GetProperty("executionId").GetGuid().Should().NotBeEmpty();
        receipt.GetProperty("workflowName").GetString().Should()
            .StartWith(ReelBolt.Inference.Api.Agents.Tools.AssistantMarketplaceTools.FenceStartPrefix).And.Contain("Promo");
        receipt.GetProperty("communityOrigin").GetBoolean().Should().BeTrue();
        receipt.GetProperty("status").GetString().Should().Be("Queued");
    }
}
