using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Production;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Structured-answer isolation and the outline view budget: regressions for follow-up review findings.</summary>
public class StructuredAnswerAndOutlineBudgetTests
{
    [Theory]
    [InlineData("Using cues [] as default.\n{\"cues\":[{\"clipId\":\"x1\"}],\"planRationale\":\"r\"}")]
    [InlineData("Shot [3] is best.\n{\"cues\":[],\"planRationale\":\"r\"}")]
    public void a_bracketed_json_literal_in_the_prefix_does_not_win_over_the_answer(string text)
    {
        (string output, _) = StructuredAnswer.Isolate(text, System.Text.Json.JsonValueKind.Object);

        output.Should().StartWith("{\"cues\"");
    }

    [Theory]
    [InlineData("{\"cues\":[{\"clipId\":\"x1\",\"anchor\":\"e3\"},{\"clipId\":\"x2\",\"anch")]
    [InlineData("{\"cues\":[{\"clipId\":\"x1\",\"anchor\":\"e3\"}], \"planRationale\": \"x\" oops}")]
    public void a_truncated_or_malformed_answer_is_not_replaced_by_an_inner_fragment(string text)
    {
        (string output, _) = StructuredAnswer.Isolate(text, System.Text.Json.JsonValueKind.Object);

        output.Should().Be(text, "an inner object or array of a broken answer is not the answer");
    }

    private static VideoAnalysisShotVisual Vis() => new(0, 0, 0, "Static", "None", 0, 0, 0, [], 0.5, 0.1, 0.2, 0, 0, 0.3, [], [], null, null, null,
        false, null, null, null, false, ColorTemperatureClass: "Warm", ToneClass: "Flat", SaturationClass: "Muted");

    private static VideoAnalysisArtifact Long()
    {
        List<VideoAnalysisShot> shots = [.. Enumerable.Range(0, 400).Select(i => new VideoAnalysisShot("s" + i, i * 3.0, i * 3.0 + 3, Vis()))];
        List<VideoAnalysisSegment> segs = [.. Enumerable.Range(0, 1200).Select(i => new VideoAnalysisSegment("t" + i, null, i * 1.0, i * 1.0 + 0.5, "word" + i))];
        VideoAnalysisMedia media = new(1201, 30, 1, 1920, 1080, "h264", "aac");
        return new VideoAnalysisArtifact(2, media, shots, [], segs, [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Sources: [new VideoAnalysisSourceInfo(0, "k", media)]);
    }

    private static (JsonObject View, JsonObject Meta) Run(int limit)
    {
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true };
        JsonObject view = new() { ["shots"] = new JsonArray(), ["silences"] = new JsonArray(), ["segments"] = new JsonArray() };
        JsonObject meta = new() { ["droppedItems"] = 5, ["effectiveMaxOutputChars"] = limit };
        VideoSpeechOutlineView.Apply(view, meta, Long(), config);
        return (view, meta);
    }

    [Fact]
    public void phrases_plus_colour_never_push_the_view_past_its_limit()
    {
        (JsonObject full, _) = Run(200_000);
        int p = full["outline"]!["phrases"]!.ToJsonString().Length;
        int c = full["outline"]!["colour"]!.ToJsonString().Length;
        int limit = full.ToJsonString().Length - System.Math.Min(p, c) / 2;

        (JsonObject view, _) = Run(limit);

        view.ToJsonString().Length.Should().BeLessThanOrEqualTo(limit,
            $"phrases ({p}) and colour ({c}) are each checked against the limit alone");
    }

    [Fact]
    public void the_colour_note_survives_when_the_phrase_list_is_left_out()
    {
        (_, JsonObject meta) = Run(2_000);

        meta["outline_summarised"]!.GetValue<string>().Should().Contain("colour");
    }

    [Fact]
    public void An_array_schema_takes_the_array_not_an_object_in_the_prefix()
    {
        (string output, _) = StructuredAnswer.Isolate("Used {\"a\":1} before.\n[{\"b\":2}]", System.Text.Json.JsonValueKind.Array);

        output.Should().Be("[{\"b\":2}]");
    }

    [Fact]
    public void Sound_designer_prompt_asks_for_strong_events_and_keeps_one_empty_rule_in_both_copies()
    {
        string fallback = (string)typeof(SoundDesignerAgent).GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var agents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        agents[AgentType.SoundDesigner].SystemPrompt.Should().Be(fallback);
        fallback.Should().Contain("The view may list strong events").And.Contain("Never write a rationale of the form \"no effects needed\" while strong events are listed");
        fallback.Should().Contain("or the view lists no strong event,");
        fallback.Should().NotContain("or the edit needs no effects");
    }
}
