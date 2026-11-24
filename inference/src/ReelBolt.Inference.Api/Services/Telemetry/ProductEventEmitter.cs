using Microsoft.Extensions.Logging;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// Best-effort server-side emitter for the product-analytics events. Telemetry never fails the request:
/// every failure is swallowed (and logged without any payload). Call it AFTER the request's own work has
/// been saved, so the emitter's write can never carry, or be blamed for, the caller's pending changes.
/// </summary>
public interface IProductEventEmitter
{
    /// <summary>Records <paramref name="eventName"/> once per user (first occurrence wins).</summary>
    Task FirstAsync(Guid userId, Guid? organizationId, string eventName, CancellationToken ct);

    /// <summary>Appends one row with a closed <paramref name="detail"/> token (never free text).</summary>
    Task AppendAsync(Guid userId, Guid? organizationId, string eventName, string? detail, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ProductEventEmitter(IProductEventLog log, ILogger<ProductEventEmitter> logger) : IProductEventEmitter
{
    /// <inheritdoc />
    public async Task FirstAsync(Guid userId, Guid? organizationId, string eventName, CancellationToken ct)
    {
        if (userId == Guid.Empty) return;
        try
        {
            await log.ClaimAndAppendAsync(userId, organizationId, eventName, ProductEventSources.Server, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Product event {Event} could not be recorded", eventName);
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(Guid userId, Guid? organizationId, string eventName, string? detail, CancellationToken ct)
    {
        if (userId == Guid.Empty) return;
        try
        {
            await log.AppendAsync(
                new ProductEvent
                {
                    UserId = userId,
                    OrganizationId = organizationId,
                    Event = eventName,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Source = ProductEventSources.Server,
                    Detail = detail,
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Product event {Event} could not be recorded", eventName);
        }
    }
}
