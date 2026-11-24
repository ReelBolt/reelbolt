using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Quality;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The scored footage-edit quality gate (docs/video-editing.md "Footage edit quality gate"): a synthetic single-speaker recording
/// goes through the view/outline and the compile, and the scores are asserted against thresholds. No paid calls, no ffmpeg.
/// </summary>
public partial class VideoCompileStepExecutorTests
{
    private const double GateOutlineCoveragePct = 95;
    private const double GateKeepEverythingDroppedSpeechMaxPct = 5;

    private static VideoAnalysisArtifact GateArtifact(SyntheticTalkingHead head, bool offerEvents)
    {
        VideoAnalysisArtifact artifact = WithSfxCandidates(BuildTalkingHeadArtifact(head)) with
        {
            Sources = [new VideoAnalysisSourceInfo(0, "k", new VideoAnalysisMedia(head.DurationSec, 30, 1, 1920, 1080, "h264", "aac"))],
            Events = head.Events,
            SfxEventsOnly = true
        };
        if (offerEvents)
            artifact = artifact with { OfferedEventIds = GateOfferedEvents(head).Select(e => e.Id).ToList() };
        return artifact;
    }

    private static List<VideoAnalysisRenderEvent> GateOfferedEvents(SyntheticTalkingHead head) =>
        SfxWorthyEvents.Select(head.Events.ToList(), _ => head.DurationSec);

    [Fact]
    public void Gate_view_outline_covers_the_whole_recording_with_default_budgets()
    {
        var head = new SyntheticTalkingHead();
        VideoAnalysisArtifact artifact = GateArtifact(head, offerEvents: true);
        JsonObject view = new()
        {
            ["shots"] = new JsonArray([.. artifact.Shots.Where((_, i) => i % 3 == 0).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id })]),
            ["segments"] = new JsonArray([.. artifact.Segments.Where((_, i) => i % 2 == 0).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id })])
        };
        JsonObject meta = new() { ["droppedItems"] = 400, ["offeredIdCount"] = 1 };
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true };

        VideoSpeechOutlineView.Apply(view, meta, artifact, config).Should().NotBeNull();

        double coveragePct = 100 * meta["offeredCoverage"]!["lastSec"]!.GetValue<double>() / meta["offeredCoverage"]!["sourceDurationSec"]!.GetValue<double>();
        coveragePct.Should().BeGreaterThanOrEqualTo(GateOutlineCoveragePct);
    }

    [Fact]
    public void Gate_sfx_offers_hold_no_beat_and_the_outline_and_read_tools_list_none()
    {
        var head = new SyntheticTalkingHead();
        head.Events.Count(e => e.Type == nameof(VideoEventType.Beat)).Should().BeGreaterThan(100);

        List<VideoAnalysisRenderEvent> offered = GateOfferedEvents(head);

        offered.Should().NotBeEmpty();
        offered.Count(e => e.Type == nameof(VideoEventType.Beat)).Should().Be(0);
        VideoEventOutline.DetectedEvents(GateArtifact(head, offerEvents: true)).Should().OnlyContain(e => e.Type != nameof(VideoEventType.Beat));
        VideoEventOutline.DetectedEvents(GateArtifact(head, offerEvents: true) with { SfxEventsOnly = null }).Count.Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task Gate_real_analyze_step_offers_no_beat_with_default_sfx_settings()
    {
        // The real VideoAnalyze executor with a stub detector: 100+ audio beats and two real moments.
        List<VideoEvent> events = [.. Enumerable.Range(0, 110).Select(i => new VideoEvent(VideoEventType.Beat, 3 + i * 8, null, null, 0.5, VideoEventOrigin.Audio, 0.9))];
        events.Add(VideoAnalyzeEventsTests.Ev(VideoEventType.Hit, 90, 0.9));
        events.Add(VideoAnalyzeEventsTests.Ev(VideoEventType.Appear, 400, 0.8));
        var detector = new VideoAnalyzeEventsTests.StubDetector(_ => VideoAnalyzeEventsTests.Set(0, [.. events]));

        VideoAnalyzeEventsTests.Run run = await VideoAnalyzeEventsTests.RunAsync(
            detector, c => c with { AnalyzeEvents = true, OfferSfxClips = true });

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        JsonArray viewEvents = run.View["events"]!.AsArray();
        viewEvents.Should().NotBeEmpty();
        viewEvents.Should().HaveCountLessThan(20, "110 beats exist and none is offered");
        viewEvents.Select(e => e!["type"]!.GetValue<string>()).Should().NotContain(t => t.Equals("beat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Gate_keep_everything_plan_drops_almost_no_speech_and_cuts_no_word()
    {
        var head = new SyntheticTalkingHead();
        string everything = BuildDecisionJson(("s0", $"s{SyntheticTalkingHead.ShotCount - 1}", "keep"));

        (JsonObject output, List<(double, double)> spans) = await CompileTalkingHeadAsync(GateArtifact(head, offerEvents: true), everything, snap: true);

        double dropped = FootageEditQuality.DroppedSpeechPct(spans, head.Words.Select(w => (w.Start, w.End)));
        dropped.Should().BeLessThan(GateKeepEverythingDroppedSpeechMaxPct);
        output["speechCoverage"]!["droppedSpeechPct"]!.GetValue<double>().Should().BeLessThan(GateKeepEverythingDroppedSpeechMaxPct);
        FootageEditQuality.CutsInsideWords(spans, head.Words.Select(w => (w.Start, w.End))).Should().Be(0);
    }

    [Fact]
    public async Task Gate_ten_range_plan_with_event_anchored_cues_scores_clean()
    {
        var head = new SyntheticTalkingHead();
        VideoAnalysisArtifact artifact = GateArtifact(head, offerEvents: true);
        string decisionJson = BuildTalkingHeadDecision(head);
        string[] anchors = head.WorthyEventIds.ToArray();
        string sfxPlanJson = BuildSfxPlanJson(anchors.Select(a => ("x0", a, "OnCut", "Normal")).ToArray());

        StepExecutionContext context = CreateSfxContext(
            artifact, decisionJson, sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { SnapCutsToSpeech = true, PrePaddingMs = 0, PostPaddingMs = 0 });
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();

        List<(double Start, double End)> spans = edl.GetProperty("segments").EnumerateArray()
            .Select(s => (s.GetProperty("requestedStartSec").GetDouble(), s.GetProperty("requestedEndSec").GetDouble())).ToList();
        var words = head.Words.Select(w => (w.Start, w.End)).ToList();

        // Scores.
        int cutsInsideWords = FootageEditQuality.CutsInsideWords(spans, words);
        double dropped = FootageEditQuality.DroppedSpeechPct(spans, words);
        List<double> cues = edl.GetProperty("sfx").GetProperty("cues").EnumerateArray().Select(c => c.GetProperty("outputStartSec").GetDouble()).ToList();
        List<double> eventOutputSecs = anchors.Select(a => SourceToOutput(head.Events.First(e => e.Id == a).ContentSec, spans)).ToList();
        double precision = FootageEditQuality.SfxOnEventPrecision(cues, eventOutputSecs);
        int overSpeech = FootageEditQuality.SfxOverSpeechCount(
            cues.Select(c => OutputToSource(c, spans)).ToList(),
            head.Words.Select(w => new VideoAnalysisSegment(w.Id, null, w.Start, w.End, "w")).ToList());

        // Thresholds.
        cues.Count.Should().Be(anchors.Length, because: edl.GetProperty("sfx").GetProperty("dropped").ToString());
        cutsInsideWords.Should().Be(0);
        output["speechSafeCuts"]!["cutsInsideWords"]!.GetValue<int>().Should().Be(0);
        precision.Should().Be(1.0);
        overSpeech.Should().Be(0);
        output["speechCoverage"]!["sfx"]!["overSpeech"]!.GetValue<int>().Should().Be(0);
        output["speechCoverage"]!["sfx"]!["onEvent"]!.GetValue<int>().Should().Be(anchors.Length);
        output["speechCoverage"]!["droppedSpeechPct"]!.GetValue<double>().Should().BeApproximately(dropped, 2.0);
        output["speechCoverage"]!["lastSpeechSourceSec"]!.GetValue<double>().Should().BeGreaterThan(head.DurationSec * 0.9);
    }

    [Fact]
    public async Task Gate_compile_rejects_an_event_id_that_does_not_exist_in_the_artifact()
    {
        var head = new SyntheticTalkingHead();
        // e9999 is not in Events or RenderEvents; naming it as an offered id must not make it resolvable.
        VideoAnalysisArtifact artifact = GateArtifact(head, offerEvents: true);
        artifact = artifact with { OfferedEventIds = artifact.OfferedEventIds!.Append("e9999").ToList() };
        string sfxPlanJson = BuildSfxPlanJson(("x0", "e9999", "OnCut", "Normal"));

        StepExecutionContext context = CreateSfxContext(
            artifact, BuildTalkingHeadDecision(head), sfxPlanJson, out Mock<IProjectFileWorkspace> workspace,
            configOverride: c => c with { PrePaddingMs = 0, PostPaddingMs = 0 });
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, edlCaptured: e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("sfx").GetProperty("appliedCueCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Gate_a_keep_span_naming_a_missing_id_still_fails_unknown_id()
    {
        var head = new SyntheticTalkingHead();
        VideoAnalysisArtifact artifact = GateArtifact(head, offerEvents: true);
        string decision = BuildDecisionJson(("s1", "s99999", "keep"));
        StepExecutionContext context = CreateContext(artifact, decision, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("UNKNOWN_ID");
    }

    [Fact]
    public void Gate_peak_helper_measures_dbfs()
    {
        FootageEditQuality.PeakDb([0.5f, -1.0f, 0.25f]).Should().BeApproximately(0.0, 1e-9);
        FootageEditQuality.PeakDb([0.1f, -0.05f]).Should().BeApproximately(-20.0, 0.01);
        FootageEditQuality.PeakDb([]).Should().Be(double.NegativeInfinity);
        FootageEditQuality.PeakDb([0f, 0f]).Should().Be(double.NegativeInfinity);
    }

    /// <summary>Opt-in: prints the same scores for a real analysis (an2.json) and cut list (edl3.json) in FOOTAGE_EDIT_FIXTURE_DIR.</summary>
    [SkippableLocalFixture]
    public void Gate_local_real_fixture_prints_scores()
    {
        string? dir = Environment.GetEnvironmentVariable("FOOTAGE_EDIT_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "an2.json")) || !File.Exists(Path.Combine(dir, "edl3.json")))
            return;

        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        VideoAnalysisArtifact artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(File.ReadAllText(Path.Combine(dir, "an2.json")), options)!;
        JsonNode edl = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "edl3.json")))!;
        JsonArray segments = (edl["segments"] ?? edl["spans"])!.AsArray();
        var spans = segments.Select(s => (
            Start: (s!["requestedStartSec"] ?? s["startSec"])!.GetValue<double>(),
            End: (s["requestedEndSec"] ?? s["endSec"])!.GetValue<double>())).ToList();
        var tokens = (artifact.Words.Count > 0
            ? artifact.Words.Select(w => (w.StartSec, w.EndSec))
            : artifact.Segments.Select(s => (s.StartSec, s.EndSec))).ToList();

        Console.WriteLine($"[real fixture] spans {spans.Count}, tokens {tokens.Count}");
        Console.WriteLine($"[real fixture] cutsInsideWords {FootageEditQuality.CutsInsideWords(spans, tokens)}");
        Console.WriteLine($"[real fixture] droppedSpeechPct {FootageEditQuality.DroppedSpeechPct(spans, tokens):0.0}");
        Console.WriteLine($"[real fixture] lastKeptSourceSec {spans.Max(s => s.End):0.0}, lastSpeechSourceSec {tokens.Max(t => t.Item2):0.0}");
        List<VideoAnalysisRenderEvent> all = (artifact.Events ?? []).ToList();
        List<VideoAnalysisRenderEvent> offered = SfxWorthyEvents.Select(all, _ => artifact.Media.DurationSec);
        Console.WriteLine($"[real fixture] events {all.Count} (beats {all.Count(e => e.Type == nameof(VideoEventType.Beat))}), sfx offers {offered.Count} (beats {offered.Count(e => e.Type == nameof(VideoEventType.Beat))})");
    }

    /// <summary>Opt-in: the reserved view for a real analysis (an2.json): numbers printed, coverage and reachability asserted.</summary>
    [SkippableLocalFixture]
    public void Gate_local_real_fixture_reserved_view()
    {
        string? dir = Environment.GetEnvironmentVariable("FOOTAGE_EDIT_FIXTURE_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "an2.json")))
            return;

        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        VideoAnalysisArtifact artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(File.ReadAllText(Path.Combine(dir, "an2.json")), options)!;
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true, OfferSfxClips = true };

        int baseChars = VideoAnalyzeStepExecutor.BuildView(artifact, [], VideoVisualDetail.None, config, false, true, new HashSet<string>())
            .ToJsonString().Length;
        int reserved = VideoSpeechOutlineView.ReservedChars(artifact);
        int effective = Math.Max(config.MaxOutputChars, baseChars + reserved + VideoAnalyzeStepExecutor.ReservedItemRoomChars);
        int totalIds = artifact.Shots.Count + artifact.SilenceSpans.Count + artifact.Segments.Count;
        JsonArray phrases = VideoSpeechOutline.PhraseList(artifact);

        JsonObject view = new() { ["shots"] = new JsonArray(), ["silences"] = new JsonArray(), ["segments"] = new JsonArray() };
        JsonObject meta = new() { ["droppedItems"] = 1, ["effectiveMaxOutputChars"] = effective };
        VideoSpeechOutlineView.Apply(view, meta, artifact, config).Should().NotBeNull();

        Console.WriteLine($"[real fixture] sfx candidates {artifact.SfxCandidates?.Count ?? 0}, total ids {totalIds}, phrases {phrases.Count} ({phrases.ToJsonString().Length} chars)");
        Console.WriteLine($"[real fixture] base view {baseChars} chars, reserved outline {reserved}, effective budget {effective} (default {config.MaxOutputChars}), offeredIdCount {meta["offeredIdCount"]}");
        meta["offeredCoverage"]!["lastSec"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(artifact.Media.DurationSec * 0.95);
        meta["offeredIdCount"]!.GetValue<int>().Should().Be(totalIds);
    }

    private static double SourceToOutput(double sourceSec, IReadOnlyList<(double Start, double End)> spans)
    {
        double at = 0;
        foreach ((double start, double end) in spans)
        {
            if (sourceSec >= start && sourceSec <= end)
                return at + (sourceSec - start);
            at += end - start;
        }

        return double.NaN;
    }

    private static double OutputToSource(double outputSec, IReadOnlyList<(double Start, double End)> spans)
    {
        double at = 0;
        foreach ((double start, double end) in spans)
        {
            if (outputSec >= at && outputSec < at + (end - start))
                return start + (outputSec - at);
            at += end - start;
        }

        return double.NaN;
    }

    /// <summary>A fact that runs always and does nothing unless the local fixture directory is set.</summary>
    private sealed class SkippableLocalFixtureAttribute : FactAttribute
    {
        public SkippableLocalFixtureAttribute()
        {
            string? dir = Environment.GetEnvironmentVariable("FOOTAGE_EDIT_FIXTURE_DIR");
            if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "an2.json")))
                Skip = "Local fixture not available: set FOOTAGE_EDIT_FIXTURE_DIR to a directory with an2.json (and edl3.json for the score test).";
        }
    }
}
