using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Agents;

/// <summary>
/// For an agent with an output schema, the persisted output is exactly the JSON value: the model's own sentence before or after
/// it, a code fence around it, is dropped (and kept in the run's diagnostics). Text that is already just the JSON is returned
/// unchanged, byte for byte.
/// </summary>
internal static class StructuredAnswer
{
    /// <summary>
    /// The first JSON block in <paramref name="text"/> that parses whole and is of the <paramref name="expected"/> kind (object or
    /// array; <see cref="JsonValueKind.Undefined"/> accepts either), and the text dropped around it. A block of another kind (a
    /// bracketed "[3]" in a sentence) is skipped. An unclosed bracket of any kind (a truncated answer) ends the search and the text comes back unchanged. A block that does not parse whole (a malformed answer) is never replaced by a
    /// fragment found inside it: the text comes back unchanged.
    /// </summary>
    public static (string Output, string? Dropped) Isolate(string text, JsonValueKind expected = JsonValueKind.Undefined)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (text, null);

        int start = 0;
        while (start < text.Length)
        {
            char open = text[start];
            if (open != '{' && open != '[')
            {
                start++;
                continue;
            }

            bool kindMatches = expected == JsonValueKind.Undefined
                || (open == '{' && expected == JsonValueKind.Object)
                || (open == '[' && expected == JsonValueKind.Array);
            int end = MatchingClose(text, start);
            string? candidate = end < 0 ? null : text.Substring(start, end - start + 1);
            if (candidate is null)
            {
                // Never closed, of any kind: a truncated answer is not replaced by a fragment found inside it.
                return (text, null);
            }

            if (!Parses(candidate))
            {
                // Closed but not JSON ("{with braces}" in a sentence, or a malformed answer): skipped whole, never mined for inner fragments.
                start = end + 1;
                continue;
            }

            if (!kindMatches)
            {
                start = end + 1;
                continue;
            }

            // A later block of the expected kind that never closes is a truncated answer: the complete one before it was prose.
            for (int j = end + 1; j < text.Length; j++)
            {
                if (text[j] != '{' && text[j] != '[')
                    continue;
                int later = MatchingClose(text, j);
                bool laterMatches = expected == JsonValueKind.Undefined
                    || (text[j] == '{' && expected == JsonValueKind.Object)
                    || (text[j] == '[' && expected == JsonValueKind.Array);
                if (later < 0)
                {
                    // The first unclosed opener ends the look-ahead (each scan either jumps past a block or stops: linear).
                    if (laterMatches)
                        return (text, null);
                    break;
                }

                j = later;
            }

            string before = text[..start];
            string after = text[(end + 1)..];
            if (string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after))
                return (text, null);

            string dropped = (before + " … " + after).Trim();
            return (candidate, StripFenceMarks(dropped));
        }

        return (text, null);
    }

    /// <summary>Array for a collection output type, object for any other.</summary>
    public static JsonValueKind ExpectedKind(Type outputType) =>
        outputType != typeof(string) && !IsDictionary(outputType) && (outputType.IsArray || typeof(System.Collections.IEnumerable).IsAssignableFrom(outputType))
            ? JsonValueKind.Array
            : JsonValueKind.Object;

    private static bool IsDictionary(Type t) =>
        typeof(System.Collections.IDictionary).IsAssignableFrom(t)
        || t.GetInterfaces().Append(t).Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

    private static string StripFenceMarks(string dropped) =>
        dropped.Replace("```json", "", StringComparison.OrdinalIgnoreCase).Replace("```", "").Replace("…", "").Trim();

    private static bool Parses(string json)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Index of the bracket that closes the one at <paramref name="start"/>, aware of string literals and escapes; -1 if unbalanced.</summary>
    private static int MatchingClose(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{' || c == '[') depth++;
            else if (c == '}' || c == ']')
            {
                depth--;
                if (depth == 0) return i;
                if (depth < 0) return -1;
            }
        }

        return -1;
    }
}
