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
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// QA probe (voiceover-doubled): ApplyPatchWorkflow with the linter wired, in a fresh request-like DbContext
/// (agents NOT already tracked), must still write an accepted patch. The lint fills agent definitions onto the
/// tracked step rows before SaveChanges.
/// </summary>
public class AssistantWorkflowPatchLintQaTests
{
    private static InferenceApiDbContext On(Microsoft.Data.Sqlite.SqliteConnection connection) =>
        new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options);

    [Fact]
    public async Task Apply_of_an_accepted_patch_succeeds_in_a_fresh_context_with_the_linter_wired()
    {
        (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext seed) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using var _c = connection;
        Guid owner = Guid.NewGuid();
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        seed.Projects.Add(project);
        var author = new AgentDefinition { Id = Guid.NewGuid(), Name = "Author", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        var reviser = new AgentDefinition { Id = Guid.NewGuid(), Name = "Rev", Description = "d", SystemPrompt = "p", AgentType = AgentType.CompositionReviser, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        var vt = new AgentDefinition { Id = Guid.NewGuid(), Name = "VT", Description = "d", SystemPrompt = "p", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        seed.AgentDefinitions.AddRange(author, reviser, vt);
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "W", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        seed.WorkflowDefinitions.Add(workflow);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        var seedDiff = new WorkflowEditDiffService(seed, tokens);
        List<CreateWorkflowStepRequest> steps =
        [
            new(author.Id, 1, null, "A1"),
            new(reviser.Id, 2, null, "Revise", StepType: "Revise"),
            new(vt.Id, 3, null, "Voice", StepType: "Voiceover", VoiceoverConfigJson: "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"hi\",\"startSec\":0}]}}"),
            new(vt.Id, 4, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: "{\"version\":1,\"source\":{\"kind\":\"StepOutput\",\"stepOrder\":2}}"),
            new(vt.Id, 5, null, "Compile", StepType: "VideoCompile", VideoCompileConfigJson:
                "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":3,\"voiceoverMode\":\"OverMusic\",\"analysisStepOrder\":4}"),
        ];
        foreach (CreateWorkflowStepRequest step in steps)
            workflow.Steps.Add(seedDiff.BuildStep(workflow.Id, step));
        seed.SaveChanges();
        seed.ChangeTracker.Clear();

        // A fresh context per call, as in a request: nothing is pre-tracked.
        AssistantWorkflowPatchTools ToolsOn(InferenceApiDbContext db) => new(
            db, tokens, TestTenancy.Access(db, new FakeCurrentUser(owner)), new WorkflowEditDiffService(db, tokens), new StepProviderOwnershipValidator(db),
            linter: new WorkflowLinter(new LintKit.FixedFactsSource(UnknownLintFacts.Instance)));

        async Task<JsonElement> Invoke(AssistantWorkflowPatchTools tools, string name, params (string, object?)[] args)
        {
            AIFunction function = tools.GetTools(new AssistantToolContext(owner, project.Id)).Single(t => t.Name == name);
            var arguments = new AIFunctionArguments();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await function.InvokeAsync(arguments);
            string text = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty
            };
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        List<WorkflowPatchOperation> ops = JsonSerializer.Deserialize<List<WorkflowPatchOperation>>(
            """[{"op":"setStepConfig","stepOrder":5,"patch":{"muteSourceAudio":true}}]""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        // Probe: the workflow ALREADY has WL021. Renaming the compile step is unrelated to it and should not be refused.
        using (InferenceApiDbContext renameDb = On(connection))
        {
            List<WorkflowPatchOperation> rename = JsonSerializer.Deserialize<List<WorkflowPatchOperation>>(
                """[{"op":"renameStep","stepOrder":5,"label":"Final cut"}]""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            JsonElement renamed = await Invoke(ToolsOn(renameDb), "ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", rename));
            RenameOfCompileRefused = renamed.TryGetProperty("code", out _);
        }

        string token;
        using (InferenceApiDbContext proposeDb = On(connection))
        {
            JsonElement proposed = await Invoke(ToolsOn(proposeDb), "ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", ops));
            token = proposed.GetProperty("confirmationToken").GetString()!;
        }

        using (InferenceApiDbContext applyDb = On(connection))
        {
            JsonElement applied = await Invoke(ToolsOn(applyDb), "ApplyPatchWorkflow", ("workflowId", workflow.Id), ("operations", ops), ("confirmationToken", token));
            applied.TryGetProperty("error", out JsonElement err).Should().BeFalse(err.ToString());
            applied.GetProperty("changedSteps").GetInt32().Should().Be(1);
        }

        using InferenceApiDbContext check = On(connection);
        check.WorkflowSteps.Single(x => x.WorkflowDefinitionId == workflow.Id && x.StepOrder == 5).VideoCompileConfigJson.Should().Contain("\"muteSourceAudio\":true");
        check.AgentDefinitions.Count().Should().Be(3);
        RenameOfCompileRefused.Should().BeFalse("renaming the step that carries a pre-existing WL021 is unrelated to it");
    }

    private bool RenameOfCompileRefused;
}
