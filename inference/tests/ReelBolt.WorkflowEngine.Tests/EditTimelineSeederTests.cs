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

/// <summary>
/// <see cref="EditTimelineSeeder"/> — the pure v1 → v2 mapping. The fixture manifest covers all six
/// track kinds, carries every skip case the seeder documents (no end time, an unknown storage key,
/// an out-of-range source index, whitespace dialogue, a second music bed), and ends with the
/// required self-check: T-013's validator run over the seeder's OWN output must produce zero
/// violations.
/// </summary>
public class EditTimelineSeederTests
{
    private const double Fps = 30.0;

    // ---------------------------------------------------------------------------------------
    // Settings: the frame rate is looked up, never re-derived by the consumer.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_source_with_a_camera_profile_hint_seeds_its_video_clips_converted_and_no_hint_changes_nothing()
    {
        var fixture = new Fixture();
        EditTimeline plain = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        EditTimeline none = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, inputProfileFor: _ => null);
        plain.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().OnlyContain(c => c.InputProfile == null);
        none.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().OnlyContain(c => c.InputProfile == null);

        EditTimeline hinted = EditTimelineSeeder.Seed(
            fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, inputProfileFor: a => a == fixture.SourceA ? "appleLog" : null);
        List<MediaClip> clips = hinted.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().ToList();
        clips.Where(c => c.AssetRef == fixture.SourceA && c.Name != null).Should().Contain(c => c.InputProfile == "appleLog");
        clips.Where(c => c.AssetRef == fixture.SourceB).Should().OnlyContain(c => c.InputProfile == null);
    }

    [Fact]
    public void Output_video_dimensions_and_well_known_rate_map_to_settings()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);

        timeline.Settings.Width.Should().Be(1920);
        timeline.Settings.Height.Should().Be(1080);
        timeline.Settings.Rate.Should().Be(new Rational(30, 1));
        timeline.Settings.PixelAspect.Should().Be(new Rational(1, 1));
        timeline.Settings.AspectPolicy.Should().Be(AspectPolicy.Fit);
        timeline.Settings.StartTimecodeFrames.Should().Be(0);
        timeline.Settings.SampleRate.Should().Be(EditTimelineSeeder.DefaultSampleRate);
    }

    [Theory]
    [InlineData(23.976, 24000, 1001)]
    [InlineData(24.0, 24, 1)]
    [InlineData(25.0, 25, 1)]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(30.0, 30, 1)]
    [InlineData(50.0, 50, 1)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(60.0, 60, 1)]
    public void Every_well_known_rate_maps_to_its_exact_rational(double fps, int num, int den)
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(fps: fps),
            Guid.NewGuid(),
            fixture.Resolve);

        timeline.Settings.Rate.Should().Be(new Rational(num, den));
    }

    [Fact]
    public void Unmatched_rate_falls_back_imprecisely_and_zero_or_nan_rate_falls_back_to_thirty()
    {
        var fixture = new Fixture();

        // A rate nothing matches keeps a thousandth-of-a-frame-per-second resolution. Its
        // denominator is 1000, which is how a consumer recognises the imprecise fallback.
        EditTimeline odd = EditTimelineSeeder.Seed(
            fixture.Manifest(fps: 27.5), Guid.NewGuid(), fixture.Resolve);
        odd.Settings.Rate.Should().Be(new Rational(27500, 1000));

        // A manifest whose rate is unusable is guarded rather than propagated: dividing by it
        // would produce frames of 0 or NaN for every item.
        foreach (double unusable in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            EditTimeline guarded = EditTimelineSeeder.Seed(
                fixture.Manifest(fps: unusable), Guid.NewGuid(), fixture.Resolve);

            guarded.Settings.Rate.Should().Be(new Rational(30, 1));
        }
    }

    // ---------------------------------------------------------------------------------------
    // Identity and seed.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Timeline_identity_and_seed_come_from_the_arguments_and_the_manifest()
    {
        var fixture = new Fixture();
        Guid newTimelineId = Guid.NewGuid();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), newTimelineId, fixture.Resolve);

        timeline.Id.Should().Be(newTimelineId);
        timeline.ProjectId.Should().Be(Fixture.ProjectId);
        timeline.SchemaVersion.Should().Be(2);
        timeline.Seed.Should().Be(new EditTimelineSeed(Fixture.ExecutionId, Fixture.Version));
    }

    [Fact]
    public void Track_labels_are_fixed_and_their_ids_are_deterministic()
    {
        EditTimelineSeeder.TrackLabelFor("video").Should().Be("V1");
        EditTimelineSeeder.TrackLabelFor("inserts").Should().Be("V2");
        EditTimelineSeeder.TrackLabelFor("graphics").Should().Be("V3");
        EditTimelineSeeder.TrackLabelFor("dialogue").Should().Be("S1");
        EditTimelineSeeder.TrackLabelFor("sfx").Should().Be("A2");
        EditTimelineSeeder.TrackLabelFor("music").Should().Be("A3");
        EditTimelineSeeder.TrackLabelFor("unknown").Should().BeNull();

        EditTimelineSeeder.TrackId("V1").Should().Be(EditTimelineSeeder.TrackId("V1"));
        EditTimelineSeeder.TrackId("V1").Should().NotBe(EditTimelineSeeder.TrackId("A2"));

        var fixture = new Fixture();
        EditTimeline first = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        EditTimeline second = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);

        second.Tracks.Select(t => t.Id).Should().Equal(first.Tracks.Select(t => t.Id));
    }

    // ---------------------------------------------------------------------------------------
    // Six track kinds, asserted item by item.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Video_track_maps_one_media_clip_per_cut_and_synthesizes_no_audio()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);

        timeline.Tracks.Should().HaveCount(6);
        Track v1 = Track(timeline, "V1");
        v1.Kind.Should().Be(TrackKind.Video);
        v1.Name.Should().Be("Video");

        // cut-1 (0..4s), the seam transition it declares, then cut-2 (5..8s). cut-3 has no end
        // time, cut-4 points outside Sources, cut-5's storage key resolves to nothing: all skipped.
        v1.Items.Should().HaveCount(3);

        var first = v1.Items[0].Should().BeOfType<MediaClip>().Subject;
        first.Name.Should().Be("Opening");
        first.RecordIn.Should().Be(0);
        first.RecordOut.Should().Be(120);
        first.SourceIn.Should().Be(0);
        first.AssetRef.Should().Be(fixture.SourceA);
        first.Provenance!.Origin.Should().Be(ProvenanceOrigin.Generated);
        first.Provenance.AgentName.Should().Be("VideoStoryEditor");
        first.Provenance.ProviderName.Should().Be("OpenAI");
        first.Provenance.StepResultId.Should().Be(Fixture.StepResultId);
        first.Provenance.Rationale.Should().Be("Strong hook");

        v1.Items[1].Should().BeOfType<Transition>();
        var last = v1.Items[2].Should().BeOfType<MediaClip>().Subject;
        last.Name.Should().Be("Second");
        last.RecordIn.Should().Be(150);
        last.RecordOut.Should().Be(240);
        last.SourceIn.Should().Be(60);
        last.AssetRef.Should().Be(fixture.SourceB);

        // The v1 manifest has no per-cut audio item, so v1's semantics ARE "no clip audio item" —
        // any audio item here would be fabricated data. Three items, all of them clips or the one
        // transition, is what proves nothing else was invented on this track.
        v1.Items.OfType<MediaClip>().Should().HaveCount(2);
        v1.Items.OfType<Transition>().Should().HaveCount(1);
    }

    [Fact]
    public void Transition_sits_exactly_at_the_boundary_between_its_neighbours()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track v1 = Track(timeline, "V1");

        var transition = v1.Items[1].Should().BeOfType<Transition>().Subject;
        transition.TransitionType.Should().Be("dissolve");
        transition.RecordIn.Should().Be(v1.Items[0].RecordOut);
        transition.RecordOut.Should().Be(v1.Items[2].RecordIn);
    }

    [Fact]
    public void A_butt_cut_with_a_transition_out_is_skipped_rather_than_emitted_zero_length()
    {
        var fixture = new Fixture();

        // cut-a ends exactly where cut-b begins, so the only transition the addendum's rule can
        // build would be zero-length — which T-013 rejects. It must be dropped, not stretched.
        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(buttCut: true), Guid.NewGuid(), fixture.Resolve);

        Track v1 = Track(timeline, "V1");
        v1.Items.Should().HaveCount(2);
        v1.Items.OfType<Transition>().Should().BeEmpty();
    }

    [Fact]
    public void Inserts_track_maps_quad_keyframes_in_quad_corners_order()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track v2 = Track(timeline, "V2");

        v2.Kind.Should().Be(TrackKind.Video);
        v2.Name.Should().Be("Inserts");

        // insert-r1 maps; insert-r2's asset resolves to nothing and is skipped.
        v2.Items.Should().HaveCount(1);
        var insert = v2.Items[0].Should().BeOfType<InsertClip>().Subject;
        insert.RecordIn.Should().Be(240);
        insert.RecordOut.Should().Be(300);
        insert.AssetRef.Should().Be(fixture.InsertAsset);
        insert.Provenance!.Origin.Should().Be(ProvenanceOrigin.Analyzed);
        insert.Provenance.AgentName.Should().BeNull();
        insert.Provenance.ProviderName.Should().BeNull();

        insert.Quad.Should().NotBeNull();
        insert.Quad!.Value.Should().BeNull();
        insert.Quad.Keys.Should().HaveCount(2);

        // v1 corner order is 0=TL, 1=TR, 2=BL, 3=BR; QuadCorners is TL, TR, BR, BL.
        Keyframe<QuadCorners> firstKey = insert.Quad.Keys![0];
        firstKey.Frame.Should().Be(240);
        firstKey.Interp.Should().Be(InterpKind.Linear);
        firstKey.Value.TopLeft.Should().Be(new QuadPoint(0.1, 0.1));
        firstKey.Value.TopRight.Should().Be(new QuadPoint(0.9, 0.1));
        firstKey.Value.BottomRight.Should().Be(new QuadPoint(0.9, 0.9));
        firstKey.Value.BottomLeft.Should().Be(new QuadPoint(0.1, 0.9));
        insert.Quad.Keys[1].Frame.Should().Be(300);
    }

    [Fact]
    public void Inserts_without_quad_keyframes_leave_the_quad_null()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(insertHasQuadKeyframes: false), Guid.NewGuid(), fixture.Resolve);

        var insert = Track(timeline, "V2").Items[0].Should().BeOfType<InsertClip>().Subject;
        insert.Quad.Should().BeNull();
    }

    [Fact]
    public void Graphics_track_maps_text_to_a_title_clip_and_an_asset_to_a_media_clip()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track v3 = Track(timeline, "V3");

        v3.Kind.Should().Be(TrackKind.Video);
        v3.Name.Should().Be("Graphics");
        v3.Items.Should().HaveCount(2);

        var title = v3.Items[0].Should().BeOfType<TitleClip>().Subject;
        title.Text.Should().Be("Jane Doe");
        title.RecordIn.Should().Be(0);
        title.RecordOut.Should().Be(90);
        title.FontSize.Should().Be(48);

        var asset = v3.Items[1].Should().BeOfType<MediaClip>().Subject;
        asset.AssetRef.Should().Be(fixture.LogoAsset);
        asset.RecordIn.Should().Be(360);
        asset.RecordOut.Should().Be(420);
    }

    [Fact]
    public void Dialogue_track_maps_captions_and_skips_unrenderable_text()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track s1 = Track(timeline, "S1");

        s1.Kind.Should().Be(TrackKind.Video);
        s1.Name.Should().Be("Captions");

        // seg-1 and seg-3 carry text (BuildDialogueTrack writes segment.Text); seg-2 is
        // whitespace-only and would sanitize to an empty caption, so it is skipped.
        s1.Items.Should().HaveCount(2);

        var first = s1.Items[0].Should().BeOfType<Caption>().Subject;
        first.Text.Should().Be("Hello there");
        first.RecordIn.Should().Be(30);
        first.RecordOut.Should().Be(75);
        first.Language.Should().Be("en");
        first.Provenance!.Origin.Should().Be(ProvenanceOrigin.Analyzed);
        first.Provenance.AgentName.Should().BeNull();
        first.Provenance.ProviderName.Should().BeNull();

        var second = s1.Items[1].Should().BeOfType<Caption>().Subject;
        second.Text.Should().Be("General Kenobi");
        second.RecordIn.Should().Be(120);
        second.RecordOut.Should().Be(180);
    }

    [Fact]
    public void Sfx_track_maps_one_media_clip_resolved_by_clip_name_with_the_offered_id()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track a2 = Track(timeline, "A2");

        a2.Kind.Should().Be(TrackKind.Audio);
        a2.Name.Should().Be("SFX");
        a2.Items.Should().HaveCount(1);

        var sfx = a2.Items[0].Should().BeOfType<MediaClip>().Subject;
        sfx.AssetRef.Should().Be(fixture.SfxAsset);
        sfx.RecordIn.Should().Be(150);
        sfx.RecordOut.Should().Be(165);
        sfx.Provenance!.OfferedId.Should().Be("x1");

        // The cue's own level is carried, not dropped, and a cue without automation has no keys.
        sfx.GainDb.Should().NotBeNull();
        sfx.GainDb!.Value.Should().Be(-6.0);
        sfx.GainDb.Keys.Should().BeNull();
    }

    [Fact]
    public void Music_track_maps_at_most_one_media_clip_resolved_by_track_name()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        Track a3 = Track(timeline, "A3");

        a3.Kind.Should().Be(TrackKind.Audio);
        a3.Name.Should().Be("Music");

        // The fixture declares two beds; v1 emits one, and a second MediaClip would be a different
        // capability rather than a mapping.
        a3.Items.Should().HaveCount(1);
        var music = a3.Items[0].Should().BeOfType<MediaClip>().Subject;
        music.AssetRef.Should().Be(fixture.MusicAsset);
        music.RecordIn.Should().Be(0);
        music.RecordOut.Should().Be(600);
        music.Provenance!.OfferedId.Should().Be("m1");

        // The bed's static level and its ducking envelope both survive, point for point.
        music.GainDb.Should().NotBeNull();
        music.GainDb!.Value.Should().Be(-18.0);
        music.GainDb.Keys.Should().HaveCount(3);

        // (0s, -12dB), (5s, -3dB), (10s, -12dB) at 30 fps.
        music.GainDb.Keys![0].Frame.Should().Be(0);
        music.GainDb.Keys[0].Value.Should().Be(-12.0);
        music.GainDb.Keys[0].Interp.Should().Be(InterpKind.Linear);
        music.GainDb.Keys[1].Frame.Should().Be(150);
        music.GainDb.Keys[1].Value.Should().Be(-3.0);
        music.GainDb.Keys[1].Interp.Should().Be(InterpKind.Linear);
        music.GainDb.Keys[2].Frame.Should().Be(300);
        music.GainDb.Keys[2].Value.Should().Be(-12.0);
    }

    [Fact]
    public void Audio_items_without_any_gain_keep_it_null_rather_than_defaulting_to_zero()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(sfxGainDb: null, musicBedDbfs: null, musicHasEnvelope: false),
            Guid.NewGuid(),
            fixture.Resolve);

        Track(timeline, "A2").Items[0].Should().BeOfType<MediaClip>().Subject.GainDb.Should().BeNull();
        Track(timeline, "A3").Items[0].Should().BeOfType<MediaClip>().Subject.GainDb.Should().BeNull();
    }

    [Fact]
    public void A_music_bed_with_a_static_level_and_no_envelope_keeps_only_the_level()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(musicHasEnvelope: false), Guid.NewGuid(), fixture.Resolve);

        var music = Track(timeline, "A3").Items[0].Should().BeOfType<MediaClip>().Subject;
        music.GainDb.Should().NotBeNull();
        music.GainDb!.Value.Should().Be(-18.0);
        music.GainDb.Keys.Should().BeNull();
    }

    [Fact]
    public void An_envelope_without_a_static_level_is_still_carried_in_full()
    {
        var fixture = new Fixture();

        // No BedDbfs: the automation is real data and must not be dropped for want of a static
        // level. Keyframed<double>.Value cannot be absent, so it holds the identity 0 dB, which
        // the envelope overrides from its first key onward.
        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(musicBedDbfs: null), Guid.NewGuid(), fixture.Resolve);

        var music = Track(timeline, "A3").Items[0].Should().BeOfType<MediaClip>().Subject;
        music.GainDb.Should().NotBeNull();
        music.GainDb!.Value.Should().Be(0.0);
        music.GainDb.Keys.Should().HaveCount(3);
        music.GainDb.Keys![0].Value.Should().Be(-12.0);
    }

    [Fact]
    public void Sfx_falls_back_to_the_sfx_id_when_the_clip_name_is_absent()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(sfxClipName: null), Guid.NewGuid(), fixture.Resolve);

        var sfx = Track(timeline, "A2").Items[0].Should().BeOfType<MediaClip>().Subject;
        sfx.AssetRef.Should().Be(fixture.SfxAsset);
        sfx.Provenance!.OfferedId.Should().Be("x1");
    }

    // ---------------------------------------------------------------------------------------
    // Skips, never fabricates.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Unresolvable_items_are_skipped_and_never_given_a_fabricated_asset_or_duration()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(), Guid.NewGuid(), _ => null);

        // Every asset-backed item goes away rather than acquiring an invented AssetRef. The
        // graphics title and the two captions carry their own text, so they need no asset at all
        // and survive on their own merits.
        timeline.Tracks.Select(track => track.Name).Should().Equal("Graphics", "Captions");

        List<Item> survivors = timeline.Tracks.SelectMany(track => track.Items).ToList();
        survivors.Should().HaveCount(3);
        survivors.OfType<TitleClip>().Should().HaveCount(1);
        survivors.OfType<Caption>().Should().HaveCount(2);
    }

    [Fact]
    public void Unknown_track_types_and_empty_tracks_are_dropped()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(includeUnknownTrack: true), Guid.NewGuid(), fixture.Resolve);

        timeline.Tracks.Should().HaveCount(6);
        timeline.Tracks.Should().NotContain(track => track.Name == "Chapters");
    }

    // ---------------------------------------------------------------------------------------
    // Self-check: the seeder's own output must satisfy T-013's validator.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Seeded_document_passes_the_EditTimeline_validator_with_no_violations()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve);
        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.ValidationContext());

        result.Violations.Should().BeEmpty(
            $"the seeder's own output must be a valid document, but got: {Describe(result)}");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_asset_resolving_to_another_project_is_emitted_verbatim_and_rejected_by_the_validator()
    {
        var fixture = new Fixture();

        // The seeder is pure: it cannot know an AssetRef handed to it by resolveAsset is foreign,
        // so it must neither launder nor drop it. T-013 is the guard, and it must still fire.
        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(),
            Guid.NewGuid(),
            key => key == "sources/a.mp4" ? fixture.ForeignFileRef : fixture.Resolve(key));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.ValidationContext());

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(violation =>
            violation.Message.Contains("not found in this project", StringComparison.Ordinal));
        result.Violations.Should().NotContain(violation =>
            violation.Message.Contains(Fixture.OtherProjectId.ToString(), StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------
    // Camera audio (PA-12 / RD-1, top-10 #7): a derush cut of a source with sound keeps it.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_cut_of_a_source_with_sound_gets_its_camera_audio_on_a_paired_audio_track()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, sourceHasAudio: key => key == "sources/a.mp4");

        timeline.Tracks.Should().HaveCount(7);
        timeline.Tracks[1].Id.Should().Be(EditTimelineSeeder.TrackId("A1"), "the camera audio sits right under the video it belongs to");
        Track a1 = Track(timeline, "A1");
        a1.Kind.Should().Be(TrackKind.Audio);
        a1.Name.Should().Be("Camera audio");

        // Only cut-1 (source a) has sound; cut-2 (source b) is silent and gets no companion.
        MediaClip video = Track(timeline, "V1").Items.OfType<MediaClip>().First();
        MediaClip audio = a1.Items.Should().ContainSingle().Which.Should().BeOfType<MediaClip>().Subject;
        audio.Id.Should().NotBe(video.Id);
        audio.RecordIn.Should().Be(video.RecordIn);
        audio.RecordOut.Should().Be(video.RecordOut);
        audio.SourceIn.Should().Be(video.SourceIn);
        audio.AssetRef.Should().Be(fixture.SourceA);
        audio.Name.Should().Be(video.Name);
    }

    [Fact]
    public void Every_cut_with_sound_gets_a_companion_and_the_audio_track_carries_no_transition()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, sourceHasAudio: _ => true);

        Track a1 = Track(timeline, "A1");
        a1.Items.Should().HaveCount(2).And.AllBeOfType<MediaClip>();
        a1.Items.Select(i => (i.RecordIn, i.RecordOut)).Should().Equal((0L, 120L), (150L, 240L));
    }

    [Fact]
    public void No_audio_lookup_or_only_silent_sources_add_no_camera_audio_track()
    {
        var fixture = new Fixture();

        EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, sourceHasAudio: _ => false)
            .Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"));
        EditTimelineSeeder.Seed(fixture.Manifest(), Guid.NewGuid(), fixture.Resolve)
            .Tracks.Should().NotContain(t => t.Id == EditTimelineSeeder.TrackId("A1"));
    }

    [Fact]
    public void A_seed_with_camera_audio_passes_the_EditTimeline_validator()
    {
        var fixture = new Fixture();

        EditTimeline timeline = EditTimelineSeeder.Seed(
            fixture.Manifest(), Guid.NewGuid(), fixture.Resolve, sourceHasAudio: _ => true);
        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.ValidationContext());

        result.Violations.Should().BeEmpty($"got: {Describe(result)}");
    }

    // ---------------------------------------------------------------------------------------
    // T-008: a treated crossfade seam places the incoming cut OVER the outgoing one. The
    // compile can blend two clips; the seeded document cannot (the validator forbids overlap
    // and a Transition must sit on a gap), so both seed paths must resolve it to a hard cut.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void A_fifteen_frame_crossfade_seam_seeds_as_a_hard_cut_on_both_seeders()
    {
        var fixture = new SeamFixture();
        TimelineManifest manifest = fixture.Manifest();

        // The plain seed: the production failure was tracks[0].items[4] starting at 164 while
        // items[3] ended at 179 (15 frames = 0.5 s at 30 fps).
        SeededTimeline plain = EditTimelineSeeder.SeedWithReport(
            manifest, Guid.NewGuid(), fixture.Resolve, _ => false, _ => fixture.Source);

        ValidationResult plainResult = EditTimelineValidator.Validate(plain.Document, fixture.ValidationContext());
        plainResult.Violations.Should().BeEmpty(
            $"the plain seed must resolve the seam, but got: {Describe(plainResult)}");

        Track plainVideo = Track(plain.Document, "V1");
        plainVideo.Items.Should().HaveCount(5);
        plainVideo.Items[3].RecordOut.Should().Be(179, "the outgoing cut keeps the blended window");
        plainVideo.Items[4].RecordIn.Should().Be(179, "the incoming cut starts where the outgoing one ends");
        ((MediaClip)plainVideo.Items[4]).SourceIn.Should().Be(179, "the trimmed frames shift the source in-point");
        plain.Document.Markers.Should().Contain(m => m.Name == "Softened seam" && m.Frame == 179,
            "the softened seam is reported instead of silently dropped");

        // The lineage seed: same production shape, from the compile's final manifest.
        SeededTimeline lineage = LineageSeeder.TrySeed(
            [new LineageLevel(SeamFixture.StepResultId, manifest, fixture.Config())],
            Guid.NewGuid(),
            _ => fixture.Source,
            _ => false);
        lineage.Should().NotBeNull("the 500 ms program fade makes the lineage path prefer its own decomposition");

        ValidationResult lineageResult = EditTimelineValidator.Validate(lineage.Document, fixture.ValidationContext());
        lineageResult.Violations.Should().BeEmpty(
            $"the lineage seed must resolve the seam, but got: {Describe(lineageResult)}");

        Track lineageVideo = Track(lineage.Document, "V1");
        lineageVideo.Items.Should().HaveCount(5);
        for (int i = 1; i < lineageVideo.Items.Count; i++)
        {
            lineageVideo.Items[i].RecordIn.Should().BeGreaterThanOrEqualTo(lineageVideo.Items[i - 1].RecordOut,
                "a treated seam is a hard cut, so seeded clips must not overlap");
        }

        lineageVideo.Items[4].RecordIn.Should().Be(179);
        lineage.Document.Markers.Should().Contain(m => m.Name == "Softened seam" && m.Frame == 179);
    }

    [Fact]
    public void An_overlapping_inserts_track_trims_the_incoming_insert_to_the_previous_covered_end()
    {
        var fixture = new OverlapFixture();
        TimelineTrack inserts = new("track-inserts", "inserts", "Screen Inserts", 1,
        [
            new TimelineItem(
                Id: "insert-1", AssetStorageKey: "renders/insert.png",
                TimelineStartSec: 0, TimelineEndSec: 2.0),
            // Starts 15 frames inside the previous insert: the compile blended this seam.
            new TimelineItem(
                Id: "insert-2", AssetStorageKey: "renders/insert.png",
                TimelineStartSec: 1.5, TimelineEndSec: 100 / 30.0)
        ]);

        SeededTimeline seeded = EditTimelineSeeder.SeedWithReport(
            fixture.Manifest(inserts), Guid.NewGuid(), fixture.Resolve);

        ValidationResult result = EditTimelineValidator.Validate(seeded.Document, fixture.ValidationContext());
        result.Violations.Should().BeEmpty($"got: {Describe(result)}");

        Track track = Track(seeded.Document, "V2");
        track.Items.Should().HaveCount(2).And.AllBeOfType<InsertClip>();
        track.Items[0].RecordOut.Should().Be(60);
        track.Items[1].RecordIn.Should().Be(60, "the incoming insert starts where the previous one ends");
        track.Items[1].RecordOut.Should().Be(100, "trimming the start does not shorten the incoming insert");
        seeded.Document.Markers.Should().Contain(m => m.Name == "Softened seam" && m.Frame == 60);
    }

    [Fact]
    public void An_overlapping_graphics_track_trims_the_incoming_title_to_the_previous_covered_end()
    {
        var fixture = new OverlapFixture();
        TimelineTrack graphics = new("track-graphics", "graphics", "Graphics", 2,
        [
            new TimelineItem(Id: "title-1", Text: "First", TimelineStartSec: 0, TimelineEndSec: 2.0),
            new TimelineItem(Id: "title-2", Text: "Second", TimelineStartSec: 1.5, TimelineEndSec: 100 / 30.0)
        ]);

        SeededTimeline seeded = EditTimelineSeeder.SeedWithReport(
            fixture.Manifest(graphics), Guid.NewGuid(), fixture.Resolve);

        ValidationResult result = EditTimelineValidator.Validate(seeded.Document, fixture.ValidationContext());
        result.Violations.Should().BeEmpty($"got: {Describe(result)}");

        Track track = Track(seeded.Document, "V3");
        track.Items.Should().HaveCount(2).And.AllBeOfType<TitleClip>();
        track.Items[0].RecordOut.Should().Be(60);
        track.Items[1].RecordIn.Should().Be(60, "the incoming title starts where the previous one ends");
        track.Items[1].RecordOut.Should().Be(100);
        ((TitleClip)track.Items[1]).Text.Should().Be("Second", "the trim must not lose the title's own content");
        seeded.Document.Markers.Should().Contain(m => m.Name == "Softened seam" && m.Frame == 60);
    }

    [Fact]
    public void A_trimmed_video_cut_trims_its_camera_audio_companion_with_it()
    {
        var fixture = new OverlapFixture();
        TimelineTrack video = new("track-video", "video", "Video", 0,
        [
            new TimelineItem(
                Id: "cut-1", SourceIndex: 0,
                TimelineStartSec: 0, TimelineEndSec: 2.0,
                SourceStartSec: 0, SourceEndSec: 2.0),
            new TimelineItem(
                Id: "cut-2", SourceIndex: 0,
                TimelineStartSec: 1.5, TimelineEndSec: 100 / 30.0,
                SourceStartSec: 1.5, SourceEndSec: 100 / 30.0)
        ]);

        // Both cuts come from a source the caller's probe says carries sound.
        SeededTimeline seeded = EditTimelineSeeder.SeedWithReport(
            fixture.Manifest(video), Guid.NewGuid(), fixture.Resolve, _ => true);

        ValidationResult result = EditTimelineValidator.Validate(seeded.Document, fixture.ValidationContext());
        result.Violations.Should().BeEmpty($"got: {Describe(result)}");

        Track v1 = Track(seeded.Document, "V1");
        Track a1 = Track(seeded.Document, "A1");
        v1.Items.Should().HaveCount(2);
        a1.Items.Should().HaveCount(2);

        MediaClip trimmedVideo = v1.Items[1].Should().BeOfType<MediaClip>().Subject;
        trimmedVideo.RecordIn.Should().Be(60);
        trimmedVideo.RecordOut.Should().Be(100);
        trimmedVideo.SourceIn.Should().Be(60, "45 start frames + 15 trimmed frames");

        MediaClip companion = a1.Items[1].Should().BeOfType<MediaClip>().Subject;
        companion.RecordIn.Should().Be(trimmedVideo.RecordIn, "the camera audio must track its video cut");
        companion.RecordOut.Should().Be(trimmedVideo.RecordOut);
        companion.SourceIn.Should().Be(trimmedVideo.SourceIn);
        a1.Items[0].RecordOut.Should().Be(60, "the untouched companion keeps its own range");
    }

    private static Track Track(EditTimeline timeline, string label) =>
        timeline.Tracks.Single(track => track.Id == EditTimelineSeeder.TrackId(label));

    private static string Describe(ValidationResult result) =>
        string.Join("; ", result.Violations.Select(violation => $"{violation.Path}: {violation.Message}"));

    /// <summary>
    /// T-008's per-track coverage: the same 15-frame overlap on the inserts, graphics and video
    /// tracks, where only the video track also gets a camera-audio companion.
    /// </summary>
    private sealed class OverlapFixture
    {
        public static readonly Guid ProjectId = Guid.Parse("88888888-8888-4888-8888-888888888888");

        private static readonly Guid InsertFileId = Guid.Parse("88888888-8888-4888-8888-888888888801");
        private static readonly Guid SourceFileId = Guid.Parse("88888888-8888-4888-8888-888888888802");

        public AssetRef InsertAsset => new(AssetRefKind.ProjectFile, ProjectFileId: InsertFileId);

        public AssetRef SourceAsset => new(AssetRefKind.ProjectFile, ProjectFileId: SourceFileId);

        public AssetRef? Resolve(string identifier) => identifier switch
        {
            "renders/insert.png" => InsertAsset,
            "sources/sound.mp4" => SourceAsset,
            _ => null
        };

        public TimelineManifest Manifest(params TimelineTrack[] tracks) => new(
            Version: 1,
            ProjectId: ProjectId,
            ExecutionId: Guid.NewGuid(),
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30, 4, "renders/overlap-final.mp4"),
            Sources:
            [
                new TimelineSource(0, "sources/sound.mp4", 30.0, 1920, 1080, 30, ProjectFileId: SourceFileId),
                new TimelineSource(1, "renders/insert.png", 30.0, 1920, 1080, 30, ProjectFileId: InsertFileId)
            ],
            Tracks: tracks);

        public EditTimelineValidationContext ValidationContext() => new(
            ProjectId,
            new Dictionary<AssetRef, long> { [InsertAsset] = 900, [SourceAsset] = 900 },
            new Dictionary<Guid, Guid> { [InsertFileId] = ProjectId, [SourceFileId] = ProjectId },
            new Dictionary<Guid, Guid>());
    }

    /// <summary>
    /// T-008's production shape: 30 fps, five video cuts where cut 3 ends at frame 179 and cut 4
    /// starts at frame 164 — the 15-frame (0.5 s) window the compile crossfades. The manifest is
    /// shared by both seeders, so a fix in one cannot hide a regression in the other.
    /// </summary>
    private sealed class SeamFixture
    {
        public static readonly Guid ProjectId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        public static readonly Guid StepResultId = Guid.Parse("66666666-6666-4666-8666-666666666666");

        private static readonly Guid FileId = Guid.Parse("77777777-7777-4777-8777-777777777777");

        private static readonly JsonSerializerOptions ConfigJson =
            new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public AssetRef Source => new(AssetRefKind.ProjectFile, ProjectFileId: FileId);

        public AssetRef? Resolve(string identifier) => identifier == "sources/seam.mp4" ? Source : null;

        /// <summary>500 ms program fade makes the lineage seeder prefer its own decomposition over the plain seed.</summary>
        public VideoCompileStepConfig Config() =>
            JsonSerializer.Deserialize<VideoCompileStepConfig>(
                """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"programFadeInMs":500}""",
                ConfigJson)!;

        public TimelineManifest Manifest()
        {
            static double Frames(long frames) => frames / 30.0;

            return new TimelineManifest(
                Version: 1,
                ProjectId: ProjectId,
                ExecutionId: Guid.NewGuid(),
                OutputVideo: new TimelineOutputVideo(1920, 1080, 30, Frames(240), "renders/seam-final.mp4"),
                Sources: [new TimelineSource(0, "sources/seam.mp4", 30.0, 1920, 1080, 30, ProjectFileId: FileId)],
                Tracks:
                [
                    new TimelineTrack("track-video", "video", "Video", 0,
                    [
                        Cut("cut-0", 0, 60),
                        Cut("cut-1", 60, 120),
                        Cut("cut-2", 120, 150),
                        // The outgoing cut ends at 179; the compile crossfades the next one in.
                        Cut("cut-3", 150, 179) with { TransitionOut = "Dissolve" },
                        Cut("cut-4", 164, 240)
                    ])
                ]);

            static TimelineItem Cut(string id, long inFrame, long outFrame) => new(
                Id: id,
                TimelineStartSec: Frames(inFrame),
                TimelineEndSec: Frames(outFrame),
                SourceIndex: 0,
                SourceStartSec: Frames(inFrame),
                SourceEndSec: Frames(outFrame),
                Label: id);
        }

        public EditTimelineValidationContext ValidationContext() => new(
            ProjectId,
            new Dictionary<AssetRef, long> { [Source] = 900 },
            new Dictionary<Guid, Guid> { [FileId] = ProjectId },
            new Dictionary<Guid, Guid>());
    }

    /// <summary>
    /// One hand-built manifest covering all six track kinds plus every skip case the seeder
    /// documents, with a resolver and a validator context built from the very same ids.
    /// </summary>
    private sealed class Fixture
    {
        public static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid OtherProjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid ExecutionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        public static readonly Guid StepResultId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        public const int Version = 3;

        private readonly Guid _sourceAFile = Guid.Parse("a0000000-0000-0000-0000-000000000001");
        private readonly Guid _sourceBFile = Guid.Parse("a0000000-0000-0000-0000-000000000002");
        private readonly Guid _insertStepResult = Guid.Parse("a0000000-0000-0000-0000-000000000003");
        private readonly Guid _logoStepResult = Guid.Parse("a0000000-0000-0000-0000-000000000004");
        private readonly Guid _sfxFile = Guid.Parse("a0000000-0000-0000-0000-000000000005");
        private readonly Guid _musicFile = Guid.Parse("a0000000-0000-0000-0000-000000000006");
        private readonly Guid _music2File = Guid.Parse("a0000000-0000-0000-0000-000000000007");
        private readonly Guid _foreignFile = Guid.Parse("a0000000-0000-0000-0000-000000000008");

        public AssetRef SourceA => new(AssetRefKind.ProjectFile, ProjectFileId: _sourceAFile);

        public AssetRef SourceB => new(AssetRefKind.ProjectFile, ProjectFileId: _sourceBFile);

        public AssetRef InsertAsset => new(AssetRefKind.StepResult, StepResultId: _insertStepResult);

        public AssetRef LogoAsset => new(AssetRefKind.StepResult, StepResultId: _logoStepResult);

        public AssetRef SfxAsset => new(AssetRefKind.ProjectFile, ProjectFileId: _sfxFile);

        public AssetRef MusicAsset => new(AssetRefKind.ProjectFile, ProjectFileId: _musicFile);

        public AssetRef Music2Asset => new(AssetRefKind.ProjectFile, ProjectFileId: _music2File);

        public AssetRef ForeignFileRef => new(AssetRefKind.ProjectFile, ProjectFileId: _foreignFile);

        /// <summary>
        /// The resolver's stand-in for T-015's project-scoped lookup. It answers by the identifier
        /// exactly as the manifest carries it: storage keys for cuts and inserts, project-file
        /// names for audio.
        /// </summary>
        public AssetRef? Resolve(string identifier) => identifier switch
        {
            "sources/a.mp4" => SourceA,
            "sources/b.mp4" => SourceB,
            "renders/insert.png" => InsertAsset,
            "renders/logo.png" => LogoAsset,
            "whoosh.wav" => SfxAsset,
            "x1" => SfxAsset,
            "bed.mp3" => MusicAsset,
            "bed2.mp3" => Music2Asset,
            _ => null
        };

        public TimelineManifest Manifest(
            double fps = Fps,
            bool buttCut = false,
            bool includeUnknownTrack = false,
            bool insertHasQuadKeyframes = true,
            string? sfxClipName = "whoosh.wav",
            double? sfxGainDb = -6.0,
            double? musicBedDbfs = -18.0,
            bool musicHasEnvelope = true)
        {
            var video = new List<TimelineItem>
            {
                new(
                    Id: "cut-1",
                    SourceIndex: 0,
                    SourceStartSec: 0,
                    SourceEndSec: 4,
                    TimelineStartSec: 0,
                    TimelineEndSec: 4,
                    Label: "Opening",
                    AgentRationale: "Strong hook",
                    TransitionOut: "dissolve",
                    AgentName: "VideoStoryEditor",
                    ProviderName: "OpenAI",
                    StepResultId: StepResultId),
                new(
                    Id: "cut-2",
                    SourceIndex: 1,
                    SourceStartSec: 2,
                    SourceEndSec: 5,
                    TimelineStartSec: buttCut ? 4 : 5,
                    TimelineEndSec: buttCut ? 7 : 8,
                    Label: "Second"),
                // Skipped: no end time. An absent end is never invented.
                new(Id: "cut-3", SourceIndex: 0, SourceStartSec: 0, TimelineStartSec: 9),
                // Skipped: SourceIndex 2 exists but its storage key resolves to nothing.
                new(Id: "cut-4", SourceIndex: 2, TimelineStartSec: 10, TimelineEndSec: 12),
                // Skipped: SourceIndex is outside Sources.
                new(Id: "cut-5", SourceIndex: 9, TimelineStartSec: 13, TimelineEndSec: 14)
            };

            TimelineTrack[] tracks =
            {
                new TimelineTrack("track-video", "video", "Video", 0, video),
                new TimelineTrack("track-inserts", "inserts", "Screen Inserts", 1, new List<TimelineItem>
                {
                    new(
                        Id: "insert-r1",
                        RegionId: "r1",
                        AssetStorageKey: "renders/insert.png",
                        TimelineStartSec: 8,
                        TimelineEndSec: 10,
                        QuadKeyframes: insertHasQuadKeyframes ? DefaultQuadKeyframes() : null),
                    // Skipped: the asset does not resolve.
                    new(
                        Id: "insert-r2",
                        RegionId: "r2",
                        AssetStorageKey: "renders/missing.png",
                        TimelineStartSec: 11,
                        TimelineEndSec: 12)
                }),
                new TimelineTrack("track-graphics", "graphics", "Graphics", 2, new List<TimelineItem>
                {
                    new(
                        Id: "overlay-p1",
                        PlacementId: "p1",
                        Text: "Jane Doe",
                        TimelineStartSec: 0,
                        TimelineEndSec: 3),
                    new(
                        Id: "overlay-p2",
                        PlacementId: "p2",
                        AssetStorageKey: "renders/logo.png",
                        TimelineStartSec: 12,
                        TimelineEndSec: 14)
                }),
                new TimelineTrack("track-dialogue", "dialogue", "Dialogue", 3, new List<TimelineItem>
                {
                    new(Id: "seg-1", Text: "Hello there", TimelineStartSec: 1, TimelineEndSec: 2.5),
                    // Skipped: whitespace-only text would sanitize to an empty caption.
                    new(Id: "seg-2", Text: "   ", TimelineStartSec: 3, TimelineEndSec: 3.5),
                    new(Id: "seg-3", Text: "General Kenobi", TimelineStartSec: 4, TimelineEndSec: 6)
                }),
                new TimelineTrack("track-sfx", "sfx", "Sound Effects", 4, new List<TimelineItem>
                {
                    new(
                        Id: "sfx-x1",
                        SfxId: "x1",
                        ClipName: sfxClipName,
                        TimelineStartSec: 5,
                        TimelineEndSec: 5.5,
                        GainDb: sfxGainDb,
                        Timing: "OnCut")
                }),
                new TimelineTrack("track-music", "music", "Music", 5, new List<TimelineItem>
                {
                    new(
                        Id: "music-m1",
                        TrackId: "m1",
                        TrackName: "bed.mp3",
                        TimelineStartSec: 0,
                        TimelineEndSec: 20,
                        BedDbfs: musicBedDbfs,
                        DuckingEnvelope: musicHasEnvelope
                            ? new[]
                            {
                                new TimelineDuckingPoint(0.0, -12.0),
                                new TimelineDuckingPoint(5.0, -3.0),
                                new TimelineDuckingPoint(10.0, -12.0)
                            }
                            : null),
                    // Skipped: at most one bed is mapped.
                    new(
                        Id: "music-m2",
                        TrackId: "m2",
                        TrackName: "bed2.mp3",
                        TimelineStartSec: 20,
                        TimelineEndSec: 30)
                })
            };

            if (includeUnknownTrack)
            {
                tracks = tracks.Append(
                    new TimelineTrack("track-chapters", "chapters", "Chapters", 6, new List<TimelineItem>
                    {
                        new(Id: "chapter-1", Label: "Intro", TimelineStartSec: 0, TimelineEndSec: 1)
                    })).ToArray();
            }

            return new TimelineManifest(
                Version: Version,
                ProjectId: ProjectId,
                ExecutionId: ExecutionId,
                OutputVideo: new TimelineOutputVideo(
                    Width: 1920,
                    Height: 1080,
                    Fps: fps,
                    DurationSec: 30.0,
                    StorageKey: "renders/final.mp4"),
                Sources: new[]
                {
                    new TimelineSource(0, "sources/a.mp4", 100.0, 1920, 1080, 30.0),
                    new TimelineSource(1, "sources/b.mp4", 50.0, 1920, 1080, 30.0),
                    new TimelineSource(2, "sources/unknown.mp4", 10.0, 1920, 1080, 30.0)
                },
                Tracks: tracks);
        }

        /// <summary>
        /// The lookups T-015 will build for real: durations for every ref the fixture resolves,
        /// and ownership for every id it names — including one id that belongs to a DIFFERENT
        /// project, so the cross-tenant rule has something hostile to bite on.
        /// </summary>
        public EditTimelineValidationContext ValidationContext()
        {
            var durations = new Dictionary<AssetRef, long>
            {
                [SourceA] = Frames(100.0),
                [SourceB] = Frames(50.0),
                [InsertAsset] = 300,
                [LogoAsset] = 300,
                [SfxAsset] = 300,
                [MusicAsset] = 1200,
                [Music2Asset] = 1200,
                [ForeignFileRef] = Frames(100.0)
            };

            var fileOwners = new Dictionary<Guid, Guid>
            {
                [_sourceAFile] = ProjectId,
                [_sourceBFile] = ProjectId,
                [_sfxFile] = ProjectId,
                [_musicFile] = ProjectId,
                [_music2File] = ProjectId,
                [_foreignFile] = OtherProjectId
            };

            var stepResultOwners = new Dictionary<Guid, Guid>
            {
                [_insertStepResult] = ProjectId,
                [_logoStepResult] = ProjectId,
                // The seeder copies the manifest's own StepResultId onto Provenance; this project owns it.
                [StepResultId] = ProjectId
            };

            return new EditTimelineValidationContext(ProjectId, durations, fileOwners, stepResultOwners);
        }

        /// <summary>Duration in frames, rounded UP so a legitimate clip never trips the overrun rule.</summary>
        private static long Frames(double seconds) => (long)Math.Ceiling(seconds * Fps);

        private static IReadOnlyList<VideoInsertQuadKeyframe> DefaultQuadKeyframes() => new[]
        {
            new VideoInsertQuadKeyframe(8.0, 0.1, 0.1, 0.9, 0.1, 0.1, 0.9, 0.9, 0.9),
            new VideoInsertQuadKeyframe(10.0, 0.2, 0.2, 0.8, 0.2, 0.2, 0.8, 0.8, 0.8)
        };
    }
}
