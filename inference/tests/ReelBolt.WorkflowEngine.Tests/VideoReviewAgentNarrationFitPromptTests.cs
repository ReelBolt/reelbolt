using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Quality;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The narration PICTURE-WINDOW FIT section of <c>VideoReviewAgent</c>'s system prompt.
///
/// <para>
/// Wave 1 shipped the fit solver and published the facts to the compile step's
/// <c>outputSummary["voiceover"]</c>, but this agent's prompt named <c>sentenceCheck</c>,
/// <c>openingCheck</c>, <c>seamCheck</c>, <c>pacing</c>, <c>graphics</c> and <c>music</c> and said
/// nothing at all about <c>voiceover</c> — so a narration line that overran its picture window
/// reached the reviewer as an unread number and the step still scored green. Three departments
/// flagged it independently. This file pins the section that closed that gap.
/// </para>
///
/// <para>
/// <b>Both sources, always.</b> The prompt lives in two places — <see cref="VideoReviewAgentImpl"/>'s
/// private <c>DefaultPrompt</c> (the runtime fallback) and <see cref="DatabaseSeeder"/>'s built-in
/// agent row — and <see cref="VideoStoryEditorPromptConsistencyTests"/> only pins that the two are
/// byte-identical. Byte-identity of two copies of a prompt that omits the section would still be a
/// silent failure, so every assertion here runs against BOTH copies: the theory data is the source,
/// not a single extracted string.
/// </para>
///
/// <para>
/// <c>ReviewLoopStepExecutor.ApplyNarrationFitCap</c> — the deterministic half of the same
/// guarantee — is pinned in <see cref="ReviewLoopStepExecutorTests"/>; this file is only about what
/// the reviewer is TOLD.
/// </para>
/// </summary>
public class VideoReviewAgentNarrationFitPromptTests
{
    private const string FallbackSource = "VideoReviewAgentImpl.DefaultPrompt";
    private const string SeededSource = "DatabaseSeeder.BuiltInAgents[VideoReviewAgent]";

    /// <summary>
    /// The two copies of the prompt, by name. A theory rather than two facts so a future third copy
    /// is one data row away, and so it is impossible to add an assertion to only one of them.
    /// </summary>
    public static TheoryData<string> PromptSources() => new() { FallbackSource, SeededSource };

    /// <summary>
    /// The prompt with every whitespace run collapsed to a single space. The prompt is hard-wrapped
    /// at ~90 columns, so a phrase the reviewer must read is routinely split across two source
    /// lines; asserting on the raw text would make these tests fail on a harmless re-wrap, which is
    /// exactly the kind of brittleness that gets a guard deleted rather than fixed.
    /// </summary>
    private static string Normalized(string source)
    {
        string prompt = source switch
        {
            FallbackSource => ReadPrivateStaticString(
                typeof(VideoReviewAgentImpl), "DefaultPrompt", "VideoReviewAgentImpl"),
            SeededSource => ReadSeededSystemPrompt(AgentType.VideoReviewAgent),
            _ => throw new Xunit.Sdk.XunitException($"unknown prompt source '{source}'")
        };

        return Regex.Replace(prompt, @"\s+", " ");
    }

    private static string ReadPrivateStaticString(System.Type declaringType, string fieldName, string label)
    {
        FieldInfo? field = declaringType.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull($"{label} must expose a private {fieldName} constant");
        return (string)field!.GetValue(null)!;
    }

    private static string ReadSeededSystemPrompt(AgentType agentType)
    {
        FieldInfo? builtInAgentsField = typeof(DatabaseSeeder)
            .GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static);
        builtInAgentsField.Should().NotBeNull("DatabaseSeeder must expose a private BuiltInAgents seed table");

        var builtInAgents = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)
            builtInAgentsField!.GetValue(null)!;

        builtInAgents.Should().ContainKey(agentType);
        return builtInAgents[agentType].SystemPrompt;
    }

    // =====================================================================================
    // The section exists at all
    // =====================================================================================

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_names_the_voiceover_node_on_the_compile_steps_output(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("`voiceover` (on the VideoCompile step's output");
        prompt.Should().Contain("`voiceover.fit`");
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_names_every_nested_fit_roll_up_key(string source)
    {
        string prompt = Normalized(source);

        // The node-level roll-up: what the reviewer thresholds against.
        foreach (string key in new[]
                 {
                     "`applicable`",
                     "`measuredLineCount`",
                     "`overflowLineCount`",
                     "`overflowTotalSec`",
                     "`maxOverflowSec`",
                     "`unknownWindowLineIds`"
                 })
        {
            prompt.Should().Contain(key, $"`voiceover.fit` carries {key} and the reviewer has to be told so");
        }
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_names_every_per_line_fit_key_including_the_optional_solver_fit(string source)
    {
        string prompt = Normalized(source);

        foreach (string key in new[]
                 {
                     "`lineId`",
                     "`windowStartSec`",
                     "`windowEndSec`",
                     "`windowSec`",
                     "`overflowSec`",
                     "`fits`",
                     "`fitSource`",
                     "`solverFit`"
                 })
        {
            prompt.Should().Contain(key, $"each `voiceover.lines[]` entry carries {key}");
        }

        // The `lines` array is named, so "voiceover.lines[]" is a key the reviewer can actually
        // find rather than an inferred one.
        prompt.Should().Contain("`lines` array");
    }

    // =====================================================================================
    // The two windows answer DIFFERENT questions — the reason the fact exists
    // =====================================================================================

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_states_the_containing_placement_is_the_window_a_listener_hears_and_is_not_the_gap_to_the_next_line(
        string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("answer DIFFERENT questions on purpose");
        prompt.Should().Contain("CONTAINING OUTPUT PLACEMENT");
        prompt.Should().Contain("the window a listener actually hears");
        prompt.Should().Contain("It is NOT the gap to the next narration line");

        // And the conclusion that follows from it: running past the cut is an OVERFLOW, never a fit.
        prompt.Should().Contain("is an OVERFLOW, never a fit");
    }

    // =====================================================================================
    // The defect, its cap, and only remediations upstream can actually follow
    // =====================================================================================

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_makes_a_positive_overflow_a_real_defect_with_the_cap_of_four(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("When `overflowLineCount` is greater than 0");
        prompt.Should().Contain("a REAL defect, not a taste note");
        prompt.Should().Contain("Score no higher than 4");
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_tells_the_reviewer_to_name_the_worst_overflow_and_the_offending_line_ids_in_issues(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("name `maxOverflowSec`");
        prompt.Should().Contain("the `lineId` of each overflowing `lines[]` entry");
        prompt.Should().Contain("`issues`");
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_offers_only_remediations_the_upstream_agents_can_follow_and_forbids_re_timing_a_line(
        string source)
    {
        string prompt = Normalized(source);

        // The two remediations that are actually reachable: a shorter line, or a longer kept span
        // (which re-runs the story editor's decision).
        prompt.Should().Contain("rewrite that line SHORTER so it fits the same anchor");
        prompt.Should().Contain("keep a LONGER span");

        // The structural reason they are the only two: NarrationPlanOutput carries no start time,
        // so there is no timestamp for any agent to move.
        prompt.Should().Contain("its plan carries no start time");
        prompt.Should().Contain("Never tell any");
        prompt.Should().Contain("\"re-time\", \"shift\", \"extend\"");
    }

    // =====================================================================================
    // The degraded case, stated explicitly and in one place
    // =====================================================================================

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_states_the_not_measurable_case_is_not_a_defect_and_must_not_cap_the_score(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("NOT MEASURABLE, and therefore never a defect");

        // All three shapes, named. `no_resolved_lines` is the ONLY `fit.applicable:false` reason the
        // mapped path can now reach — the old assertion here pinned a two-value list whose second
        // value (`window_not_found`) the merged head cannot produce, and a test asserting text no
        // producer can justify is the defect, not the guard. See the test below, which pins the
        // removal from the other direction.
        prompt.Should().Contain("when `fit.applicable` is `false`");
        prompt.Should().Contain("`reason` is `no_resolved_lines`");
        prompt.Should().Contain("when there is no `fit` node at all");
        prompt.Should().Contain("`reason: voiceover_not_enabled`");

        // And the three consequences, explicitly: no cap, no score reduction, no issue.
        prompt.Should().Contain("do NOT cap the score for it");
        prompt.Should().Contain("do NOT lower it");
        prompt.Should().Contain("do NOT report anything about it in `issues`");
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_carries_the_declared_and_lost_clause_and_no_longer_treats_window_not_found_as_live(
        string source)
    {
        string prompt = Normalized(source);

        // Ruling 2. `window_not_found` is unreachable from the mapped path — the fit lookup uses the
        // RAW mapped start, which always lands inside a placement — so naming it told the reviewer to
        // expect a state the compile cannot emit. Absent ENTIRELY, not merely deprioritised: the
        // ordering of a dead reason in a list is not something this test can check honestly.
        prompt.Should().NotContain("window_not_found");

        // Ruling 1. The declared-and-lost defect, and the keys the reviewer has to quote so a human
        // reading the review can tell an ENVIRONMENT cause from a PRODUCER one.
        prompt.Should().Contain("DECLARED and never reached the picture");
        prompt.Should().Contain("`applied`");
        prompt.Should().Contain("`appliedLineCount`");
        prompt.Should().Contain("`lineCount`");
        prompt.Should().Contain("`unavailable`");
        prompt.Should().Contain("`reason`");
        prompt.Should().Contain("carries NO NARRATION");

        // The two causes, named as causes rather than as another score band: an ffmpeg build the
        // loop cannot fix, against a producer that lost every declared line.
        prompt.Should().Contain("ENVIRONMENT problem");
        prompt.Should().Contain("PRODUCER problem");
        prompt.Should().Contain("all_lines_unavailable");

        // And the one shape that must NOT be read as this defect.
        prompt.Should().Contain("empty-plan case");
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_treats_a_partial_measurement_as_partial_and_never_a_defect_on_its_own(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("A non-empty `unknownWindowLineIds`");
        prompt.Should().Contain("a partial measurement");
        prompt.Should().Contain("never a defect on its own");
    }

    // =====================================================================================
    // The section is additive, not a rewrite of the neighbours
    // =====================================================================================

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_neighbouring_fact_sections_are_still_named(string source)
    {
        // The voiceover section was inserted among the deterministic-evidence bullets; the ones
        // that were already there must still be there, so this addition cannot have quietly
        // replaced one of them.
        string prompt = Normalized(source);

        foreach (string key in new[] { "`sentenceCheck`", "`transitions`" })
            prompt.Should().Contain(key);
    }

    [Theory]
    [MemberData(nameof(PromptSources))]
    public void The_prompt_judges_lost_footage_and_names_no_facts_the_compile_step_never_emits(string source)
    {
        string prompt = Normalized(source);

        prompt.Should().Contain("Judge what was LOST as well as what remains");
        prompt.Should().Contain("Give no credit for an edit merely being shorter");
        prompt.Should().NotContain("a retry that discards more good footage");
        prompt.Should().Contain("\"The brief\" means the user's request");
        prompt.Should().Contain("Judge the opening from the first Keep span's `fromId`");
        prompt.Should().Contain("thin coverage of good");
        foreach (string phantom in new[] { "openingCheck", "seamCheck", "shortSegmentPct", "lookJumpCount" })
            prompt.Should().NotContain(phantom);
    }

    [Fact]
    public void The_two_prompt_copies_are_byte_identical()
    {
        // Restated here (VideoStoryEditorPromptConsistencyTests owns the general case) because this
        // file's whole premise is that both copies carry the new section: if they ever diverge, the
        // theory above could be green on one copy and green on a stale other.
        Normalized(SeededSource).Should().Be(Normalized(FallbackSource));
    }
}
