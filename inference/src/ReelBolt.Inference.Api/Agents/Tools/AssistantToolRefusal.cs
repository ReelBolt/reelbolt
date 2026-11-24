using System.Text.Json;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Recognises the assistant tool surfaces' <b>refusal envelope</b>: a tool body that declines to do
/// the work returns a JSON object with a top-level <c>error</c> string, which the model reads as an
/// ordinary result (HTTP 200, <c>isError: false</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A refusal is a successful HTTP call carrying a refusal payload, so anything
/// metering the call has to tell the two apart or every refusal is recorded as a success. The MCP
/// bridge does exactly that: a refused call is metered with <c>succeeded: false</c> and
/// <c>error_code: tool_refused</c>, matching how the bridge's own refusals are recorded.
/// </para>
/// <para>
/// <b>What this is, honestly:</b> a <b>convention</b>, not a type. Every refusal site in
/// <c>Agents/Tools</c> serialises an anonymous object whose member is literally named <c>error</c>
/// (<c>JsonSerializer.Serialize(new { error = ... })</c>, 53 sites at the time of writing), and the
/// model-facing contract of those tools is that envelope. There is no typed marker on the result to
/// key off, because the tools return <c>string</c>/<c>object</c> and
/// <c>AIFunctionFactory</c> serialises the value before the bridge ever sees it.
/// </para>
/// <para>
/// <b>Why it is not fragile prose matching.</b> The check is structural JSON — a top-level property
/// named exactly <c>error</c> whose value is a non-empty string — never a substring of the message
/// text, so reworded refusals keep working and unrelated prose is never matched. The one shape that
/// would collide is a <em>successful</em> payload that carries its own top-level <c>error</c> member.
/// None does today: the DTO results the factory serialises keep their declared property names
/// (PascalCase — <c>TestInferenceProviderResponse.Error</c> on a failed connection test is the closest
/// case, and it serialises as <c>Error</c>, not <c>error</c>). That coupling is pinned by a test
/// (<c>AssistantToolBridgeControllerTests.A_failed_connection_test_result_is_not_mistaken_for_a_refusal</c>),
/// so a future serializer-policy change that made the two collide fails a test rather than silently
/// mislabelling usage.
/// </para>
/// </remarks>
public static class AssistantToolRefusal
{
    /// <summary>
    /// True when <paramref name="resultText"/> is the refusal envelope: a JSON object with a top-level
    /// <c>error</c> property holding a non-empty string. Anything else — a payload, a list, prose (the
    /// file-read tools return raw content), or malformed JSON — is not a refusal.
    /// </summary>
    public static bool IsRefusal(string? resultText)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return false;

        // Cheap pre-filter: every refusal envelope is an object, so large non-JSON results (a read
        // file, a schema dump) are never handed to the parser.
        ReadOnlySpan<char> trimmed = resultText.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(resultText);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(error.GetString());
        }
        catch (JsonException)
        {
            // Not JSON at all: a result, never a refusal.
            return false;
        }
    }

    /// <summary>
    /// Reads a refusal envelope: its plain sentence (<c>error</c>) and the stable <c>code</c> it carries beside it
    /// (e.g. <c>MODEL_UNPRICED</c>; null when it names none). False when <paramref name="resultText"/> is not a refusal.
    /// </summary>
    public static bool TryRead(string? resultText, out string message, out string? code)
    {
        message = string.Empty;
        code = null;
        if (!IsRefusal(resultText))
            return false;

        using JsonDocument document = JsonDocument.Parse(resultText!);
        message = document.RootElement.GetProperty("error").GetString()!;
        code = document.RootElement.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        return true;
    }
}
