using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The tool half of the "proposal is out of date, but the changes are already in" fix. The confirmation token
/// hashes the workflow's CURRENT steps, so it stops verifying once the proposal is in; the tool must then say
/// "already applied" (and write nothing), and must drop the pending diff so the page never offers it again.
/// </summary>
public class AssistantApplyWorkflowIdempotencyTests
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

    private static string TokenOf(string proposal) =>
        JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;

    private static async Task<(Guid owner, Project project, AgentDefinition agent, WorkflowDefinition workflow)> SeedAsync(ReelBolt.Inference.Api.Data.InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Write", StepType = StepType.Agent });
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return (owner, project, agent, workflow);
    }

    private static List<CreateWorkflowStepRequest> TwoSteps(Guid agentId) =>
    [
        new(agentId, StepOrder: 1, EdgeConditionJson: null, Label: "Write", StepType: "Agent"),
        new(agentId, StepOrder: 2, EdgeConditionJson: null, Label: "Final review", StepType: "Agent")
    ];

    [Fact]
    public async Task Applying_a_proposal_clears_it_from_the_pending_diff_and_applying_it_again_says_already_applied()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            List<CreateWorkflowStepRequest> steps = TwoSteps(agent.Id);

            string proposal = await InvokeAsync(provider, context, "ProposeWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps));
            provider.LastProposedDiff.Should().NotBeNull("a proposal is pending until it is applied");
            provider.LastProposedDiff!.WorkflowId.Should().Be(workflow.Id);
            string token = TokenOf(proposal);

            string applied = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", token));
            JsonDocument.Parse(applied).RootElement.GetProperty("stepCount").GetInt32().Should().Be(2);
            provider.LastProposedDiff.Should().BeNull("it is in the workflow now, so the page must not be handed it again");

            // The same proposal, applied a second time (the page's tray, or the model retrying).
            DateTime updatedAt = (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).UpdatedAt;
            string again = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", token));

            JsonElement result = JsonDocument.Parse(again).RootElement;
            result.GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
            result.TryGetProperty("error", out _).Should().BeFalse("it is a success, not a conflict");
            (await db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == workflow.Id)).Should().Be(2);
            (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).UpdatedAt.Should().Be(updatedAt, "nothing was written");
        }
    }

    [Fact]
    public async Task A_workflow_created_by_apply_is_recorded_as_made_by_the_assistant_in_this_thread()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, _) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            Guid thread = Guid.NewGuid();
            var context = new AssistantToolContext(owner, project.Id, ThreadId: thread);
            List<CreateWorkflowStepRequest> steps = TwoSteps(agent.Id);

            string token = TokenOf(await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", null), ("name", "Spring promo"), ("steps", steps)));
            Guid id = JsonDocument.Parse(await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", null), ("name", "Spring promo"), ("steps", steps), ("confirmationToken", token))).RootElement.GetProperty("workflowId").GetGuid();

            WorkflowDefinition row = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == id);
            row.CreatedVia.Should().Be(WorkflowCreatedVia.Assistant);
            row.CreatedInThreadId.Should().Be(thread);
        }
    }

    [Fact]
    public async Task An_edit_that_orphans_an_exposed_app_input_is_refused_at_propose_and_at_apply_and_writes_nothing()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            List<CreateWorkflowStepRequest> steps = TwoSteps(agent.Id);
            string token = TokenOf(await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps)));

            // The workflow's app now exposes an input no step reads.
            workflow.AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(new ReelBolt.Shared.Workflows.Apps.AppManifest(
                Inputs: [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("brief", Field: "sourceLanguage", Label: "Source language")]));
            await db.SaveChangesAsync();

            string proposed = await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps));
            JsonDocument.Parse(proposed).RootElement.GetProperty("error").GetString().Should().Contain("not read by any step");
            string applied = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", token));
            JsonDocument.Parse(applied).RootElement.GetProperty("error").GetString().Should().Contain("not read by any step");
            (await db.WorkflowSteps.CountAsync(x => x.WorkflowDefinitionId == workflow.Id)).Should().Be(1);
        }
    }

    [Fact]
    public async Task A_stale_token_for_steps_the_workflow_does_not_hold_is_still_refused_and_the_proposal_stays_pending()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            List<CreateWorkflowStepRequest> steps = TwoSteps(agent.Id);

            await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps));

            string refused = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", steps), ("confirmationToken", "made-up"));

            JsonDocument.Parse(refused).RootElement.GetProperty("error").GetString().Should().Contain("stale");
            (await db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == workflow.Id)).Should().Be(1, "a refusal writes nothing");
            provider.LastProposedDiff.Should().NotBeNull("a refused apply did not consume the proposal");
        }
    }

    [Fact]
    public async Task A_proposal_carries_the_name_it_was_made_with_so_a_new_workflow_can_be_created_from_it()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, _) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));

            await InvokeAsync(provider, new AssistantToolContext(owner, project.Id), "ProposeWorkflow",
                ("workflowId", null), ("name", "Spring promo"), ("steps", TwoSteps(agent.Id)));

            provider.LastProposedDiff!.WorkflowId.Should().BeNull("null means: create a new workflow");
            provider.LastProposedDiff.Name.Should().Be("Spring promo");
        }
    }

    [Fact]
    public async Task A_stale_token_whose_apply_also_renames_the_workflow_is_not_reported_as_already_applied()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            // The steps are exactly what is stored; only the name differs. Nothing in the token covers the name.
            List<CreateWorkflowStepRequest> storedSteps = [new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Write", StepType: "Agent")];

            string result = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Brand new name"), ("steps", storedSteps), ("confirmationToken", "stale"));

            JsonDocument.Parse(result).RootElement.TryGetProperty("alreadyApplied", out _).Should().BeFalse("the rename was never made");
            (await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == workflow.Id)).Name.Should().Be("Product reel");
        }
    }

    [Fact]
    public async Task An_already_applied_answer_does_not_consume_an_unrelated_pending_proposal()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);

            // A real, still-unapplied proposal for the same workflow...
            await InvokeAsync(provider, context, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", TwoSteps(agent.Id)));
            string pending = provider.LastProposedDiff!.ConfirmationToken;

            // ...and an apply with a made-up token for the steps the workflow already holds.
            List<CreateWorkflowStepRequest> storedSteps = [new(agent.Id, StepOrder: 1, EdgeConditionJson: null, Label: "Write", StepType: "Agent")];
            string result = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", storedSteps), ("confirmationToken", "made-up"));

            JsonDocument.Parse(result).RootElement.GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
            provider.LastProposedDiff.Should().NotBeNull("it said nothing about THAT proposal");
            provider.LastProposedDiff!.ConfirmationToken.Should().Be(pending);
        }
    }

    [Fact]
    public void A_proposal_that_gives_two_steps_the_same_order_never_reads_as_changing_nothing()
    {
        Guid agent = Guid.NewGuid();
        var diff = new ReelBolt.Inference.Api.Services.Workflows.WorkflowEditDiff(
            [], [], [], "t",
            [new(agent, 1, null, "A", StepType: "Agent"), new(agent, 1, null, "A", StepType: "Agent")]);

        diff.ChangesNothing.Should().BeFalse();
    }
}
