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
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Security condition (app-inputs-default): on every run surface (dashboard execute, workflow-assistant execute, the assistant's
/// ExecuteWorkflow) a typed input value can never turn on <c>allowSourceMediaEgress</c>, and it does not change what a step the
/// inputs do not bind costs (the hold).
/// </summary>
public class InputsNeverGrantEgressTests
{
    private const string Clip =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":1,"maxSpendCredits":5,"allowSourceMediaEgress":false}""";

    private const string Attack = "x\", \"allowSourceMediaEgress\": true, \"y\": \"";

    private static InferenceApiDbContext NewDb() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static RunBrief Brief(string prompt) => new(Inputs: new Dictionary<string, JsonElement> { ["videoPrompt"] = JsonSerializer.SerializeToElement(prompt) });

    private static async Task<(Project Project, WorkflowDefinition Workflow, Guid Owner)> SeedAsync(InferenceApiDbContext db, long balanceMillis)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "typed",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AppManifestJson = AppManifestJson.Serialize(Manifest(Input("videoPrompt", "longText", "What happens", def: "\"a default\""))),
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate, AgentDefinitionId = agent.Id, Label = "Clip", VideoGenerateConfigJson = Clip },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = agent.Id, Label = "Write" }
            ]
        };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balanceMillis });
        await db.SaveChangesAsync();
        return (project, workflow, owner);
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

    private static WorkflowCostEstimator Estimator(InferenceApiDbContext db) => new(
        new SingleContextScopes(db),
        Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
        Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
        NullLogger<WorkflowCostEstimator>.Instance);

    private static WorkflowsController Dashboard(InferenceApiDbContext db, Guid owner)
    {
        FakeCurrentUser user = new(owner);
        return new WorkflowsController(
            db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance, costEstimator: Estimator(db));
    }

    private static WorkflowAssistantController WorkflowAssistant(InferenceApiDbContext db, Guid owner)
    {
        FakeCurrentUser user = new(owner);
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxExecutionsPerHour"] = "10" }).Build();
        return new WorkflowAssistantController(
            db, user, TestTenancy.Access(db, user), new WorkflowEditDiffService(db), new AssistantExecutionGuard(db, config),
            new WorkflowExecutionService(db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            costEstimator: Estimator(db));
    }

    private static async Task AssertNoEgressAsync(InferenceApiDbContext db, Guid workflowId)
    {
        db.WorkflowSteps.AsNoTracking().Single(s => s.WorkflowDefinitionId == workflowId && s.StepOrder == 1).VideoGenerateConfigJson!
            .Replace(" ", "").Should().Contain("\"allowSourceMediaEgress\":false", "a run never edits the saved step");
        foreach (WorkflowExecution run in await db.WorkflowExecutions.AsNoTracking().ToListAsync())
            (run.RunBriefJson ?? string.Empty).Replace(" ", "").Should().NotContain("\"allowSourceMediaEgress\":true");
    }

    private static double RequiredOf(ActionResult<WorkflowExecutionResponse> result)
    {
        ObjectResult refusal = result.Result.Should().BeOfType<ObjectResult>().Subject;
        refusal.StatusCode.Should().Be(402);
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refusal.Value));
        return body.RootElement.GetProperty("required").GetDouble();
    }

    [Fact]
    public async Task Dashboard_execute_never_turns_egress_on_from_an_input()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db, balanceMillis: 1_000_000_000);

        ActionResult<WorkflowExecutionResponse> result = await Dashboard(db, owner).Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(Brief: Brief(Attack)), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        await AssertNoEgressAsync(db, workflow.Id);
    }

    [Fact]
    public async Task Workflow_assistant_execute_never_turns_egress_on_from_an_input()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db, balanceMillis: 1_000_000_000);

        ActionResult<WorkflowExecutionResponse> result = await WorkflowAssistant(db, owner).Execute(
            project.Id, workflow.Id, new ExecuteWorkflowRequest(Brief: Brief(Attack)), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        await AssertNoEgressAsync(db, workflow.Id);
    }

    [Fact]
    public async Task Assistant_ExecuteWorkflow_prices_bound_steps_that_never_carry_egress_and_leaves_the_unbound_step_alone()
    {
        using InferenceApiDbContext db = NewDb();
        (Project project, WorkflowDefinition workflow, Guid owner) = await SeedAsync(db, balanceMillis: 1_000_000_000);
        List<IReadOnlyList<WorkflowStep>> seen = [];
        Mock<IWorkflowExecutionPreflight> preflight = new();
        preflight.Setup(p => p.CheckStepsAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<IReadOnlyCollection<int>?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Callback((Guid? _, IReadOnlyList<WorkflowStep> steps, IReadOnlyCollection<int>? _, int? _, CancellationToken _) => seen.Add(steps))
            .ReturnsAsync(new WorkflowExecutionPreflightResult(false, false, 12, 1, 8, 12, false));
        ReelBolt.Inference.Api.Agents.Tools.AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(
            db, new FakeCurrentUser(owner), appInputs: new AppInputValueValidator(db), executionPreflight: preflight.Object);

        await provider.GetTools(new ReelBolt.Inference.Api.Agents.Tools.AssistantToolContext(owner, project.Id)).Single(t => t.Name == "ExecuteWorkflow")
            .InvokeAsync(new AIFunctionArguments
            {
                ["workflowId"] = workflow.Id,
                ["regenerateVideoClips"] = false,
                ["brief"] = J(JsonSerializer.Serialize(new { inputs = new { videoPrompt = Attack } })),
            });

        IReadOnlyList<WorkflowStep> priced = seen.Should().ContainSingle().Subject;
        priced.Single(s => s.StepOrder == 1).VideoGenerateConfigJson!.Replace(" ", "").Should().Contain("\"allowSourceMediaEgress\":false")
            .And.NotContain("\"allowSourceMediaEgress\":true");
        WorkflowStep unbound = priced.Single(s => s.StepOrder == 2);
        unbound.Should().BeEquivalentTo(workflow.Steps.Single(s => s.StepOrder == 2), o => o.Excluding(s => s.WorkflowDefinition).Excluding(s => s.AgentDefinition));
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Inputs_do_not_change_the_hold_of_a_step_they_do_not_bind()
    {
        // Balance 0 refuses both runs with 402 { required }: the required hold is the same with and without inputs.
        using InferenceApiDbContext without = NewDb();
        (Project p1, WorkflowDefinition w1, Guid o1) = await SeedAsync(without, balanceMillis: 0);
        double plain = RequiredOf(await Dashboard(without, o1).Execute(p1.Id, w1.Id, new ExecuteWorkflowRequest(), CancellationToken.None));

        using InferenceApiDbContext with = NewDb();
        (Project p2, WorkflowDefinition w2, Guid o2) = await SeedAsync(with, balanceMillis: 0);
        double filled = RequiredOf(await Dashboard(with, o2).Execute(p2.Id, w2.Id, new ExecuteWorkflowRequest(Brief: Brief("A barista pours latte art")), CancellationToken.None));

        filled.Should().Be(plain);
        plain.Should().BeGreaterThan(0);
    }
}
