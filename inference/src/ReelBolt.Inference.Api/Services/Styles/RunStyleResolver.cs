using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Services.Styles;

/// <summary>
/// What a run was started with beyond its brief: an editing style snapshot, an output-format
/// override and an output resolution (short side in pixels, see <see cref="ReelBolt.Shared.Styles.OutputResolution"/>).
/// <see cref="Brief"/> is the part of the typed run brief that has no column of its own (stored on
/// <c>run_brief_json</c>). All null means a run exactly like one started before styles existed.
/// </summary>
public sealed record RunChoices(
    RunStyleSnapshot? Style, RunOutputFormat? OutputFormat, int? OutputResolution = null, StoredRunBrief? Brief = null)
{
    /// <summary>True when the run chose nothing.</summary>
    public bool IsEmpty => Style is null && OutputFormat is null && OutputResolution is null && Brief is null;
}

/// <summary>A resolved <see cref="RunChoices"/>, or the plain-language reason it could not be resolved.</summary>
public sealed record RunChoicesResolution(RunChoices? Choices, string? Error)
{
    public static RunChoicesResolution Fail(string error) => new(null, error);
}

/// <summary>
/// Turns what the caller asked for at run time — a saved style id, a built-in preset key, or a
/// one-off style, plus an output format and an output resolution — into the snapshot stored on the
/// execution row. The resolution is only parsed here; whether the plan allows it is checked when the
/// run is queued (<c>WorkflowExecutionService</c>), so every execute path gets the same 402.
/// </summary>
public interface IRunStyleResolver
{
    Task<RunChoicesResolution> ResolveAsync(
        Guid userId,
        Guid projectId,
        Guid? styleId,
        string? presetKey,
        StyleConfig? inlineStyle,
        string? outputFormat,
        string? outputResolution,
        CancellationToken ct);
}

/// <inheritdoc cref="IRunStyleResolver"/>
/// <remarks>
/// A saved style resolves only for its owner, and only when it is global or pinned to THIS project;
/// anything else is "not found", never "forbidden", so a guessed id is not confirmed to a stranger.
/// The snapshot always carries sanitized notes, whichever way the style arrived.
/// </remarks>
public sealed class RunStyleResolver : IRunStyleResolver
{
    private readonly InferenceApiDbContext _db;

    public RunStyleResolver(InferenceApiDbContext db) => _db = db;

    public async Task<RunChoicesResolution> ResolveAsync(
        Guid userId,
        Guid projectId,
        Guid? styleId,
        string? presetKey,
        StyleConfig? inlineStyle,
        string? outputFormat,
        string? outputResolution,
        CancellationToken ct)
    {
        if (!TryParseOutputFormat(outputFormat, out RunOutputFormat? format))
        {
            return RunChoicesResolution.Fail(
                $"Unknown output format '{outputFormat}'. Use Original, Vertical, Square, Landscape or Portrait.");
        }

        if (!OutputResolution.TryParse(outputResolution, out int? resolution))
            return RunChoicesResolution.Fail(OutputResolution.UnknownMessage(outputResolution));

        int chosen = (styleId.HasValue ? 1 : 0) + (string.IsNullOrWhiteSpace(presetKey) ? 0 : 1) + (inlineStyle is null ? 0 : 1);
        if (chosen > 1)
            return RunChoicesResolution.Fail("Choose one style: a saved style, a preset, or a one-off style — not several.");

        RunStyleSnapshot? snapshot = null;

        if (styleId is { } id)
        {
            EditingStyle? style = await _db.EditingStyles.AsNoTracking().FirstOrDefaultAsync(
                s => s.Id == id && s.OwnerId == userId && (s.ProjectId == null || s.ProjectId == projectId), ct);
            if (style is null)
                return RunChoicesResolution.Fail("Style not found.");

            StyleConfig config = (StyleJson.TryParseConfig(style.ConfigJson) ?? new StyleConfig()).Sanitized();
            snapshot = new RunStyleSnapshot(style.Name, null, style.Id, config);
        }
        else if (!string.IsNullOrWhiteSpace(presetKey))
        {
            StylePreset? preset = StylePresetCatalog.Find(presetKey);
            if (preset is null)
                return RunChoicesResolution.Fail($"Unknown style preset '{presetKey}'.");

            snapshot = new RunStyleSnapshot(preset.Name, preset.Key, null, preset.Config.Sanitized());
        }
        else if (inlineStyle is not null)
        {
            snapshot = new RunStyleSnapshot("Custom (this run only)", null, null, inlineStyle.Sanitized());
        }

        if (snapshot is not null && snapshot.Config.IsEmpty)
            snapshot = null;

        return new RunChoicesResolution(new RunChoices(snapshot, format, resolution), null);
    }

    /// <summary>
    /// Parses an output format by enum name (case-insensitive) or by its ratio ("9:16", "1:1",
    /// "16:9", "4:5"). Null/blank and "Original" parse to null (no override).
    /// </summary>
    public static bool TryParseOutputFormat(string? value, out RunOutputFormat? format)
    {
        format = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string v = value.Trim();
        RunOutputFormat? parsed = v switch
        {
            "9:16" => RunOutputFormat.Vertical,
            "1:1" => RunOutputFormat.Square,
            "16:9" => RunOutputFormat.Landscape,
            "4:5" => RunOutputFormat.Portrait,
            _ => Enum.TryParse(v, ignoreCase: true, out RunOutputFormat named) && Enum.IsDefined(named) && !int.TryParse(v, out _)
                ? named
                : null
        };

        if (parsed is null)
            return false;

        format = parsed == RunOutputFormat.Original ? null : parsed;
        return true;
    }
}
