using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>One audited public API request (the route is the endpoint's TEMPLATE, never the raw path).</summary>
public sealed record ApiKeyAuditEntry(
    Guid ApiKeyId,
    Guid OrganizationId,
    DateTime OccurredAt,
    string Method,
    string Route,
    int StatusCode,
    long DurationMs,
    string RequestId);

/// <summary>Hands an audit row to the background writer. Never blocks: a full channel sheds the row.</summary>
public interface IApiKeyAuditWriter
{
    void TryWrite(ApiKeyAuditEntry entry);
}

/// <summary>
/// The audit trail's writer: a bounded <see cref="Channel{T}"/> (10,000) drained by a
/// <see cref="BackgroundService"/> every two seconds or 500 rows, whichever comes first. A full channel
/// drops the row and increments <see cref="PublicApiDiagnostics.AuditDropped"/> - an audit row is never
/// worth delaying a request. The same batch updates <c>api_keys.last_used_at</c> at most once a minute per
/// key (an in-memory throttle), so a busy key does not write that row on every call.
/// </summary>
public sealed class ApiKeyAuditWriter : BackgroundService, IApiKeyAuditWriter
{
    public const int DefaultCapacity = 10_000;
    public const int MaxBatch = 500;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LastUsedInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<ApiKeyAuditEntry> _channel;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ApiKeyAuditWriter> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _lastUsed = new();
    private long _dropped;

    public ApiKeyAuditWriter(IServiceScopeFactory scopes, ILogger<ApiKeyAuditWriter> logger, int capacity = DefaultCapacity)
    {
        _scopes = scopes;
        _logger = logger;
        // FullMode.Wait + TryWrite tells the caller the channel is full (DropWrite would report success and
        // hide the shed row), so the drop can be counted. Nothing ever waits on this channel from a request.
        _channel = Channel.CreateBounded<ApiKeyAuditEntry>(
            new BoundedChannelOptions(Math.Max(1, capacity)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    }

    /// <summary>Rows shed because the bounded channel was full (exposed for the drop-count test).</summary>
    internal long DroppedCount => Interlocked.Read(ref _dropped);

    public void TryWrite(ApiKeyAuditEntry entry)
    {
        if (_channel.Writer.TryWrite(entry)) return;
        Interlocked.Increment(ref _dropped);
        PublicApiDiagnostics.AuditDropped.Add(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Task<bool> waiting = _channel.Reader.WaitToReadAsync(stoppingToken).AsTask();
                Task delay = Task.Delay(FlushInterval, stoppingToken);
                await Task.WhenAny(waiting, delay);
                await DrainAndWriteAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Drains up to <see cref="MaxBatch"/> rows and writes them (also the test seam).</summary>
    internal async Task<int> DrainAndWriteAsync(CancellationToken ct)
    {
        List<ApiKeyAuditEntry> batch = new(MaxBatch);
        while (batch.Count < MaxBatch && _channel.Reader.TryRead(out ApiKeyAuditEntry? entry))
            batch.Add(entry);
        if (batch.Count == 0) return 0;

        try
        {
            DateTime now = batch[^1].OccurredAt;
            HashSet<Guid> touch = [];
            foreach (ApiKeyAuditEntry entry in batch)
            {
                if (_lastUsed.AddOrUpdate(
                        entry.ApiKeyId,
                        now,
                        (_, previous) => now - previous >= LastUsedInterval ? now : previous) == now)
                {
                    touch.Add(entry.ApiKeyId);
                }
            }

            using IServiceScope scope = _scopes.CreateScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.ApiKeyAuditEvents.AddRange(batch.Select(e => new ApiKeyAuditEvent
            {
                ApiKeyId = e.ApiKeyId,
                OrganizationId = e.OrganizationId,
                OccurredAt = e.OccurredAt,
                Method = e.Method,
                Route = e.Route,
                StatusCode = e.StatusCode,
                DurationMs = e.DurationMs,
                RequestId = e.RequestId
            }));
            await db.SaveChangesAsync(ct);

            if (touch.Count > 0)
            {
                await db.ApiKeys.IgnoreQueryFilters()
                    .Where(k => touch.Contains(k.Id))
                    .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, now), ct);
            }

            return batch.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Writing {Count} API key audit events failed: {Error}", batch.Count, ex.GetType().Name);
            return 0;
        }
    }
}
