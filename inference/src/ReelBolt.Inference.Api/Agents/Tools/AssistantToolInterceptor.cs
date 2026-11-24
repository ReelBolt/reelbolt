using System.Text.Json;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.History;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Wraps one assistant tool so the chat transport can (a) ask the user before a tool that writes runs, and
/// (b) observe every result. The name, description and schema the model sees are the inner function's, byte for
/// byte; only what happens around the call changes.
/// </summary>
/// <remarks>
/// <para>
/// A tool is gated by <see cref="AssistantApprovalCatalog.IsGated"/> — its NAME — so a tool added tomorrow is
/// covered the day it is registered. <c>Propose*</c> tools are never gated (they write nothing); their results
/// are handed to the gate so the matching <c>Apply*</c> can describe the real change on its card.
/// </para>
/// <para>
/// Only the in-app chat transport installs a gate. The MCP bridge builds the same tools with no gate: the
/// external client asks its own user, and there is no chat stream to show a card on.
/// </para>
/// </remarks>
internal sealed class AssistantToolInterceptor : DelegatingAIFunction
{
    private readonly IAssistantApprovalGate? _gate;
    private readonly Action<string, object?>? _onResult;

    public AssistantToolInterceptor(AIFunction inner, IAssistantApprovalGate? gate, Action<string, object?>? onResult)
        : base(inner)
    {
        _gate = gate;
        _onResult = onResult;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        object? result;
        // A proposal's result is what the model reads (a copied recipe's text fenced); the gate keeps the plain copy the
        // tool offers for the customer's card, so the card reads exactly what it read before the fence.
        AssistantCardCopy.Slot? cardCopy = _gate is not null && AssistantApprovalCatalog.IsProposal(Name) ? AssistantCardCopy.Begin() : null;
        if (_gate is not null && AssistantApprovalCatalog.IsGated(Name))
        {
            // The card and the tool must read the SAME arguments. Hand both one canonical copy holding only
            // the function's own parameter names, matched exactly: a stray or differently-cased key means
            // nothing to either of them, so it can never make the card describe one action and the tool
            // perform another.
            AIFunctionArguments canonical = Canonicalise(arguments);
            result = await _gate.RunAsync(
                Name,
                canonical,
                async () =>
                {
                    // Contract section 9: after approval, before the write. Best effort and bounded.
                    await ProjectHistoryAutoCapture.TryCaptureForToolAsync(Name, canonical, cancellationToken);
                    return await base.InvokeCoreAsync(canonical, cancellationToken);
                },
                cancellationToken);
        }
        else
        {
            await ProjectHistoryAutoCapture.TryCaptureForToolAsync(Name, arguments, cancellationToken);
            result = await base.InvokeCoreAsync(arguments, cancellationToken);
        }

        if (_gate is not null && AssistantApprovalCatalog.IsProposal(Name))
            _gate.NoteProposal(Name, cardCopy?.Json ?? result);

        try
        {
            _onResult?.Invoke(Name, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Observing a result is never allowed to fail the tool that produced it.
        }

        return result;
    }

    /// <summary>A copy of <paramref name="arguments"/> that keeps only keys equal (ordinally) to a parameter name of this function.</summary>
    private AIFunctionArguments Canonicalise(AIFunctionArguments arguments)
    {
        HashSet<string> parameters = ParameterNames();
        var canonical = new AIFunctionArguments { Context = arguments.Context, Services = arguments.Services };
        foreach (KeyValuePair<string, object?> pair in arguments)
        {
            if (parameters.Contains(pair.Key))
                canonical[pair.Key] = pair.Value;
        }

        return canonical;
    }

    private HashSet<string> ParameterNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (JsonSchema.ValueKind == JsonValueKind.Object
            && JsonSchema.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in properties.EnumerateObject())
                names.Add(property.Name);
        }

        return names;
    }
}

/// <summary>
/// The plain (unfenced) copy of a proposal result, for the customer's approval card only (MKT-01). The interceptor opens
/// a slot around a proposal call; a tool that fences a copied recipe's text for the model offers the plain JSON it
/// built before fencing. With no slot open (no gate: MCP, REST twins) the offer is dropped and only the fenced result exists.
/// </summary>
internal static class AssistantCardCopy
{
    private static readonly AsyncLocal<Slot?> Current = new();

    internal sealed class Slot
    {
        public string? Json { get; set; }
    }

    internal static Slot Begin()
    {
        Slot slot = new();
        Current.Value = slot;
        return slot;
    }

    internal static void Offer(string json)
    {
        if (Current.Value is { } slot)
            slot.Json = json;
    }
}
