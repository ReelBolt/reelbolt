namespace ReelBolt.WorkflowEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

/// <summary>
/// A film with a cold open, cutaways and an end card: the timeline manifest and the reviewer's [TIMELINE FACTS]
/// must describe the whole program (prod run 4f8aff78 was scored on "17.65 s, three of five clips never appear").
/// </summary>
public class GeneratedSegmentTimelineFactsTests
{
    private const double ColdOpenSec = 6.0;
    private const double EndCardSec = 7.92;
    private const double BodySec = 17.65;
    private const double FilmSec = BodySec + ColdOpenSec + EndCardSec; // 31.57

    private static (TimelineManifest Manifest, string Output) Compile(bool withClips)
    {
        var artifact = new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(60, 30, 1, 1920, 1080),
            Shots: [],
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 10, 0, 10, 0, 300),
            new(20, 27.65, 20, 27.65, 600, 830),
        };
        OutputTimeline timeline = OutputTimeline.Build(spans, [new SeamPlan(0, SeamTreatment.HardCut, 0, 0, null, 0.0, "Rule")]);
        List<TimelineGeneratedClip>? clips = withClips
            ?
            [
                new("clip-open", "coldopen", 0, ColdOpenSec),
                new("clip-cut-1", "cutaway", ColdOpenSec + 2, ColdOpenSec + 3.5),
                new("clip-cut-2", "cutaway", ColdOpenSec + 6, ColdOpenSec + 7.5),
                new("clip-cut-3", "cutaway", ColdOpenSec + 12, ColdOpenSec + 13.5),
                new("clip-end", "endcard", ColdOpenSec + BodySec, FilmSec),
            ]
            : null;

        TimelineManifest manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(), Guid.NewGuid(), artifact, spans, timeline, "projects/p/out.mp4", 1920, 1080, 30,
            generatedClips: clips, finalOutputSec: withClips ? FilmSec : null);
        string film = (withClips ? FilmSec : BodySec).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string output = "{\"status\":\"completed\",\"outputDurationSec\":" + film + ",\"length\":{\"requestedSec\":31.57,\"actualSec\":31.57,\"met\":true}}";
        return (manifest, output);
    }

    [Fact]
    public void A_film_with_generated_segments_is_described_as_the_whole_program()
    {
        var (manifest, output) = Compile(withClips: true);

        manifest.OutputVideo.DurationSec.Should().Be(31.57);
        TimelineItem[] video = manifest.Tracks.Single(t => t.Type == "video").Items.OrderBy(i => i.TimelineStartSec).ToArray();
        video.Where(i => i.SourceIndex is not null).Select(i => i.TimelineStartSec).Should().Equal(ColdOpenSec, ColdOpenSec + 10);
        video.Should().Contain(i => i.Label!.Contains("clip-open") && i.TimelineStartSec == 0 && i.TimelineEndSec == ColdOpenSec);
        video.Should().Contain(i => i.Label!.Contains("clip-cut-2") && i.TimelineStartSec == 12 && i.TimelineEndSec == 13.5);
        video.Should().Contain(i => i.Label!.Contains("clip-end") && i.TimelineEndSec == 31.57);

        string block = ReviewTimelineFacts.Render(manifest, output, null, 3)!;
        block.Should().Contain("31.57 s long").And.NotContain("17.65");
        foreach (string id in new[] { "clip-open", "clip-cut-1", "clip-cut-2", "clip-cut-3", "clip-end" })
            block.Should().Contain(id);
        block.Should().NotContain("\"met\":false");
    }

    [Fact]
    public void A_film_without_generated_segments_is_unchanged()
    {
        var (manifest, _) = Compile(withClips: false);

        manifest.OutputVideo.DurationSec.Should().Be(BodySec);
        manifest.Tracks.Single(t => t.Type == "video").Items.Should().OnlyContain(i => i.SourceIndex != null);
    }
}

/// <summary>The seeder, the audio facts and the length check read the same generated-segment film.</summary>
public class GeneratedSegmentConsumersTests
{
    private static List<TimelineItem> FilmItems() =>
    [
        new(Id: "generated-1", TimelineStartSec: 0, TimelineEndSec: 3, Label: "generated cold open clip a (placed)"),
        new(Id: "cut-1", TimelineStartSec: 3, TimelineEndSec: 10, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 7),
        new(Id: "generated-2", TimelineStartSec: 5, TimelineEndSec: 6, Label: "generated cutaway clip b (placed)"),
        new(Id: "generated-3", TimelineStartSec: 10, TimelineEndSec: 12, Label: "generated end card clip c (placed)"),
    ];

    [Fact]
    public void The_seeder_skips_generated_clips_without_listing_them_as_unplaced()
    {
        Guid file = Guid.NewGuid();
        var manifest = new TimelineManifest(1, Guid.NewGuid(), Guid.NewGuid(),
            new TimelineOutputVideo(1920, 1080, 30, 12, "renders/final.mp4"),
            [new TimelineSource(0, "sources/a.mp4", 30, 1920, 1080, 30, ProjectFileId: file)],
            [new TimelineTrack("track-video", "video", "Video", 0, FilmItems())]);

        SeededTimeline seeded = ReelBolt.Shared.Editing.EditTimelineSeeder.SeedWithReport(
            manifest, Guid.NewGuid(), id => id == "sources/a.mp4" ? new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: file) : null);

        seeded.Unplaced.Should().BeEmpty();
        seeded.Document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().ContainSingle()
            .Which.RecordIn.Should().Be(90, "the body keeps its place after the cold open");
    }

    [Fact]
    public void The_audio_facts_count_where_a_cutaway_hands_the_picture_back_as_a_cut()
    {
        // Quiet until 6 s, loud after: the jump is at the cutaway's end, which is a cut but not the start of any item.
        var lines = new System.Text.StringBuilder();
        for (int i = 1; i <= 100; i++)
        {
            double t = i / 10.0;
            lines.Append("[Parsed_ebur128_0 @ 0x1] t: ").Append(t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
                .Append("  TARGET:-23 LUFS    M:").Append(t > 6 ? "-10.0" : "-30.0").Append(" S:-20.0     I: -20.0 LUFS       LRA:   0.0 LU\n");
        }

        var manifest = new TimelineManifest(1, Guid.Empty, Guid.Empty,
            new TimelineOutputVideo(1920, 1080, 30, 12, "f.mp4"), [],
            [new TimelineTrack("track-video", "video", "Video", 0, FilmItems())]);

        string block = ReviewAudioFacts.Render(ReviewAudioFacts.Parse(lines.ToString()), manifest)!;

        block.Should().Contain("Loudness jumps").And.Contain("6 s (+");
    }

    [Fact]
    public void A_correct_film_length_gets_no_length_finding()
    {
        // The 31.57 s film with generated segments against a 32 s request: measured, inside the 15 % tolerance, no cap, no sentence.
        var outcome = new LengthCheckOutcome(32, 31.57, null);
        outcome.WithinTolerance.Should().BeTrue();
        outcome.Capped.Should().BeFalse();

        string review = LengthCheckReport.AttachToOutput("""{"score":9}""", outcome, null);
        LengthCheckReport.ApplyRecordedCap(9, review, null).Should().Be(9);
        LengthCheckReport.WithBriefBlock("brief", review, null).Should().Be("brief");

        // The old body-only 17.65 s was a miss: the red state this guards against.
        new LengthCheckOutcome(32, 17.65, null).Capped.Should().BeTrue();
    }
}
