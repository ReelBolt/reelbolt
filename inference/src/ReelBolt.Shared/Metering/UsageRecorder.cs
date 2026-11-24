using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Metering;

/// <summary>Batching knobs; the defaults are the B3b contract (about 2 seconds or 100 rows).</summary>
public sealed record UsageRecorderOptions
{
    public int MaxBatchSize { get; init; } = 100;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int WriteAttempts { get; init; } = 4;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Prices usage and batch-inserts it. Callers get the price immediately (so the scope's running credits are
/// current for the mid-step ceiling) while the insert is buffered: a worker drains the channel every
/// <see cref="UsageRecorderOptions.FlushInterval"/> or <see cref="UsageRecorderOptions.MaxBatchSize"/> rows,
/// and <see cref="StopAsync"/> completes the channel and writes whatever is left.
/// </summary>
public sealed class UsageRecorder : IUsageRecorder, IHostedService
{
    private readonly IModelPriceService _prices;
    private readonly IUsageEventSink _sink;
    private readonly ILogger<UsageRecorder> _logger;
    private readonly UsageRecorderOptions _options;
    private readonly bool _failOnUnattributed;
    private readonly Channel<UsageEvent> _channel = Channel.CreateUnbounded<UsageEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private Task? _worker;

    public UsageRecorder(
        IModelPriceService prices,
        IUsageEventSink sink,
        ILogger<UsageRecorder> logger,
        UsageRecorderOptions? options = null,
        IOptions<MeteringOptions>? metering = null)
    {
        _prices = prices;
        _sink = sink;
        _logger = logger;
        _options = options ?? new UsageRecorderOptions();
        _failOnUnattributed = metering?.Value.FailOnUnattributed ?? false;
    }

    public async ValueTask<long> RecordAsync(UsageRecordRequest request, CancellationToken ct = default)
    {
        if (request.Quantity <= 0m) return 0;

        UsageScopeState? scope = request.Scope ?? UsageScope.Current;
        if (scope is null && _failOnUnattributed)
        {
            throw new UnattributedUsageException(
                $"Metered {request.Kind} call for {request.ProviderKind}/{request.ModelName} ran outside any usage scope " +
                $"(Metering:{nameof(MeteringOptions.FailOnUnattributed)} is on). Open a UsageScope around the call.");
        }

        UsageEvent row = BuildRow(request, scope);

        try
        {
            PriceQuote quote = request.QuotedCostUsd is { } quotedUsd
                ? await _prices.PriceQuotedUsdAsync(quotedUsd, request.Kind, request.BillingMode, row.OccurredAt, ct)
                : await _prices.PriceAsync(new PriceRequest(
                    request.ProviderId,
                    request.AccountId,
                    request.ProviderKind,
                    request.Capability ?? ProviderCapabilityUsage.DefaultCapabilityFor(request.Kind),
                    request.ModelName,
                    request.Kind,
                    row.Variant,
                    request.BillingMode,
                    request.Quantity,
                    row.OccurredAt,
                    request.PromptTokens), ct);
            row.CreditsMillis = quote.CreditsMillis;
            row.CostUsd = quote.CostUsd;
            row.PriceId = quote.PriceId;

            // A prompt-length tier the lookup chose is recorded as the event's variant, so the row says what it billed at.
            if (row.Variant is null && ModelPriceService.TryParsePromptTier(quote.Variant, out _))
                row.Variant = quote.Variant;
        }
        catch (UnpricedUsageException ex)
        {
            // Fail closed means "never silently free": the row is still written (credits 0, no price) so the
            // gap is visible in the ledger, and the error is loud enough to page on.
            _logger.LogError(ex, "Usage {Kind} for {ProviderKind}/{Model} has no price; recorded unpriced",
                request.Kind, request.ProviderKind, request.ModelName);
            MarkUnpriced(row, request, scope, noPriceFound: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Pricing {Kind} for {ProviderKind}/{Model} failed; recorded unpriced",
                request.Kind, request.ProviderKind, request.ModelName);
            MarkUnpriced(row, request, scope, noPriceFound: false);
        }

        scope?.AddCredits(row.CreditsMillis);

        if (!_channel.Writer.TryWrite(row))
        {
            _logger.LogError("Usage recorder is shut down; dropped {Kind} event {Key} ({Credits} millicredits)",
                row.Kind, row.IdempotencyKey, row.CreditsMillis);
        }

        return row.CreditsMillis;
    }

    /// <summary>
    /// a PLATFORM unit whose price lookup failed keeps its row (quantity, model) at 0 credits, flagged
    /// <see cref="UsageEvent.Unpriced"/>, counted on <see cref="UnpricedUsageDiagnostics.UnpricedUsage"/> and remembered on
    /// its scope (<see cref="UsageScopeState.SawUnpricedPlatformUsage"/>, read in-process at settle time because
    /// <c>usage_events</c> is flushed asynchronously). BYO and Runner units are never charged, so they stay unflagged.
    /// </summary>
    private static void MarkUnpriced(UsageEvent row, UsageRecordRequest request, UsageScopeState? scope, bool noPriceFound)
    {
        if (request.BillingMode != UsageBillingMode.Platform) return;

        // The tokens of a web-search call are informational when the model has no token price: the call's real
        // charge is its WebSearches unit, so a missing token price must not make the step settle at its full hold.
        // Only when the lookup succeeded and found no price: a failed lookup keeps the fail-expensive behaviour.
        if (request.InformationalWhenUnpriced && noPriceFound) return;

        row.Unpriced = true;
        UnpricedUsageDiagnostics.UnpricedUsage.Add(1,
            new KeyValuePair<string, object?>("source", row.Source.ToString()),
            new KeyValuePair<string, object?>("capability",
                (request.Capability ?? ProviderCapabilityUsage.DefaultCapabilityFor(request.Kind)).ToString()));
        scope?.MarkUnpriced();
    }

    private UsageEvent BuildRow(UsageRecordRequest request, UsageScopeState? scope)
    {
        UsageEvent row = new()
        {
            Id = Guid.NewGuid(),
            Kind = request.Kind,
            Quantity = request.Quantity,
            ProviderId = request.ProviderId,
            ProviderKind = request.ProviderKind,
            ModelName = request.ModelName,
            Variant = string.IsNullOrWhiteSpace(request.Variant) ? null : request.Variant.Trim(),
            BillingMode = request.BillingMode,
            OccurredAt = DateTime.UtcNow
        };

        if (scope is null)
        {
            // Never lost, never silent: it lands on the platform org and is flagged for a human to chase.
            _logger.LogWarning(
                "Metered {Kind} call for {ProviderKind}/{Model} ran outside any usage scope; attributing to the platform org as Unattributed",
                request.Kind, request.ProviderKind, request.ModelName);
            row.OrganizationId = WellKnownOrganizations.PlatformId;
            row.Source = UsageSource.Unattributed;
            row.IdempotencyKey = request.IdempotencyKey ?? $"unattributed:{Guid.NewGuid():N}:{KindCode(request.Kind)}";
            return row;
        }

        // An explicit key replaces the per-scope call number entirely, so it must not consume one either.
        long seq = request.IdempotencyKey is null ? request.CallSeq ?? scope.NextCallSeq() : 0;
        row.OrganizationId = scope.OrganizationId ?? WellKnownOrganizations.PlatformId;
        row.ProjectId = scope.ProjectId;
        row.ExecutionId = scope.ExecutionId;
        row.StepResultId = scope.StepResultId;
        row.UserId = scope.UserId;
        row.Source = scope.Source;
        row.IdempotencyKey = request.IdempotencyKey ?? $"{(scope.StepResultId ?? scope.ScopeId):N}:{seq}:{KindCode(request.Kind)}";
        return row;
    }

    private static string KindCode(UsageKind kind) => kind switch
    {
        UsageKind.LlmInputTokens => "in",
        UsageKind.LlmOutputTokens => "out",
        UsageKind.LlmCachedInputTokens => "cin",
        _ => kind.ToString()
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker ??= Task.Run(DrainAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_worker is null) return;

        // The worker drains to completion regardless of the host's token: losing a ledger row to a hurried
        // shutdown is worse than a slower one. The token only bounds how long we are willing to wait.
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        ChannelReader<UsageEvent> reader = _channel.Reader;
        List<UsageEvent> batch = new(_options.MaxBatchSize);

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            using CancellationTokenSource window = new(_options.FlushInterval);

            while (batch.Count < _options.MaxBatchSize)
            {
                if (reader.TryRead(out UsageEvent? next))
                {
                    batch.Add(next);
                    continue;
                }

                try
                {
                    if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await WriteWithRetryAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task WriteWithRetryAsync(List<UsageEvent> batch)
    {
        if (batch.Count == 0) return;

        for (int attempt = 1; attempt <= _options.WriteAttempts; attempt++)
        {
            try
            {
                await _sink.WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Writing {Count} usage events failed (attempt {Attempt}/{Max})",
                    batch.Count, attempt, _options.WriteAttempts);
                if (attempt < _options.WriteAttempts)
                    await Task.Delay(_options.RetryDelay).ConfigureAwait(false);
            }
        }

        // Last resort: the idempotency keys and amounts are in the log so the rows can be replayed by hand.
        foreach (UsageEvent row in batch)
        {
            _logger.LogCritical(
                "LOST usage event {Key}: org {Org} {Kind} qty {Quantity} {Provider}/{Model} {Mode} credits {Credits} cost {Cost}",
                row.IdempotencyKey, row.OrganizationId, row.Kind, row.Quantity, row.ProviderKind, row.ModelName,
                row.BillingMode, row.CreditsMillis, row.CostUsd);
        }
    }
}

/// <summary>Metrics of the metering path.</summary>
public static class UnpricedUsageDiagnostics
{
    /// <summary>
    /// The billing meter (the Inference API exports it as <c>ReelBolt.Inference.Billing</c>, beside the spend report's
    /// alert counter).
    /// </summary>
    public const string MeterName = "ReelBolt.Inference.Billing";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// One per PLATFORM usage unit recorded without a price (no effective price row, or a pricing failure), tagged with
    /// <c>source</c> and <c>capability</c>. Zero is what a healthy day looks like; anything else is a price to add.
    /// </summary>
    public static readonly Counter<long> UnpricedUsage = Meter.CreateCounter<long>(
        "reelbolt.billing.unpriced_usage", "units",
        "Platform usage recorded without a price (charged at least the operation's hold), tagged with source and capability");
}
