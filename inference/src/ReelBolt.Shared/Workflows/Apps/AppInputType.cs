namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The type of an app input of kind <c>input</c> (docs/design/app-inputs.md 4.2). <b>APPEND ONLY</b>: persisted by
/// its wire name (<see cref="AppInputTypes.WireName"/>) in <c>workflow_definitions.app_manifest_json</c>.
/// </summary>
public enum AppInputType
{
    /// <summary>One line of text (<c>shortText</c>), at most 200 characters.</summary>
    ShortText,

    /// <summary>Multi-line text (<c>longText</c>): prompts, scripts. At most 7000 characters.</summary>
    LongText,

    /// <summary>A number (<c>number</c>) with optional min, max and step.</summary>
    Number,

    /// <summary>Whole seconds (<c>duration</c>), from a range or a list of choices.</summary>
    Duration,

    /// <summary>One of a closed list of options (<c>choice</c>).</summary>
    Choice,

    /// <summary>A switch (<c>toggle</c>): true or false.</summary>
    Toggle,

    /// <summary>A picture shape (<c>shape</c>): 9:16, 1:1, 16:9 or 4:5.</summary>
    Shape,

    /// <summary>A model (<c>model</c>): a capability id the server re-resolves (docs/design/app-inputs.md 8).</summary>
    Model
}

/// <summary>The closed vocabularies of app inputs of kind <c>input</c>.</summary>
public static class AppInputTypes
{
    public const int MaxShortText = 200;
    public const int MaxLongText = 7000;
    public const int MaxOptions = 12;
    public const int MaxHelp = 160;
    public const int MaxExample = 300;
    public const int MaxOptionLabel = 40;
    public const int MaxOptionHint = 80;
    public const int MaxRequestTemplate = 1000;
    public const int MaxAllowedModels = 12;

    /// <summary>The picture shapes a <c>shape</c> input offers.</summary>
    public static readonly IReadOnlyList<string> Shapes = ["9:16", "1:1", "16:9", "4:5"];

    /// <summary>The units a <c>number</c> input may show (none is null).</summary>
    public static readonly IReadOnlyList<string> Units = ["seconds", "takes", "percent"];

    /// <summary>The jobs a <c>model</c> input may filter by (docs/design/app-inputs.md 8.1).</summary>
    public static readonly IReadOnlyList<string> ModelJobs = ["textToVideo", "imageToVideo", "reference", "talkingHead", "avatarStill"];

    /// <summary>The capabilities a <c>model</c> input may choose from (by <c>InferenceProviderCapability</c> name).</summary>
    public static readonly IReadOnlyList<string> ModelCapabilities =
        ["VideoGeneration", "LipSync", "ImageEdit", "MusicGeneration", "SoundEffectGeneration", "SpeechSynthesis", "Chat", "Vision"];

    private static readonly Dictionary<string, AppInputType> ByWireName = new(StringComparer.Ordinal)
    {
        ["shortText"] = AppInputType.ShortText,
        ["longText"] = AppInputType.LongText,
        ["number"] = AppInputType.Number,
        ["duration"] = AppInputType.Duration,
        ["choice"] = AppInputType.Choice,
        ["toggle"] = AppInputType.Toggle,
        ["shape"] = AppInputType.Shape,
        ["model"] = AppInputType.Model
    };

    /// <summary>All wire names, in enum order.</summary>
    public static IReadOnlyList<string> WireNames => [.. Enum.GetValues<AppInputType>().Select(WireName)];

    /// <summary>The camelCase name an input's <c>type</c> member carries.</summary>
    public static string WireName(AppInputType type) => type switch
    {
        AppInputType.ShortText => "shortText",
        AppInputType.LongText => "longText",
        AppInputType.Number => "number",
        AppInputType.Duration => "duration",
        AppInputType.Choice => "choice",
        AppInputType.Toggle => "toggle",
        AppInputType.Shape => "shape",
        AppInputType.Model => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    /// <summary>Parses a wire name (exact, case-sensitive: a manifest is code-like data).</summary>
    public static bool TryParse(string? wireName, out AppInputType type)
    {
        type = default;
        return wireName is not null && ByWireName.TryGetValue(wireName, out type);
    }

    /// <summary>True for the two text types, the only ones that bind through <c>{{input.id}}</c> placeholders.</summary>
    public static bool IsText(AppInputType type) => type is AppInputType.ShortText or AppInputType.LongText;

    /// <summary>The largest <c>maxLength</c> a text type may declare.</summary>
    public static int TextCap(AppInputType type) => type == AppInputType.ShortText ? MaxShortText : MaxLongText;
}
