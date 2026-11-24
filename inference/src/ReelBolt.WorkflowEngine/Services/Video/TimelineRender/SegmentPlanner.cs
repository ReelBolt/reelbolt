using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The two clips a <see cref="Transition"/> blends (P5): <paramref name="From"/> ends where the
/// transition starts and continues past its own <c>recordOut</c> for the transition's length;
/// <paramref name="To"/> starts where the transition ends and is pulled back for the same length.
/// P7: each side's colour-group grade rides along, so a leg renders exactly as its clip does outside
/// the transition (RD-6).
/// </summary>
public sealed record TransitionNeighbours(MediaClip From, MediaClip To, GradeStack? FromGroupGrade = null, GradeStack? ToGroupGrade = null);

/// <summary>One video item active across a whole region, with its stacking position.</summary>
/// <param name="TrackIndex">Index of the item's track in the document (lower = further back).</param>
/// <param name="Item">The item. <see cref="MediaClip"/>, <see cref="TitleClip"/>, <see cref="Caption"/>,
/// <see cref="GeneratorClip"/> and a <see cref="Transition"/> with two media neighbours are rendered;
/// every other kind is planned as inert and reported in <see cref="RenderPlan.SkippedItems"/>.</param>
/// <param name="Transition">For a <see cref="Transition"/> item: the clips it blends.</param>
/// <param name="GroupGrade">P7: for a <see cref="MediaClip"/> in a colour group, the group's grade stack (applied before the clip's own).</param>
public sealed record RenderLayer(int TrackIndex, Item Item, TransitionNeighbours? Transition = null, GradeStack? GroupGrade = null);

/// <summary>
/// A span of the program during which the set of active video items — and every parameter of each
/// — is constant. The unit of rendering and of caching.
/// </summary>
/// <param name="StartFrame">Inclusive, timeline frames.</param>
/// <param name="EndFrame">Exclusive, timeline frames.</param>
/// <param name="Layers">Bottom-to-top.</param>
public sealed record RenderRegion(long StartFrame, long EndFrame, IReadOnlyList<RenderLayer> Layers)
{
    public long DurationFrames => EndFrame - StartFrame;
}

/// <summary>A linear gain ramp over absolute timeline frames (P5): 0 at <paramref name="StartFrame"/>
/// rising to 1 at <paramref name="EndFrame"/> for a fade-in, the reverse for a fade-out.</summary>
public sealed record AudioFade(long StartFrame, long EndFrame);

/// <summary>An audio clip placed on the program, as the mix pass needs it.</summary>
/// <param name="TrackIndex">Index of the clip's track.</param>
/// <param name="Clip">The clip.</param>
/// <param name="StartFrame">Clipped to the render range, inclusive.</param>
/// <param name="EndFrame">Clipped to the render range, exclusive.</param>
/// <param name="SourceInFrames">The clip's source position at <paramref name="StartFrame"/>, in
/// SOURCE frames at the timeline rate (playback rate applied). For a reversed clip this is the
/// LOWEST source frame of the span; the segment is read forwards and reversed.</param>
/// <param name="FadeIn">P5: the clip's own fade-in, or the fade-in half of an audio transition.</param>
/// <param name="FadeOut">P5: the clip's own fade-out, or the fade-out half of an audio transition.</param>
public sealed record AudioClipPlan(
    int TrackIndex,
    MediaClip Clip,
    long StartFrame,
    long EndFrame,
    double SourceInFrames,
    AudioFade? FadeIn = null,
    AudioFade? FadeOut = null);

/// <summary>An item the planner saw but this wave does not render.</summary>
/// <remarks>
/// A render that leaves something out still completes, but never silently (RD-5): every skip has
/// a plain <see cref="Message"/>, which the step output carries once, in <c>skippedItems</c>, for
/// the Deliver panel to show (never copied into <c>warnings</c> as well).
/// </remarks>
public sealed record SkippedItem(Guid ItemId, string Type, string Reason)
{
    /// <summary>The reason as one plain sentence a user can read (no ids, no codes).</summary>
    public string Message => (Type, Reason) switch
    {
        (nameof(InsertClip), "insert_incomplete") => "A screen insert without a picture or a corner position was left out.",
        (nameof(InsertClip), "not_rendered_in_this_wave") => "A screen insert is not part of rendered videos yet, so it was left out.",
        (_, "not_rendered_in_this_wave") => "An item of a kind rendered videos do not support yet was left out.",
        (_, "transition_neighbours_not_media") => "A transition was left out because it does not sit between two enabled clips.",
        (_, "insufficient_handles") => "A sound transition was played as a cut because the next clip has no extra media before its start to blend with.",
        (_, "audio_track_item_not_a_media_clip") => "An item on an audio track that is not a sound clip was left out.",
        _ => "An item was left out of this render.",
    };
}

/// <summary>The whole render plan for one document and range.</summary>
public sealed record RenderPlan(
    long RangeStartFrame,
    long RangeEndFrame,
    IReadOnlyList<RenderRegion> Regions,
    IReadOnlyList<AudioClipPlan> AudioClips,
    IReadOnlyList<SkippedItem> SkippedItems)
{
    public long DurationFrames => RangeEndFrame - RangeStartFrame;
}

/// <summary>
/// Splits a document's program into regions of constant composition and lists the audio clips the
/// mix pass places. Pure: no I/O, no ffmpeg, deterministic for a given document and range.
/// </summary>
/// <remarks>
/// <para>
/// Boundaries are every enabled video item's <c>recordIn</c>/<c>recordOut</c> (on enabled tracks)
/// clipped to the range, plus the range ends. Between two consecutive boundaries the active layer
/// set cannot change. Keyframed values (P5) do NOT add boundaries: a region's filtergraph carries
/// the whole curve as a time expression relative to the region start, and the recipe hash covers
/// "every parameter within the region" by relativising keyframes the same way (see
/// <see cref="RegionRecipeHasher"/>), so a keyframe edit changes exactly the regions it touches.
/// </para>
/// <para>
/// Track order is stacking order: tracks earlier in <c>Tracks</c> are further back, matching the P3
/// compositor's <c>activeLayersAt</c>, so the preview and the render agree on what is on top.
/// A disabled track or item contributes nothing. A <c>Transition</c> is rendered when both its
/// neighbours on the track are enabled <c>MediaClip</c>s (the layer carries them as
/// <see cref="TransitionNeighbours"/>); any other transition, an <c>InsertClip</c> without media or a quad, and the three
/// structural stubs are reported as skipped rather than guessed.
/// </para>
/// <para>
/// Audio (P5): a clip's own fades become <see cref="AudioFade"/> ramps; a transition between two
/// audio clips extends the first through the transition span with a fade-out and pulls the second
/// back with a fade-in (an equal-length linear crossfade), when the second clip has the source
/// handle for it — otherwise the cut stays hard and the transition is reported as skipped. A held
/// clip is silent and plans no audio.
/// </para>
/// </remarks>
public static class SegmentPlanner
{
    public static RenderPlan Plan(EditTimeline document, TimelineRenderRange? range)
    {
        ArgumentNullException.ThrowIfNull(document);

        long end = TimelineEndFrame(document);
        long rangeStart = 0;
        long rangeEnd = end;
        if (range is not null)
        {
            rangeStart = Math.Max(0, range.InFrame);
            rangeEnd = Math.Min(Math.Max(rangeStart + 1, range.OutFrame), Math.Max(end, rangeStart + 1));
        }

        if (rangeEnd <= rangeStart)
        {
            // An empty document renders one black frame rather than nothing: a zero-length output is
            // not a file a player can open, and the step reports the duration honestly.
            rangeEnd = rangeStart + 1;
        }

        List<SkippedItem> skipped = [];
        SortedSet<long> boundaries = [rangeStart, rangeEnd];
        List<(int TrackIndex, Item Item, TransitionNeighbours? Transition)> videoItems = [];
        List<AudioClipPlan> audio = [];

        bool anySolo = document.Tracks.Any(t => t.Kind == TrackKind.Audio && t.Solo && t.Enabled);

        // P7: a clip's colour group grade rides on its layer (a clip is in at most one group).
        Dictionary<Guid, GradeStack> groupGrades = [];
        foreach (ColorGroup group in document.ColorGroups ?? [])
        {
            if (group.Grade is null) continue;
            foreach (Guid clipId in group.ClipIds ?? []) groupGrades.TryAdd(clipId, group.Grade);
        }

        for (int trackIndex = 0; trackIndex < document.Tracks.Count; trackIndex++)
        {
            Track track = document.Tracks[trackIndex];
            if (!track.Enabled) continue;

            IReadOnlyList<Item> items = track.Items;
            for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                Item item = items[itemIndex];
                if (item.RecordOut <= rangeStart || item.RecordIn >= rangeEnd) continue;

                if (track.Kind == TrackKind.Video)
                {
                    switch (item)
                    {
                        case MediaClip { Enabled: false }:
                        case TitleClip { Enabled: false }:
                        case Caption { Enabled: false }:
                        case GeneratorClip { Enabled: false }:
                        case Transition { Enabled: false }:
                        case AdjustmentClip { Enabled: false }:
                        case InsertClip { Enabled: false }:
                            continue;
                        case MediaClip:
                        case TitleClip:
                        case Caption:
                        case GeneratorClip:
                        case AdjustmentClip:
                            videoItems.Add((trackIndex, item, null));
                            boundaries.Add(Math.Max(rangeStart, item.RecordIn));
                            boundaries.Add(Math.Min(rangeEnd, item.RecordOut));
                            break;
                        case InsertClip insert when InsertLayerBuilder.IsRenderable(insert):
                            videoItems.Add((trackIndex, item, null));
                            boundaries.Add(Math.Max(rangeStart, item.RecordIn));
                            boundaries.Add(Math.Min(rangeEnd, item.RecordOut));
                            break;
                        case InsertClip:
                            skipped.Add(new SkippedItem(item.Id, item.GetType().Name, "insert_incomplete"));
                            break;
                        case Transition transition:
                            {
                                TransitionNeighbours? neighbours = Neighbours(items, itemIndex);
                                if (neighbours is null)
                                {
                                    skipped.Add(new SkippedItem(item.Id, item.GetType().Name, "transition_neighbours_not_media"));
                                    break;
                                }

                                videoItems.Add((trackIndex, transition, neighbours));
                                boundaries.Add(Math.Max(rangeStart, item.RecordIn));
                                boundaries.Add(Math.Min(rangeEnd, item.RecordOut));
                                break;
                            }
                        default:
                            skipped.Add(new SkippedItem(item.Id, item.GetType().Name, "not_rendered_in_this_wave"));
                            break;
                    }
                }
                else
                {
                    if (item is Transition audioTransition)
                    {
                        PlanAudioTransition(items, itemIndex, audioTransition, trackIndex, track, anySolo, rangeStart, rangeEnd, audio, skipped);
                        continue;
                    }

                    if (item is not MediaClip clip)
                    {
                        skipped.Add(new SkippedItem(item.Id, item.GetType().Name, "audio_track_item_not_a_media_clip"));
                        continue;
                    }

                    if (!clip.Enabled || track.Mute || (anySolo && !track.Solo)) continue;
                    if (clip.Hold) continue; // a freeze frame is silent

                    // A clip already placed by the transition that precedes it is not placed twice.
                    if (audio.Any(a => a.Clip.Id == clip.Id)) continue;

                    long start = Math.Max(rangeStart, clip.RecordIn);
                    long stop = Math.Min(rangeEnd, clip.RecordOut);
                    audio.Add(new AudioClipPlan(trackIndex, clip, start, stop, SourceAt(clip, start, stop), ClipFadeIn(clip), ClipFadeOut(clip)));
                }
            }
        }

        long[] cuts = boundaries.ToArray();
        List<RenderRegion> regions = new(cuts.Length - 1);
        for (int i = 0; i + 1 < cuts.Length; i++)
        {
            long start = cuts[i];
            long stop = cuts[i + 1];
            List<RenderLayer> layers = videoItems
                .Where(v => v.Item.RecordIn <= start && v.Item.RecordOut >= stop)
                .OrderBy(v => v.TrackIndex)
                .ThenBy(v => v.Item.RecordIn)
                .Select(v => new RenderLayer(
                    v.TrackIndex,
                    v.Item,
                    v.Transition is { } n ? n with { FromGroupGrade = groupGrades.GetValueOrDefault(n.From.Id), ToGroupGrade = groupGrades.GetValueOrDefault(n.To.Id) } : null,
                    v.Item is MediaClip member && groupGrades.TryGetValue(member.Id, out GradeStack? groupGrade) ? groupGrade : null))
                .ToList();
            regions.Add(new RenderRegion(start, stop, layers));
        }

        return new RenderPlan(rangeStart, rangeEnd, regions, audio, skipped);
    }

    /// <summary>
    /// The last <c>recordOut</c> of anything that actually plays (0 when nothing does): items on
    /// enabled tracks, themselves enabled, and on an audio track only when the track is heard (not
    /// muted, not silenced by another track's solo). A hidden track, a disabled clip or a muted
    /// audio track running past the real content no longer stretches the delivery with black and
    /// silence (RD-9).
    /// </summary>
    public static long TimelineEndFrame(EditTimeline document)
    {
        bool anySolo = document.Tracks.Any(t => t.Kind == TrackKind.Audio && t.Solo && t.Enabled);
        return document.Tracks
            .Where(t => t.Enabled && (t.Kind != TrackKind.Audio || (!t.Mute && (!anySolo || t.Solo))))
            .SelectMany(t => t.Items)
            .Where(IsEnabled)
            .Select(i => i.RecordOut)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>Whether the item is switched on (kinds without the flag always are).</summary>
    internal static bool IsEnabled(Item item) => item switch
    {
        MediaClip c => c.Enabled,
        TitleClip c => c.Enabled,
        Caption c => c.Enabled,
        GeneratorClip c => c.Enabled,
        InsertClip c => c.Enabled,
        Transition c => c.Enabled,
        AdjustmentClip c => c.Enabled,
        CompoundClip c => c.Enabled,
        MulticamClip c => c.Enabled,
        _ => true,
    };

    /// <summary>
    /// The source position (in source frames at the timeline rate) a clip reads at timeline
    /// <paramref name="frame"/>: forwards from <c>sourceIn</c>, or — reversed — counted back from the
    /// clip's end so <c>sourceIn</c> stays the lowest frame touched. A held clip always reads <c>sourceIn</c>.
    /// </summary>
    public static double SourceFrameAt(MediaClip clip, long frame)
    {
        double rate = EffectiveRate(clip);
        if (clip.Hold) return clip.SourceIn;
        if (clip.Reverse) return clip.SourceIn + (clip.RecordOut - frame) * rate;
        return clip.SourceIn + (frame - clip.RecordIn) * rate;
    }

    /// <summary>
    /// The playback rate the render uses for a clip — ONE clamp, to
    /// [<see cref="RegionFilterGraphBuilder.MinPlaybackRate"/>, <see cref="RegionFilterGraphBuilder.MaxPlaybackRate"/>],
    /// shared by the picture, the sound and every source position so they cannot drift apart (RD-7).
    /// A missing or non-positive rate is 1.
    /// </summary>
    public static double EffectiveRate(MediaClip clip) =>
        Math.Clamp(clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0, RegionFilterGraphBuilder.MinPlaybackRate, RegionFilterGraphBuilder.MaxPlaybackRate);

    /// <summary>The LOWEST source frame a span <c>[start, stop)</c> of the clip touches — where the mix starts reading.</summary>
    private static double SourceAt(MediaClip clip, long start, long stop) =>
        clip.Reverse ? SourceFrameAt(clip, stop) : SourceFrameAt(clip, start);

    private static AudioFade? ClipFadeIn(MediaClip clip) =>
        clip.FadeInFrames > 0 ? new AudioFade(clip.RecordIn, clip.RecordIn + clip.FadeInFrames) : null;

    private static AudioFade? ClipFadeOut(MediaClip clip) =>
        clip.FadeOutFrames > 0 ? new AudioFade(clip.RecordOut - clip.FadeOutFrames, clip.RecordOut) : null;

    /// <summary>A copy of <paramref name="clip"/> on a different span (the record is init-less, so it is rebuilt through its constructor).</summary>
    private static MediaClip Respan(MediaClip clip, long recordIn, long recordOut, long sourceIn) =>
        new(clip.Id, clip.Name, recordIn, recordOut, clip.AssetRef, sourceIn, clip.PlaybackRate, clip.Enabled, clip.Provenance,
            clip.GainDb, clip.Transform, clip.Crop, clip.Reverse, clip.Hold, clip.FadeInFrames, clip.FadeOutFrames,
            SourceRotation: clip.SourceRotation);

    /// <summary>The two enabled media clips around <c>items[index]</c>, or null when either side is something else.</summary>
    private static TransitionNeighbours? Neighbours(IReadOnlyList<Item> items, int index)
    {
        if (index == 0 || index + 1 >= items.Count) return null;
        if (items[index - 1] is not MediaClip { Enabled: true } from) return null;
        if (items[index + 1] is not MediaClip { Enabled: true } to) return null;
        if (from.RecordOut != items[index].RecordIn || to.RecordIn != items[index].RecordOut) return null;
        return new TransitionNeighbours(from, to);
    }

    private static void PlanAudioTransition(
        IReadOnlyList<Item> items,
        int index,
        Transition transition,
        int trackIndex,
        Track track,
        bool anySolo,
        long rangeStart,
        long rangeEnd,
        List<AudioClipPlan> audio,
        List<SkippedItem> skipped)
    {
        if (!transition.Enabled) return;
        if (track.Mute || (anySolo && !track.Solo)) return;

        TransitionNeighbours? neighbours = Neighbours(items, index);
        if (neighbours is null || neighbours.From.Hold || neighbours.To.Hold)
        {
            skipped.Add(new SkippedItem(transition.Id, nameof(Transition), "transition_neighbours_not_media"));
            return;
        }

        long span = transition.RecordOut - transition.RecordIn;
        MediaClip outgoing = neighbours.From;
        MediaClip incoming = neighbours.To;
        double toRate = EffectiveRate(incoming);

        // The incoming clip is pulled back by the transition span; it needs that much source before
        // its own sourceIn. Without it the cut stays hard rather than reading before frame zero.
        if (!incoming.Reverse && incoming.SourceIn - span * toRate < 0)
        {
            skipped.Add(new SkippedItem(transition.Id, nameof(Transition), "insufficient_handles"));
            return;
        }

        AudioFade ramp = new(transition.RecordIn, transition.RecordOut);

        // Outgoing clip: its ordinary span extended to the end of the transition, fading out over it.
        long fromStart = Math.Max(rangeStart, outgoing.RecordIn);
        long fromStop = Math.Min(rangeEnd, transition.RecordOut);
        if (fromStop > fromStart)
        {
            MediaClip extendedFrom = Respan(outgoing, outgoing.RecordIn, transition.RecordOut, outgoing.SourceIn);
            audio.RemoveAll(a => a.Clip.Id == outgoing.Id);
            audio.Add(new AudioClipPlan(trackIndex, extendedFrom, fromStart, fromStop, SourceAt(extendedFrom, fromStart, fromStop), ClipFadeIn(outgoing), ramp));
        }

        // Incoming clip: pulled back to the start of the transition, fading in over it.
        long toStart = Math.Max(rangeStart, transition.RecordIn);
        long toStop = Math.Min(rangeEnd, incoming.RecordOut);
        if (toStop > toStart)
        {
            long pulled = (long)Math.Round(span * toRate);
            MediaClip extendedTo = incoming.Reverse
                ? Respan(incoming, transition.RecordIn, incoming.RecordOut, incoming.SourceIn)
                : Respan(incoming, transition.RecordIn, incoming.RecordOut, Math.Max(0, incoming.SourceIn - pulled));
            audio.Add(new AudioClipPlan(trackIndex, extendedTo, toStart, toStop, SourceAt(extendedTo, toStart, toStop), ramp, ClipFadeOut(incoming)));
        }
    }
}
