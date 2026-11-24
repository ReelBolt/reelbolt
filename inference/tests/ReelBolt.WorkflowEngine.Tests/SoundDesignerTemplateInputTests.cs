using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Context;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The REAL "video-derush-edit-sfx" template: the sound designer's prompt input, built by the engine's own path over the analyze
/// step's output and the story editor's decision, still carries every sound-effect clip and every event.
/// </summary>
public class SoundDesignerTemplateInputTests
{
    private static string AnalyzeOutput(int filler)
    {
        JsonArray clips = [.. Enumerable.Range(0, 14).Select(i => (JsonNode)new JsonObject { ["id"] = "x" + i, ["name"] = $"sfx{i:D2}.wav" })];
        JsonArray events = [.. Enumerable.Range(0, 20).Select(i => (JsonNode)new JsonObject { ["id"] = "e" + i, ["type"] = "hit", ["strength"] = "strong" })];
        JsonArray segments = [.. Enumerable.Range(0, filler).Select(i => (JsonNode)new JsonObject { ["id"] = "t" + i, ["text"] = new string('w', 400) })];
        return new JsonObject
        {
            ["view"] = new JsonObject { ["sfxClips"] = clips, ["events"] = events, ["segments"] = segments },
            ["meta"] = new JsonObject { ["offeredIdCount"] = filler }
        }.ToJsonString();
    }

    private static StepExecutionContext Context(int filler, AgentInputBudgetOptions options)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("video-derush-edit-sfx")!;
        List<WorkflowStep> steps = [.. template.Steps.Select((s, i) => new WorkflowStep
        {
            Id = Guid.NewGuid(), WorkflowDefinitionId = Guid.NewGuid(), StepOrder = i + 1, StepType = s.StepType,
            AgentInputContextMode = s.AgentInputContextMode ?? AgentInputContextMode.PreviousStepOnly,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = s.AgentType, Name = s.AgentType.ToString() },
            AgentDefinitionId = Guid.NewGuid()
        })];
        WorkflowStep sound = steps[2];
        sound.AgentDefinition!.AgentType.Should().Be(AgentType.SoundDesigner);
        sound.AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = sound,
            AllSteps = steps,
            AccumulatedOutput = string.Empty,
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(1, "Analyze source video", AnalyzeOutput(filler)),
                new StepOutputHistoryEntry(2, "Decide which spans to keep", "{\"keep\":[{\"fromId\":\"t0\",\"toId\":\"t9\",\"reason\":\"intro\"}]}")
            ],
            CurrentStepIndex = 2,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
            BudgetOptions = options
        };
    }

    private static void AssertClipsAndEvents(string input)
    {
        foreach (int i in Enumerable.Range(0, 14))
            input.Should().Contain($"\"x{i}\"");
        foreach (int i in Enumerable.Range(0, 20))
            input.Should().Contain($"\"e{i}\"");
        input.Should().Contain("\"keep\"");
    }

    [Fact]
    public void The_sound_designer_input_carries_every_clip_and_event_at_the_default_budget()
    {
        string input = Context(30, new AgentInputBudgetOptions()).BuildAgentInput();

        AssertClipsAndEvents(input);
    }

    [Fact]
    public void The_sound_designer_input_keeps_clips_and_events_when_a_huge_analysis_is_compacted()
    {
        // A step-1 output far over the limit with only the newest step kept verbatim: the analysis is digested.
        var options = new AgentInputBudgetOptions { MaxInputChars = 20_000, RecentStepsVerbatim = 1 };
        StepExecutionContext context = Context(2000, options);

        string input = context.BuildAgentInput();

        input.Length.Should().BeLessThan(60_000, "the analysis really was compacted");
        AssertClipsAndEvents(input);
    }
}
