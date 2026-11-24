using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
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

/// <summary>ProposePatchWorkflow / ProposeCloneWorkflow lint the result like ProposeWorkflow: new structural errors (WL021) are refused.</summary>
public class AssistantWorkflowPatchLintTests
{
    private const string LintCode = "WORKFLOW_LINT_ERRORS";

    private sealed class Scenario : IDisposable
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        public InferenceApiDbContext Db { get; }
        public Guid Owner { get; } = Guid.NewGuid();
        public Project Project { get; }
        public WorkflowDefinition Workflow { get; }
        public AssistantWorkflowPatchTools Tools { get; }

        public Scenario(bool mute, bool withLinter = true)
        {
            (_connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
            Db = db;
            Project = new Project { Id = Guid.NewGuid(), Name = "Spring promo", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Dictionary<AgentType, AgentDefinition> agents = [];
            foreach (AgentType type in Enum.GetValues<AgentType>())
            {
                var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
                agents[type] = agent;
                Db.AgentDefinitions.Add(agent);
            }

            Workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Owner reel", ProjectId = Project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.WorkflowDefinitions.Add(Workflow);

            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            var diff = new WorkflowEditDiffService(Db, tokens);
            foreach (CreateWorkflowStepRequest step in CeoShape(agents, mute))
                Workflow.Steps.Add(diff.BuildStep(Workflow.Id, step));
            Db.SaveChanges();

            Tools = new AssistantWorkflowPatchTools(
                Db, tokens, TestTenancy.Access(Db, new FakeCurrentUser(Owner)), diff, new StepProviderOwnershipValidator(Db),
                linter: withLinter ? new WorkflowLinter(new LintKit.FixedFactsSource(UnknownLintFacts.Instance)) : null);
        }

        private static string Compile(bool mute) =>
            "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":7,\"voiceoverMode\":\"OverMusic\",\"analysisStepOrder\":8"
            + (mute ? ",\"muteSourceAudio\":true" : string.Empty) + "}";

        private static List<CreateWorkflowStepRequest> CeoShape(Dictionary<AgentType, AgentDefinition> agents, bool mute)
        {
            Guid author = agents[AgentType.AuthorAgent].Id;
            return
            [
                new(author, 1, null, "A1"),
                new(author, 2, null, "A2"), new(author, 3, null, "A3"), new(author, 4, null, "A4"), new(author, 5, null, "A5"),
                new(agents[AgentType.CompositionReviser].Id, 6, null, "Revise", StepType: "Revise"),
                new(agents[AgentType.VideoTransform].Id, 7, null, "Voice", StepType: "Voiceover",
                    VoiceoverConfigJson: "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"hi\",\"startSec\":0}]}}"),
                new(agents[AgentType.VideoTransform].Id, 8, null, "Analyze", StepType: "VideoAnalyze",
                    VideoAnalyzeConfigJson: "{\"version\":1,\"source\":{\"kind\":\"StepOutput\",\"stepOrder\":6}}"),
                new(agents[AgentType.VideoTransform].Id, 9, null, "Compile", StepType: "VideoCompile", VideoCompileConfigJson: Compile(mute)),
            ];
        }

        public async Task<JsonElement> InvokeAsync(string tool, params (string, object?)[] args)
        {
            AIFunction function = Tools.GetTools(new AssistantToolContext(Owner, Project.Id)).Single(t => t.Name == tool);
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

        public Task<JsonElement> ProposePatchAsync(string opsJson) =>
            InvokeAsync("ProposePatchWorkflow", ("workflowId", Workflow.Id), ("operations", JsonSerializer.Deserialize<List<WorkflowPatchOperation>>(opsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))));

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private const string Unmute = """[{"op":"setStepConfig","stepOrder":9,"patch":{"muteSourceAudio":false}}]""";
    private const string Mute = """[{"op":"setStepConfig","stepOrder":9,"patch":{"muteSourceAudio":true}}]""";
    private const string Rename = """[{"op":"renameStep","stepOrder":2,"label":"Hook"}]""";

    private static void AssertRefusedWithWl021(JsonElement result)
    {
        result.GetProperty("code").GetString().Should().Be(LintCode);
        result.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("ruleId").GetString()).Should().Contain("WL021");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_patch_that_introduces_the_doubled_voiceover_is_refused()
    {
        using var s = new Scenario(mute: true);
        AssertRefusedWithWl021(await s.ProposePatchAsync(Unmute));
    }

    [Fact]
    public async Task A_patch_on_a_workflow_that_already_has_the_error_is_not_refused_for_something_unrelated()
    {
        using var s = new Scenario(mute: false);
        JsonElement result = await s.ProposePatchAsync(Rename);
        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_fixing_patch_is_accepted()
    {
        using var s = new Scenario(mute: false);
        JsonElement result = await s.ProposePatchAsync(Mute);
        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Apply_refuses_what_propose_would_refuse_and_writes_nothing()
    {
        using var s = new Scenario(mute: true);
        // A token minted without the linter (as before this gate existed) cannot be applied to a linted workflow shape.
        using var open = new Scenario(mute: true, withLinter: false);
        string token = (await open.ProposePatchAsync(Unmute)).GetProperty("confirmationToken").GetString()!;
        JsonElement applied = await s.InvokeAsync("ApplyPatchWorkflow", ("workflowId", s.Workflow.Id),
            ("operations", JsonSerializer.Deserialize<List<WorkflowPatchOperation>>(Unmute, new JsonSerializerOptions(JsonSerializerDefaults.Web))), ("confirmationToken", token));
        applied.GetProperty("code").GetString().Should().Be(LintCode);
        s.Db.WorkflowSteps.Single(x => x.WorkflowDefinitionId == s.Workflow.Id && x.StepOrder == 9).VideoCompileConfigJson.Should().Contain("\"muteSourceAudio\":true");
    }

    [Fact]
    public async Task Cloning_a_clean_workflow_passes()
    {
        using var s = new Scenario(mute: true);
        JsonElement result = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy"));
        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Cloning_a_workflow_that_already_has_the_error_is_not_refused()
    {
        using var s = new Scenario(mute: false);
        JsonElement result = await s.InvokeAsync("ProposeCloneWorkflow", ("workflowId", s.Workflow.Id), ("name", "Copy"));
        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Without_a_linter_nothing_changes()
    {
        using var s = new Scenario(mute: true, withLinter: false);
        JsonElement result = await s.ProposePatchAsync(Unmute);
        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
