using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// One colour wheel of a primaries correction (P7): a per-channel offset plus a master, each in
/// [-1, 1] where 0 is "no change". Which filter parameter a wheel drives is the renderer's business
/// (<c>GradeFilterBuilder</c>); the document only carries the intent.
/// </summary>
public sealed record GradeWheel(double R = 0, double G = 0, double B = 0, double Master = 0)
{
    public double R { get; } = R;

    public double G { get; } = G;

    public double B { get; } = B;

    public double Master { get; } = Master;

    /// <summary>True when every value is 0 (a method, not a property: the schema drift guard maps every public property to a JSON field).</summary>
    public bool IsIdentity() => R == 0 && G == 0 && B == 0 && Master == 0;
}

/// <summary>
/// The primaries of one grade node (P7). Every field defaults to its identity, so a node that sets
/// nothing changes nothing. Ranges are enforced by <see cref="EditTimelineValidator"/>:
/// <c>Contrast</c> [0.25, 3], <c>Pivot</c> [0, 1], <c>Saturation</c> [0, 3], <c>Hue</c> degrees
/// [-180, 180], <c>Temperature</c>/<c>Tint</c> [-1, 1], <c>Exposure</c> stops [-4, 4].
/// </summary>
public sealed record GradePrimaries(
    GradeWheel? Lift = null,
    GradeWheel? Gamma = null,
    GradeWheel? Gain = null,
    GradeWheel? Offset = null,
    double Contrast = 1,
    double Pivot = 0.5,
    double Saturation = 1,
    double Hue = 0,
    double Temperature = 0,
    double Tint = 0,
    double Exposure = 0)
{
    public GradeWheel? Lift { get; } = Lift;

    public GradeWheel? Gamma { get; } = Gamma;

    public GradeWheel? Gain { get; } = Gain;

    public GradeWheel? Offset { get; } = Offset;

    public double Contrast { get; } = Contrast;

    public double Pivot { get; } = Pivot;

    public double Saturation { get; } = Saturation;

    public double Hue { get; } = Hue;

    public double Temperature { get; } = Temperature;

    public double Tint { get; } = Tint;

    public double Exposure { get; } = Exposure;

    public bool IsIdentity() =>
        (Lift?.IsIdentity() ?? true) && (Gamma?.IsIdentity() ?? true) && (Gain?.IsIdentity() ?? true) && (Offset?.IsIdentity() ?? true)
        && Contrast == 1 && Saturation == 1 && Hue == 0 && Temperature == 0 && Tint == 0 && Exposure == 0;
}

/// <summary>A point of a tone curve (P7), both coordinates in [0, 1].</summary>
public sealed record CurvePoint(double X, double Y)
{
    public double X { get; } = X;

    public double Y { get; } = Y;
}

/// <summary>
/// Custom curves (P7): a master (luma) curve and one per channel, each an ordered list of at most
/// <see cref="EditTimelineValidator.MaxCurvePoints"/> points with strictly increasing <c>X</c>. A
/// null or empty curve is the identity.
/// </summary>
public sealed record GradeCurves(
    IReadOnlyList<CurvePoint>? Y = null,
    IReadOnlyList<CurvePoint>? R = null,
    IReadOnlyList<CurvePoint>? G = null,
    IReadOnlyList<CurvePoint>? B = null)
{
    public IReadOnlyList<CurvePoint>? Y { get; } = Y;

    public IReadOnlyList<CurvePoint>? R { get; } = R;

    public IReadOnlyList<CurvePoint>? G { get; } = G;

    public IReadOnlyList<CurvePoint>? B { get; } = B;

    public bool IsIdentity() => IsFlat(Y) && IsFlat(R) && IsFlat(G) && IsFlat(B);

    private static bool IsFlat(IReadOnlyList<CurvePoint>? points) => points is null || points.Count == 0;
}

/// <summary>
/// A 3D LUT applied by a grade node (P7): a <c>.cube</c> project file, resolved and parsed server
/// side (<c>CubeLut</c>, size and value clamps) before ffmpeg ever sees it, blended toward identity
/// by <c>Intensity</c> in [0, 1].
/// </summary>
public sealed record GradeLut(AssetRef File, double Intensity = 1)
{
    public AssetRef File { get; } = File;

    public double Intensity { get; } = Intensity;
}

/// <summary>
/// P9: an HSL qualifier — the secondary selection a node's correction is confined to. Hue is in
/// degrees: pixels within <c>HueWidth</c> of <c>HueCenter</c> (around the colour wheel) qualify,
/// <c>HueWidth = 180</c> takes every hue; saturation and lightness are [0, 1] bands; <c>Softness</c>
/// (0..0.5) feathers every edge; <c>Invert</c> takes the complement. The identity (all hues, full
/// bands, not inverted) selects everything.
/// </summary>
public sealed record GradeQualifier(
    double HueCenter = 0,
    double HueWidth = 180,
    double SatMin = 0,
    double SatMax = 1,
    double LumMin = 0,
    double LumMax = 1,
    double Softness = 0.1,
    bool Invert = false)
{
    public double HueCenter { get; } = HueCenter;

    public double HueWidth { get; } = HueWidth;

    public double SatMin { get; } = SatMin;

    public double SatMax { get; } = SatMax;

    public double LumMin { get; } = LumMin;

    public double LumMax { get; } = LumMax;

    public double Softness { get; } = Softness;

    public bool Invert { get; } = Invert;

    /// <summary>True when the qualifier selects every pixel (a method so the drift guard sees no field).</summary>
    public bool SelectsAll() => HueWidth >= 180 && SatMin <= 0 && SatMax >= 1 && LumMin <= 0 && LumMax >= 1 && !Invert;
}

/// <summary>
/// P9: a power window — a shape (the P7 <see cref="ShapeMask"/>) the node's correction is confined to,
/// optionally TRACKED: <c>OffsetX</c>/<c>OffsetY</c> move the shape over time in canvas fractions
/// [-1, 1], keyframed in timeline frames (the feature tracker writes them).
/// </summary>
public sealed record GradeWindow(ShapeMask Shape, Keyframed<double>? OffsetX = null, Keyframed<double>? OffsetY = null)
{
    public ShapeMask Shape { get; } = Shape;

    public Keyframed<double>? OffsetX { get; } = OffsetX;

    public Keyframed<double>? OffsetY { get; } = OffsetY;
}

/// <summary>
/// One correction of a grade stack (P7): primaries, curves and/or a LUT, individually bypassable. P9:
/// an optional <see cref="Qualifier"/> and/or <see cref="Window"/> confine the correction to a
/// secondary selection; absent, the correction is whole-frame exactly as in P7.
/// </summary>
public sealed record GradeNode(
    Guid Id,
    string Name = "Correction",
    bool Enabled = true,
    GradePrimaries? Primaries = null,
    GradeCurves? Curves = null,
    GradeLut? Lut = null,
    GradeQualifier? Qualifier = null,
    GradeWindow? Window = null)
{
    public GradeQualifier? Qualifier { get; } = Qualifier;

    public GradeWindow? Window { get; } = Window;

    public Guid Id { get; } = Id;

    public string Name { get; } = Name;

    public bool Enabled { get; } = Enabled;

    public GradePrimaries? Primaries { get; } = Primaries;

    public GradeCurves? Curves { get; } = Curves;

    public GradeLut? Lut { get; } = Lut;
}

/// <summary>
/// A grade stack (P7): the ordered corrections of a clip, a colour group or an adjustment clip.
/// <c>Enabled = false</c> bypasses the whole stack without losing it — the before/after toggle is
/// this flag, and a bypassed grade is never baked into a render or a still.
/// </summary>
public sealed record GradeStack(bool Enabled = true, IReadOnlyList<GradeNode>? Nodes = null)
{
    public bool Enabled { get; } = Enabled;

    public IReadOnlyList<GradeNode>? Nodes { get; } = Nodes;
}

/// <summary>
/// A colour group (P7): clips graded together. The group's stack is applied before each member's
/// own stack. <c>LookGroupId</c> records the analysis look group (<c>k{n}</c>) the group was
/// pre-populated from, when it was.
/// </summary>
public sealed record ColorGroup(
    Guid Id,
    string Name,
    IReadOnlyList<Guid> ClipIds,
    GradeStack? Grade = null,
    string? LookGroupId = null)
{
    public Guid Id { get; } = Id;

    public string Name { get; } = Name;

    public IReadOnlyList<Guid> ClipIds { get; } = ClipIds;

    public GradeStack? Grade { get; } = Grade;

    public string? LookGroupId { get; } = LookGroupId;
}

/// <summary>
/// A shape mask (P7) on a clip's picture, in canvas fractions: the clip shows only inside (or,
/// inverted, outside) a rectangle or ellipse centred at (<c>X</c>, <c>Y</c>) of size
/// <c>W</c>×<c>H</c>, with a feathered edge of <c>Feather</c> (fraction of the canvas, [0, 0.5]).
/// <para>
/// The second variant has <c>Kind == "segmentation"</c>: the matte is the union of the named
/// segmentation tracks (<c>g{n}</c>) of a VideoAnalyze step result, with <c>Invert</c> and a
/// <c>FeatherPx</c> edge in [0, 64] pixels; X/Y/W/H/Feather do not apply. It is serialised as
/// <c>{kind, analysisStepResultId, trackIds, invert, featherPx}</c> by <see cref="ShapeMaskJsonConverter"/>;
/// a shape mask serialises exactly as before (no kind is added to a document that had none).
/// </para>
/// </summary>
[JsonConverter(typeof(ShapeMaskJsonConverter))]
public sealed record ShapeMask(
    string Kind = "rect",
    double X = 0.5,
    double Y = 0.5,
    double W = 0.5,
    double H = 0.5,
    double Feather = 0,
    bool Invert = false,
    Guid? AnalysisStepResultId = null,
    IReadOnlyList<string>? TrackIds = null,
    double FeatherPx = 0)
{
    public const string SegmentationKind = "segmentation";

    public string Kind { get; } = Kind;

    public double X { get; } = X;

    public double Y { get; } = Y;

    public double W { get; } = W;

    public double H { get; } = H;

    public double Feather { get; } = Feather;

    public bool Invert { get; } = Invert;

    /// <summary>Segmentation variant: the VideoAnalyze step result whose <c>SegmentationTracks</c> are named.</summary>
    public Guid? AnalysisStepResultId { get; } = AnalysisStepResultId;

    /// <summary>Segmentation variant: the track ids (<c>g{n}</c>), 1..8.</summary>
    public IReadOnlyList<string>? TrackIds { get; } = TrackIds;

    /// <summary>Segmentation variant: the edge feather in SOURCE pixels (the mask is blurred at the source's own frame size, before any crop or scale), [0, 64].</summary>
    public double FeatherPx { get; } = FeatherPx;

    public bool IsSegmentationMask() => string.Equals(Kind, SegmentationKind, StringComparison.Ordinal);
}

/// <summary>
/// Reads and writes the two <see cref="ShapeMask"/> variants. A shape mask keeps its exact legacy wire
/// form (kind, x, y, w, h, feather, invert — kind defaults to rect when absent); the segmentation
/// variant carries only its own fields.
/// </summary>
public sealed class ShapeMaskJsonConverter : JsonConverter<ShapeMask>
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private sealed record ShapeWire(string Kind = "rect", double X = 0.5, double Y = 0.5, double W = 0.5, double H = 0.5, double Feather = 0, bool Invert = false);

    private sealed record SegmentationWire(string Kind, Guid AnalysisStepResultId, IReadOnlyList<string> TrackIds, bool Invert, double FeatherPx);

    public override ShapeMask? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("a mask must be an object");
        }

        bool segmentation = root.TryGetProperty("kind", out JsonElement kind)
            && kind.ValueKind == JsonValueKind.String
            && kind.GetString() == ShapeMask.SegmentationKind;
        if (!segmentation)
        {
            ShapeWire w = root.Deserialize<ShapeWire>(Wire)!;
            return new ShapeMask(w.Kind, w.X, w.Y, w.W, w.H, w.Feather, w.Invert);
        }

        // The web client requires both; so does the wire here (a missing one is not a silent default).
        if (!root.TryGetProperty("invert", out _) || !root.TryGetProperty("featherPx", out _))
            throw new JsonException("a segmentation mask needs invert and featherPx");
        SegmentationWire s = root.Deserialize<SegmentationWire>(Wire)!;
        return new ShapeMask(ShapeMask.SegmentationKind, Invert: s.Invert, AnalysisStepResultId: s.AnalysisStepResultId, TrackIds: s.TrackIds, FeatherPx: s.FeatherPx);
    }

    public override void Write(Utf8JsonWriter writer, ShapeMask value, JsonSerializerOptions options)
    {
        if (value.IsSegmentationMask())
        {
            JsonSerializer.Serialize(writer, new SegmentationWire(value.Kind, value.AnalysisStepResultId ?? Guid.Empty, value.TrackIds ?? [], value.Invert, value.FeatherPx), Wire);
        }
        else
        {
            JsonSerializer.Serialize(writer, new ShapeWire(value.Kind, value.X, value.Y, value.W, value.H, value.Feather, value.Invert), Wire);
        }
    }
}

/// <summary>
/// A chroma keyer (P7): the key colour as <c>#RRGGBB</c>, <c>Similarity</c> (0, 1] and
/// <c>Blend</c> [0, 1] as ffmpeg's <c>colorkey</c> takes them, and <c>Spill</c> [0, 1] — how much of
/// the key colour's cast to pull out of the remaining pixels.
/// </summary>
public sealed record ChromaKeyer(string Color = "#00FF00", double Similarity = 0.3, double Blend = 0, double Spill = 0)
{
    public string Color { get; } = Color;

    public double Similarity { get; } = Similarity;

    public double Blend { get; } = Blend;

    public double Spill { get; } = Spill;
}

/// <summary>One effect of the closed catalogue (P7), <see cref="EditTimelineValidator.EffectTypes"/>, with an amount in [0, 1] ([0, 2] for blur: sigma = amount x 20).</summary>
public sealed record ClipEffect(string Type, double Amount = 0.5, bool Enabled = true)
{
    public string Type { get; } = Type;

    public double Amount { get; } = Amount;

    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// One region effect of a clip: a word from <see cref="EditTimelineValidator.RegionEffectWords"/> applied inside (or, with the mask's
/// <c>invert</c>, outside) the object a segmentation <see cref="Mask"/> follows. Strength is in [0, 1].
/// </summary>
[JsonConverter(typeof(RegionClipEffectJsonConverter))]
public sealed record RegionClipEffect(string Effect, double Strength, ShapeMask Mask, bool Enabled = true)
{
    public string Effect { get; } = Effect;

    public double Strength { get; } = Strength;

    /// <summary>Must be a segmentation mask (validated); the same value and binding rules as a clip's <c>mask</c>.</summary>
    public ShapeMask Mask { get; } = Mask;

    public bool Enabled { get; } = Enabled;
}

/// <summary>
/// Wire form of <see cref="RegionClipEffect"/>: effect, strength, mask and enabled are all required (a missing one is a
/// refusal, like a segmentation mask's invert/featherPx), so the server agrees with the schema and the web engine.
/// </summary>
public sealed class RegionClipEffectJsonConverter : JsonConverter<RegionClipEffect>
{
    public override RegionClipEffect? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("a region effect must be an object");
        }

        foreach (string name in new[] { "effect", "strength", "mask", "enabled" })
        {
            if (!root.TryGetProperty(name, out JsonElement member) || member.ValueKind == JsonValueKind.Null)
                throw new JsonException($"a region effect needs {name}");
        }

        if (root.GetProperty("effect").ValueKind != JsonValueKind.String
            || root.GetProperty("strength").ValueKind != JsonValueKind.Number
            || root.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException("a region effect needs a string effect, a numeric strength and a boolean enabled");
        }

        ShapeMask mask;
        try
        {
            mask = root.GetProperty("mask").Deserialize<ShapeMask>(options)!;
        }
        catch (JsonException ex)
        {
            // The inner deserialize reports "$" relative to the mask; rethrow without a path so the outer reader
            // locates the failure at this region effect (a converter cannot know its own prefix any finer).
            throw new JsonException("a region effect's mask is invalid: " + ex.Message);
        }

        return new RegionClipEffect(root.GetProperty("effect").GetString()!, root.GetProperty("strength").GetDouble(), mask, root.GetProperty("enabled").GetBoolean());
    }

    public override void Write(Utf8JsonWriter writer, RegionClipEffect value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("effect", value.Effect);
        writer.WriteNumber("strength", value.Strength);
        writer.WritePropertyName("mask");
        JsonSerializer.Serialize(writer, value.Mask, options);
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Auto-reframe (P7): crop the source to the canvas aspect around a subject point (canvas
/// fractions). The point is the editor's — pre-filled from the analysis subject rectangle where
/// one exists — never a number the renderer invents.
/// </summary>
public sealed record ReframeTarget(double X = 0.5, double Y = 0.5)
{
    public double X { get; } = X;

    public double Y { get; } = Y;
}
