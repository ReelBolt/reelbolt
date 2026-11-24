using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// The caller-supplied lookups <see cref="EditTimelineValidator"/> needs. Every one of them is a
/// plain dictionary so the validator stays pure — <c>ReelBolt.Shared</c> has no <c>DbContext</c>
/// and must never query a database.
/// </summary>
/// <param name="ExpectedProjectId">
/// The project the document is being validated FOR. An <see cref="AssetRef"/> that resolves
/// anywhere else is a violation, however well-formed it is.
/// </param>
/// <param name="AssetDurationFrames">
/// <see cref="AssetRef"/> → the referenced asset's known duration in frames. A MISSING key is
/// NOT a violation: it means the duration is not yet known, so the source-extent check degrades
/// to "unvalidated" for that clip rather than rejecting it. See the remarks on
/// <see cref="EditTimelineValidator"/> for why this is deliberately asymmetric with the two
/// ownership lookups below (where a missing key IS a violation).
/// </param>
/// <param name="ProjectFileOwners">
/// <c>ProjectFile.Id</c> → owning <c>ProjectId</c>. A missing key is a violation.
/// </param>
/// <param name="StepResultOwners">
/// <c>WorkflowStepResult.Id</c> → owning <c>ProjectId</c> (reached transitively through
/// <c>WorkflowExecution.ProjectId</c>). A missing key is a violation. This one map answers for
/// BOTH ways a document can name a step result — <c>AssetRefKind.StepResult</c> and
/// <see cref="Provenance.StepResultId"/> — so a caller must collect both id sets before querying,
/// or every provenance id resolves as "unknown".
/// </param>
/// <param name="SegmentationTrackIds">
/// Segmentation masks (<c>mask.kind == "segmentation"</c>): <c>WorkflowStepResult.Id</c> → the set of
/// <c>VideoSegmentationTrack.Id</c> (<c>g{n}</c>) its VideoAnalyze artifact carries. The caller includes an
/// entry ONLY for a VideoAnalyze result of the SAME project as <see cref="ExpectedProjectId"/>; a foreign,
/// missing, non-VideoAnalyze or track-less result is simply absent, and the validator refuses an absent id
/// with the same message as an unknown track, so the 400 is no existence oracle across projects. Null means
/// no segmentation lookups were supplied (every segmentation mask is then refused).
/// </param>
/// <param name="FramesPerSecond">
/// R01.6: the timeline's frame rate, used only to word a source-overrun message in seconds
/// (the frames of <see cref="AssetDurationFrames"/> are at this rate). Null words it in frames.
/// </param>
/// <remarks>
/// T-015 builds all three maps from the <c>InferenceApiDbContext</c> for the timeline's project
/// before calling the validator; nothing else is needed.
/// </remarks>
public sealed record EditTimelineValidationContext(
    Guid ExpectedProjectId,
    IReadOnlyDictionary<AssetRef, long> AssetDurationFrames,
    IReadOnlyDictionary<Guid, Guid> ProjectFileOwners,
    IReadOnlyDictionary<Guid, Guid> StepResultOwners,
    double? FramesPerSecond = null,
    IReadOnlyDictionary<Guid, IReadOnlySet<string>>? SegmentationTrackIds = null,
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, SegmentationTrackFacts>>? SegmentationTrackFacts = null);

/// <summary>
/// The complete outcome of one <see cref="EditTimelineValidator.Validate"/> call.
/// </summary>
/// <param name="Violations">
/// EVERY violation found, in document order — never just the first. <c>Path</c> is a precise
/// dotted/indexed locator (e.g. <c>tracks[0].items[2].recordIn</c>) so a 400 response body built
/// from it tells the caller exactly which field to fix.
/// </param>
/// <remarks>
/// The tuple is <c>(string Path, string Message)</c>: <c>Path</c> says WHERE, <c>Message</c> says
/// WHAT and why.
/// </remarks>
public sealed record ValidationResult(IReadOnlyList<(string Path, string Message)> Violations)
{
    /// <summary>
    /// True when the document has no violations at all.
    /// </summary>
    public bool IsValid => Violations.Count == 0;
}

/// <summary>
/// Validates an <see cref="EditTimeline"/> document against the structural and cross-tenant
/// invariants the editing contract depends on. Pure: no I/O, no database, no clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-tenant AssetRef resolution is the point of this type.</b> An <see cref="AssetRef"/> is
/// the one place a timeline document names an entity that lives outside itself, so it is the one
/// place a caller could smuggle a reference to another project's file or step result into the
/// caller's own timeline. Every ref must resolve, through the caller-supplied ownership maps,
/// to <see cref="EditTimelineValidationContext.ExpectedProjectId"/> and nowhere else. This mirrors
/// the trust boundary <c>StepResultArtifactsController</c>'s artifact endpoint already enforces;
/// getting it wrong is a cross-tenant data leak, not merely a bad document.
/// </para>
/// <para>
/// <b><see cref="Provenance.StepResultId"/> is the SECOND cross-tenant hole, and it is closed the
/// same way.</b> An <see cref="AssetRef"/> is not the only field that can name another project's
/// row: every <see cref="Item"/> carries an optional <see cref="Provenance"/>, and its
/// <see cref="Provenance.StepResultId"/> names a <c>WorkflowStepResult</c> exactly as
/// <c>AssetRefKind.StepResult</c> does. It is therefore resolved through the SAME
/// <see cref="EditTimelineValidationContext.StepResultOwners"/> map, with the same rule — absent
/// or foreign is a violation — and it is checked on EVERY item kind, since
/// <see cref="Provenance"/> lives on the base <see cref="Item"/> rather than on a subclass. See
/// <see cref="ValidateProvenance"/> for what is deliberately NOT checked alongside it, and why.
/// </para>
/// <para>
/// <b>Unknown asset DURATION is not a violation — deliberately, and asymmetrically with
/// OWNERSHIP.</b> The two lookups answer different kinds of question and are treated differently
/// on purpose:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Ownership</b> (<see cref="EditTimelineValidationContext.ProjectFileOwners"/> /
/// <see cref="EditTimelineValidationContext.StepResultOwners"/>): a MISSING entry IS a violation.
/// Ownership is a security property held in a database the caller can always consult, and
/// "unresolvable" must never be read as "trusted" — that rule is what stops one project's ref
/// from resolving into another's.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Duration</b> (<see cref="EditTimelineValidationContext.AssetDurationFrames"/>): a MISSING
/// entry is NOT a violation. The source-extent check can only ever PROVE an overrun; the absence
/// of a duration means "not yet known", not "provably wrong", so it degrades to unvalidated
/// rather than to a rejection. No caller in this phase can populate that map at all —
/// <c>ProjectFile</c> has no duration column, the <c>MediaDerivative</c> table has no writer yet
/// and the Inference API has no ffprobe — so treating "unknown" as a violation would reject
/// every real <c>MediaClip</c> and make the whole validator unusable. A clip whose asset duration
/// IS supplied still gets the full overrun check, so a future phase that starts populating
/// durations (e.g. from <c>MediaDerivative.DurationSec</c>) automatically re-arms the check with
/// no change to this class.
/// </description>
/// </item>
/// </list>
/// <para>
/// In one line: an unresolvable <i>asset identity</i> is rejected, an unresolvable <i>asset
/// length</i> merely goes unchecked.
/// </para>
/// <para>
/// <b>What "unsanitizable text" means here.</b> <c>TitleClip</c>/<c>Caption</c> text must survive
/// <see cref="OverlayTextSanitizer.Sanitize"/> with something renderable left over — the render
/// path sanitizes text again and drops an overlay whose text sanitizes away, so a clip whose text
/// is null, whitespace-only, or composed entirely of characters the allowlist strips is a
/// document that cannot produce the output it claims. Text that merely CONTAINS such characters
/// (an apostrophe, <c>%</c>, <c>:</c>, an emoji) is NOT a violation: real ASR captions contain
/// apostrophes, and rejecting them would be a functional defect rather than a hardening. There is
/// deliberately no "text must equal its sanitized form" rule.
/// </para>
/// <para>
/// Every item is checked regardless of its <c>Enabled</c> flag — the document's geometry is the
/// contract, and a disabled item that has since been corrupted still has to survive a round trip
/// through the editor before it can be re-enabled.
/// </para>
/// </remarks>
public static class EditTimelineValidator
{
    /// <summary>
    /// Maximum number of tracks a document may carry.
    /// </summary>
    public const int MaxTracks = 64;

    /// <summary>
    /// Maximum number of items a single track may carry.
    /// </summary>
    public const int MaxItemsPerTrack = 5000;

    /// <summary>
    /// Maximum number of keyframes a single <see cref="Keyframed{T}"/> may carry.
    /// </summary>
    public const int MaxKeyframes = 256;

    /// <summary>P5: the longest clip that may be played in reverse, in timeline frames (reversing buffers the segment).</summary>
    public const int MaxReverseFrames = 1800;

    /// <summary>P5: parametric EQ bands per track.</summary>
    public const int MaxEqBands = 4;

    /// <summary>The only <see cref="EditTimeline.SchemaVersion"/> the contract accepts.</summary>
    public const int SchemaVersion = 2;

    /// <summary>P5: the closed <c>xfade</c> transition catalogue — the only values <c>Transition.transitionType</c> may take.</summary>
    public static readonly IReadOnlySet<string> TransitionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "fade", "dissolve", "fadeblack", "fadewhite", "fadegrays", "distance", "pixelize",
        "wipeleft", "wiperight", "wipeup", "wipedown", "wipetl", "wipetr", "wipebl", "wipebr",
        "slideleft", "slideright", "slideup", "slidedown",
        "smoothleft", "smoothright", "smoothup", "smoothdown",
        "circlecrop", "rectcrop", "circleopen", "circleclose",
        "vertopen", "vertclose", "horzopen", "horzclose",
        "diagtl", "diagtr", "diagbl", "diagbr",
        "hlslice", "hrslice", "vuslice", "vdslice",
        "radial", "zoomin", "squeezev", "squeezeh", "hblur"
    };

    /// <summary>P5: the easing words a transition accepts — the <see cref="InterpKind"/> names, camelCase.</summary>
    public static readonly IReadOnlySet<string> TransitionEasings = new HashSet<string>(StringComparer.Ordinal)
    {
        "linear", "easeInQuad", "easeOutQuad", "easeInOutQuad", "easeInCubic", "easeOutCubic", "easeInOutCubic", "step"
    };

    /// <summary>P5: the closed generator catalogue — the only values <c>GeneratorClip.generatorType</c> may take.</summary>
    public static readonly IReadOnlySet<string> GeneratorTypes = new HashSet<string>(StringComparer.Ordinal) { "color", "bars", "testsrc" };

    /// <summary>P7: corrections per grade stack.</summary>
    public const int MaxGradeNodes = 8;

    /// <summary>P7: points per tone curve.</summary>
    public const int MaxCurvePoints = 16;

    /// <summary>P7: effects per clip.</summary>
    public const int MaxEffects = 8;

    /// <summary>Most region effects one clip may carry.</summary>
    public const int MaxRegionEffects = 4;

    /// <summary>T-102: blur alone ranges to 2 (sigma = amount x 20, so one effect reaches the compile's canvas-fill sigma 40); every other effect stays in [0, 1].</summary>
    public const double MaxBlurAmount = 2.0;

    /// <summary>P7: colour groups per timeline.</summary>
    public const int MaxColorGroups = 32;

    /// <summary>The closed region-effect words (the workflow's <c>RegionEffect</c> set, lowercase).</summary>
    public static readonly IReadOnlySet<string> RegionEffectWords = new HashSet<string>(StringComparer.Ordinal) { "blur", "pixelate", "highlight", "spotlight", "colorpop", "outline" };

    /// <summary>P7: the closed effects catalogue — mirrored by the TS engine's <c>EFFECT_TYPES</c> and rendered by <c>GradeFilterBuilder.Effects</c>.</summary>
    public static readonly IReadOnlySet<string> EffectTypes = new HashSet<string>(StringComparer.Ordinal) { "blur", "sharpen", "vignette", "grain", "pixelate", "mono" };

    /// <summary>P7: the closed title-template catalogue.</summary>
    public static readonly IReadOnlySet<string> TitleTemplates = new HashSet<string>(StringComparer.Ordinal) { "plain", "lowerThird", "boxed", "outline", "card" };

    /// <summary>R09.6: the closed caption-look catalogue.</summary>
    public static readonly IReadOnlySet<string> CaptionLooks = new HashSet<string>(StringComparer.Ordinal) { "clean", "bold", "boxed", "outline", "spotlight" };

    /// <summary>R09.6/R10.5: the 3x3 text-position pad.</summary>
    public static readonly IReadOnlySet<string> TextPositions = new HashSet<string>(StringComparer.Ordinal) { "topLeft", "topCenter", "topRight", "middleLeft", "center", "middleRight", "bottomLeft", "bottomCenter", "bottomRight" };

    /// <summary>P7: how a slowed clip makes its frames.</summary>
    public static readonly IReadOnlySet<string> RetimeQualities = new HashSet<string>(StringComparer.Ordinal) { "frame", "opticalFlow" };

    /// <summary>P7: the mask shapes.</summary>
    public static readonly IReadOnlySet<string> MaskKinds = new HashSet<string>(StringComparer.Ordinal) { "rect", "ellipse" };

    /// <summary>
    /// P8: how many levels a chain of compound clips (or of bus routing) may have, the root counting
    /// as level 1 — so a compound clip may sit at most three sequences deep.
    /// </summary>
    public const int MaxNestingDepth = 4;

    /// <summary>P8: sequences per timeline.</summary>
    public const int MaxSequences = 64;

    /// <summary>P8: buses per timeline.</summary>
    public const int MaxBuses = 16;

    /// <summary>P8: angles per multicam clip.</summary>
    public const int MaxMulticamAngles = 16;

    /// <summary>P8: angle switches per multicam clip.</summary>
    public const int MaxMulticamCuts = 2000;

    /// <summary>P8: the closed channel-map catalogue (<see cref="MediaClip.ChannelMap"/>).</summary>
    public static readonly IReadOnlySet<string> ChannelMaps = new HashSet<string>(StringComparer.Ordinal) { "stereo", "left", "right", "mono", "swap" };

    /// <summary>P8: the denoiser kinds this wave ships (<c>rnn</c> needs a model file the image does not carry).</summary>
    public static readonly IReadOnlySet<string> DenoiseKinds = new HashSet<string>(StringComparer.Ordinal) { "fft" };

    /// <summary>P8: the leveler kinds.</summary>
    public static readonly IReadOnlySet<string> LevelerKinds = new HashSet<string>(StringComparer.Ordinal) { "speech", "dynamic" };

    /// <summary>
    /// Validates <paramref name="document"/> against the editing contract, returning EVERY
    /// violation rather than the first.
    /// </summary>
    /// <param name="document">The document to validate.</param>
    /// <param name="context">The caller-supplied ownership and duration lookups.</param>
    /// <returns>A <see cref="ValidationResult"/> whose <see cref="ValidationResult.Violations"/> is empty for a valid document.</returns>
    public static ValidationResult Validate(EditTimeline document, EditTimelineValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        List<(string Path, string Message)> violations = [];

        // The contract is schema version 2 and nothing else: a document that omits the field reads
        // as 2 (the JSON constructor's default), any other value is refused rather than stored.
        if (document.SchemaVersion != SchemaVersion)
        {
            violations.Add((
                "schemaVersion",
                $"schemaVersion must be {SchemaVersion}, found {document.SchemaVersion}"));
        }

        if (document.Tracks is not { } tracks)
        {
            violations.Add(("tracks", "tracks must not be null"));
            return new ValidationResult(violations);
        }

        if (tracks.Count > MaxTracks)
        {
            violations.Add((
                "tracks",
                $"a timeline must carry at most {MaxTracks} tracks, found {tracks.Count}"));
        }

        ValidateTracks(tracks, "tracks", document, context, violations);
        ValidateColorGroups(document, context, violations);
        ValidateSequences(document, context, violations);
        ValidateBuses(document, violations);

        return new ValidationResult(violations);
    }

    /// <summary>
    /// Item types that carry picture and no sound, so an audio track has nothing to play from them.
    /// A <see cref="MediaClip"/> is NOT here: on an audio track it means the file's sound (a music
    /// bed, a camera file's audio from an OTIO import). Compound and multicam clips carry sound too.
    /// </summary>
    private static bool IsPictureOnly(Item item) =>
        item is TitleClip or GeneratorClip or InsertClip or Caption or AdjustmentClip;

    private static string ItemTypeName(Item item) => item switch
    {
        TitleClip => "title clip",
        GeneratorClip => "generator clip",
        InsertClip => "insert clip",
        Caption => "caption",
        AdjustmentClip => "adjustment clip",
        _ => "item"
    };

    /// <summary>The per-track checks, shared by the root <c>tracks</c> and every sequence's (P8).</summary>
    private static void ValidateTracks(
        IReadOnlyList<Track> tracks,
        string basePath,
        EditTimeline document,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        for (int t = 0; t < tracks.Count; t++)
        {
            Track? track = tracks[t];
            string trackPath = $"{basePath}[{t}]";

            if (track is null)
            {
                violations.Add((trackPath, "track must not be null"));
                continue;
            }

            if (track.Items is not { } items)
            {
                violations.Add((trackPath + ".items", "items must not be null"));
                continue;
            }

            if (items.Count > MaxItemsPerTrack)
            {
                violations.Add((
                    trackPath + ".items",
                    $"a track must carry at most {MaxItemsPerTrack} items, found {items.Count}"));
            }

            for (int i = 0; i < items.Count; i++)
            {
                ValidateItem(items[i], $"{trackPath}.items[{i}]", document, context, violations);

                if (track.Kind != TrackKind.Video && items[i] is MediaClip { BlendMode: { } blendWord } && blendWord != "normal")
                {
                    violations.Add((
                        $"{trackPath}.items[{i}].blendMode",
                        $"blendMode '{blendWord}' is only allowed on a clip on a video track"));
                }

                if (track.Kind == TrackKind.Audio && items[i] is { } placed && IsPictureOnly(placed))
                {
                    violations.Add((
                        $"{trackPath}.items[{i}]",
                        $"a {ItemTypeName(placed)} is picture only and cannot sit on an audio track"));
                }
            }

            ValidateTrackAudio(track, trackPath, violations);
            ValidateTrackOrdering(items, trackPath, violations);
        }

        ValidateLinkGroups(tracks, basePath, violations);
    }

    /// <summary>R08.1: the most clips one link group may hold.</summary>
    public const int MaxLinkGroupMembers = 8;

    /// <summary>R08.1: the longest <c>linkId</c>.</summary>
    public const int MaxLinkIdLength = 64;

    /// <summary>
    /// R08.1: a <c>linkId</c> is 1..<see cref="MaxLinkIdLength"/> characters of <c>[A-Za-z0-9_-]</c>; the clips
    /// sharing one (within one track list) sit on different tracks, share one source and number at most
    /// <see cref="MaxLinkGroupMembers"/>. A group of one is tolerated.
    /// </summary>
    private static void ValidateLinkGroups(IReadOnlyList<Track> tracks, string basePath, List<(string Path, string Message)> violations)
    {
        Dictionary<string, (HashSet<int> Tracks, AssetRef Asset, int Count)> groups = new(StringComparer.Ordinal);
        for (int t = 0; t < tracks.Count; t++)
        {
            if (tracks[t]?.Items is not { } items) continue;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not MediaClip { LinkId: { } id } clip) continue;
                string linkPath = $"{basePath}[{t}].items[{i}].linkId";
                if (id.Length == 0 || id.Length > MaxLinkIdLength || !id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
                {
                    violations.Add((linkPath, "a link needs a short name of letters, digits, dashes or underscores (up to 64 characters)"));
                    continue;
                }

                if (!groups.TryGetValue(id, out var group))
                {
                    groups[id] = ([t], clip.AssetRef, 1);
                    continue;
                }

                if (!group.Tracks.Add(t))
                {
                    violations.Add((linkPath, "two clips linked together cannot sit on the same track"));
                }
                else if (!Equals(group.Asset, clip.AssetRef))
                {
                    violations.Add((linkPath, "clips linked together must come from the same media"));
                }
                else if (group.Count + 1 > MaxLinkGroupMembers)
                {
                    violations.Add((linkPath, $"at most {MaxLinkGroupMembers} clips can be linked together"));
                }

                groups[id] = (group.Tracks, group.Asset, group.Count + 1);
            }
        }
    }

    /// <summary>
    /// P8: the sequences compound clips play — unique ids, a name, the same per-track rules as the
    /// root, and the nesting rules: a compound clip names an existing sequence, no sequence contains
    /// itself (directly or through another), and the chain from the root is at most
    /// <see cref="MaxNestingDepth"/> deep.
    /// </summary>
    private static void ValidateSequences(EditTimeline document, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        IReadOnlyList<EditSequence> sequences = document.Sequences ?? [];
        if (sequences.Count > MaxSequences)
        {
            violations.Add(("sequences", $"a timeline may carry at most {MaxSequences} sequences, found {sequences.Count}"));
        }

        Dictionary<Guid, (EditSequence Sequence, int Index)> byId = [];
        for (int i = 0; i < sequences.Count; i++)
        {
            EditSequence? sequence = sequences[i];
            string path = $"sequences[{i}]";
            if (sequence is null)
            {
                violations.Add((path, "sequence must not be null"));
                continue;
            }

            if (!byId.TryAdd(sequence.Id, (sequence, i)))
            {
                violations.Add((path + ".id", $"sequence id {sequence.Id} is duplicated"));
            }

            if (string.IsNullOrWhiteSpace(sequence.Name))
            {
                violations.Add((path + ".name", "sequence name must not be empty"));
            }

            if (sequence.Tracks is not { } tracks)
            {
                violations.Add((path + ".tracks", "tracks must not be null"));
                continue;
            }

            if (tracks.Count > MaxTracks)
            {
                violations.Add((path + ".tracks", $"a sequence must carry at most {MaxTracks} tracks, found {tracks.Count}"));
            }

            ValidateTracks(tracks, path + ".tracks", document, context, violations);
        }

        // Nesting: walk from the root; a sequence on the current chain is a cycle, a chain longer
        // than MaxNestingDepth is too deep. Each compound clip is reported once, at its own path.
        HashSet<Guid> onPath = [];
        WalkCompounds(document.Tracks ?? [], "tracks", 1, byId, onPath, violations);
    }

    private static void WalkCompounds(
        IReadOnlyList<Track> tracks,
        string basePath,
        int depth,
        Dictionary<Guid, (EditSequence Sequence, int Index)> sequences,
        HashSet<Guid> onPath,
        List<(string Path, string Message)> violations)
    {
        for (int t = 0; t < tracks.Count; t++)
        {
            if (tracks[t]?.Items is not { } items) continue;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not CompoundClip compound) continue;
                string path = $"{basePath}[{t}].items[{i}].sequenceId";
                if (!sequences.TryGetValue(compound.SequenceId, out (EditSequence Sequence, int Index) found))
                {
                    // Reported here (not in ValidateItem) so a sequence listed twice is not also
                    // "missing": the lookup is by the first occurrence.
                    violations.Add((path, $"sequence {compound.SequenceId} does not exist in this timeline"));
                    continue;
                }

                if (onPath.Contains(compound.SequenceId))
                {
                    violations.Add((path, $"sequence {compound.SequenceId} contains itself"));
                    continue;
                }

                if (depth >= MaxNestingDepth)
                {
                    violations.Add((path, $"compound clips may nest at most {MaxNestingDepth} deep"));
                    continue;
                }

                onPath.Add(compound.SequenceId);
                WalkCompounds(found.Sequence.Tracks ?? [], $"sequences[{found.Index}].tracks", depth + 1, sequences, onPath, violations);
                onPath.Remove(compound.SequenceId);
            }
        }
    }

    /// <summary>
    /// P8: the buses — unique ids, a name, the same audio ranges as a track, and routing that never
    /// cycles or exceeds <see cref="MaxNestingDepth"/>. A track or bus whose target bus does not
    /// exist is NOT a violation: like a colour-group member that was removed, it is tolerated and
    /// inert (the mix routes it to the master), so removing a bus never invalidates a document.
    /// </summary>
    private static void ValidateBuses(EditTimeline document, List<(string Path, string Message)> violations)
    {
        IReadOnlyList<AudioBus> buses = document.Buses ?? [];
        if (buses.Count > MaxBuses)
        {
            violations.Add(("buses", $"a timeline may carry at most {MaxBuses} buses, found {buses.Count}"));
        }

        Dictionary<Guid, AudioBus> byId = [];
        for (int i = 0; i < buses.Count; i++)
        {
            AudioBus? bus = buses[i];
            string path = $"buses[{i}]";
            if (bus is null)
            {
                violations.Add((path, "bus must not be null"));
                continue;
            }

            if (!byId.TryAdd(bus.Id, bus))
            {
                violations.Add((path + ".id", $"bus id {bus.Id} is duplicated"));
            }

            if (string.IsNullOrWhiteSpace(bus.Name))
            {
                violations.Add((path + ".name", "bus name must not be empty"));
            }

            if (bus.OutputBusId == bus.Id)
            {
                violations.Add((path + ".outputBusId", "a bus cannot feed itself"));
            }

            ValidateAudioChannel(bus.GainDb, bus.Pan, bus.Eq, bus.Dynamics, bus.Denoise, bus.Leveler, path, violations);
        }

        for (int i = 0; i < buses.Count; i++)
        {
            AudioBus? bus = buses[i];
            if (bus is null || bus.OutputBusId is null || bus.OutputBusId == bus.Id) continue;
            HashSet<Guid> seen = [bus.Id];
            Guid? next = bus.OutputBusId;
            int depth = 1;
            while (next is { } id && byId.TryGetValue(id, out AudioBus? target))
            {
                if (!seen.Add(id))
                {
                    violations.Add(($"buses[{i}].outputBusId", "bus routing forms a cycle"));
                    break;
                }

                depth++;
                if (depth > MaxNestingDepth)
                {
                    violations.Add(($"buses[{i}].outputBusId", $"buses may chain at most {MaxNestingDepth} deep"));
                    break;
                }

                next = target.OutputBusId;
            }
        }
    }

    /// <summary>
    /// Validates a single item's own fields: <c>recordIn</c>/<c>recordOut</c> sanity, its
    /// <see cref="Provenance"/>, its <see cref="AssetRef"/> (if it has one), its source extent, its
    /// keyframe count, and its text.
    /// </summary>
    private static void ValidateItem(
        Item? item,
        string path,
        EditTimeline document,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        if (item is null)
        {
            violations.Add((path, "item must not be null"));
            return;
        }

        if (item.RecordIn < 0)
        {
            violations.Add((
                path + ".recordIn",
                $"recordIn must be >= 0, found {item.RecordIn}"));
        }

        if (item.RecordIn >= item.RecordOut)
        {
            violations.Add((
                path + ".recordOut",
                $"recordOut ({item.RecordOut}) must be greater than recordIn ({item.RecordIn})"));
        }

        // Checked before the per-kind switch and NOT inside it: Provenance sits on the base Item,
        // so every item kind — including the ones this switch has no case for — must be covered.
        ValidateProvenance(item.Provenance, path + ".provenance", context, violations);

        switch (item)
        {
            case MediaClip media:
                // Only worth checking the source extent when the ref itself is trustworthy — a
                // foreign or unknown ref is already reported and piling a second violation on it
                // would bury the security-relevant one in noise.
                if (ValidateAssetRef(media.AssetRef, path + ".assetRef", context, violations))
                {
                    ValidateMediaClipSourceExtent(media, path, context, violations);
                }

                ValidateMediaClipDepth(media, path, violations);
                ValidateMediaClipPicture(media, path, context, violations);
                if (media.ChannelMap is null || !ChannelMaps.Contains(media.ChannelMap))
                {
                    violations.Add((path + ".channelMap", $"channelMap '{media.ChannelMap}' is not in the channel-map catalogue"));
                }

                // P3: the camera profile is a closed word. null means no transform, so only a
                // present value is checked; an audio-only asset may still carry one (ignored).
                if (media.InputProfile is { } inputProfile && !CameraProfiles.IsKnown(inputProfile))
                {
                    violations.Add((path + ".inputProfile", $"inputProfile '{inputProfile}' is not in the camera-profile catalogue"));
                }
                break;

            case CompoundClip compound:
                // The sequence lookup itself is reported by the nesting walk (ValidateSequences),
                // which also owns the cycle and depth rules; here only the clip's own fields.
                if (compound.SourceIn < 0)
                {
                    violations.Add((path + ".sourceIn", $"sourceIn must be >= 0, found {compound.SourceIn}"));
                }
                break;

            case MulticamClip multicam:
                ValidateMulticam(multicam, path, context, violations);
                break;

            case AdjustmentClip adjustment:
                ValidateGrade(adjustment.Grade, path + ".grade", context, violations);
                ValidateEffects(adjustment.Effects, path + ".effects", violations);
                break;

            case Transition transition:
                if (transition.TransitionType is null || !TransitionTypes.Contains(transition.TransitionType))
                {
                    violations.Add((
                        path + ".transitionType",
                        $"transitionType '{transition.TransitionType}' is not in the transition catalogue"));
                }

                if (transition.Easing is not null && !TransitionEasings.Contains(transition.Easing))
                {
                    violations.Add((path + ".easing", $"easing '{transition.Easing}' is not an interpolation kind"));
                }
                break;

            case GeneratorClip generator:
                if (generator.GeneratorType is null || !GeneratorTypes.Contains(generator.GeneratorType))
                {
                    violations.Add((
                        path + ".generatorType",
                        $"generatorType '{generator.GeneratorType}' is not in the generator catalogue"));
                }

                if (generator.GeneratorConfig is { } config && config.TryGetValue("color", out object? color))
                {
                    string? text = color switch
                    {
                        string str => str,
                        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } el => el.GetString(),
                        _ => null
                    };
                    if (text is null || !IsHexColor(text))
                    {
                        violations.Add((path + ".generatorConfig.color", "generator colour must be a #RRGGBB string"));
                    }
                }
                break;

            case InsertClip insert:
                if (insert.AssetRef is not null)
                {
                    ValidateAssetRef(insert.AssetRef, path + ".assetRef", context, violations);
                }

                ValidateQuadKeyframes(insert.Quad, path + ".quad", violations);
                ValidateKeyer(insert.Matte, path + ".matte", violations);
                break;

            case TitleClip title:
                ValidateOverlayText(title.Text, path + ".text", violations);
                if (title.Template is not null && !TitleTemplates.Contains(title.Template))
                {
                    violations.Add((path + ".template", $"template '{title.Template}' is not in the title-template catalogue"));
                }

                if (title.Position is not null && !TextPositions.Contains(title.Position))
                {
                    violations.Add((path + ".position", $"position '{title.Position}' is not on the text-position pad"));
                }

                if (title.Transform is { } titleTransform)
                {
                    ValidateClipTransform(titleTransform, path + ".transform", violations);
                }
                break;

            case Caption caption:
                ValidateOverlayText(caption.Text, path + ".text", violations);
                if (caption.Look is not null && !CaptionLooks.Contains(caption.Look))
                {
                    violations.Add((path + ".look", $"look '{caption.Look}' is not in the caption-look catalogue"));
                }

                if (caption.Position is not null && !TextPositions.Contains(caption.Position))
                {
                    violations.Add((path + ".position", $"position '{caption.Position}' is not on the text-position pad"));
                }
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// P8: a multicam clip's angles (1..<see cref="MaxMulticamAngles"/>, unique ids, each source
    /// owned by the project), its active angle, its cuts (strictly increasing, strictly inside the
    /// span, naming known angles) and its gain.
    /// </summary>
    private static void ValidateMulticam(MulticamClip multicam, string path, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        if (multicam.SourceIn < 0)
        {
            violations.Add((path + ".sourceIn", $"sourceIn must be >= 0, found {multicam.SourceIn}"));
        }

        HashSet<Guid> angleIds = [];
        if (multicam.Angles is not { Count: > 0 } angles)
        {
            violations.Add((path + ".angles", "a multicam clip needs at least one angle"));
        }
        else
        {
            if (angles.Count > MaxMulticamAngles)
            {
                violations.Add((path + ".angles", $"at most {MaxMulticamAngles} angles, found {angles.Count}"));
            }

            for (int a = 0; a < angles.Count; a++)
            {
                MulticamAngle? angle = angles[a];
                string anglePath = $"{path}.angles[{a}]";
                if (angle is null)
                {
                    violations.Add((anglePath, "angle must not be null"));
                    continue;
                }

                if (!angleIds.Add(angle.Id))
                {
                    violations.Add((anglePath + ".id", $"angle id {angle.Id} is duplicated"));
                }

                ValidateAssetRef(angle.AssetRef, anglePath + ".assetRef", context, violations);

                // P3: the angle's camera profile is the same closed word as a media clip's; null
                // means display space, so only a present value is checked.
                if (angle.InputProfile is { } angleProfile && !CameraProfiles.IsKnown(angleProfile))
                {
                    violations.Add((anglePath + ".inputProfile", $"inputProfile '{angleProfile}' is not in the camera-profile catalogue"));
                }
            }

            if (!angleIds.Contains(multicam.ActiveAngleId))
            {
                violations.Add((path + ".activeAngleId", $"activeAngleId {multicam.ActiveAngleId} is not one of the clip's angles"));
            }
        }

        if (multicam.Cuts is { } cuts)
        {
            if (cuts.Count > MaxMulticamCuts)
            {
                violations.Add((path + ".cuts", $"at most {MaxMulticamCuts} cuts, found {cuts.Count}"));
            }

            long previous = long.MinValue;
            for (int c = 0; c < cuts.Count; c++)
            {
                MulticamCut? cut = cuts[c];
                string cutPath = $"{path}.cuts[{c}]";
                if (cut is null)
                {
                    violations.Add((cutPath, "cut must not be null"));
                    continue;
                }

                if (cut.AtFrame <= multicam.RecordIn || cut.AtFrame >= multicam.RecordOut)
                {
                    violations.Add((cutPath + ".atFrame", $"a cut must fall strictly inside the clip's span ({multicam.RecordIn}, {multicam.RecordOut}), found {cut.AtFrame}"));
                }

                if (cut.AtFrame <= previous)
                {
                    violations.Add((cutPath + ".atFrame", $"cuts must be in strictly increasing frame order, but {cut.AtFrame} follows {previous}"));
                }

                previous = cut.AtFrame;

                if (!angleIds.Contains(cut.AngleId))
                {
                    violations.Add((cutPath + ".angleId", $"angleId {cut.AngleId} is not one of the clip's angles"));
                }
            }
        }

        if (multicam.AudioSource is { } audioSource
            && audioSource != "program" && audioSource != "none"
            && !(Guid.TryParse(audioSource, out Guid sourceAngle) && angleIds.Contains(sourceAngle)))
        {
            violations.Add((path + ".audioSource", "the sound source must be the program, none, or one of this clip's own angles"));
        }

        ValidateKeyframedDouble(multicam.GainDb, path + ".gainDb", -60, 20, violations);
    }

    /// <summary>
    /// Validates one item's <see cref="Provenance"/>: its <see cref="Provenance.StepResultId"/>,
    /// when present, must resolve through <see cref="EditTimelineValidationContext.StepResultOwners"/>
    /// to the expected project — absent or foreign is a violation, exactly as for
    /// <c>AssetRefKind.StepResult</c>.
    /// </summary>
    /// <param name="provenance">The item's provenance, or <c>null</c> when it carries none.</param>
    /// <param name="path">
    /// The dotted locator of the whole provenance object (e.g. <c>tracks[0].items[2].provenance</c>);
    /// the reported path appends <c>.stepResultId</c>.
    /// </param>
    /// <param name="context">The caller-supplied ownership lookups.</param>
    /// <param name="violations">The accumulator.</param>
    /// <remarks>
    /// <para>
    /// <b>A null <see cref="Provenance"/> — or one with a null <see cref="Provenance.StepResultId"/>
    /// — is NOT a violation.</b> Provenance is optional metadata: a hand-authored clip, a
    /// <c>Transition</c> and a v1 seed item that had no recorded step result all legitimately carry
    /// none, and the only thing that would be gained by demanding one is rejecting valid documents.
    /// The rule bites ONLY once the document actually names a step result — at which point that
    /// name is a cross-project reference and gets the same treatment as an <see cref="AssetRef"/>.
    /// </para>
    /// <para>
    /// <b><see cref="Provenance.AgentDefinitionId"/> and <see cref="Provenance.ProviderId"/> are
    /// deliberately NOT validated here.</b> They name rows in GLOBAL, non-project-scoped catalogues
    /// — <c>agent_definitions</c> and <c>inference_providers</c> carry no <c>ProjectId</c> column at
    /// all — so there is no cross-tenant boundary to enforce on them: any project may legitimately
    /// name any agent or provider, and "does this id exist?" would be a referential-integrity check
    /// with nothing to protect rather than a security one. They are also write-only today: nothing
    /// on the server reads either field, so a stale or invented id cannot currently influence any
    /// decision. That is a property of the CURRENT consumers, not of the fields, so a future
    /// consumer that starts acting on <see cref="Provenance.AgentDefinitionId"/> or
    /// <see cref="Provenance.ProviderId"/> must resolve-and-check it at its own point of use
    /// (where it also gets to decide what "unknown" should mean) rather than assuming validation
    /// happened here. Same reasoning for <see cref="Provenance.OfferedId"/>, which is an opaque
    /// analysis-scoped token that is not a row id in any table.
    /// </para>
    /// </remarks>
    private static void ValidateProvenance(
        Provenance? provenance,
        string path,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        if (provenance?.StepResultId is not { } id)
        {
            return;
        }

        string idPath = path + ".stepResultId";

        if (!context.StepResultOwners.TryGetValue(id, out Guid owner))
        {
            violations.Add((
                idPath,
                NotFoundInProject("stepResultId", id)));
            return;
        }

        if (owner != context.ExpectedProjectId)
        {
            violations.Add((
                idPath,
                NotFoundInProject("stepResultId", id)));
        }
    }

    /// <summary>
    /// One sentence for both unknown and foreign-owned ids, so a 400 never confirms cross-tenant
    /// existence or names the owning project.
    /// </summary>
    private static string NotFoundInProject(string idName, Guid id) => $"{idName} {id}{NotFoundInProjectSuffix}";

    private const string NotFoundInProjectSuffix = " not found in this project";

    /// <summary>
    /// True when <paramref name="message"/> is the sentence for an id that does not resolve inside the
    /// project (deleted, pruned or foreign: deliberately one case). Callers that treat this differently
    /// from a structural violation ask here, never by matching the wording themselves.
    /// </summary>
    public static bool IsNotFoundInProject(string message) =>
        message.EndsWith(NotFoundInProjectSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Checks that the ref is well-formed AND resolves to the expected project.
    /// </summary>
    /// <returns>
    /// True only when the ref resolved cleanly to
    /// <see cref="EditTimelineValidationContext.ExpectedProjectId"/>; false when it is null,
    /// internally inconsistent, unknown, or owned by somebody else (each of which is reported).
    /// </returns>
    private static bool ValidateAssetRef(
        AssetRef? assetRef,
        string path,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        if (assetRef is null)
        {
            violations.Add((path, "assetRef must not be null"));
            return false;
        }

        return assetRef.Kind switch
        {
            AssetRefKind.ProjectFile => ValidateGuardedOwnership(
                assetRef.Kind,
                assetRef.ProjectFileId,
                contradictoryId: assetRef.StepResultId,
                path,
                requiredIdName: "projectFileId",
                contradictoryIdName: "stepResultId",
                owners: context.ProjectFileOwners,
                context,
                violations),

            AssetRefKind.StepResult => ValidateGuardedOwnership(
                assetRef.Kind,
                assetRef.StepResultId,
                contradictoryId: assetRef.ProjectFileId,
                path,
                requiredIdName: "stepResultId",
                contradictoryIdName: "projectFileId",
                owners: context.StepResultOwners,
                context,
                violations),

            _ => RejectUnknownKind(assetRef.Kind, path, violations),
        };
    }

    /// <summary>
    /// The shared shape of both <see cref="AssetRefKind"/> branches: the id the kind calls for must
    /// be present, the id the kind does NOT call for must be absent, and the present id must be
    /// known to the ownership map and owned by the expected project.
    /// </summary>
    private static bool ValidateGuardedOwnership(
        AssetRefKind kind,
        Guid? requiredId,
        Guid? contradictoryId,
        string path,
        string requiredIdName,
        string contradictoryIdName,
        IReadOnlyDictionary<Guid, Guid> owners,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        if (requiredId is not { } id)
        {
            violations.Add((path, $"assetRef.kind is {kind} but {requiredIdName} is null"));
            return false;
        }

        if (contradictoryId is not null)
        {
            violations.Add((
                path,
                $"assetRef.kind is {kind} but {contradictoryIdName} is also set; exactly one id may be populated"));
            return false;
        }

        if (!owners.TryGetValue(id, out Guid owner))
        {
            violations.Add((
                path,
                NotFoundInProject(requiredIdName, id)));
            return false;
        }

        if (owner != context.ExpectedProjectId)
        {
            violations.Add((
                path,
                NotFoundInProject(requiredIdName, id)));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rejects an unsupported <see cref="AssetRefKind"/>. Unreachable for today's two-value enum,
    /// but a deserialized out-of-range integer would otherwise fall through as "valid".
    /// </summary>
    private static bool RejectUnknownKind(
        AssetRefKind kind,
        string path,
        List<(string Path, string Message)> violations)
    {
        violations.Add((path, $"assetRef.kind '{kind}' is not a supported asset reference kind"));
        return false;
    }

    /// <summary>
    /// Enforces <c>sourceIn + (recordOut - recordIn) &lt;= assetDurationFrames</c> for a
    /// <see cref="MediaClip"/>, but ONLY when the caller actually knows the asset's duration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only called once <see cref="ValidateAssetRef"/> has confirmed the ref is owned by the
    /// expected project — a foreign or unknown ref is already reported, and piling a second
    /// violation on top would bury the security-relevant one in noise.
    /// </para>
    /// <para>
    /// <b>An unknown duration is skipped, not rejected.</b> This check can only ever PROVE an
    /// overrun: if the caller has a duration and the clip runs past it, the document is provably
    /// wrong. If the caller has NO duration for the asset, nothing has been proven — the duration
    /// is merely "not yet known" (no caller in this phase can supply one: <c>ProjectFile</c> has
    /// no duration column and the <c>MediaDerivative</c> table has no writer yet), so the clip
    /// degrades to unvalidated instead of being rejected. Rejecting it would fail every real
    /// document, which is a far worse defect than an unchecked source extent. Once a later phase
    /// starts populating <see cref="EditTimelineValidationContext.AssetDurationFrames"/> the
    /// overrun check re-arms automatically, with no change here.
    /// </para>
    /// </remarks>
    /// <summary>
    /// T-023: the media clips (<c>tracks[i].items[j]</c> paths) whose source length is NOT known, so the
    /// overrun check could not run for them. Never a violation (fail open) but reported, so the editor can say
    /// "length not checked" instead of silently passing. Mirrors the TS <c>uncheckedMediaClips</c>.
    /// </summary>
    public static IReadOnlyList<string> UncheckedMediaClips(EditTimeline document, IReadOnlyDictionary<AssetRef, long> assetDurationFrames)
    {
        List<string> paths = [];
        for (int t = 0; t < document.Tracks.Count; t++)
        {
            IReadOnlyList<Item> items = document.Tracks[t].Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is MediaClip clip
                    && clip.RecordOut > clip.RecordIn
                    && clip.AssetRef is { } reference
                    && (reference.ProjectFileId is not null || reference.StepResultId is not null)
                    && !assetDurationFrames.ContainsKey(reference))
                {
                    paths.Add($"tracks[{t}].items[{i}]");
                }
            }
        }

        return paths;
    }

    private static void ValidateMediaClipSourceExtent(
        MediaClip clip,
        string path,
        EditTimelineValidationContext context,
        List<(string Path, string Message)> violations)
    {
        long extent = clip.RecordOut - clip.RecordIn;
        if (extent <= 0)
        {
            // recordOut <= recordIn is already reported once, on recordOut; the arithmetic below
            // would only produce a second, redundant message about the same defect.
            return;
        }

        if (!context.AssetDurationFrames.TryGetValue(clip.AssetRef, out long availableFrames))
        {
            // Unknown duration = unvalidated, NOT a violation. See the remarks above; the
            // ownership maps are the asymmetric opposite and stay strict.
            return;
        }

        // The source frames the clip consumes: its record extent times the playback rate (a 2x clip
        // reads twice as much source, a 0.5x clip half), one frame for a freeze. Rounded UP is the
        // wrong way for a refusal, so a hair over is allowed: only a provable overrun is refused.
        double rate = double.IsFinite(clip.PlaybackRate) && clip.PlaybackRate > 0 ? clip.PlaybackRate : 1.0;
        long consumed = clip.Hold ? 1 : (long)Math.Floor(extent * rate);
        long requiredFrames = clip.SourceIn + Math.Max(1, consumed);
        if (requiredFrames > availableFrames)
        {
            string wording = context.FramesPerSecond is { } fps && fps > 0
                ? $"it needs {FormatSeconds(requiredFrames / fps)} of the source but the source is only {FormatSeconds(availableFrames / fps)} long"
                : $"it needs {requiredFrames} frames of the source but the source has only {availableFrames}";
            violations.Add((
                path + ".sourceIn",
                $"The clip \"{clip.Name}\" runs past the end of its source: {wording}. Shorten the clip or move its start earlier."));
        }
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>The channels of a <see cref="ClipTransform"/> (a media clip's, or a title's).</summary>
    private static void ValidateClipTransform(ClipTransform t, string path, List<(string Path, string Message)> violations)
    {
        ValidateKeyframedDouble(t.PositionX, path + ".positionX", -2, 2, violations);
        ValidateKeyframedDouble(t.PositionY, path + ".positionY", -2, 2, violations);
        ValidateKeyframedDouble(t.Scale, path + ".scale", 0, 8, violations, exclusiveMin: true);
        ValidateKeyframedDouble(t.ScaleY, path + ".scaleY", 0, 8, violations, exclusiveMin: true);
        ValidateKeyframedDouble(t.Rotation, path + ".rotation", -360, 360, violations);
        ValidateKeyframedDouble(t.Opacity, path + ".opacity", 0, 1, violations);
    }

    /// <summary>
    /// P5: the clip-depth fields of a <see cref="MediaClip"/> — transform channels, crop, fades,
    /// reverse and hold. Every number that can reach a filtergraph is range-checked HERE, so the
    /// renderer's builders only ever format values the validator already admitted.
    /// </summary>
    private static void ValidateMediaClipDepth(MediaClip media, string path, List<(string Path, string Message)> violations)
    {
        ValidateKeyframedDouble(media.GainDb, path + ".gainDb", -60, 20, violations);

        if (media.Transform is { } t)
        {
            ValidateClipTransform(t, path + ".transform", violations);
        }

        if (media.SourceRotation is { } sourceRotation && sourceRotation is not (0 or 90 or 180 or 270))
        {
            violations.Add((path + ".sourceRotation", $"sourceRotation must be 0, 90, 180 or 270 degrees clockwise, found {sourceRotation}"));
        }

        if (media.Crop is { } c)
        {
            foreach ((string name, double value) in new[] { ("left", c.Left), ("top", c.Top), ("right", c.Right), ("bottom", c.Bottom) })
            {
                if (!double.IsFinite(value) || value < 0 || value >= 1)
                {
                    violations.Add((path + ".crop." + name, $"crop {name} must be a fraction in [0, 1), found {value}"));
                }
            }

            if (c.Left + c.Right >= 1 || c.Top + c.Bottom >= 1)
            {
                violations.Add((path + ".crop", "crop must leave some picture: left+right and top+bottom must each stay below 1"));
            }
        }

        long duration = media.RecordOut - media.RecordIn;
        if (media.FadeInFrames < 0)
        {
            violations.Add((path + ".fadeInFrames", "fadeInFrames must not be negative"));
        }

        if (media.FadeOutFrames < 0)
        {
            violations.Add((path + ".fadeOutFrames", "fadeOutFrames must not be negative"));
        }

        if (media.FadeInFrames >= 0 && media.FadeOutFrames >= 0 && media.FadeInFrames + media.FadeOutFrames > duration)
        {
            violations.Add((path + ".fadeInFrames", $"the fades ({media.FadeInFrames} + {media.FadeOutFrames} frames) are longer than the clip ({duration} frames)"));
        }

        if (media.Reverse && media.Hold)
        {
            violations.Add((path + ".reverse", "a clip cannot be both reversed and held"));
        }

        if (media.Reverse && duration > MaxReverseFrames)
        {
            violations.Add((path + ".reverse", $"a reversed clip may span at most {MaxReverseFrames} frames, found {duration}"));
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SegmentationTrackIdPattern = new(@"^g[0-9]{1,6}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The segmentation variant of a clip mask: 1..8 distinct <c>g{n}</c> track ids, <c>featherPx</c> in [0, 64],
    /// and every id present in the same-project VideoAnalyze result named by <c>analysisStepResultId</c>.
    /// A foreign/missing result and an unknown track share one message (no cross-project existence oracle).
    /// </summary>
    private static void ValidateSegmentationMask(MediaClip clip, ShapeMask mask, string path, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        if (!double.IsFinite(mask.FeatherPx) || mask.FeatherPx < 0 || mask.FeatherPx > 64)
        {
            violations.Add((path + ".featherPx", $"mask featherPx must be in [0, 64], found {mask.FeatherPx}"));
        }

        IReadOnlyList<string> trackIds = mask.TrackIds ?? [];
        if (trackIds.Count < 1 || trackIds.Count > 8)
        {
            violations.Add((path + ".trackIds", $"a segmentation mask needs 1 to 8 trackIds, found {trackIds.Count}"));
        }

        if (trackIds.Any(id => id is null || !SegmentationTrackIdPattern.IsMatch(id)) || trackIds.Distinct(StringComparer.Ordinal).Count() != trackIds.Count)
        {
            violations.Add((path + ".trackIds", "segmentation trackIds must be distinct ids of the form g{n}"));
            return;
        }

        IReadOnlySet<string>? known = null;
        bool resolved = mask.AnalysisStepResultId is { } stepResultId
            && context.SegmentationTrackIds is { } lookup
            && lookup.TryGetValue(stepResultId, out known);
        if (!resolved || known is null || trackIds.Any(id => !known.Contains(id)))
        {
            violations.Add((path + ".analysisStepResultId", "the segmentation mask does not name a video analysis of this project with all of its tracks"));
            return;
        }

        // The same binding rule the render applies: right source, whole source, covering the clip's range.
        if (context.SegmentationTrackFacts is { } factsByResult && mask.AnalysisStepResultId is { } resultId
            && factsByResult.TryGetValue(resultId, out IReadOnlyDictionary<string, SegmentationTrackFacts>? trackFacts))
        {
            foreach (string id in trackIds)
            {
                if (trackFacts.TryGetValue(id, out SegmentationTrackFacts? f) && SegmentationMaskBinding.Check(clip, context.FramesPerSecond, f) is { } refusal)
                {
                    violations.Add((path + ".trackIds", "the segmentation mask does not fit this clip: " + refusal));
                    return;
                }
            }
        }
    }

    /// <summary>P7: the picture fields of a <see cref="MediaClip"/> — grade, mask, keyer, effects, reframe, retime quality, corner pin.</summary>
    private static void ValidateMediaClipPicture(MediaClip media, string path, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        ValidateGrade(media.Grade, path + ".grade", context, violations);
        ValidateEffects(media.Effects, path + ".effects", violations);
        ValidateRegionEffects(media, path + ".regionEffects", context, violations);
        ValidateKeyer(media.Keyer, path + ".keyer", violations);

        if (media.Mask is { } segmentation && segmentation.IsSegmentationMask())
        {
            ValidateSegmentationMask(media, segmentation, path + ".mask", context, violations);
            if (media.Stabilize)
            {
                violations.Add((path + ".stabilize", "a clip cannot be stabilised and carry a segmentation mask"));
            }
        }
        else if (media.Mask is { } mask)
        {
            if (!MaskKinds.Contains(mask.Kind ?? string.Empty))
            {
                violations.Add((path + ".mask.kind", $"mask kind '{mask.Kind}' must be rect or ellipse"));
            }

            foreach ((string name, double value) in new[] { ("x", mask.X), ("y", mask.Y), ("w", mask.W), ("h", mask.H) })
            {
                if (!double.IsFinite(value) || value < 0 || value > 1)
                {
                    violations.Add((path + ".mask." + name, $"mask {name} must be a fraction in [0, 1], found {value}"));
                }
            }

            if (!double.IsFinite(mask.Feather) || mask.Feather < 0 || mask.Feather > 0.5)
            {
                violations.Add((path + ".mask.feather", $"mask feather must be in [0, 0.5], found {mask.Feather}"));
            }
        }

        if (media.Reframe is { } reframe)
        {
            foreach ((string name, double value) in new[] { ("x", reframe.X), ("y", reframe.Y) })
            {
                if (!double.IsFinite(value) || value < 0 || value > 1)
                {
                    violations.Add((path + ".reframe." + name, $"reframe {name} must be a fraction in [0, 1], found {value}"));
                }
            }
        }

        if (media.BlendMode is not null && !BlendModes.IsKnown(media.BlendMode))
        {
            violations.Add((path + ".blendMode", $"blendMode '{media.BlendMode}' is not one of {string.Join(", ", BlendModes.Words)}"));
        }

        if (media.RetimeQuality is not null && !RetimeQualities.Contains(media.RetimeQuality))
        {
            violations.Add((path + ".retimeQuality", $"retimeQuality '{media.RetimeQuality}' must be frame or opticalFlow"));
        }

        if (media.CornerPin is { } pin)
        {
            foreach ((string name, QuadPoint? point) in new[] { ("topLeft", pin.TopLeft), ("topRight", pin.TopRight), ("bottomRight", pin.BottomRight), ("bottomLeft", pin.BottomLeft) })
            {
                if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < -1 || point.X > 2 || point.Y < -1 || point.Y > 2)
                {
                    violations.Add((path + ".cornerPin." + name, "corner pin corners must be finite canvas fractions in [-1, 2]"));
                }
            }
        }
    }

    /// <summary>P7: a grade stack — node count, node ids, primaries ranges, curves, LUT ownership and intensity.</summary>
    private static void ValidateGrade(GradeStack? stack, string path, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        if (stack?.Nodes is not { } nodes)
        {
            return;
        }

        if (nodes.Count > MaxGradeNodes)
        {
            violations.Add((path + ".nodes", $"a grade may carry at most {MaxGradeNodes} corrections, found {nodes.Count}"));
        }

        HashSet<Guid> ids = [];
        for (int i = 0; i < nodes.Count; i++)
        {
            GradeNode? node = nodes[i];
            string nodePath = $"{path}.nodes[{i}]";
            if (node is null)
            {
                violations.Add((nodePath, "grade node must not be null"));
                continue;
            }

            if (!ids.Add(node.Id))
            {
                violations.Add((nodePath + ".id", $"grade node id {node.Id} is duplicated"));
            }

            if (node.Primaries is { } p)
            {
                ValidateWheel(p.Lift, nodePath + ".primaries.lift", violations);
                ValidateWheel(p.Gamma, nodePath + ".primaries.gamma", violations);
                ValidateWheel(p.Gain, nodePath + ".primaries.gain", violations);
                ValidateWheel(p.Offset, nodePath + ".primaries.offset", violations);
                ValidateRange(p.Contrast, 0.25, 3, nodePath + ".primaries.contrast", violations);
                ValidateRange(p.Pivot, 0, 1, nodePath + ".primaries.pivot", violations);
                ValidateRange(p.Saturation, 0, 3, nodePath + ".primaries.saturation", violations);
                ValidateRange(p.Hue, -180, 180, nodePath + ".primaries.hue", violations);
                ValidateRange(p.Temperature, -1, 1, nodePath + ".primaries.temperature", violations);
                ValidateRange(p.Tint, -1, 1, nodePath + ".primaries.tint", violations);
                ValidateRange(p.Exposure, -4, 4, nodePath + ".primaries.exposure", violations);
            }

            if (node.Curves is { } c)
            {
                ValidateCurve(c.Y, nodePath + ".curves.y", violations);
                ValidateCurve(c.R, nodePath + ".curves.r", violations);
                ValidateCurve(c.G, nodePath + ".curves.g", violations);
                ValidateCurve(c.B, nodePath + ".curves.b", violations);
            }

            if (node.Lut is { } lut)
            {
                ValidateAssetRef(lut.File, nodePath + ".lut.file", context, violations);
                ValidateRange(lut.Intensity, 0, 1, nodePath + ".lut.intensity", violations);
            }

            if (node.Qualifier is { } q)
            {
                // P9: an HSL qualifier — hue centre [0, 360), half-width (0, 180] degrees, bands in [0, 1].
                if (!double.IsFinite(q.HueCenter) || q.HueCenter < 0 || q.HueCenter >= 360)
                {
                    violations.Add((nodePath + ".qualifier.hueCenter", $"hueCenter must be in [0, 360), found {q.HueCenter}"));
                }

                if (!double.IsFinite(q.HueWidth) || q.HueWidth <= 0 || q.HueWidth > 180)
                {
                    violations.Add((nodePath + ".qualifier.hueWidth", $"hueWidth must be in (0, 180], found {q.HueWidth}"));
                }

                ValidateRange(q.SatMin, 0, 1, nodePath + ".qualifier.satMin", violations);
                ValidateRange(q.SatMax, 0, 1, nodePath + ".qualifier.satMax", violations);
                ValidateRange(q.LumMin, 0, 1, nodePath + ".qualifier.lumMin", violations);
                ValidateRange(q.LumMax, 0, 1, nodePath + ".qualifier.lumMax", violations);
                ValidateRange(q.Softness, 0, 0.5, nodePath + ".qualifier.softness", violations);
                if (q.SatMin > q.SatMax) violations.Add((nodePath + ".qualifier.satMin", "satMin must not exceed satMax"));
                if (q.LumMin > q.LumMax) violations.Add((nodePath + ".qualifier.lumMin", "lumMin must not exceed lumMax"));
            }

            if (node.Window is { } window)
            {
                // P9: a (tracked) power window — the P7 mask rules, plus keyframed offsets in [-1, 1].
                ShapeMask? shape = window.Shape;
                if (shape is null)
                {
                    violations.Add((nodePath + ".window.shape", "a window needs a shape"));
                }
                else
                {
                    if (!MaskKinds.Contains(shape.Kind ?? string.Empty))
                    {
                        violations.Add((nodePath + ".window.shape.kind", $"window kind '{shape.Kind}' must be rect or ellipse"));
                    }

                    ValidateRange(shape.X, 0, 1, nodePath + ".window.shape.x", violations);
                    ValidateRange(shape.Y, 0, 1, nodePath + ".window.shape.y", violations);
                    ValidateRange(shape.W, 0, 1, nodePath + ".window.shape.w", violations);
                    ValidateRange(shape.H, 0, 1, nodePath + ".window.shape.h", violations);
                    ValidateRange(shape.Feather, 0, 0.5, nodePath + ".window.shape.feather", violations);
                }

                ValidateKeyframedDouble(window.OffsetX, nodePath + ".window.offsetX", -1, 1, violations);
                ValidateKeyframedDouble(window.OffsetY, nodePath + ".window.offsetY", -1, 1, violations);
            }
        }
    }

    private static void ValidateWheel(GradeWheel? wheel, string path, List<(string Path, string Message)> violations)
    {
        if (wheel is null) return;
        ValidateRange(wheel.R, -1, 1, path + ".r", violations);
        ValidateRange(wheel.G, -1, 1, path + ".g", violations);
        ValidateRange(wheel.B, -1, 1, path + ".b", violations);
        ValidateRange(wheel.Master, -1, 1, path + ".master", violations);
    }

    private static void ValidateCurve(IReadOnlyList<CurvePoint>? points, string path, List<(string Path, string Message)> violations)
    {
        if (points is null) return;
        if (points.Count > MaxCurvePoints)
        {
            violations.Add((path, $"a curve may carry at most {MaxCurvePoints} points, found {points.Count}"));
        }

        for (int i = 0; i < points.Count; i++)
        {
            CurvePoint? point = points[i];
            if (point is null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
            {
                violations.Add(($"{path}[{i}]", "curve points must have both coordinates in [0, 1]"));
                continue;
            }

            if (i > 0 && points[i - 1] is { } previous && point.X <= previous.X)
            {
                violations.Add(($"{path}[{i}].x", $"curve points must be strictly increasing by x; {point.X} does not follow {previous.X}"));
            }
        }
    }

    private static void ValidateEffects(IReadOnlyList<ClipEffect>? effects, string path, List<(string Path, string Message)> violations)
    {
        if (effects is null) return;
        if (effects.Count > MaxEffects)
        {
            violations.Add((path, $"at most {MaxEffects} effects may be applied, found {effects.Count}"));
        }

        for (int i = 0; i < effects.Count; i++)
        {
            ClipEffect? effect = effects[i];
            if (effect is null)
            {
                violations.Add(($"{path}[{i}]", "effect must not be null"));
                continue;
            }

            if (effect.Type is null || !EffectTypes.Contains(effect.Type))
            {
                violations.Add(($"{path}[{i}].type", $"effect '{effect.Type}' is not in the effects catalogue"));
            }

            ValidateRange(effect.Amount, 0, string.Equals(effect.Type, "blur", StringComparison.Ordinal) ? MaxBlurAmount : 1, $"{path}[{i}].amount", violations);
        }
    }

    private static void ValidateRegionEffects(MediaClip media, string path, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        if (media.RegionEffects is not { } effects) return;
        if (effects.Count > MaxRegionEffects)
        {
            violations.Add((path, $"at most {MaxRegionEffects} region effects may be applied, found {effects.Count}"));
        }

        for (int i = 0; i < effects.Count; i++)
        {
            RegionClipEffect? effect = effects[i];
            if (effect is null)
            {
                violations.Add(($"{path}[{i}]", "region effect must not be null"));
                continue;
            }

            if (effect.Effect is null || !RegionEffectWords.Contains(effect.Effect))
            {
                violations.Add(($"{path}[{i}].effect", $"region effect '{effect.Effect}' must be one of blur, pixelate, highlight, spotlight, colorpop, outline"));
            }

            ValidateRange(effect.Strength, 0, 1, $"{path}[{i}].strength", violations);

            if (effect.Mask is null || !effect.Mask.IsSegmentationMask())
            {
                violations.Add(($"{path}[{i}].mask", "a region effect needs a segmentation mask"));
            }
            else
            {
                ValidateSegmentationMask(media, effect.Mask, $"{path}[{i}].mask", context, violations);
            }
        }

        if (effects.Count > 0 && media.Stabilize)
        {
            violations.Add((path.Replace(".regionEffects", ".stabilize", StringComparison.Ordinal), "a clip cannot be stabilised and carry region effects"));
        }
    }

    private static void ValidateKeyer(ChromaKeyer? keyer, string path, List<(string Path, string Message)> violations)
    {
        if (keyer is null) return;
        if (keyer.Color is null || !IsHexColor(keyer.Color))
        {
            violations.Add((path + ".color", "key colour must be a #RRGGBB string"));
        }

        if (!double.IsFinite(keyer.Similarity) || keyer.Similarity <= 0 || keyer.Similarity > 1)
        {
            violations.Add((path + ".similarity", $"similarity must be in (0, 1], found {keyer.Similarity}"));
        }

        ValidateRange(keyer.Blend, 0, 1, path + ".blend", violations);
        ValidateRange(keyer.Spill, 0, 1, path + ".spill", violations);
    }

    private static void ValidateRange(double value, double min, double max, string path, List<(string Path, string Message)> violations)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            violations.Add((path, $"{path[(path.LastIndexOf('.') + 1)..]} must be in [{min}, {max}], found {value}"));
        }
    }

    /// <summary>P7: colour groups — count, unique ids, each clip in one group only (a member that no longer exists is tolerated), the group grade.</summary>
    private static void ValidateColorGroups(EditTimeline document, EditTimelineValidationContext context, List<(string Path, string Message)> violations)
    {
        if (document.ColorGroups is not { } groups) return;
        if (groups.Count > MaxColorGroups)
        {
            violations.Add(("colorGroups", $"a timeline may carry at most {MaxColorGroups} colour groups, found {groups.Count}"));
        }

        HashSet<Guid> groupIds = [];
        HashSet<Guid> seenMembers = [];
        for (int i = 0; i < groups.Count; i++)
        {
            ColorGroup? group = groups[i];
            string path = $"colorGroups[{i}]";
            if (group is null)
            {
                violations.Add((path, "colour group must not be null"));
                continue;
            }

            if (!groupIds.Add(group.Id))
            {
                violations.Add((path + ".id", $"colour group id {group.Id} is duplicated"));
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                violations.Add((path + ".name", "colour group name must not be empty"));
            }

            if (group.ClipIds is { } members)
            {
                for (int m = 0; m < members.Count; m++)
                {
                    // A member that no longer names a media clip is tolerated (the clip was removed
                    // after it was grouped) and inert — the same rule the TS engine applies, so a
                    // document the client holds valid is never refused here for that reason alone.
                    if (!seenMembers.Add(members[m]))
                    {
                        violations.Add(($"{path}.clipIds[{m}]", $"clip {members[m]} is in more than one colour group"));
                    }
                }
            }

            ValidateGrade(group.Grade, path + ".grade", context, violations);
        }
    }

    /// <summary>P5: the audio fields of a <see cref="Track"/> — fader, pan and EQ; P8 adds dynamics, denoise and leveler.</summary>
    private static void ValidateTrackAudio(Track track, string trackPath, List<(string Path, string Message)> violations) =>
        ValidateAudioChannel(track.GainDb, track.Pan, track.Eq, track.Dynamics, track.Denoise, track.Leveler, trackPath, violations);

    /// <summary>P8: the processing a track or a bus carries — one definition for both.</summary>
    private static void ValidateAudioChannel(
        Keyframed<double>? gainDb,
        double pan,
        TrackEq? eq,
        AudioDynamics? dynamics,
        AudioDenoise? denoise,
        AudioLeveler? leveler,
        string trackPath,
        List<(string Path, string Message)> violations)
    {
        ValidateKeyframedDouble(gainDb, trackPath + ".gainDb", -60, 20, violations);

        if (!double.IsFinite(pan) || pan < -1 || pan > 1)
        {
            violations.Add((trackPath + ".pan", $"pan must be in [-1, 1], found {pan}"));
        }

        if (dynamics is { } dyn)
        {
            if (dyn.Compressor is { } comp)
            {
                ValidateRange(comp.ThresholdDb, -60, 0, trackPath + ".dynamics.compressor.thresholdDb", violations);
                ValidateRange(comp.Ratio, 1, 20, trackPath + ".dynamics.compressor.ratio", violations);
                ValidateRange(comp.AttackMs, 1, 2000, trackPath + ".dynamics.compressor.attackMs", violations);
                ValidateRange(comp.ReleaseMs, 1, 2000, trackPath + ".dynamics.compressor.releaseMs", violations);
                ValidateRange(comp.MakeupDb, 0, 24, trackPath + ".dynamics.compressor.makeupDb", violations);
            }

            if (dyn.Limiter is { } lim)
            {
                ValidateRange(lim.CeilingDb, -24, 0, trackPath + ".dynamics.limiter.ceilingDb", violations);
            }

            if (dyn.Gate is { } gate)
            {
                ValidateRange(gate.ThresholdDb, -90, 0, trackPath + ".dynamics.gate.thresholdDb", violations);
                ValidateRange(gate.Ratio, 1, 20, trackPath + ".dynamics.gate.ratio", violations);
                ValidateRange(gate.AttackMs, 1, 2000, trackPath + ".dynamics.gate.attackMs", violations);
                ValidateRange(gate.ReleaseMs, 1, 2000, trackPath + ".dynamics.gate.releaseMs", violations);
            }

            if (dyn.Deesser is { } de)
            {
                ValidateRange(de.Intensity, 0, 1, trackPath + ".dynamics.deesser.intensity", violations);
            }
        }

        if (denoise is { } dn)
        {
            if (dn.Kind is null || !DenoiseKinds.Contains(dn.Kind))
            {
                violations.Add((trackPath + ".denoise.kind", $"denoise kind '{dn.Kind}' is not in the denoiser catalogue"));
            }

            ValidateRange(dn.Strength, 0, 1, trackPath + ".denoise.strength", violations);
        }

        if (leveler is { } lv)
        {
            if (lv.Kind is null || !LevelerKinds.Contains(lv.Kind))
            {
                violations.Add((trackPath + ".leveler.kind", $"leveler kind '{lv.Kind}' is not in the leveler catalogue"));
            }

            ValidateRange(lv.TargetDb, -40, 0, trackPath + ".leveler.targetDb", violations);
        }

        if (eq is null)
        {
            return;
        }

        if (eq.LowCutHz is { } low && (!double.IsFinite(low) || low < 20 || low > 20000))
        {
            violations.Add((trackPath + ".eq.lowCutHz", $"lowCutHz must be in [20, 20000], found {low}"));
        }

        if (eq.HighCutHz is { } high && (!double.IsFinite(high) || high < 20 || high > 20000))
        {
            violations.Add((trackPath + ".eq.highCutHz", $"highCutHz must be in [20, 20000], found {high}"));
        }

        if (eq.LowCutHz is { } l2 && eq.HighCutHz is { } h2 && l2 >= h2)
        {
            violations.Add((trackPath + ".eq", "lowCutHz must be below highCutHz"));
        }

        if (eq.Bands is { } bands)
        {
            if (bands.Count > MaxEqBands)
            {
                violations.Add((trackPath + ".eq.bands", $"at most {MaxEqBands} EQ bands, found {bands.Count}"));
            }

            for (int i = 0; i < bands.Count; i++)
            {
                EqBand? band = bands[i];
                string bandPath = $"{trackPath}.eq.bands[{i}]";
                if (band is null)
                {
                    violations.Add((bandPath, "band must not be null"));
                    continue;
                }

                if (!double.IsFinite(band.FrequencyHz) || band.FrequencyHz < 20 || band.FrequencyHz > 20000)
                {
                    violations.Add((bandPath + ".frequencyHz", $"frequencyHz must be in [20, 20000], found {band.FrequencyHz}"));
                }

                if (!double.IsFinite(band.GainDb) || band.GainDb < -24 || band.GainDb > 24)
                {
                    violations.Add((bandPath + ".gainDb", $"gainDb must be in [-24, 24], found {band.GainDb}"));
                }

                if (!double.IsFinite(band.Q) || band.Q <= 0 || band.Q > 10)
                {
                    violations.Add((bandPath + ".q", $"q must be in (0, 10], found {band.Q}"));
                }
            }
        }
    }

    /// <summary>
    /// P5: a <see cref="Keyframed{T}"/> number whose static value and every key value must be
    /// finite and inside <paramref name="min"/>..<paramref name="max"/>, with at most
    /// <see cref="MaxKeyframes"/> keys in strictly increasing frame order.
    /// </summary>
    private static void ValidateKeyframedDouble(
        Keyframed<double>? keyframed,
        string path,
        double min,
        double max,
        List<(string Path, string Message)> violations,
        bool exclusiveMin = false)
    {
        if (keyframed is null)
        {
            return;
        }

        bool InRange(double v) => double.IsFinite(v) && (exclusiveMin ? v > min : v >= min) && v <= max;

        // A missing JSON `value` deserializes as 0. For a range that excludes 0 (scale), 0 therefore
        // reads as "no static value" — the identity — rather than as a violation.
        bool valueUnset = exclusiveMin && keyframed.Value == 0;
        if (!valueUnset && !InRange(keyframed.Value))
        {
            violations.Add((path + ".value", $"value must be in {(exclusiveMin ? "(" : "[")}{min}, {max}], found {keyframed.Value}"));
        }

        if (keyframed.Keys is not { } keys)
        {
            return;
        }

        if (keys.Count > MaxKeyframes)
        {
            violations.Add((path + ".keys", $"a keyframed value must carry at most {MaxKeyframes} keyframes, found {keys.Count}"));
        }

        for (int i = 0; i < keys.Count; i++)
        {
            Keyframe<double>? key = keys[i];
            if (key is null)
            {
                violations.Add(($"{path}.keys[{i}]", "keyframe must not be null"));
                continue;
            }

            if (!InRange(key.Value))
            {
                violations.Add(($"{path}.keys[{i}].value", $"keyframe value must be in {(exclusiveMin ? "(" : "[")}{min}, {max}], found {key.Value}"));
            }

            if (i > 0 && keys[i - 1] is { } previous && key.Frame <= previous.Frame)
            {
                violations.Add(($"{path}.keys[{i}].frame", $"keyframes must be strictly increasing by frame; {key.Frame} does not follow {previous.Frame}"));
            }
        }
    }

    private static bool IsHexColor(string text)
    {
        if (text.Length != 7 || text[0] != '#')
        {
            return false;
        }

        for (int i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Enforces the per-<see cref="Keyframed{T}"/> keyframe bound.
    /// </summary>
    private static void ValidateQuadKeyframes(
        Keyframed<QuadCorners>? keyframed,
        string path,
        List<(string Path, string Message)> violations)
    {
        if (keyframed?.Keys is { } keys && keys.Count > MaxKeyframes)
        {
            violations.Add((
                path + ".keys",
                $"a keyframed value must carry at most {MaxKeyframes} keyframes, found {keys.Count}"));
        }
    }

    /// <summary>
    /// Enforces the "unsanitizable text" rule (see the remarks on <see cref="EditTimelineValidator"/>)
    /// for one <c>TitleClip</c>/<c>Caption</c> text field.
    /// </summary>
    private static void ValidateOverlayText(
        string? text,
        string path,
        List<(string Path, string Message)> violations)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            violations.Add((path, "text must not be null or whitespace-only"));
            return;
        }

        // The limit is irrelevant here: the only question is whether ANYTHING renderable would
        // survive sanitization, not how long the rendered string is.
        if (OverlayTextSanitizer.Sanitize(text, int.MaxValue).Length == 0)
        {
            violations.Add((
                path,
                "text contains no character that survives overlay-text sanitization, so nothing would render"));
        }
    }

    /// <summary>
    /// Enforces "sorted by <c>recordIn</c>" and "non-overlapping, with a <see cref="Transition"/>
    /// sitting exactly at the boundary between its neighbours" across one track's items.
    /// </summary>
    /// <remarks>
    /// The two rules are checked per adjacent pair. Once a pair is out of order the overlap test is
    /// SKIPPED for that pair: adjacency is meaningless in an unsorted list, so the overlap rule
    /// would flag a legitimately later predecessor and report a second, fabricated violation for a
    /// defect that has already been reported once.
    /// </remarks>
    private static void ValidateTrackOrdering(
        IReadOnlyList<Item> items,
        string trackPath,
        List<(string Path, string Message)> violations)
    {
        for (int i = 1; i < items.Count; i++)
        {
            Item? previous = items[i - 1];
            Item? current = items[i];

            if (previous is null || current is null)
            {
                continue;
            }

            string recordInPath = $"{trackPath}.items[{i}].recordIn";

            if (current.RecordIn < previous.RecordIn)
            {
                violations.Add((
                    recordInPath,
                    $"items must be sorted by recordIn, but recordIn {current.RecordIn} follows {previous.RecordIn}"));
                continue;
            }

            if (previous is Transition || current is Transition)
            {
                if (current.RecordIn != previous.RecordOut)
                {
                    violations.Add((
                        recordInPath,
                        $"a transition must sit exactly at the boundary between its neighbours: expected recordIn {previous.RecordOut}, found {current.RecordIn}"));
                }
            }
            else if (current.RecordIn < previous.RecordOut)
            {
                violations.Add((
                    recordInPath,
                    $"item overlaps the previous item: it starts at {current.RecordIn} but the previous item ends at {previous.RecordOut}"));
            }
        }
    }
}
