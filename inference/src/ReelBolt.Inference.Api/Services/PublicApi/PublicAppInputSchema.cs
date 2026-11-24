using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The JSON Schema (draft 2020-12) of the <c>brief.inputs</c> object a run start accepts, derived from the app manifest's
/// typed inputs (kind "input"). Pure; the limits mirror <see cref="AppInputValueChecker"/>.
/// </summary>
public static class PublicAppInputSchema
{
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>Builds the schema. <paramref name="map"/> (optional) tightens text caps the way the run check does.</summary>
    public static JsonElement Build(AppManifest manifest, AppBindingMap? map = null)
    {
        JsonObject properties = [];
        JsonArray required = [];
        foreach (AppManifestInput input in (manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null))
        {
            // An unknown type is left out (additionalProperties:false then refuses it) rather than described as free text.
            if (!AppInputTypes.TryParse(input.Type, out AppInputType type) || Property(input, type, map) is not { } property)
                continue;
            properties[input.Id!] = property;
            // Mirrors the checker: a permission, a toggle (defaults off), an input with a default and a conditional input are never "missing".
            if (input.Required == true && input.Permission is null && input.ShowWhen is null && type != AppInputType.Toggle
                && input.Default is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
                required.Add(input.Id!);
        }

        JsonObject schema = new()
        {
            ["$schema"] = Dialect,
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0)
            schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject? Property(AppManifestInput input, AppInputType type, AppBindingMap? map)
    {
        JsonObject p = [];
        switch (type)
        {
            case AppInputType.ShortText:
            case AppInputType.LongText:
                p["type"] = "string";
                p["maxLength"] = Math.Max(0, map is null
                    ? Math.Min(AppInputTypes.TextCap(type), input.MaxLength ?? int.MaxValue)
                    : AppInputValueChecker.EffectiveMaxLength(input, map));
                break;
            case AppInputType.Number:
            case AppInputType.Duration:
                bool duration = type == AppInputType.Duration;
                p["type"] = duration ? "integer" : "number";
                if (duration && input.Choices is { Count: > 0 } durations)
                    p["enum"] = Values(durations.Where(c => c.ValueKind == JsonValueKind.Number));
                double? min = duration ? Math.Max(1, input.Min ?? 1) : input.Min;
                if (min is { } lo)
                    p["minimum"] = lo;
                if (input.Max is { } hi)
                    p["maximum"] = hi;
                // The check measures steps from the minimum with a tolerance: multipleOf only for a whole step that divides the minimum.
                if (!duration && input.Min is { } from && input.Step is { } step && step >= 1 && step == Math.Floor(step) && from % step == 0)
                    p["multipleOf"] = step;
                break;
            case AppInputType.Choice:
                p["enum"] = Values((input.Options ?? []).Select(o => o.Value));
                break;
            case AppInputType.Toggle:
                p["type"] = "boolean";
                break;
            case AppInputType.Shape:
                p["type"] = "string";
                p["enum"] = input.Choices is { Count: > 0 } c
                    ? Values(c.Where(x => x.ValueKind == JsonValueKind.String))
                    : Values(AppInputTypes.Shapes.Select(s => JsonSerializer.SerializeToElement(s)));
                break;
            case AppInputType.Model:
                p["type"] = "string";
                p["format"] = "uuid"; // the allowed model list is deliberately not exposed
                break;
            default:
                return null;
        }

        if (!string.IsNullOrWhiteSpace(input.Label))
            p["title"] = input.Label;
        if (!string.IsNullOrWhiteSpace(input.Help))
            p["description"] = input.Help;
        if (input.Permission is null && input.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } def)
            p["default"] = JsonNode.Parse(def.GetRawText());
        return p;
    }

    private static JsonArray Values(IEnumerable<JsonElement> values)
    {
        JsonArray a = [];
        foreach (JsonElement v in values)
            a.Add(JsonNode.Parse(v.GetRawText()));
        return a;
    }
}
