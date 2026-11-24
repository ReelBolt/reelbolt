#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A price service that prices every LLM token unit of any capability alike: input 2 millicredits per token, output 8,
/// cached 1 (cost = credits / 1000), Platform ×1 and Byo/Runner ×0, and fails closed for any other Platform unit. It
/// keeps every request so a test can assert what a writer asked to price (capability, variant, account).
/// </summary>
internal sealed class StaticPriceService : IModelPriceService
{
    private static readonly IReadOnlyDictionary<UsageKind, (decimal Millis, Guid PriceId)> Rates =
        new Dictionary<UsageKind, (decimal, Guid)>
        {
            [UsageKind.LlmInputTokens] = (2m, Guid.Parse("00000000-0000-0000-0000-0000000000a1")),
            [UsageKind.LlmOutputTokens] = (8m, Guid.Parse("00000000-0000-0000-0000-0000000000a2")),
            [UsageKind.LlmCachedInputTokens] = (1m, Guid.Parse("00000000-0000-0000-0000-0000000000a3"))
        };

    private readonly object _gate = new();
    private readonly List<PriceRequest> _requests = [];

    public IReadOnlyList<PriceRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    public static Guid PriceIdFor(UsageKind unit) => Rates[unit].PriceId;

    public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
    {
        lock (_gate) _requests.Add(request);

        if (!Rates.TryGetValue(request.Unit, out (decimal Millis, Guid PriceId) rate))
        {
            if (request.BillingMode == UsageBillingMode.Platform)
                throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);
            return ValueTask.FromResult(new PriceQuote(0, 0m, null));
        }

        bool platform = request.BillingMode == UsageBillingMode.Platform;
        long millis = platform ? (long)Math.Round(request.Quantity * rate.Millis, MidpointRounding.AwayFromZero) : 0;
        return ValueTask.FromResult(new PriceQuote(millis, platform ? request.Quantity * rate.Millis / 1000m : 0m, rate.PriceId));
    }

    public ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default)
    {
        bool platform = billingMode == UsageBillingMode.Platform;
        decimal millis = costUsd * PricingDefaults.DefaultMarkup * PricingDefaults.CreditsMillisPerUsd;
        return ValueTask.FromResult(new PriceQuote(
            platform ? (long)Math.Round(millis, MidpointRounding.AwayFromZero) : 0, platform ? costUsd : 0m, null));
    }

    public void Invalidate()
    {
    }
}

/// <summary>A price service with nothing priced: every Platform unit fails closed, Byo and Runner are free.</summary>
internal sealed class EmptyPriceService : IModelPriceService
{
    private readonly ModelPriceService _inner = new(new EmptyPriceSource());

    public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) => _inner.PriceAsync(request, ct);

    public ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
        _inner.PriceQuotedUsdAsync(costUsd, unit, billingMode, at, ct);

    public void Invalidate() => _inner.Invalidate();

    private sealed class EmptyPriceSource : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(new ModelPriceSnapshot([], [], []));
    }
}

internal sealed class DiscardSink : IUsageEventSink
{
    public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class CapturingSink : IUsageEventSink
{
    private readonly object _gate = new();
    private readonly List<UsageEvent> _events = [];
    public List<int> BatchSizes { get; } = [];
    public int FailuresRemaining;

    public IReadOnlyList<UsageEvent> Events
    {
        get
        {
            lock (_gate) return _events.ToList();
        }
    }

    public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct)
    {
        lock (_gate)
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("sink down");
            }

            BatchSizes.Add(events.Count);
            _events.AddRange(events);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A chat client that answers "ok" and reports fixed usage on both the buffered and the streaming path.</summary>
internal sealed class UsageReportingChatClient(long inputTokens, long outputTokens, long? cachedTokens = null, string answer = "ok") : IChatClient
{
    public int Calls;

    private UsageDetails Usage() => new()
    {
        InputTokenCount = inputTokens,
        OutputTokenCount = outputTokens,
        CachedInputTokenCount = cachedTokens
    };

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)) { Usage = Usage() });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        await Task.Yield();
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");

        // Usage rides on a final update of its own, as OpenAI-compatible streams report it.
        ChatResponseUpdate last = new(ChatRole.Assistant, []);
        last.Contents.Add(new UsageContent(Usage()));
        yield return last;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
