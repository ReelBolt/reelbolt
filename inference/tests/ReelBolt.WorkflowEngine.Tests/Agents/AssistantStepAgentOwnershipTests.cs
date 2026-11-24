using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// SQFU-S7, save-time half: the A8 check on a step's OWN agent (a platform built-in or one of the project
/// organization's agents) runs on every path that writes workflow steps, not only REST. The assistant/MCP
/// ProposeWorkflow and ApplyWorkflow tools and the update branch of the assistant controller's apply refuse
/// another organization's agent with REST's message before writing anything.
/// </summary>
public class AssistantStepAgentOwnershipTests
{
    private static string Text(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => raw?.ToString() ?? string.Empty
    };

    private static async Task<string> InvokeAsync(AssistantToolProvider provider, AssistantToolContext context, string tool, params (string, object?)[] args)
    {
        AIFunction function = provider.GetTools(context).Single(t => t.Name == tool);
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return Text(await function.InvokeAsync(arguments));
    }

    private sealed record Seeded(Guid Owner, Project Project, WorkflowDefinition Workflow, AgentDefinition BuiltIn, AgentDefinition Mine, AgentDefinition Theirs);

    private static async Task<Seeded> SeedAsync(InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var builtIn = new AgentDefinition { Id = Guid.NewGuid(), Name = "Author", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        var mine = new AgentDefinition { Id = Guid.NewGuid(), Name = "Mine", Description = "d", SystemPrompt = "p", AgentType = AgentType.Custom, OrganizationId = org, CreatedAt = DateTime.UtcNow };
        var theirs = new AgentDefinition { Id = Guid.NewGuid(), Name = "Theirs", Description = "d", SystemPrompt = "p", AgentType = AgentType.Custom, OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = builtIn.Id, StepOrder = 1, Label = "Write", StepType = StepType.Agent });
        db.Projects.Add(project);
        db.AgentDefinitions.AddRange(builtIn, mine, theirs);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return new Seeded(owner, project, workflow, builtIn, mine, theirs);
    }

    private static List<CreateWorkflowStepRequest> Steps(params Guid[] agents) =>
        [.. agents.Select((id, i) => new CreateWorkflowStepRequest(id, StepOrder: i + 1, EdgeConditionJson: null, Label: $"Step {i + 1}", StepType: "Agent"))];

    /// <summary>The token a proposal of <paramref name="steps"/> for the seeded workflow would carry.</summary>
    private static string TokenFor(InferenceApiDbContext db, Guid? workflowId, List<CreateWorkflowStepRequest> steps)
    {
        List<WorkflowStep> current = workflowId is { } id
            ? [.. db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == id)]
            : [];
        return new WorkflowEditDiffService(db).ComputeDiff(workflowId, current, steps).ConfirmationToken;
    }

    private static WorkflowAssistantController Controller(InferenceApiDbContext db, Guid owner)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Assistant:MaxExecutionsPerHour", "10" } })
            .Build();
        return new WorkflowAssistantController(
            db,
            new FakeCurrentUser(owner), TestTenancy.Access(db, new FakeCurrentUser(owner)),
            new WorkflowEditDiffService(db),
            new AssistantExecutionGuard(db, config),
            new WorkflowExecutionService(db, Moq.Mock.Of<MassTransit.IPublishEndpoint>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowExecutionService>.Instance));
    }

    [Fact]
    public async Task ProposeWorkflow_refuses_a_step_whose_agent_belongs_to_another_organization()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Seeded s = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(s.Owner));

            string proposal = await InvokeAsync(provider, new AssistantToolContext(s.Owner, s.Project.Id), "ProposeWorkflow",
                ("workflowId", s.Workflow.Id), ("name", "Product reel"), ("steps", Steps(s.BuiltIn.Id, s.Theirs.Id)));

            JsonDocument.Parse(proposal).RootElement.GetProperty("error").GetString().Should().Be("Invalid or unauthorized agent");
            provider.LastProposedDiff.Should().BeNull("a refused proposal is never handed to the page as pending");
        }
    }

    [Fact]
    public async Task ApplyWorkflow_refuses_a_step_whose_agent_belongs_to_another_organization_and_writes_nothing()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Seeded s = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(s.Owner));
            List<CreateWorkflowStepRequest> steps = Steps(s.BuiltIn.Id, s.Theirs.Id);
            // A token computed exactly as a proposal would (the model may also hold one from a proposal
            // made before this check existed): the check may not rely on Propose having run.
            string token = TokenFor(db, s.Workflow.Id, steps);

            string applied = await InvokeAsync(provider, new AssistantToolContext(s.Owner, s.Project.Id), "ApplyWorkflow",
                ("workflowId", s.Workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", token));
            string created = await InvokeAsync(provider, new AssistantToolContext(s.Owner, s.Project.Id), "ApplyWorkflow",
                ("workflowId", null), ("name", "New one"), ("steps", steps), ("confirmationToken", TokenFor(db, null, steps)));

            JsonDocument.Parse(applied).RootElement.GetProperty("error").GetString().Should().Be("Invalid or unauthorized agent");
            JsonDocument.Parse(created).RootElement.GetProperty("error").GetString().Should().Be("Invalid or unauthorized agent");
            db.ChangeTracker.Clear();
            (await db.WorkflowSteps.Where(x => x.WorkflowDefinitionId == s.Workflow.Id).Select(x => x.AgentDefinitionId).ToListAsync())
                .Should().Equal([s.BuiltIn.Id], "a refused apply leaves the stored steps exactly as they were");
            (await db.WorkflowDefinitions.CountAsync()).Should().Be(1, "and creates no workflow");
        }
    }

    [Fact]
    public async Task The_assistant_controllers_propose_refuses_another_organizations_agent()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Seeded s = await SeedAsync(db);

            ActionResult<WorkflowEditDiff> result = await Controller(db, s.Owner).Propose(
                s.Project.Id,
                new ProposeWorkflowRequest(WorkflowId: s.Workflow.Id, Name: "Product reel", Steps: Steps(s.BuiltIn.Id, s.Theirs.Id)),
                CancellationToken.None);

            BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            bad.Value!.ToString().Should().Contain("Invalid or unauthorized agent");
        }
    }

    [Fact]
    public async Task The_assistant_controllers_update_branch_refuses_another_organizations_agent()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Seeded s = await SeedAsync(db);
            List<CreateWorkflowStepRequest> steps = Steps(s.BuiltIn.Id, s.Theirs.Id);

            ActionResult<WorkflowAssistantApplyResponse> result = await Controller(db, s.Owner).Apply(
                s.Project.Id,
                new ApplyWorkflowRequest(WorkflowId: s.Workflow.Id, Name: "Product reel", Steps: steps, ConfirmationToken: TokenFor(db, s.Workflow.Id, steps)),
                CancellationToken.None);

            BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
            bad.Value!.ToString().Should().Contain("Invalid or unauthorized agent");
            db.ChangeTracker.Clear();
            (await db.WorkflowSteps.Where(x => x.WorkflowDefinitionId == s.Workflow.Id).Select(x => x.AgentDefinitionId).ToListAsync())
                .Should().Equal([s.BuiltIn.Id]);
        }
    }

    [Fact]
    public async Task Own_organization_and_built_in_agents_still_apply_through_the_tool_and_the_controller()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Seeded s = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(s.Owner));
            var context = new AssistantToolContext(s.Owner, s.Project.Id);
            List<CreateWorkflowStepRequest> steps = Steps(s.BuiltIn.Id, s.Mine.Id);

            string proposal = await InvokeAsync(provider, context, "ProposeWorkflow",
                ("workflowId", s.Workflow.Id), ("name", "Product reel"), ("steps", steps));
            string token = JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;
            string applied = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", s.Workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", token));
            JsonDocument.Parse(applied).RootElement.GetProperty("stepCount").GetInt32().Should().Be(2);

            db.ChangeTracker.Clear();
            List<CreateWorkflowStepRequest> back = Steps(s.Mine.Id);
            ActionResult<WorkflowAssistantApplyResponse> result = await Controller(db, s.Owner).Apply(
                s.Project.Id,
                new ApplyWorkflowRequest(WorkflowId: s.Workflow.Id, Name: "Product reel", Steps: back, ConfirmationToken: TokenFor(db, s.Workflow.Id, back)),
                CancellationToken.None);
            result.Result.Should().BeOfType<OkObjectResult>();
        }
    }
}
