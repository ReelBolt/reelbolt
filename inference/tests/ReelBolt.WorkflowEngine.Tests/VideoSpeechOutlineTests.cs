using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The whole-film speech outline, its read tools and the view that carries it, on a synthetic 170 s talking head.</summary>
public class VideoSpeechOutlineTests
{
    private const double Seconds = 170;
    private const int Tokens = 683;
    private const int Shots = 89;

    /// <summary>Word-level tokens spread over the whole film in bursts of 8 with a pause between them; optional beat events.</summary>
    private static VideoAnalysisArtifact TalkingHead(bool withBeats = false)
    {
        double step = Seconds / Tokens;
        List<VideoAnalysisSegment> segments = [];
        double t = 0;
        for (int i = 0; i < Tokens; i++)
        {
            double pause = i > 0 && i % 8 == 0 ? 0.4 : 0;
            double start = t + pause;
            segments.Add(new VideoAnalysisSegment("t" + i, null, start, start + step * 0.8, "word" + (i % 17 == 0 ? "gadget" : "x") + i));
            t = start + step * 0.85;
        }

        double scale = Seconds / t;
        segments = [.. segments.Select(s => s with { StartSec = s.StartSec * scale, EndSec = s.EndSec * scale })];
        List<VideoAnalysisShot> shots = [.. Enumerable.Range(0, Shots).Select(i => new VideoAnalysisShot("s" + i, i * Seconds / Shots, (i + 1) * Seconds / Shots))];
        List<VideoAnalysisRenderEvent>? events = null;
        if (withBeats)
            events = [.. Enumerable.Range(0, 363).Select(i => new VideoAnalysisRenderEvent(
                "e" + i, "beat", "beat", i * 14, i * Seconds / 363, SourceIndex: 0, Type: nameof(VideoEventType.Beat), Origin: "Audio", Strength: 0.5))];

        VideoAnalysisMedia media = new(Seconds, 30, 1, 1920, 1080, "h264", "aac");
        return new VideoAnalysisArtifact(
            2, media, shots, [], segments, [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, "k", media)],
            Events: events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_outline_covers_the_whole_film_inside_its_budget(bool withBeats)
    {
        VideoSpeechOutlineResult outline = VideoSpeechOutline.Build(TalkingHead(withBeats))!;

        outline.Sections[0].StartSec.Should().Be(0);
        outline.Sections[^1].EndSec.Should().BeGreaterThanOrEqualTo(Seconds * 0.95);
        outline.LastSec.Should().BeGreaterThanOrEqualTo(Seconds * 0.95);
        outline.Sections.Count.Should().BeInRange(3, 12);
        outline.TokensEstimate.Should().BeLessThanOrEqualTo(VideoEventOutline.OutlineTokenBudget(Seconds));
        outline.Nodes.Count.Should().Be(outline.Sections.Count);
        // The last section really holds speech from the end of the film.
        outline.Nodes[^1]!["cuts"]!.AsArray().Select(n => VideoEventOutline.IdNumber(n!.GetValue<string>())).Max().Should().BeGreaterThan(Tokens * 8 / 10);
    }

    [Fact]
    public void The_outline_is_deterministic_and_carries_no_time()
    {
        string a = VideoSpeechOutline.Build(TalkingHead())!.Nodes.ToJsonString();
        string b = VideoSpeechOutline.Build(TalkingHead())!.Nodes.ToJsonString();
        a.Should().Be(b);
        a.Should().NotContain("Sec").And.NotContain("startSec");
    }

    [Fact]
    public void Beats_become_one_tempo_line_per_section_and_no_beat_ids()
    {
        VideoSpeechOutlineResult outline = VideoSpeechOutline.Build(TalkingHead(withBeats: true))!;
        string json = outline.Nodes.ToJsonString();
        json.Should().Contain("bpm, beats omitted");
        json.Should().NotContain("\"e");
    }

    [Fact]
    public void Cut_points_start_a_run_of_speech_and_never_land_inside_one()
    {
        VideoAnalysisArtifact artifact = TalkingHead();
        Dictionary<string, SpeechPhrase> byStart = VideoSpeechOutline.Phrases(artifact).ToDictionary(p => p.FromId, StringComparer.Ordinal);
        VideoSpeechOutlineResult outline = VideoSpeechOutline.Build(artifact)!;

        foreach (JsonNode? section in outline.Nodes)
            foreach (JsonNode? cut in section!["cuts"]!.AsArray())
                byStart.Should().ContainKey(cut!.GetValue<string>(), "a cut point is the first token after a pause");
    }

    [Fact]
    public void Every_tool_answer_is_bounded_and_every_id_in_it_resolves_for_the_compile()
    {
        VideoAnalysisArtifact artifact = TalkingHead(withBeats: true);
        var idTimes = VideoCompileStepExecutor.BuildIdTimeIndex(artifact);

        foreach (string section in VideoSpeechOutline.SectionsFor(artifact).Select(s => s.Id))
        {
            string cursor = "";
            for (int page = 0; page < 20; page++)
            {
                JsonObject answer = JsonNode.Parse(VideoFootageReader.ReadSection(artifact, section, cursor))!.AsObject();
                VideoEventWords.EstimateTokens(answer.ToJsonString()).Should().BeLessThanOrEqualTo(VideoFootageReader.MaxTokens);
                foreach (JsonNode? phrase in answer["phrases"]!.AsArray())
                    foreach (string id in phrase!["ids"]!.GetValue<string>().Split('-'))
                        idTimes.Should().ContainKey(id);
                if (answer["truncated"]!.GetValue<bool>() == false)
                    break;
                answer["next"]!.GetValue<string>().Should().NotBeNullOrEmpty();
                cursor = answer["next"]!.GetValue<string>();
            }
        }

        JsonObject range = JsonNode.Parse(VideoFootageReader.ReadTranscript(artifact, "t10", "c3", null))!.AsObject();
        VideoEventWords.EstimateTokens(range.ToJsonString()).Should().BeLessThanOrEqualTo(VideoFootageReader.MaxTokens);
        range["note"]!.GetValue<string>().Should().Contain("UNTRUSTED");
        JsonNode.Parse(VideoFootageReader.ReadTranscript(artifact, "zzz", "t4", null))!["error"].Should().NotBeNull();
    }

    [Fact]
    public void A_view_that_left_items_out_becomes_the_outline_plus_the_first_section_and_every_id_is_reachable()
    {
        VideoAnalysisArtifact artifact = TalkingHead();
        JsonObject view = new()
        {
            ["shots"] = new JsonArray([.. artifact.Shots.Where((_, i) => i % 3 == 0).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id })]),
            ["silences"] = new JsonArray(),
            ["segments"] = new JsonArray([.. artifact.Segments.Where((_, i) => i % 2 == 0).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id })])
        };
        JsonObject meta = new() { ["droppedItems"] = 400, ["offeredIdCount"] = 1 };
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true };

        List<string>? reachable = VideoSpeechOutlineView.Apply(view, meta, artifact, config);

        reachable.Should().NotBeNull();
        reachable!.Should().Contain("t682").And.Contain("s88");
        view["outline"]!["sections"]!.AsArray().Count.Should().BeGreaterThanOrEqualTo(3);
        view["segments"]!.AsArray().Count.Should().BeLessThan(Tokens / 2);
        meta["offeredCoverage"]!["lastSec"]!.GetValue<double>().Should().BeGreaterThanOrEqualTo(Seconds * 0.95);
        meta["offeredCoverage"]!["sourceDurationSec"]!.GetValue<double>().Should().BeApproximately(Seconds, 0.01);
        // Off, or a complete view, leaves everything untouched.
        VideoSpeechOutlineView.Apply(new JsonObject(), new JsonObject { ["droppedItems"] = 0 }, artifact, config).Should().BeNull();
        VideoSpeechOutlineView.Apply(new JsonObject(), new JsonObject { ["droppedItems"] = 5 }, artifact, config with { Outline = false }).Should().BeNull();
    }
}
