namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The session of a job: pinned to one runner and one gateway pod for its whole life, so ops route
/// by <see cref="GatewayInstance"/> with no shared cache. Table <c>runner_sessions</c>, owned by the
/// WorkflowEngine migrations, written by the gateway.
/// </summary>
public class RunnerSession
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid ExecutionId { get; set; }
    public Guid RunnerId { get; set; }
    public string? GatewayInstance { get; set; }
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpAt { get; set; }

    /// <summary>Null while the session is open.</summary>
    public DateTime? ClosedAt { get; set; }

    public RunnerJob? Job { get; set; }
}
