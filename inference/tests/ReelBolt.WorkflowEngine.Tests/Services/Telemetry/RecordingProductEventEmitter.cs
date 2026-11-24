using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Services.Telemetry;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>
/// A product-event emitter double that records every CALL, without the once-per-user claim the real one
/// applies. A test that must show a path does not emit twice (a replayed upload completion) needs the
/// calls, not the rows.
/// </summary>
public sealed class RecordingProductEventEmitter : IProductEventEmitter
{
    /// <summary>Every <see cref="FirstAsync"/> call, in order.</summary>
    public List<(Guid UserId, Guid? OrganizationId, string Event)> Firsts { get; } = [];

    /// <summary>Every <see cref="AppendAsync"/> call, in order.</summary>
    public List<(Guid UserId, Guid? OrganizationId, string Event, string? Detail)> Appends { get; } = [];

    public Task FirstAsync(Guid userId, Guid? organizationId, string eventName, CancellationToken ct)
    {
        Firsts.Add((userId, organizationId, eventName));
        return Task.CompletedTask;
    }

    public Task AppendAsync(Guid userId, Guid? organizationId, string eventName, string? detail, CancellationToken ct)
    {
        Appends.Add((userId, organizationId, eventName, detail));
        return Task.CompletedTask;
    }
}
