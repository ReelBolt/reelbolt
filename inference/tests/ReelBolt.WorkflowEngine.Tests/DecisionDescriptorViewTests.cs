using System;
using System.Collections.Generic;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="DecisionDescriptorView"/> — the BOUNDED state a Gate-mode
/// Colorist/MusicSupervisor step sends to a hosted Decision provider.
///
/// <para>
/// The two properties that matter are that the projection is <b>narrow</b> (only the descriptors the
/// question set actually needs, never the agent's full input, never transcript text) and that it is
/// <b>bounded</b> (a hard character cap, whatever the source view's size), because an unbounded or
/// over-broad state is both a context-budget bug and a data-egress one.
/// </para>
/// </summary>
public class DecisionDescriptorViewTests
{
    private const string TranscriptSentence = "THIS IS TRANSCRIPT TEXT THAT MUST NEVER LEAVE THE HOST";

    /// <summary>
    /// A <c>VideoAnalyze</c> <c>{view, meta}</c> envelope shaped like the real one: shots carry the
    /// measured colour words AND a transcript-derived segment sits beside them, so the
    /// "no transcript text" assertion has something real to catch — a projection that fell back to
    /// the whole view would carry the segment's text.
    /// </summary>
    private static string AnalyzeView(string shots) => $$$"""
        {"view":{"media":{"durationSec":12.0,"fpsNum":30,"fpsDen":1,"width":1920,"height":1080},
          "shots":[{{{shots}}}],
          "silences":[],
          "segments":[{"id":"t0","startSec":0.0,"endSec":4.0,"text":"{{{TranscriptSentence}}}"}],
          "musicTracks":[{"id":"m0","name":"calm-piano-loop.mp3"},{"id":"m1","name":"stinger-hit.wav"}]},
         "meta":{"version":2}}
        """;

    private static string Shot(string id, string temp, string tone, string sat) => $$$"""
        {"id":"{{{id}}}","startSec":0.0,"endSec":1.0,"durationSec":1.0,"v":{"motion":0.1,"temp":"{{{temp}}}","tone":"{{{tone}}}","sat":"{{{sat}}}"}}
        """;

    /// <summary>
    /// The pre-Phase-4 shape: a shot with a <c>v</c> node but no D1-D3 measured colour words. This
    /// is what a default (<c>VisualDetail = Compact</c>) analyze run produces.
    /// </summary>
    private const string ShotWithNoColourWords =
        """{"id":"s0","startSec":0.0,"endSec":1.0,"durationSec":1.0,"v":{"motion":0.1,"bright":0.5,"contrast":0.2}}""";

    private static List<StepOutputHistoryEntry> History(int analyzeStepOrder, string output) =>
        [new StepOutputHistoryEntry(analyzeStepOrder, "VideoAnalyze", output)];

    private static StepExecutionContext CreateContext(
        AgentType agentType, int stepOrder, IReadOnlyList<StepOutputHistoryEntry> history)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agentType, Name = agentType.ToString() }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private static StepExecutionContext ColoristContext(IReadOnlyList<StepOutputHistoryEntry> history)
        => CreateContext(AgentType.Colorist, stepOrder: 3, history);

    private static StepExecutionContext MusicContext(IReadOnlyList<StepOutputHistoryEntry> history)
        => CreateContext(AgentType.MusicSupervisor, stepOrder: 3, history);

    [Fact]
    public void ForColorist_adds_the_outline_colour_rows_of_shots_past_the_first_section()
    {
        string view = AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal")).Replace("\"musicTracks\"", "\"outline\":{\"colour\":[\"s0 temp=Warm tone=Neutral sat=Normal\",\"s88 temp=Cool tone=Deepened sat=Muted\"]},\"musicTracks\"");

        string state = DecisionDescriptorView.ForColorist(ColoristContext(History(1, view)));

        state.Should().Contain("s88 temp=Cool tone=Deepened sat=Muted");
        state.Split("s0 ").Length.Should().Be(2, "s0 is listed once");
    }

    [Fact]
    public void ForColorist_projects_the_per_shot_measured_colour_words()
    {
        string view = AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal") + "," + Shot("s1", "Cool", "Deepened", "Muted"));

        string state = DecisionDescriptorView.ForColorist(ColoristContext(History(1, view)));

        state.Should().NotBeNullOrWhiteSpace();
        state.Should().Contain("s0").And.Contain("temp=Warm").And.Contain("tone=Neutral").And.Contain("sat=Normal");
        state.Should().Contain("s1").And.Contain("temp=Cool").And.Contain("tone=Deepened").And.Contain("sat=Muted");
    }

    [Fact]
    public void ForColorist_projection_contains_no_transcript_text()
    {
        string view = AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal"));

        string state = DecisionDescriptorView.ForColorist(ColoristContext(History(1, view)));

        state.Should().NotContain(TranscriptSentence);
        state.Should().NotContain("t0");
    }

    [Fact]
    public void ForColorist_projection_stays_under_the_hard_character_cap()
    {
        // Deliberately far more shots than any real source: the cap must hold by construction, not
        // because a realistic source happens to be small.
        var shots = new List<string>();
        for (int i = 0; i < 500; i++)
            shots.Add(Shot($"s{i}", "Warm", "Neutral", "Normal"));

        string state = DecisionDescriptorView.ForColorist(ColoristContext(History(1, AnalyzeView(string.Join(",", shots)))));

        state.Length.Should().BeLessThanOrEqualTo(DecisionDescriptorView.MaxStateChars);
        state.Should().Contain("omitted");
    }

    [Fact]
    public void ForColorist_is_empty_when_the_shots_carry_no_measured_colour_words()
    {
        DecisionDescriptorView.ForColorist(ColoristContext(History(1, AnalyzeView(ShotWithNoColourWords)))).Should().BeEmpty();
    }

    [Fact]
    public void ForColorist_is_empty_when_the_analyze_step_is_not_in_history()
    {
        DecisionDescriptorView.ForColorist(ColoristContext([])).Should().BeEmpty();
    }

    [Fact]
    public void ForColorist_is_empty_when_no_earlier_step_produced_a_view_envelope()
    {
        DecisionDescriptorView.ForColorist(ColoristContext(History(1, """{"not":"a view envelope"}"""))).Should().BeEmpty();
    }

    [Fact]
    public void ForMusicSupervisor_projects_the_offered_candidate_descriptors()
    {
        string state = DecisionDescriptorView.ForMusicSupervisor(
            MusicContext(History(1, AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal")))));

        state.Should().NotBeNullOrWhiteSpace();
        state.Should().Contain("m0").And.Contain("calm-piano-loop.mp3");
        state.Should().Contain("m1").And.Contain("stinger-hit.wav");
    }

    [Fact]
    public void ForMusicSupervisor_projection_contains_no_transcript_text()
    {
        string state = DecisionDescriptorView.ForMusicSupervisor(
            MusicContext(History(1, AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal")))));

        state.Should().NotContain(TranscriptSentence);
    }

    [Fact]
    public void ForMusicSupervisor_projection_stays_under_the_hard_character_cap()
    {
        var tracks = new List<string>();
        for (int i = 0; i < 500; i++)
            tracks.Add($$"""{"id":"m{{i}}","name":"a-deliberately-long-music-file-name-number-{{i}}-that-keeps-going.mp3"}""");

        string view = $$$"""
        {"view":{"shots":[{{{Shot("s0", "Warm", "Neutral", "Normal")}}}],
          "musicTracks":[{{{string.Join(",", tracks)}}}]},
         "meta":{"version":2}}
        """;

        string state = DecisionDescriptorView.ForMusicSupervisor(MusicContext(History(1, view)));

        state.Length.Should().BeLessThanOrEqualTo(DecisionDescriptorView.MaxStateChars);
        state.Should().Contain("omitted");
    }

    [Fact]
    public void ForMusicSupervisor_is_empty_when_no_candidates_were_offered()
    {
        string view = """
        {"view":{"shots":[{"id":"s0","startSec":0.0,"endSec":1.0}]},"meta":{"version":2}}
        """;

        DecisionDescriptorView.ForMusicSupervisor(MusicContext(History(1, view))).Should().BeEmpty();
    }

    [Fact]
    public void ForMusicSupervisor_is_empty_when_the_analyze_step_is_not_in_history()
    {
        DecisionDescriptorView.ForMusicSupervisor(MusicContext([])).Should().BeEmpty();
    }

    [Fact]
    public void Only_earlier_steps_are_considered()
    {
        // The analyze step sits AFTER the consumer in history, so it is not an upstream input.
        string view = AnalyzeView(Shot("s0", "Warm", "Neutral", "Normal"));

        DecisionDescriptorView.ForColorist(ColoristContext(History(analyzeStepOrder: 9, view))).Should().BeEmpty();
    }

    [Fact]
    public void Prompt_output_override_is_honoured()
    {
        // The override is the prompt-facing rendering; the raw entry is what SetPromptOutputOverride
        // deliberately replaced. The projection must read the same one the agent's prompt would.
        StepExecutionContext context = ColoristContext(History(1, """{"view":{"shots":[]},"meta":{}}"""));
        context.SetPromptOutputOverride(1, AnalyzeView(Shot("s0", "Vibrant", "Neutral", "Normal")));

        DecisionDescriptorView.ForColorist(context).Should().Contain("temp=Vibrant");
    }
}
