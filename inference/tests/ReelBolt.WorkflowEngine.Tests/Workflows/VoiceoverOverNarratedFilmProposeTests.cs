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
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>WL021 through the assistant: the doubled-voiceover shape is refused as a structural error, and accepted once the source sound is muted.</summary>
public class VoiceoverOverNarratedFilmProposeTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Project _project;
    private readonly Dictionary<AgentType, AgentDefinition> _agents = [];

    public VoiceoverOverNarratedFilmProposeTests()
    {
        foreach (AgentType type in Enum.GetValues<AgentType>())
        {
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            _agents[type] = agent;
            _db.AgentDefinitions.Add(agent);
        }

        _project = new Project { Id = Guid.NewGuid(), Name = "Spring promo", OwnerId = _owner, OrganizationId = TestTenancy.OrgOf(_owner) };
        _db.Projects.Add(_project);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static string Compile(bool mute) =>
        "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":7,\"voiceoverMode\":\"OverMusic\",\"analysisStepOrder\":8"
        + (mute ? ",\"muteSourceAudio\":true" : string.Empty) + "}";

    private List<CreateWorkflowStepRequest> CeoShape(bool mute)
    {
        Guid author = _agents[AgentType.AuthorAgent].Id;
        List<CreateWorkflowStepRequest> steps =
        [
            new(_agents[AgentType.VideoTransform].Id, 1, null, "Design", StepType: "DesignSystem"),
            new(author, 2, null, "A2"), new(author, 3, null, "A3"), new(author, 4, null, "A4"), new(author, 5, null, "A5"),
            new(_agents[AgentType.CompositionReviser].Id, 6, null, "Revise", StepType: "Revise"),
            new(_agents[AgentType.VideoTransform].Id, 7, null, "Voice", StepType: "Voiceover",
                VoiceoverConfigJson: "{\"source\":{\"kind\":\"Inline\",\"lines\":[{\"text\":\"hi\",\"startSec\":0}]}}"),
            new(_agents[AgentType.VideoTransform].Id, 8, null, "Analyze", StepType: "VideoAnalyze",
                VideoAnalyzeConfigJson: "{\"version\":1,\"source\":{\"kind\":\"StepOutput\",\"stepOrder\":6}}"),
            new(_agents[AgentType.VideoTransform].Id, 9, null, "Compile", StepType: "VideoCompile", VideoCompileConfigJson: Compile(mute)),
        ];
        return steps;
    }

    private AssistantToolProvider Provider() =>
        AssistantToolTestFactory.CreateProvider(_db, new FakeCurrentUser(_owner),
            linter: new WorkflowLinter(new LintKit.FixedFactsSource(UnknownLintFacts.Instance)));

    private async Task<JsonElement> ProposeAsync(List<CreateWorkflowStepRequest> steps)
    {
        AIFunction fn = Provider().GetTools(new AssistantToolContext(_owner, _project.Id)).Single(t => t.Name == "ProposeWorkflow");
        object? raw = await fn.InvokeAsync(new AIFunctionArguments { ["workflowId"] = null, ["name"] = "Mine", ["steps"] = steps });
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task ProposeWorkflow_refuses_a_voiceover_over_a_revised_film()
    {
        JsonElement result = await ProposeAsync(CeoShape(mute: false));

        result.GetProperty("code").GetString().Should().Be("WORKFLOW_LINT_ERRORS");
        result.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("ruleId").GetString()).Should().Contain("WL021");
        result.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProposeWorkflow_accepts_the_same_shape_with_the_source_sound_muted()
    {
        JsonElement result = await ProposeAsync(CeoShape(mute: true));

        result.TryGetProperty("code", out _).Should().BeFalse();
        result.GetProperty("confirmationToken").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
