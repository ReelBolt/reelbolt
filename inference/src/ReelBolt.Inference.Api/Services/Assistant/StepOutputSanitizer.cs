using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Inference.Api.Agents.Tools;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// What a tenant may read of a step's stored output and error text (T-024: QA-P0-8, S-13, S-14). The raw text
/// stays in the database and the logs; every read model that echoes it (<c>GetWorkflowExecution</c>,
/// <c>GetExecutionStatus</c>) passes it through here first, and cleans BEFORE it cuts so a key straddling the
/// cut can never survive half-visible.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Storage keys: a JSON property whose name ends with <c>Key</c> and whose value is a string starting
/// <c>projects/</c> is dropped; any other string value starting <c>projects/</c> becomes <see cref="StoredFile"/>,
/// and a key embedded in a sentence is replaced in place.</item>
/// <item>Errors: the failure code plus a cleaned sentence — no URLs, internal host:port or IP addresses,
/// server file paths, stack frames or exception type names.</item>
/// <item>Budgets: a legacy <c>BUDGET_EXCEEDED</c> text that names the platform-wide (global) cap or spend,
/// written before the tenant-safe sentence existed, reads as <see cref="LegacyBudgetSentence"/>.</item>
/// </list>
/// </remarks>
public static partial class StepOutputSanitizer
{
    /// <summary>What a storage key reads as.</summary>
    public const string StoredFile = "[stored file]";

    /// <summary>What a legacy budget refusal carrying platform-wide figures reads as.</summary>
    public const string LegacyBudgetSentence = "This step hit a daily video budget.";

    private const string BudgetCode = "BUDGET_EXCEEDED";
    private const string SafePlatformSentencePrefix = "Video generation has reached the platform's daily limit";
    private const string Address = "[internal address]";
    private const string ServerPath = "[path]";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A step's output (JSON or plain text) with every storage key removed, then cut to <paramref name="max"/> chars.</summary>
    public static string? Preview(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        string cleaned = TryCleanJson(text) ?? ScrubKeys(text);
        return AssistantStepShapes.Truncate(cleaned, max);
    }

    /// <summary>
    /// An error for a tenant: <paramref name="failureCode"/> (when known) plus the cleaned sentence, cut to
    /// <paramref name="max"/> chars. <c>null</c> in, <c>null</c> out.
    /// </summary>
    public static string? Error(string? failureCode, string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        string text = raw;
        // Rows written before the source limits became plain words carry a byte count and a setting name
        // ("Source video is N bytes, exceeding MaxInputBytes=..."): the assistant gets a sentence instead.
        if (OldSourceLimit().IsMatch(text))
            text = "A source video is larger or longer than we can edit. Trim it or export a smaller copy, then run again.";
        bool budget = string.Equals(failureCode, BudgetCode, StringComparison.Ordinal)
            || text.Contains(BudgetCode, StringComparison.Ordinal);

        text = StackFrameLine().Replace(text, " ");
        text = InlineStackFrame().Replace(text, " ");
        text = StackTraceMarker().Replace(text, " ");
        text = text.Replace("--->", " ", StringComparison.Ordinal);
        text = ExceptionTypeName().Replace(text, " ");
        text = SchemeUrl().Replace(text, Address);
        text = ScrubKeys(text);
        text = HostAndPort().Replace(text, Address);
        text = IpAddress().Replace(text, Address);
        text = UnixPath().Replace(text, ServerPath);
        text = WindowsPath().Replace(text, ServerPath);
        text = Whitespace().Replace(text, " ").Trim();

        if (!string.IsNullOrEmpty(failureCode) && text.StartsWith(failureCode, StringComparison.Ordinal))
            text = text[failureCode.Length..].TrimStart(':', ' ');
        if (budget)
            text = CleanBudget(text.StartsWith(BudgetCode, StringComparison.Ordinal) ? text[BudgetCode.Length..].TrimStart(':', ' ') : text);

        if (!string.IsNullOrEmpty(failureCode))
            text = text.Length == 0 ? failureCode : $"{failureCode}: {text}";

        return AssistantStepShapes.Truncate(text, max);
    }

    private static string? TryCleanJson(string text)
    {
        ReadOnlySpan<char> head = text.AsSpan().TrimStart();
        if (head.IsEmpty || (head[0] != '{' && head[0] != '['))
            return null;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is null)
            return null;

        node = Clean(node);
        return node?.ToJsonString(Compact) ?? "null";
    }

    private static JsonNode? Clean(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string name in obj.Select(p => p.Key).ToList())
                {
                    JsonNode? child = obj[name];
                    if (child is null)
                        continue;
                    if (name.EndsWith("Key", StringComparison.Ordinal) && IsKeyString(child))
                    {
                        obj.Remove(name);
                        continue;
                    }

                    // S-13: error text stored inside an output (VideoGenerate meta.error, a clip's detail) gets the
                    // same cleaning as errorMessage/errorDetails; legacy rows still carry raw exception text there.
                    JsonNode? cleaned = IsErrorProperty(name) && child is JsonValue text && text.TryGetValue(out string? raw)
                        ? JsonValue.Create(Error(null, raw, ErrorTextMax))
                        : Clean(child);
                    if (!ReferenceEquals(cleaned, child))
                        obj[name] = cleaned;
                }

                if (obj["failed"] is JsonValue failed && failed.TryGetValue(out string? code)
                    && string.Equals(code, BudgetCode, StringComparison.Ordinal)
                    && obj["error"] is JsonValue error && error.TryGetValue(out string? message))
                {
                    obj["error"] = CleanBudget(message);
                }

                return obj;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    JsonNode? child = array[i];
                    if (child is null)
                        continue;
                    JsonNode? cleaned = Clean(child);
                    if (!ReferenceEquals(cleaned, child))
                        array[i] = cleaned;
                }

                return array;

            case JsonValue value when value.TryGetValue(out string? s):
                string scrubbed = s.StartsWith("projects/", StringComparison.Ordinal) ? StoredFile : ScrubKeys(s);
                return string.Equals(scrubbed, s, StringComparison.Ordinal) ? value : JsonValue.Create(scrubbed);

            default:
                return node;
        }
    }

    private const int ErrorTextMax = 8000;

    private static readonly string[] ErrorPropertySuffixes = ["error", "detail", "details", "message"];

    /// <summary><c>error</c>, <c>detail</c>, <c>details</c>, <c>message</c>, or a name ending in one (<c>lastError</c>).</summary>
    private static bool IsErrorProperty(string name) =>
        ErrorPropertySuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static bool IsKeyString(JsonNode node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && s.StartsWith("projects/", StringComparison.Ordinal);

    private static string ScrubKeys(string text) =>
        text.Contains("projects/", StringComparison.Ordinal) ? StorageKey().Replace(text, StoredFile) : text;

    /// <summary>A budget sentence naming the platform-wide (global) cap is legacy and other tenants' business.</summary>
    private static string CleanBudget(string text)
    {
        if (text.StartsWith(SafePlatformSentencePrefix, StringComparison.Ordinal))
            return text;
        return text.Contains("platform", StringComparison.OrdinalIgnoreCase) || text.Contains("global", StringComparison.OrdinalIgnoreCase)
            ? LegacyBudgetSentence
            : text;
    }

    [GeneratedRegex(@"projects/[0-9a-fA-F-]{36}(?:/[^\s""'<>(),;]*)?")]
    private static partial Regex StorageKey();

    [GeneratedRegex(@"^[ \t]+at [^\r\n]*$", RegexOptions.Multiline)]
    private static partial Regex StackFrameLine();

    [GeneratedRegex(@"\s+at [\w.`<>\[\],]+\([^)]*\)(?: in \S+:line \d+)?")]
    private static partial Regex InlineStackFrame();

    [GeneratedRegex(@"-{3}\s*End of [^\r\n-]*-{3}")]
    private static partial Regex StackTraceMarker();

    [GeneratedRegex(@"\b(?:[A-Za-z_]\w*\.)*\w*Exception\b:?")]
    private static partial Regex ExceptionTypeName();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.-]*://\S+")]
    private static partial Regex SchemeUrl();

    [GeneratedRegex(@"\b(?:\d{1,3}(?:\.\d{1,3}){3}|[A-Za-z][A-Za-z0-9-]*(?:\.[A-Za-z0-9-]+)*):\d{2,5}\b")]
    private static partial Regex HostAndPort();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b")]
    private static partial Regex IpAddress();

    [GeneratedRegex(@"(?<![\w/:.\]])/(?:[\w.@-]+/)+[\w.@-]*(?::line \d+)?")]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\b[A-Za-z]:\\\S+")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\b(?:MaxInputBytes|MaxDurationSeconds)\b")]
    private static partial Regex OldSourceLimit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
