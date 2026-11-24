using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Styles;

// Every enum in this file is APPEND ONLY and persisted BY NAME (each carries its own string
// converter, so it serializes as a name regardless of the host's global JSON options): a style is
// stored as JSON on the execution row (workflow_executions.style_config_json) and on a saved style
// (editing_styles.config_json), and renaming or reordering a member would silently change what an
// old run or saved style means. See docs/editing-styles.md.

/// <summary>How fast the edit moves. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StylePacing>))]
public enum StylePacing
{
    /// <summary>Let shots breathe; long holds, few cuts.</summary>
    Slow,
    /// <summary>A natural, conversational rhythm.</summary>
    Medium,
    /// <summary>Short shots, quick cuts, no dead air.</summary>
    Fast,
    /// <summary>Cuts land on the music's beat.</summary>
    BeatSynced
}

/// <summary>How the story is ordered. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleStorytelling>))]
public enum StyleStorytelling
{
    /// <summary>Events in the order they happened.</summary>
    Chronological,
    /// <summary>Open on the most striking moment, then explain.</summary>
    HookFirst,
    /// <summary>Name a problem, then show the solution.</summary>
    ProblemSolution,
    /// <summary>A collage of moments carried by mood rather than plot.</summary>
    Montage
}

/// <summary>How shots are joined. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleEditing>))]
public enum StyleEditing
{
    /// <summary>Hard cuts, including jump cuts within a take.</summary>
    JumpCuts,
    /// <summary>Gentle joins: dissolves where the footage allows them.</summary>
    SmoothTransitions,
    /// <summary>Hard cuts for energy, softer joins between sections.</summary>
    Mixed
}

/// <summary>The overall tone / energy. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleTone>))]
public enum StyleTone
{
    Calm,
    Upbeat,
    Energetic,
    Serious,
    Playful,
    Inspirational
}

/// <summary>The look of burned captions. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleCaptions>))]
public enum StyleCaptions
{
    /// <summary>Plain subtitles.</summary>
    Clean,
    /// <summary>Words light up as they are spoken.</summary>
    Karaoke,
    /// <summary>One big word at a time — the short-form social look.</summary>
    Punch,
    /// <summary>Big bold words, a few at a time, no highlight — the Reels/TikTok caption look.</summary>
    Bold
}

/// <summary>The colour look. Mirrors the colourist's Look words. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleColor>))]
public enum StyleColor
{
    Natural,
    Warm,
    Cool,
    Filmic,
    Vibrant,
    Muted,
    Mono
}

/// <summary>How energetic the background music should be. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleMusicEnergy>))]
public enum StyleMusicEnergy
{
    Low,
    Medium,
    High
}

/// <summary>How much on-screen graphics the edit carries. <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleGraphicsDensity>))]
public enum StyleGraphicsDensity
{
    None,
    Minimal,
    Moderate,
    Rich
}

/// <summary>How hard the dynamic-edit planner works the cut (pace bound for the closed effect catalogue). <b>APPEND ONLY</b> — persisted by name.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StyleDynamics>))]
public enum StyleDynamics
{
    /// <summary>Few, subtle moments.</summary>
    Calm,
    /// <summary>A moderate number of moments.</summary>
    Medium,
    /// <summary>Many, strong moments including beat alternation.</summary>
    Fast
}

/// <summary>
/// The output shape picked when a workflow is RUN, overriding <c>OutputFormat</c> on every
/// <c>VideoCompile</c> step of that run. <b>APPEND ONLY</b> — persisted by name on
/// <c>workflow_executions.output_format_override</c>. <see cref="Original"/> means "no override":
/// it is normalized to a null column so a run without a format choice stays byte-identical.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunOutputFormat>))]
public enum RunOutputFormat
{
    /// <summary>Whatever each compile step is configured with (no override).</summary>
    Original,
    /// <summary>9:16, 1080x1920 — Reels, TikTok, Shorts.</summary>
    Vertical,
    /// <summary>1:1, 1080x1080 — feed posts.</summary>
    Square,
    /// <summary>16:9, 1920x1080 — YouTube, websites.</summary>
    Landscape,
    /// <summary>4:5, 1080x1350 — portrait feed posts.</summary>
    Portrait
}

/// <summary>
/// An editing style: one optional choice per axis plus bounded free-text notes. Every axis is
/// nullable — null means "no preference", and a style whose every axis is null and whose notes are
/// empty is treated as no style at all (<see cref="IsEmpty"/>). A positional record, so new axes
/// are APPENDED with a null default.
/// </summary>
public sealed record StyleConfig(
    StylePacing? Pacing = null,
    StyleStorytelling? Storytelling = null,
    StyleEditing? Editing = null,
    StyleTone? Tone = null,
    StyleCaptions? Captions = null,
    StyleColor? Color = null,
    StyleMusicEnergy? Music = null,
    StyleGraphicsDensity? Graphics = null,
    string? Notes = null,
    string? Language = null,
    StyleDynamics? Dynamics = null)
{
    /// <summary>True when the style expresses no preference at all.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Pacing is null && Storytelling is null && Editing is null && Tone is null && Captions is null &&
        Color is null && Music is null && Graphics is null && string.IsNullOrWhiteSpace(Notes) &&
        string.IsNullOrWhiteSpace(Language) && Dynamics is null;

    /// <summary>A copy with <see cref="Notes"/> run through <see cref="StyleNotesSanitizer"/>.</summary>
    public StyleConfig Sanitized() => this with { Notes = StyleNotesSanitizer.Sanitize(Notes), Language = StyleLanguageSanitizer.Sanitize(Language) };
}

/// <summary>
/// What a run actually used, snapshotted onto the execution row so later edits to (or deletion of)
/// a saved style never change what a past run means or which cache entries it may reuse.
/// </summary>
/// <param name="Name">Display name ("Cinematic", or the saved style's name). Display only — never reaches a prompt.</param>
/// <param name="PresetKey">The built-in preset this came from, when it did.</param>
/// <param name="StyleId">The saved style this came from, when it did.</param>
/// <param name="Config">The sanitized style itself.</param>
public sealed record RunStyleSnapshot(string? Name, string? PresetKey, Guid? StyleId, StyleConfig Config);

/// <summary>The one JSON shape styles are stored and hashed in.</summary>
public static class StyleJson
{
    /// <summary>camelCase, nulls omitted, enum names — the persisted and hashed form.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(RunStyleSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Options);

    public static string Serialize(StyleConfig config) => JsonSerializer.Serialize(config, Options);

    /// <summary>Parses a stored snapshot; null for null/blank/garbage (a bad row degrades to "no style", never a failed run).</summary>
    public static RunStyleSnapshot? TryParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            RunStyleSnapshot? snapshot = JsonSerializer.Deserialize<RunStyleSnapshot>(json, Options);
            return snapshot?.Config is null ? null : snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a stored style config; null for null/blank/garbage.</summary>
    public static StyleConfig? TryParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StyleConfig>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
