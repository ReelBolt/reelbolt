using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Removes markdown emphasis from copy that is drawn on screen verbatim. Language models write
/// "Build the edit as a *flowchart.*" out of habit, and a Remotion component renders those
/// asterisks literally (customer finding, 2026-10-04). The rules are deliberately narrow, so
/// ordinary copy passes through unchanged:
/// <list type="bullet">
/// <item><c>*x*</c>, <c>**x**</c>, <c>***x***</c>: only when the markers hug a word on both sides and
/// are not glued to a letter or digit outside (so <c>a*b*c</c>, <c>2 * 3</c>, a footnote
/// <c>*Terms apply</c> and a glob such as <c>src/**/*.tsx</c> are untouched).</item>
/// <item><c>_x_</c>, <c>__x__</c>: same rule, so <c>snake_case_name</c> is untouched.</item>
/// <item><c>`x`</c>: the backticks are dropped, the text is kept.</item>
/// <item>A leading <c>#</c>..<c>######</c> heading marker followed by a space (so <c>#1</c> and
/// <c>#ff0000</c> are untouched).</item>
/// </list>
/// The text inside a marker pair is always kept; only the markers go.
/// </summary>
public static class OnScreenCopySanitizer
{
    private static readonly Regex AsteriskEmphasis = new(
        @"(?<![\p{L}\p{N}*\\])(\*{1,3})(?=[^\s*])([^\r\n]+?)(?<=[^\s*])\1(?![\p{L}\p{N}*])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnderscoreEmphasis = new(
        @"(?<![\p{L}\p{N}_\\])(_{1,3})(?=[^\s_])([^\r\n]+?)(?<=[^\s_])\1(?![\p{L}\p{N}_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InlineCode = new(
        @"`{1,3}([^`\r\n]+?)`{1,3}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HeadingMarker = new(
        @"^[ \t]{0,3}#{1,6}[ \t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    // Relaxed escaping so a rewritten output keeps "é" readable in downstream prompts instead of
    // turning it into "\u00E9"; the value is only ever re-parsed as JSON or read by a model.
    private static readonly JsonSerializerOptions RewriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Returns <paramref name="text"/> with markdown emphasis markers removed.</summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        // Cheap exit: nothing a rule could match.
        if (text.IndexOfAny(['*', '_', '`', '#']) < 0)
            return text;

        string result = HeadingMarker.Replace(text, string.Empty);
        result = InlineCode.Replace(result, "$1");
        result = AsteriskEmphasis.Replace(result, "$2");
        result = UnderscoreEmphasis.Replace(result, "$2");
        return result;
    }

    /// <summary>True when <see cref="Strip"/> would change <paramref name="text"/>.</summary>
    public static bool ContainsMarkdown(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(Strip(text), text, StringComparison.Ordinal);

    /// <summary>
    /// Strips markdown from every string value (or string array element) held under a property
    /// whose name is in <paramref name="fieldNames"/> (case-insensitive), anywhere in the JSON
    /// document. Returns <paramref name="json"/> itself — byte-identical — when it is not a JSON
    /// object/array or when nothing changed, so an output without markdown is never reformatted.
    /// </summary>
    public static string StripJsonFields(string json, IReadOnlyCollection<string> fieldNames)
    {
        if (string.IsNullOrWhiteSpace(json) || fieldNames.Count == 0)
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is null)
            return json;

        HashSet<string> names = new(fieldNames, StringComparer.OrdinalIgnoreCase);
        bool changed = Walk(root, names, inTargetField: false);
        return changed ? root.ToJsonString(RewriteOptions) : json;
    }

    private static bool Walk(JsonNode node, HashSet<string> names, bool inTargetField)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    JsonNode? child = obj[key];
                    if (child is null)
                        continue;

                    bool target = names.Contains(key);
                    if (target && TryStripValue(child, out JsonNode? replacement))
                    {
                        obj[key] = replacement;
                        changed = true;
                        continue;
                    }

                    changed |= Walk(child, names, target);
                }
                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? child = array[i];
                    if (child is null)
                        continue;

                    if (inTargetField && TryStripValue(child, out JsonNode? replacement))
                    {
                        array[i] = replacement;
                        changed = true;
                        continue;
                    }

                    changed |= Walk(child, names, inTargetField: false);
                }
                break;
        }

        return changed;
    }

    private static bool TryStripValue(JsonNode node, out JsonNode? replacement)
    {
        replacement = null;
        if (node is not JsonValue value || !value.TryGetValue(out string? text) || text is null)
            return false;

        string stripped = Strip(text);
        if (string.Equals(stripped, text, StringComparison.Ordinal))
            return false;

        replacement = JsonValue.Create(stripped);
        return true;
    }
}
