using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Sp = (double Start, double End, int SourceIndex, System.Collections.Generic.IReadOnlyList<string> OriginIds);

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>One analysis holding several source clips: the outline, the read tools, speech-safe cuts and the interleave guard.</summary>
public class MultiSourceOutlineTests
{
    /// <summary>Talking heads: word tokens in bursts with pauses; each source has its own pace; ids t{n} run across sources.</summary>
    private static VideoAnalysisArtifact Sources(params double[] durations)
    {
        List<VideoAnalysisSegment> segs = [];
        List<VideoAnalysisShot> shots = [];
        List<VideoAnalysisSourceInfo> infos = [];
        int t = 0, s = 0;
        for (int src = 0; src < durations.Length; src++)
        {
            double step = 0.28 + 0.03 * src;
            double clock = 0;
            int burst = 0;
            while (clock + step < durations[src] - 0.5)
            {
                if (burst > 0 && burst % 8 == 0)
                    clock += 0.4;
                segs.Add(new VideoAnalysisSegment("t" + t++, null, clock, clock + step * 0.8, $"word{src}x{burst}", SourceIndex: src));
                clock += step;
                burst++;
            }

            for (double at = 0; at < durations[src]; at += 10)
                shots.Add(new VideoAnalysisShot("s" + s++, at, Math.Min(durations[src], at + 10), SourceIndex: src));
            infos.Add(new VideoAnalysisSourceInfo(src, "k" + src, new VideoAnalysisMedia(durations[src], 30, 1, 1920, 1080, "h264", "aac")));
        }

        return new VideoAnalysisArtifact(
            2, infos[0].Media, shots, [], segs, [], [.. shots.Select(x => x.Id), .. segs.Select(x => x.Id)],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Sources: infos);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    public void Every_source_is_covered_inside_the_budget_and_none_is_dropped(int count)
    {
        VideoAnalysisArtifact artifact = Sources([.. Enumerable.Range(0, count).Select(i => count == 2 ? (i == 0 ? 120.0 : 90.0) : 60.0 + 7 * i)]);
        VideoSpeechOutlineResult outline = VideoSpeechOutline.Build(artifact)!;
        double total = artifact.Sources!.Sum(x => x.Media.DurationSec);

        outline.TokensEstimate.Should().BeLessThanOrEqualTo(VideoEventOutline.OutlineTokenBudget(total));
        Console.WriteLine($"sources={count} tokens={outline.TokensEstimate} sections={outline.Sections.Count} summarised={outline.Summarised}");
        outline.Nodes.ToJsonString().Should().NotContain("Sec");

        if (count == 1)
        {
            outline.Sources.Should().BeNull();
            outline.Nodes.ToJsonString().Should().NotContain("\"source");
            return;
        }

        outline.Sources!.Count.Should().Be(count);
        for (int src = 0; src < count; src++)
        {
            JsonObject line = outline.Sources[src]!.AsObject();
            line["source"]!.GetValue<int>().Should().Be(src);
            List<VideoAnalysisSegment> own = [.. artifact.Segments.Where(x => x.SourceIndex == src)];
            line["ids"]!.GetValue<string>().Should().Be(own[0].Id + "-" + own[^1].Id, "the line reaches the source's whole speech");
        }

        outline.Nodes.Select(n => n!["source"] ?? n["sources"]).Should().OnlyContain(x => x != null);
    }

    [Fact]
    public void Read_tools_resolve_ids_of_every_source_and_answer_bad_input_with_bounded_text()
    {
        VideoAnalysisArtifact artifact = Sources(120, 90);
        VideoAnalysisSegment first1 = artifact.Segments.First(x => x.SourceIndex == 1);

        JsonNode read = JsonNode.Parse(VideoFootageReader.ReadTranscript(artifact, first1.Id, artifact.Segments[^1].Id, null))!;
        read["phrases"]!.AsArray().Count.Should().BeGreaterThan(0);
        VideoEventWords.EstimateTokens(read.ToJsonString()).Should().BeLessThanOrEqualTo(VideoFootageReader.MaxTokens);

        string across = VideoFootageReader.ReadTranscript(artifact, "t0", artifact.Segments[^1].Id, null);
        VideoEventWords.EstimateTokens(across).Should().BeLessThanOrEqualTo(VideoFootageReader.MaxTokens);

        JsonNode.Parse(VideoFootageReader.ReadTranscript(artifact, "t999999", "t0", null))!["error"].Should().NotBeNull();
        JsonNode.Parse(VideoFootageReader.ReadSection(artifact, "c99", null))!["error"].Should().NotBeNull();
    }

    [Fact]
    public void Two_source_plan_cuts_outside_words_on_both_sources_and_satisfies_the_interleave_guard()
    {
        VideoAnalysisArtifact artifact = Sources(120, 90);
        List<SpeechPhrase> phrases = VideoSpeechOutline.Phrases(artifact);
        Dictionary<string, VideoAnalysisSegment> byId = artifact.Segments.ToDictionary(x => x.Id);
        SpeechPhrase P(int src, int k) => phrases.Where(p => p.SourceIndex == src).ElementAt(k);

        // A stub story-editor plan: src0, src1, src0 again; each keep starts and ends on phrase ids (what the tools return).
        (SpeechPhrase From, SpeechPhrase To)[] plan =
        [
            (P(0, 2), P(0, 6)), (P(1, 3), P(1, 8)), (P(0, 10), P(0, 14))
        ];
        VideoEditDecisionOutput decision = new()
        {
            Keep = [.. plan.Select(x => new VideoEditKeepSpan { FromId = x.From.FromId, ToId = x.To.ToId, Reason = "stub" })],
            EditRationale = "stub",
            SuggestedTitle = "t"
        };

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(InterleaveOffer.FromArtifact(artifact), decision, null);
        verdict.Required.Should().BeTrue();
        verdict.Satisfied.Should().BeTrue(verdict.Reason);

        List<SpeechToken> tokens = [.. artifact.Segments.Select(x => new SpeechToken(x.StartSec, x.EndSec, x.SourceIndex))];
        // Start every keep a little INSIDE its first word and end it inside its last, so the pass has real work on both sources.
        List<Sp> spans = [.. plan.Select(x => (byId[x.From.FromId].StartSec + 0.05, byId[x.To.ToId].EndSec - 0.05, x.From.SourceIndex,
            (IReadOnlyList<string>)[x.From.FromId, x.To.ToId]))];
        IReadOnlyDictionary<int, double> durations = new Dictionary<int, double> { [0] = 120, [1] = 90 };
        SpeechSafeCuts.CountInsideWords(spans.Select(x => (x.Start, x.End, x.SourceIndex)), tokens).Should().BeGreaterThan(0, "the input must start inside words");

        (List<Sp> final, SpeechSafeCutsReport report) = SpeechSafeCuts.Apply(spans, tokens, null, durations, null, 0.25);

        report.CutsInsideWordsBefore.Should().BeGreaterThan(0);
        report.CutsInsideWords.Should().Be(0);
        SpeechSafeCuts.CountInsideWords(final.Select(x => (x.Start, x.End, x.SourceIndex)), tokens).Should().Be(0, "counted on the OUTPUT, both sources");
        final.Select(x => x.SourceIndex).Should().Equal(0, 1, 0);

        VideoEditDecisionOutput finalDecision = new()
        {
            Keep = [.. final.Select(x => new VideoEditKeepSpan { FromId = x.OriginIds[0], ToId = x.OriginIds[^1], Reason = "stub" })],
            EditRationale = "stub",
            SuggestedTitle = "t"
        };
        InterleaveGuard.Evaluate(InterleaveOffer.FromArtifact(artifact), finalDecision, null).Satisfied.Should().BeTrue();
    }

    [Fact]
    public void Many_sources_are_summarised_loudly_but_every_source_keeps_its_line()
    {
        VideoAnalysisArtifact artifact = Sources([.. Enumerable.Range(0, 150).Select(_ => 20.0)]);
        VideoSpeechOutlineResult outline = VideoSpeechOutline.Build(artifact)!;
        Console.WriteLine($"sources=150 tokens={outline.TokensEstimate} summarised={outline.Summarised}");

        outline.Summarised.Should().BeTrue();
        outline.Sources!.Count.Should().Be(150);
        outline.Sources.Select(l => l!["source"]!.GetValue<int>()).Should().Equal(Enumerable.Range(0, 150));

        JsonObject view = new() { ["shots"] = new JsonArray(), ["silences"] = new JsonArray(), ["segments"] = new JsonArray() };
        JsonObject meta = new() { ["droppedItems"] = 5, ["offeredIdCount"] = 1 };
        var config = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.PreviousStepOutput)) with { Outline = true };
        ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoSpeechOutlineView.Apply(view, meta, artifact, config);
        meta["outline_summarised"].Should().NotBeNull();
        view["outline"]!["sources"]!.AsArray().Count.Should().Be(150);
    }
}
