using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Maps a v1 <see cref="TimelineManifest"/> — the "Expanded Studio Tracks" document
/// <c>TimelineManifestBuilder</c> emits for a compiled render — onto the v2
/// <see cref="EditTimeline"/> contract the editor actually edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure.</b> No I/O, no database, no clock, no random: the same manifest and the same
/// <c>resolveAsset</c> always produce the same document, which is what makes a seed reproducible
/// and testable.
/// </para>
/// <para>
/// <b>Track identity.</b> The fixed v1 track labels (<c>V1</c>, <c>V2</c>, <c>V3</c>, <c>S1</c>,
/// <c>A2</c>, <c>A3</c>) are the timeline's stable track names, but T-011's <see cref="Track"/>
/// exposes only a <see cref="Guid"/> <c>Id</c>, so each label is turned into a deterministic,
/// name-based GUID (SHA-256 over a fixed namespace plus the label, first 16 bytes). The label
/// itself is carried in <see cref="Track.Name"/> alongside its human-readable role, and
/// <see cref="TrackLabelFor"/> is the single source of both.
/// </para>
/// <para>
/// <b>Skipping, never fabricating.</b> Every omission below leaves a value absent rather than
/// inventing one, because a fabricated edit is worse than a missing one. An item is SKIPPED (the
/// rest of the track still maps) when:
/// </para>
/// <list type="bullet">
/// <item><description>it has no end time (<c>TimelineEndSec</c> is null), or its end time does not
/// cross its start time once converted to frames;</description></item>
/// <item><description><c>resolveAsset</c> returns <c>null</c> for the identifier the manifest
/// carries — an unresolvable asset must never be swapped for a guess, and the T-013 validator
/// treats an unknown asset as a violation;</description></item>
/// <item><description>it needs an identifier it does not have: a <c>video</c> item with no
/// <c>SourceIndex</c> or one pointing outside <c>manifest.Sources</c>, an <c>inserts</c> item with
/// no <c>AssetStorageKey</c>, a <c>graphics</c> item with neither <c>Text</c> nor
/// <c>AssetStorageKey</c>, an <c>sfx</c> item with neither <c>ClipName</c> nor <c>SfxId</c>, or a
/// <c>music</c> item with no <c>TrackName</c>;</description></item>
/// <item><description>it carries no renderable text: a <c>dialogue</c> item whose <c>Text</c> is
/// null or whitespace would sanitize to an empty caption, which the T-013 validator rejects. A
/// <c>graphics</c> item in the same state is not skipped — it falls back to its rendered asset,
/// which is a real alternative the manifest offers;</description></item>
/// <item><description>it carries a <c>TransitionOut</c> but has no next item on its own track, or
/// the predecessor's <c>RecordOut</c> equals the successor's <c>RecordIn</c> (a hard butt cut).
/// A zero-length <see cref="Transition"/> cannot "sit exactly at the boundary" the validator
/// requires, so no transition is emitted at all.</description></item>
/// <item><description>it lies wholly inside the preceding item's covered range once a treated
/// crossfade seam is resolved to a hard cut (the compile blends two clips; the v2 document cannot),
/// or it is trimmed to start at that covered end with one "Softened seam" marker per seam
/// frame.</description></item>
/// </list>
/// <para>
/// The one absent-value default the seeder applies to a time is <c>SourceIn</c>: a null
/// <c>SourceStartSec</c> is read as frame 0, the source's own beginning, which is the only reading
/// that adds no information. Every other default comes from the target type itself (caption
/// language <c>"en"</c>, a title's own styling) rather than from the seeder. The timeline's own
/// <see cref="EditTimeline.Name"/> is the render's storage key, the only name the manifest carries;
/// it is empty rather than invented when even that is missing.
/// </para>
/// <para>
/// <b>Audio gain.</b> An audio item's gain is carried by <see cref="MediaClip.GainDb"/> as a
/// keyframed <c>double</c>: an SFX cue's <c>GainDb</c> becomes the clip's static value, and the
/// music bed's <c>BedDbfs</c> plus <c>DuckingEnvelope</c> become that static value plus a
/// <see cref="InterpKind.Linear"/> gain envelope whose keys are the envelope points at their own
/// frames. Neither direction is synthesized — no envelope is derived from a static level, and no
/// static level is derived from an envelope — and an audio item the manifest gives no gain at all
/// keeps a null <c>GainDb</c> rather than a defaulted one. The single exception is forced by the
/// target type: <c>Keyframed&lt;double&gt;.Value</c> is a plain <c>double</c> that cannot be
/// absent, so a ducking envelope present with no <c>BedDbfs</c> is emitted with the identity
/// <c>0</c> dB static value, which the envelope overrides from its first key onward; dropping the
/// automation instead would lose real data. <c>PlaybackRate</c> is deliberately NOT used as a
/// stand-in for gain: rate and gain are unrelated quantities, and conflating them would silently
/// alter the edit.
/// </para>
/// </remarks>
public static class EditTimelineSeeder
{
    /// <summary>
    /// The fixed v1 track label → (v2 track name, track kind) mapping. The label also seeds the
    /// deterministic <see cref="Track.Id"/> (see the class remarks).
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Name, TrackKind Kind)> TrackMap =
        new(StringComparer.Ordinal)
        {
            ["video"] = ("V1", "Video", TrackKind.Video),
            ["inserts"] = ("V2", "Inserts", TrackKind.Video),
            ["graphics"] = ("V3", "Graphics", TrackKind.Video),
            ["dialogue"] = ("S1", "Captions", TrackKind.Video),
            ["sfx"] = ("A2", "SFX", TrackKind.Audio),
            ["music"] = ("A3", "Music", TrackKind.Audio)
        };

    /// <summary>
    /// Frame rate used when the manifest's own <c>OutputVideo.Fps</c> is unusable (zero, negative,
    /// NaN or infinite) — the same 30 fps fallback <c>TimelineManifestBuilder</c> already applies
    /// when it writes the manifest in the first place.
    /// </summary>
    public const double FallbackFps = 30.0;

    /// <summary>
    /// Sample rate written to <see cref="EditTimelineSettings.SampleRate"/>. v1 manifests carry no
    /// sample rate, and every audio path in the pipeline is 48 kHz.
    /// </summary>
    public const int DefaultSampleRate = 48000;

    /// <summary>The track label of the camera-audio companion track (see <see cref="Seed"/>).</summary>
    public const string CameraAudioLabel = "A1";

    /// <summary>The camera-audio companion track's name.</summary>
    public const string CameraAudioName = "Camera audio";

    /// <summary>
    /// The well-known frame rates, as exact rationals, in the order they are matched. Only these
    /// are exact: 23.976/29.97/59.94 are the 1001-denominator NTSC rates, and a double can never
    /// represent them.
    /// </summary>
    private static readonly (double Fps, int Num, int Den)[] WellKnownRates =
    {
        (23.976, 24000, 1001),
        (24.0, 24, 1),
        (25.0, 25, 1),
        (29.97, 30000, 1001),
        (30.0, 30, 1),
        (50.0, 50, 1),
        (59.94, 60000, 1001),
        (60.0, 60, 1)
    };

    /// <summary>
    /// Tolerance for matching a manifest's <c>Fps</c> against <see cref="WellKnownRates"/>. A
    /// rounded manifest rate (e.g. 23.98 for 23.976) still lands on its exact rational.
    /// </summary>
    private const double RateMatchTolerance = 0.01;

    /// <summary>
    /// Seeds an <see cref="EditTimeline"/> from a v1 <see cref="TimelineManifest"/>.
    /// </summary>
    /// <param name="manifest">The compiled render's manifest. Its tracks are mapped in manifest order.</param>
    /// <param name="newTimelineId">The id of the NEW v2 timeline (<see cref="EditTimeline.ProjectId"/> comes from the manifest).</param>
    /// <param name="resolveAsset">
    /// Resolves an asset identifier to a v2 <see cref="AssetRef"/>, or <c>null</c> when it resolves
    /// to nothing this project owns. The argument is the identifier <b>as the manifest carries
    /// it</b>, and the two kinds differ: for <c>video</c> cuts and <c>inserts</c> it is a storage
    /// key (a source's <c>StorageKey</c>, an insert's <c>AssetStorageKey</c>), while for
    /// <c>sfx</c> and <c>music</c> it is a project-file name (<c>ClipName</c>, <c>TrackName</c>).
    /// The caller owns that distinction, not the seeder: a manifest references audio by name and
    /// video by key. A <c>null</c> result skips the item (see the class remarks).
    /// </param>
    /// <param name="sourceHasAudio">
    /// Whether a source (by its <c>StorageKey</c>) carries an audio stream, as its probe measured it.
    /// Each <c>video</c> cut of a source with sound gets its camera audio as a companion
    /// <see cref="MediaClip"/> on the <c>A1</c> "Camera audio" track, placed right under <c>V1</c>:
    /// same asset, same record and source range, no link between the two. <c>null</c> (or a source
    /// it answers <c>false</c> for) adds no companion: sound is never assumed.
    /// </param>
    /// <returns>The seeded document, with <see cref="EditTimeline.Seed"/> set from the manifest's execution id and version.</returns>
    public static EditTimeline Seed(
        TimelineManifest manifest,
        Guid newTimelineId,
        Func<string, AssetRef?> resolveAsset,
        Func<string, bool>? sourceHasAudio = null,
        Func<TimelineSource, AssetRef?>? resolveSourceIdentity = null,
        Func<AssetRef, string?>? inputProfileFor = null) =>
        SeedWithReport(manifest, newTimelineId, resolveAsset, sourceHasAudio, resolveSourceIdentity, inputProfileFor).Document;

    /// <summary>
    /// <see cref="Seed"/> plus the honest list of manifest items that could not be placed because
    /// their asset did not resolve. <paramref name="resolveSourceIdentity"/> is called only for a
    /// source that carries <see cref="TimelineSource.ProjectFileId"/>/<see cref="TimelineSource.StepResultId"/>
    /// and must return a ref ONLY when the caller verified the project owns it; its answer wins over
    /// the storage-key lookup, which stays the fallback for manifests written before the identity existed.
    /// </summary>
    public static SeededTimeline SeedWithReport(
        TimelineManifest manifest,
        Guid newTimelineId,
        Func<string, AssetRef?> resolveAsset,
        Func<string, bool>? sourceHasAudio = null,
        Func<TimelineSource, AssetRef?>? resolveSourceIdentity = null,
        Func<AssetRef, string?>? inputProfileFor = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resolveAsset);

        double fps = EffectiveFps(manifest.OutputVideo.Fps);
        var context = new SeedContext(manifest, fps, resolveAsset, sourceHasAudio, resolveSourceIdentity, inputProfileFor);
        CollectCensorWindows(manifest, context);

        var tracks = new List<Track>();
        foreach (TimelineTrack manifestTrack in manifest.Tracks ?? [])
        {
            if (manifestTrack is null || !TrackMap.TryGetValue(manifestTrack.Type ?? string.Empty, out var mapped))
            {
                // An unknown track type is a v1 addition this seeder does not know how to
                // represent; dropping it beats guessing a kind for it.
                continue;
            }

            IReadOnlyList<Item> items = MapTrackItems(manifestTrack, context);
            if (items.Count == 0)
            {
                // An empty v2 track carries no information the v1 manifest lacked, and the v1
                // builder already omits empty tracks.
                continue;
            }

            tracks.Add(new Track(
                Id: TrackId(mapped.Label),
                Kind: mapped.Kind,
                Name: mapped.Name,
                Color: null,
                Height: 100,
                Locked: false,
                Enabled: true,
                Mute: false,
                Solo: false,
                BusId: null,
                Items: items));

            if (mapped.Label == "V1" && context.CameraAudio.Count > 0)
            {
                tracks.Add(new Track(
                    Id: TrackId(CameraAudioLabel),
                    Kind: TrackKind.Audio,
                    Name: CameraAudioName,
                    Color: null,
                    Height: 100,
                    Locked: false,
                    Enabled: true,
                    Mute: false,
                    Solo: false,
                    BusId: null,
                    Items: context.CameraAudio.Select(companion => (Item)companion.Clip).ToList()));
            }
        }

        var document = new EditTimeline(
            id: newTimelineId,
            projectId: manifest.ProjectId,
            name: manifest.OutputVideo.StorageKey ?? string.Empty,
            settings: new EditTimelineSettings(
                Width: manifest.OutputVideo.Width,
                Height: manifest.OutputVideo.Height,
                Rate: RateFromFps(fps),
                SampleRate: DefaultSampleRate,
                PixelAspect: new Rational(1, 1),
                AspectPolicy: AspectPolicy.Fit,
                StartTimecodeFrames: 0),
            tracks: tracks,
            markers: context.Markers,
            inOut: null,
            seed: new EditTimelineSeed(manifest.ExecutionId, manifest.Version));
        return new SeededTimeline(document, context.Unplaced);
    }

    /// <summary>
    /// The fixed v1 track label (<c>V1</c>, <c>S1</c>, <c>A3</c>, …) for a manifest track type, or
    /// <c>null</c> when the seeder maps no track for that type.
    /// </summary>
    /// <param name="manifestTrackType">A <see cref="TimelineTrack.Type"/> value.</param>
    public static string? TrackLabelFor(string? manifestTrackType) =>
        manifestTrackType is not null && TrackMap.TryGetValue(manifestTrackType, out var mapped)
            ? mapped.Label
            : null;

    /// <summary>
    /// The deterministic <see cref="Track.Id"/> for a fixed track label: a name-based GUID, so the
    /// same label yields the same id in every process and every run.
    /// </summary>
    /// <param name="label">A label returned by <see cref="TrackLabelFor"/>.</param>
    public static Guid TrackId(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"reelbolt.edittimeline.track:{label}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// The manifest's frame rate when it is usable, else <see cref="FallbackFps"/>.
    /// </summary>
    internal static double EffectiveFps(double fps) =>
        double.IsFinite(fps) && fps > 0 ? fps : FallbackFps;

    /// <summary>
    /// The exact rational for a frame rate, matched against <see cref="WellKnownRates"/> within
    /// <see cref="RateMatchTolerance"/>.
    /// </summary>
    /// <remarks>
    /// <b>Imprecise fallback:</b> an unmatched rate becomes <c>round(fps × 1000) / 1000</c>, which
    /// represents every rate a manifest can plausibly carry to within a thousandth of a frame per
    /// second. It is deliberately NOT a continued-fraction or best-rational approximation: those
    /// would hand back an EXACT-looking rational (e.g. 2997/100 for 29.97) whose precision is
    /// implied rather than measured, and a consumer cannot tell it apart from a real rate. The
    /// caller can recognise the fallback — its denominator is always 1000.
    /// </remarks>
    internal static Rational RateFromFps(double fps)
    {
        if (double.IsFinite(fps) && fps > 0)
        {
            foreach ((double known, int num, int den) in WellKnownRates)
            {
                if (Math.Abs(fps - known) <= RateMatchTolerance)
                {
                    return new Rational(num, den);
                }
            }

            return new Rational((int)Math.Round(fps * 1000.0, MidpointRounding.AwayFromZero), 1000);
        }

        return new Rational((int)FallbackFps, 1);
    }

    /// <summary>
    /// <c>(long)Math.Round(seconds × fps)</c> — the one seconds→frames rule, used for every record
    /// point and every keyframe frame in this seeder.
    /// </summary>
    internal static long Frames(double seconds, double fps) =>
        double.IsFinite(seconds) ? (long)Math.Round(seconds * fps, MidpointRounding.AwayFromZero) : 0L;

    /// <summary>
    /// Maps one manifest track's items, in manifest order, then splices in a
    /// <see cref="Transition"/> after every item whose <c>TransitionOut</c> a non-zero-length seam
    /// can be built from.
    /// </summary>
    private static IReadOnlyList<Item> MapTrackItems(TimelineTrack manifestTrack, SeedContext context)
    {
        var mapped = new List<Item>();
        var mappedSource = new List<TimelineItem>();

        foreach (TimelineItem item in manifestTrack.Items ?? [])
        {
            if (item is null)
            {
                continue;
            }

            Item? seed = manifestTrack.Type switch
            {
                "video" => MapVideoItem(item, context),
                "inserts" => MapInsertItem(item, context),
                "graphics" => MapGraphicsItem(item, context),
                "dialogue" => MapDialogueItem(item, context),
                "sfx" => MapSfxItem(item, context),
                "music" => MapMusicItem(item, context),
                _ => null
            };

            if (seed is not null)
            {
                mapped.Add(seed);
                mappedSource.Add(item);
            }
        }

        if (manifestTrack.Type == "dialogue")
        {
            TrimOverlappingCaptions(mapped, mappedSource, manifestTrack.Type, context);
        }
        else
        {
            ResolveTrackOverlaps(mapped, mappedSource, manifestTrack.Type, context);
        }

        if (context.CensorWindows.Count > 0 && manifestTrack.Type is "video" or "inserts" or "graphics")
        {
            // V1 is always original footage. On the overlay tracks only an item whose asset IS one of the manifest's
            // original sources plays original pixels; a rendered overlay (insert/graphic/title media) is not the censored
            // footage and stays.
            WithholdCensoredPicture(mapped, mappedSource, context, manifestTrack.Type == "video");
        }

        return InsertTransitions(mapped, mappedSource, context);
    }

    /// <summary>
    /// Reads the manifest's <c>censor</c> track into frame windows and reports each censor item as unplaced.
    /// A window is widened outward the way <c>LineageSeeder.WidenCensorWindows</c> widens one (start rounds
    /// down; end gets the manifest's half-millisecond rounding plus one frame), so a frame the compile
    /// touched is never left showing. The plain seed carries no obscuring itself, so the picture in a
    /// window is withheld instead (see <see cref="WithholdCensoredPicture"/>).
    /// </summary>
    private static void CollectCensorWindows(TimelineManifest manifest, SeedContext context)
    {
        const double Epsilon = 1e-6;
        const double ManifestRoundingSec = 0.0005;
        foreach (TimelineTrack track in manifest.Tracks ?? [])
        {
            if (track is null || track.Type != "censor")
            {
                continue;
            }

            foreach (TimelineItem item in track.Items ?? [])
            {
                if (item is null)
                {
                    continue;
                }

                double start = item.TimelineStartSec;
                double end = Math.Max(item.TimelineEndSec ?? (start + (item.DurationSec ?? 0)), start);
                long inFrame = Math.Max(0, (long)Math.Floor((start * context.Fps) + Epsilon));
                long outFrame = (long)Math.Floor(((end + ManifestRoundingSec) * context.Fps) + Epsilon) + 1;
                context.CensorWindows.Add((inFrame, Math.Max(outFrame, inFrame + 1)));
                context.ReportUnplaced("censor", item, UnplacedReasons.CensoredStretchWithheld);
            }
        }
    }

    /// <summary>
    /// Cuts every window of <see cref="SeedContext.CensorWindows"/> out of the seeded picture: a clip is
    /// trimmed or split so no original footage sits in a censored window (a hole is left). The source
    /// in-point advances with a trimmed start so the remaining footage still lines up. A trimmed clip
    /// loses its outgoing transition (its seam moved). Camera audio is left as the manifest has it.
    /// </summary>
    private static void WithholdCensoredPicture(List<Item> mapped, List<TimelineItem> mappedSource, SeedContext context, bool allOriginal)
    {
        bool PlaysOriginal(AssetRef asset) => allOriginal || context.SourceAssets.Contains(asset);

        var windows = context.CensorWindows.OrderBy(w => w.In).ToList();
        var resultItems = new List<Item>(mapped.Count);
        var resultSource = new List<TimelineItem>(mapped.Count);
        for (int i = 0; i < mapped.Count; i++)
        {
            if (!allOriginal && mapped[i] is InsertClip { AssetRef: { } insertAsset } insertClip && PlaysOriginal(insertAsset) &&
                windows.Any(w => insertClip.RecordIn < w.Out && insertClip.RecordOut > w.In))
            {
                // An insert that plays original source pixels over a censored window cannot be trimmed here: drop it, and say so.
                context.ReportUnplaced("inserts", mappedSource[i], UnplacedReasons.CensoredStretchWithheld);
                continue;
            }

            if (mapped[i] is not MediaClip clip || !PlaysOriginal(clip.AssetRef))
            {
                resultItems.Add(mapped[i]);
                resultSource.Add(mappedSource[i]);
                continue;
            }

            var pieces = new List<(long In, long Out)> { (clip.RecordIn, clip.RecordOut) };
            foreach ((long wIn, long wOut) in windows)
            {
                var next = new List<(long In, long Out)>(pieces.Count + 1);
                foreach ((long pIn, long pOut) in pieces)
                {
                    if (pOut <= wIn || pIn >= wOut)
                    {
                        next.Add((pIn, pOut));
                        continue;
                    }

                    if (pIn < wIn)
                    {
                        next.Add((pIn, wIn));
                    }

                    if (pOut > wOut)
                    {
                        next.Add((wOut, pOut));
                    }
                }

                pieces = next;
            }

            if (pieces.Count == 1 && pieces[0] == (clip.RecordIn, clip.RecordOut))
            {
                resultItems.Add(clip);
                resultSource.Add(mappedSource[i]);
                continue;
            }

            for (int n = 0; n < pieces.Count; n++)
            {
                (long pIn, long pOut) = pieces[n];
                resultItems.Add(new MediaClip(
                    n == 0 && pIn == clip.RecordIn ? clip.Id : DeterministicItemId($"censor-split:{clip.Id}:{pIn}"),
                    clip.Name, pIn, pOut, clip.AssetRef, clip.SourceIn + (pIn - clip.RecordIn), clip.PlaybackRate,
                    clip.Enabled, clip.Provenance, clip.GainDb, clip.Transform, clip.Crop, clip.Reverse, clip.Hold,
                    clip.FadeInFrames, clip.FadeOutFrames, clip.Grade, clip.Mask, clip.Keyer, clip.Effects,
                    clip.Stabilize, clip.Reframe, clip.RetimeQuality, clip.CornerPin, clip.ChannelMap, clip.LinkId,
                    clip.RegionEffects, clip.InputProfile));
                resultSource.Add(mappedSource[i] with { TransitionOut = null });
            }
        }

        mapped.Clear();
        mapped.AddRange(resultItems);
        mappedSource.Clear();
        mappedSource.AddRange(resultSource);
    }

    /// <summary>
    /// Resolves consecutive overlaps on a seeded track in manifest order. The compile's transition plan
    /// deliberately overlaps a treated crossfade seam, but the v2 document cannot express two clips over
    /// the same span (tracks must not overlap and a <see cref="Transition"/> only fits a gap), so the
    /// outgoing item keeps the overlapping window and the incoming one starts where it ends: a hard cut
    /// on the original footage, with one marker per softened seam frame. A <see cref="MediaClip"/>'s
    /// source in-point advances by the trimmed frames so it still shows the right footage; an item wholly
    /// inside the previous range cannot be shown at all and is reported instead of silently dropped.
    /// </summary>
    private static void ResolveTrackOverlaps(
        List<Item> mapped,
        List<TimelineItem> mappedSource,
        string type,
        SeedContext context)
    {
        long? coveredEnd = null;
        for (int i = 0; i < mapped.Count; i++)
        {
            Item item = mapped[i];
            if (coveredEnd is { } end && item.RecordIn < end)
            {
                context.ReportSeam(end);
                if (item.RecordOut <= end)
                {
                    context.ReportUnplaced(type, mappedSource[i], UnplacedReasons.SeamOverlapDropped);
                    if (item is MediaClip dropped)
                    {
                        context.CameraAudio.RemoveAll(companion => companion.VideoId == dropped.Id);
                    }

                    mapped.RemoveAt(i);
                    mappedSource.RemoveAt(i);
                    i--;
                    continue;
                }

                long trim = end - item.RecordIn;
                Item trimmed = item switch
                {
                    MediaClip clip => WithRange(clip, end, clip.SourceIn + trim),
                    InsertClip insert => new InsertClip(
                        insert.Id, insert.Name, end, insert.RecordOut, insert.AssetRef, insert.Quad,
                        insert.Enabled, insert.Provenance, insert.Matte),
                    TitleClip title => new TitleClip(
                        title.Id, title.Name, end, title.RecordOut, title.Text, title.FontSize,
                        title.FontFamily, title.Color, title.Enabled, title.Provenance, title.Template,
                        title.Position, title.Transform),
                    _ => item
                };
                mapped[i] = trimmed;

                // The camera-audio companion of a video cut is the same source window; trim it with it.
                if (item is MediaClip video)
                {
                    int companion = context.CameraAudio.FindIndex(pair => pair.VideoId == video.Id);
                    if (companion >= 0)
                    {
                        MediaClip audio = context.CameraAudio[companion].Clip;
                        context.CameraAudio[companion] = (video.Id, WithRange(audio, end, audio.SourceIn + trim));
                    }
                }

                item = trimmed;
            }

            coveredEnd = coveredEnd is { } previous ? Math.Max(previous, item.RecordOut) : item.RecordOut;
        }
    }

    /// <summary>
    /// A <see cref="MediaClip"/>'s fields are read-only on the record, so a new record range (and the
    /// matching source in-point) is written by rebuilding it field for field.
    /// </summary>
    private static MediaClip WithRange(MediaClip clip, long recordIn, long sourceIn) =>
        new(clip.Id, clip.Name, recordIn, clip.RecordOut, clip.AssetRef, sourceIn, clip.PlaybackRate,
            clip.Enabled, clip.Provenance, clip.GainDb, clip.Transform, clip.Crop, clip.Reverse, clip.Hold,
            clip.FadeInFrames, clip.FadeOutFrames, clip.Grade, clip.Mask, clip.Keyer, clip.Effects,
            clip.Stabilize, clip.Reframe, clip.RetimeQuality, clip.CornerPin, clip.ChannelMap, clip.LinkId,
            clip.RegionEffects, clip.InputProfile, clip.SourceRotation, clip.BlendMode);

    /// <summary>
    /// A track's items may not overlap, but transcript cues do (a cue mapped across cuts, or ASR timing). Sorts by start,
    /// trims a cue to the next cue's start, and drops a cue nothing remains of, reporting it as unplaced so no words vanish silently.
    /// </summary>
    private static void TrimOverlappingCaptions(List<Item> mapped, List<TimelineItem> mappedSource, string type, SeedContext context)
    {
        List<(Item Item, TimelineItem Source)> ordered = mapped.Zip(mappedSource).OrderBy(p => p.First.RecordIn).ToList();
        List<(Item Item, TimelineItem Source)> kept = [];
        foreach ((Item item, TimelineItem source) in ordered)
        {
            if (kept.Count > 0 && kept[^1].Item is Caption prev && prev.RecordOut > item.RecordIn)
            {
                if (prev.RecordIn >= item.RecordIn)
                {
                    context.ReportUnplaced(type, kept[^1].Source, "dialogue_overlap_dropped");
                    kept.RemoveAt(kept.Count - 1);
                }
                else
                {
                    kept[^1] = (new Caption(prev.Id, prev.Name, prev.RecordIn, item.RecordIn, prev.Text, prev.Language, prev.Enabled, prev.Provenance, prev.Look, prev.Position), kept[^1].Source);
                }
            }

            kept.Add((item, source));
        }

        mapped.Clear();
        mappedSource.Clear();
        foreach ((Item item, TimelineItem source) in kept)
        {
            mapped.Add(item);
            mappedSource.Add(source);
        }
    }

    /// <summary>
    /// Splices a <see cref="Transition"/> between an item carrying a <c>TransitionOut</c> and its
    /// mapped successor. Skipped unless the two frames strictly increase, because the T-013
    /// validator requires a transition both to sit exactly at the boundary (<c>RecordIn</c> = the
    /// predecessor's <c>RecordOut</c>) and to have a positive extent.
    /// </summary>
    private static IReadOnlyList<Item> InsertTransitions(
        List<Item> mapped,
        List<TimelineItem> mappedSource,
        SeedContext context)
    {
        var withTransitions = new List<Item>(mapped.Count);

        for (int i = 0; i < mapped.Count; i++)
        {
            withTransitions.Add(mapped[i]);

            string? transitionOut = mappedSource[i].TransitionOut;
            if (string.IsNullOrWhiteSpace(transitionOut) || i + 1 >= mapped.Count)
            {
                continue;
            }

            long boundary = mapped[i].RecordOut;
            if (boundary <= mapped[i].RecordIn || boundary >= mapped[i + 1].RecordIn)
            {
                continue;
            }

            // A transition across a censored stretch (a clip between them was withheld) would blend picture back in.
            long nextIn = mapped[i + 1].RecordIn;
            if (context.CensorWindows.Any(w => boundary < w.Out && nextIn > w.In))
            {
                continue;
            }

            withTransitions.Add(new Transition(
                Id: DeterministicItemId($"transition:{i}:{transitionOut}"),
                Name: transitionOut,
                RecordIn: boundary,
                RecordOut: mapped[i + 1].RecordIn,
                TransitionType: transitionOut,
                Provenance: new Provenance(ProvenanceOrigin.Generated)));
        }

        return withTransitions;
    }

    /// <summary>
    /// One <c>video</c> item → one <see cref="MediaClip"/> on <c>V1</c>, plus, when the caller's
    /// <c>sourceHasAudio</c> says the source has sound, its camera audio as a companion clip queued
    /// for <c>A1</c>. Sound is never assumed: an unknown or silent source gets no companion.
    /// </summary>
    private static Item? MapVideoItem(TimelineItem item, SeedContext context)
    {
        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        if (item.SourceIndex is not { } sourceIndex ||
            context.Manifest.Sources is not { } sources ||
            sourceIndex < 0 || sourceIndex >= sources.Count)
        {
            return null;
        }

        AssetRef? assetRef = context.ResolveSource(sources[sourceIndex]);
        if (assetRef is null)
        {
            context.ReportUnplaced("video", item, UnplacedReasons.SourceUnresolved);
            return null;
        }

        // SourceStartSec is relative to an author-trimmed window; the clip points at the original asset.
        long sourceIn = Frames((item.SourceStartSec ?? 0.0) + (sources[sourceIndex].TrimInSec ?? 0.0), context.Fps);
        Guid id = DeterministicItemId($"video:{item.Id}:{sourceIndex}:{recordIn}");
        if (context.HasAudio(sources[sourceIndex].StorageKey))
        {
            context.CameraAudio.Add((id, new MediaClip(
                Id: DeterministicItemId($"camera-audio:{item.Id}:{sourceIndex}:{recordIn}"),
                Name: ItemName(item),
                RecordIn: recordIn,
                RecordOut: recordOut,
                AssetRef: assetRef,
                SourceIn: sourceIn,
                Provenance: AgentProvenance(item))));
        }

        return new MediaClip(
            Id: id,
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: assetRef,
            SourceIn: sourceIn,
            Provenance: AgentProvenance(item),
            InputProfile: context.InputProfileFor(assetRef));
    }

    /// <summary>
    /// One <c>inserts</c> item → one <see cref="InsertClip"/> on <c>V2</c>, corner-pinned by the
    /// tracked quad. Provenance is <see cref="ProvenanceOrigin.Analyzed"/> with no agent: the quad
    /// comes from deterministic chroma tracking, never from a model.
    /// </summary>
    private static Item? MapInsertItem(TimelineItem item, SeedContext context)
    {
        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        AssetRef? assetRef = context.Resolve(item.AssetStorageKey);
        if (assetRef is null)
        {
            context.ReportUnplaced("inserts", item, UnplacedReasons.AssetUnresolved);
            return null;
        }

        return new InsertClip(
            Id: DeterministicItemId($"insert:{item.Id}:{recordIn}"),
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: assetRef,
            Quad: BuildQuad(item.QuadKeyframes, context),
            Provenance: new Provenance(ProvenanceOrigin.Analyzed));
    }

    /// <summary>
    /// One <c>graphics</c> item → a <see cref="TitleClip"/> when it carries text, else a
    /// <see cref="MediaClip"/> over its rendered asset. A whitespace-only <c>Text</c> counts as "no
    /// text": it would sanitize to nothing and the validator would reject the caption outright.
    /// </summary>
    private static Item? MapGraphicsItem(TimelineItem item, SeedContext context)
    {
        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.Text))
        {
            return new TitleClip(
                Id: DeterministicItemId($"graphics-text:{item.Id}:{recordIn}"),
                Name: ItemName(item),
                RecordIn: recordIn,
                RecordOut: recordOut,
                Text: item.Text!,
                Provenance: AgentProvenance(item));
        }

        AssetRef? assetRef = context.Resolve(item.AssetStorageKey);
        if (assetRef is null)
        {
            context.ReportUnplaced("graphics", item, UnplacedReasons.AssetUnresolved);
            return null;
        }

        return new MediaClip(
            Id: DeterministicItemId($"graphics-asset:{item.Id}:{recordIn}"),
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: assetRef,
            Provenance: AgentProvenance(item));
    }

    /// <summary>
    /// One <c>dialogue</c> item → one <see cref="Caption"/> on <c>S1</c>. VERIFIED against
    /// <c>TimelineManifestBuilder.BuildDialogueTrack</c>: it populates <c>Text</c> from
    /// <c>segment.Text</c> (or <c>word.Text</c> when there are no segments) alongside the mapped
    /// start/end seconds, so an item reaching this method has a real transcript string or none at
    /// all. Provenance is <see cref="ProvenanceOrigin.Analyzed"/> with no agent: the text is ASR
    /// output, not an agent decision.
    /// </summary>
    private static Item? MapDialogueItem(TimelineItem item, SeedContext context)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return null;
        }

        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        return new Caption(
            Id: DeterministicItemId($"dialogue:{item.Id}:{recordIn}"),
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            Text: item.Text!,
            Provenance: new Provenance(ProvenanceOrigin.Analyzed));
    }

    /// <summary>
    /// One <c>sfx</c> item → one <see cref="MediaClip"/> on <c>A2</c>, resolving its asset by
    /// project-file name (<c>ClipName</c>, falling back to the <c>SfxId</c> the manifest also
    /// carries), recording that <c>SfxId</c> as the offered id, and carrying the cue's own
    /// <c>GainDb</c> as the clip's static gain (null when the manifest has none — a missing gain is
    /// never defaulted to 0 dB).
    /// </summary>
    private static Item? MapSfxItem(TimelineItem item, SeedContext context)
    {
        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        string? assetName = !string.IsNullOrWhiteSpace(item.ClipName) ? item.ClipName : item.SfxId;
        AssetRef? assetRef = context.Resolve(assetName);
        if (assetRef is null)
        {
            context.ReportUnplaced("sfx", item, UnplacedReasons.AssetUnresolved);
            return null;
        }

        return new MediaClip(
            Id: DeterministicItemId($"sfx:{item.Id}:{recordIn}"),
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: assetRef,
            Provenance: AgentProvenance(item, item.SfxId),
            GainDb: BuildGainDb(item.GainDb, envelope: null, context.Fps));
    }

    /// <summary>
    /// The music bed → at most one <see cref="MediaClip"/> on <c>A3</c>, resolving its asset by
    /// project-file name (<c>TrackName</c>), recording the <c>TrackId</c> as the offered id, and
    /// carrying the bed's <c>BedDbfs</c> as the clip's static gain alongside its
    /// <c>DuckingEnvelope</c> as the gain automation. <c>DuckedDbfs</c> is deliberately not mapped:
    /// it is the level a duck is aiming at, already described by the envelope's own points, and
    /// duplicating it would invent a second source of truth.
    /// Later music items are ignored: v1 emits exactly one, and a second bed would be a different
    /// capability rather than a mapping.
    /// </summary>
    private static Item? MapMusicItem(TimelineItem item, SeedContext context)
    {
        if (context.MusicMapped)
        {
            return null;
        }

        if (!TryRecordRange(item, context, out long recordIn, out long recordOut))
        {
            return null;
        }

        AssetRef? assetRef = context.Resolve(item.TrackName);
        if (assetRef is null)
        {
            context.ReportUnplaced("music", item, UnplacedReasons.AssetUnresolved);
            return null;
        }

        context.MusicMapped = true;

        return new MediaClip(
            Id: DeterministicItemId($"music:{item.Id}"),
            Name: ItemName(item),
            RecordIn: recordIn,
            RecordOut: recordOut,
            AssetRef: assetRef,
            Provenance: AgentProvenance(item, item.TrackId),
            GainDb: BuildGainDb(item.BedDbfs, item.DuckingEnvelope, context.Fps));
    }

    /// <summary>
    /// The clip gain for an audio item, or <c>null</c> when the manifest carries neither a static
    /// level nor an envelope. The static level and the automation are independent inputs: neither is
    /// derived from the other, and an absent one stays absent.
    /// </summary>
    /// <param name="staticDb">The manifest's own clip level (<c>GainDb</c>, <c>BedDbfs</c>), if any.</param>
    /// <param name="envelope">The manifest's ducking envelope, if any.</param>
    /// <param name="fps">The frame rate every envelope frame is converted with.</param>
    private static Keyframed<double>? BuildGainDb(
        double? staticDb,
        IReadOnlyList<TimelineDuckingPoint>? envelope,
        double fps)
    {
        IReadOnlyList<Keyframe<double>>? keys = BuildGainEnvelope(envelope, fps);
        if (staticDb is null && keys is null)
        {
            return null;
        }

        // Keyframed<double>.Value is a plain double and cannot express "absent", so an envelope
        // with no static level keeps the identity 0 dB — which the envelope overrides from its
        // first key onward. Emitting that is strictly closer to the manifest than dropping the
        // automation would be.
        return new Keyframed<double>(Value: staticDb ?? 0.0, Keys: keys);
    }

    /// <summary>
    /// Builds the gain automation from a music bed's ducking envelope, or <c>null</c> when the
    /// manifest carries none. Every point is <see cref="InterpKind.Linear"/>: v1 records envelope
    /// points only, and carries no per-point interpolation to copy.
    /// </summary>
    private static IReadOnlyList<Keyframe<double>>? BuildGainEnvelope(
        IReadOnlyList<TimelineDuckingPoint>? envelope,
        double fps)
    {
        if (envelope is not { Count: > 0 })
        {
            return null;
        }

        var keys = new List<Keyframe<double>>(envelope.Count);
        foreach (TimelineDuckingPoint point in envelope)
        {
            if (point is null)
            {
                continue;
            }

            keys.Add(new Keyframe<double>(
                Frame: Frames(point.TimeSec, fps),
                Value: point.GainDb,
                Interp: InterpKind.Linear));
        }

        return keys.Count == 0 ? null : keys;
    }

    /// <summary>
    /// Builds the corner-pin animation from the tracked quad keyframes, or <c>null</c> when the
    /// manifest carries none. v1 records corner positions only, so every keyframe is
    /// <see cref="InterpKind.Linear"/>.
    /// </summary>
    private static Keyframed<QuadCorners>? BuildQuad(
        IReadOnlyList<VideoInsertQuadKeyframe>? keyframes,
        SeedContext context)
    {
        if (keyframes is not { Count: > 0 })
        {
            return null;
        }

        var keys = new List<Keyframe<QuadCorners>>(keyframes.Count);
        foreach (VideoInsertQuadKeyframe keyframe in keyframes)
        {
            if (keyframe is null)
            {
                continue;
            }

            // v1 corner order is 0 = top-left, 1 = top-right, 2 = bottom-left, 3 = bottom-right;
            // QuadCorners orders them top-left, top-right, bottom-right, bottom-left.
            keys.Add(new Keyframe<QuadCorners>(
                Frame: Frames(keyframe.TimeSec, context.Fps),
                Value: new QuadCorners(
                    new QuadPoint(keyframe.X0, keyframe.Y0),
                    new QuadPoint(keyframe.X1, keyframe.Y1),
                    new QuadPoint(keyframe.X3, keyframe.Y3),
                    new QuadPoint(keyframe.X2, keyframe.Y2)),
                Interp: InterpKind.Linear));
        }

        return keys.Count == 0 ? null : new Keyframed<QuadCorners>(Value: null, Keys: keys);
    }

    /// <summary>
    /// The provenance an agent-authored item carries: the manifest's own
    /// <c>AgentName</c>/<c>ProviderName</c>/<c>StepResultId</c> copied verbatim, each <c>null</c>
    /// when the source had none. Ids are filled by v2-native creation paths; a v1 seed can only
    /// supply the names the manifest actually recorded, and never resolves one from the other.
    /// </summary>
    /// <param name="item">The manifest item the provenance is copied from.</param>
    /// <param name="offeredId">
    /// The opaque analysis/decision id the item was chosen from, when the mapping has one (the
    /// SFX <c>SfxId</c>, the music bed's <c>TrackId</c>).
    /// </param>
    private static Provenance AgentProvenance(TimelineItem item, string? offeredId = null) =>
        new(
            Origin: ProvenanceOrigin.Generated,
            StepResultId: item.StepResultId,
            OfferedId: offeredId,
            Rationale: item.AgentRationale,
            AgentName: item.AgentName,
            ProviderName: item.ProviderName);

    /// <summary>
    /// The item's record range in frames, or <c>false</c> when the manifest does not carry one
    /// that maps to a positive extent — a missing end time is never invented.
    /// </summary>
    private static bool TryRecordRange(
        TimelineItem item,
        SeedContext context,
        out long recordIn,
        out long recordOut)
    {
        recordIn = 0;
        recordOut = 0;

        if (item.TimelineEndSec is not { } end)
        {
            return false;
        }

        recordIn = Frames(item.TimelineStartSec, context.Fps);
        recordOut = Frames(end, context.Fps);
        return recordOut > recordIn;
    }

    /// <summary>
    /// The item's display name: the manifest's own human-readable label, else the id it carries,
    /// else empty. Never a synthesized name.
    /// </summary>
    private static string ItemName(TimelineItem item) =>
        !string.IsNullOrWhiteSpace(item.Label) ? item.Label! : item.Id ?? string.Empty;

    /// <summary>
    /// A deterministic v2 item id derived from the manifest identity of the item it came from, so
    /// that re-seeding the same manifest yields the same document.
    /// </summary>
    internal static Guid DeterministicItemId(string identity)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"reelbolt.edittimeline.item:{identity}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Per-call state: the manifest, the frame rate every conversion uses, the fingerprint cache
    /// for a single asset identifier, and whether the (single) music bed has been mapped.
    /// </summary>
    private sealed class SeedContext(
        TimelineManifest manifest,
        double fps,
        Func<string, AssetRef?> resolveAsset,
        Func<string, bool>? sourceHasAudio,
        Func<TimelineSource, AssetRef?>? resolveSourceIdentity,
        Func<AssetRef, string?>? inputProfileFor = null)
    {
        /// <summary>The camera profile a clip of this asset is seeded with (a High detector hint), or null for none.</summary>
        public string? InputProfileFor(AssetRef asset) => inputProfileFor?.Invoke(asset) is { Length: > 0 } p ? p : null;

        /// <summary>
        /// Cache of <c>identifier → resolved ref</c> (including a cached <c>null</c>), so one
        /// shared source is resolved once per seed. <c>null</c> is deliberately cached: a name the
        /// project does not own must not be re-resolved for every item that references it.
        /// </summary>
        private readonly Dictionary<string, AssetRef?> _resolved = new(StringComparer.Ordinal);

        /// <summary>
        /// The manifest being mapped.
        /// </summary>
        public TimelineManifest Manifest { get; } = manifest;

        /// <summary>
        /// The frame rate (<c>manifest.OutputVideo.Fps</c>, guarded) every seconds→frames
        /// conversion uses.
        /// </summary>
        public double Fps { get; } = fps;

        /// <summary>
        /// True once the music bed has been mapped, so at most one music <c>MediaClip</c> is emitted.
        /// </summary>
        public bool MusicMapped { get; set; }

        /// <summary>
        /// The camera-audio companions of the mapped <c>video</c> cuts, keyed by their video clip's id so
        /// a seam trim on V1 can trim the companion on A1 with it.
        /// </summary>
        public List<(Guid VideoId, MediaClip Clip)> CameraAudio { get; } = [];

        /// <summary>
        /// One "Softened seam" marker per frame where a trim turned a compile crossfade into a hard cut,
        /// deduplicated by frame and in first-seen order so the seed is deterministic.
        /// </summary>
        public List<Marker> Markers { get; } = [];

        private readonly HashSet<long> _seamFrames = [];

        /// <summary>Items whose asset did not resolve, in manifest order.</summary>
        public List<UnplacedItem> Unplaced { get; } = [];

        /// <summary>Frame windows [In, Out) of the manifest's censor track, already widened outward.</summary>
        public List<(long In, long Out)> CensorWindows { get; } = [];

        /// <summary>Every asset a manifest source resolves to: the original footage, as opposed to a rendered overlay.</summary>
        public HashSet<AssetRef> SourceAssets => _sourceAssets ??= BuildSourceAssets();

        private HashSet<AssetRef>? _sourceAssets;

        private HashSet<AssetRef> BuildSourceAssets()
        {
            {
                var set = new HashSet<AssetRef>();
                foreach (TimelineSource source in Manifest.Sources ?? [])
                {
                    if (source is not null && ResolveSource(source) is { } asset)
                    {
                        set.Add(asset);
                    }
                }

                return set;
            }
        }

        public void ReportUnplaced(string manifestTrackType, TimelineItem item, string reason) =>
            Unplaced.Add(new UnplacedItem(
                TrackLabelFor(manifestTrackType) ?? manifestTrackType,
                item.Id ?? string.Empty,
                string.IsNullOrWhiteSpace(item.Label) ? null : item.Label,
                reason));

        /// <summary>Reports one softened seam frame; the compile blended this cut, the seed hard-cuts it.</summary>
        public void ReportSeam(long seamFrame)
        {
            if (!_seamFrames.Add(seamFrame))
            {
                return;
            }

            Markers.Add(new Marker(
                DeterministicItemId($"edit:seam:{seamFrame}"),
                seamFrame,
                "Softened seam",
                "blue",
                "The compile blended this seam with a transition; the seeded timeline uses a hard cut here.",
                null));
        }

        /// <summary>
        /// A source's asset: its verified identity first (a trimmed or still source's key is an
        /// analysis scratch key no row owns), else the storage-key lookup (old manifests).
        /// </summary>
        public AssetRef? ResolveSource(TimelineSource source)
        {
            if ((source.ProjectFileId is not null || source.StepResultId is not null) &&
                resolveSourceIdentity?.Invoke(source) is { } byIdentity)
            {
                return byIdentity;
            }

            return Resolve(source.StorageKey);
        }

        /// <summary>True only when the caller's probe lookup says this source has an audio stream.</summary>
        public bool HasAudio(string? storageKey) =>
            sourceHasAudio is not null && !string.IsNullOrWhiteSpace(storageKey) && sourceHasAudio(storageKey!);

        /// <summary>
        /// Resolves a manifest asset identifier through the caller's resolver, caching the result.
        /// </summary>
        public AssetRef? Resolve(string? identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            if (_resolved.TryGetValue(identifier!, out AssetRef? cached))
            {
                return cached;
            }

            AssetRef? resolved = resolveAsset(identifier!);
            _resolved[identifier!] = resolved;
            return resolved;
        }
    }
}

/// <summary>A seeded document and what the seeder could not place.</summary>
public sealed record SeededTimeline(EditTimeline Document, IReadOnlyList<UnplacedItem> Unplaced);

/// <summary>
/// A manifest item left off the seeded timeline. Deliberately carries no storage key, bucket or URL:
/// it is returned to the browser.
/// </summary>
/// <param name="Track">The v1 track label (<c>V1</c>, <c>V2</c>, <c>V3</c>, <c>A2</c>, <c>A3</c>).</param>
/// <param name="ItemId">The manifest item id.</param>
/// <param name="Label">The item's label, when it has one.</param>
/// <param name="Reason">One of <see cref="UnplacedReasons"/>.</param>
public sealed record UnplacedItem(string Track, string ItemId, string? Label, string Reason);

/// <summary>Reason codes of <see cref="UnplacedItem.Reason"/>.</summary>
public static class UnplacedReasons
{
    /// <summary>A cut's source clip is not an asset of this project.</summary>
    public const string SourceUnresolved = "source_unresolved";

    /// <summary>An insert, graphic, sfx or music asset is not an asset of this project.</summary>
    public const string AssetUnresolved = "asset_unresolved";

    /// <summary>A crossfade seam left the item wholly inside the previous item's range, so it has no frames of its own.</summary>
    public const string SeamOverlapDropped = "seam_overlap_dropped";

    /// <summary>A censored stretch (manifest <c>censor</c> track): the plain seed cannot carry the obscuring, so the picture there is withheld.</summary>
    public const string CensoredStretchWithheld = "censored_stretch_withheld";
}
