using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Review;

/// <summary>The kind of value a review adjustment carries.</summary>
public enum ReviewAdjustableValueKind
{
    /// <summary>A whole number inside <see cref="ReviewAdjustableSetting.Min"/>..<see cref="ReviewAdjustableSetting.Max"/>.</summary>
    Integer,

    /// <summary>A finite number inside the range, rounded to two decimals.</summary>
    Number,

    /// <summary>One of <see cref="ReviewAdjustableSetting.Choices"/> (an enum member name).</summary>
    Choice
}

/// <summary>How a setting's range is decided.</summary>
public enum ReviewAdjustableBound
{
    /// <summary><see cref="ReviewAdjustableSetting.Min"/>..<see cref="ReviewAdjustableSetting.Max"/>.</summary>
    Fixed,

    /// <summary>
    /// Within <see cref="ReviewAdjustableSettings.TargetLengthTolerance"/> of the run brief's length, else of the
    /// value the user saved; no base length means the setting cannot be changed.
    /// </summary>
    AroundTargetLength
}

/// <summary>
/// One step setting a ReviewLoop reviewer may change on the RUN's own snapshot (docs/design/review-loop-adjustments.md).
/// <see cref="Name"/> is the camelCase JSON name of a positional member of the step type's config record; the drift
/// test pins that every entry maps to a real member of the right CLR type.
/// </summary>
public sealed record ReviewAdjustableSetting(
    StepType StepType,
    string Name,
    ReviewAdjustableValueKind Kind,
    double Min,
    double Max,
    IReadOnlyList<string> Choices,
    string Description,
    ReviewAdjustableBound Bound = ReviewAdjustableBound.Fixed)
{
    /// <summary>The config record's property name (PascalCase).</summary>
    public string PropertyName => char.ToUpperInvariant(Name[0]) + Name[1..];
}

/// <summary>A validated value ready to be written, or why it was refused.</summary>
public sealed record ReviewAdjustmentValue(JsonNode? Value, string? Refused)
{
    public static ReviewAdjustmentValue Ok(JsonNode value) => new(value, null);

    public static ReviewAdjustmentValue No(string why) => new(null, why);
}

/// <summary>
/// The closed, typed allowlist of step settings a review loop may adjust, and the one place they are read, checked
/// and written. Every entry exists purely for quality: none adds a model or vendor call, changes egress consent, a
/// provider, a model, a spend cap, a take count or a prompt (see <see cref="ForbiddenReason"/>). Anything not listed
/// here is refused.
/// </summary>
public static class ReviewAdjustableSettings
{
    /// <summary>A target length may move at most this fraction away from its base (the brief, else the saved value).</summary>
    public const double TargetLengthTolerance = 0.25;

    /// <summary>The most adjustments one review round may carry; the rest are refused.</summary>
    public const int MaxPerRound = 6;

    /// <summary>The longest reason kept on a recorded adjustment.</summary>
    public const int MaxReasonChars = 200;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Every adjustable setting, in the order the reviewer is shown them.</summary>
    public static IReadOnlyList<ReviewAdjustableSetting> All { get; } =
    [
        new(StepType.VideoCompile, "overlayFontSizePct", ReviewAdjustableValueKind.Integer, 3, 12, [],
            "height of text the compile draws itself, in % of the frame height (no effect on a title rendered as a picture)"),
        // The legibility floor of overlay text AND rendered graphics; the graphics step reads it from the compile step's
        // config in the run snapshot, which is exactly where an adjustment lands. Use it for a title rendered as a picture.
        new(StepType.VideoCompile, "minOverlayCapHeightPct", ReviewAdjustableValueKind.Number, 1, 12, [],
            "smallest capital-letter height of any title, overlay text or rendered graphic, in % of the frame height"),
        new(StepType.VideoCompile, "overlayBoxHeightPct", ReviewAdjustableValueKind.Integer, 10, 30, [],
            "height of the box behind a title or overlay, in % of the frame height"),
        new(StepType.VideoCompile, "maxSfxCueSeconds", ReviewAdjustableValueKind.Number, 0.5, 4, [],
            "longest any single sound effect plays, in seconds (shorter cues stop overlapping)"),
        new(StepType.VideoCompile, "targetDurationSec", ReviewAdjustableValueKind.Integer, 1, 3600, [],
            "the length the cut aims for, in seconds",
            ReviewAdjustableBound.AroundTargetLength),
        new(StepType.VideoCompile, "minSegmentMs", ReviewAdjustableValueKind.Integer, 300, 3000, [],
            "shortest kept piece, in milliseconds (higher = fewer flash cuts)"),
        new(StepType.VideoCompile, "outputFit", ReviewAdjustableValueKind.Choice, 0, 0, ["Crop", "Pad", "Subject"],
            "how a source of another shape fills the frame"),
        new(StepType.VideoCompile, "subjectSafeArea", ReviewAdjustableValueKind.Number, 0, 0.15, [],
            "margin kept around the subject when outputFit is Subject, as a fraction of the frame"),
        new(StepType.VideoCompile, "captionFontSizePct", ReviewAdjustableValueKind.Integer, 3, 12, [],
            "caption text height, in % of the frame height (4 = automatic size)"),
        new(StepType.VideoCompile, "musicGainDb", ReviewAdjustableValueKind.Number, -20, 0, [],
            "background music level, in dB (0 = as mixed, lower = quieter)"),
        new(StepType.VideoCompile, "transitionPolicy", ReviewAdjustableValueKind.Choice, 0, 0, ["Off", "AudioOnly", "Auto", "Expressive"],
            "how cuts are treated (Off = hard cuts)"),
        new(StepType.VideoAnalyze, "maxOutputChars", ReviewAdjustableValueKind.Integer, 8000, 120000, [],
            "size budget of the analysis the editors read; too small drops placements and detail"),
        new(StepType.VideoAnalyze, "maxSubShotsPerShot", ReviewAdjustableValueKind.Integer, 1, 8, [],
            "most sub-shots one shot is split into (lower = smaller analysis)"),
    ];

    /// <summary>Words that mark a setting as never adjustable, whatever its step type (checked before the allowlist).</summary>
    private static readonly (string Fragment, string Why)[] ForbiddenFragments =
    [
        ("egress", "consent to send footage out is given only by the user"),
        ("provider", "the provider is chosen by the user"),
        ("model", "the model is chosen by the user"),
        ("spend", "spend caps are set by the user"),
        ("credit", "spend caps are set by the user"),
        ("budget", "spend caps are set by the user"),
        ("take", "the number of takes decides cost and is set by the user"),
        ("prompt", "prompts are written by the user"),
        ("voice", "voices need the user's consent"),
        ("maxclips", "the number of generated clips decides cost and is set by the user"),
        ("transcription", "transcription is a paid call the user turns on"),
        ("vision", "vision calls are paid calls the user turns on"),
        ("segmentation", "segmentation is a paid call the user turns on"),
    ];

    /// <summary>
    /// Words that mark a change-request key as never settable by a review, though a customer may set it: a review never
    /// writes a prompt, and nothing in a change catalogue may name consent, a provider, a model or a spend cap.
    /// </summary>
    private static readonly (string Fragment, string Why)[] ForbiddenChangeKeyFragments =
    [
        ("prompt", "prompts are written by the user"),
        ("egress", "consent to send footage out is given only by the user"),
        ("provider", "the provider is chosen by the user"),
        ("model", "the model is chosen by the user"),
        ("spend", "spend caps are set by the user"),
        ("credit", "spend caps are set by the user"),
        ("budget", "spend caps are set by the user"),
        ("maxclips", "the number of generated clips decides cost and is set by the user"),
        ("takes", "the number of takes decides cost and is set by the user"),
        ("voiceid", "voices need the user's consent"),
    ];

    /// <summary>Why a review may not set the change-request key <paramref name="key"/>, or null when nothing forbids it.</summary>
    public static string? ForbiddenChangeKeyReason(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        string lower = key.Trim().ToLowerInvariant();
        foreach ((string fragment, string why) in ForbiddenChangeKeyFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                return why;
        }

        return null;
    }

    /// <summary>
    /// Step types whose config settings are never adjustable (paid generation and synthesis). The ones that accept a
    /// change request (Voiceover, VideoGenerate, AudioGenerate, ThumbnailGenerate) are changed through it instead.
    /// </summary>
    public static IReadOnlySet<StepType> NeverAdjustableStepTypes { get; } = new HashSet<StepType>
    {
        StepType.VideoGenerate, StepType.AudioGenerate, StepType.Voiceover, StepType.ThumbnailGenerate,
        StepType.DesignGraphic, StepType.AudioCleanup, StepType.Revise,
    };

    /// <summary>The setting of <paramref name="stepType"/> named <paramref name="name"/> (case-insensitive), or null.</summary>
    public static ReviewAdjustableSetting? Find(StepType stepType, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(s => s.StepType == stepType && string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The settings of <paramref name="stepType"/>, in display order.</summary>
    public static IReadOnlyList<ReviewAdjustableSetting> For(StepType stepType) => [.. All.Where(s => s.StepType == stepType)];

    /// <summary>Why <paramref name="name"/> is never adjustable, or null when nothing forbids it by name.</summary>
    public static string? ForbiddenReason(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string lower = name.Trim().ToLowerInvariant();
        foreach ((string fragment, string why) in ForbiddenFragments)
        {
            if (lower.Contains(fragment, StringComparison.Ordinal))
                return why;
        }

        return null;
    }

    /// <summary>The config JSON of <paramref name="step"/> for an adjustable step type, or null.</summary>
    public static string? ConfigJson(WorkflowStep step) => step.StepType switch
    {
        StepType.VideoCompile => step.VideoCompileConfigJson,
        StepType.VideoAnalyze => step.VideoAnalyzeConfigJson,
        _ => null
    };

    /// <summary>Writes <paramref name="json"/> to the config column of <paramref name="step"/> (adjustable step types only).</summary>
    public static void SetConfigJson(WorkflowStep step, string json)
    {
        switch (step.StepType)
        {
            case StepType.VideoCompile:
                step.VideoCompileConfigJson = json;
                break;
            case StepType.VideoAnalyze:
                step.VideoAnalyzeConfigJson = json;
                break;
            default:
                throw new InvalidOperationException($"Step type {step.StepType} has no adjustable settings.");
        }
    }

    /// <summary>The config record type of an adjustable step type.</summary>
    public static Type? ConfigType(StepType stepType) => stepType switch
    {
        StepType.VideoCompile => typeof(VideoCompileStepConfig),
        StepType.VideoAnalyze => typeof(VideoAnalyzeStepConfig),
        _ => null
    };

    /// <summary>
    /// The value the setting has in <paramref name="configJson"/> (the record default when absent), as a JSON value:
    /// a number, an enum member name, or null for an unset optional member. Null also when the config cannot be read.
    /// </summary>
    public static JsonNode? ReadCurrent(ReviewAdjustableSetting setting, string? configJson)
    {
        object? config = Deserialize(setting.StepType, configJson);
        if (config is null)
            return null;
        PropertyInfo? property = config.GetType().GetProperty(setting.PropertyName);
        object? value = property?.GetValue(config);
        return value switch
        {
            null => null,
            Enum e => JsonValue.Create(e.ToString()),
            int i => JsonValue.Create(i),
            double d => JsonValue.Create(Math.Round(d, 3)),
            _ => null
        };
    }

    /// <summary>The value as the reviewer and the record show it ("unset" for null).</summary>
    public static string Display(JsonNode? value) => value switch
    {
        null => "unset",
        JsonValue v when v.TryGetValue(out string? s) => s ?? "unset",
        JsonValue v when v.TryGetValue(out double d) => d.ToString("0.###", CultureInfo.InvariantCulture),
        _ => value.ToJsonString()
    };

    /// <summary>
    /// The inclusive range a value must sit in. For <see cref="ReviewAdjustableBound.AroundTargetLength"/> it is
    /// built around <paramref name="baseLengthSec"/> and is null when there is no base length.
    /// </summary>
    public static (double Min, double Max)? Range(ReviewAdjustableSetting setting, int? baseLengthSec)
    {
        if (setting.Bound == ReviewAdjustableBound.Fixed)
            return (setting.Min, setting.Max);
        if (baseLengthSec is not > 0)
            return null;
        double low = Math.Ceiling(baseLengthSec.Value * (1 - TargetLengthTolerance));
        double high = Math.Floor(baseLengthSec.Value * (1 + TargetLengthTolerance));
        return (Math.Max(setting.Min, low), Math.Min(setting.Max, high));
    }

    /// <summary>Human words for the allowed values ("3..12", "Crop | Pad | Subject").</summary>
    public static string? RangeText(ReviewAdjustableSetting setting, int? baseLengthSec)
    {
        if (setting.Kind == ReviewAdjustableValueKind.Choice)
            return string.Join(" | ", setting.Choices);
        if (Range(setting, baseLengthSec) is not { } range)
            return null;
        return $"{range.Min.ToString("0.##", CultureInfo.InvariantCulture)}..{range.Max.ToString("0.##", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Parses and range-checks a proposed value. Never throws.</summary>
    public static ReviewAdjustmentValue Parse(ReviewAdjustableSetting setting, JsonNode? raw, int? baseLengthSec)
    {
        if (raw is not JsonValue value)
            return ReviewAdjustmentValue.No("no value was given");

        if (setting.Kind == ReviewAdjustableValueKind.Choice)
        {
            if (!value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
                return ReviewAdjustmentValue.No($"the value must be one of {string.Join(", ", setting.Choices)}");
            string? choice = setting.Choices.FirstOrDefault(c => string.Equals(c, text.Trim(), StringComparison.OrdinalIgnoreCase));
            return choice is null
                ? ReviewAdjustmentValue.No($"the value must be one of {string.Join(", ", setting.Choices)}")
                : ReviewAdjustmentValue.Ok(JsonValue.Create(choice));
        }

        double number;
        if (value.TryGetValue(out double d))
            number = d;
        else if (value.TryGetValue(out int i))
            number = i;
        else if (value.TryGetValue(out long l))
            number = l;
        else if (value.TryGetValue(out decimal m))
            number = (double)m;
        else if (value.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            number = parsed;
        else
            return ReviewAdjustmentValue.No("the value must be a number");

        if (!double.IsFinite(number))
            return ReviewAdjustmentValue.No("the value must be a finite number");

        if (Range(setting, baseLengthSec) is not { } range)
            return ReviewAdjustmentValue.No("this run has no target length to adjust around");

        if (setting.Kind == ReviewAdjustableValueKind.Integer)
        {
            if (Math.Abs(number - Math.Round(number)) > 1e-9)
                return ReviewAdjustmentValue.No("the value must be a whole number");
            number = Math.Round(number);
        }
        else
        {
            number = Math.Round(number, 2, MidpointRounding.AwayFromZero);
        }

        if (number < range.Min || number > range.Max)
            return ReviewAdjustmentValue.No($"the value must be between {RangeText(setting, baseLengthSec)}");

        return setting.Kind == ReviewAdjustableValueKind.Integer
            ? ReviewAdjustmentValue.Ok(JsonValue.Create((int)number))
            : ReviewAdjustmentValue.Ok(JsonValue.Create(number));
    }

    /// <summary>
    /// <paramref name="configJson"/> with the setting written, or null when the result does not read back as the step's
    /// config, reads back a different value, or gains a validation error it did not have.
    /// </summary>
    public static string? Apply(ReviewAdjustableSetting setting, string? configJson, JsonNode value)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(configJson)
                ? new JsonObject()
                : JsonNode.Parse(configJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return null;
        }

        // One spelling per member: drop any differently cased copy so the written value is the one read back.
        foreach (string key in root.Select(p => p.Key).Where(k => string.Equals(k, setting.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            root.Remove(key);
        root[setting.Name] = value.DeepClone();
        string json = root.ToJsonString();

        object? before = Deserialize(setting.StepType, configJson);
        object? after = Deserialize(setting.StepType, json);
        if (after is null)
            return null;
        if (!JsonNode.DeepEquals(ReadCurrent(setting, json), value))
            return null;

        IReadOnlyList<string> errorsBefore = Errors(before);
        if (Errors(after).Any(e => !errorsBefore.Contains(e)))
            return null;

        return json;
    }

    /// <summary>The step's config record, or null when the JSON cannot be read (absent JSON reads as the defaults).</summary>
    public static object? Deserialize(StepType stepType, string? configJson)
    {
        Type? type = ConfigType(stepType);
        if (type is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson, type, ConfigJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Errors(object? config) => config switch
    {
        VideoCompileStepConfig compile => compile.Validate(),
        _ => []
    };
}
