#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Privacy invariant: a censored stretch never reaches a seeded editor timeline uncensored.</summary>
public class EditTimelineSeederCensorTests
{
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static TimelineManifest Manifest(IReadOnlyList<TimelineItem> cuts, params TimelineItem[] censors)
    {
        var tracks = new List<TimelineTrack> { new("v", "video", "Video", 0, cuts) };
        if (censors.Length > 0)
        {
            tracks.Add(new TimelineTrack("track-censor", "censor", "Censored", 1, censors));
        }

        return new TimelineManifest(
            1, Guid.NewGuid(), Guid.NewGuid(),
            new TimelineOutputVideo(1920, 1080, 30, 10, "out.mp4"),
            [new TimelineSource(0, "src.mp4", 60, 1920, 1080, 30)],
            tracks);
    }

    private static TimelineItem Cut(string id, double t0, double t1, double s0) =>
        new(Id: id, TimelineStartSec: t0, TimelineEndSec: t1, SourceIndex: 0, SourceStartSec: s0, SourceEndSec: s0 + (t1 - t0), Label: id);

    private static TimelineItem Censor(string id, double t0, double t1) =>
        new(Id: id, RegionId: "r", Label: "face", TimelineStartSec: t0, TimelineEndSec: t1);

    private static SeededTimeline Seed(TimelineManifest manifest) =>
        EditTimelineSeeder.SeedWithReport(
            manifest, Guid.NewGuid(),
            _ => new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: FileId),
            _ => true);

    private static List<MediaClip> Picture(EditTimeline doc) =>
        doc.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();

    [Fact]
    public void A_censor_window_leaves_a_hole_in_the_picture_widened_outward_and_is_reported()
    {
        SeededTimeline seeded = Seed(Manifest([Cut("c", 0, 10, 0)], Censor("censor-r-0", 4.0, 5.0)));

        List<MediaClip> picture = Picture(seeded.Document);
        picture.Select(c => (c.RecordIn, c.RecordOut, c.SourceIn)).Should().Equal((0L, 120L, 0L), (151L, 300L, 151L));
        seeded.Unplaced.Should().ContainSingle().Which.Should().Be(
            new UnplacedItem("censor", "censor-r-0", "face", UnplacedReasons.CensoredStretchWithheld));
        // Camera audio is untouched.
        seeded.Document.Tracks.Single(t => t.Name == "Camera audio").Items.OfType<MediaClip>()
            .Single().Should().Match<MediaClip>(a => a.RecordIn == 0 && a.RecordOut == 300);
    }

    [Fact]
    public void A_zero_length_censor_still_withholds_a_frame_and_one_covering_a_whole_clip_removes_it()
    {
        SeededTimeline seeded = Seed(Manifest(
            [Cut("a", 0, 4, 0), Cut("b", 4, 6, 4), Cut("c", 6, 10, 6)],
            Censor("z", 2.0, 2.0), Censor("whole-b", 3.9, 6.1)));

        List<MediaClip> picture = Picture(seeded.Document);
        long[] windowStarts = [60, 117];
        long[] windowEnds = [61, 184];
        foreach (MediaClip clip in picture)
        {
            for (int i = 0; i < windowStarts.Length; i++)
            {
                (clip.RecordIn < windowEnds[i] && clip.RecordOut > windowStarts[i]).Should().BeFalse(
                    $"clip {clip.RecordIn}-{clip.RecordOut} must not overlap the censored window {windowStarts[i]}-{windowEnds[i]}");
            }
        }

        seeded.Unplaced.Where(u => u.Reason == UnplacedReasons.CensoredStretchWithheld).Should().HaveCount(2);
    }

    [Fact]
    public void A_manifest_without_a_censor_track_seeds_exactly_as_before()
    {
        SeededTimeline seeded = Seed(Manifest([Cut("c", 0, 10, 0)]));

        Picture(seeded.Document).Should().ContainSingle().Which.RecordOut.Should().Be(300);
        seeded.Unplaced.Should().BeEmpty();
    }

    private static readonly Guid OverlayId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static SeededTimeline SeedWithOverlays(TimelineManifest manifest) =>
        EditTimelineSeeder.SeedWithReport(
            manifest, Guid.NewGuid(),
            key => new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: key == "overlay.png" ? OverlayId : FileId),
            _ => true);

    [Fact]
    public void No_transition_sits_over_or_touches_a_censored_window_even_when_the_clip_before_it_has_a_transition_out()
    {
        TimelineItem a = Cut("a", 0, 4, 0) with { TransitionOut = "dissolve" };
        TimelineItem b = Cut("b", 4, 6, 4) with { TransitionOut = "dissolve" };
        // The window removes b wholly: a's dissolve would otherwise span the hole and blend footage back in at its edge.
        SeededTimeline seeded = Seed(Manifest([a, b, Cut("c", 6, 10, 6)], Censor("k", 4.0, 6.0)));

        EditTimeline doc = seeded.Document;
        List<Item> v1 = doc.Tracks.Single(t => t.Name == "Video").Items.ToList();
        foreach (Transition t in v1.OfType<Transition>())
        {
            (t.RecordIn < 181 && t.RecordOut > 120).Should().BeFalse($"transition {t.RecordIn}-{t.RecordOut} overlaps the censored window 120-181");
        }

        v1.OfType<MediaClip>().Should().OnlyContain(c => c.RecordOut <= 120 || c.RecordIn >= 181);
    }

    private static List<MediaClip> GraphicClips(string key)
    {
        TimelineTrack graphics = new("g", "graphics", "Graphics", 2, [new TimelineItem(Id: "g", TimelineStartSec: 3, TimelineEndSec: 7, AssetStorageKey: key)]);
        TimelineManifest plain = Manifest([Cut("c", 0, 10, 0)], Censor("k", 4.0, 5.0));
        TimelineManifest manifest = plain with { Tracks = [.. plain.Tracks!, graphics] };
        return SeedWithOverlays(manifest).Document.Tracks.Single(t => t.Name == "Graphics").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
    }

    [Fact]
    public void A_graphic_that_plays_original_source_pixels_is_withheld_over_a_censored_window()
    {
        GraphicClips("src.mp4").Select(c => (c.RecordIn, c.RecordOut)).Should().Equal((90L, 120L), (151L, 210L));
    }

    [Fact]
    public void A_rendered_overlay_graphic_is_not_original_footage_and_stays_whole()
    {
        GraphicClips("overlay.png").Should().ContainSingle().Which.Should().Match<MediaClip>(c => c.RecordIn == 90 && c.RecordOut == 210);
    }

    [Fact]
    public void An_insert_that_plays_original_source_over_a_censored_window_is_dropped_and_listed()
    {
        TimelineTrack inserts = new("i", "inserts", "Inserts", 2, [new TimelineItem(Id: "ins", TimelineStartSec: 3, TimelineEndSec: 7, AssetStorageKey: "src.mp4")]);
        TimelineManifest plain = Manifest([Cut("c", 0, 10, 0)], Censor("k", 4.0, 5.0));

        SeededTimeline seeded = SeedWithOverlays(plain with { Tracks = [.. plain.Tracks!, inserts] });

        seeded.Document.Tracks.SelectMany(t => t.Items).OfType<InsertClip>().Should().BeEmpty();
        seeded.Unplaced.Should().Contain(u => u.Track == "V2" && u.ItemId == "ins" && u.Reason == UnplacedReasons.CensoredStretchWithheld);
    }
}
