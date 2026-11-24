using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-201: the patch and clone tools. Every write goes through the same validation as ApplyWorkflow, and
/// every refusal is asserted to have left the stored rows exactly as they were.
/// </summary>
public class AssistantWorkflowPatchToolsTests
{
    private sealed class Scenario : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        public InferenceApiDbContext Db { get; }
        public Guid Owner { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Project SiblingProject { get; }
        public Project ForeignProject { get; }
        public AgentDefinition Agent { get; }
        public WorkflowDefinition Workflow { get; }
        public AssistantWorkflowPatchTools Tools { get; }
        public Guid Thread { get; } = Guid.NewGuid();
        public AssistantToolContext Context => new(Owner, Project.Id, ThreadId: Thread);

        public Scenario()
        {
            (_connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
            Db = db;
            Project = NewProject(Owner, "Launch Teaser");
            SiblingProject = NewProject(Owner, "Sibling");
            Guid stranger = Guid.NewGuid();
            ForeignProject = NewProject(stranger, "Foreign");
            Agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            Db.AgentDefinitions.Add(Agent);
            Workflow = NewWorkflow(Project.Id, "Product reel");
            Workflow.Steps.Add(Step(Workflow.Id, 1, "Write", StepType.Agent));
            WorkflowStep extract = Step(Workflow.Id, 2, "Pull", StepType.Extract);
            extract.ExtractConfigJson = """{"prompt":"a","options":{"x":1,"y":2}}""";
            extract.MaxIterations = 3;
            Workflow.Steps.Add(extract);
            Db.SaveChanges();

            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            Tools = new AssistantWorkflowPatchTools(
                Db, tokens, TestTenancy.Access(Db, new FakeCurrentUser(Owner)),
                new WorkflowEditDiffService(Db, tokens), new StepProviderOwnershipValidator(Db));
        }

        private Project NewProject(Guid owner, string name)
        {
            var project = new Project { Id = Guid.NewGuid(), Name = name, OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(project);
            return project;
        }

        public WorkflowDefinition NewWorkflow(Guid projectId, string name, WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard)
        {
            var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = name, ProjectId = projectId, Kind = kind, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.WorkflowDefinitions.Add(workflow);
            return workflow;
        }

        public WorkflowStep Step(Guid workflowId, int order, string label, StepType type) =>
            new() { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, AgentDefinitionId = Agent.Id, StepOrder = order, Label = label, StepType = type };

        public async Task<string> InvokeAsync(string tool, params (string, object?)[] args)
        {
            AIFunction function = Tools.GetTools(Context).Single(t => t.Name == tool);
            var arguments = new AIFunctionArguments();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await function.InvokeAsync(arguments);
            return raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty
            };
        }

        public List<WorkflowStep> Stored(Guid workflowId) =>
            Db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == workflowId).OrderBy(s => s.StepOrder).ToList();

        /// <summary>A snapshot of everything a refusal must not touch.</summary>
        public string Snapshot() => JsonSerializer.Serialize(new
        {
            workflows = Db.WorkflowDefinitions.AsNoTracking().OrderBy(w => w.Id).Select(w => new { w.Id, w.Name, w.UpdatedAt }).ToList(),
            steps = Db.WorkflowSteps.AsNoTracking().OrderBy(s => s.Id).ToList().Select(s => JsonSerializer.Serialize(s, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles })).ToList()
        });

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static JsonElement Ops(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string TokenOf(string proposal) => Parse(proposal).GetProperty("confirmationToken").GetString()!;

    private static string ErrorOf(string text) => Parse(text).GetProperty("error").GetString()!;

    private const string ConfigPatchOps =
        """[{"op":"setStepConfig","stepOrder":2,"patch":{"prompt":"b","options":{"x":null,"z":3}}}]""";

    private static readonly string OrphaningManifest = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(
        new ReelBolt.Shared.Workflows.Apps.AppManifest(Inputs:
            [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("brief", Field: "sourceLanguage", Label: "Source language")]));

    /// <summary>
    /// A contiguous 1..4 graph of steps that cannot improve on a repeat (VideoAnalyze, Extract) followed by
    /// two review loops over the Extract: normalization removes both. When <paramref name="rawGraphHasErrors"/>
    /// the RAW graph is unsavable (a ReviewLoop flagged as producing a playable output, and a loop naming a
    /// foreign inference provider), so a patch accepted here proves the gates saw the normalized graph.
    /// </summary>
    private static WorkflowDefinition AddReviewLoopWorkflow(Scenario s, bool rawGraphHasErrors)
    {
        WorkflowDefinition workflow = s.NewWorkflow(s.Project.Id, "Review loop patch target");
        WorkflowStep analyze = s.Step(workflow.Id, 1, "Analyze", StepType.VideoAnalyze);
        analyze.VideoAnalyzeConfigJson = "{}";
        WorkflowStep extract = s.Step(workflow.Id, 2, "Pull", StepType.Extract);
        extract.ExtractConfigJson = """{"prompt":"p"}""";
        WorkflowStep invalidLoop = s.Step(workflow.Id, 3, "Review A", StepType.ReviewLoop);
        invalidLoop.LoopTargetStepOrder = 2;
        invalidLoop.ReviewLoopConfigJson = "{}";
        // A ReviewLoop cannot produce a playable output, so this is a save-validator error the raw graph carries.
        invalidLoop.RegisterOutputAsProjectFile = rawGraphHasErrors;
        WorkflowStep foreignLoop = s.Step(workflow.Id, 4, "Review B", StepType.ReviewLoop);
        foreignLoop.LoopTargetStepOrder = 2;
        foreignLoop.ReviewLoopConfigJson = rawGraphHasErrors
            ? $$"""{"providerId":"{{Guid.NewGuid()}}"}"""
            : "{}";
        workflow.Steps.Add(analyze);
        workflow.Steps.Add(extract);
        workflow.Steps.Add(invalidLoop);
        workflow.Steps.Add(foreignLoop);
        s.Db.SaveChanges();
        return workflow;
    }

    [Fact]
    public async Task A_rename_is_accepted_after_normalization_removes_the_loops_the_raw_graph_would_refuse()
    {
        using var s = new Scenario();
        WorkflowDefinition workflow = AddReviewLoopWorkflow(s, rawGraphHasErrors: true);
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"Analyze a"}]""";
        string before = s.Snapshot();

        string proposed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", Ops(ops)));
        Parse(proposed).TryGetProperty("error", out _).Should().BeFalse(
            $"the raw loop config and foreign provider would refuse; the save and ownership gates must see the normalized graph (response: {proposed})");
        s.Snapshot().Should().Be(before, "a proposal never writes");

        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", workflow.Id), ("operations", Ops(ops)), ("confirmationToken", TokenOf(proposed)));
        Parse(applied).TryGetProperty("error", out _).Should().BeFalse("apply verifies and gates the same normalized graph");
        Parse(applied).GetProperty("stepCount").GetInt32().Should().Be(2);

        List<WorkflowStep> stored = s.Stored(workflow.Id);
        stored.Should().HaveCount(2);
        stored.Should().NotContain(step => step.StepType == StepType.ReviewLoop);
    }

    [Fact]
    public async Task An_input_read_only_by_the_removed_loops_is_refused_by_the_orphan_gate_at_propose_and_apply()
    {
        using var s = new Scenario();
        WorkflowDefinition workflow = AddReviewLoopWorkflow(s, rawGraphHasErrors: false);
        workflow.AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(
            new ReelBolt.Shared.Workflows.Apps.AppManifest(Inputs:
                [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("brief", Field: "language", Label: "Language")]));
        s.Db.SaveChanges();
        string before = s.Snapshot();
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"Analyze a"}]""";

        string proposed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", Ops(ops)));
        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", workflow.Id), ("operations", Ops(ops)), ("confirmationToken", "x"));

        ErrorOf(proposed).Should().Contain("not read by any step",
            "the ReviewLoops that read language are removed by normalization, so the manifest input is orphaned");
        ErrorOf(applied).Should().Contain("not read by any step");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Apply_writes_the_normalized_graph_and_the_proposed_token_stays_valid()
    {
        using var s = new Scenario();
        WorkflowDefinition workflow = AddReviewLoopWorkflow(s, rawGraphHasErrors: true);
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"Analyze a"}]""";

        string proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", Ops(ops)));
        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", workflow.Id), ("operations", Ops(ops)), ("confirmationToken", TokenOf(proposal)));

        Parse(applied).GetProperty("stepCount").GetInt32().Should().Be(2);
        List<WorkflowStep> stored = s.Stored(workflow.Id);
        stored.Select(step => step.StepOrder).Should().Equal(1, 2);
        stored.Should().NotContain(step => step.StepType == StepType.ReviewLoop);
        stored[0].Label.Should().Be("Analyze a");
        stored[1].StepType.Should().Be(StepType.Extract);
    }

    [Fact]
    public async Task Propose_reports_the_removed_review_loops_and_apply_writes_the_normalized_graph()
    {
        using var s = new Scenario();
        WorkflowDefinition workflow = AddReviewLoopWorkflow(s, rawGraphHasErrors: false);
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"Analyze a"}]""";

        string proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", Ops(ops)));

        JsonElement proposed = Parse(proposal);
        JsonElement[] normalization = [.. proposed.GetProperty("normalization").EnumerateArray()];
        normalization.Should().HaveCount(2);
        normalization.Should().OnlyContain(entry => entry.GetProperty("change").GetString() == "removed");
        normalization.Select(entry => entry.GetProperty("stepOrder").GetInt32()).Should().BeEquivalentTo(new[] { 3, 4 },
            "the loops are reported at their original orders");
        normalization.Should().OnlyContain(entry => entry.GetProperty("stepType").GetString() == "ReviewLoop");
        normalization.Should().OnlyContain(entry => entry.GetProperty("fromStepOrder").GetInt32() == 2);
        normalization.Should().OnlyContain(entry => entry.EnumerateObject().All(p => p.Name != "toStepOrder"));

        JsonElement[] removed = [.. proposed.GetProperty("changedSteps").EnumerateArray()
            .Where(entry => entry.TryGetProperty("removed", out JsonElement flag) && flag.GetBoolean())];
        removed.Select(entry => entry.GetProperty("stepOrder").GetInt32()).Should().BeEquivalentTo(new[] { 3, 4 });

        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", workflow.Id), ("operations", Ops(ops)), ("confirmationToken", TokenOf(proposal)));
        Parse(applied).TryGetProperty("error", out _).Should().BeFalse();
        Parse(applied).GetProperty("stepCount").GetInt32().Should().Be(2);
        s.Stored(workflow.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_changed_step_above_a_removed_loop_is_reported_at_its_written_order_with_was()
    {
        using var s = new Scenario();
        WorkflowDefinition workflow = s.NewWorkflow(s.Project.Id, "Moved step target");
        WorkflowStep analyze = s.Step(workflow.Id, 1, "Analyze", StepType.VideoAnalyze);
        analyze.VideoAnalyzeConfigJson = "{}";
        WorkflowStep loop = s.Step(workflow.Id, 2, "Review", StepType.ReviewLoop);
        loop.LoopTargetStepOrder = 1;
        loop.ReviewLoopConfigJson = "{}";
        WorkflowStep write = s.Step(workflow.Id, 3, "Write", StepType.Agent);
        WorkflowStep compile = s.Step(workflow.Id, 4, "Compile", StepType.VideoCompile);
        compile.VideoCompileConfigJson = "{}";
        workflow.Steps.Add(analyze);
        workflow.Steps.Add(loop);
        workflow.Steps.Add(write);
        workflow.Steps.Add(compile);
        s.Db.SaveChanges();
        const string ops = """[{"op":"renameStep","stepOrder":4,"label":"Compile the film"}]""";

        string proposal = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", Ops(ops)));

        JsonElement moved = Parse(proposal).GetProperty("changedSteps").EnumerateArray().Single(entry => entry.TryGetProperty("was", out _));
        moved.GetProperty("stepOrder").GetInt32().Should().Be(3, "the removed loop closes the order above it");
        moved.GetProperty("was").GetInt32().Should().Be(4);
        JsonElement label = moved.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("field").GetString() == "label");
        label.GetProperty("before").GetString().Should().Be("Compile");
        label.GetProperty("after").GetString().Should().Be("Compile the film");
    }

    [Fact]
    public async Task A_clone_is_recorded_as_made_by_the_assistant_in_this_thread()
    {
        using var s = new Scenario();
        string proposal = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy"));
        string applied = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy"), ("confirmationToken", TokenOf(proposal)));

        WorkflowDefinition clone = s.Db.WorkflowDefinitions.AsNoTracking().Single(w => w.Id == Parse(applied).GetProperty("workflowId").GetGuid());
        clone.CreatedVia.Should().Be(WorkflowCreatedVia.Assistant);
        clone.CreatedInThreadId.Should().Be(s.Thread);
    }

    [Fact]
    public async Task A_patch_that_leaves_an_exposed_app_input_orphaned_is_refused_at_propose_and_at_apply_and_writes_nothing()
    {
        using var s = new Scenario();
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"Draft"}]""";
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops))));
        s.Workflow.AppManifestJson = OrphaningManifest;
        s.Db.SaveChanges();
        string before = s.Snapshot();

        ErrorOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)))).Should().Contain("not read by any step");
        ErrorOf(await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)), ("confirmationToken", token)))
            .Should().Contain("not read by any step");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Propose_returns_a_compact_diff_of_only_the_changed_step_and_writes_nothing()
    {
        using var s = new Scenario();
        string before = s.Snapshot();

        string text = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)));

        JsonElement diff = Parse(text);
        diff.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
        diff.GetProperty("changesNothing").GetBoolean().Should().BeFalse();
        diff.GetProperty("workflowName").GetString().Should().Be("Product reel");
        diff.GetProperty("modifications").GetArrayLength().Should().Be(1);
        diff.GetProperty("modifications")[0].GetProperty("stepOrder").GetInt32().Should().Be(2);
        JsonElement changed = diff.GetProperty("changedSteps");
        changed.GetArrayLength().Should().Be(1, "step 1 did not change and must not be listed");
        string fields = changed[0].GetProperty("fields").GetRawText();
        fields.Should().Contain("extractConfigJson.prompt").And.Contain("extractConfigJson.options.x").And.Contain("extractConfigJson.options.z");
        s.Snapshot().Should().Be(before, "a proposal never writes");
    }

    [Fact]
    public async Task Apply_writes_the_rfc7396_merge_where_null_deletes_and_untouched_keys_survive()
    {
        using var s = new Scenario();
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps))));

        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)), ("confirmationToken", token));

        Parse(applied).GetProperty("stepCount").GetInt32().Should().Be(2);
        List<WorkflowStep> stored = s.Stored(s.Workflow.Id);
        stored.Should().HaveCount(2);
        JsonNode config = JsonNode.Parse(stored[1].ExtractConfigJson!)!;
        config["prompt"]!.GetValue<string>().Should().Be("b");
        config["options"]!["x"].Should().BeNull("a null in the patch deletes the key");
        config["options"]!["y"]!.GetValue<int>().Should().Be(2, "a key the patch does not mention survives");
        config["options"]!["z"]!.GetValue<int>().Should().Be(3);
        stored[1].MaxIterations.Should().Be(3, "fields outside the config are untouched");
        stored[0].Label.Should().Be("Write");
    }

    [Fact]
    public async Task RenameStep_and_setStepField_change_only_those_fields()
    {
        using var s = new Scenario();
        const string ops = """
            [{"op":"renameStep","stepOrder":1,"label":"Draft"},
             {"op":"setStepField","stepOrder":2,"field":"maxIterations","value":5},
             {"op":"setStepField","stepOrder":1,"field":"registerOutputAsProjectFile","value":true}]
            """;
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops))));

        await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)), ("confirmationToken", token));

        List<WorkflowStep> stored = s.Stored(s.Workflow.Id);
        stored[0].Label.Should().Be("Draft");
        stored[1].MaxIterations.Should().Be(5);
        stored[0].RegisterOutputAsProjectFile.Should().BeTrue();
        stored[1].ExtractConfigJson.Should().Contain("\"prompt\"");
    }

    [Theory]
    [InlineData("""[{"op":"setStepField","stepOrder":1,"field":"agentSecret","value":"x"}]""", "not an allowed field")]
    [InlineData("""[{"op":"dropStep","stepOrder":1}]""", "unknown op")]
    [InlineData("""[{"op":"renameStep","stepOrder":9,"label":"x"}]""", "no step 9")]
    [InlineData("""[{"op":"setStepConfig","stepOrder":1,"patch":{"a":1}}]""", "no per-type config")]
    [InlineData("""[{"op":"setStepConfig","stepOrder":2,"patch":[1,2]}]""", "JSON object")]
    [InlineData("""[]""", "at least one")]
    public async Task Malformed_operations_are_refused_in_propose_and_apply_and_write_nothing(string ops, string expected)
    {
        using var s = new Scenario();
        string before = s.Snapshot();

        string proposed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));
        string applied = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)), ("confirmationToken", "whatever"));

        ErrorOf(proposed).Should().Contain(expected);
        ErrorOf(applied).Should().Contain(expected);
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task More_than_fifty_operations_are_refused()
    {
        using var s = new Scenario();
        string ops = "[" + string.Join(",", Enumerable.Repeat("""{"op":"renameStep","stepOrder":1,"label":"x"}""", 51)) + "]";

        string text = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));

        ErrorOf(text).Should().Contain("50");
    }

    [Fact]
    public async Task A_patch_whose_result_fails_save_validation_is_refused_with_the_validator_errors_and_nothing_is_written()
    {
        using var s = new Scenario();
        string before = s.Snapshot();
        const string ops = """[{"op":"setStepField","stepOrder":1,"field":"continueCompositionJson","value":"{not json"}]""";

        string proposed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));
        string applied = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)), ("confirmationToken", "x"));

        foreach (string text in new[] { proposed, applied })
        {
            Parse(text).GetProperty("errors").GetArrayLength().Should().BeGreaterThan(0);
            ErrorOf(text).Should().Contain("Step 1");
        }

        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_patch_pointing_a_step_result_source_at_a_run_of_another_project_is_refused()
    {
        using var s = new Scenario();
        WorkflowStep analyze = s.Step(s.Workflow.Id, 3, "Look", StepType.VideoAnalyze);
        analyze.VideoAnalyzeConfigJson = """{"prompt":"p"}""";
        s.Db.WorkflowSteps.Add(analyze);
        s.Db.SaveChanges();
        string before = s.Snapshot();
        string ops = $$$$"""[{"op":"setStepConfig","stepOrder":3,"patch":{"source":{"kind":"StepResult","stepResultId":"{{{{Guid.NewGuid()}}}}"}}}]""";

        string proposed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ops)));

        ErrorOf(proposed).Should().Contain("not found in this project");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_token_goes_stale_when_the_stored_workflow_changed_after_the_proposal()
    {
        using var s = new Scenario();
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps))));
        WorkflowStep tracked = s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 2);
        tracked.ExtractConfigJson = """{"prompt":"someone else edited this"}""";
        s.Db.SaveChanges();
        string before = s.Snapshot();

        string applied = await s.InvokeAsync("ApplyPatchWorkflow",
            ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)), ("confirmationToken", token));

        ErrorOf(applied).Should().Contain("stale confirmationToken");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task A_fabricated_token_and_a_different_operation_list_are_both_stale()
    {
        using var s = new Scenario();
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps))));
        const string other = """[{"op":"renameStep","stepOrder":1,"label":"Sneaky"}]""";
        string before = s.Snapshot();

        string fabricated = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)), ("confirmationToken", "nope"));
        string swapped = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(other)), ("confirmationToken", token));

        ErrorOf(fabricated).Should().Contain("stale");
        ErrorOf(swapped).Should().Contain("stale");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Applying_the_same_patch_twice_answers_already_applied()
    {
        using var s = new Scenario();
        string token = TokenOf(await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps))));
        await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)), ("confirmationToken", token));

        string again = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id), ("operations", Ops(ConfigPatchOps)), ("confirmationToken", token));

        Parse(again).GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_workflow_of_a_foreign_project_and_a_hidden_kind_read_as_not_found_for_patching()
    {
        using var s = new Scenario();
        WorkflowDefinition foreign = s.NewWorkflow(s.ForeignProject.Id, "Theirs");
        foreign.Steps.Add(s.Step(foreign.Id, 1, "x", StepType.Agent));
        WorkflowDefinition hidden = s.NewWorkflow(s.Project.Id, "Revisions", WorkflowDefinitionKind.CompositionRevision);
        hidden.Steps.Add(s.Step(hidden.Id, 1, "x", StepType.Agent));
        s.Db.SaveChanges();
        string before = s.Snapshot();
        const string ops = """[{"op":"renameStep","stepOrder":1,"label":"x"}]""";

        string guessed = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", foreign.Id), ("operations", Ops(ops)));
        string viaProject = await s.InvokeAsync("ProposePatchWorkflow", ("workflowId", foreign.Id), ("operations", Ops(ops)), ("projectId", s.ForeignProject.Id));
        string revision = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", hidden.Id), ("operations", Ops(ops)), ("confirmationToken", "x"));

        ErrorOf(guessed).Should().Be("workflow not found");
        ErrorOf(viaProject).Should().Be("project not found");
        ErrorOf(revision).Should().Be("workflow not found");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Clone_into_the_same_project_copies_every_scalar_column_of_every_step()
    {
        using var s = new Scenario();
        WorkflowStep rich = s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 2);
        rich.EdgeConditionJson = """{"if":"x"}""";
        rich.ConditionExpression = "a > b";
        rich.LoopSourceExpression = "items";
        rich.LoopTargetStepOrder = 1;
        rich.MinScore = 80;
        rich.InputMappingJson = """{"a":1}""";
        rich.AgentInputContextMode = AgentInputContextMode.SelectedPriorSteps;
        rich.SelectedPriorStepOrdersJson = "[1]";
        rich.CacheMode = StepCacheMode.Never;
        s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 1).RegisterOutputAsProjectFile = true;
        s.Workflow.RequiresUserInput = true;
        s.Db.SaveChanges();

        string proposal = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Product reel v2"));
        s.Db.WorkflowDefinitions.Count().Should().Be(1, "a proposal writes nothing");
        string applied = await s.InvokeAsync("ApplyCloneWorkflow",
            ("workflowId", s.Workflow.Id), ("name", "Product reel v2"), ("confirmationToken", TokenOf(proposal)));

        Guid cloneId = Parse(applied).GetProperty("workflowId").GetGuid();
        cloneId.Should().NotBe(s.Workflow.Id);
        WorkflowDefinition clone = s.Db.WorkflowDefinitions.AsNoTracking().Single(w => w.Id == cloneId);
        clone.Name.Should().Be("Product reel v2");
        clone.ProjectId.Should().Be(s.Project.Id);
        clone.RequiresUserInput.Should().BeTrue();
        clone.Kind.Should().Be(WorkflowDefinitionKind.Standard);

        List<WorkflowStep> source = s.Stored(s.Workflow.Id);
        List<WorkflowStep> copy = s.Stored(cloneId);
        copy.Should().HaveCount(source.Count);
        PropertyInfo[] scalars = typeof(WorkflowStep).GetProperties()
            .Where(p => p.Name is not (nameof(WorkflowStep.Id) or nameof(WorkflowStep.WorkflowDefinitionId))
                && (p.PropertyType.IsValueType || p.PropertyType == typeof(string)))
            .ToArray();
        scalars.Should().NotBeEmpty();
        for (int i = 0; i < source.Count; i++)
        {
            copy[i].Id.Should().NotBe(source[i].Id);
            foreach (PropertyInfo p in scalars)
                p.GetValue(copy[i]).Should().Be(p.GetValue(source[i]), $"column {p.Name} of step {source[i].StepOrder} must be cloned");
        }
    }

    [Fact]
    public async Task Clone_into_another_project_of_the_same_workspace_works_when_nothing_references_the_source_project()
    {
        using var s = new Scenario();

        string proposal = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("targetProjectId", s.SiblingProject.Id));
        string applied = await s.InvokeAsync("ApplyCloneWorkflow",
            ("workflowId", s.Workflow.Id), ("targetProjectId", s.SiblingProject.Id), ("confirmationToken", TokenOf(proposal)));

        Guid cloneId = Parse(applied).GetProperty("workflowId").GetGuid();
        s.Db.WorkflowDefinitions.AsNoTracking().Single(w => w.Id == cloneId).ProjectId.Should().Be(s.SiblingProject.Id);
        Parse(proposal).GetProperty("name").GetString().Should().Be("Product reel (copy)");
    }

    [Fact]
    public async Task A_clone_token_is_single_use_so_a_replayed_apply_writes_no_second_copy()
    {
        using var s = new Scenario();
        string proposal = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Once"));

        string first = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Once"), ("confirmationToken", TokenOf(proposal)));
        string replay = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Once"), ("confirmationToken", TokenOf(proposal)));

        Parse(first).TryGetProperty("workflowId", out _).Should().BeTrue();
        ErrorOf(replay).Should().Contain("stale confirmationToken");
        s.Db.WorkflowDefinitions.Count(w => w.Name == "Once").Should().Be(1);
    }

    [Fact]
    public async Task Clone_into_another_project_refuses_references_to_the_source_projects_runs()
    {
        using var s = new Scenario();
        WorkflowStep cont = s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 1);
        cont.ContinueCompositionJson = $$"""{"versionId":"{{Guid.NewGuid()}}"}""";
        WorkflowStep analyze = s.Step(s.Workflow.Id, 3, "Look", StepType.VideoAnalyze);
        analyze.VideoAnalyzeConfigJson = $$$"""{"source":{"kind":"StepResult","stepResultId":"{{{Guid.NewGuid()}}}"}}""";
        s.Db.WorkflowSteps.Add(analyze);
        s.Db.SaveChanges();
        string before = s.Snapshot();

        string proposed = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("targetProjectId", s.SiblingProject.Id));
        string applied = await s.InvokeAsync("ApplyCloneWorkflow",
            ("workflowId", s.Workflow.Id), ("targetProjectId", s.SiblingProject.Id), ("confirmationToken", "x"));

        foreach (string text in new[] { proposed, applied })
            ErrorOf(text).Should().Contain("Steps 1, 3").And.Contain("same project");
        s.Snapshot().Should().Be(before);
    }

    [Theory]
    [InlineData("ExtractFrame")]
    [InlineData("ThumbnailSource")]
    [InlineData("ThumbnailAnalysis")]
    public async Task Clone_into_another_project_refuses_step_result_ids_of_frame_and_thumbnail_steps(string kind)
    {
        using var s = new Scenario();
        Guid id = Guid.NewGuid();
        WorkflowStep step = kind == "ExtractFrame"
            ? s.Step(s.Workflow.Id, 3, "Frame", StepType.ExtractFrame)
            : s.Step(s.Workflow.Id, 3, "Cover", StepType.ThumbnailGenerate);
        if (kind == "ExtractFrame")
            step.ExtractFrameConfigJson = $$$"""{"source":{"kind":"StepResult","stepResultId":"{{{id}}}"},"at":"Last"}""";
        else if (kind == "ThumbnailSource")
            step.ThumbnailGenerateConfigJson = $$$"""{"analysisStepOrder":1,"selection":"AtTime","source":{"kind":"StepResult","stepResultId":"{{{id}}}"}}""";
        else
            step.ThumbnailGenerateConfigJson = $$$"""{"analysisStepResultId":"{{{id}}}"}""";
        s.Db.WorkflowSteps.Add(step);
        s.Db.SaveChanges();
        string before = s.Snapshot();

        string proposed = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("targetProjectId", s.SiblingProject.Id));

        ErrorOf(proposed).Should().Contain("Steps 3").And.Contain("same project");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Clone_into_a_foreign_project_and_clone_of_a_hidden_kind_are_refused()
    {
        using var s = new Scenario();
        WorkflowDefinition hidden = s.NewWorkflow(s.Project.Id, "Revisions", WorkflowDefinitionKind.CompositionRevision);
        hidden.Steps.Add(s.Step(hidden.Id, 1, "x", StepType.Agent));
        s.Db.SaveChanges();
        string before = s.Snapshot();

        string foreign = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("targetProjectId", s.ForeignProject.Id));
        string foreignApply = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("targetProjectId", s.ForeignProject.Id), ("confirmationToken", "x"));
        string revision = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", hidden.Id));
        string revisionApply = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", hidden.Id), ("confirmationToken", "x"));

        ErrorOf(foreign).Should().Be("project not found");
        ErrorOf(foreignApply).Should().Be("project not found");
        ErrorOf(revision).Should().Be("workflow not found");
        ErrorOf(revisionApply).Should().Be("workflow not found");
        s.Snapshot().Should().Be(before);
    }

    [Fact]
    public async Task Clone_token_is_stale_when_the_source_changed_or_the_name_differs()
    {
        using var s = new Scenario();
        string token = TokenOf(await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy A")));

        string renamed = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy B"), ("confirmationToken", token));
        s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 1).Label = "Changed";
        s.Db.SaveChanges();
        string changed = await s.InvokeAsync("ApplyCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy A"), ("confirmationToken", token));

        ErrorOf(renamed).Should().Contain("stale");
        ErrorOf(changed).Should().Contain("stale");
        s.Db.WorkflowDefinitions.Count().Should().Be(1);
    }

    [Fact]
    public void Captions_cover_the_four_tools()
    {
        AssistantWorkflowPatchTools.Captions.Keys.Should().BeEquivalentTo(
            "ProposePatchWorkflow", "ApplyPatchWorkflow", "ProposeCloneWorkflow", "ApplyCloneWorkflow");
        AssistantWorkflowPatchTools.Captions.Values.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c));
    }
}
