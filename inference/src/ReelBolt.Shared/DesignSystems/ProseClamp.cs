using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The soft caps of the model-written PROSE fields of the product map and the design document, kept in
/// one place so the validators and <see cref="ProseClamp"/> cannot drift. Every other capped field is
/// structural (ids, paths, looked-up names, enums, versions, URLs, patterns) and is never trimmed.
/// </summary>
public static class ProseLimits
{
    public const int Architecture = 600;
    public const int RoutingStrategy = 120;
    public const int DirectoryPurpose = 160;
    public const int EntryPointRole = 60;
    public const int RouteTitle = 80;
    public const int ComponentResponsibility = 160;
    public const int Pattern = 120;
    public const int FrameworkProjectType = 40;
    public const int VoiceSummary = 600;
    public const int VoiceTone = 80;
    public const int VoiceDo = 140;
    public const int VoiceDont = 140;
    public const int VoiceTagline = 120;
    public const int VoiceVocabulary = 40;

    /// <summary>Capped product-map string fields that are prose and get trimmed (path family to cap).</summary>
    public static readonly IReadOnlyDictionary<string, int> ProductMapProse = new Dictionary<string, int>
    {
        ["architecture"] = Architecture,
        ["routingStrategy"] = RoutingStrategy,
        ["directories[].purpose"] = DirectoryPurpose,
        ["entryPoints[].role"] = EntryPointRole,
        ["routes[].title"] = RouteTitle,
        ["components[].responsibility"] = ComponentResponsibility,
        ["patterns[]"] = Pattern,
        ["framework.projectType"] = FrameworkProjectType,
    };

    /// <summary>Capped product-map string fields that are structural and must never be trimmed.</summary>
    public static readonly IReadOnlySet<string> ProductMapStructural = new HashSet<string>
    {
        "name", "framework.version", "framework.styling[]",
        "directories[].path", "entryPoints[].path", "routes[].file", "components[].name",
        "components[].path", "components[].keyProps[]", "dependencies.runtime[].name",
        "dependencies.runtime[].version", "dependencies.dev[].name", "dependencies.dev[].version",
        "dependencies.ui[]", "previewCards[].name", "previewCards[].group", "previewCards[].subtitle",
        "previewCards[].viewport", "previewCards[].path", "previewCards[].text", "previewCards[].assets[]",
    };

    /// <summary>Design-document voice fields that are prose (key to per-entry cap).</summary>
    public static readonly IReadOnlyDictionary<string, int> VoiceProse = new Dictionary<string, int>
    {
        ["summary"] = VoiceSummary,
        ["tone"] = VoiceTone,
        ["do"] = VoiceDo,
        ["dont"] = VoiceDont,
        ["taglines"] = VoiceTagline,
        ["vocabulary"] = VoiceVocabulary,
    };
}

/// <summary>
/// Trims over-length model prose to its cap instead of failing the build. Pure and total. Structural
/// fields and values carrying control or bidirectional characters are left for the validators to refuse.
/// </summary>
public static class ProseClamp
{
    private const char Ellipsis = '…';

    /// <summary>Trims <paramref name="text"/> to at most <paramref name="cap"/> chars, ending in an ellipsis when cut.</summary>
    public static string Trim(string text, int cap)
    {
        if (text is null || cap <= 0 || text.Length <= cap || ProductMapValidator.HasControlOrBidi(text))
            return text ?? "";

        if (cap == 1)
            return Ellipsis.ToString();

        int limit = cap - 1; // room for the ellipsis

        // Never split a surrogate pair: if the cut falls between the halves, step back.
        if (char.IsHighSurrogate(text[limit - 1]) && char.IsLowSurrogate(text[limit]))
            limit--;

        int window = (int)Math.Floor(cap * 0.4);
        int floor = Math.Max(1, limit - window);
        int cut = -1;
        for (int i = limit - 1; i >= floor; i--)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                cut = i + 1;
                break;
            }
        }

        if (cut > 0)
            return text[..cut].TrimEnd() + Ellipsis;

        for (int i = limit; i > 0 && i >= limit - window; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                string head = text[..i].TrimEnd();
                if (head.Length > 0)
                    return head + Ellipsis;
            }
        }

        return text[..limit] + Ellipsis;
    }

    private static string Apply(string? value, int cap, string path, List<ImportDiagnostic> diagnostics)
    {
        if (value is null || value.Length <= cap)
            return value ?? "";
        string trimmed = Trim(value, cap);
        if (trimmed != value)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                "PROSE_TRIMMED",
                path,
                $"{path} was {value.Length} characters; trimmed to fit the limit of {cap}."));
        }

        return trimmed;
    }

    /// <summary>Returns a copy of the map with every over-cap prose field trimmed; one warning per field.</summary>
    public static ProductMapDocument Normalize(ProductMapDocument map, List<ImportDiagnostic> diagnostics)
    {
        IReadOnlyDictionary<string, int> caps = ProseLimits.ProductMapProse;
        string? Opt(string? v, string family, string path) => v is null ? null : Apply(v, caps[family], path, diagnostics);
        string Req(string? v, string family, string path) => Apply(v, caps[family], path, diagnostics);

        ProductMapFramework? fw = map.Framework;
        return map with
        {
            Framework = fw is null ? fw! : fw with
            {
                ProjectType = Req(fw.ProjectType, "framework.projectType", "framework.projectType"),
            },
            Architecture = Opt(map.Architecture, "architecture", "architecture"),
            RoutingStrategy = Opt(map.RoutingStrategy, "routingStrategy", "routingStrategy"),
            Directories = (map.Directories ?? []).Select((d, i) => d is null ? d! : d with
            {
                Purpose = Req(d.Purpose, "directories[].purpose", $"directories[{i}].purpose"),
            }).ToList(),
            EntryPoints = (map.EntryPoints ?? []).Select((e, i) => e is null ? e! : e with
            {
                Role = Req(e.Role, "entryPoints[].role", $"entryPoints[{i}].role"),
            }).ToList(),
            Routes = (map.Routes ?? []).Select((r, i) => r is null ? r! : r with
            {
                Title = Opt(r.Title, "routes[].title", $"routes[{i}].title"),
            }).ToList(),
            Components = (map.Components ?? []).Select((c, i) => c is null ? c! : c with
            {
                Responsibility = Req(c.Responsibility, "components[].responsibility", $"components[{i}].responsibility"),
            }).ToList(),
            Patterns = (map.Patterns ?? []).Select((p, i) => Req(p, "patterns[]", $"patterns[{i}]")).ToList(),
        };
    }

    /// <summary>Trims the over-cap voice strings of a design document in place; one warning per field.</summary>
    public static void NormalizeVoice(JsonObject design, List<ImportDiagnostic> diagnostics)
    {
        if (design["voice"] is not JsonObject voice)
            return;

        foreach (KeyValuePair<string, int> family in ProseLimits.VoiceProse)
        {
            if (!voice.TryGetPropertyValue(family.Key, out JsonNode? node) || node is null)
                continue;

            if (node is JsonArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue v && v.TryGetValue(out string? s) && s is not null && s.Length > family.Value)
                        array[i] = Apply(s, family.Value, $"voice.{family.Key}[{i}]", diagnostics);
                }
            }
            else if (node is JsonValue sv && sv.TryGetValue(out string? text) && text is not null && text.Length > family.Value)
            {
                voice[family.Key] = Apply(text, family.Value, $"voice.{family.Key}", diagnostics);
            }
        }
    }
}
