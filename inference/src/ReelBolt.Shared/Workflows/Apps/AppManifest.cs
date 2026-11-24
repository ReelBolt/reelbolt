using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The app manifest (version 1): how one workflow is presented as an app (docs/workflow-apps.md). Stored in
/// <c>workflow_definitions.app_manifest_json</c> when an author wrote one, otherwise derived from the steps by
/// <see cref="AppManifestDeriver"/> and never persisted. camelCase on the wire, unknown members refused (a
/// typo is an error, never silently ignored). A positional record: new members are APPENDED with a null default.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppManifest(
    int Version = 1,
    string? Title = null,
    string? Tagline = null,
    string? Category = null,
    string? SuggestedStylePreset = null,
    IReadOnlyList<AppManifestInput>? Inputs = null,
    AppManifestResult? Result = null,
    AppManifestAdvanced? Advanced = null,
    // ---- version 2 (docs/design/app-inputs.md 5.4), appended ----
    string? RequestTemplate = null);

/// <summary>
/// One input of an app. <see cref="Kind"/> is <c>brief</c> (a run-brief field, <see cref="Field"/>), <c>files</c>
/// (project files by <see cref="Role"/>), <c>notes</c> (the free-text request) or, from manifest version 2,
/// <c>input</c> (a typed question bound into step settings, docs/design/app-inputs.md 4). For brief inputs
/// <see cref="Label"/> and <see cref="Choices"/> are presentation only: values go through the run brief's own
/// validators. The members from <see cref="Id"/> on are version 2 and were APPENDED. <see cref="More"/> applies to
/// <see cref="Permission"/> marks the one system field (<see cref="MediaEgressPermission"/>): derived from the steps,
/// never stored, and refused when hand-written. <see cref="More"/> applies to
/// every kind: the app creator decides what sits under "More options" (owner decision 2026-11-06); when it is absent
/// <see cref="AppInputPlacement.IsUnderMore"/> gives the default for the input's kind and type.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppManifestInput(
    string Kind,
    string? Field = null,
    string? Role = null,
    string? Label = null,
    JsonElement? Default = null,
    IReadOnlyList<JsonElement>? Choices = null,
    double? Min = null,
    double? Max = null,
    // ---- version 2 (docs/design/app-inputs.md 4.1), appended ----
    string? Id = null,
    string? Type = null,
    string? Help = null,
    string? Example = null,
    bool? Required = null,
    int? MaxLength = null,
    double? Step = null,
    string? Unit = null,
    IReadOnlyList<AppInputOption>? Options = null,
    bool? More = null,
    AppInputShowWhen? ShowWhen = null,
    string? Capability = null,
    string? Job = null,
    IReadOnlyList<Guid>? Allowed = null,
    Guid? Recommended = null,
    Guid? Agent = null,
    string? OnLabel = null,
    string? OffLabel = null,
    string? LabelKey = null,
    string? HelpKey = null,
    string? ExampleKey = null,
    // ---- appended: the system permission field (docs/design/app-inputs.md "Permission to send media as an app field") ----
    string? Permission = null);

/// <summary>One option of a <c>choice</c> input: the value bound into the step (a string or a number) and its plain label.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppInputOption(JsonElement Value, string? Label = null, string? Hint = null, string? LabelKey = null);

/// <summary>
/// Display-only condition (docs/design/app-inputs.md 4.4): the input is shown only while the <c>toggle</c> or
/// <c>choice</c> input named by <see cref="Input"/> has the value <see cref="EqualsValue"/> (wire name <c>equals</c>).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppInputShowWhen(
    string Input,
    [property: JsonPropertyName("equals")] JsonElement EqualsValue);

/// <summary>
/// Where an input sits on the app page: shown at first, or under "More options". The author's <c>more</c> wins; with
/// none, essentials are shown (text, length, shape, files, notes, the avatar, voice, language and the format) and
/// models and fine-tuning go under "More options" (owner decision 2026-11-06).
/// </summary>
public static class AppInputPlacement
{
    private static readonly HashSet<string> BriefFieldsUnderMore = new(StringComparer.Ordinal)
    {
        BriefFieldConsumers.Resolution, BriefFieldConsumers.Tone, BriefFieldConsumers.DesignSystem,
        BriefFieldConsumers.OverlayCardAudio, BriefFieldConsumers.StylePreset, BriefFieldConsumers.SourceLanguage
    };

    /// <summary>True when the input goes under "More options".</summary>
    public static bool IsUnderMore(AppManifestInput input)
    {
        if (input.More is { } more)
            return more;
        return input.Kind switch
        {
            AppManifestVocabulary.KindBrief => input.Field is not null && BriefFieldsUnderMore.Contains(input.Field),
            AppManifestVocabulary.KindInput => AppInputTypes.TryParse(input.Type, out AppInputType type) && type == AppInputType.Model,
            _ => false
        };
    }
}

/// <summary>Which output the run page leads with: <c>video</c>, <c>image</c>, <c>audio</c> or <c>files</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppManifestResult(string Kind = "video");

/// <summary>Whether the graph ("Advanced") toggle is offered; false hides it for locked, curated apps.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppManifestAdvanced(bool AllowGraph = true);

/// <summary>The one JSON shape a manifest is stored and echoed in.</summary>
public static class AppManifestJson
{
    /// <summary>camelCase, nulls omitted, case-insensitive on read.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(AppManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    /// <summary>Parses a stored manifest; null for null/blank/garbage (a bad row degrades to the derived manifest).</summary>
    public static AppManifest? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AppManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The closed vocabularies a manifest draws from.</summary>
public static class AppManifestVocabulary
{
    public const int Version = 1;

    /// <summary>The version a manifest is written at when it carries any version-2 member.</summary>
    public const int Version2 = 2;

    /// <summary>At most this many inputs are shown before "More options" (the version-1 cap of inputs in all).</summary>
    public const int MaxInputs = 8;

    /// <summary>At most this many inputs in all, counting those under "More options" (version 2).</summary>
    public const int MaxInputsTotal = 32;
    public const int MaxTitle = 60;
    public const int MaxTagline = 140;
    public const int MaxLabel = 40;
    public const int MaxChoices = 12;
    public const int MaxFilesPerSlot = 50;

    public const string KindBrief = "brief";
    public const string KindFiles = "files";
    public const string KindNotes = "notes";
    public const string KindInput = "input";

    /// <summary>The values an input's <c>permission</c> member may take (only on the derived system field).</summary>
    public static readonly IReadOnlyList<string> Permissions = [MediaEgressPermission.Marker];

    public static readonly IReadOnlyList<string> Categories = ["promo", "footage", "brand", "utility"];
    public static readonly IReadOnlyList<string> ResultKinds = ["video", "image", "audio", "files"];
    public static readonly IReadOnlyList<string> FileRoles = ["video", "code", "music", "sfx", "image", "document", "insertContent", "overlay"];

    /// <summary>The run-brief fields an app may expose (the brief's own camelCase names).</summary>
    public static readonly IReadOnlyList<string> BriefFields =
    [
        BriefFieldConsumers.Language, BriefFieldConsumers.SourceLanguage, BriefFieldConsumers.LengthSec,
        BriefFieldConsumers.Format, BriefFieldConsumers.Resolution, BriefFieldConsumers.StylePreset,
        BriefFieldConsumers.Tone, BriefFieldConsumers.DesignSystem, BriefFieldConsumers.Voice,
        BriefFieldConsumers.OverlayCardAudio, BriefFieldConsumers.Avatar, BriefFieldConsumers.Pace
    ];
}
