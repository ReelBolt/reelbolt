using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Repairs, or failing that drops, the individual design tokens a model-written document gets wrong, so
/// that one bad value (a gradient set as a colour, a prose answer, <c>clamp()</c> as a size) costs that
/// token and not the whole paid profile build.
///
/// <para>
/// The validator is the single source of truth for what is wrong: this class validates, maps every
/// violation that lives inside a token to that token, first tries <see cref="TokenNormalizer"/> (the one
/// shared normaliser) on a token whose only breach is a non-canonical <c>$value</c> (DS_V09), and drops
/// whatever is still wrong with a WARN <see cref="ImportDiagnostic"/>. Document-level violations (schema,
/// size, token count, unknown top-level keys, forbidden strings DS_V16/DS_V20) are never absorbed: they
/// stay in the document for the caller's own validation to fail on. Valid input is left untouched.
/// </para>
/// </summary>
public static class DesignTokenSanitizer
{
    private const int MaxRounds = 8;
    private const int ClipLength = 60;

    /// <summary>The most diagnostics a compose keeps before a single summary entry.</summary>
    public const int MaxDiagnostics = 200;

    /// <summary>
    /// At most <see cref="MaxDiagnostics"/> diagnostics followed by one summary entry when more were raised,
    /// so a hostile output cannot bloat the artifact.
    /// </summary>
    public static List<ImportDiagnostic> CapDiagnostics(List<ImportDiagnostic> diagnostics)
    {
        if (diagnostics.Count <= MaxDiagnostics)
            return diagnostics;

        List<ImportDiagnostic> capped = diagnostics.GetRange(0, MaxDiagnostics);
        capped.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn,
            "DIAGNOSTICS_TRUNCATED",
            string.Empty,
            $"{diagnostics.Count - MaxDiagnostics} further diagnostics omitted"));
        return capped;
    }

    private static readonly HashSet<string> NeverAbsorbed = new(StringComparer.Ordinal)
    {
        "DS_V00", "DS_V01", "DS_V02", "DS_V03", "DS_V07", "DS_V16", "DS_V20",
    };

    /// <summary>
    /// Sanitizes <paramref name="design"/> in place, appending one WARN diagnostic per dropped token to
    /// <paramref name="diagnostics"/>. Returns how many tokens were dropped. Never throws.
    /// </summary>
    public static int Sanitize(JsonObject design, DesignValidationContext context, List<ImportDiagnostic> diagnostics)
    {
        int dropped = 0;
        var normalizeTried = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                DesignValidationResult result = DesignDocumentValidator.Validate(design, context);

                // token path -> the codes that breached inside it
                var byToken = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var fatalTokens = new HashSet<string>(StringComparer.Ordinal);
                foreach (DesignViolation violation in result.Violations)
                {
                    string? tokenPath = TokenPathOf(design, violation.Path);
                    if (NeverAbsorbed.Contains(violation.Code))
                    {
                        // A token carrying a forbidden string is not quietly repaired: the build fails as before.
                        if (tokenPath is not null)
                            fatalTokens.Add(tokenPath);
                        continue;
                    }

                    if (tokenPath is null)
                        continue;

                    if (!byToken.TryGetValue(tokenPath, out List<string>? codes))
                        byToken[tokenPath] = codes = [];
                    codes.Add(violation.Code);
                }

                if (byToken.Keys.All(fatalTokens.Contains))
                    break;

                foreach ((string path, List<string> codes) in byToken)
                {
                    JsonObject? token = DesignDocument.FindToken(design, path);
                    if (token is null || fatalTokens.Contains(path))
                        continue;

                    string type = token["$type"] is JsonValue t && t.TryGetValue(out string? s) ? s ?? "" : "";

                    if (codes.All(c => c == "DS_V09") && normalizeTried.Add(path))
                    {
                        JsonNode? repaired = TokenNormalizer.Normalize(type, token["$value"]?.DeepClone(), out _);
                        if (repaired is not null)
                        {
                            token["$value"] = repaired;
                            continue;
                        }
                    }

                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        codes.All(c => c == "DS_V09") ? UnparseableCode(type) : "TOKEN_INVALID_DROPPED",
                        ClipPath(path),
                        // The dropped value is never echoed: only its kind, the rules it broke and its size.
                        $"{(type.Length == 0 ? "token" : Clip(type))} token dropped ({string.Join(", ", codes.Distinct())}); value length {token["$value"]?.ToJsonString().Length ?? 0}"));
                    Remove(design, path);
                    dropped++;
                }
            }
        }
        catch (Exception)
        {
            // Never throw: whatever is left is judged by the caller's own validation.
        }

        return dropped;
    }

    /// <summary>The diagnostic code for a value that is not canonical and could not be normalised.</summary>
    public static string UnparseableCode(string type) => type switch
    {
        "color" => "COLOR_UNPARSEABLE",
        "dimension" => "DIMENSION_UNPARSEABLE",
        "duration" => "DURATION_UNPARSEABLE",
        "fontFamily" => "FONT_FAMILY_UNPARSEABLE",
        "fontWeight" => "FONT_WEIGHT_UNPARSEABLE",
        "cubicBezier" => "CUBIC_BEZIER_UNPARSEABLE",
        "number" => "NUMBER_UNPARSEABLE",
        "shadow" => "SHADOW_UNPARSEABLE",
        "typography" => "TYPOGRAPHY_UNPARSEABLE",
        _ => "TOKEN_UNPARSEABLE",
    };

    /// <summary>
    /// A model-supplied value made safe to show: secrets redacted, hidden/control characters and markup
    /// dropped, cut to a short length. Never null.
    /// </summary>
    public static string Clip(string? value) => ClipText(value, ClipLength);

    /// <summary>A token path or any other model-influenced identifier made safe to show (redacted, sanitised, at most 200 characters).</summary>
    public static string ClipPath(string? path) => ClipText(path, 200);

    /// <summary>Redacts secrets first, then sanitises and cuts to <paramref name="maxLength"/>. Never null.</summary>
    public static string ClipText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        string redacted = SecretRedactor.Redact(value).text;
        return DesignText.Sanitize(redacted, maxLength) ?? "";
    }

    private static string? TokenPathOf(JsonObject design, string violationPath)
    {
        if (string.IsNullOrEmpty(violationPath))
            return null;

        string path = violationPath;
        int bracket = path.IndexOf('[');
        if (bracket >= 0)
            path = path[..bracket];
        int dollar = path.IndexOf(".$", StringComparison.Ordinal);
        if (dollar >= 0)
            path = path[..dollar];

        JsonObject? token = DesignDocument.FindToken(design, path);
        return token is not null && token.ContainsKey("$value") ? path : null;
    }

    private static void Remove(JsonObject design, string path)
    {
        string[] segments = path.Split('.');
        var chain = new List<JsonObject> { design };
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (chain[^1][segments[i]] is not JsonObject next)
                return;
            chain.Add(next);
        }

        chain[^1].Remove(segments[^1]);

        // Prune the groups the removal emptied.
        for (int i = segments.Length - 2; i >= 0; i--)
        {
            if (chain[i + 1].Count > 0)
                break;
            chain[i].Remove(segments[i]);
        }
    }
}
