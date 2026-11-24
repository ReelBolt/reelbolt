using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.Shared.Data.OutputSchemas;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A 174 s talking head (89 shots, ~690 word tokens, 14 sound effects) through the real step at the default budget.</summary>
public partial class VideoAnalyzeStepExecutorTests
{
    private const double ReservedDuration = 174.2;

    private static string? _lastArtifactJson;

    private static async Task<(JsonObject Root, StepExecutionResult Result)> RunTalkingHeadAsync(
        Func<VideoAnalyzeStepConfig, VideoAnalyzeStepConfig> configure, int tokens = 690, int shotCount = 89, int sfxCount = 14,
        ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector? detector = null, double durationSec = ReservedDuration,
        Mock<IFrameGridSampler>? frameGridSampler = null)
    {
        StepExecutionContext context = CreateContext(
            out Mock<IProjectFileWorkspace> workspace, out Mock<IMediaProbe> probe, out Mock<ISilenceDetector> silence,
            out Mock<IShotDetector> shotDetector, out Mock<ITranscriptionClientFactory> transcriptionFactory,
            out Mock<IInferenceProviderResolver> providerResolver,
            configOverride: c => configure(c with { Transcription = VideoTranscriptionMode.Required, DetectSilence = false, AnalyzeAudioLevels = false }));
        probe.Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(durationSec, 60, 1, 1920, 1080, "h264", "aac", 48000));
        silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(double, double)>());
        double shotLen = durationSec / shotCount;
        shotDetector.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, shotCount).Select(i => (i * shotLen, (i + 1) * shotLen)).ToArray());
        workspace.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(0, sfxCount).Select(i =>
                new ProjectWorkspaceFile(Guid.NewGuid(), Guid.NewGuid(), $"sfx{i:D2}.wav", null, "userFiles", "k" + i, "audio/wav", 1000, DateTime.UtcNow, null)).ToList());

        // Word-level tokens in bursts of 8 with a pause between them, like the prod ASR.
        List<TranscriptSegment> words = [];
        double step = (durationSec - 2) / (tokens * 1.05), t = 0.5;
        for (int i = 0; i < tokens; i++)
        {
            if (i > 0 && i % 8 == 0) t += step * 0.4;
            words.Add(new TranscriptSegment($"word{i}", t, t + step * 0.8));
            t += step;
        }

        SetupTranscription(transcriptionFactory, providerResolver, [.. words]);
        string? artifactJson = null;
        VideoAnalyzeStepExecutor executor = CreateExecutor(
            workspace, probe, silence, shotDetector, transcriptionFactory, providerResolver, frameGridSampler: frameGridSampler, eventDetector: detector);
        // After CreateExecutor, whose own default setup would otherwise win.
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                string text = File.ReadAllText(path);
                if (text.StartsWith('{') && text.Contains("\"segments\""))
                    artifactJson = text;
                return Task.FromResult("projects/p/agentFiles/video-analysis/e/step-1-analysis.json");
            });
        StepExecutionResult result = await executor.ExecuteAsync(context);
        _lastArtifactJson = artifactJson;
        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        return (JsonNode.Parse(result.Output)!.AsObject(), result);
    }

    [Fact]
    public async Task Outline_mode_keeps_the_sfx_list_and_offers_the_whole_film_at_the_default_budget()
    {
        (JsonObject root, _) = await RunTalkingHeadAsync(c => c with { Outline = true, OfferSfxClips = true });
        JsonObject view = root["view"]!.AsObject(), meta = root["meta"]!.AsObject();

        meta["offeredSfxClipCount"]!.GetValue<int>().Should().Be(14);
        view["sfxClips"]!.AsArray().Count.Should().Be(14);
        string warnings = (root["warnings"] ?? new JsonArray()).ToJsonString();
        warnings.Should().NotContain("sfx_clips_suppressed").And.NotContain("maxOutputChars").And.NotContain("Raise");
        meta["offeredCoverage"]!["lastSec"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(ReservedDuration * 0.95);

        JsonArray phrases = view["outline"]!["phrases"]!.AsArray();
        phrases.Count.Should().BeGreaterThan(20);
        phrases.Last()!["ids"]!.GetValue<string>().Should().EndWith("t689", "the last phrase of the film is listed");
        meta["offeredIdCount"]!.GetValue<int>().Should().BeGreaterThanOrEqualTo(690 + 89);
        int limit = meta["effectiveMaxOutputChars"]?.GetValue<int>() ?? 24000;
        view.ToJsonString().Length.Should().BeLessThanOrEqualTo(limit, "the view never exceeds the effective budget");
        Console.WriteLine($"effective={limit} viewChars={view.ToJsonString().Length} offeredIds={meta["offeredIdCount"]} sfx={meta["offeredSfxClipCount"]}");
    }

    [Fact]
    public async Task Without_the_outline_flag_the_old_suppression_still_happens()
    {
        (JsonObject root, _) = await RunTalkingHeadAsync(c => c with { Outline = false, OfferSfxClips = true });
        Console.WriteLine($"OLD sfx={root["meta"]!["offeredSfxClipCount"]} ids={root["meta"]!["offeredIdCount"]} dropped={root["meta"]!["droppedItems"]} warn={(root["warnings"] ?? new JsonArray()).ToJsonString().Length}");
        root["meta"]!["effectiveMaxOutputChars"].Should().BeNull();
        root["view"]!["outline"].Should().BeNull();
        root["meta"]!["offeredSfxClipCount"]!.GetValue<int>().Should().Be(0, "flag off keeps the old suppression");
        (root["warnings"] ?? new JsonArray()).ToJsonString().Should().Contain("sfx_clips_suppressed");
    }

    private sealed class BeatDetector(int beats) : ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector
    {
        public Task<ReelBolt.Shared.Workflows.VideoEvents.VideoEventSet> DetectAsync(
            IMediaWorkspace workspace, string pixelsVirtualPath, MediaProbeResult probe, ReelBolt.WorkflowEngine.Services.Video.Events.VideoEventOptions? options = null,
            string? audioVirtualPath = null, int pixelsFpsNum = 0, int pixelsFpsDen = 0, CancellationToken ct = default)
        {
            List<ReelBolt.Shared.Workflows.VideoEvents.VideoEvent> events = [.. Enumerable.Range(0, beats).Select(i =>
                new ReelBolt.Shared.Workflows.VideoEvents.VideoEvent(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.Beat, 3 + i * 28, null, null, 0.5,
                    ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Audio, 0.9))];
            for (int i = 0; i < 37; i++)
                events.Add(new(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.Hit, 100 + i * 250, null, null, 0.9,
                    ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Pixels, 0.9));
            return Task.FromResult(new ReelBolt.Shared.Workflows.VideoEvents.VideoEventSet(60, (int)(ReservedDuration * 60), 1920, 1080, events, 0));
        }
    }

    [Fact]
    public async Task Prod_shaped_run_with_400_events_keeps_everything_inside_the_budget_and_the_last_section_compiles()
    {
        (JsonObject root, _) = await RunTalkingHeadAsync(
            c => c with { Outline = true, OfferSfxClips = true, AnalyzeEvents = true }, detector: new BeatDetector(363));
        JsonObject view = root["view"]!.AsObject(), meta = root["meta"]!.AsObject();

        meta["offeredSfxClipCount"]!.GetValue<int>().Should().Be(14);
        string warnings = (root["warnings"] ?? new JsonArray()).ToJsonString();
        warnings.Should().NotContain("sfx_clips_suppressed").And.NotContain("Raise").And.NotContain("\"maxOutputChars\"");
        int limit = meta["effectiveMaxOutputChars"]?.GetValue<int>() ?? 24000;
        view.ToJsonString().Length.Should().BeLessThanOrEqualTo(limit);
        view["outline"]!["phrases"].Should().NotBeNull("the events never take the phrase list's room");
        (view["events"]?.AsArray().Select(e => e!["type"]!.GetValue<string>()) ?? []).Should().NotContain("beat");

        // A keep on the last phrase of the film resolves in the compile's id index.
        VideoAnalysisArtifact artifact = ReelBolt.Shared.Workflows.VideoEvents.VideoEventReader.Parse(_lastArtifactJson!)!;
        var idTimes = ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.BuildIdTimeIndex(artifact);
        string lastPhrase = view["outline"]!["phrases"]!.AsArray().Last()!["ids"]!.GetValue<string>();
        foreach (string id in lastPhrase.Split('-'))
            idTimes.Should().ContainKey(id);
    }

    [Fact]
    public async Task A_small_configured_limit_is_raised_to_hold_the_reserved_parts_and_never_above_200000()
    {
        (JsonObject root, _) = await RunTalkingHeadAsync(c => c with { Outline = true, OfferSfxClips = true, MaxOutputChars = 3000 });
        JsonObject meta = root["meta"]!.AsObject();

        int effective = meta["effectiveMaxOutputChars"]!.GetValue<int>();
        effective.Should().BeGreaterThan(3000).And.BeLessThanOrEqualTo(200_000);
        meta["offeredSfxClipCount"]!.GetValue<int>().Should().Be(14);
        root["view"]!.ToJsonString().Length.Should().BeLessThanOrEqualTo(effective);

        (JsonObject huge, _) = await RunTalkingHeadAsync(c => c with { Outline = true, OfferSfxClips = true, MaxOutputChars = 500_000 }, tokens: 9000, shotCount: 90, durationSec: 3000);
        (huge["meta"]!["effectiveMaxOutputChars"]?.GetValue<int>() ?? 200_000).Should().BeLessThanOrEqualTo(200_000);
        huge["view"]!.ToJsonString().Length.Should().BeLessThanOrEqualTo(200_000);
    }

    private sealed class EditRoomProbe() : EditRoomStepExecutor(
        Mock.Of<IAgentChatClientProvider>(), Mock.Of<IAgentToolProvider>(), Mock.Of<IAgentRegistry>(), Mock.Of<IProjectFileWorkspace>(),
        new WorkflowExecutionContextAccessor(), Microsoft.Extensions.Logging.Abstractions.NullLogger<EditRoomStepExecutor>.Instance, null!)
    {
        public (int Dropped, HashSet<string> Offered) Run(VideoEditDecisionOutput decision, JsonNode root)
        {
            HashSet<string> offered = ExtractOfferedIds(root);
            return (FilterToOfferedIds(decision, offered, root).Dropped, offered);
        }
    }

    private sealed class ColorGradeProbe() : ColorGradeRoomStepExecutor(
        Mock.Of<IAgentChatClientProvider>(), Mock.Of<IAgentToolProvider>(), Mock.Of<IAgentRegistry>(), Mock.Of<IProjectFileWorkspace>(),
        new WorkflowExecutionContextAccessor(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ColorGradeRoomStepExecutor>.Instance)
    {
        public HashSet<string> Offered(JsonNode root) => ExtractOfferedIds(root);
    }

    [Fact]
    public async Task Rooms_and_the_interleave_guard_accept_ids_from_the_last_section_in_outline_mode()
    {
        (JsonObject root, _) = await RunTalkingHeadAsync(c => c with { Outline = true, OfferSfxClips = true, MaxOutputChars = 6000 });
        root["view"]!["outline"]!["phrases"].Should().NotBeNull();
        string lastPhrase = root["view"]!["outline"]!["phrases"]!.AsArray().Last()!["ids"]!.GetValue<string>();
        string[] ends = lastPhrase.Split('-');

        // The EditRoom no longer drops a keep whose ids come from the outline (the id-drop bug).
        var decision = new VideoEditDecisionOutput
        {
            Keep = [new VideoEditKeepSpan { FromId = ends[0], ToId = ends[^1], Reason = "last thought" }],
            EditRationale = "x",
            SuggestedTitle = "t"
        };
        (int dropped, HashSet<string> offered) = new EditRoomProbe().Run(decision, root);
        dropped.Should().Be(0);
        decision.Keep.Should().HaveCount(1);
        offered.Should().Contain("t0").And.Contain("t689").And.Contain("s88", "late shot ids are listed as ranges");

        // A genuinely unknown id is still dropped.
        var bogus = new VideoEditDecisionOutput { Keep = [new VideoEditKeepSpan { FromId = "t9999", ToId = "t10000", Reason = "x" }] };
        new EditRoomProbe().Run(bogus, root).Dropped.Should().Be(1);

        new ColorGradeProbe().Offered(root).Should().Contain("s88");
        ReelBolt.WorkflowEngine.Services.Video.InterleaveOffer.FromView(root).IdSources.Should().ContainKey("t689");
    }

    private sealed class GraphicsRoomProbe() : GraphicsRoomStepExecutor(
        Mock.Of<IAgentChatClientProvider>(), Mock.Of<IAgentToolProvider>(), Mock.Of<IAgentRegistry>(), Mock.Of<IProjectFileWorkspace>(),
        new WorkflowExecutionContextAccessor(), Mock.Of<IMotionGraphicsPlacementAnnotator>(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<GraphicsRoomStepExecutor>.Instance)
    {
        public HashSet<string> Offered(JsonNode root) => ExtractOfferedIds(root);
    }

    [Fact]
    public async Task Graphics_room_keeps_its_placements_and_the_outline_on_a_long_source_at_the_default_budget()
    {
        var grid = new Mock<IFrameGridSampler>();
        grid.Setup(g => g.SampleAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildColoredGrid(4, 4, fps: 2.0, (60, 150, 140, 130)));

        (JsonObject root, _) = await RunTalkingHeadAsync(
            c => c with
            {
                Outline = true,
                OfferSfxClips = true,
                AnalyzeVisuals = true,
                EmitOverlayPlacements = true,
                VisualDetail = VideoVisualDetail.Full,
                DetectNearDuplicates = false,
                MaxPlacementsPerShot = 1,
                MaxOutputChars = 24_000
            },
            frameGridSampler: grid);
        JsonObject view = root["view"]!.AsObject();

        view["placements"].Should().NotBeNull("placements are reserved in outline mode");
        HashSet<string> offered = new GraphicsRoomProbe().Offered(root);
        offered.Should().NotBeEmpty();
        view["placements"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).Should().OnlyContain(id => offered.Contains(id));
        root["meta"]!["offeredSfxClipCount"]!.GetValue<int>().Should().Be(14);
        // The outline's phrase ids are still there and the edit side accepts them.
        string lastPhrase = view["outline"]!["phrases"]!.AsArray().Last()!["ids"]!.GetValue<string>();
        var decision = new VideoEditDecisionOutput { Keep = [new VideoEditKeepSpan { FromId = lastPhrase.Split('-')[0], ToId = lastPhrase.Split('-')[^1], Reason = "x" }] };
        new EditRoomProbe().Run(decision, root).Dropped.Should().Be(0);
        view.ToJsonString().Length.Should().BeLessThanOrEqualTo(root["meta"]!["effectiveMaxOutputChars"]?.GetValue<int>() ?? 24_000);
    }

    [Fact]
    public async Task Outline_mode_gives_the_colour_room_a_reading_for_every_shot_and_the_range_universe_for_every_id()
    {
        var grid = new Mock<IFrameGridSampler>();
        grid.Setup(g => g.SampleAsync(
                It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildColoredGrid(4, 4, fps: 2.0, (60, 150, 140, 130)));
        (JsonObject root, _) = await RunTalkingHeadAsync(
            c => c with { Outline = true, AnalyzeVisuals = true, VisualDetail = VideoVisualDetail.Full, DetectNearDuplicates = false },
            frameGridSampler: grid);

        JsonArray colour = root["view"]!["outline"]!["colour"]!.AsArray();
        // Only the shots the visual pass measured have a reading; one past the first section (s0 to s6) must be among them.
        colour.Select(r => r!.GetValue<string>()).Should().Contain(r => r.StartsWith("s15 temp="), "a late shot has a reading too");
        root["meta"]!["offeredIdRanges"]!.AsArray().Select(r => r!["ids"]!.GetValue<string>()).Should().Contain("t0-t689");
    }

    [Fact]
    public void A_multi_hour_transcript_without_a_phrase_list_still_lets_the_room_keep_a_late_id()
    {
        int n = 12_000;
        List<VideoAnalysisSegment> segs = [.. Enumerable.Range(0, n).Select(i => new VideoAnalysisSegment("t" + i, null, i * 0.9, i * 0.9 + 0.5, "word" + i))];
        VideoAnalysisMedia media = new(n * 0.9 + 1, 30, 1, 1920, 1080, "h264", "aac");
        VideoAnalysisArtifact artifact = new(2, media, [], [], segs, [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Sources: [new VideoAnalysisSourceInfo(0, "k", media)]);
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true };
        JsonObject view = new() { ["shots"] = new JsonArray(), ["silences"] = new JsonArray(), ["segments"] = new JsonArray() };
        JsonObject meta = new() { ["droppedItems"] = 5, ["effectiveMaxOutputChars"] = 24_000 };

        VideoSpeechOutlineView.Apply(view, meta, artifact, config);

        view["outline"]!["phrases"].Should().BeNull("the phrase list did not fit");
        JsonObject root = new() { ["view"] = view, ["meta"] = meta };
        var decision = new VideoEditDecisionOutput { Keep = [new VideoEditKeepSpan { FromId = "t11000", ToId = "t11500", Reason = "late" }] };
        new EditRoomProbe().Run(decision, root).Dropped.Should().Be(0);
        ReelBolt.WorkflowEngine.Services.Video.InterleaveOffer.FromView(root).IdSources.Should().ContainKey("t11999");
    }

    [Fact]
    public void Outline_mode_warnings_carry_no_limit_key_and_the_off_path_keeps_it()
    {
        VideoAnalysisArtifact artifact = new(2, new VideoAnalysisMedia(10, 30, 1, 1920, 1080, "h264", "aac"), [], [], [], [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            SfxCandidates: [new VideoAnalysisSfxCandidate("x0", Guid.NewGuid(), "a.wav", "audio/wav", 10)]);
        var config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { OfferSfxClips = true };

        var on = VideoAnalyzeStepExecutor.BuildViewBudgetWarnings(config with { Outline = true }, artifact, [], [], [], [], [], []);
        var off = VideoAnalyzeStepExecutor.BuildViewBudgetWarnings(config, artifact, [], [], [], [], [], []);

        on.Should().ContainSingle().Which.ContainsKey("maxOutputChars").Should().BeFalse();
        off.Should().ContainSingle().Which.ContainsKey("maxOutputChars").Should().BeTrue();
    }

    [Fact]
    public void Expanding_a_view_is_capped()
    {
        JsonObject view = new() { ["outline"] = new JsonObject { ["phrases"] = new JsonArray(new JsonObject { ["ids"] = "t0-t190000" }, new JsonObject { ["ids"] = "t200000-t390000" }) } };

        ReelBolt.Shared.Workflows.VideoEvents.OutlineViewIds.Expand(view).Count().Should().Be(ReelBolt.Shared.Workflows.VideoEvents.OutlineViewIds.MaxExpandedIds);
    }
}
