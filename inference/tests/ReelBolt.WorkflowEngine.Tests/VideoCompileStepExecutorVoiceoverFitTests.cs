using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The narration picture-window FIT FACTS on the compile's <c>voiceover</c> node.
/// </summary>
/// <remarks>
/// <para>
/// One fact, one section: T-101 (renamed by T-103) measures every resolved narration line against
/// the output placement its start landed in — the shot the line starts in, NEVER the gap to the
/// next line — and rolls the measurable ones up into <c>voiceover.fit</c>. That node is the input
/// the review loop reads, so its shape (key names included) is the contract this file pins.
/// </para>
/// <para>
/// The <c>storageKey</c> resolution seam and the partial-missing log level already have coverage in
/// <see cref="VideoCompileStepExecutorTests"/> and are deliberately NOT duplicated here; this file
/// covers only what that file leaves open.
/// </para>
/// <para>
/// Self-contained on purpose: it copies the small harness pieces it needs from
/// <see cref="VideoCompileStepExecutorTests"/> (artifact/decision/context construction, the EDL and
/// ffmpeg-argv capture, the media-probe stub) rather than editing that file. No real ffmpeg runs and
/// no network is touched: every ffmpeg call goes through a Moq <see cref="IVideoToolRunner"/>, every
/// media probe through a Moq <see cref="IMediaProbe"/>, and every byte the executor "downloads"
/// comes from an in-test string.
/// </para>
/// </remarks>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileStepExecutorVoiceoverFitTests
{
    // Deliberately does NOT contain the substring "voiceover-": the GAP 5 test below greps every
    // ffmpeg argument for a narration scratch path, and a scratch root that matched would make
    // that check pass for the wrong reason.
    private const string ProjectFileStorageRoot = "video-compile-narration-fit-tests";

    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ExecutionId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly Guid CompileStepId = Guid.Parse("24681357-2468-1357-2468-135724681357");

    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";
    private const string EdlKey = "projects/p/agentFiles/video-analysis/e/step-4-edl.json";

    private const int AnalyzeStepOrder = 1;
    private const int StoryEditorStepOrder = 2;
    private const int VoiceoverStepOrder = 3;
    private const int CompileStepOrder = 4;

    // ==========================================================================================
    // The fixture every fit assertion is DERIVED from — never re-typed as a decimal in an assert
    // ==========================================================================================

    /// <summary>
    /// The two-shot fixture: <c>[0,10)</c> of <c>s0</c> kept, then <c>[12,20)</c> of <c>s1</c> kept,
    /// with <c>[10,12)</c> cut away. Padding is zeroed in every config below, so a kept shot's output
    /// window IS its own source length and a window assertion can be derived from these four
    /// constants rather than copied from a run.
    /// <para>
    /// The gap is load-bearing: <c>VideoCompileStepExecutor</c> coalesces adjacent kept spans, so two
    /// abutting shots kept as two ranges would collapse into ONE placement and there would be no
    /// shot boundary for a window to end at.
    /// </para>
    /// </summary>
    private const double Shot0StartSec = 0.0;
    private const double Shot0EndSec = 10.0;
    private const double Shot1StartSec = 12.0;
    private const double Shot1EndSec = 20.0;

    /// <summary>
    /// Where the compiled programme ends, derived from the fixture: padding is zero, so the output
    /// timeline is the two kept shots back to back with the cut-away <c>[10,12)</c> removed.
    /// </summary>
    private const double OutputProgramEndSec =
        (Shot0EndSec - Shot0StartSec) + (Shot1EndSec - Shot1StartSec);

    /// <summary>
    /// A narration line that starts 2s into the FIRST shot and speaks for 3s: comfortably inside
    /// the <c>[0,10)</c> shot, so it must measure as a fit.
    /// </summary>
    private const double FittingLineInShot0StartSec = 2.0;
    private const double FittingLineInShot0DurationSec = 3.0;

    /// <summary>A second fitting line, this one starting inside the SECOND shot.</summary>
    private const double FittingLineInShot1StartSec = 14.0;
    private const double FittingLineInShot1DurationSec = 5.0;

    /// <summary>
    /// A line that starts 8s into the <c>[0,10)</c> shot and speaks for 5s, so it runs 3s past the
    /// cut into <c>[10,20)</c>. It is the ONLY declared line: measuring against "the next line's
    /// start" (or the end of the programme) would call this a fit, and measuring against the shot
    /// it starts in calls it an overflow. That difference is the entire point of the fact.
    /// </summary>
    private const double OverflowingLineStartSec = 8.0;
    private const double OverflowingLineDurationSec = 5.0;

    /// <summary>
    /// A start that survives <c>MapToOutputSec</c> but whose 3-decimal ROUNDING — the rounding the
    /// line node's own <c>outputStartSec</c> key uses, and the value the fit lookup is keyed on —
    /// lands exactly on the shot's exclusive end, i.e. in no placement at all.
    /// </summary>
    private const double StartThatRoundsUpToShot0EndSec = 9.9999;

    // Content-addressed line keys: sha256(sanitizedText|voiceId|model) — no execution id anywhere.
    private const string LineKeyA =
        "projects/p/agentFiles/voiceover/lines/1111111111111111111111111111111111111111111111111111111111111111.wav";
    private const string LineKeyB =
        "projects/p/agentFiles/voiceover/lines/2222222222222222222222222222222222222222222222222222222222222222.wav";
    private const string LineKeyC =
        "projects/p/agentFiles/voiceover/lines/3333333333333333333333333333333333333333333333333333333333333333.wav";

    // ==========================================================================================
    // GAP 2 — an overflowing line, and the window is the CONTAINING SHOT, not the next line
    // ==========================================================================================

    [Fact]
    public async Task Narration_line_that_runs_into_the_next_lines_shot_reports_a_positive_overflowSec_and_fits_false()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        // A line running from 8s past a cut that lands at 10s, into a shot whose own line starts
        // at 14s — so the window cannot carry over the cut (docs/voiceover.md).
        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", OverflowingLineStartSec, OverflowingLineDurationSec, LineKeyA),
            ("vo2", FittingLineInShot1StartSec, 2.0, LineKeyB));

        // The discriminator, stated as a fixture property on the OUTPUT timeline: the line ends
        // inside the kept programme, so a "does it reach the next line / the end of the cut" measure
        // reports a FIT and only the shot it starts in reports an overflow. If this stops holding,
        // the assertions below would pass for the wrong reason.
        OutputProgramEndSec.Should().BeGreaterThan(
            OverflowingLineStartSec + OverflowingLineDurationSec,
            "the line must stop inside the kept programme, so measuring to the next line — or to the "
            + "end of the edit — would call it a fit; that is exactly the defect this window catches");

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);

        JsonElement line = VoiceoverLines(run).Single(l => l.GetProperty("lineId").GetString() == "vo1");
        JsonElement fit = VoiceoverFit(run);

        double expectedOverflowSec = OverflowingLineDurationSec - (Shot0EndSec - OverflowingLineStartSec);

        // The three PRE-EXISTING per-line keys are untouched by T-101/T-103.
        line.GetProperty("lineId").GetString().Should().Be("vo1");
        line.GetProperty("outputStartSec").GetDouble().Should().BeApproximately(
            OverflowingLineStartSec, 1e-9, "the mapping from source to output seconds is unchanged");
        line.GetProperty("playDurationSec").GetDouble().Should().BeApproximately(
            OverflowingLineDurationSec, 1e-9, "the declared spoken duration is unchanged");

        // The window is the shot the line STARTS in — both bounds, on both sides.
        line.GetProperty("windowStartSec").GetDouble().Should().BeApproximately(
            Shot0StartSec, 1e-9, "the window is the containing shot's own output start");
        line.GetProperty("windowEndSec").GetDouble().Should().BeApproximately(
            Shot0EndSec, 1e-9,
            "the window ends where the shot the line starts in ends — NOT at the next line, and not "
            + "at the end of the programme");
        line.GetProperty("windowSec").GetDouble().Should().BeApproximately(
            Shot0EndSec - OverflowingLineStartSec, 1e-9,
            "the window is the picture time left from the line's own start to the end of that shot");

        line.GetProperty("overflowSec").GetDouble().Should().BeApproximately(
            expectedOverflowSec, 1e-9, "the line speaks for longer than its shot has left");
        expectedOverflowSec.Should().BeGreaterThan(0, "GAP 2 is the positive-overflow case");
        line.GetProperty("fits").GetBoolean().Should().BeFalse(
            "a line that runs past its containing shot's cut does not fit");

        // T-103's rename, pinned by its exact camelCase spelling.
        line.GetProperty("fitSource").GetString().Should().Be(
            "compile_measured",
            "with no upstream solver fit in the Voiceover step's view.lines[], the compile measured "
            + "this line itself and says so");
        line.TryGetProperty("fit-source", out _).Should().BeFalse(
            "T-103 removed the hyphenated key; it must not reappear under its old name");

        // The node-level roll-up the review loop reads.
        fit.GetProperty("applicable").GetBoolean().Should().BeTrue();
        fit.GetProperty("measuredLineCount").GetInt32().Should().Be(2);
        fit.GetProperty("overflowLineCount").GetInt32().Should().BeGreaterThanOrEqualTo(
            1, "GAP 2 requires the node to count the overflowing line");
        fit.GetProperty("overflowTotalSec").GetDouble().Should().BeGreaterThan(
            0, "GAP 2 requires a positive total overflow");
        fit.GetProperty("overflowTotalSec").GetDouble().Should().BeApproximately(
            expectedOverflowSec, 1e-9, "vo2 fits, so the total IS vo1's overflow");
        fit.GetProperty("maxOverflowSec").GetDouble().Should().BeApproximately(
            expectedOverflowSec, 1e-9, "the worst single overflow, which is the thresholdable number");
        fit.GetProperty("unknownWindowLineIds").EnumerateArray().Should().BeEmpty(
            "this line's window WAS found — only the two degradation cases leave it unmeasured");
    }

    [Fact]
    public async Task Narration_line_carries_over_a_cut_into_a_following_shot_with_no_line_of_its_own()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        // The only line runs 3s past the cut at 10s into a shot that starts no line of its own.
        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", OverflowingLineStartSec, OverflowingLineDurationSec, LineKeyA));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);

        JsonElement line = SingleVoiceoverLine(run);
        line.GetProperty("windowStartSec").GetDouble().Should().BeApproximately(Shot0StartSec, 1e-9);
        line.GetProperty("windowEndSec").GetDouble().Should().BeApproximately(
            OutputProgramEndSec, 1e-9, "the window carries over the cut to the end of the line-free shot");
        line.GetProperty("carriedShots").GetInt32().Should().Be(1);
        line.GetProperty("overflowSec").GetDouble().Should().Be(0);
        line.GetProperty("fits").GetBoolean().Should().BeTrue();

        JsonElement fit = VoiceoverFit(run);
        fit.GetProperty("overflowLineCount").GetInt32().Should().Be(0);
    }

    // ==========================================================================================
    // GAP 3 — the negative control: a line that fits must not be reported as overflowing
    // ==========================================================================================

    [Fact]
    public async Task Narration_lines_that_fit_their_containing_shots_report_overflowSec_zero_and_leave_overflowLineCount_at_zero()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", FittingLineInShot0StartSec, FittingLineInShot0DurationSec, LineKeyA),
            ("vo2", FittingLineInShot1StartSec, FittingLineInShot1DurationSec, LineKeyB));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);

        double expectedWindowSecInShot0 = Shot0EndSec - FittingLineInShot0StartSec;
        double expectedWindowSecInShot1 = Shot1EndSec - FittingLineInShot1StartSec;

        // Both windows are non-empty and both lines are shorter than the window they play in, so
        // "overflow 0" below is a measurement of a fit rather than the arithmetic of a dead window.
        expectedWindowSecInShot0.Should().BeGreaterThan(FittingLineInShot0DurationSec);
        expectedWindowSecInShot1.Should().BeGreaterThan(FittingLineInShot1DurationSec);

        List<JsonElement> lines = VoiceoverLines(run).ToList();
        lines.Should().HaveCount(2);

        JsonElement first = lines.Single(l => l.GetProperty("lineId").GetString() == "vo1");
        JsonElement second = lines.Single(l => l.GetProperty("lineId").GetString() == "vo2");

        first.GetProperty("windowSec").GetDouble().Should().BeApproximately(expectedWindowSecInShot0, 1e-9);
        first.GetProperty("overflowSec").GetDouble().Should().Be(0.0);
        first.GetProperty("fits").GetBoolean().Should().BeTrue();

        second.GetProperty("windowSec").GetDouble().Should().BeApproximately(expectedWindowSecInShot1, 1e-9);
        second.GetProperty("overflowSec").GetDouble().Should().Be(0.0);
        second.GetProperty("fits").GetBoolean().Should().BeTrue();

        JsonElement fit = VoiceoverFit(run);
        fit.GetProperty("applicable").GetBoolean().Should().BeTrue();
        fit.GetProperty("measuredLineCount").GetInt32().Should().Be(2);
        fit.GetProperty("overflowLineCount").GetInt32().Should().Be(
            0, "without this control GAP 2's `>= 1` could be satisfied by a constant");
        fit.GetProperty("overflowTotalSec").GetDouble().Should().Be(0.0);
        fit.GetProperty("maxOverflowSec").GetDouble().Should().Be(0.0);
        fit.GetProperty("unknownWindowLineIds").EnumerateArray().Should().BeEmpty();
    }

    // ==========================================================================================
    // GAP 4 — degradation is RECORDED and never fatal, on both arms
    // ==========================================================================================

    [Fact]
    public async Task A_narration_line_whose_start_rounds_onto_its_shots_end_is_measured_in_that_shot_rather_than_dropped_as_window_not_found()
    {
        // A single kept shot. The line's start survives the source->output mapping but its
        // 3-decimal rounding — the same rounding its own outputStartSec key carries — lands exactly
        // on the shot's exclusive end. Containment must therefore be decided on the RAW start, so
        // the line is measured in the shot it genuinely starts in instead of being discarded as a
        // line with no containing placement.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec)],
            offeredIds: ["s0"]);
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));

        Math.Round(StartThatRoundsUpToShot0EndSec, 3).Should().Be(
            Shot0EndSec,
            "the fixture only reaches this arm while the rounded start sits exactly on the shot's end");
        StartThatRoundsUpToShot0EndSec.Should().BeLessThan(
            Shot0EndSec, "the line must still be inside the shot in source time, or it would be cut away");

        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", StartThatRoundsUpToShot0EndSec, 0.5, LineKeyA));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);
        JsonDocument.Parse(run.OutputJson).RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        // Line resolution is untouched by the fit lookup: one line was declared, one resolved.
        JsonElement voiceover = Voiceover(run);
        voiceover.GetProperty("applied").GetBoolean().Should().BeTrue("the line did resolve");
        voiceover.GetProperty("appliedLineCount").GetInt32().Should().Be(1);
        voiceover.GetProperty("lineCount").GetInt32().Should().Be(1);

        // The line is MEASURED — the raw start found its shot — so every per-line window key is
        // present. Its window is the whole [0, Shot0EndSec) shot, and the line starts at the very
        // end of it: the window is empty, the line overflows it by its full played duration, and it
        // does not fit. Measuring a line the old lookup dropped entirely is the point of the test.
        JsonElement line = SingleVoiceoverLine(run);
        line.GetProperty("lineId").GetString().Should().Be("vo1");
        line.GetProperty("windowStartSec").GetDouble().Should().BeApproximately(
            Shot0StartSec, 1e-9, "the window is the containing shot's own output start");
        line.GetProperty("windowEndSec").GetDouble().Should().BeApproximately(
            Shot0EndSec, 1e-9, "the window ends at the containing shot's output end");
        line.GetProperty("windowSec").GetDouble().Should().BeApproximately(
            0.0, 1e-9,
            "the line starts on the shot's exclusive end, so no picture time is left inside the window");
        line.GetProperty("overflowSec").GetDouble().Should().BeApproximately(
            0.5, 1e-9, "the whole declared duration overflows an empty window");
        line.GetProperty("fits").GetBoolean().Should().BeFalse(
            "a line speaking past the end of its own shot does not fit");

        // The roll-up is the APPLICABLE shape: one line measured, one overflowing, its window found.
        JsonElement fit = VoiceoverFit(run);
        fit.GetProperty("applicable").GetBoolean().Should().BeTrue(
            "the line's window WAS found — this is no longer a degradation case");
        fit.GetProperty("measuredLineCount").GetInt32().Should().Be(1);
        fit.GetProperty("overflowLineCount").GetInt32().Should().Be(1);
        fit.GetProperty("overflowTotalSec").GetDouble().Should().BeApproximately(0.5, 1e-9);
        fit.GetProperty("maxOverflowSec").GetDouble().Should().BeApproximately(0.5, 1e-9);
        fit.GetProperty("unknownWindowLineIds").EnumerateArray().Should().BeEmpty(
            "the line is measured, so its id must NOT appear as an unknown window");
        fit.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["applicable", "measuredLineCount", "overflowLineCount", "overflowTotalSec",
             "maxOverflowSec", "unknownWindowLineIds"],
            "the applicable shape carries no `reason` key");
        fit.TryGetProperty("reason", out _).Should().BeFalse(
            "the window_not_found arm must not fire for a line whose shot was found; a mutation "
            + "that asserts the old arm here is the regression this test exists to catch");
    }

    [Fact]
    public async Task No_resolved_narration_lines_records_no_resolved_lines_as_the_fit_reason_and_the_compile_still_completes()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", FittingLineInShot0StartSec, FittingLineInShot0DurationSec, LineKeyA),
            ("vo2", FittingLineInShot1StartSec, FittingLineInShot1DurationSec, LineKeyB),
            ("vo3", FittingLineInShot1StartSec, FittingLineInShot1DurationSec, LineKeyC));

        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, voiceoverJson, WithVoiceover(), run,
            voiceoverLinesAvailable: false);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);
        JsonDocument.Parse(run.OutputJson).RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        // The precondition of THIS arm: three lines were DECLARED and not one resolved, so the
        // reason cannot be window_not_found.
        JsonElement voiceover = Voiceover(run);
        voiceover.GetProperty("applied").GetBoolean().Should().BeFalse();
        voiceover.GetProperty("appliedLineCount").GetInt32().Should().Be(0);
        voiceover.GetProperty("lineCount").GetInt32().Should().Be(
            3,
            "three lines were declared and all three lost their artifact; a node reporting 0 here is "
            + "indistinguishable from a Voiceover step that was never asked for anything, which is "
            + "exactly what makes a cap keyed off `lineCount > 0` dead code");
        voiceover.GetProperty("reason").GetString().Should().Be(
            "all_lines_unavailable",
            "declared-and-none-resolved is its own outcome on this node, and it is the one the "
            + "narration cap reads");

        JsonElement fit = VoiceoverFit(run);
        fit.GetProperty("applicable").GetBoolean().Should().BeFalse();
        fit.GetProperty("reason").GetString().Should().Be(
            "no_resolved_lines",
            "the no-lines case and the no-window case are different degradations and must not be "
            + "reported under one reason");
        fit.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["applicable", "reason"]);
    }

    // ==========================================================================================
    // The narration cap's two INERT shapes — the declared-vs-applied node values stay separable
    // ==========================================================================================

    /// <summary>
    /// A well-formed EMPTY <c>NarrationPlan</c> is a SUCCESSFUL Voiceover step narrating nothing, so
    /// the node must report zero declared lines here. That zero is what keeps a cap keyed off
    /// <c>lineCount &gt; 0 &amp;&amp; appliedLineCount == 0</c> inert: the producer was asked for
    /// nothing, which is a different fact from the producer being asked and delivering nothing.
    /// </summary>
    [Fact]
    public async Task Empty_narration_plan_reports_zero_declared_lines_and_leaves_the_all_lines_unavailable_arm_inert()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        // view.lines is an EMPTY ARRAY: the plan is well formed and simply contains nothing.
        string voiceoverJson = VoiceoverOutputJson();

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        // An empty plan is a success, not a degradation: the compile completes with valid JSON.
        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);
        JsonDocument.Parse(run.OutputJson).RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        JsonElement voiceover = Voiceover(run);
        voiceover.GetProperty("applied").GetBoolean().Should().BeFalse();
        voiceover.GetProperty("appliedLineCount").GetInt32().Should().Be(0);
        voiceover.GetProperty("lineCount").GetInt32().Should().Be(
            0,
            "nothing was DECLARED, and lineCount reports what the producer was asked for — this "
            + "zero is precisely what keeps the narration cap inert on an empty plan");

        voiceover.GetProperty("reason").GetString().Should().Be(
            "no_valid_voiceover_lines",
            "an empty plan still gets its own reason, distinct from the declared-and-lost one");
        voiceover.GetProperty("reason").GetString().Should().NotBe(
            "all_lines_unavailable",
            "a cap keyed off `lineCount > 0 && appliedLineCount == 0` must never fire here; the "
            + "empty plan is the false positive it has to avoid");
    }

    /// <summary>
    /// PARTIAL narration: two lines were declared and one of them — a legitimately skipped/failed
    /// line carrying <c>durationSec 0</c> — is counted as declared, then filtered before resolution.
    /// <c>lineCount</c> stays the DECLARED count while <c>appliedLineCount</c> is positive, so the
    /// cap must stay inert: narration really is in the render.
    /// </summary>
    [Fact]
    public async Task Partially_applied_narration_reports_the_declared_line_count_and_leaves_the_all_lines_unavailable_arm_inert()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        // vo1 is a normal line inside the first shot. vo2 was declared but produced nothing usable:
        // durationSec 0 and no storageKey, which is exactly how the producer persists a
        // skipped/failed line. The compile counts it as declared, then filters it out.
        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", FittingLineInShot0StartSec, FittingLineInShot0DurationSec, LineKeyA),
            ("vo2", FittingLineInShot1StartSec, 0.0, null));

        var run = new RunResult();
        await RunCompileAsync(artifact, decisionJson, voiceoverJson, WithVoiceover(), run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);

        JsonElement voiceover = Voiceover(run);
        voiceover.GetProperty("lineCount").GetInt32().Should().Be(
            2,
            "both lines were DECLARED — the unusable one still counts, and lineCount must report the "
            + "declared total rather than a duplicate of what resolved");
        voiceover.GetProperty("appliedLineCount").GetInt32().Should().Be(
            1, "only vo1 survived the duration filter and resolved");
        voiceover.GetProperty("applied").GetBoolean().Should().BeTrue(
            "narration really is in the render, so this is not the nothing-applied case");

        voiceover.TryGetProperty("reason", out _).Should().BeFalse(
            "the all_lines_unavailable arm requires appliedLineCount == 0 and must stay inert on a "
            + "partial plan, which carries no reason at all");

        // The resolved line is the one that was usable, and only it carries a line node.
        JsonElement line = SingleVoiceoverLine(run);
        line.GetProperty("lineId").GetString().Should().Be("vo1");
    }

    // ==========================================================================================
    // GAP 5 — EnableVoiceover = false stays byte-identical
    // ==========================================================================================

    [Fact]
    public async Task EnableVoiceover_false_emits_no_voiceover_node_and_an_argv_and_EDL_untouched_by_the_fit_facts()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: [("s0", Shot0StartSec, Shot0EndSec), ("s1", Shot1StartSec, Shot1EndSec)],
            offeredIds: ["s0", "s1"]);
        // Two kept RANGES, so the two shots stay two placements (adjacent spans coalesce).
        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"), ("s1", "s1", "keep"));

        // The line JSON is present and would overflow its shot if it were ever read — so a fit
        // block that ran despite EnableVoiceover=false would be visible in the argv, not just in
        // the output.
        string voiceoverJson = VoiceoverOutputJson(
            ("vo1", OverflowingLineStartSec, OverflowingLineDurationSec, LineKeyA));

        // Baseline: a config that never mentions a Voiceover step at all.
        var baseline = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, voiceoverJson,
            cfg => WithVoiceover()(cfg) with { EnableVoiceover = false, VoiceoverStepOrder = null },
            baseline);

        // The case under test: the feature is off but a Voiceover step IS configured, which is
        // exactly the config T-101's fit block could have reached.
        var run = new RunResult();
        await RunCompileAsync(
            artifact, decisionJson, voiceoverJson,
            cfg => WithVoiceover()(cfg) with { EnableVoiceover = false },
            run);

        run.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.OutputJson);
        baseline.Status.Should().Be(StepStatus.Completed);

        // No voiceover node in the EDL...
        run.EdlJson.Should().NotContain("voiceover");
        // ...nor in the step's output summary.
        run.OutputJson.Should().NotContain("voiceover");

        // The EDL is byte-for-byte the config that never mentioned a Voiceover step.
        run.EdlJson.Should().Be(baseline.EdlJson, "EnableVoiceover=false must be the untouched path");

        // And so is the ffmpeg argv — no extra input, no extra filter, no amix for a narration
        // track that does not exist.
        NormalizedInvocations(run).Should().BeEquivalentTo(
            NormalizedInvocations(baseline), o => o.WithStrictOrdering());

        List<string> encode = EncodeInvocation(run);
        encode.Count(a => a == "-i").Should().Be(1, "the single source clip is the only input");

        // The fit block sits downstream of line resolution, so the sharpest argv-side statement is
        // that no narration artifact was ever fetched and no narration scratch path ever reached
        // ffmpeg — with the feature off, the whole resolve-and-measure path is unreachable.
        run.Workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(k => k.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "EnableVoiceover=false never reaches ResolveVoiceoverAsync, so no line is downloaded");
        run.FfmpegInvocations.SelectMany(a => a).Should().NotContain(
            a => a.Contains("voiceover-", StringComparison.Ordinal),
            "no narration scratch path may reach ffmpeg when the feature is off");
    }

    // ==========================================================================================
    // Config / JSON helpers
    // ==========================================================================================

    /// <summary>
    /// The voiceover-enabled compile config every fit test starts from. Padding is zeroed so the
    /// output timeline is exactly the kept shots' own times, which is what lets every window
    /// assertion above be derived from <see cref="Shot0EndSec"/> and friends.
    /// </summary>
    private static Func<VideoCompileStepConfig, VideoCompileStepConfig> WithVoiceover() =>
        cfg => cfg with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VoiceoverStepOrder,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

    /// <summary>
    /// The Voiceover step's own output JSON: <c>view.lines[]</c> with the id/start/duration the
    /// compile reads and the content-addressed <c>storageKey</c> the producer wrote (omitted
    /// entirely when null, which is the producer's own JsonIgnore behaviour).
    /// </summary>
    private static string VoiceoverOutputJson(
        params (string Id, double StartSec, double DurationSec, string? StorageKey)[] lines) =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = lines.Select(l => new
                {
                    id = l.Id,
                    startSec = l.StartSec,
                    durationSec = l.DurationSec,
                    status = "ok",
                    storageKey = l.StorageKey
                }).ToList()
            },
            meta = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    private static string BuildDecisionJson(params (string FromId, string ToId, string Reason)[] spans)
    {
        var keep = spans.Select(s => new { fromId = s.FromId, toId = s.ToId, reason = s.Reason });
        return JsonSerializer.Serialize(new { keep, editRationale = "test", suggestedTitle = "Test Edit" });
    }

    private static VideoAnalysisArtifact BuildArtifact(
        (string Id, double Start, double End)[] shots,
        string[] offeredIds) =>
        new(
            Version: 1,
            Media: new VideoAnalysisMedia(shots.Max(s => s.End), 30, 1, 1920, 1080),
            Shots: shots.Select(s => new VideoAnalysisShot(s.Id, s.Start, s.End)).ToList(),
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: offeredIds.ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));

    private static JsonSerializerOptions ConfigOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static JsonSerializerOptions ArtifactOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ==========================================================================================
    // Assertion helpers
    // ==========================================================================================

    /// <summary>Everything one compile run produced, for comparing two runs against each other.</summary>
    private sealed class RunResult
    {
        public StepExecutionResult Result { get; set; } = null!;
        public string OutputJson => Result.Output;
        public StepStatus Status => Result.Status;
        public string? EdlJson { get; set; }

        /// <summary>The workspace the run was given, so a test can Verify what was never asked of it.</summary>
        public Mock<IProjectFileWorkspace> Workspace { get; set; } = null!;

        /// <summary>The run's scratch root, so two runs' argv compare on content, not temp paths.</summary>
        public string ScratchRoot { get; set; } = string.Empty;

        public List<List<string>> FfmpegInvocations { get; } = [];
    }

    private static JsonElement Voiceover(RunResult run)
    {
        JsonElement edl = JsonDocument.Parse(run.EdlJson!).RootElement;
        edl.TryGetProperty("voiceover", out JsonElement voiceover)
            .Should().BeTrue($"the EDL must carry a voiceover node: {run.EdlJson}");
        return voiceover;
    }

    private static JsonElement VoiceoverFit(RunResult run)
    {
        JsonElement voiceover = Voiceover(run);
        voiceover.TryGetProperty("fit", out JsonElement fit)
            .Should().BeTrue($"the voiceover node must carry its fit roll-up: {voiceover.GetRawText()}");
        return fit;
    }

    private static IEnumerable<JsonElement> VoiceoverLines(RunResult run) =>
        Voiceover(run).GetProperty("lines").EnumerateArray();

    private static JsonElement SingleVoiceoverLine(RunResult run)
    {
        List<JsonElement> lines = VoiceoverLines(run).ToList();
        lines.Should().HaveCount(1, $"this fixture declares exactly one line: {Voiceover(run).GetRawText()}");
        return lines[0];
    }

    /// <summary>
    /// The single ENCODE invocation, with the run's scratch root folded out. Discriminated on
    /// <c>-map</c>, which the encode emits and no other invocation does.
    /// </summary>
    private static List<string> EncodeInvocation(RunResult run)
    {
        List<List<string>> encodes = run.FfmpegInvocations.Where(a => a.Contains("-map")).ToList();
        encodes.Should().HaveCount(1, "these compiles issue exactly one encode call");
        return Normalize(encodes[0], run.ScratchRoot);
    }

    private static List<List<string>> NormalizedInvocations(RunResult run) =>
        run.FfmpegInvocations.Select(a => Normalize(a, run.ScratchRoot)).ToList();

    private static List<string> Normalize(IReadOnlyList<string> args, string scratchRoot)
    {
        string root = Path.GetFullPath(scratchRoot);
        return args
            .Select(a => a.StartsWith(root, StringComparison.Ordinal)
                ? "<scratch>" + a[root.Length..].Replace('\\', '/')
                : a)
            .ToList();
    }

    // ==========================================================================================
    // Execution harness
    // ==========================================================================================

    private const string ProbeJson = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
          "r_frame_rate": "30/1", "duration": "10" } ], "format": { "duration": "10" } }
        """;

    /// <summary>
    /// Runs one full compile and records the EDL, the step output and every ffmpeg invocation into
    /// <paramref name="capture"/>.
    /// </summary>
    private static async Task RunCompileAsync(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string voiceoverJson,
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configOverride,
        RunResult capture,
        bool voiceoverLinesAvailable = true)
    {
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, voiceoverJson, configOverride,
            out Mock<IProjectFileWorkspace> workspace, out Mock<ILogger<VideoCompileStepExecutor>> logger,
            voiceoverLinesAvailable);

        // Re-configured AFTER CreateContext's own upload setup (Moq resolves overlapping setups to
        // the most recently configured one) so the EDL's bytes can be captured while the temp file
        // still exists. The EDL is uploaded twice — the second write carries timelineStorageKey —
        // and the last write wins, which is the complete document.
        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                {
                    capture.EdlJson = File.ReadAllText(path);
                }
            })
            .ReturnsAsync(EdlKey);
        workspace.SetupRegisteredRenderPublish();

        List<List<string>> invocations = [];

        // The drawtext/amix/perspective/color-grade availability probes are process-lifetime static
        // caches inside the executor; reset them so each test's own mocked runner is consulted.
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge colorbalance colorlevels hue eq ...", string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, ProbeJson, string.Empty, false));
        // Catch-alls, set up BEFORE the narrower "filter=amix" setup below (Moq resolves
        // overlapping setups to the most recently configured one).
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Capture(args, invocations))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>(
                (args, _, _, _) => Capture(args, invocations))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));

        // Every ffmpeg invocation is captured, whatever path issued it.
        void Capture(IReadOnlyList<string> args, List<List<string>> sink) => sink.Add(args.ToList());

        string scratchRoot = Path.Combine(Path.GetTempPath(), ProjectFileStorageRoot, Guid.NewGuid().ToString("N"));

        var executor = new VideoCompileStepExecutor(
            toolRunner.Object,
            mediaProbe.Object,
            workspace.Object,
            new Mock<IServiceScopeFactory>().Object,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = scratchRoot,
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 30
            }),
            logger.Object);

        capture.Result = await executor.ExecuteAsync(context);
        capture.FfmpegInvocations.AddRange(invocations);
        capture.Workspace = workspace;
        capture.ScratchRoot = scratchRoot;
    }

    /// <summary>
    /// Builds the full step context: the analysis artifact, the editorial decision, the Voiceover
    /// step's own output, the compile's config, and a <see cref="IProjectFileWorkspace"/> that
    /// serves every storage key this compile touches from in-test strings.
    /// </summary>
    private static StepExecutionContext CreateContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        string voiceoverJson,
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configOverride,
        out Mock<IProjectFileWorkspace> workspace,
        out Mock<ILogger<VideoCompileStepExecutor>> logger,
        bool voiceoverLinesAvailable)
    {
        workspace = new Mock<IProjectFileWorkspace>();
        logger = new Mock<ILogger<VideoCompileStepExecutor>>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]);
                return Task.CompletedTask;
            });

        // Scoped to the voiceover layout only, so the analysis/source setups above keep their own
        // behaviour.
        var voiceoverDownload = workspace.Setup(w => w.DownloadStorageKeyToFileAsync(
            It.IsAny<Guid>(),
            It.Is<string>(k => k.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()));

        if (voiceoverLinesAvailable)
        {
            voiceoverDownload.Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]);
                return Task.CompletedTask;
            });
        }
        else
        {
            // The bucket genuinely holds none of these objects.
            voiceoverDownload.ThrowsAsync(new AmazonS3Exception("NoSuchKey")
            {
                StatusCode = HttpStatusCode.NotFound,
                ErrorCode = "NoSuchKey"
            });
        }

        workspace
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync(EdlKey);
        workspace.SetupRegisteredRenderPublish();

        workspace
            .Setup(w => w.UploadBinaryFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
                // Deterministic storage key AND id: two runs of the same compile must be
                // comparable string-for-string, and a random Guid here would make them differ.
                new ProjectWorkspaceFile(
                    Guid.Parse("00000000-0000-0000-0000-0000000000aa"), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{fileName}", mime, 1024, DateTime.UnixEpoch, null));
        workspace.SetupRegisteredRenderPublish();

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: StoryEditorStepOrder),
            AnalysisStepOrder: AnalyzeStepOrder);
        config = configOverride(config);

        var step = new WorkflowStep
        {
            Id = CompileStepId,
            StepOrder = CompileStepOrder,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions())
        };

        // VideoCompileStepExecutor resolves the source video by reading the referenced VideoAnalyze
        // step's OWN VideoAnalyzeConfigJson.Source (never a StepOutputHistory heuristic), so a real
        // analyze step matching AnalysisStepOrder must be present in AllSteps.
        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = AnalyzeStepOrder,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new(AnalyzeStepOrder, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new(StoryEditorStepOrder, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new(VoiceoverStepOrder, "Voiceover", voiceoverJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = ExecutionId, ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }
}
