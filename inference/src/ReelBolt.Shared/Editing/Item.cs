using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Base type for timeline items (clips, effects, transitions, etc).
/// Uses JSON polymorphism with type discriminator "type".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MediaClip), "mediaClip")]
[JsonDerivedType(typeof(TitleClip), "titleClip")]
[JsonDerivedType(typeof(GeneratorClip), "generatorClip")]
[JsonDerivedType(typeof(InsertClip), "insertClip")]
[JsonDerivedType(typeof(Caption), "caption")]
[JsonDerivedType(typeof(Transition), "transition")]
[JsonDerivedType(typeof(CompoundClip), "compoundClip")]
[JsonDerivedType(typeof(MulticamClip), "multicamClip")]
[JsonDerivedType(typeof(AdjustmentClip), "adjustmentClip")]
public abstract record Item(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    Provenance? Provenance = null)
{
    /// <summary>
    /// The unique identifier for this item.
    /// </summary>
    public Guid Id { get; } = Id;

    /// <summary>
    /// Human-readable name for this item.
    /// </summary>
    public string Name { get; } = Name;

    /// <summary>
    /// Start position on the record (output) clock in frames.
    /// </summary>
    public long RecordIn { get; } = RecordIn;

    /// <summary>
    /// End position on the record (output) clock in frames (exclusive).
    /// </summary>
    public long RecordOut { get; } = RecordOut;

    /// <summary>
    /// How this item was created (agent decision, import, manual edit), or null when unknown.
    /// </summary>
    public Provenance? Provenance { get; } = Provenance;
}

/// <summary>
/// A media clip referencing external source content.
/// </summary>
public record MediaClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    AssetRef AssetRef,
    long SourceIn = 0,
    double PlaybackRate = 1.0,
    bool Enabled = true,
    Provenance? Provenance = null,
    Keyframed<double>? GainDb = null,
    ClipTransform? Transform = null,
    ClipCrop? Crop = null,
    bool Reverse = false,
    bool Hold = false,
    long FadeInFrames = 0,
    long FadeOutFrames = 0,
    GradeStack? Grade = null,
    ShapeMask? Mask = null,
    ChromaKeyer? Keyer = null,
    IReadOnlyList<ClipEffect>? Effects = null,
    bool Stabilize = false,
    ReframeTarget? Reframe = null,
    string RetimeQuality = "frame",
    QuadCorners? CornerPin = null,
    string ChannelMap = "stereo",
    string? LinkId = null,
    IReadOnlyList<RegionClipEffect>? RegionEffects = null,
    string? InputProfile = null,
    int? SourceRotation = null,
    string? BlendMode = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>
    /// Reference to the source media asset, resolvable against the timeline's own project.
    /// </summary>
    public AssetRef AssetRef { get; } = AssetRef;

    /// <summary>
    /// Offset into the source asset in frames.
    /// </summary>
    public long SourceIn { get; } = SourceIn;

    /// <summary>
    /// Playback speed multiplier.
    /// </summary>
    public double PlaybackRate { get; } = PlaybackRate;

    /// <summary>
    /// Whether this clip is enabled in the output.
    /// </summary>
    public bool Enabled { get; } = Enabled;

    /// <summary>
    /// Clip gain in dB. <c>Value</c> is the clip's static gain/level; <c>Keys</c>, when present, is
    /// a time-varying gain envelope that overrides <c>Value</c> from each keyframe's frame onward
    /// — the same clip-gain-plus-automation split a DAW uses.
    /// </summary>
    public Keyframed<double>? GainDb { get; } = GainDb;

    /// <summary>
    /// P5: the picture transform (position, scale, rotation, opacity), every channel optionally
    /// keyframed. <c>null</c> is the identity — a clip without a transform renders exactly as a P4
    /// clip did, fitted to the canvas with the document's aspect policy.
    /// </summary>
    public ClipTransform? Transform { get; } = Transform;

    /// <summary>P5: a static crop, as fractions of the source picture. <c>null</c> crops nothing.</summary>
    public ClipCrop? Crop { get; } = Crop;

    /// <summary>
    /// P5: play the source backwards. The clip then shows source position
    /// <c>sourceIn + (recordOut - frame) * playbackRate</c> at timeline <c>frame</c>, so
    /// <c>sourceIn</c> is still the lowest source frame the clip touches. Bounded by
    /// <see cref="EditTimelineValidator.MaxReverseFrames"/>, because reversing buffers the segment.
    /// </summary>
    public bool Reverse { get; } = Reverse;

    /// <summary>
    /// P5: a freeze frame — every timeline frame of the clip shows source frame <c>sourceIn</c>, and
    /// the clip contributes no audio. Mutually exclusive with <see cref="Reverse"/>.
    /// </summary>
    public bool Hold { get; } = Hold;

    /// <summary>P5: picture AND sound fade in over this many timeline frames from <c>recordIn</c>.</summary>
    public long FadeInFrames { get; } = FadeInFrames;

    /// <summary>P5: picture AND sound fade out over this many timeline frames before <c>recordOut</c>.</summary>
    public long FadeOutFrames { get; } = FadeOutFrames;

    /// <summary>P7: the clip's own grade stack, applied after its colour group's. <c>null</c> is no grade.</summary>
    public GradeStack? Grade { get; } = Grade;

    /// <summary>P7: a shape mask on the picture. <c>null</c> shows the whole picture.</summary>
    public ShapeMask? Mask { get; } = Mask;

    /// <summary>P7: a chroma keyer. <c>null</c> keys nothing.</summary>
    public ChromaKeyer? Keyer { get; } = Keyer;

    /// <summary>P7: effects from the closed catalogue, applied in order after the grade. <c>null</c>/empty is none.</summary>
    public IReadOnlyList<ClipEffect>? Effects { get; } = Effects;

    /// <summary>P7: stabilise the picture (two-pass <c>vidstab</c> at render time; not previewed).</summary>
    public bool Stabilize { get; } = Stabilize;

    /// <summary>P7: crop the source to the canvas aspect around a subject point. <c>null</c> keeps the aspect policy's fit.</summary>
    public ReframeTarget? Reframe { get; } = Reframe;

    /// <summary>P7: <c>"frame"</c> (duplicate/drop frames) or <c>"opticalFlow"</c> (motion-interpolated slow motion) — <see cref="EditTimelineValidator.RetimeQualities"/>.</summary>
    public string RetimeQuality { get; } = RetimeQuality;

    /// <summary>P7: a manual corner pin of the whole picture onto the canvas, corners in canvas fractions. <c>null</c> is no pin.</summary>
    public QuadCorners? CornerPin { get; } = CornerPin;

    /// <summary>
    /// P8: how the source's channels reach the stereo mix — <c>stereo</c> (as is, the default),
    /// <c>left</c>/<c>right</c> (that channel on both sides), <c>mono</c> (the sum on both sides) or
    /// <c>swap</c>. Words only; the mix resolves them.
    /// </summary>
    public string ChannelMap { get; } = ChannelMap;

    /// <summary>R08.1: clips sharing a link id form a link group (typically a video clip and its camera-audio companion). <c>null</c> is not linked.</summary>
    public string? LinkId { get; } = LinkId;

    /// <summary>Prompted region effects (blur, pixelate, ...) applied to the object a segmentation mask follows, in order. <c>null</c>/empty is none; at most <see cref="EditTimelineValidator.MaxRegionEffects"/>.</summary>
    public IReadOnlyList<RegionClipEffect>? RegionEffects { get; } = RegionEffects;

    /// <summary>
    /// P3: the camera log profile the source was shot in — one of <c>CameraProfiles.Ids</c>
    /// (<c>appleLog</c>, <c>sonySLog3</c>, …), or <c>null</c> for a source already in display
    /// space. Saved on the clip and exported; the renderer applies the matching input transform.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputProfile { get; } = InputProfile;

    /// <summary>
    /// An extra clockwise quarter turn of the SOURCE picture (0, 90, 180 or 270), on top of the
    /// display orientation the engine already autorotates to. Applied before crop and fit, so width
    /// and height swap for 90/270 and crop fractions refer to the rotated picture. <c>null</c>/0 is the identity.
    /// </summary>
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.Strict)]
    public int? SourceRotation { get; } = SourceRotation;

    /// <summary>
    /// How this clip's picture combines with what is composited below it: one of <see cref="BlendModes.Words"/>
    /// (JSON <c>blendMode</c>, a word, never a number). <c>null</c> or <c>"normal"</c> is plain alpha-over,
    /// byte-identical to a clip without the field. Video tracks only; on the bottom-most picture it blends over black.
    /// </summary>
    public string? BlendMode { get; } = BlendMode;
}

/// <summary>
/// A media clip's picture transform (P5). Every channel is a <see cref="Keyframed{T}"/> number so a
/// move, a zoom or a fade can be animated; a <c>null</c> channel is its identity value.
/// </summary>
/// <param name="PositionX">Horizontal offset of the clip's centre from the canvas centre, as a fraction of the canvas WIDTH (0 = centred, 0.5 = right edge at the centre). Range [-2, 2].</param>
/// <param name="PositionY">Vertical offset, as a fraction of the canvas HEIGHT, positive = down. Range [-2, 2].</param>
/// <param name="Scale">Uniform scale relative to the fitted size (1 = fitted). Range (0, 8].</param>
/// <param name="Rotation">Rotation about the clip's centre, degrees, positive = clockwise. Range [-360, 360].</param>
/// <param name="Opacity">Opacity, 0..1 (1 = opaque).</param>
/// <param name="ScaleY">Vertical scale, range (0, 8]. Absent = the same as <paramref name="Scale"/> (uniform); when present, <paramref name="Scale"/> is the horizontal scale.</param>
public sealed record ClipTransform(
    Keyframed<double>? PositionX = null,
    Keyframed<double>? PositionY = null,
    Keyframed<double>? Scale = null,
    Keyframed<double>? Rotation = null,
    Keyframed<double>? Opacity = null,
    Keyframed<double>? ScaleY = null)
{
    /// <summary>Vertical scale; <c>null</c> means "same as <see cref="Scale"/>".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Keyframed<double>? ScaleY { get; } = ScaleY;

    public Keyframed<double>? PositionX { get; } = PositionX;

    public Keyframed<double>? PositionY { get; } = PositionY;

    public Keyframed<double>? Scale { get; } = Scale;

    public Keyframed<double>? Rotation { get; } = Rotation;

    public Keyframed<double>? Opacity { get; } = Opacity;
}

/// <summary>
/// A static crop of a media clip's source picture (P5), each edge a fraction of the source's own
/// dimension in [0, 1). <c>Left + Right</c> and <c>Top + Bottom</c> must each stay below 1.
/// </summary>
public sealed record ClipCrop(double Left = 0, double Top = 0, double Right = 0, double Bottom = 0)
{
    public double Left { get; } = Left;

    public double Top { get; } = Top;

    public double Right { get; } = Right;

    public double Bottom { get; } = Bottom;
}

/// <summary>
/// A title/text clip with formatting.
/// </summary>
public record TitleClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    string Text,
    double FontSize = 48,
    string FontFamily = "Arial",
    string Color = "#FFFFFF",
    bool Enabled = true,
    Provenance? Provenance = null,
    string? Template = null,
    string? Position = null,
    ClipTransform? Transform = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>Free move/scale/rotate/opacity around the title's own anchor; <c>null</c> keeps today's drawtext line.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClipTransform? Transform { get; } = Transform;

    /// <summary>R10.5: where the title sits on a 3x3 pad (<see cref="EditTimelineValidator.TextPositions"/>); <c>null</c> is centred.</summary>
    public string? Position { get; } = Position;

    /// <summary>
    /// Title text content.
    /// </summary>
    public string Text { get; } = Text;

    /// <summary>P7: a title template from the closed catalogue (<see cref="EditTimelineValidator.TitleTemplates"/>); <c>null</c> is the plain centred title.</summary>
    public string? Template { get; } = Template;

    /// <summary>
    /// Font size in points.
    /// </summary>
    public double FontSize { get; } = FontSize;

    /// <summary>
    /// Font family name.
    /// </summary>
    public string FontFamily { get; } = FontFamily;

    /// <summary>
    /// Text color hex code.
    /// </summary>
    public string Color { get; } = Color;

    /// <summary>
    /// Whether this clip is enabled in the output.
    /// </summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// A generated clip (bars, noise, color, etc).
/// </summary>
public record GeneratorClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    string GeneratorType,
    Dictionary<string, object>? GeneratorConfig = null,
    bool Enabled = true,
    Provenance? Provenance = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>
    /// Type of generated content (e.g., "bars", "noise", "color").
    /// </summary>
    public string GeneratorType { get; } = GeneratorType;

    /// <summary>
    /// Configuration specific to the generator type.
    /// </summary>
    public Dictionary<string, object>? GeneratorConfig { get; } = GeneratorConfig;

    /// <summary>
    /// Whether this clip is enabled in the output.
    /// </summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// An insert clip for placing assets or composed content.
/// </summary>
public record InsertClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    AssetRef? AssetRef = null,
    Keyframed<QuadCorners>? Quad = null,
    bool Enabled = true,
    Provenance? Provenance = null,
    ChromaKeyer? Matte = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>
    /// Reference to an inserted asset.
    /// </summary>
    public AssetRef? AssetRef { get; } = AssetRef;

    /// <summary>P7: the matte parameters — the insert is keyed against the base with these before it is pinned. <c>null</c> is an opaque insert.</summary>
    public ChromaKeyer? Matte { get; } = Matte;

    /// <summary>
    /// Tracked corner-pin quad (keyframed over tracked frames), if this insert is corner-pinned.
    /// </summary>
    public Keyframed<QuadCorners>? Quad { get; } = Quad;

    /// <summary>
    /// Whether this clip is enabled in the output.
    /// </summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// A caption/subtitle clip with language support.
/// </summary>
public record Caption(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    string Text,
    string Language = "en",
    bool Enabled = true,
    Provenance? Provenance = null,
    string? Look = null,
    string? Position = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>R09.6: the burn-in look (<see cref="EditTimelineValidator.CaptionLooks"/>); <c>null</c> is the clean look.</summary>
    public string? Look { get; } = Look;

    /// <summary>R09.6: where the caption sits on a 3x3 pad (<see cref="EditTimelineValidator.TextPositions"/>); <c>null</c> is bottom centre.</summary>
    public string? Position { get; } = Position;

    /// <summary>
    /// Caption text content.
    /// </summary>
    public string Text { get; } = Text;

    /// <summary>
    /// Language code (e.g., "en", "es", "fr").
    /// </summary>
    public string Language { get; } = Language;

    /// <summary>
    /// Whether this caption is enabled in the output.
    /// </summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// A transition effect between clips.
/// </summary>
public record Transition(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    string TransitionType,
    string Easing = "linear",
    bool Enabled = true,
    Provenance? Provenance = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>
    /// Type of transition (e.g., "dissolve", "fade", "wipe").
    /// </summary>
    public string TransitionType { get; } = TransitionType;

    /// <summary>
    /// Easing function for the transition.
    /// </summary>
    public string Easing { get; } = Easing;

    /// <summary>
    /// Whether this transition is enabled.
    /// </summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// A point in source-frame coordinates, used by <see cref="QuadCorners"/>.
/// </summary>
public record QuadPoint(double X, double Y);

/// <summary>
/// The four corner points of a (possibly non-planar) quad, corner-pinning an insert onto a
/// tracked region of the source video.
/// </summary>
public record QuadCorners(
    QuadPoint TopLeft,
    QuadPoint TopRight,
    QuadPoint BottomRight,
    QuadPoint BottomLeft);

/// <summary>
/// P8: a compound clip — plays the <see cref="EditSequence"/> named by <see cref="SequenceId"/>
/// (from <see cref="EditTimeline.Sequences"/>) from sequence frame <see cref="SourceIn"/> for its
/// span. The renderer and the preview FLATTEN it (<see cref="TimelineFlattener"/>): the sequence's
/// tracks are laid over the clip's span, so nothing downstream knows nesting exists.
/// </summary>
public record CompoundClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    Guid SequenceId,
    long SourceIn = 0,
    bool Enabled = true,
    Provenance? Provenance = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>The sequence this clip plays; must exist in the document's <c>sequences</c>.</summary>
    public Guid SequenceId { get; } = SequenceId;

    /// <summary>Offset into the sequence, in frames.</summary>
    public long SourceIn { get; } = SourceIn;

    /// <summary>Whether this clip is enabled in the output.</summary>
    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// P8: a multicam clip — several synchronised <see cref="Angles"/> of the same moment, one of which
/// plays at any time: <see cref="ActiveAngleId"/> from the clip's start, then each <see cref="Cuts"/>
/// entry switches at its frame. Flattened into plain media clips by <see cref="TimelineFlattener"/>.
/// </summary>
public record MulticamClip(
    Guid Id,
    string Name,
    long RecordIn,
    long RecordOut,
    IReadOnlyList<MulticamAngle> Angles,
    Guid ActiveAngleId,
    IReadOnlyList<MulticamCut>? Cuts = null,
    long SourceIn = 0,
    bool Enabled = true,
    Provenance? Provenance = null,
    Keyframed<double>? GainDb = null,
    string? AudioSource = null
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>R08.2: whose sound the clip plays: <c>program</c> (absent; the first angle), an angle id of this clip, or <c>none</c>.</summary>
    public string? AudioSource { get; } = AudioSource;

    /// <summary>The synchronised angles (1..<see cref="EditTimelineValidator.MaxMulticamAngles"/>), each with its own source and sync offset.</summary>
    public IReadOnlyList<MulticamAngle> Angles { get; } = Angles;

    /// <summary>The angle playing from <c>recordIn</c> until the first cut.</summary>
    public Guid ActiveAngleId { get; } = ActiveAngleId;

    /// <summary>Angle switches in strictly increasing frame order, each strictly inside the span. <c>null</c>/empty is no switch.</summary>
    public IReadOnlyList<MulticamCut>? Cuts { get; } = Cuts;

    /// <summary>Offset into the synchronised multicam source, in frames.</summary>
    public long SourceIn { get; } = SourceIn;

    /// <summary>Whether this clip is enabled in the output.</summary>
    public bool Enabled { get; } = Enabled;

    /// <summary>Clip gain in dB for the active angle's audio, like <see cref="MediaClip.GainDb"/>.</summary>
    public Keyframed<double>? GainDb { get; } = GainDb;
}

/// <summary>
/// P7: an adjustment clip — a grade and effects applied to everything composited BELOW it on the
/// program for its span. It reads no source of its own.
/// </summary>
public record AdjustmentClip(
    Guid Id,
    string Name = "AdjustmentClip",
    long RecordIn = 0,
    long RecordOut = 0,
    Provenance? Provenance = null,
    GradeStack? Grade = null,
    IReadOnlyList<ClipEffect>? Effects = null,
    bool Enabled = true
) : Item(Id, Name, RecordIn, RecordOut, Provenance)
{
    /// <summary>The grade applied to the layers below.</summary>
    public GradeStack? Grade { get; } = Grade;

    /// <summary>Effects from the closed catalogue, applied after the grade.</summary>
    public IReadOnlyList<ClipEffect>? Effects { get; } = Effects;

    /// <summary>Whether this adjustment is applied in the output.</summary>
    public bool Enabled { get; } = Enabled;
}
