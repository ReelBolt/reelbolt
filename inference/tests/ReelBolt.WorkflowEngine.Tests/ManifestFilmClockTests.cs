namespace ReelBolt.WorkflowEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

/// <summary>
/// QA-DYN-41: every track of the manifest is on ONE clock, the finished film's. The compile lays graphics, censor
/// boxes, inserts, captions and sound effects on the body and then prepends a cold open, so each plays the cold
/// open's length later in the film; the consumers (review facts, editor seed, lineage seed) read that clock as is.
/// </summary>
public class ManifestFilmClockTests
{
    private const double ColdOpenSec = 6.0;
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static TimelineManifest Film(bool coldOpen)
    {
        var artifact = new VideoAnalysisArtifact(
            Version: 1, Media: new VideoAnalysisMedia(60, 30, 1, 1920, 1080), Shots: [], SilenceSpans: [],
            Segments: [new VideoAnalysisSegment("seg-1", null, 7.0, 8.0, "hello there")], Words: [], OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, 10, 0, 10, 0, 300) };
        OutputTimeline timeline = OutputTimeline.Build(spans, []);
        var overlay = new ResolvedOverlay("p1", "Title", "Hi", "", 2000, "Normal", 2, 4, new VideoAnalysisRect(0, 0.33, 1, 0.33), "Light");
        var censor = new ResolvedCensorRegion("r0", "plate", 3, 5,
            [new InsertQuadFrame(0, 100, 100, 300, 100, 100, 160, 300, 160)], 30, 1);
        var sfx = new ResolvedSfxCue("s1", "a1", Guid.NewGuid(), "whoosh", "/tmp/w.wav", 1.0, 0.5, 1, 0.1);
        List<TimelineGeneratedClip>? clips = coldOpen ? [new("clip-open", "coldopen", 0, ColdOpenSec)] : null;
        return TimelineManifestBuilder.Build(
            Guid.NewGuid(), Guid.NewGuid(), artifact, spans, timeline, "projects/p/out.mp4", 1920, 1080, 30,
            overlays: [overlay], sfx: [sfx], censors: [censor], generatedClips: clips,
            finalOutputSec: coldOpen ? 10 + ColdOpenSec : null);
    }

    private static TimelineItem Item(TimelineManifest m, string type) => m.Tracks.Single(t => t.Type == type).Items.Single();

    [Fact]
    public void Every_track_of_a_film_with_a_cold_open_is_in_film_time()
    {
        TimelineManifest shifted = Film(coldOpen: true);
        TimelineManifest plain = Film(coldOpen: false);

        foreach (string type in new[] { "graphics", "censor", "sfx", "dialogue" })
        {
            TimelineItem a = Item(plain, type), b = Item(shifted, type);
            b.TimelineStartSec.Should().BeApproximately(a.TimelineStartSec + ColdOpenSec, 1e-9, type);
            b.TimelineEndSec.Should().BeApproximately(a.TimelineEndSec!.Value + ColdOpenSec, 1e-9, type);
        }

        Item(shifted, "censor").OutputQuadKeyframes!.First().TimeSec.Should().BeApproximately(Item(plain, "censor").OutputQuadKeyframes!.First().TimeSec + ColdOpenSec, 1e-9);
    }

    [Fact]
    public void The_review_facts_place_a_graphic_and_a_cue_at_the_film_time()
    {
        string block = ReviewTimelineFacts.Render(Film(coldOpen: true), "{\"status\":\"completed\",\"outputDurationSec\":16}", null, 3)!;
        block.Should().Contain("- [9–11 s] \"plate\"").And.Contain("- [8–10 s] text \"Hi\"").And.Contain("- [7–7.5 s] cue \"s1\"").And.Contain("- [13–14 s]");
    }

    [Fact]
    public void The_editor_seed_places_the_graphic_and_the_cue_at_the_film_time()
    {
        TimelineManifest m = AddSource(Film(coldOpen: true));
        SeededTimeline seeded = EditTimelineSeeder.SeedWithReport(m, Guid.NewGuid(), id => id == "sources/a.mp4" ? new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: FileId) : null);

        var items = seeded.Document.Tracks.SelectMany(t => t.Items).ToList();
        long clipIn = items.OfType<MediaClip>().Where(c => c.AssetRef.ProjectFileId == FileId && c.RecordIn >= 180).OrderBy(c => c.RecordIn).First().RecordIn; // the film's censor box (9-11 s) now splits the body clip in the plain seed
        clipIn.Should().Be(180, "the body starts after the cold open");
        items.Where(i => i is not MediaClip { AssetRef.ProjectFileId: var f } || f != FileId).Select(i => i.RecordIn).Should().Contain(240, "the graphic (body 2 s) plays at film 8 s");
    }

    [Fact]
    public void A_censored_stretch_stays_censored_in_the_lineage_seed_when_the_film_has_a_cold_open()
    {
        TimelineManifest m = AddSource(Film(coldOpen: true));
        var level = new LineageLevel(Guid.NewGuid(), m, new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1));

        SeededTimeline? seeded = LineageSeeder.TrySeed([level], Guid.NewGuid(),
            s => s.ProjectFileId is { } id ? new AssetRef(AssetRefKind.ProjectFile, id) : null, _ => true, _ => null, null);

        seeded.Should().NotBeNull();
        // The censor box is on film 9-11 s = frames 270-330: nothing there may replay the ORIGINAL source (it would show the object unobscured).
        // QA-DYN-60: the clause "c.SourceIn != null" was always true (SourceIn is a long). The window test alone is the assertion, and a
        // guard shows it is not vacuous: the original source does play elsewhere in the seed (the same filter finds it outside the window).
        var originalClips = seeded!.Document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Where(c => c.AssetRef.ProjectFileId == FileId).ToList();
        originalClips.Should().NotBeEmpty("the seed replays the original source where it is not censored, so the filter below can match");
        originalClips.Where(c => c.RecordIn < 330 && c.RecordOut > 270).Should().BeEmpty();
    }

    private static TimelineManifest AddSource(TimelineManifest m) =>
        m with { Sources = [new TimelineSource(0, "sources/a.mp4", 60, 1920, 1080, 30, ProjectFileId: FileId)] };
}
