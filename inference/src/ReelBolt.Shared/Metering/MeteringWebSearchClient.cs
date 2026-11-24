using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Meters a successful web search: <see cref="UsageKind.WebSearches"/> x the billable search units under the
/// WebSearch capability, plus (OpenAI/Gemini) the call's input/output tokens as the ordinary token kinds under the
/// Chat capability and the same model, so they price from the chat rows of the catalog. An unpriced token row
/// is informational (<see cref="UsageRecordRequest.InformationalWhenUnpriced"/>): it never makes the step settle at its hold. A failure to record never fails the search.
/// </summary>
public sealed class MeteringWebSearchClient : IWebSearchClient
{
    private readonly IWebSearchClient _inner;
    private readonly ResolvedWebSearchProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringWebSearchClient(
        IWebSearchClient inner, ResolvedWebSearchProvider provider, IUsageRecorder recorder, ILogger? logger = null)
    {
        _inner = inner;
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    public async Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct)
    {
        WebSearchResponse response = await _inner.SearchAsync(request, ct);

        try
        {
            UsageBillingMode mode = MeteringChatClient.BillingModeOf(_provider.OrganizationId);
            UsageScopeState? scope = UsageScope.Current;

            if (response.SearchUnits > 0)
            {
                await _recorder.RecordAsync(new UsageRecordRequest(
                    UsageKind.WebSearches, response.SearchUnits, _provider.ProviderId, _provider.Kind, _provider.ModelName,
                    mode, scope?.NextCallSeq(), scope,
                    Capability: InferenceProviderCapability.WebSearch, AccountId: _provider.AccountId), ct);
            }

            if (response.InputTokens > 0 || response.OutputTokens > 0)
            {
                scope?.AddTokens(response.InputTokens, response.OutputTokens);
                long? seq = scope?.NextCallSeq();
                await RecordTokensAsync(UsageKind.LlmInputTokens, response.InputTokens, mode, seq, scope, response.InputTokens, ct);
                await RecordTokensAsync(UsageKind.LlmOutputTokens, response.OutputTokens, mode, seq, scope, response.InputTokens, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} web search failed", _provider.Kind, _provider.ModelName);
        }

        return response;
    }

    private ValueTask<long> RecordTokensAsync(
        UsageKind kind, long quantity, UsageBillingMode mode, long? seq, UsageScopeState? scope, long prompt, CancellationToken ct) =>
        quantity <= 0
            ? ValueTask.FromResult(0L)
            : _recorder.RecordAsync(new UsageRecordRequest(
                kind, quantity, _provider.ProviderId, _provider.Kind, _provider.ModelName, mode, seq, scope,
                Capability: InferenceProviderCapability.Chat, AccountId: _provider.AccountId, PromptTokens: prompt,
                InformationalWhenUnpriced: true), ct);
}
