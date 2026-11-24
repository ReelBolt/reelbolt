using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Services.Telemetry;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>A product-event log double that records what it was asked to write, and can be told to fail.</summary>
public sealed class RecordingProductEventLog : IProductEventLog
{
    private readonly HashSet<(Guid, string)> _claimed = [];

    public bool Throw { get; set; }
    public List<ProductEvent> Rows { get; } = [];

    /// <summary>The first touch stored per visitor: first wins, like <c>ON CONFLICT DO NOTHING</c>.</summary>
    public Dictionary<Guid, VisitorAttribution> FirstTouches { get; } = [];

    public Task AppendAsync(ProductEvent entry, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("product_events is unavailable");
        Rows.Add(entry);
        return Task.CompletedTask;
    }

    public Task<bool> ClaimAndAppendAsync(Guid userId, Guid? organizationId, string eventName, string source, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("product_events is unavailable");
        if (!_claimed.Add((userId, eventName))) return Task.FromResult(false);
        Rows.Add(new ProductEvent
        {
            UserId = userId,
            OrganizationId = organizationId,
            Event = eventName,
            Source = source,
            OccurredAt = DateTimeOffset.UtcNow,
        });
        return Task.FromResult(true);
    }

    public Task<bool> ClaimAndAppendWithDetailAsync(Guid userId, Guid? organizationId, string eventName, string claimKey, string? detail, string source, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("product_events is unavailable");
        if (!_claimed.Add((userId, claimKey))) return Task.FromResult(false);
        Rows.Add(new ProductEvent
        {
            UserId = userId,
            OrganizationId = organizationId,
            Event = eventName,
            OccurredAt = DateTimeOffset.UtcNow,
            Source = source,
            Detail = detail,
        });
        return Task.FromResult(true);
    }

    public Task<bool> RecordVisitAsync(VisitorAttribution firstTouch, ProductEvent entry, DateTimeOffset dedupeSince, CancellationToken ct)
    {
        if (Throw) throw new InvalidOperationException("product_events is unavailable");
        FirstTouches.TryAdd(firstTouch.VisitorId, firstTouch);
        if (Rows.Exists(r => r.VisitorId == entry.VisitorId && r.Event == entry.Event && r.Detail == entry.Detail && r.OccurredAt >= dedupeSince))
            return Task.FromResult(false);
        Rows.Add(entry);
        return Task.FromResult(true);
    }

    public Task<bool> ClaimAndAppendFirstPaymentAsync(Guid organizationId, Guid? userId, CancellationToken ct)
        => Task.FromResult(false);
}
