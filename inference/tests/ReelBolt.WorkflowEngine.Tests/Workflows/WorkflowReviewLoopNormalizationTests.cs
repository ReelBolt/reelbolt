using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// Every proposal surface runs <see cref="WorkflowStepNormalizer"/> inside
/// <see cref="WorkflowEditDiffService.ComputeDiff"/>, so a model-authored review loop that would only make
/// the linter warn (WL003) is retargeted to a step that can improve, or dropped when nothing can. These
/// tests pin the four shapes and the token round-trip that lets Propose and Apply see the same graph.
/// </summary>
public class WorkflowReviewLoopNormalizationTests
{
    [Fact]
    public void A_review_over_a_compile_in_a_script_and_compile_graph_is_retargeted_to_the_author_step()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Write the script", "Agent"),
            new(agent, 2, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 2)
        ];

        IReadOnlyList<CreateWorkflowStepRequest> normalized = WorkflowStepNormalizer.Normalize(steps);

        normalized.Should().HaveCount(3);
        normalized.Single(step => step.StepOrder == 3).LoopTargetStepOrder.Should().Be(1);
    }

    [Fact]
    public void A_review_with_no_improving_step_is_dropped_and_later_references_close_up()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Analyze", "VideoAnalyze", VideoAnalyzeConfigJson: "{}"),
            new(agent, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1),
            new(agent, 3, null, "Write the script", "Agent"),
            new(agent, 4, null, "Compile", "VideoCompile", VideoCompileConfigJson: "{}",
                SelectedPriorStepOrdersJson: "[3]",
                InputMappingJson: """{"decision":{"from":"Step","stepOrder":3}}""")
        ];

        IReadOnlyList<CreateWorkflowStepRequest> normalized = WorkflowStepNormalizer.Normalize(steps);

        normalized.Should().HaveCount(3);
        normalized.Select(step => step.StepOrder).Should().Equal(1, 2, 3);
        normalized.Should().NotContain(step => step.StepType == "ReviewLoop");
        CreateWorkflowStepRequest compile = normalized.Single(step => step.Label == "Compile");
        compile.StepOrder.Should().Be(3);
        JsonNode.Parse(compile.SelectedPriorStepOrdersJson!).ToJsonString().Should().Be("[2]");
        JsonNode.Parse(compile.InputMappingJson!)!["decision"]!["stepOrder"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void A_valid_review_loop_is_byte_identical()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Write the script", "Agent"),
            new(agent, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1)
        ];

        IReadOnlyList<CreateWorkflowStepRequest> normalized = WorkflowStepNormalizer.Normalize(steps);

        normalized.Should().BeSameAs(steps);
        JsonSerializer.Serialize(normalized).Should().Be(JsonSerializer.Serialize(steps));
    }

    [Fact]
    public void A_contiguous_graph_loses_only_the_removed_loop_and_shifts_steps_and_references_above_it()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1),
            new(agent, 3, null, "Write the script", "Agent"),
            new(agent, 4, null, "Render", "VideoCompile", VideoCompileConfigJson: "{}",
                SelectedPriorStepOrdersJson: "[3]",
                InputMappingJson: """{"decision":{"from":"Step","stepOrder":3}}""")
        ];

        WorkflowNormalizationResult result = WorkflowStepNormalizer.NormalizeWithChanges(steps);

        result.Steps.Should().HaveCount(3);
        result.Steps.Select(step => step.StepOrder).Should().Equal(1, 2, 3);
        result.Steps.Should().NotContain(step => step.StepType == "ReviewLoop");
        result.Steps.Single(step => step.Label == "Compile the film").StepOrder.Should().Be(1,
            "an order below the removed loop must not move");
        CreateWorkflowStepRequest render = result.Steps.Single(step => step.Label == "Render");
        render.StepOrder.Should().Be(3, "only the steps above the removed loop move down by one");
        JsonNode.Parse(render.SelectedPriorStepOrdersJson!).ToJsonString().Should().Be("[2]");
        JsonNode.Parse(render.InputMappingJson!)!["decision"]!["stepOrder"]!.GetValue<int>().Should().Be(2);

        result.Changes.Should().HaveCount(1);
        WorkflowNormalizationChange change = result.Changes[0];
        change.Kind.Should().Be(WorkflowNormalizationChangeKind.Removed);
        change.StepOrder.Should().Be(2);
        change.Label.Should().Be("Final review");
        change.StepType.Should().Be("ReviewLoop");
        change.FromTarget.Should().Be(1);
        change.ToTarget.Should().BeNull();
    }

    [Fact]
    public void A_non_contiguous_graph_is_returned_unchanged_without_throwing()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1),
            new(agent, 5, null, "Render", "VideoCompile", VideoCompileConfigJson: "{}")
        ];

        WorkflowNormalizationResult result = WorkflowStepNormalizer.NormalizeWithChanges(steps);

        result.Steps.Should().BeSameAs(steps);
        result.Changes.Should().BeEmpty();
        WorkflowStepNormalizer.Normalize(steps).Should().BeSameAs(steps);
    }

    [Fact]
    public void A_graph_with_duplicate_orders_is_returned_unchanged()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1),
            new(agent, 2, null, "Write the script", "Agent")
        ];

        WorkflowNormalizationResult result = WorkflowStepNormalizer.NormalizeWithChanges(steps);

        result.Steps.Should().BeSameAs(steps);
        result.Changes.Should().BeEmpty();
        WorkflowStepNormalizer.Normalize(steps).Should().BeSameAs(steps);
    }

    [Fact]
    public void Retargeting_a_contiguous_graph_is_reported_with_from_and_to_targets()
    {
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Write the script", "Agent"),
            new(agent, 2, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 2)
        ];

        WorkflowNormalizationResult result = WorkflowStepNormalizer.NormalizeWithChanges(steps);

        result.Steps.Should().HaveCount(3);
        result.Steps.Single(step => step.StepOrder == 3).LoopTargetStepOrder.Should().Be(1);
        result.Changes.Should().HaveCount(1);
        WorkflowNormalizationChange change = result.Changes[0];
        change.Kind.Should().Be(WorkflowNormalizationChangeKind.Retargeted);
        change.StepOrder.Should().Be(3);
        change.Label.Should().Be("Final review");
        change.StepType.Should().Be("ReviewLoop");
        change.FromTarget.Should().Be(2);
        change.ToTarget.Should().Be(1);
    }

    [Fact]
    public void The_normalizer_is_idempotent_for_both_retargeting_and_dropping()
    {
        Guid agent = Guid.NewGuid();
        foreach (List<CreateWorkflowStepRequest> steps in new[]
        {
            new List<CreateWorkflowStepRequest>
            {
                new(agent, 1, null, "Write the script", "Agent"),
                new(agent, 2, null, "Branch", "Conditional"),
                new(agent, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 2)
            },
            new List<CreateWorkflowStepRequest>
            {
                new(agent, 1, null, "Branch", "Conditional"),
                new(agent, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1),
                new(agent, 3, null, "Write the script", "Agent")
            }
        })
        {
            IReadOnlyList<CreateWorkflowStepRequest> once = WorkflowStepNormalizer.Normalize(steps);
            IReadOnlyList<CreateWorkflowStepRequest> twice = WorkflowStepNormalizer.Normalize(once);

            twice.Should().BeSameAs(once);
            JsonSerializer.Serialize(twice).Should().Be(JsonSerializer.Serialize(once));
        }
    }

    [Fact]
    public void ComputeDiff_reports_and_tokens_the_normalized_steps_deterministically()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new InferenceApiDbContext(options);
        var service = new WorkflowEditDiffService(db);
        Guid workflowId = Guid.NewGuid();
        Guid agent = Guid.NewGuid();
        List<CreateWorkflowStepRequest> steps =
        [
            new(agent, 1, null, "Write the script", "Agent"),
            new(agent, 2, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
            new(agent, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 2)
        ];

        WorkflowEditDiff proposed = service.ComputeDiff(workflowId, [], steps);
        WorkflowEditDiff replayed = service.ComputeDiff(workflowId, [], steps);

        proposed.ProposedSteps.Should().HaveCount(3);
        proposed.ProposedSteps.Single(step => step.StepType == "ReviewLoop").LoopTargetStepOrder.Should().Be(1);
        replayed.ConfirmationToken.Should().Be(proposed.ConfirmationToken,
            "Apply recomputes the same normalization, so the model's original steps still verify");
    }

    [Fact]
    public async Task Propose_then_Apply_of_the_models_original_steps_verifies_and_saves_the_normalized_steps()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Write the script", StepType = StepType.Agent });
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 2, Label = "Compile the film", StepType = StepType.VideoCompile, VideoCompileConfigJson = "{}" });
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);

            // The model's ORIGINAL steps: the loop points at the compile and changes nothing on a repeat.
            List<CreateWorkflowStepRequest> original =
            [
                new(agent.Id, 1, null, "Write the script", "Agent"),
                new(agent.Id, 2, null, "Compile the film", "VideoCompile", VideoCompileConfigJson: "{}"),
                new(agent.Id, 3, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 2)
            ];

            string proposal = await InvokeAsync(provider, context, "ProposeWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", original));
            string token = JsonDocument.Parse(proposal).RootElement.GetProperty("confirmationToken").GetString()!;

            string applied = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", original), ("confirmationToken", token));

            JsonElement result = JsonDocument.Parse(applied).RootElement;
            result.TryGetProperty("error", out _).Should().BeFalse("the apply must verify and write, not answer stale");
            result.GetProperty("stepCount").GetInt32().Should().Be(3);

            List<WorkflowStep> stored = await db.WorkflowSteps.AsNoTracking()
                .Where(step => step.WorkflowDefinitionId == workflow.Id)
                .OrderBy(step => step.StepOrder)
                .ToListAsync();
            stored.Should().HaveCount(3);
            stored.Single(step => step.StepType == StepType.ReviewLoop).LoopTargetStepOrder
                .Should().Be(1, "the saved graph is the normalized proposal, not the raw request");
        }
    }

    [Fact]
    public async Task The_create_apply_card_describes_the_normalized_graph_that_will_be_written()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Code", Description = "d", SystemPrompt = "p", AgentType = AgentType.CodeStructureAnalyzer, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            List<CreateWorkflowStepRequest> steps =
            [
                new(agent.Id, 1, null, "Analyze", "VideoAnalyze", VideoAnalyzeConfigJson: "{}"),
                new(agent.Id, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1, ReviewLoopConfigJson: "{}")
            ];

            string proposal = await InvokeAsync(provider, context, "ProposeWorkflow",
                ("workflowId", (Guid?)null), ("name", "Product reel"), ("steps", steps));
            JsonElement proposed = JsonDocument.Parse(proposal).RootElement.Clone();

            JsonElement normalization = proposed.GetProperty("normalization").EnumerateArray().Single();
            normalization.GetProperty("change").GetString().Should().Be("removed");
            normalization.GetProperty("stepOrder").GetInt32().Should().Be(2);
            normalization.GetProperty("stepType").GetString().Should().Be("ReviewLoop");
            normalization.GetProperty("fromStepOrder").GetInt32().Should().Be(1);

            var cardBuilder = new AssistantApprovalCardBuilder(
                db, TestTenancy.Access(db, new FakeCurrentUser(owner)), new FakeCurrentUser(owner));
            var arguments = new AIFunctionArguments();
            arguments["name"] = "Product reel";
            ApprovalVerdict verdict = await cardBuilder.BuildAsync(
                "ApplyWorkflow", arguments, new AssistantToolContext(owner, project.Id),
                new ProposalRecord("ProposeWorkflow", proposed), CancellationToken.None);

            ApprovalCard card = verdict.Card!;
            card.Summary.Should().Be("A new workflow with 1 step.", "one normalized step is written");
            List<AssistantActionChange> added = [.. card.Changes!.Where(change => change.Kind == AssistantChangeKind.Add)];
            added.Should().HaveCount(1);
            added.Should().OnlyContain(change => !change.Text.Contains("Review", StringComparison.OrdinalIgnoreCase),
                "the review loop is removed, not written");
            card.Changes!.Should().Contain(change => change.Kind == AssistantChangeKind.Remove && change.Text.Contains("Final review"));
        }
    }

    [Fact]
    public async Task ProposeWorkflow_refuses_an_edit_whose_normalized_graph_orphans_an_app_input()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Code", Description = "d", SystemPrompt = "p", AgentType = AgentType.CodeStructureAnalyzer, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            var workflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Product reel",
                ProjectId = project.Id,
                AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(
                    new ReelBolt.Shared.Workflows.Apps.AppManifest(Inputs:
                        [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("brief", Field: "language", Label: "Language")])),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Analyze", StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = "{}" });
            workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 2, Label = "Final review", StepType = StepType.ReviewLoop, LoopTargetStepOrder = 1, ReviewLoopConfigJson = "{}" });
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);
            db.WorkflowDefinitions.Add(workflow);
            await db.SaveChangesAsync();

            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            List<CreateWorkflowStepRequest> steps =
            [
                new(agent.Id, 1, null, "Analyze", "VideoAnalyze", VideoAnalyzeConfigJson: "{}"),
                new(agent.Id, 2, null, "Final review", "ReviewLoop", LoopTargetStepOrder: 1, ReviewLoopConfigJson: "{}")
            ];

            string proposal = await InvokeAsync(provider, context, "ProposeWorkflow",
                ("workflowId", workflow.Id), ("name", workflow.Name), ("steps", steps));

            JsonElement result = JsonDocument.Parse(proposal).RootElement;
            result.GetProperty("error").GetString().Should().Contain("not read by any step",
                "the raw graph's ReviewLoop reads language, but normalization removes it");
            (await db.WorkflowSteps.CountAsync(step => step.WorkflowDefinitionId == workflow.Id)).Should().Be(2);
        }
    }

    private static async Task<string> InvokeAsync(AssistantToolProvider provider, AssistantToolContext context, string tool, params (string, object?)[] args)
    {
        AIFunction function = provider.GetTools(context).Single(candidate => candidate.Name == tool);
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await function.InvokeAsync(arguments);
        return raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            JsonElement element => element.GetRawText(),
            _ => raw?.ToString() ?? string.Empty
        };
    }
}
