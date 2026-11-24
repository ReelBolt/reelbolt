using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.Briefs;

/// <summary>Whether a run is narrated. <b>APPEND ONLY</b> - persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunBriefVoiceMode>))]
public enum RunBriefVoiceMode
{
    None,
    Narrated
}

/// <summary>What a group of source files is for. <b>APPEND ONLY</b> - persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunBriefFileRole>))]
public enum RunBriefFileRole
{
    Video,
    Code,
    Music,
    Sfx,
    Image,
    Document,

    /// <summary>What goes on a tracked screen (green-screen app): one picture or video. Appended.</summary>
    InsertContent,

    /// <summary>Pictures or videos to show over the film (transitional overlay): at most four. Appended.</summary>
    Overlay
}

/// <summary>The brief's voice choice: <see cref="Mode"/> plus, when narrated, which voice (at most one id).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunBriefVoice(
    RunBriefVoiceMode? Mode = null,
    Guid? ProjectVoiceId = null,
    Guid? LibraryVoiceId = null);

/// <summary>
/// The brief's avatar choice (avatars phase 2): the project avatar a <c>VideoGenerate</c> step that names none shows
/// in this run. Never turns on a step's permission to send media (that is given only in the app), never replaces an
/// avatar a step already names, never applies to a step with a first/last frame or a plan.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunBriefAvatar(Guid? AvatarId = null);

/// <summary>The project files of one role.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunBriefSourceFiles(
    RunBriefFileRole? Role = null,
    IReadOnlyList<Guid>? FileIds = null);

/// <summary>
/// The typed run brief (version 1), as it travels on the wire: one JSON object on REST, in the
/// assistant's <c>ExecuteWorkflow</c> tool and over MCP (docs/run-brief.md). Every field is optional;
/// absent means "no preference". A positional record, so new fields are APPENDED with a null default,
/// and unknown fields are refused rather than ignored. The API splits it: style, tone, format and
/// resolution go into the run's existing columns, the rest into <see cref="StoredRunBrief"/>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunBrief(
    string? Language = null,
    string? SourceLanguage = null,
    int? LengthSec = null,
    string? Format = null,
    string? Resolution = null,
    string? StylePreset = null,
    Guid? StyleId = null,
    StyleConfig? Style = null,
    StyleTone? Tone = null,
    string? DesignSystem = null,
    RunBriefVoice? Voice = null,
    IReadOnlyList<RunBriefSourceFiles>? SourceFiles = null,
    string? Notes = null,
    string? OverlayCardAudio = null,
    RunBriefAvatar? Avatar = null,
    /// <summary>
    /// The values of the app's inputs of kind <c>input</c>, by input id (docs/design/app-inputs.md 6.1). Shape-checked by
    /// <c>RunBriefValidator</c>; typed against the app's manifest by <c>AppInputValueChecker</c>. Appended.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    /// <summary>How busy the dynamic edit is (calm, medium, fast); overrides the style's dynamics. Appended.</summary>
    string? Pace = null)
{
    /// <summary>The schema version a stored brief is written at.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The designSystem value that runs without brand tokens.</summary>
    public const string DesignSystemNone = "none";

    /// <summary>The designSystem value that pins the project's approved design (the default).</summary>
    public const string DesignSystemProject = "project";

    /// <summary>The overlayCardAudio value that leaves the planner's per-card choice alone (the default).</summary>
    public const string OverlayCardAudioAgent = "agent";

    /// <summary>The overlayCardAudio value that keeps the film's sound under a full-frame picture.</summary>
    public const string OverlayCardAudioKeep = "keep";

    /// <summary>The overlayCardAudio value that mutes the film's sound under a full-frame picture.</summary>
    public const string OverlayCardAudioMute = "mute";

    /// <summary>The pace values (<c>calm</c>, <c>medium</c>, <c>fast</c>): how busy the dynamic edit is; overrides the style's dynamics.</summary>
    public const string PaceCalm = "calm";

    public const string PaceMedium = "medium";

    public const string PaceFast = "fast";
}

/// <summary>
/// The part of a <see cref="RunBrief"/> that has no column of its own, persisted on
/// <c>workflow_executions.run_brief_json</c> (engine-owned, nullable). Null column means the run was
/// started without any of these fields, which keeps it byte-identical to a run made before briefs existed.
/// </summary>
public sealed record StoredRunBrief(
    int Version = RunBrief.CurrentVersion,
    string? Language = null,
    string? SourceLanguage = null,
    int? LengthSec = null,
    string? DesignSystem = null,
    RunBriefVoice? Voice = null,
    IReadOnlyList<RunBriefSourceFiles>? SourceFiles = null,
    string? OverlayCardAudio = null,
    RunBriefAvatar? Avatar = null,
    /// <summary>The canonical app-input values the run binds (docs/design/app-inputs.md 6.1, 6.2 item 6). Appended.</summary>
    IReadOnlyDictionary<string, JsonElement>? Inputs = null,
    /// <summary>
    /// Who allowed sending media for this run (<c>app</c>, <c>api_key</c>, <c>mcp</c>, <c>assistant_card</c>): written by the
    /// run gate beside <c>inputs.allowMediaEgress</c>, and required by the engine before it honours that value. Appended.
    /// </summary>
    string? AllowMediaEgressBy = null,
    /// <summary>How busy the dynamic edit is (calm, medium, fast); overrides the style's dynamics. Appended.</summary>
    string? Pace = null)
{
    /// <summary>True when no stored field is set (nothing worth a column value).</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Language is null && SourceLanguage is null && LengthSec is null && DesignSystem is null
        && Voice is null && (SourceFiles is null || SourceFiles.Count == 0)
        && OverlayCardAudio is null && Avatar?.AvatarId is null && (Inputs is null || Inputs.Count == 0) && Pace is null;
}

/// <summary>The one JSON shape a run brief is stored and echoed in.</summary>
public static class RunBriefJson
{
    /// <summary>camelCase, nulls omitted, enum names.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(StoredRunBrief brief) => JsonSerializer.Serialize(brief, Options);

    /// <summary>Parses a stored brief; null for null/blank/garbage (a bad row degrades to "no brief", never a failed run).</summary>
    public static StoredRunBrief? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StoredRunBrief>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
