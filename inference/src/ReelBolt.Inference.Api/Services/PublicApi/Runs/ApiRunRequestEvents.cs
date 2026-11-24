namespace ReelBolt.Inference.Api.Services.PublicApi.Runs;

/// <summary>
/// In-process hook for the end of a queued run request (public-api T-007). The webhook ticket (T-009) implements
/// it to deliver <c>run_request.started</c> / <c>run_request.refused</c>; the default does nothing. A throwing
/// implementation never fails the dispatcher (it is logged).
/// </summary>
public interface IApiRunRequestEvents
{
    /// <summary>The queued start became run <paramref name="executionId"/>.</summary>
    Task StartedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, Guid executionId, CancellationToken ct);

    /// <summary>The queued start was refused with <paramref name="code"/> (the code the synchronous path gives).</summary>
    Task RefusedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, string code, string message, CancellationToken ct);
}

/// <summary>The default hook: nothing listens yet.</summary>
public sealed class NoOpApiRunRequestEvents : IApiRunRequestEvents
{
    public Task StartedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, Guid executionId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RefusedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, string code, string message, CancellationToken ct) =>
        Task.CompletedTask;
}
