using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>Speaker labels: provider parsing from hand-written fixtures (no network), merge policy, outline and reader.</summary>
public class SpeakerLabelsTests
{
    private const string FishDiarized = """
        {"text":"<|speaker:0|> Hi there <|speaker:1|> Hello","duration":4.0,
         "segments":[{"text":"Hi","start":0.0,"end":0.5},{"text":"there","start":0.6,"end":1.0},{"text":"Hello","start":2.0,"end":2.5},{"text":"gap","start":3.1,"end":3.3}],
         "speaker_turns":[{"speaker":"speaker:0","text":"Hi there","start":0.0,"end":1.2},{"speaker":"speaker:1","text":"Hello","start":1.9,"end":3.0}]}
        """;

    private static HttpClient Http(HttpMessageHandler h) => new(h) { BaseAddress = new Uri("https://api.fish.audio") };

    private static Task<TranscriptResult> Fish(string body, string? model, out FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler handler)
    {
        handler = new FishAudioSpeechSynthesisClientTests.MockHttpMessageHandler(body, null, HttpStatusCode.OK);
        return new FishAudioTranscriptionClient(Http(handler), model).TranscribeAsync(
            new MemoryStream(FishAudioSpeechSynthesisClientTests.GetSampleWavBytes()), "a.wav", null, false, CancellationToken.None);
    }

    [Fact]
    public async Task Fish_pro_model_sends_diarize_and_labels_by_midpoint_with_gaps_unlabeled()
    {
        TranscriptResult r = await Fish(FishDiarized, "transcribe-1-pro", out var h);

        r.Segments.Select(s => s.Speaker).Should().Equal("speaker:0", "speaker:0", "speaker:1", null);
        r.Text.Should().NotContain("<|");
        h.LastRequest!.Headers.GetValues("model").Single().Should().Be("transcribe-1-pro");
        h.LastRequestBody.Should().Contain("diarize");
    }

    [Fact]
    public async Task Fish_default_model_sends_no_diarize_and_stays_unlabeled()
    {
        TranscriptResult r = await Fish("""{"text":"Hi","duration":1,"segments":[{"text":"Hi","start":0,"end":1}]}""", null, out var h);

        r.Segments.Single().Speaker.Should().BeNull();
        h.LastRequestBody.Should().NotContain("diarize");
    }

    [Fact]
    public void Midpoint_assignment_edge_cases()
    {
        SpeakerTurn[] turns = [new("a", 0, 1), new("b", 0.5, 2)];
        TranscriptionDiarization.SpeakerAt(turns, 0.6, 0.8).Should().Be("a"); // overlap: first listed
        TranscriptionDiarization.SpeakerAt(turns, 2.5, 3.0).Should().BeNull(); // gap
        TranscriptionDiarization.SpeakerAt(turns, 0.9, 1.1).Should().Be("b"); // midpoint 1.0 is past turn a
        TranscriptionDiarization.SpeakerAt([], 0, 1).Should().BeNull();
    }

    [Fact]
    public void OpenAi_diarized_json_maps_segments_with_speakers_and_no_words()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            {"text":"Hi. Yes.","segments":[{"id":"s0","start":0,"end":1,"text":"Hi.","speaker":"A"},{"id":"s1","start":1,"end":2,"text":"Yes.","speaker":"B"}]}
            """);
        TranscriptResult r = TranscriptionDiarization.ParseOpenAiDiarized(doc.RootElement);

        r.Words.Should().BeEmpty();
        r.Segments.Select(s => s.Speaker).Should().Equal("A", "B");
        TranscriptionDiarization.OpenAiModelDiarizes("gpt-4o-transcribe-diarize").Should().BeTrue();
        TranscriptionDiarization.OpenAiModelDiarizes("gpt-transcribe").Should().BeFalse();
    }

    [Fact]
    public void Chunk_merge_drops_labels_when_more_than_one_chunk_and_keeps_them_for_one()
    {
        TranscriptResult r = new("x", [new TranscriptSegment("a", 0, 1, Speaker: "A")], [new TranscriptWord("a", 0, 1, "A")], null);

        TranscriptionDiarization.MergeChunkSpeakers(r, 1).Segments[0].Speaker.Should().Be("A");
        TranscriptResult merged = TranscriptionDiarization.MergeChunkSpeakers(r, 2);
        merged.Segments[0].Speaker.Should().BeNull();
        merged.Words[0].Speaker.Should().BeNull();
    }

    [Fact]
    public void Normalizer_numbers_by_first_appearance_and_keeps_sources_apart()
    {
        SpeakerIdNormalizer n = new();
        n.Normalize(0, "B").Should().Be("S1");
        n.Normalize(0, "A").Should().Be("S2");
        n.Normalize(0, "B").Should().Be("S1");
        n.Normalize(1, "B").Should().Be("S3");
        n.Normalize(0, null).Should().BeNull();
        n.Count.Should().Be(3);
    }

    private static VideoAnalysisArtifact Talk(bool labeled)
    {
        List<VideoAnalysisSegment> segs = [];
        for (int i = 0; i < 40; i++)
        {
            double start = i * 0.3;
            string? sp = labeled ? (i < 10 || i >= 30 ? "S1" : "S2") : null;
            segs.Add(new VideoAnalysisSegment("t" + i, null, start, start + 0.25, "word" + i, Speaker: sp));
        }

        VideoAnalysisMedia media = new(12, 30, 1, 1920, 1080, "h264", "aac");
        return new VideoAnalysisArtifact(2, media, [], [], segs, [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, "k", media)]);
    }

    [Fact]
    public void Phrases_break_on_speaker_change_and_carry_the_speaker()
    {
        List<SpeechPhrase> p = VideoSpeechOutline.Phrases(Talk(true));

        p.Select(x => x.Speaker).Should().Equal("S1", "S2", "S1");
        p[0].ToId.Should().Be("t9");
        p[1].FromId.Should().Be("t10");
    }

    [Fact]
    public void Outline_and_reader_show_speakers_only_when_labeled()
    {
        string labeled = VideoSpeechOutline.Build(Talk(true))!.Nodes.ToJsonString();
        labeled.Should().Contain("\"speakers\"").And.Contain("S1").And.Contain("\"turns\"");

        string unlabeled = VideoSpeechOutline.Build(Talk(false))!.Nodes.ToJsonString();
        unlabeled.Should().NotContain("speaker").And.NotContain("turns");

        JsonNode read = JsonNode.Parse(VideoFootageReader.ReadTranscript(Talk(true), "t0", "t39", null))!;
        read["phrases"]!.AsArray().Select(n => n!["speaker"]!.GetValue<string>()).Should().Equal("S1", "S2", "S1");
        VideoFootageReader.ReadTranscript(Talk(false), "t0", "t39", null).Should().NotContain("speaker");
    }
}
