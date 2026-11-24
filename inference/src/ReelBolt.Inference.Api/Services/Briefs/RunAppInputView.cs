using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Briefs;

/// <summary>
/// One app field a run was started with, as the run history shows it (docs/design/app-inputs.md 10.4, 26): the label of
/// the run's own page (labels of the time), the value as sent and in words. A model is named, never shown by id.
/// </summary>
/// <param name="Id">The field's id on the run's page.</param>
/// <param name="Label">The field's label on the run's page.</param>
/// <param name="LabelKey">The curated catalogue key of the label, when the recipe has one (the web translates it).</param>
/// <param name="Type">The field's type wire name (<c>shortText</c> ... <c>model</c>).</param>
/// <param name="Value">The value the run used (a model's value is left out: it is an id).</param>
/// <param name="ValueText">The value in English words: text as typed (cut to 200), a choice by its label, a switch by its words, a model by name.</param>
/// <param name="ValueLabelKey">The catalogue key of the chosen option's label, for a choice that has one.</param>
/// <param name="OnLabel">The switch's own "on" words, when the page set them.</param>
/// <param name="OffLabel">The switch's own "off" words, when the page set them.</param>
/// <param name="Unit">The unit of a number, when the page set one.</param>
/// <param name="ModelName">The chosen model's name; null for another type, or a model this workspace cannot see.</param>
public sealed record RunAppInputResponse(
    string Id,
    string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LabelKey,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Value,
    string ValueText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueLabelKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OnLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OffLabel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ModelName = null,
    // Appended: for the permission to send media, who allowed it (app, api_key, mcp, assistant_card).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AllowedBy = null);

/// <summary>Builds <see cref="RunAppInputResponse"/> rows from a run's page and its stored values.</summary>
public static class RunAppInputView
{
    /// <summary>The words for a model this workspace cannot name.</summary>
    public const string UnknownModelWords = "a model of this workspace";

    /// <summary>
    /// The run's non-empty app fields in the page's order, or null when the run carries none (so the response field is
    /// left out and a run without app inputs reads exactly as before).
    /// </summary>
    public static IReadOnlyList<RunAppInputResponse>? Compose(
        AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values, IReadOnlyDictionary<Guid, string> modelNames,
        string? mediaEgressBy = null, string? apiKeyName = null)
    {
        if (values is null || values.Count == 0)
            return null;

        List<RunAppInputResponse> rows = [];
        foreach (AppManifestInput input in manifest?.Inputs ?? [])
        {
            if (input.Kind != AppManifestVocabulary.KindInput || input.Id is not { } id
                || !values.TryGetValue(id, out JsonElement value) || IsEmpty(value))
                continue;

            AppInputTypes.TryParse(input.Type, out AppInputType type);
            string label = AppInputValueChecker.Label(input);
            if (type == AppInputType.Model)
            {
                string? name = value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid modelId)
                    && modelNames.TryGetValue(modelId, out string? found) ? found : null;
                rows.Add(new RunAppInputResponse(id, label, input.LabelKey, input.Type ?? string.Empty, Value: null,
                    ValueText: name ?? UnknownModelWords, ModelName: name));
                continue;
            }

            string words = AppRequestComposer.Display(input, value);
            AppInputOption? option = type == AppInputType.Choice
                ? (input.Options ?? []).FirstOrDefault(o => AppInputValueChecker.SameScalar(o.Value, value))
                : null;
            rows.Add(new RunAppInputResponse(
                id, label, input.LabelKey, input.Type ?? string.Empty, value.Clone(),
                words.Length > 200 ? words[..200] + "…" : words,
                ValueLabelKey: option?.LabelKey,
                OnLabel: type == AppInputType.Toggle ? input.OnLabel : null,
                OffLabel: type == AppInputType.Toggle ? input.OffLabel : null,
                Unit: input.Unit));
        }

        // The permission to send media is the app's own field (never on a stored page): shown last, with who allowed it.
        if (MediaEgressPermission.IsRequested(values) && rows.All(r => r.Id != MediaEgressPermission.InputId))
        {
            rows.Add(new RunAppInputResponse(
                MediaEgressPermission.InputId, MediaEgressPermission.Label, MediaEgressPermission.LabelKey,
                AppInputTypes.WireName(AppInputType.Toggle), JsonSerializer.SerializeToElement(true),
                AllowedByWords(mediaEgressBy, apiKeyName), AllowedBy: MediaEgressPermission.IsGrantor(mediaEgressBy) ? mediaEgressBy : null));
        }

        return rows.Count > 0 ? rows : null;
    }

    /// <summary>Who allowed sending media, in words.</summary>
    public static string AllowedByWords(string? by, string? apiKeyName = null) => by switch
    {
        MediaEgressPermission.ByApp => "Allowed in the app",
        MediaEgressPermission.ByApiKey => string.IsNullOrWhiteSpace(apiKeyName) ? "Allowed by an API key" : $"Allowed by API key {apiKeyName}",
        MediaEgressPermission.ByMcp => "Allowed by a connected tool",
        MediaEgressPermission.ByAssistantCard => "Approved in the assistant",
        _ => "Allowed"
    };

    /// <summary>The capability ids a run's model fields chose, so their names can be read in one query.</summary>
    public static IEnumerable<Guid> ModelIds(AppManifest? manifest, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (manifest?.Inputs is null || values is null)
            yield break;
        foreach (AppManifestInput input in manifest.Inputs)
        {
            if (input.Kind == AppManifestVocabulary.KindInput && input.Id is { } id
                && AppInputTypes.TryParse(input.Type, out AppInputType type) && type == AppInputType.Model
                && values.TryGetValue(id, out JsonElement value) && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out Guid modelId))
                yield return modelId;
        }
    }

    private static bool IsEmpty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
}
