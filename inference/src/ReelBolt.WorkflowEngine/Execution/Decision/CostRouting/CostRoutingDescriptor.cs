using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// Builds the BOUNDED descriptor that is handed to a Decision provider as the cost-routing question's
/// state.
///
/// <para>
/// <b>This never contains raw prior-step text, and that is the whole point.</b> A cost-routing
/// question is asked before the step runs, so there is no agent output to include — and the one
/// thing available (<c>StepExecutionContext.AccumulatedOutput</c>, plus every entry in
/// <c>StepOutputHistory</c>) is exactly the unbounded upstream blob the prompt-context budget exists
/// to keep out of prompts. What is included instead is a fixed, closed set of SCALARS about the work
/// about to be done: which step, which agent, what it may do (tool count, declared output schema),
/// and how big the resolved input is. The input itself is represented by its LENGTH and a SHA-256
/// prefix, so a decision can still distinguish "a 400-char first step" from "the same step re-run
/// against a 380KB context" without any of that content leaving the process.
/// </para>
///
/// <para>
/// The character cap is enforced twice on purpose: every caller-influenced field is individually
/// bounded (so the JSON stays well-formed under normal use), and the finished string is then hard
/// truncated at <see cref="MaxDescriptorChars"/> regardless (so the cap holds even if a future field
/// is added without its own bound). The caller must not assume the result parses as JSON — the
/// decision client treats the state as opaque text.
/// </para>
/// </summary>
public static class CostRoutingDescriptor
{
    /// <summary>
    /// Hard ceiling on the descriptor's length, asserted by test. Sized to be trivially cheap for a
    /// System One decision model while still carrying every scalar above with room to spare.
    /// </summary>
    public const int MaxDescriptorChars = 2000;

    /// <summary>Per-field cap for the free-text-ish members (agent name, schema name).</summary>
    internal const int MaxNameChars = 64;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the descriptor for one agent step. Deliberately takes scalars rather than a
    /// <c>StepExecutionContext</c> + agent pair: that keeps the "no raw text can leak in" property
    /// inspectable at the call site and lets the cap be tested without constructing a whole context.
    /// </summary>
    /// <param name="resolvedAgentInput">
    /// The exact prompt the agent is about to be given. ONLY its length and hash are used — the
    /// value itself never reaches the descriptor.
    /// </param>
    public static string Build(
        int stepOrder,
        string? agentName,
        AgentType agentType,
        StepType stepType,
        int toolCount,
        string? outputSchemaName,
        int priorStepCount,
        int iterationCount,
        string? resolvedAgentInput)
    {
        Dictionary<string, object?> descriptor = new(StringComparer.Ordinal)
        {
            ["stepOrder"] = stepOrder,
            ["agentName"] = Bound(agentName, MaxNameChars),
            ["agentType"] = agentType.ToString(),
            ["stepType"] = stepType.ToString(),
            ["toolCount"] = toolCount,
            ["outputSchema"] = Bound(outputSchemaName, MaxNameChars),
            ["priorStepCount"] = priorStepCount,
            ["iterationCount"] = iterationCount,
            ["inputChars"] = resolvedAgentInput?.Length ?? 0,
            ["inputSha256"] = HashPrefix(resolvedAgentInput),
        };

        string json = JsonSerializer.Serialize(descriptor, Options);
        return json.Length <= MaxDescriptorChars ? json : json[..MaxDescriptorChars];
    }

    private static string Bound(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxChars ? value : value[..maxChars];
    }

    private static string HashPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16];
    }
}
