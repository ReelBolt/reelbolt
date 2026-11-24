using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The one choke point that meters chat: every model call made through <c>IChatClientFactory</c> (agents,
/// room seats, vision captions, the titler, the assistant) reports its provider-returned usage to
/// <see cref="IUsageRecorder"/> against the ambient <see cref="UsageScope"/>. Wrapped OUTSIDE the factory's
/// client cache because attribution (provider row, organization, billing mode) belongs to the resolved
/// provider, and two rows with identical wiring share a cached client but not an owner.
/// </summary>
public sealed class MeteringChatClient : DelegatingChatClient
{
    private readonly ResolvedInferenceProvider _provider;
    private readonly IUsageRecorder _recorder;
    private readonly ILogger? _logger;

    public MeteringChatClient(IChatClient inner, ResolvedInferenceProvider provider, IUsageRecorder recorder, ILogger? logger = null)
        : base(inner)
    {
        _provider = provider;
        _recorder = recorder;
        _logger = logger;
    }

    /// <summary>Platform when the resolved provider is the platform's (no owning organization), otherwise Byo.</summary>
    public static UsageBillingMode BillingModeOf(Guid? providerOrganizationId) =>
        providerOrganizationId is null ? UsageBillingMode.Platform : UsageBillingMode.Byo;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        await RecordAsync(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        UsageDetails? total = null;
        try
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Usage arrives as UsageContent, normally on the final update; summing is what
                // ChatResponseExtensions.ToChatResponse does, so a provider that splits it is handled the same.
                foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
                {
                    total ??= new UsageDetails();
                    total.Add(usage.Details);
                }

                yield return update;
            }
        }
        finally
        {
            // Also runs when the consumer stops early: tokens were spent either way.
            await RecordAsync(total);
        }
    }

    /// <summary>
    /// Splitting input into uncached and cached keeps each at its own price (cached is a subset of input). Every unit
    /// carries the whole prompt length (<see cref="UsageDetails.InputTokenCount"/>) so a long-context call prices all
    /// three at its <c>gt{N}k</c> tier: the Anthropic SDK's adapter already sums uncached input, cache reads and cache
    /// writes into it, and OpenAI's (and Gemini's OpenAI-compatible) prompt_tokens already include the cached tokens,
    /// which is exactly what each vendor counts toward its long-context threshold.
    /// </summary>
    private async Task RecordAsync(UsageDetails? usage)
    {
        if (usage is null) return;

        try
        {
            long input = usage.InputTokenCount ?? 0;
            long output = usage.OutputTokenCount ?? 0;
            long cached = Math.Min(usage.CachedInputTokenCount ?? 0, input);
            if (input == 0 && output == 0) return;

            UsageScopeState? scope = UsageScope.Current;
            scope?.AddTokens(input, output);

            UsageBillingMode mode = BillingModeOf(_provider.OrganizationId);
            long? seq = scope?.NextCallSeq();

            await RecordKindAsync(UsageKind.LlmInputTokens, input - cached, mode, seq, scope, input);
            await RecordKindAsync(UsageKind.LlmOutputTokens, output, mode, seq, scope, input);
            await RecordKindAsync(UsageKind.LlmCachedInputTokens, cached, mode, seq, scope, input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UnattributedUsageException)
        {
            _logger?.LogError(ex, "Metering a {Kind}/{Model} chat call failed", _provider.Kind, _provider.ModelName);
        }
    }

    private ValueTask<long> RecordKindAsync(
        UsageKind kind, long quantity, UsageBillingMode mode, long? seq, UsageScopeState? scope, long promptTokens) =>
        _recorder.RecordAsync(new UsageRecordRequest(
            kind, quantity, _provider.ProviderId, _provider.Kind, _provider.ModelName, mode, seq, scope,
            Capability: _provider.Capability, AccountId: _provider.AccountId, PromptTokens: promptTokens));

    /// <summary>The cached inner client is shared by every wrapper; disposing one wrapper must not dispose it.</summary>
    protected override void Dispose(bool disposing)
    {
    }
}
