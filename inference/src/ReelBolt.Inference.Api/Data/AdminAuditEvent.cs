namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// One platform-admin write against a user account (create, update, delete, password reset), table
/// <c>admin_audit_events</c>. Append-only: the Go API INSERTs a row by raw SQL for every admin user write
/// and never updates or deletes one. Platform-wide, so it carries no tenant query filter, and neither
/// user id is a foreign key: deleting either account keeps the audit. The WorkflowEngine does not map it.
/// </summary>
public class AdminAuditEvent
{
    public Guid Id { get; set; }

    /// <summary>The acting platform admin. No FK: the audit outlives the account.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>The account acted on. No FK: a <c>user.delete</c> row must survive the delete.</summary>
    public Guid? TargetUserId { get; set; }

    /// <summary><c>user.create</c>, <c>user.update</c>, <c>user.delete</c>, <c>user.reset_password</c>, …</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// JSON object of changed non-secret fields, <c>{"is_admin":{"old":false,"new":true}}</c>. Never a password
    /// or any other secret.
    /// </summary>
    public string? ChangedFields { get; set; }

    public string? ClientIp { get; set; }

    /// <summary>The caller's User-Agent, truncated to 512 characters by the writer.</summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
