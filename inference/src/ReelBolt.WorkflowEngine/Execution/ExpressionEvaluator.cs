using System.Text.Json;
using Microsoft.Extensions.Logging;
using NCalc;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// Evaluates condition expressions against JSON data using NCalc.
/// Supports dot-notation paths like $.score, $.result.items, etc.
///
/// Expression syntax:
/// - Use $.path.to.value to reference JSON fields (e.g. $.score > 8)
/// - Bare names also work when unambiguous (score > 8)
/// - Logical operators: &amp;&amp; / || (or AND / OR)
/// - Comparisons: &gt;, &lt;, &gt;=, &lt;=, ==, !=
///
/// Parallel output support:
/// - When the accumulated output is a parallel step result array
///   [{agentName:"X",output:"...json..."}, ...]
///   you can reference per-agent fields as $.AgentName.field (e.g. $.ReviewAgent.score > 8)
/// </summary>
public class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates a condition expression against JSON output.
    /// Returns true if the expression evaluates to true.
    /// </summary>
    public static bool Evaluate(string expression, string jsonOutput) =>
        Evaluate(expression, jsonOutput, null);

    /// <summary>
    /// Evaluates a condition expression against JSON output, with additional named parameters in
    /// scope alongside the JSON-derived ones.
    ///
    /// <para>
    /// Extra parameters are reachable by their name exactly as written — <c>decision.p_yes</c> and
    /// <c>$.decision.p_yes</c> both resolve — because the two name-rewrite passes below are told
    /// about them, not just the NCalc parameter dictionary. <b>A JSON-derived parameter always
    /// wins a name collision</b>: the extras are injected at the same single
    /// <c>e.Parameters[key] = value</c> site, strictly after the JSON ones, and a colliding extra is
    /// dropped rather than allowed to silently shadow a real JSON field.
    /// </para>
    ///
    /// <para>
    /// Passing null (or an empty dictionary) is byte-identical to
    /// <see cref="Evaluate(string, string)"/>: the extra-name list is empty, so both rewrite passes
    /// iterate exactly the JSON keys in exactly their original order.
    /// </para>
    /// </summary>
    public static bool Evaluate(
        string expression, string jsonOutput,
        IReadOnlyDictionary<string, object?>? extraParameters) =>
        EvaluateCore(expression, jsonOutput, extraParameters, logger: null);

    /// <summary>
    /// The single implementation behind every public overload, plus a logger for the one diagnostic
    /// this class can emit. Kept internal and non-overloaded at call sites that also pass a logger
    /// (<c>ConditionalStepExecutor</c>) because <see cref="ExpressionEvaluator"/> is static and
    /// therefore has no injected <see cref="ILogger"/> of its own; a caller with no logger still
    /// gets the JSON-wins behaviour, just without the collision diagnostic.
    /// </summary>
    /// <param name="logger">
    /// Optional. Receives one warning per extra parameter dropped because a JSON-derived parameter
    /// of the same name already exists.
    /// </param>
    /// <param name="reservedPrefix">
    /// A namespace the extras own outright (app inputs' <c>input.</c>, docs/design/app-inputs.md 9.2): every JSON-derived
    /// parameter whose name starts with it is dropped (logged) before anything else, so a step's output can never set or
    /// shadow it, and the extras of that namespace win. Null (every existing caller) is byte-identical to before.
    /// </param>
    internal static bool EvaluateCore(
        string expression, string jsonOutput,
        IReadOnlyDictionary<string, object?>? extraParameters,
        ILogger? logger,
        string? reservedPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        Dictionary<string, object?> parameters = ExtractParameters(jsonOutput);
        if (!string.IsNullOrEmpty(reservedPrefix))
        {
            foreach (string key in parameters.Keys.Where(k => k.StartsWith(reservedPrefix, StringComparison.Ordinal)).ToList())
            {
                parameters.Remove(key);
                logger?.LogWarning(
                    "ExpressionEvaluator: the step output's '{Key}' is in the reserved '{Prefix}' namespace and is ignored; the app input wins",
                    key, reservedPrefix);
            }
        }

        // Every name the two rewrite passes below must know about. The extra names are appended
        // (never interleaved) and the list is then stably length-ordered, so with no extras this is
        // exactly `parameters.Keys` in its original order and the rewritten NCalc string is
        // byte-identical to what this method produced before the overload existed.
        List<string> parameterNames = parameters.Keys.ToList();
        if (extraParameters is { Count: > 0 })
        {
            foreach (string extraKey in extraParameters.Keys)
            {
                if (!parameters.ContainsKey(extraKey))
                    parameterNames.Add(extraKey);
            }
        }

        // Replace $.xxx references with NCalc parameter syntax.
        // Sort by key length descending so longer paths (e.g. "score.value") are replaced
        // before shorter ones ("score"), preventing partial-replacement bugs.
        string ncalcExpression = expression;
        foreach (string key in parameterNames.OrderByDescending(k => k.Length))
        {
            ncalcExpression = ncalcExpression.Replace($"$.{key}", $"[{key}]");
        }

        // Also replace bare key references (without $. prefix) when they are not already
        // inside brackets so users can write "score > 8" as well as "$.score > 8".
        // IsIdentChar treats '.' as an identifier character, which is exactly what makes this pass
        // work on a dotted extra name like "decision.p_yes" as a whole word.
        foreach (string key in parameterNames.OrderByDescending(k => k.Length))
        {
            // Only replace if the bare name appears without a leading $. or [
            if (!ncalcExpression.Contains($"[{key}]"))
            {
                // Use word-boundary-like replacement: only replace standalone occurrences
                ncalcExpression = ReplaceWholeWord(ncalcExpression, key, $"[{key}]");
            }
        }

        // Normalise AND/OR keywords to NCalc operators
        ncalcExpression = ncalcExpression
            .Replace(" AND ", " && ")
            .Replace(" OR ", " || ")
            .Replace(" and ", " && ")
            .Replace(" or ", " || ");

        // Security: Reject expressions that are too long or deeply nested to prevent StackOverflowException
        if (ncalcExpression.Length > 2000 || ExceedsMaxParenDepth(ncalcExpression, 20))
            return false;

        try
        {
            Expression e = new(ncalcExpression);

            foreach (var (key, value) in parameters)
            {
                e.Parameters[key] = value;
            }

            // THE single injection site for extra parameters: strictly after the JSON-derived ones,
            // and skipping any name the JSON already owns — so a decision parameter can never
            // silently shadow a real JSON key, and the JSON value is what wins.
            if (extraParameters is { Count: > 0 })
            {
                foreach (var (key, value) in extraParameters)
                {
                    if (parameters.ContainsKey(key))
                    {
                        logger?.LogWarning(
                            "ExpressionEvaluator: extra parameter '{Key}' collides with a JSON-derived parameter of the same name; the JSON value wins and the extra is ignored",
                            key);
                        continue;
                    }

                    e.Parameters[key] = value;
                }
            }

            object? result = e.Evaluate();
            return IsTruthy(result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts a value from JSON output using a dot-notation path like "score" or "result.items".
    /// Supports the $. prefix and also bare paths.
    /// Returns null if the path does not exist.
    /// </summary>
    public static string? ExtractJsonValue(string jsonOutput, string path)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput))
            return null;

        // Remove $. prefix if present
        if (path.StartsWith("$."))
            path = path[2..];

        // Empty path = return the whole output
        if (string.IsNullOrWhiteSpace(path))
            return jsonOutput;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonOutput);
            JsonElement current = doc.RootElement;
            string[] segments = path.Split('.');

            // Parallel step output: [{agentName,output}, ...]
            // Support $.AgentName.field by locating the matching array element and navigating into its parsed output.
            if (current.ValueKind == JsonValueKind.Array && segments.Length >= 1)
            {
                string agentNameSegment = segments[0];
                foreach (JsonElement element in current.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("agentName", out JsonElement nameEl)
                        && element.TryGetProperty("output", out JsonElement outputEl)
                        && string.Equals(nameEl.GetString(), agentNameSegment, StringComparison.OrdinalIgnoreCase))
                    {
                        string? outputJson = outputEl.GetString();
                        if (outputJson == null) return null;

                        if (segments.Length == 1)
                            return outputJson;

                        // Recurse into the agent's output JSON with the remaining path
                        return ExtractJsonValue(outputJson, string.Join('.', segments[1..]));
                    }
                }
                // Fall through to regular array index access below
            }

            foreach (string segment in segments)
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, out int arrayIndex)
                    && arrayIndex >= 0 && arrayIndex < current.GetArrayLength())
                {
                    current = current[arrayIndex];
                }
                else
                {
                    return null;
                }
            }

            return current.ValueKind == JsonValueKind.String
                ? current.GetString()
                : current.GetRawText();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Applies an InputMappingJson specification to build a new JSON object from selected fields.
    /// The spec is a JSON object where each value is a JSONPath expression evaluated against
    /// <paramref name="jsonOutput"/>. Supports parallel output navigation ($.AgentName.field).
    /// Returns null if the spec is invalid or produces no fields.
    /// </summary>
    public static string? ApplyInputMapping(string jsonOutput, string inputMappingJson)
    {
        if (string.IsNullOrWhiteSpace(inputMappingJson)) return null;

        try
        {
            using JsonDocument specDoc = JsonDocument.Parse(inputMappingJson);
            if (specDoc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var result = new Dictionary<string, JsonElement>();

            foreach (JsonProperty prop in specDoc.RootElement.EnumerateObject())
            {
                string? expression = prop.Value.GetString();
                if (expression == null) continue;

                string? extracted = ExtractJsonValue(jsonOutput, expression);
                if (extracted == null) continue;

                // Parse the extracted value so it serialises correctly (not double-encoded)
                try
                {
                    using JsonDocument valDoc = JsonDocument.Parse(extracted);
                    result[prop.Name] = valDoc.RootElement.Clone();
                }
                catch
                {
                    // Treat as a raw string
                    result[prop.Name] = JsonDocument.Parse($"\"{JsonEncodedText.Encode(extracted)}\"").RootElement.Clone();
                }
            }

            if (result.Count == 0) return null;

            return JsonSerializer.Serialize(result);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts array elements from JSON output using a dot-notation path.
    /// If path is empty or "$", returns elements of the root array.
    /// If the path points to an array, returns its elements.
    /// </summary>
    public static List<string> ExtractJsonArray(string jsonOutput, string path)
    {
        if (string.IsNullOrWhiteSpace(jsonOutput)) return [];

        // Normalise path
        string normPath = path ?? "";
        if (normPath.StartsWith("$."))
            normPath = normPath[2..];

        // Handle root-array shorthand: empty path, "$" or just "$."
        if (string.IsNullOrWhiteSpace(normPath) || normPath == "$")
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(jsonOutput);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray()
                        .Select(e => e.GetRawText())
                        .ToList();
                }
            }
            catch { }
            return [];
        }

        string? value = ExtractJsonValue(jsonOutput, normPath);
        if (value == null) return [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetRawText())
                    .ToList();
            }
        }
        catch { }

        return [];
    }

    /// <summary>
    /// Checks if the expression's maximum parenthesis nesting depth exceeds <paramref name="maxDepth"/>.
    /// Returns true if exceeded, false otherwise.
    /// </summary>
    private static bool ExceedsMaxParenDepth(string expression, int maxDepth)
    {
        int depth = 0;
        foreach (char c in expression)
        {
            if (c == '(')
            {
                depth++;
                if (depth > maxDepth) return true;
            }
            else if (c == ')')
            {
                depth--;
            }
        }
        return false;
    }

    private static bool IsTruthy(object? result) => result switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0.0,
        decimal m => m != 0m,
        string s => !string.IsNullOrEmpty(s),
        _ => false
    };

    /// <summary>
    /// Replaces all whole-word occurrences of <paramref name="word"/> with
    /// <paramref name="replacement"/> inside <paramref name="source"/>.
    /// "Whole word" means the character before and after the match must not
    /// be a letter, digit, underscore, dot, or bracket — i.e. typical identifier chars.
    /// </summary>
    private static string ReplaceWholeWord(string source, string word, string replacement)
    {
        int start = 0;
        while (true)
        {
            int idx = source.IndexOf(word, start, StringComparison.Ordinal);
            if (idx < 0) break;

            bool prevOk = idx == 0 || !IsIdentChar(source[idx - 1]);
            bool nextOk = idx + word.Length >= source.Length || !IsIdentChar(source[idx + word.Length]);

            if (prevOk && nextOk)
            {
                source = string.Concat(source.AsSpan(0, idx), replacement, source.AsSpan(idx + word.Length));
                start = idx + replacement.Length;
            }
            else
            {
                start = idx + 1;
            }
        }
        return source;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '[' || c == ']';

    private static Dictionary<string, object?> ExtractParameters(string jsonOutput)
    {
        var parameters = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(jsonOutput)) return parameters;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(jsonOutput);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ExtractFromElement(doc.RootElement, "", parameters);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // Parallel step output format: [{agentName:"X", output:"...json..."}, ...]
                // Expose each agent's fields as agentName.field for expression evaluation.
                foreach (JsonElement element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    if (!element.TryGetProperty("agentName", out JsonElement nameEl)) continue;
                    if (!element.TryGetProperty("output", out JsonElement outputEl)) continue;

                    string? agentName = nameEl.GetString();
                    string? outputJson = outputEl.GetString();
                    if (string.IsNullOrWhiteSpace(agentName) || string.IsNullOrWhiteSpace(outputJson)) continue;

                    try
                    {
                        using JsonDocument agentDoc = JsonDocument.Parse(outputJson);
                        if (agentDoc.RootElement.ValueKind == JsonValueKind.Object)
                            ExtractFromElement(agentDoc.RootElement, agentName, parameters);
                        else
                            parameters[agentName] = outputJson;
                    }
                    catch { /* skip malformed agent output */ }
                }
            }
        }
        catch { }

        return parameters;
    }

    private static void ExtractFromElement(JsonElement element, string prefix, Dictionary<string, object?> parameters)
    {
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            string key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    parameters[key] = prop.Value.TryGetInt32(out int intVal) ? intVal : prop.Value.GetDouble();
                    break;
                case JsonValueKind.String:
                    parameters[key] = prop.Value.GetString();
                    break;
                case JsonValueKind.True:
                    parameters[key] = true;
                    break;
                case JsonValueKind.False:
                    parameters[key] = false;
                    break;
                case JsonValueKind.Null:
                    parameters[key] = null;
                    break;
                case JsonValueKind.Object:
                    // Store both the raw value and recurse for nested access
                    parameters[key] = prop.Value.GetRawText();
                    ExtractFromElement(prop.Value, key, parameters);
                    break;
                case JsonValueKind.Array:
                    parameters[key] = prop.Value.GetRawText();
                    break;
            }
        }
    }
}
