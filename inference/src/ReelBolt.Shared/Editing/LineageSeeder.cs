using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Editing;

/// <summary>One segmentation track of a VideoAnalyze artifact, as the lineage seeder needs it.</summary>
public sealed record LineageSegmentationTrack(string Id, int SourceIndex, string Prompt, SegmentationTrackFacts? Facts = null);

/// <summary>
/// One compile of the chain: its result, manifest, config and the analysis its region effects resolve against.
/// <see cref="Config"/> null = the step's config could not be read (every span through it falls back).
/// </summary>
public sealed record LineageLevel(
    Guid StepResultId,
    TimelineManifest Manifest,
    VideoCompileStepConfig? Config,
    Guid? AnalysisStepResultId = null,
    IReadOnlyList<LineageSegmentationTrack>? AnalysisTracks = null);

/// <summary>Constants the engine uses for the effects the seeder maps; pinned against the engine by a test.</summary>
public static class LineageCalibration
{
    /// <summary>RegionEffect Blur: sigma = this x the frame's short side x strength (RegionEffectFilterBuilder.BlurSigmaFraction).</summary>
    public const double RegionBlurSigmaFraction = 0.015;

    /// <summary>RegionEffect Pixelate: block = this x short side x strength (PixelateBlockFraction).</summary>
    public const double RegionPixelateBlockFraction = 0.02;

    /// <summary>Censor Blur: sigma = this x frame WIDTH, floor 4 (CensorFilterBuilder.EffectFilter).</summary>
    public const double CensorBlurSigmaFraction = 0.015;

    /// <summary>Canvas fill Blur: gblur sigma in canvas pixels, resolution independent (VideoCompileStepExecutor, ReframeFilterBuilder).</summary>
    public const double CanvasFillBlurSigma = 40;

    /// <summary>Canvas fill dimming: <c>eq=brightness=-0.08</c>, expressed as a grade offset (master) on the fill layer.</summary>
    public const double CanvasFillBrightness = -0.08;

    /// <summary>The editor blur effect: sigma = amount x this (GradeFilterBuilder.Effects).</summary>
    public const double EditorBlurSigmaPerAmount = 20;

    /// <summary>The editor pixelate effect: block = amount x this (GradeFilterBuilder.Effects).</summary>
    public const double EditorPixelateBlockPerAmount = 64;
}

/// <summary>
/// T-102: seeds a timeline from the run's FINAL compile but decomposed onto the ORIGINAL sources: the cuts of
/// every chained compile replayed (time), each level's source-to-canvas fit composed (space, through
/// <see cref="CanvasFitGeometry"/>, the engine's own arithmetic), the Pad + blur canvas fill as a lower blurred
/// layer, and region effects / censor as an upper segmentation-masked layer. A span that cannot be represented
/// falls back to the compile's own output for THAT span only, flagged on the clip and on a timeline marker.
/// </summary>
public static class LineageSeeder
{
    public const string FillTrackLabel = "VF";
    public const string MainTrackLabel = "V1";
    public const string RegionTrackLabelPrefix = "VR";

    /// <summary>Frame tolerance when judging "1:1 time" and coverage.</summary>
    private const double ToleranceSec = 0.05;

    private sealed record Chain(LineageLevel Level, int SourceIndex);

    /// <summary>One run of the final timeline: a window of an original file (Origin set) or a span only the compile output can show (Fallback set).</summary>
    private sealed record Piece(TimelineSource? Origin, double OriginStartSec, double OutStartSec, double DurationSec, IReadOnlyList<Chain> OuterToInner, string? Fallback);

    private readonly record struct Bad(double Start, double End, string Reason);

    /// <summary>
    /// How every <see cref="VideoCompileStepConfig"/> member is treated, so a member added later cannot be silently ignored
    /// (a drift-guard test enumerates the record and fails on an unlisted or stale name).
    /// mapped = carried into the document; fallback = the affected span plays the compile output with a marker; marked = a
    /// timeline marker says it differs; irrelevant = no effect on what the seeded picture or sound shows.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ConfigCoverage = LineageConfigCoverage.Table;

    /// <summary>
    /// Seeds from <paramref name="levels"/> (the first is the seeded result). Returns null when the lineage adds
    /// nothing over the plain manifest seeding (a single compile with an identity fit and no effects), so the
    /// caller keeps today's behaviour byte for byte.
    /// </summary>
    public static SeededTimeline? TrySeed(
        IReadOnlyList<LineageLevel> levels,
        Guid newTimelineId,
        Func<TimelineSource, AssetRef?> resolveOrigin,
        Func<string, bool> hasAudio,
        Func<string, AssetRef?>? resolveKey = null,
        Func<string, bool?>? altersPixelsOnDecode = null,
        Func<AssetRef, string?>? inputProfileFor = null)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0 || levels[0].Config is null)
        {
            return null;
        }

        LineageLevel final = levels[0];
        TimelineManifest fm = final.Manifest;
        Dictionary<Guid, LineageLevel> byResult = levels.GroupBy(l => l.StepResultId).ToDictionary(g => g.Key, g => g.First());
        double fps = EditTimelineSeeder.EffectiveFps(fm.OutputVideo.Fps);
        int cw = Math.Max(2, fm.OutputVideo.Width);
        int ch = Math.Max(2, fm.OutputVideo.Height);

        List<TimelineItem> cuts = (fm.Tracks ?? []).Where(IsVideo).SelectMany(t => t.Items ?? []).OrderBy(i => i.TimelineStartSec).ToList();
        if (cuts.Count == 0)
        {
            return null;
        }

        AssetRef finalRender = new(AssetRefKind.StepResult, StepResultId: final.StepResultId);
        bool finalHasAudio = fm.OutputVideo.StorageKey is { } outKey && hasAudio(outKey);

        // The program fade is the WHOLE final picture fading; it can be carried onto the seeded layers only when no
        // level has a region effect or a censor, because those layers would keep their own opacity through the fade.
        bool noRegionOrCensor = levels.All(l =>
            (l.Config?.RegionEffects?.Count ?? 0) == 0 &&
            !(l.Manifest.Tracks ?? []).Any(t => string.Equals(t.Type, "censor", StringComparison.Ordinal) && (t.Items?.Count ?? 0) > 0));

        List<Item> fill = [];
        List<Item> main = [];
        List<Item> audio = [];
        Dictionary<string, (string Name, List<Item> Items)> regionTracks = new(StringComparer.Ordinal);
        List<Marker> markers = [];
        List<UnplacedItem> unplaced = [];
        bool nontrivial = false;
        int depth = 1;

        // 1. Every video cut of the final compile becomes pieces: original-file windows and/or fallback spans.
        List<(TimelineItem Cut, Piece Piece)> pieces = [];
        foreach (TimelineItem cut in cuts)
        {
            double t0 = cut.TimelineStartSec;
            double t1 = cut.TimelineEndSec ?? (t0 + ((cut.SourceEndSec ?? 0) - (cut.SourceStartSec ?? 0)));
            if (EditTimelineSeeder.Frames(t1, fps) <= EditTimelineSeeder.Frames(t0, fps))
            {
                // A zero-length cut has no frames to show: nothing is lost, but say so.
                unplaced.Add(new UnplacedItem("video", cut.Id ?? string.Empty, cut.Label, "lineage_empty_cut"));
                continue;
            }

            if (cut.SourceIndex is not { } srcIdx || srcIdx < 0 || srcIdx >= (fm.Sources?.Count ?? 0))
            {
                // A cut with no usable source cannot be rebuilt: its span plays the compile output.
                pieces.Add((cut, new Piece(null, 0, t0, t1 - t0, [], "cut_without_source")));
                continue;
            }

            List<Piece> into = [];
            Expand(final, cut, srcIdx, cut.SourceStartSec ?? 0, t0, t1, t0, [], byResult, into, resolveOrigin, noRegionOrCensor);
            pieces.AddRange(into.Select(p => (cut, p)));
        }

        // A treated seam with a crossfade places the next cut's start OVER the previous cut's tail. The seeded
        // document cannot blend two clips (the validator forbids overlap and a Transition must sit on a gap), so
        // the overlap becomes a hard cut: the outgoing clip keeps it and one marker says the compile softened it.
        markers.AddRange(ResolveSeamOverlaps(pieces, fps));

        // A censored window must cover every frame the compile's inclusive enable=between(t,s,e) touches: round it OUTWARD to
        // frames (start floor, end floor + 1), and trim the neighbouring pieces back so nothing overlaps.
        WidenCensorWindows(pieces, fps, fm.OutputVideo.DurationSec);

        // 2. Representable pieces become layers; a layer that cannot be built turns that piece into a fallback.
        List<(TimelineItem Cut, Piece Piece, PieceLayers? Layers)> built = [];
        int pieceIndex = 0;
        foreach ((TimelineItem cut, Piece piece) in pieces)
        {
            if (piece.Fallback is not null)
            {
                built.Add((cut, piece, null));
                continue;
            }

            string? pieceReason = null;
            PieceLayers? layers = BuildLayers(piece, cw, ch, fps, final, cut, pieceIndex++, resolveOrigin, hasAudio, altersPixelsOnDecode, inputProfileFor, ref pieceReason);
            built.Add(layers is null ? (cut, piece with { Origin = null, Fallback = pieceReason ?? "not_representable" }, null) : (cut, piece, layers));
        }

        // 3. Emit in time order; adjacent fallback pieces become one fallback clip with one marker.
        int i = 0;
        while (i < built.Count)
        {
            (TimelineItem cut, Piece piece, PieceLayers? layers) = built[i];
            if (layers is not null)
            {
                main.Add(layers.Main);
                if (layers.Fill is not null) { fill.Add(layers.Fill); }
                if (layers.Audio is not null) { audio.Add(layers.Audio); }
                foreach ((string key, string name, MediaClip clip) in layers.Region)
                {
                    if (!regionTracks.TryGetValue(key, out var bucket))
                    {
                        regionTracks[key] = bucket = (name, []);
                    }

                    bucket.Items.Add(clip);
                }

                nontrivial |= layers.Nontrivial || piece.OuterToInner.Count > 1;
                depth = Math.Max(depth, piece.OuterToInner.Count);
                i++;
                continue;
            }

            int j = i;
            double start = piece.OutStartSec, end = piece.OutStartSec + piece.DurationSec;
            List<string> reasons = [piece.Fallback!];
            while (j + 1 < built.Count && built[j + 1].Layers is null && built[j + 1].Piece.OutStartSec <= end + 1e-6)
            {
                j++;
                end = Math.Max(end, built[j].Piece.OutStartSec + built[j].Piece.DurationSec);
                if (!reasons.Contains(built[j].Piece.Fallback!)) { reasons.Add(built[j].Piece.Fallback!); }
            }

            long recIn = EditTimelineSeeder.Frames(start, fps);
            long recOut = Math.Max(recIn + 1, EditTimelineSeeder.Frames(end, fps));
            string reasonText = string.Join(", ", reasons);
            string message = $"Not decomposed ({reasonText}): this span plays the workflow's compile output because the original cuts and effects cannot be rebuilt exactly.";
            string link = LinkId($"fallback:{recIn}");
            main.Add(new MediaClip(
                Id: EditTimelineSeeder.DeterministicItemId($"lineage:fallback:{recIn}"),
                Name: cut.Label ?? "Compile output",
                RecordIn: recIn,
                RecordOut: recOut,
                AssetRef: finalRender,
                SourceIn: recIn,
                Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: final.StepResultId, Rationale: message),
                LinkId: finalHasAudio ? link : null));
            if (finalHasAudio)
            {
                audio.Add(new MediaClip(
                    Id: EditTimelineSeeder.DeterministicItemId($"lineage:fallback-audio:{recIn}"),
                    Name: cut.Label ?? "Compile output",
                    RecordIn: recIn,
                    RecordOut: recOut,
                    AssetRef: finalRender,
                    SourceIn: recIn,
                    Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: final.StepResultId, Rationale: message),
                    LinkId: link));
            }

            markers.Add(new Marker(
                EditTimelineSeeder.DeterministicItemId($"lineage:marker:{recIn}"),
                recIn,
                "From the compile output",
                "orange",
                $"Not decomposed: {reasonText}",
                recOut - recIn));
            unplaced.Add(new UnplacedItem("video", cut.Id ?? string.Empty, cut.Label, "lineage_fallback:" + reasonText));
            nontrivial = true;
            i = j + 1;
        }

        // The final compile's program fade is the whole picture/sound fading. Map it onto the first and last
        // ORIGINAL-footage clips (a fallback already contains the compile's own fade), then say nothing about it
        // in AudioDifferences — only a fade that was actually carried is removed from that note.
        bool videoFadeMapped = false;
        bool audioFadeMapped = false;
        ApplyProgramFades(final.Config!, main, fill, audio, fps, ref videoFadeMapped, ref audioFadeMapped);
        nontrivial |= videoFadeMapped || audioFadeMapped;

        if (!nontrivial && depth <= 1)
        {
            return null;
        }

        // Audio the compile shaped that the seeded document does not carry: say so rather than let it differ silently.
        string[] audioNotes = levels.SelectMany((l, index) => AudioDifferences(l, index == 0 && audioFadeMapped)).Distinct().ToArray();
        if (audioNotes.Length > 0)
        {
            markers.Add(new Marker(
                EditTimelineSeeder.DeterministicItemId("lineage:audio-note"),
                0,
                "Audio differs from the compile",
                "orange",
                "The compile shaped the sound (" + string.Join(", ", audioNotes) + "); the seeded sound tracks do not reproduce that.",
                null));
        }

        Track NewTrack(string label, string name, TrackKind kind, List<Item> items) =>
            new(EditTimelineSeeder.TrackId(label), kind, name, null, 100, false, true, false, false, null, items);

        List<Track> tracks = [];
        if (fill.Count > 0) { tracks.Add(NewTrack(FillTrackLabel, "Canvas fill (blurred)", TrackKind.Video, fill)); }
        if (main.Count > 0) { tracks.Add(NewTrack(MainTrackLabel, "Video", TrackKind.Video, main)); }
        foreach ((string key, (string name, List<Item> items)) in regionTracks.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            tracks.Add(NewTrack(RegionTrackLabelPrefix + key, name, TrackKind.Video, items));
        }

        if (audio.Count > 0) { tracks.Add(NewTrack(EditTimelineSeeder.CameraAudioLabel, EditTimelineSeeder.CameraAudioName, TrackKind.Audio, audio)); }

        // The final compile's music, sfx and caption items keep their own tracks, mapped by the plain seeder.
        List<TimelineTrack> extras = (fm.Tracks ?? []).Where(t => t.Type is "music" or "sfx" or "dialogue" && (t.Items?.Count ?? 0) > 0).ToList();
        if (extras.Count > 0 && resolveKey is not null)
        {
            SeededTimeline plain = EditTimelineSeeder.SeedWithReport(fm with { Tracks = extras }, newTimelineId, resolveKey, null, resolveOrigin);
            // The plain seeder maps the transcript ("dialogue") onto a visible Captions track. The transcript is only a picture
            // element when the compile burned captions in; otherwise keep it as a DISABLED track (kept for the editor, never
            // rendered or previewed) so the seeded timeline does not show subtitles the final video never had.
            bool captionsBurned = final.Config!.EnableCaptions;
            tracks.AddRange(plain.Document.Tracks.Select(t => !captionsBurned && t.Kind == TrackKind.Video ? t with { Enabled = false } : t));
            unplaced.AddRange(plain.Unplaced);
            if (markers.Any(m => m.Name == "From the compile output") && plain.Document.Tracks.Any(t => t.Kind == TrackKind.Audio))
            {
                markers = markers.Select(m => m.Name == "From the compile output"
                    ? m with { Note = m.Note + ". The compile output on this span already carries the music and sound effects, so they play twice here." }
                    : m).ToList();
            }
        }

        EditTimeline document = new(
            id: newTimelineId,
            projectId: fm.ProjectId,
            name: fm.OutputVideo.StorageKey ?? string.Empty,
            settings: new EditTimelineSettings(cw, ch, EditTimelineSeeder.RateFromFps(fps), EditTimelineSeeder.DefaultSampleRate, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks: tracks,
            markers: markers,
            inOut: null,
            seed: new EditTimelineSeed(fm.ExecutionId, fm.Version));
        return new SeededTimeline(document, unplaced);
    }

    private static void WidenCensorWindows(List<(TimelineItem Cut, Piece Piece)> pieces, double fps, double programSec)
    {
        const double Epsilon = 1e-6;
        const double ManifestRoundingSec = 0.0005;
        long totalFrames = (long)Math.Floor((programSec * fps) + Epsilon);
        for (int k = 0; k < pieces.Count; k++)
        {
            Piece p = pieces[k].Piece;
            if (p.Fallback is null || !p.Fallback.Contains("censor_boxes", StringComparison.Ordinal))
            {
                continue;
            }

            long inF = Math.Max(0, (long)Math.Floor((p.OutStartSec * fps) + Epsilon));
            // The manifest writes times rounded to the millisecond while the compile uses the unrounded end, so widen the end by that half-millisecond.
            // The start needs no widening: the first frame touched is ceil(s_true * fps) and floor(s_written * fps) is never above it.
            long outF = (long)Math.Floor(((p.OutStartSec + p.DurationSec + ManifestRoundingSec) * fps) + Epsilon) + 1;
            if (totalFrames > 0) { outF = Math.Min(outF, totalFrames); }
            pieces[k] = (pieces[k].Cut, p with { OutStartSec = inF / fps, DurationSec = Math.Max(1.0 / fps, (outF - inF) / fps) });

            // The piece before ends where the censored window begins; the piece after begins where it ends (source advances with it).
            if (k > 0 && pieces[k - 1].Piece is { Fallback: null } before && before.OutStartSec + before.DurationSec > inF / fps + 1e-9)
            {
                double dur = (inF / fps) - before.OutStartSec;
                pieces[k - 1] = (pieces[k - 1].Cut, before with { DurationSec = dur });
            }

            if (k + 1 < pieces.Count && pieces[k + 1].Piece is { Fallback: null } after && after.OutStartSec < outF / fps - 1e-9)
            {
                double cut = (outF / fps) - after.OutStartSec;
                pieces[k + 1] = (pieces[k + 1].Cut, after with { OutStartSec = outF / fps, OriginStartSec = after.OriginStartSec + cut, DurationSec = after.DurationSec - cut });
            }
        }

        // A neighbour trimmed to nothing is wholly covered by the censored window: drop it.
        pieces.RemoveAll(x => x.Piece.Fallback is null && x.Piece.DurationSec < 0.5 / fps);
    }

    private static bool IsVideo(TimelineTrack t) => string.Equals(t.Type, "video", StringComparison.Ordinal);

    /// <summary>A link id in the editor's current format (1-64 letters, digits, '-' or '_'), stable per identity.</summary>
    private static string LinkId(string identity) => "lk-" + EditTimelineSeeder.DeterministicItemId("lineage:link:" + identity).ToString("N");

    /// <summary>Audio shaping the compile applied that the seeded sound tracks do not reproduce (voiceover, loudness, fades, ducking).</summary>
    private static IEnumerable<string> AudioDifferences(LineageLevel level, bool programAudioFadeMapped)
    {
        if (level.Config is not { } c) { yield break; }
        if (c.EnableVoiceover || c.EnablePickups) { yield return "voiceover"; }
        if (c.LoudnessTarget != ProgramLoudness.Off) { yield return "loudness"; }
        if ((c.ProgramAudioFadeInMs > 0 || c.ProgramAudioFadeOutMs > 0) && !programAudioFadeMapped) { yield return "audio fade"; }
        if (c.MixInsertAudio || c.InsertAudioGainDb != 0) { yield return "insert audio"; }
        if (c.EnableMusic && c.MusicDucking != MusicDuckingMode.Off) { yield return "music ducking"; }
        if (c.AudioSeamRampMs > 0 && c.TransitionPolicy != VideoTransitionPolicy.Off) { yield return "audio seams"; }
    }

    /// <summary>
    /// Carries the final compile's program fades onto the seeded clips. A fade is mapped onto the FIRST and LAST
    /// main clip (and the fill clip that shares its link group) and onto the first/last camera-audio clip, with a
    /// plain clamp to each clip's own length. A first/last clip that is a fallback is skipped: the compile's own
    /// render on that span already carries the fade, so fading it again would double it.
    /// </summary>
    private static void ApplyProgramFades(
        VideoCompileStepConfig config,
        List<Item> main,
        List<Item> fill,
        List<Item> audio,
        double fps,
        ref bool videoMapped,
        ref bool audioMapped)
    {
        long fadeIn = EditTimelineSeeder.Frames(config.ProgramFadeInMs / 1000.0, fps);
        long fadeOut = EditTimelineSeeder.Frames(config.ProgramFadeOutMs / 1000.0, fps);
        if (fadeIn > 0 || fadeOut > 0)
        {
            videoMapped = true;
            ApplyEndFades(main, fill, fadeIn, fadeOut);
        }

        long audioIn = EditTimelineSeeder.Frames(config.ProgramAudioFadeInMs / 1000.0, fps);
        long audioOut = EditTimelineSeeder.Frames(config.ProgramAudioFadeOutMs / 1000.0, fps);
        if (audioIn > 0 || audioOut > 0)
        {
            // Only camera audio (the original file) can carry it; a fallback audio clip already carries the compile's fade.
            audioMapped = audio.Count > 0 && IsOriginClip(audio[0]) && IsOriginClip(audio[^1]);
            if (audioMapped)
            {
                ApplyEndFades(audio, [], audioIn, audioOut);
            }
        }
    }

    private static bool IsOriginClip(Item item) => item is MediaClip clip && clip.AssetRef.Kind == AssetRefKind.ProjectFile;

    /// <summary>
    /// Writes the fade onto the first and last media clips of <paramref name="items"/> (and onto the fill clips in
    /// the same link group), clamping each to its own length so a same-clip start and end never exceed it.
    /// </summary>
    private static void ApplyEndFades(List<Item> items, List<Item> fill, long fadeIn, long fadeOut)
    {
        int firstIndex = items.FindIndex(i => i is MediaClip);
        int lastIndex = items.FindLastIndex(i => i is MediaClip);
        bool firstMaps = firstIndex >= 0 && IsOriginClip(items[firstIndex]);
        bool lastMaps = lastIndex >= 0 && IsOriginClip(items[lastIndex]);
        if (!firstMaps && !lastMaps)
        {
            return;
        }

        MediaClip first = firstMaps ? (MediaClip)items[firstIndex] : null!;
        MediaClip last = lastMaps ? (MediaClip)items[lastIndex] : null!;
        long firstLength = firstMaps ? first.RecordOut - first.RecordIn : 0;
        long lastLength = lastMaps ? last.RecordOut - last.RecordIn : 0;
        long inFrames = firstMaps ? Math.Clamp(fadeIn, 0, firstLength) : 0;
        long outFrames = !lastMaps ? 0
            : firstIndex == lastIndex ? Math.Clamp(fadeOut, 0, lastLength - inFrames)
            : Math.Clamp(fadeOut, 0, lastLength);

        if (firstMaps)
        {
            items[firstIndex] = WithFades(first, inFrames, null);
        }

        if (outFrames > 0)
        {
            items[lastIndex] = WithFades((MediaClip)items[lastIndex], null, outFrames);
        }

        // The blurred canvas fill sits UNDER the picture: it has to fade with it or the edges stay lit while the picture goes black.
        for (int i = 0; i < fill.Count; i++)
        {
            if (fill[i] is not MediaClip f)
            {
                continue;
            }

            long? fadeInValue = firstMaps && f.LinkId == first.LinkId ? inFrames : null;
            long? fadeOutValue = outFrames > 0 && f.LinkId == last.LinkId ? outFrames : null;
            if (fadeInValue is not null || fadeOutValue is not null)
            {
                fill[i] = WithFades(f, fadeInValue, fadeOutValue);
            }
        }
    }

    private static MediaClip WithInputProfile(MediaClip c, string inputProfile) =>
        new(c.Id, c.Name, c.RecordIn, c.RecordOut, c.AssetRef, c.SourceIn, c.PlaybackRate, c.Enabled, c.Provenance,
            c.GainDb, c.Transform, c.Crop, c.Reverse, c.Hold, c.FadeInFrames, c.FadeOutFrames,
            c.Grade, c.Mask, c.Keyer, c.Effects, c.Stabilize, c.Reframe, c.RetimeQuality, c.CornerPin, c.ChannelMap, c.LinkId, c.RegionEffects,
            inputProfile, c.SourceRotation);

    /// <summary>MediaClip's fields are read-only on the record, so a fade is written by rebuilding it field for field.</summary>
    private static MediaClip WithFades(MediaClip c, long? fadeIn, long? fadeOut) =>
        new(c.Id, c.Name, c.RecordIn, c.RecordOut, c.AssetRef, c.SourceIn, c.PlaybackRate, c.Enabled, c.Provenance,
            c.GainDb, c.Transform, c.Crop, c.Reverse, c.Hold,
            fadeIn ?? c.FadeInFrames, fadeOut ?? c.FadeOutFrames,
            c.Grade, c.Mask, c.Keyer, c.Effects, c.Stabilize, c.Reframe, c.RetimeQuality, c.CornerPin, c.ChannelMap, c.LinkId, c.RegionEffects, c.InputProfile, c.SourceRotation, c.BlendMode);

    /// <summary>
    /// Resolves the overlap a treated crossfade seam puts between two cuts. The seeded document cannot blend two
    /// clips (tracks must not overlap and a Transition can only sit on a gap), so the outgoing clip keeps the
    /// overlapping window and the incoming clip starts where it ends: a plain hard cut on the original footage,
    /// with one marker per softened seam saying the compile blended it.
    /// </summary>
    private static List<Marker> ResolveSeamOverlaps(List<(TimelineItem Cut, Piece Piece)> pieces, double fps)
    {
        List<Marker> markers = [];
        long? lastSeamFrame = null;
        double coveredEnd = double.NegativeInfinity;
        for (int k = 0; k < pieces.Count; k++)
        {
            Piece piece = pieces[k].Piece;
            double start = piece.OutStartSec;
            double end = start + piece.DurationSec;
            if (start < coveredEnd - 1e-9)
            {
                long seamFrame = EditTimelineSeeder.Frames(coveredEnd, fps);
                if (lastSeamFrame != seamFrame)
                {
                    markers.Add(new Marker(
                        EditTimelineSeeder.DeterministicItemId($"lineage:seam:{seamFrame}"),
                        seamFrame,
                        "Softened seam",
                        "blue",
                        "The compile blended this seam with a transition; the seeded timeline uses a hard cut here.",
                        null));
                    lastSeamFrame = seamFrame;
                }

                double trim = coveredEnd - start;
                if (trim >= piece.DurationSec - 1e-9)
                {
                    // The whole piece lies inside the previous clip's frame: the hard cut shows the outgoing one.
                    pieces.RemoveAt(k);
                    k--;
                    continue;
                }

                piece = piece with { OutStartSec = coveredEnd, OriginStartSec = piece.OriginStartSec + trim, DurationSec = piece.DurationSec - trim };
                pieces[k] = (pieces[k].Cut, piece);
                end = piece.OutStartSec + piece.DurationSec;
            }

            coveredEnd = Math.Max(coveredEnd, end);
        }

        return markers;
    }

    // ── Time: replay each level's cuts down to the original ─────────────────────────────────────────

    private static void AddFallback(List<Piece> into, double finalStart, double duration, string reason)
    {
        if (duration > 1e-6)
        {
            into.Add(new Piece(null, 0, finalStart, duration, [], reason));
        }
    }

    /// <summary>
    /// Replays <paramref name="cut"/> of <paramref name="level"/> over its output window [t0,t1] (<paramref name="a"/> = the matching time in
    /// the cut's source). Only sub-windows that cannot be rebuilt become fallback pieces; the rest recurse down to the original file.
    /// <paramref name="finalStart"/> is where t0 lands on the FINAL timeline (time is 1:1 at every level: a cut whose length differs from its
    /// source window is retimed and falls back).
    /// </summary>
    private static void Expand(
        LineageLevel level, TimelineItem cut, int srcIdx, double a, double t0, double t1, double finalStart, List<Chain> outer,
        Dictionary<Guid, LineageLevel> byResult, List<Piece> into, Func<TimelineSource, AssetRef?> resolveOrigin, bool noRegionOrCensor)
    {
        double len = t1 - t0;
        bool isFinal = outer.Count == 0;
        if (WholeReason(level, cut, srcIdx, isFinal, noRegionOrCensor) is { } whole)
        {
            AddFallback(into, finalStart, len, whole);
            return;
        }

        List<Bad> bad = BadWindows(level, t0, t1, isFinal);
        double cursor = t0;
        foreach (Bad w in bad)
        {
            if (w.Start > cursor + 1e-6)
            {
                ExpandGood(level, srcIdx, a + (cursor - t0), cursor, w.Start, finalStart + (cursor - t0), outer, byResult, into, resolveOrigin, noRegionOrCensor);
            }

            AddFallback(into, finalStart + (w.Start - t0), w.End - w.Start, w.Reason);
            cursor = w.End;
        }

        if (t1 > cursor + 1e-6)
        {
            ExpandGood(level, srcIdx, a + (cursor - t0), cursor, t1, finalStart + (cursor - t0), outer, byResult, into, resolveOrigin, noRegionOrCensor);
        }
    }

    private static void ExpandGood(
        LineageLevel level, int srcIdx, double a, double t0, double t1, double finalStart, List<Chain> outer,
        Dictionary<Guid, LineageLevel> byResult, List<Piece> into, Func<TimelineSource, AssetRef?> resolveOrigin, bool noRegionOrCensor)
    {
        double len = t1 - t0;
        TimelineSource source = level.Manifest.Sources![srcIdx];
        List<Chain> chain = [.. outer, new Chain(level, srcIdx)];
        double trim = source.TrimInSec ?? 0;
        if (source.ProjectFileId is not null)
        {
            if (resolveOrigin(source) is null)
            {
                AddFallback(into, finalStart, len, "source_unresolved");
                return;
            }

            into.Add(new Piece(source, a + trim, finalStart, len, chain, null));
            return;
        }

        if (source.StepResultId is { } inputId && byResult.TryGetValue(inputId, out LineageLevel? inner))
        {
            // The wanted window [x0, x1] is on the inner compile's OUTPUT timeline; replay the inner cuts across it.
            double x0 = a + trim, x1 = x0 + len;
            double cursor = x0;
            foreach (TimelineItem c in (inner.Manifest.Tracks ?? []).Where(IsVideo).SelectMany(t => t.Items ?? []).OrderBy(i => i.TimelineStartSec))
            {
                double c0 = c.TimelineStartSec;
                double c1 = c.TimelineEndSec ?? (c0 + ((c.SourceEndSec ?? 0) - (c.SourceStartSec ?? 0)));
                double o0 = Math.Max(c0, x0), o1 = Math.Min(c1, x1);
                if (o1 - o0 <= 1e-6)
                {
                    continue;
                }

                if (o0 > cursor + ToleranceSec)
                {
                    AddFallback(into, finalStart + (cursor - x0), o0 - cursor, "gap_in_inner_cuts");
                }

                double subStart = finalStart + (o0 - x0);
                if (c.SourceIndex is not { } innerSrc || innerSrc < 0 || innerSrc >= (inner.Manifest.Sources?.Count ?? 0))
                {
                    AddFallback(into, subStart, o1 - o0, "cut_without_source");
                }
                else
                {
                    Expand(inner, c, innerSrc, (c.SourceStartSec ?? 0) + (o0 - c0), o0, o1, subStart, chain, byResult, into, resolveOrigin, noRegionOrCensor);
                }

                cursor = Math.Max(cursor, o1);
            }

            if (x1 > cursor + ToleranceSec)
            {
                AddFallback(into, finalStart + (cursor - x0), x1 - cursor, "gap_in_inner_cuts");
            }

            return;
        }

        AddFallback(into, finalStart, len, "source_unresolved");
    }

    /// <summary>Why a whole cut of a level cannot be rebuilt (independent of any window); null when it can.</summary>
    private static string? WholeReason(LineageLevel level, TimelineItem cut, int srcIdx, bool isFinal, bool noRegionOrCensor)
    {
        VideoCompileStepConfig? c = level.Config;
        if (c is null)
        {
            return "step_config_unreadable";
        }

        // Generated cutaways sit over the original in the compile; replaying the original under a cutaway the seeded
        // timeline cannot cover would show whatever this level censors, so the whole cut plays the censored render.
        if ((c.EnableGeneratedClips || c.GeneratedClips is not null) &&
            (c.RegionEffects is { Count: > 0 } || c.CensorLabels is { Count: > 0 } ||
             (level.Manifest.Tracks ?? []).Any(t => string.Equals(t.Type, "censor", StringComparison.Ordinal) && (t.Items?.Count ?? 0) > 0)))
        {
            return "generated_clip_censored";
        }

        if (level.Manifest.Sources is not { } sources || srcIdx < 0 || srcIdx >= sources.Count)
        {
            return "source_unresolved";
        }

        // The cut's OWN timeline length against its OWN source window: a retime (speed, freeze) breaks the 1:1 time the replay relies on.
        double? outLen = cut.TimelineEndSec - cut.TimelineStartSec;
        double? srcLen = cut.SourceEndSec - cut.SourceStartSec;
        if (outLen is null || srcLen is null || Math.Abs(outLen.Value - srcLen.Value) > ToleranceSec)
        {
            return "retimed_or_speed_changed";
        }

        if (!string.IsNullOrWhiteSpace(cut.ColorGrade) || !string.IsNullOrWhiteSpace(cut.Color))
        {
            return "color_grade";
        }

        if (c.Reframes is { Count: > 0 } || c.FollowSubject is { Count: > 0 } || c.EventMoves is { Count: > 0 }) { return "reframe"; }
        if (c.FlashCues is { Count: > 0 }) { return "flash_cues"; }
        if (c.SpeedRamps is { Count: > 0 }) { return "speed_ramps"; }
        if (c.SubjectSafeArea is not null) { return "subject_safe_area"; }
        if (c.OutputFit == VideoOutputFit.Subject || (c.SourceFits?.Values.Any(v => v == VideoOutputFit.Subject) ?? false)) { return "subject_fit"; }
        // A whole-program video fade is carried onto the first/last original clip (see ApplyProgramFades), but only
        // the FINAL level's fade onto a BLACK program with no region/censor layers: an inner compile's fade is baked
        // into its render, a white fade is not representable, and a region/censor layer would not fade with the picture.
        // A whole-program video fade is carried onto the first/last original clip (see ApplyProgramFades), but only
        // the FINAL level's fade onto a BLACK program with no region/censor layers: an inner compile's fade is baked
        // into its render, a white fade is not representable, and a region/censor layer would not fade with the picture.
        if (c.ProgramFadeInMs > 0 || c.ProgramFadeOutMs > 0)
        {
            if (!isFinal || !string.Equals(c.ProgramFadeColor, "black", StringComparison.OrdinalIgnoreCase) || !noRegionOrCensor)
            {
                return "program_fade";
            }
        }
        if (c.EnableSpanMotion) { return "span_motion"; }
        if (c.EnableGeneratedClips && c.GeneratedClips is not null && sources[srcIdx].ProjectFileId is null && sources[srcIdx].StepResultId is null) { return "generated_clip"; }

        return null;
    }

    /// <summary>The sub-windows of [t0,t1] on a level's output timeline that carry something only its output shows, merged, in order.</summary>
    private static List<Bad> BadWindows(LineageLevel level, double t0, double t1, bool isFinal)
    {
        VideoCompileStepConfig c = level.Config!;
        List<Bad> found = [];
        void Add(double s, double e, string reason)
        {
            double cs = Math.Max(s, t0), ce = Math.Min(e, t1);
            if (ce - cs > 1e-6) { found.Add(new Bad(cs, ce, reason)); }
        }

        foreach (TimelineTrack track in level.Manifest.Tracks ?? [])
        {
            foreach (TimelineItem item in track.Items ?? [])
            {
                double s = item.TimelineStartSec;
                double e = item.TimelineEndSec ?? (s + (item.DurationSec ?? 0));
                if (IsVideo(track))
                {
                    // A treated seam's crossfade is resolved as a hard cut plus one marker (ResolveSeamOverlaps):
                    // the original cuts can be replayed, only the blend between them cannot.
                    continue;
                }

                switch (track.Type)
                {
                    case "inserts": Add(s, e, "screen_insert"); break;
                    case "graphics": Add(s, e, "graphics_overlay"); break;
                    // Padded, tracked boxes the engine censors: not the silhouette, so that window plays the compile output.
                    // A zero-length censor still touches one frame (the compile's between() is inclusive).
                    case "censor": Add(s, Math.Max(e, s + 1e-4), "censor_boxes"); break;
                    // The transcript is metadata unless captions were burned into this level's picture.
                    case "dialogue": if (!isFinal && c.EnableCaptions) { Add(s, e, "captions"); } break;
                    // The final compile's sound is mapped onto its own tracks; an inner compile's is baked into its output.
                    case "sfx": if (!isFinal) { Add(s, e, "sound_effect"); } break;
                    case "music": if (!isFinal) { Add(s, e, "music_bed"); } break;
                    default: Add(s, e, "extra_track"); break;
                }
            }
        }

        List<Bad> merged = [];
        foreach (Bad b in found.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && b.Start <= merged[^1].End + 1e-6)
            {
                Bad last = merged[^1];
                merged[^1] = last with { End = Math.Max(last.End, b.End), Reason = last.Reason == b.Reason || last.Reason.Contains(b.Reason, StringComparison.Ordinal) ? last.Reason : last.Reason + "+" + b.Reason };
            }
            else
            {
                merged.Add(b);
            }
        }

        return merged;
    }

    // ── Space + layers ─────────────────────────────────────────────────────────────────────────────

    private sealed record PieceLayers(MediaClip Main, MediaClip? Fill, MediaClip? Audio, List<(string Key, string Name, MediaClip Clip)> Region, bool Nontrivial);

    /// <summary>Origin pixel to canvas pixel: x' = Scale x + OffsetX, y' = Scale y + OffsetY (aspect preserved).</summary>
    private readonly record struct Affine(double Scale, double OffsetX, double OffsetY);

    private static Affine Apply(Affine inner, CanvasFitRect fit, int srcW, int srcH)
    {
        double fx = fit.Width / srcW;
        return new Affine(inner.Scale * fx, (inner.OffsetX * fx) + fit.OffsetX, (inner.OffsetY * fx) + fit.OffsetY);
    }

    private static CanvasFitRect FitFor(LineageLevel level, int srcIdx, int canvasW, int canvasH, int srcW, int srcH)
    {
        VideoCompileStepConfig c = level.Config!;
        VideoOutputFit fit = c.SourceFits is { } map && map.TryGetValue(srcIdx, out VideoOutputFit over) ? over : c.OutputFit;
        bool fixedFormat = c.OutputFormat != VideoOutputFormat.Source;
        // Source format keeps the source's own canvas: the fit is the identity (both sizes are equal).
        return CanvasFitGeometry.For(srcW, srcH, canvasW, canvasH, cover: fixedFormat && fit == VideoOutputFit.Crop);
    }

    private static PieceLayers? BuildLayers(
        Piece piece, int finalW, int finalH, double fps, LineageLevel final, TimelineItem cut, int index,
        Func<TimelineSource, AssetRef?> resolveOrigin, Func<string, bool> hasAudio, Func<string, bool?>? altersPixelsOnDecode, Func<AssetRef, string?>? inputProfileFor, ref string? reason)
    {
        TimelineSource originSource = piece.Origin!;
        AssetRef origin = resolveOrigin(originSource)!;
        int ow = Math.Max(2, originSource.Width);
        int oh = Math.Max(2, originSource.Height);
        string link = LinkId($"{cut.Id}:{index}:{piece.OutStartSec:F3}");

        // Compose the fits innermost -> outermost. `before` is the picture as the FINAL level receives it.
        IReadOnlyList<Chain> innerToOuter = piece.OuterToInner.Reverse().ToList();
        Affine acc = new(1, 0, 0);
        int curW = ow, curH = oh;
        Affine beforeFinal = acc;
        int beforeW = curW, beforeH = curH;
        double outerScale = 1;   // canvas-to-canvas scale from the innermost canvas to the final one
        bool finalPads = false;
        for (int i = 0; i < innerToOuter.Count; i++)
        {
            Chain step = innerToOuter[i];
            int canvasW = Math.Max(2, step.Level.Manifest.OutputVideo.Width);
            int canvasH = Math.Max(2, step.Level.Manifest.OutputVideo.Height);
            if (i == innerToOuter.Count - 1)
            {
                beforeFinal = acc;
                beforeW = curW;
                beforeH = curH;
            }

            CanvasFitRect fit = FitFor(step.Level, step.SourceIndex, canvasW, canvasH, curW, curH);
            bool pads = fit.OffsetX > 0.5 || fit.OffsetY > 0.5;
            if (i == innerToOuter.Count - 1) { finalPads = pads; }
            else if (pads && step.Level.Config!.CanvasFill == VideoCanvasFill.Blur)
            {
                reason = "canvas_fill_below_final_level";
                return null;
            }

            if (i >= 1) { outerScale *= fit.Width / curW; }
            acc = Apply(acc, fit, curW, curH);
            curW = canvasW;
            curH = canvasH;
        }

        if (curW != finalW || curH != finalH)
        {
            reason = "canvas_mismatch";
            return null;
        }

        // Region effects / censor belong to the INNERMOST compile (their analysis tracks are on the original).
        List<(string Key, string Name, MediaClip Clip)> region = [];
        MediaClip main = BuildClip(piece, origin, acc, ow, oh, finalW, finalH, fps, cut, index, "main", effects: null, mask: null, opacity: 1, linkId: link);

        // Outer levels carrying their own region effects / censor would act on a picture the original has no track for.
        for (int i = 1; i < innerToOuter.Count; i++)
        {
            VideoCompileStepConfig oc = innerToOuter[i].Level.Config!;
            if (oc.RegionEffects is { Count: > 0 })
            {
                reason = "effect_on_intermediate_render";
                return null;
            }
        }

        // The Pad + blur fill: supported when the picture reaching the final level fills its whole canvas.
        MediaClip? fillClip = null;
        VideoCompileStepConfig fc = final.Config!;
        if (finalPads && fc.CanvasFill == VideoCanvasFill.Blur)
        {
            if (!Covers(beforeFinal, ow, oh, beforeW, beforeH))
            {
                reason = "canvas_fill_over_padded_input";
                return null;
            }

            CanvasFitRect cover = CanvasFitGeometry.For(beforeW, beforeH, finalW, finalH, cover: true);
            Affine fillAcc = Apply(beforeFinal, cover, beforeW, beforeH);
            fillClip = BuildClip(piece, origin, fillAcc, ow, oh, finalW, finalH, fps, cut, index, "fill",
                effects: BlurEffects(LineageCalibration.CanvasFillBlurSigma), mask: null, opacity: 1, linkId: link,
                grade: new GradeStack(true, [new GradeNode(EditTimelineSeeder.DeterministicItemId($"lineage:fill-grade:{cut.Id}:{index}"), "Canvas fill dim", true, new GradePrimaries(Offset: new GradeWheel(Master: LineageCalibration.CanvasFillBrightness)))]));
        }

        // Region effects and censor of the innermost compile.
        VideoCompileStepConfig ic = innerToOuter[0].Level.Config!;
        LineageLevel innerLevel = innerToOuter[0].Level;
        int innerSrc = innerToOuter[0].SourceIndex;
        double k = outerScale;

        int innerShort = Math.Max(1, Math.Min(innerLevel.Manifest.OutputVideo.Width, innerLevel.Manifest.OutputVideo.Height));
        List<(string Target, string Effect, double Sigma, double Block, bool Invert, double Feather, double Opacity)> wanted = [];
        foreach (VideoRegionEffect e in ic.RegionEffects ?? [])
        {
            if (e.FromEventId is not null || e.ToEventId is not null)
            {
                reason = "region_effect_window";
                return null;
            }

            switch (e.Effect)
            {
                case RegionEffect.Blur:
                    wanted.Add((e.Target, "blur", Math.Max(0.1, Math.Round(innerShort * LineageCalibration.RegionBlurSigmaFraction * e.Strength, 2)), 0, e.Invert, e.FeatherPx, e.Opacity));
                    break;
                case RegionEffect.Pixelate:
                    wanted.Add((e.Target, "pixelate", 0, innerShort * LineageCalibration.RegionPixelateBlockFraction * e.Strength, e.Invert, e.FeatherPx, e.Opacity));
                    break;
                default:
                    reason = "region_effect_" + e.Effect.ToString().ToLowerInvariant();
                    return null;
            }
        }

        // Several region effects on the same target, effect and invert are ONE layer; blurs stack as variances.
        foreach (var group in wanted.GroupBy(w => (w.Target.ToLowerInvariant(), w.Effect, w.Invert)))
        {
            var first = group.First();
            IReadOnlyList<string> trackIds = (innerLevel.AnalysisTracks ?? [])
                .Where(t => string.Equals(t.Prompt, first.Target, StringComparison.OrdinalIgnoreCase) && t.SourceIndex == innerSrc)
                .Select(t => t.Id).ToList();
            if (innerLevel.AnalysisStepResultId is not { } analysisId || trackIds.Count == 0)
            {
                reason = "region_target_not_tracked";
                return null;
            }

            IReadOnlyList<ClipEffect>? layerEffects = first.Effect == "blur"
                ? BlurEffects(Math.Sqrt(group.Sum(g => g.Sigma * g.Sigma)) * k)
                : PixelateEffects(group.Max(g => g.Block) * k);
            if (layerEffects is null)
            {
                // The compile's strength is beyond what an editor effect can express: never seed a weaker layer.
                reason = "effect_beyond_editor_range";
                return null;
            }

            // A clip mask names at most 8 tracks: more tracks become more layers of the same effect, never a truncation.
            List<string> allIds = trackIds.ToList();
            if (allIds.Count > MaxRegionTracks)
            {
                reason = "too_many_region_tracks";
                return null;
            }

            // invert(A union B) is not invert(A) union invert(B): split inverted layers would each blur the objects the others keep sharp.
            if (first.Invert && allIds.Count > 8)
            {
                reason = "inverted_mask_over_8_tracks";
                return null;
            }

            bool unverifiedRotation = altersPixelsOnDecode?.Invoke(piece.Origin!.StorageKey) is null
                && (innerLevel.AnalysisTracks ?? []).Any(t => allIds.Contains(t.Id) && !string.Equals(t.Facts?.DecodeGeometry, VideoAnalysisSourceInfo.UprightGeometry, StringComparison.Ordinal));
            for (int chunk = 0; chunk * 8 < allIds.Count; chunk++)
            {
                List<string> ids = allIds.Skip(chunk * 8).Take(8).ToList();
                ShapeMask mask = new(ShapeMask.SegmentationKind, AnalysisStepResultId: analysisId, TrackIds: ids, Invert: first.Invert, FeatherPx: Math.Min(64, group.Max(g => g.Feather)));
                MediaClip layer = BuildClip(piece, origin, acc, ow, oh, finalW, finalH, fps, cut, index, $"region:{first.Target}{first.Effect}:{chunk}",
                    effects: layerEffects, mask: mask, opacity: group.Min(g => g.Opacity), linkId: link,
                    note: unverifiedRotation ? "The rotation of this source was not verified when the timeline was seeded; the render refuses a mask that does not fit." : null);
                // The same binding rule the validator and the render apply: the track must be cut from THIS clip's own
                // source, untrimmed, covering the clip's source span. A track that does not bind sends the span to the fallback.
                foreach (string trackId in ids)
                {
                    SegmentationTrackFacts? facts = (innerLevel.AnalysisTracks ?? []).FirstOrDefault(t => t.Id == trackId)?.Facts;
                    string? refusal = facts is null ? "no source facts for the track" : SegmentationMaskBinding.Check(layer, fps, facts, altersPixelsOnDecode?.Invoke(piece.Origin!.StorageKey));
                    if (refusal is not null)
                    {
                        reason = "mask_not_bound";
                        return null;
                    }
                }

                string slug = Slug(first.Target + "-" + first.Effect + (first.Invert ? "-inv" : string.Empty)) + (chunk == 0 ? string.Empty : $"-p{chunk + 1}");
                string label = $"{(first.Effect == "blur" ? "Blur" : "Pixelate")}: {first.Target}" + (chunk == 0 ? string.Empty : $" ({chunk + 1})");
                region.Add((slug, label, layer));
            }
        }

        MediaClip? cameraAudio = null;
        if (!innerToOuter.Any(l => l.Level.Config!.MuteSourceAudio) && hasAudio(piece.Origin!.StorageKey))
        {
            cameraAudio = new MediaClip(
                Id: EditTimelineSeeder.DeterministicItemId($"lineage:audio:{cut.Id}:{index}:{main.RecordIn}"),
                Name: main.Name,
                RecordIn: main.RecordIn,
                RecordOut: main.RecordOut,
                AssetRef: main.AssetRef,
                SourceIn: main.SourceIn,
                Provenance: main.Provenance,
                LinkId: link);
        }

        bool nontrivial = fillClip is not null || region.Count > 0 || !IsIdentityFit(acc, ow, oh, finalW, finalH);
        // log-footage-color T-013: a clip of a source with a High camera-profile hint is seeded already converted.
        // Null function or no hint changes nothing.
        if (inputProfileFor?.Invoke(origin) is { Length: > 0 } inputProfile)
        {
            main = WithInputProfile(main, inputProfile);
            if (fillClip is not null) fillClip = WithInputProfile(fillClip, inputProfile);
            region = region.Select(r => (r.Key, r.Name, Clip: WithInputProfile(r.Clip, inputProfile))).ToList();
        }

        return new PieceLayers(main, fillClip, cameraAudio, region, nontrivial);
    }

    private static bool IsIdentityFit(Affine a, int ow, int oh, int cw, int ch) =>
        Math.Abs(a.OffsetX) < 0.5 && Math.Abs(a.OffsetY) < 0.5 && Math.Abs((a.Scale * ow) - cw) < 0.5 && Math.Abs((a.Scale * oh) - ch) < 0.5;

    private static bool Covers(Affine a, int ow, int oh, int canvasW, int canvasH) =>
        a.OffsetX <= 0.5 && a.OffsetY <= 0.5 && a.OffsetX + (a.Scale * ow) >= canvasW - 0.5 && a.OffsetY + (a.Scale * oh) >= canvasH - 0.5;

    private static string Slug(string s) => new(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

    /// <summary>
    /// A clip showing the original with the composed affine: crop what overflows the canvas, then the editor's own
    /// fit (decrease, no pad) x Scale, centred + Position (canvas fractions). A Transform is ALWAYS set so the clip
    /// takes the editor's no-pad path and lower layers show through where the picture does not reach.
    /// </summary>
    private static MediaClip BuildClip(
        Piece piece, AssetRef origin, Affine a, int ow, int oh, int cw, int ch, double fps, TimelineItem cut, int index,
        string kind, IReadOnlyList<ClipEffect>? effects, ShapeMask? mask, double opacity, GradeStack? grade = null, string? linkId = null, string? note = null)
    {
        double imgW = a.Scale * ow, imgH = a.Scale * oh;
        double left = Math.Max(0, -a.OffsetX) / imgW;
        double right = Math.Max(0, a.OffsetX + imgW - cw) / imgW;
        double top = Math.Max(0, -a.OffsetY) / imgH;
        double bottom = Math.Max(0, a.OffsetY + imgH - ch) / imgH;
        if (left < 1e-6) { left = 0; }
        if (right < 1e-6) { right = 0; }
        if (top < 1e-6) { top = 0; }
        if (bottom < 1e-6) { bottom = 0; }

        double visW = imgW * (1 - left - right), visH = imgH * (1 - top - bottom);
        double srcW = ow * (1 - left - right), srcH = oh * (1 - top - bottom);
        double f = Math.Min(cw / srcW, ch / srcH);
        double scale = a.Scale / f;
        double cx = Math.Max(0, a.OffsetX) + (visW / 2);
        double cy = Math.Max(0, a.OffsetY) + (visH / 2);
        double px = (cx - (cw / 2.0)) / cw;
        double py = (cy - (ch / 2.0)) / ch;

        ClipCrop? crop = left + right + top + bottom > 0 ? new ClipCrop(Round(left), Round(top), Round(right), Round(bottom)) : null;
        ClipTransform transform = new(
            PositionX: new Keyframed<double>(Math.Abs(px) < 1e-6 ? 0 : Round(px)),
            PositionY: new Keyframed<double>(Math.Abs(py) < 1e-6 ? 0 : Round(py)),
            Scale: new Keyframed<double>(Round(scale)),
            Opacity: opacity < 1 ? new Keyframed<double>(Round(opacity)) : null);

        long recIn = EditTimelineSeeder.Frames(piece.OutStartSec, fps);
        long recOut = EditTimelineSeeder.Frames(piece.OutStartSec + piece.DurationSec, fps);
        if (recOut <= recIn) { recOut = recIn + 1; }

        return new MediaClip(
            Id: EditTimelineSeeder.DeterministicItemId($"lineage:{kind}:{cut.Id}:{index}:{recIn}"),
            Name: cut.Label ?? "Cut",
            RecordIn: recIn,
            RecordOut: recOut,
            AssetRef: origin,
            SourceIn: EditTimelineSeeder.Frames(piece.OriginStartSec, fps),
            Provenance: new Provenance(ProvenanceOrigin.Generated, StepResultId: cut.StepResultId, Rationale: note ?? cut.AgentRationale, AgentName: cut.AgentName, ProviderName: cut.ProviderName),
            Transform: transform,
            Crop: crop,
            Grade: grade,
            Mask: mask,
            Effects: effects,
            LinkId: linkId);
    }

    /// <summary>
    /// A Gaussian of <paramref name="sigma"/> canvas pixels as editor blur effects (sigma = amount x 20, amount up to 2, so one
    /// effect reaches the compile's canvas-fill sigma 40). Past 40, equal blurs add in variance: n = ceil(sigma / 40), each sigma / sqrt(n).
    /// </summary>
    internal static IReadOnlyList<ClipEffect>? BlurEffects(double sigma)
    {
        if (!double.IsFinite(sigma) || sigma <= 0)
        {
            return [new ClipEffect("blur", 0.01)];
        }

        // One effect reaches amount 2 (sigma 40). More than the 8-effect cap cannot be expressed.
        double max = EditTimelineValidator.MaxBlurAmount * LineageCalibration.EditorBlurSigmaPerAmount;
        // Gaussians add in VARIANCE: n equal blurs of sigma/sqrt(n) make sigma, and one effect reaches `max`, so n = ceil((sigma/max)^2).
        int n = (int)Math.Ceiling(((sigma / max) * (sigma / max)) - 1e-9);
        if (n > EditTimelineValidator.MaxEffects)
        {
            return null;
        }

        n = Math.Max(1, n);
        double amount = Math.Clamp(sigma / Math.Sqrt(n) / LineageCalibration.EditorBlurSigmaPerAmount, 0.01, EditTimelineValidator.MaxBlurAmount);
        return Enumerable.Range(0, n).Select(_ => new ClipEffect("blur", Math.Round(amount, 4))).ToList();
    }

    /// <summary>The editor pixelate amount for a block of <paramref name="blockPx"/> canvas pixels; null past its 64 px ceiling.</summary>
    internal static IReadOnlyList<ClipEffect>? PixelateEffects(double blockPx)
    {
        double amount = blockPx / LineageCalibration.EditorPixelateBlockPerAmount;
        return amount > 1.0 + 1e-9 ? null : [new ClipEffect("pixelate", Math.Clamp(amount, 0.01, 1.0))];
    }

    /// <summary>Most tracks one region effect may name: the engine's own per-effect ceiling (MaxTracksPerEffect).</summary>
    private const int MaxRegionTracks = 16;

    private static double Round(double v) => Math.Round(v, 6);
}
