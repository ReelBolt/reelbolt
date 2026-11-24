using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// How an <see cref="AssistantToolProvider"/> reports tool failures. <paramref name="Transport"/> is <c>chat</c> or <c>mcp</c>;
/// <paramref name="AnswerExceptions"/> makes a throwing tool answer the model with a usable error instead of rethrowing
/// (chat: the framework would otherwise reduce it to "Error: Function failed."; MCP: the bridge owns that answer).
/// </summary>
public sealed record AssistantToolFailureReporting(ILogger Logger, string Transport, bool AnswerExceptions)
{
    /// <summary>
    /// How many throws one reporting scope (one chat turn) answers as results. Past it a throw is rethrown again, so the
    /// framework's own consecutive-error stop still ends a turn whose tools keep failing instead of looping to the cap.
    /// </summary>
    public const int MaxAnsweredExceptions = 5;

    private int _answered;

    /// <summary>Takes one answer from the scope's budget; false once it is spent.</summary>
    internal bool TryTakeAnswer() => Interlocked.Increment(ref _answered) <= MaxAnsweredExceptions;
}

/// <summary>
/// Makes every assistant tool failure visible: a thrown exception and an <c>{ "error": ... }</c> result are each logged at
/// Warning with the tool name, thread, transport, code and the (redacted, capped, single-line) error text; never the arguments.
/// In chat a throw is answered with a usable <c>{ error, code }</c> result; MCP rethrows so the bridge's own fixed answer stands.
/// </summary>
/// <remarks>
/// Sits inside the approval interceptor's caller chain but OUTSIDE the interceptor: the gate still sees the throw (card
/// Failed, turn observed abandoned), and the turn guard's wrapper is outside this one, so its stop exception is never caught.
/// S-75: the model is never given an exception's message or type; the Warning line carries the same short reference the answer quotes.
/// </remarks>
internal sealed class AssistantToolFailureReporter : DelegatingAIFunction
{
    private const int MaxErrorChars = 500;

    private readonly AssistantToolFailureReporting _options;
    private readonly Guid? _threadId;

    public AssistantToolFailureReporter(AIFunction inner, AssistantToolFailureReporting options, Guid? threadId)
        : base(inner)
    {
        _options = options;
        _threadId = threadId;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        object? result;
        try
        {
            result = await base.InvokeCoreAsync(arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string reference = Guid.NewGuid().ToString("N")[..8];
            // The exception object is not handed to the logger: its message would reach the log unredacted. The type and
            // the throwing member locate it; the message is the redacted, capped one.
            _options.Logger.LogWarning(
                "Assistant tool {ToolName} threw {ExceptionType} at {ThrownAt} (transport {Transport}, thread {ThreadId}, ref {Reference}): {Error}",
                Name, ex.GetType().Name, ThrownAt(ex), _options.Transport, _threadId, reference, Clean(ex.Message));

            if (!_options.AnswerExceptions || !_options.TryTakeAnswer())
                throw;

            return Answer(ex, arguments, reference);
        }

        if (TryReadError(result, out string error, out string? code))
        {
            _options.Logger.LogWarning(
                "Assistant tool {ToolName} returned an error (transport {Transport}, thread {ThreadId}, code {Code}): {Error}",
                Name, _options.Transport, _threadId, code ?? "-", Clean(error));
        }

        return result;
    }

    /// <summary>One line, secrets redacted BEFORE the cap (a cut never splits a key the redactor would have caught), capped.</summary>
    internal static string Clean(string? text)
    {
        string oneLine = SecretRedactor.Redact(Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim()).text;
        return oneLine.Length > MaxErrorChars ? oneLine[..MaxErrorChars] + "..." : oneLine;
    }

    /// <summary>The member that threw (type and method name only), for locating the failure without the message.</summary>
    private static string ThrownAt(Exception ex) =>
        ex.TargetSite is { } site ? $"{site.DeclaringType?.Name}.{site.Name}" : "-";

    /// <summary>Reads a top-level string <c>error</c> (and string <c>code</c>) from a tool result of any shape.</summary>
    private static bool TryReadError(object? result, out string error, out string? code)
    {
        error = string.Empty;
        code = null;
        try
        {
            JsonElement element;
            switch (result)
            {
                case null:
                    return false;
                case JsonElement e:
                    element = e;
                    break;
                case string s:
                    if (!s.TrimStart().StartsWith('{'))
                        return false;
                    using (JsonDocument doc = JsonDocument.Parse(s))
                        element = doc.RootElement.Clone();
                    break;
                case JsonNode node:
                    element = JsonSerializer.SerializeToElement(node);
                    break;
                case ValueType or Array:
                    return false;
                default:
                    element = JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions);
                    break;
            }

            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } str && str.TrimStart().StartsWith('{'))
            {
                using JsonDocument inner = JsonDocument.Parse(str);
                element = inner.RootElement.Clone();
            }

            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("error", out JsonElement err)
                || err.ValueKind != JsonValueKind.String)
                return false;

            error = err.GetString() ?? string.Empty;
            if (element.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                code = c.GetString();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private JsonElement Answer(Exception ex, AIFunctionArguments arguments, string reference)
    {
        if (ex is ArgumentException or JsonException && DescribeArgumentProblem(arguments) is { } problem)
            return Result(problem, "TOOL_ARGUMENTS_INVALID");

        return Result(
            $"The server failed while running {Name}; the arguments were not the problem. Do not repeat the same call more than once: "
            + $"tell the user it failed and quote this reference: {reference}.",
            "TOOL_FAILED");
    }

    private static JsonElement Result(string error, string code) =>
        JsonSerializer.SerializeToElement(new { error, code });

    /// <summary>
    /// After a binding failure: names the parameter the call got wrong. Probes only AFTER the throw, with the binder's own
    /// serializer options, so nothing the binder accepts is ever refused. Null when no argument explains the failure.
    /// </summary>
    private string? DescribeArgumentProblem(AIFunctionArguments arguments)
    {
        MethodInfo? method = UnderlyingMethod;
        if (method is null)
            return null;

        foreach (ParameterInfo p in method.GetParameters())
        {
            if (p.Name is null || p.ParameterType == typeof(CancellationToken) || p.HasDefaultValue || p.IsOptional)
                continue;
            if (!arguments.ContainsKey(p.Name))
                return $"The required argument '{p.Name}' ({FriendlyType(p.ParameterType)}) was not supplied. Call {Name} again with it set." + Hint(p.Name);
        }

        foreach (ParameterInfo p in method.GetParameters())
        {
            if (p.Name is null || !arguments.TryGetValue(p.Name, out object? value) || value is not JsonElement json)
                continue;
            try
            {
                JsonSerializer.Deserialize(json, p.ParameterType, AIJsonUtilities.DefaultOptions);
            }
            catch (Exception probe) when (probe is JsonException or NotSupportedException or InvalidOperationException)
            {
                return $"The argument '{p.Name}' could not be read as {FriendlyType(p.ParameterType)}. Call {Name} again with a value of that type." + Hint(p.Name);
            }
        }

        return null;
    }

    private static string Hint(string parameter) => parameter == "workflowId"
        ? " To create a new workflow omit workflowId (or pass null); to edit an existing one pass its id exactly as ListWorkflows returned it."
        : string.Empty;

    private static string FriendlyType(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        string name = t == typeof(Guid) ? "a GUID"
            : t == typeof(string) ? "a string"
            : t == typeof(bool) ? "a boolean"
            : t == typeof(int) || t == typeof(long) ? "an integer"
            : t == typeof(double) || t == typeof(decimal) || t == typeof(float) ? "a number"
            : t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) ? "a JSON array"
            : "a JSON " + (t.IsEnum ? "string (an enum value)" : "value of type " + t.Name);
        return Nullable.GetUnderlyingType(type) is not null ? name + " or null" : name;
    }
}
