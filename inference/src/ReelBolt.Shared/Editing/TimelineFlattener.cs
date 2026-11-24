using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// P8: turns a document with <see cref="CompoundClip"/>s and <see cref="MulticamClip"/>s into one
/// with neither, so the renderer (<c>SegmentPlanner</c>/<c>AudioMixBuilder</c>) and the preview
/// (its TS mirror, <c>lib/editing/structure/flatten.ts</c>) never learn that nesting exists.
/// </summary>
/// <remarks>
/// <para>
/// A compound clip is replaced by its sequence's tracks: each nested track becomes a NEW track
/// inserted right after the host track (so it composites above the host's other items and below
/// every later track), its items shifted by <c>recordIn − sourceIn</c> and clipped to the clip's
/// span (a media clip's <c>sourceIn</c> advances by the head trim at its rate; a transition that
/// would be cut is dropped, since half a transition is not a transition). Nested clips keep their
/// own flags; a disabled compound, a disabled host track or a sequence that does not exist expands
/// to nothing. Nesting is followed to <see cref="EditTimelineValidator.MaxNestingDepth"/>; deeper
/// content, and a sequence already on the chain, is dropped rather than looped over.
/// </para>
/// <para>
/// A multicam clip becomes one media clip per angle run — from <c>recordIn</c> to the first cut
/// with the active angle, then from cut to cut — each reading
/// <c>offsetFrames + sourceIn + (runStart − recordIn)</c> of its angle's source.
/// </para>
/// <para>
/// Ids are DERIVED, never fresh: an expanded track or item id is a stable hash of the host clip's
/// id and the nested id, so two compound clips playing the same sequence yield distinct ids (the
/// planner dedupes audio by clip id) and the same document always flattens to the same ids (the
/// recipe hash and the region cache depend on it). Pure: the input document is never modified.
/// </para>
/// </remarks>
public static class TimelineFlattener
{
    /// <summary>Whether <paramref name="document"/> carries anything <see cref="Flatten"/> would change.</summary>
    public static bool NeedsFlattening(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Tracks.Any(t => t?.Items is { } items && items.Any(i => i is CompoundClip or MulticamClip));
    }

    /// <summary>
    /// The flattened document — the same instance when there is nothing to flatten, otherwise a
    /// copy whose tracks hold neither compound nor multicam clips. <c>Sequences</c> and <c>Buses</c>
    /// are carried unchanged.
    /// </summary>
    public static EditTimeline Flatten(EditTimeline document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!NeedsFlattening(document)) return document;

        Dictionary<Guid, EditSequence> sequences = [];
        foreach (EditSequence? sequence in document.Sequences ?? [])
        {
            if (sequence is not null) sequences.TryAdd(sequence.Id, sequence);
        }

        List<Track> tracks = FlattenTracks(document.Tracks, sequences, 1, []);
        return document with { Tracks = tracks };
    }

    /// <summary>The media clips a multicam clip plays, one per angle run, in record order.</summary>
    public static IReadOnlyList<MediaClip> ExpandMulticam(MulticamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<MediaClip> result = [];
        if (clip.Angles is not { Count: > 0 } angles) return result;
        Dictionary<Guid, MulticamAngle> byId = [];
        foreach (MulticamAngle? angle in angles)
        {
            if (angle is not null) byId.TryAdd(angle.Id, angle);
        }

        if (!byId.TryGetValue(clip.ActiveAngleId, out MulticamAngle? active)) active = byId.Values.FirstOrDefault();
        if (active is null) return result;

        List<(long At, MulticamAngle Angle)> runs = [(clip.RecordIn, active)];
        foreach (MulticamCut? cut in (clip.Cuts ?? []).OrderBy(c => c?.AtFrame ?? long.MinValue))
        {
            if (cut is null || cut.AtFrame <= clip.RecordIn || cut.AtFrame >= clip.RecordOut) continue;
            if (!byId.TryGetValue(cut.AngleId, out MulticamAngle? angle)) continue;
            if (cut.AtFrame <= runs[^1].At) continue;
            runs.Add((cut.AtFrame, angle));
        }

        for (int i = 0; i < runs.Count; i++)
        {
            long start = runs[i].At;
            long end = i + 1 < runs.Count ? runs[i + 1].At : clip.RecordOut;
            if (end <= start) continue;
            MulticamAngle angle = runs[i].Angle;
            long sourceIn = Math.Max(0, angle.OffsetFrames + clip.SourceIn + (start - clip.RecordIn));
            result.Add(new MediaClip(
                DeriveId(clip.Id, $"run{i}"),
                $"{clip.Name} · {angle.Name}",
                start,
                end,
                angle.AssetRef,
                sourceIn,
                1.0,
                clip.Enabled,
                clip.Provenance,
                clip.GainDb,
                InputProfile: angle.InputProfile));
        }

        return result;
    }

    /// <summary>
    /// R08.2: the program-audio track of a multicam clip, or null when it plays no sound (<c>none</c>, no angles).
    /// <c>audioSource</c> absent or <c>program</c> is the first angle; an angle id is that angle; the clip's
    /// whole span plays that one source.
    /// </summary>
    public static Track? ExpandMulticamAudio(MulticamClip clip, Track host)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(host);
        if (clip.AudioSource == "none" || clip.Angles is not { Count: > 0 } angles) return null;
        MulticamAngle? source = null;
        if (clip.AudioSource is not (null or "program") && Guid.TryParse(clip.AudioSource, out Guid angleId))
        {
            source = angles.FirstOrDefault(a => a is not null && a.Id == angleId);
        }

        source ??= angles.FirstOrDefault(a => a is not null);
        if (source is null || clip.RecordOut <= clip.RecordIn) return null;
        MediaClip audio = new(
            DeriveId(clip.Id, "audio0"),
            $"{clip.Name} · {source.Name}",
            clip.RecordIn,
            clip.RecordOut,
            source.AssetRef,
            Math.Max(0, source.OffsetFrames + clip.SourceIn),
            1.0,
            clip.Enabled,
            clip.Provenance,
            clip.GainDb,
            InputProfile: source.InputProfile);
        return new Track(DeriveId(clip.Id, "audio"), TrackKind.Audio, $"{clip.Name} / audio", null, host.Height, false, host.Enabled, host.Mute, false, null, [audio]);
    }

    /// <summary>A stable id for an expanded element: a hash of its scope (the host clip) and its own id or role.</summary>
    public static Guid DeriveId(Guid scope, string local)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(scope.ToString("N") + ":" + local));
        return new Guid(hash);
    }

    private static List<Track> FlattenTracks(IReadOnlyList<Track> tracks, Dictionary<Guid, EditSequence> sequences, int depth, HashSet<Guid> onPath)
    {
        List<Track> output = [];
        foreach (Track? track in tracks)
        {
            if (track is null) continue;
            List<Item> items = [];
            List<Track> nested = [];
            foreach (Item? item in track.Items ?? [])
            {
                switch (item)
                {
                    case null:
                        continue;
                    case MulticamClip multicam:
                        if (!multicam.Enabled) continue;
                        items.AddRange(ExpandMulticam(multicam));
                        if (ExpandMulticamAudio(multicam, track) is { } audioTrack) nested.Add(audioTrack);
                        break;
                    case CompoundClip compound:
                        if (!compound.Enabled || !track.Enabled) continue;
                        if (!sequences.TryGetValue(compound.SequenceId, out EditSequence? sequence)) continue;
                        if (depth >= EditTimelineValidator.MaxNestingDepth || onPath.Contains(compound.SequenceId)) continue;
                        onPath.Add(compound.SequenceId);
                        List<Track> inner = FlattenTracks(sequence.Tracks ?? [], sequences, depth + 1, onPath);
                        onPath.Remove(compound.SequenceId);
                        foreach (Track innerTrack in inner)
                        {
                            Track? placed = PlaceNestedTrack(innerTrack, compound, track);
                            if (placed is not null) nested.Add(placed);
                        }
                        break;
                    default:
                        items.Add(item);
                        break;
                }
            }

            output.Add(track with { Items = items });
            output.AddRange(nested);
        }

        return output;
    }

    /// <summary>The nested track laid over the compound's span: shifted, clipped, ids derived.</summary>
    private static Track? PlaceNestedTrack(Track nested, CompoundClip compound, Track host)
    {
        long shift = compound.RecordIn - compound.SourceIn;
        List<Item> items = [];
        foreach (Item item in nested.Items)
        {
            Item? placed = Respan(item, shift, compound.RecordIn, compound.RecordOut, compound.Id);
            if (placed is not null) items.Add(placed);
        }

        if (items.Count == 0) return null;

        bool mute = nested.Mute || (host.Kind == TrackKind.Audio && host.Mute);
        return nested with
        {
            Id = DeriveId(compound.Id, nested.Id.ToString("N")),
            Name = $"{compound.Name} / {nested.Name}",
            Mute = mute,
            Items = items
        };
    }

    /// <summary>The item translated by <paramref name="shift"/> and clipped to <c>[start, end)</c>, or null when nothing of it survives.</summary>
    private static Item? Respan(Item item, long shift, long start, long end, Guid scope)
    {
        long recordIn = item.RecordIn + shift;
        long recordOut = item.RecordOut + shift;
        long newIn = Math.Max(recordIn, start);
        long newOut = Math.Min(recordOut, end);
        if (newOut <= newIn) return null;
        long headTrim = newIn - recordIn;
        long tailTrim = recordOut - newOut;
        Guid id = DeriveId(scope, item.Id.ToString("N"));

        switch (item)
        {
            case MediaClip clip:
                {
                    double rate = clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
                    long sourceIn = clip.Hold
                        ? clip.SourceIn
                        : clip.Reverse
                            ? clip.SourceIn + (long)Math.Round(tailTrim * rate)
                            : clip.SourceIn + (long)Math.Round(headTrim * rate);
                    return new MediaClip(id, clip.Name, newIn, newOut, clip.AssetRef, Math.Max(0, sourceIn), clip.PlaybackRate, clip.Enabled, clip.Provenance,
                        ShiftKeys(clip.GainDb, shift), ShiftTransform(clip.Transform, shift), clip.Crop, clip.Reverse, clip.Hold,
                        Math.Max(0, Math.Min(clip.FadeInFrames - headTrim, newOut - newIn)), Math.Max(0, Math.Min(clip.FadeOutFrames - tailTrim, newOut - newIn)),
                        clip.Grade, clip.Mask, clip.Keyer, clip.Effects, clip.Stabilize, clip.Reframe, clip.RetimeQuality, clip.CornerPin, clip.ChannelMap, clip.LinkId, clip.RegionEffects, clip.InputProfile, clip.SourceRotation, clip.BlendMode);
                }
            case TitleClip title:
                return new TitleClip(id, title.Name, newIn, newOut, title.Text, title.FontSize, title.FontFamily, title.Color, title.Enabled, title.Provenance, title.Template, title.Position, ShiftTransform(title.Transform, shift));
            case Caption caption:
                return new Caption(id, caption.Name, newIn, newOut, caption.Text, caption.Language, caption.Enabled, caption.Provenance);
            case GeneratorClip generator:
                return new GeneratorClip(id, generator.Name, newIn, newOut, generator.GeneratorType, generator.GeneratorConfig, generator.Enabled, generator.Provenance);
            case InsertClip insert:
                return new InsertClip(id, insert.Name, newIn, newOut, insert.AssetRef, ShiftKeys(insert.Quad, shift), insert.Enabled, insert.Provenance, insert.Matte);
            case AdjustmentClip adjustment:
                return new AdjustmentClip(id, adjustment.Name, newIn, newOut, adjustment.Provenance, adjustment.Grade, adjustment.Effects, adjustment.Enabled);
            case Transition transition:
                // Half a transition is not a transition: a clipped one is dropped and the cut stays hard.
                if (headTrim != 0 || tailTrim != 0) return null;
                return new Transition(id, transition.Name, newIn, newOut, transition.TransitionType, transition.Easing, transition.Enabled, transition.Provenance);
            default:
                // A compound or multicam clip never reaches here (the nested tracks were flattened first).
                return null;
        }
    }

    private static Keyframed<T>? ShiftKeys<T>(Keyframed<T>? channel, long shift)
    {
        if (channel?.Keys is not { Count: > 0 } keys || shift == 0) return channel;
        return new Keyframed<T>(channel.Value, keys.Select(k => new Keyframe<T>(k.Frame + shift, k.Value, k.Interp)).ToList());
    }

    private static ClipTransform? ShiftTransform(ClipTransform? transform, long shift)
    {
        if (transform is null || shift == 0) return transform;
        return new ClipTransform(
            ShiftKeys(transform.PositionX, shift),
            ShiftKeys(transform.PositionY, shift),
            ShiftKeys(transform.Scale, shift),
            ShiftKeys(transform.Rotation, shift),
            ShiftKeys(transform.Opacity, shift),
            ShiftKeys(transform.ScaleY, shift));
    }
}
