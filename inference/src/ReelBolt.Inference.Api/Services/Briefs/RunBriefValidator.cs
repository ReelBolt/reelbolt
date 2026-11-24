using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Services.Briefs;

/// <summary>
/// The members of the legacy run request a <see cref="RunBrief"/> field can duplicate. Sending both a
/// legacy member and the same brief field is refused (docs/run-brief.md, "One request shape").
/// </summary>
public sealed record LegacyRunInputs(
    Guid? StyleId = null,
    string? StylePresetKey = null,
    StyleConfig? Style = null,
    string? OutputFormat = null,
    string? OutputResolution = null,
    string? UserRequest = null);

/// <summary>
/// The one set of rules a run brief must satisfy, shared by the REST execute endpoints, the assistant's
/// <c>ExecuteWorkflow</c> tool (and so MCP) so the three can never disagree about what is a valid brief.
/// Shape checks only: style, format and resolution values are resolved by <c>RunStyleResolver</c>, which
/// stays their only resolver. Modelled on <see cref="Styles.EditingStyleRules"/>.
/// </summary>
public static partial class RunBriefValidator
{
    public const int MinLengthSec = 5;
    public const int MaxLengthSec = 600;

    /// <summary>At most this many files in one role, and in the whole brief.</summary>
    public const int MaxFilesPerRole = 50;
    public const int MaxFilesTotal = 200;

    /// <summary>The overlay role's own cap: at most four pictures or videos per run.</summary>
    public const int MaxOverlayFiles = 4;

    /// <summary>At most this many app-input values in one brief (docs/design/app-inputs.md 6.1).</summary>
    public const int MaxInputs = 32;

    /// <summary>The largest one app-input value may be, as JSON text (8 KB).</summary>
    public const int MaxInputBytes = 8 * 1024;

    /// <summary>The largest all app-input values may be together, as JSON text (32 KB).</summary>
    public const int MaxInputsBytes = 32 * 1024;

    /// <summary>The longest tag accepted (RFC 5646 recommends supporting 35 characters).</summary>
    public const int MaxLanguageLength = 35;

    // BCP-47 shape: language (2-3 letters with up to 3 extended-language subtags, or a 4-8 letter registered one),
    // optional script, region, variants, extensions and a private-use tail. Any tag of that shape is accepted;
    // whether an agent can write it is the agent's business, not the schema's. Every part is separated by '-', and
    // the length is capped before the pattern runs.
    [GeneratedRegex("^(?:[A-Za-z]{2,3}(?:-[A-Za-z]{3}){0,3}|[A-Za-z]{4,8})(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?(?:-(?:[A-Za-z0-9]{5,8}|[0-9][A-Za-z0-9]{3}))*(?:-[0-9A-WY-Za-wy-z](?:-[A-Za-z0-9]{2,8})+)*(?:-[xX](?:-[A-Za-z0-9]{1,8})+)?$")]
    private static partial Regex LanguageShape();

    /// <summary>
    /// Parses the brief an assistant or MCP call passes as a JSON value. Null/absent is "no brief". An unknown
    /// field, a wrong type or an unknown enum name is a plain-language error, never an exception.
    /// </summary>
    public static (RunBrief? Brief, string? Error) ParseJson(JsonElement? json)
    {
        if (json is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return (null, null);
        if (element.ValueKind != JsonValueKind.Object)
            return (null, "The brief must be an object, for example {\"language\":\"it\",\"lengthSec\":45}.");

        try
        {
            RunBrief? brief = element.Deserialize<RunBrief>(RunBriefJson.Options);
            return brief is null ? (null, null) : (brief, null);
        }
        catch (JsonException ex)
        {
            string where = string.IsNullOrWhiteSpace(ex.Path) || ex.Path == "$" ? "the brief" : $"'{ex.Path.TrimStart('$', '.')}'";
            return (null, $"The brief has a field or value that is not recognised at {where}. Use only the brief fields and values documented for a run brief.");
        }
    }

    /// <summary>
    /// Validates a brief on its own. Returns the first plain-language problem, or null when it is valid.
    /// </summary>
    public static string? Validate(RunBrief brief)
    {
        if (brief.Language is not null && !TryNormalizeLanguage(brief.Language, out _))
            return LanguageMessage("language", brief.Language);
        if (brief.SourceLanguage is not null && !TryNormalizeLanguage(brief.SourceLanguage, out _))
            return LanguageMessage("sourceLanguage", brief.SourceLanguage);

        if (brief.LengthSec is { } length && (length < MinLengthSec || length > MaxLengthSec))
            return $"lengthSec must be between {MinLengthSec} and {MaxLengthSec} seconds.";

        if (brief.Tone is { } tone && !Enum.IsDefined(tone))
            return "tone is not one of Calm, Upbeat, Energetic, Serious, Playful or Inspirational.";

        if (brief.DesignSystem is not null && NormalizeDesignSystem(brief.DesignSystem) is null && !IsBlank(brief.DesignSystem))
            return "designSystem must be 'project' (the project's approved design, the default) or 'none' (no brand tokens).";

        if (!IsBlank(brief.OverlayCardAudio) && NormalizeOverlayCardAudio(brief.OverlayCardAudio) is null)
            return "overlayCardAudio must be 'agent', 'keep' or 'mute'.";

        if (brief.Avatar is { } avatar && (avatar.AvatarId is null || avatar.AvatarId == Guid.Empty))
            return "avatar.avatarId must be the id of an avatar of this project.";
        if (!IsBlank(brief.Pace) && NormalizePace(brief.Pace) is null)
            return "pace must be 'calm', 'medium' or 'fast'.";

        if (brief.Voice is { } voice)
        {
            if (voice.Mode is { } mode && !Enum.IsDefined(mode))
                return "voice.mode must be 'none' or 'narrated'.";
            if (voice.ProjectVoiceId == Guid.Empty || voice.LibraryVoiceId == Guid.Empty)
                return "voice ids must be real ids.";
            if (voice.ProjectVoiceId is not null && voice.LibraryVoiceId is not null)
                return "Choose one voice: a project voice or a library voice, not both.";
            if ((voice.ProjectVoiceId is not null || voice.LibraryVoiceId is not null) && voice.Mode != RunBriefVoiceMode.Narrated)
                return "A voice can only be chosen when voice.mode is 'narrated'.";
        }

        if (ValidateInputsShape(brief.Inputs) is { } inputsProblem)
            return inputsProblem;

        if (brief.SourceFiles is { } groups)
        {
            var roles = new HashSet<RunBriefFileRole>();
            int total = 0;
            foreach (RunBriefSourceFiles group in groups)
            {
                if (group is null || group.Role is not { } role || !Enum.IsDefined(role))
                    return "Each sourceFiles entry needs a role: video, code, music, sfx, image, document, insertContent or overlay.";
                if (!roles.Add(role))
                    return $"sourceFiles lists the role '{WireName(role)}' more than once; put all its files in one entry.";
                if (group.FileIds is not { Count: > 0 } ids)
                    return $"sourceFiles entry '{WireName(role)}' needs at least one file id.";
                if (ids.Any(i => i == Guid.Empty))
                    return "sourceFiles file ids must be real ids.";
                if (role == RunBriefFileRole.InsertContent && ids.Count != 1)
                    return "sourceFiles entry 'insertcontent' takes exactly one file: the picture or video that goes on the screen.";
                if (role == RunBriefFileRole.Overlay && ids.Count > MaxOverlayFiles)
                    return $"sourceFiles entry 'overlay' takes at most {MaxOverlayFiles} pictures or videos.";
                if (ids.Count > MaxFilesPerRole)
                    return $"At most {MaxFilesPerRole} files per role.";
                total += ids.Count;
            }

            if (total > MaxFilesTotal)
                return $"At most {MaxFilesTotal} source files per run.";
        }

        return null;
    }

    /// <summary>
    /// The shape of <c>brief.inputs</c> only (docs/design/app-inputs.md 6.1): at most <see cref="MaxInputs"/> entries,
    /// ids shaped like input ids, each value a string, number or boolean (or null for "left empty"), at most
    /// <see cref="MaxInputBytes"/> each and <see cref="MaxInputsBytes"/> together. Whether the ids exist and the values
    /// fit is checked against the app's manifest by <c>AppInputValueChecker</c>.
    /// </summary>
    public static string? ValidateInputsShape(IReadOnlyDictionary<string, JsonElement>? inputs)
    {
        if (inputs is null)
            return null;
        if (inputs.Count > MaxInputs)
            return $"inputs can carry at most {MaxInputs} values.";
        int total = 0;
        foreach ((string id, JsonElement value) in inputs)
        {
            if (!ReelBolt.Shared.Workflows.Apps.AppInputPlaceholders.IdPattern().IsMatch(id))
                return "Each inputs entry must be named by an input id: a lower-case letter then letters and digits, at most 32 characters.";
            if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                return $"inputs.{id} must be text, a number, true or false.";
            int size = System.Text.Encoding.UTF8.GetByteCount(value.GetRawText());
            if (size > MaxInputBytes)
                return $"inputs.{id} is too large.";
            total += size;
        }

        return total > MaxInputsBytes ? "inputs are too large together." : null;
    }

    /// <summary>
    /// A legacy member and the same brief field both set is a 400, the same way choosing two styles is. The
    /// style members are compared member to member; two different ways of choosing a style (a preset in the
    /// brief and a saved style in the legacy members) are left to the style resolver's "choose one style".
    /// </summary>
    public static string? FindLegacyConflict(RunBrief brief, LegacyRunInputs legacy)
    {
        if (brief.StyleId is not null && legacy.StyleId is not null)
            return Conflict("styleId");
        if (!IsBlank(brief.StylePreset) && !IsBlank(legacy.StylePresetKey))
            return Conflict("stylePreset");
        if (brief.Style is not null && legacy.Style is not null)
            return Conflict("style");
        if (!IsBlank(brief.Format) && !IsBlank(legacy.OutputFormat))
            return Conflict("format");
        if (!IsBlank(brief.Resolution) && !IsBlank(legacy.OutputResolution))
            return Conflict("resolution");
        if (!IsBlank(brief.Notes) && !IsBlank(legacy.UserRequest))
            return Conflict("notes");
        return null;
    }

    /// <summary>
    /// A language tag in its canonical casing (<c>it</c>, <c>es-MX</c>, <c>zh-Hant-TW</c>, <c>de-DE-1996</c>), or
    /// false when it is not shaped like a BCP-47 tag.
    /// </summary>
    public static bool TryNormalizeLanguage(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string tag = value.Trim();
        if (tag.Length > MaxLanguageLength || !LanguageShape().IsMatch(tag))
            return false;

        string[] parts = tag.Split('-');
        bool tail = false;
        bool scriptOrRegionOpen = true;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (i == 0 || tail)
            {
                parts[i] = part.ToLowerInvariant();
                continue;
            }

            if (part.Length == 1)
                tail = true;

            bool alpha4 = part.Length == 4 && part.All(char.IsAsciiLetter);
            bool region = (part.Length == 2 && part.All(char.IsAsciiLetter)) || (part.Length == 3 && part.All(char.IsAsciiDigit));
            if (scriptOrRegionOpen && alpha4)
                parts[i] = char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            else if (scriptOrRegionOpen && region)
            {
                parts[i] = part.ToUpperInvariant();
                scriptOrRegionOpen = false;
            }
            else
                parts[i] = part.ToLowerInvariant();

            if (part.Length >= 5)
                scriptOrRegionOpen = false;
        }

        normalized = string.Join('-', parts);
        return true;
    }

    /// <summary>
    /// <c>none</c> for "no brand tokens", <c>project</c> for the default, null for anything else (including
    /// blank). A library or version id is not a v1 value.
    /// </summary>
    public static string? NormalizeDesignSystem(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        RunBrief.DesignSystemNone => RunBrief.DesignSystemNone,
        RunBrief.DesignSystemProject => RunBrief.DesignSystemProject,
        _ => null
    };

    /// <summary><c>agent</c>, <c>keep</c> or <c>mute</c> (case-insensitive); null for anything else, including blank.</summary>
    public static string? NormalizeOverlayCardAudio(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        RunBrief.OverlayCardAudioAgent => RunBrief.OverlayCardAudioAgent,
        RunBrief.OverlayCardAudioKeep => RunBrief.OverlayCardAudioKeep,
        RunBrief.OverlayCardAudioMute => RunBrief.OverlayCardAudioMute,
        _ => null
    };

    /// <summary><c>calm</c>, <c>medium</c> or <c>fast</c> (case-insensitive); null for anything else, including blank.</summary>
    public static string? NormalizePace(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        RunBrief.PaceCalm => RunBrief.PaceCalm,
        RunBrief.PaceMedium => RunBrief.PaceMedium,
        RunBrief.PaceFast => RunBrief.PaceFast,
        _ => null
    };

    /// <summary>The file role's wire spelling: the persisted enum name, camelCased for <c>insertContent</c>.</summary>
    private static string WireName(RunBriefFileRole role) =>
        role == RunBriefFileRole.InsertContent ? "insertContent" : role.ToString().ToLowerInvariant();

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Conflict(string field) =>
        $"'{field}' is set both inside the brief and as a separate request field. Send it in one place only.";

    private static string LanguageMessage(string field, string value) =>
        $"{field} '{(value.Length > 40 ? value[..40] + "..." : value)}' is not a language tag. Use a code like 'it', 'en' or 'es-MX'.";
}
