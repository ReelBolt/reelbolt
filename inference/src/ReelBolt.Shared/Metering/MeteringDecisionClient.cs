using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Meters decision-model calls. They never pass through <c>IChatClient</c> (TypeSafe/Open-Jev use a raw
/// <c>HttpClient</c>, the logprob client the OpenAI SDK directly) so <see cref="MeteringChatClient"/> cannot see
/// them; every decision site obtains its client from <c>IDecisionClientFactory</c>, which makes the factory the
/// choke point. Records the token counts the decision result already carries; both units carry the input count as the
/// prompt length for the <c>gt{N}k</c> tier lookup (a logprob client that asks one completion per question sums them,
/// so this can only overstate a single prompt, never understate it).
/// </summary>
public sealed class MeteringDecisionClient : IDecisionClient
{
    private readonly IDecisionClient _inner;
    private readonly ResolvedDecisionProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringDecisionClient(IDecisionClient inner, ResolvedDecisionProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<DecisionResult> DecideAsync(
        DecisionState state, IReadOnlyList<DecisionQuestion> questions, CancellationToken ct)
    {
        DecisionResult result = await _inner.DecideAsync(state, questions, ct);

        try
        {
            if (result.InputTokens > 0 || result.OutputTokens > 0)
            {
                UsageScopeState? scope = UsageScope.Current;
                scope?.AddTokens(result.InputTokens, result.OutputTokens);

                UsageBillingMode mode = MeteringChatClient.BillingModeOf(_provider.OrganizationId);
                long? seq = scope?.NextCallSeq();
                await _recorder.RecordAsync(new UsageRecordRequest(
                    UsageKind.LlmInputTokens, result.InputTokens, _provider.ProviderId, _provider.Kind, _provider.ModelName, mode, seq, scope,
                    Capability: InferenceProviderCapability.Decision, AccountId: _provider.AccountId,
                    PromptTokens: result.InputTokens), ct);
                await _recorder.RecordAsync(new UsageRecordRequest(
                    UsageKind.LlmOutputTokens, result.OutputTokens, _provider.ProviderId, _provider.Kind, _provider.ModelName, mode, seq, scope,
                    Capability: InferenceProviderCapability.Decision, AccountId: _provider.AccountId,
                    PromptTokens: result.InputTokens), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} decision call failed", _provider.Kind, _provider.ModelName);
        }

        return result;
    }
}
