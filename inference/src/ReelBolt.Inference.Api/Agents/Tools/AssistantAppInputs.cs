using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The assistant's side of app inputs (docs/design/app-inputs.md 13.2, 13.3; lane L8): reading an <c>appManifest</c>
/// argument, and the plain
/// sentences an approval card shows for a change to the app page. Every save rule stays the REST one
/// (<see cref="StepConfigSaveValidator"/>, <see cref="WorkflowAppSupport.CheckForSave"/>); this only feeds them.
/// </summary>
internal static class AssistantAppInputs
{
    /// <summary>The most sentences one card lists about the app page.</summary>
    public const int MaxLines = 12;

    /// <summary>
    /// Reads an <c>appManifest</c> argument: null when none was given; an object (or that object escaped into a string)
    /// read with the stored manifest's own options, so an unknown member is refused exactly as REST refuses it.
    /// </summary>
    public static AppManifest? ParseManifest(JsonElement? json, out string? error)
    {
        error = null;
        if (json is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try
        {
            string raw = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "appManifest must be a JSON object (the app page: title, tagline, inputs, requestTemplate).";
                return null;
            }

            AppManifest? manifest = JsonSerializer.Deserialize<AppManifest>(doc.RootElement.GetRawText(), AppManifestJson.Options);
            if (manifest is null)
                error = "appManifest must be a JSON object (the app page: title, tagline, inputs, requestTemplate).";
            return manifest;
        }
        catch (JsonException ex)
        {
            error = $"appManifest is not a valid app page: {ex.Message}";
            return null;
        }
    }

    /// <summary>A digest of a manifest's stored JSON (null and empty read the same), for a confirmation payload.</summary>
    public static string Digest(string? manifestJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson ?? string.Empty)));

    /// <summary>A digest of a manifest as it would be stored.</summary>
    public static string Digest(AppManifest? manifest) => Digest(manifest is null ? null : AppManifestJson.Serialize(manifest));

    /// <summary>
    /// The legacy <c>ApplyWorkflow</c> token extended to cover an <c>appManifest</c> argument: the same token when none was
    /// given (so a workflow change without one is byte-for-byte today's), else a digest of the token, the manifest to
    /// store and the manifest stored now, so a manifest changed between Propose and Apply makes the token stale.
    /// </summary>
    public static string WorkflowToken(string stepsToken, AppManifest? supplied, string? storedManifestJson) =>
        supplied is null
            ? stepsToken
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{stepsToken.Length}:{stepsToken}|{Digest(supplied)}|{Digest(storedManifestJson)}")));

    /// <summary>The capability ids a manifest names (allowed and recommended models), so a card can name them.</summary>
    public static IEnumerable<Guid> ManifestModelIds(params AppManifest?[] manifests) =>
        manifests
            .SelectMany(m => m?.Inputs ?? [])
            .SelectMany(i => (i.Allowed ?? []).Concat(i.Recommended is { } r ? [r] : Array.Empty<Guid>()))
            .Distinct();

    /// <summary>
    /// What a save changes about the app page, in plain sentences (docs/design/app-inputs.md 13.3): fields added, changed
    /// and removed (with their kind, whether they are required and whether they sit under "More options"), where each
    /// field is used (a step's setting by its label, the request sent with each run, a branch, an agent's model), the
    /// request template and the page's title. Labels and step names only, never ids. Empty when nothing changes.
    /// </summary>
    public static List<string> DescribeChanges(
        AppManifest? before, IReadOnlyList<AppBindingStep> beforeSteps,
        AppManifest? after, IReadOnlyList<AppBindingStep> afterSteps,
        IReadOnlyDictionary<Guid, string> modelNames)
    {
        List<string> lines = [];
        Dictionary<string, AppManifestInput> was = InputsById(before);
        Dictionary<string, AppManifestInput> now = InputsById(after);

        foreach ((string id, AppManifestInput input) in now)
        {
            if (!was.TryGetValue(id, out AppManifestInput? old))
                lines.Add($"Adds the field {Quote(LabelOf(input))} ({Describe(input, modelNames)}) to the app page.");
            else if (!SameInput(old, input))
                lines.Add($"Changes the field {Quote(LabelOf(input))} ({Describe(input, modelNames)}).");
        }

        foreach ((string id, AppManifestInput input) in was)
        {
            if (!now.ContainsKey(id))
                lines.Add($"Removes the field {Quote(LabelOf(input))} from the app page.");
        }

        if (!string.Equals(after?.Title, before?.Title, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(after?.Title))
            lines.Add($"Sets the app page title to {Quote(after!.Title!)}.");
        if (!string.Equals(after?.Tagline, before?.Tagline, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(after?.Tagline))
            lines.Add($"Sets the app page tagline to {Quote(after!.Tagline!)}.");
        if (!string.Equals(after?.RequestTemplate, before?.RequestTemplate, StringComparison.Ordinal))
        {
            lines.Add(string.IsNullOrWhiteSpace(after?.RequestTemplate)
                ? "Stops adding the app's request text to each run."
                : $"Adds this to the request of each run, with the fields filled in: {Quote(ShowTemplate(after!.RequestTemplate!, now))}.");
        }

        HashSet<string> oldUses = [.. Uses(before, beforeSteps, was)];
        foreach (string use in Uses(after, afterSteps, now))
        {
            if (!oldUses.Remove(use))
                lines.Add($"Uses {use}.");
        }

        foreach (string gone in oldUses)
            lines.Add($"Stops using {gone}.");

        return lines;
    }

    /// <summary>One plain line per app input a run sets, by label: text quoted and cut, a choice or switch by its words, a model by name.</summary>
    public static IEnumerable<string> RunLines(
        AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values, IReadOnlyDictionary<Guid, string> modelNames)
    {
        if (manifest is null || values is null || values.Count == 0)
            yield break;
        Dictionary<string, AppManifestInput> inputs = InputsById(manifest);
        foreach ((string id, JsonElement value) in values)
        {
            if (!inputs.TryGetValue(id, out AppManifestInput? input))
                continue;
            string shown = AppInputTypes.TryParse(input.Type, out AppInputType type) && type == AppInputType.Model
                ? value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid modelId) && modelNames.TryGetValue(modelId, out string? name)
                    ? name
                    : "a model of this workspace"
                : AppRequestComposer.Display(input, value);
            yield return $"{LabelOf(input)}: {Quote(Short(shown, 200))}";
        }
    }

    /// <summary>The capability ids a run's model inputs chose, so the card can name them.</summary>
    public static IEnumerable<Guid> ChosenModels(AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (manifest is null || values is null)
            yield break;
        foreach (AppManifestInput input in manifest.Inputs ?? [])
        {
            if (input.Kind == AppManifestVocabulary.KindInput && input.Id is { } id
                && AppInputTypes.TryParse(input.Type, out AppInputType type) && type == AppInputType.Model
                && values.TryGetValue(id, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out Guid modelId))
                yield return modelId;
        }
    }

    /// <summary>The names of capability ids (platform rows and the caller's organization's own, through the tenant filter).</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>> ModelNamesAsync(InferenceApiDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        Guid[] wanted = [.. ids.Distinct()];
        if (wanted.Length == 0)
            return new Dictionary<Guid, string>();
        return await db.ModelCapabilities.AsNoTracking()
            .Where(c => wanted.Contains(c.Id))
            .Select(c => new { c.Id, c.Model.DisplayName, c.Model.ModelCode })
            .ToDictionaryAsync(c => c.Id, c => string.IsNullOrWhiteSpace(c.DisplayName) ? c.ModelCode : c.DisplayName, ct);
    }

    /// <summary>A type in everyday words, for the card and the KB.</summary>
    public static string TypeWords(string? wireType) => AppInputTypes.TryParse(wireType, out AppInputType type)
        ? type switch
        {
            AppInputType.ShortText => "short text",
            AppInputType.LongText => "long text",
            AppInputType.Number => "number",
            AppInputType.Duration => "length in seconds",
            AppInputType.Choice => "a choice",
            AppInputType.Toggle => "on/off switch",
            AppInputType.Shape => "shape",
            AppInputType.Model => "model choice",
            _ => "field"
        }
        : "field";

    private static string Describe(AppManifestInput input, IReadOnlyDictionary<Guid, string> modelNames)
    {
        List<string> parts = [TypeWords(input.Type)];
        if (input.Required == true)
            parts.Add("required");
        if (AppInputPlacement.IsUnderMore(input))
            parts.Add("under More options");
        if (input.Allowed is { Count: > 0 } allowed)
            parts.Add($"{allowed.Count} model{(allowed.Count == 1 ? "" : "s")} allowed");
        if (input.Recommended is { } recommended)
            parts.Add(modelNames.TryGetValue(recommended, out string? name) ? $"{Quote(name)} recommended" : "one recommended");
        return string.Join(", ", parts);
    }

    private static IEnumerable<string> Uses(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, AppManifestInput> inputs)
    {
        if (manifest is null || inputs.Count == 0)
            yield break;
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        foreach (AppInputUse use in map.Uses.DistinctBy(u => (u.InputId, u.Kind, u.StepOrder, u.Where)))
        {
            if (!inputs.TryGetValue(use.InputId, out AppManifestInput? input))
                continue;
            string label = Quote(LabelOf(input));
            string step = use.StepOrder is { } order ? StepName(steps, order) : string.Empty;
            yield return use.Kind switch
            {
                AppInputUseKind.Condition => $"{label} to choose the path at {step}",
                AppInputUseKind.RequestTemplate => $"{label} in the request sent with each run",
                AppInputUseKind.AgentModel => $"{label} as the model of {step}",
                _ when use.Field is { IsModel: true } => $"{label} as the {use.Field.Label.ToLowerInvariant()} of {step}",
                _ => $"{label} as the {(use.Field?.Label ?? use.Where).ToLowerInvariant()} of {step}"
            };
        }
    }

    private static string StepName(IReadOnlyList<AppBindingStep> steps, int order)
    {
        AppBindingStep? step = steps.FirstOrDefault(s => s.StepOrder == order);
        return string.IsNullOrWhiteSpace(step?.Name) ? $"step {order}" : $"step {order} {Quote(step.Name!)}";
    }

    private static Dictionary<string, AppManifestInput> InputsById(AppManifest? manifest)
    {
        Dictionary<string, AppManifestInput> map = new(StringComparer.Ordinal);
        foreach (AppManifestInput input in manifest?.Inputs ?? [])
        {
            if (input.Kind == AppManifestVocabulary.KindInput && input.Id is { Length: > 0 } id)
                map.TryAdd(id, input);
        }

        return map;
    }

    private static bool SameInput(AppManifestInput a, AppManifestInput b) =>
        string.Equals(JsonSerializer.Serialize(a, AppManifestJson.Options), JsonSerializer.Serialize(b, AppManifestJson.Options), StringComparison.Ordinal);

    private static string LabelOf(AppManifestInput input) =>
        string.IsNullOrWhiteSpace(input.Label) ? input.Id ?? "a field" : input.Label!;

    /// <summary>The request template with each placeholder shown as its field's label in brackets.</summary>
    private static string ShowTemplate(string template, IReadOnlyDictionary<string, AppManifestInput> inputs) =>
        Short(AppInputPlaceholders.Substitute(template, id => inputs.TryGetValue(id, out AppManifestInput? i) ? $"[{LabelOf(i)}]" : "[a field]"), 200);

    private static string Quote(string value) => "\"" + Short(value, 80) + "\"";

    private static string Short(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "…");
}
