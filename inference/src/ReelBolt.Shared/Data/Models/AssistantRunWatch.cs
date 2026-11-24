namespace ReelBolt.Shared.Data.Models;

/// <summary>Where an <see cref="AssistantRunWatch"/> stands. Stored as its name.</summary>
public enum AssistantRunWatchStatus
{
    /// <summary>Armed; waiting for the run to finish.</summary>
    Pending,

    /// <summary>A waker holds it and is writing the wake turn.</summary>
    Claimed,

    /// <summary>The wake message was written.</summary>
    Fired,

    /// <summary>Withdrawn (thread archived or the user stepped in).</summary>
    Cancelled,

    /// <summary>The run never finished within the watch's lifetime.</summary>
    Expired,

    /// <summary>Given up after repeated failures to wake the thread.</summary>
    Abandoned
}

/// <summary>
/// A server-side request, made by the assistant with <c>WatchExecution</c>, to be woken in a chat thread
/// when a workflow run reaches a terminal state. API-owned; reached only through its thread (which is
/// tenant-filtered), so it carries no filter of its own.
/// </summary>
public class AssistantRunWatch
{
    public Guid Id { get; set; }

    /// <summary>The thread to wake. Deleting the thread deletes its watches.</summary>
    public Guid ThreadId { get; set; }

    /// <summary>The run watched. No foreign key: <c>workflow_executions</c> is engine-owned.</summary>
    public Guid ExecutionId { get; set; }

    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The workflow's name for display only; sanitized and at most <see cref="WorkflowNameMaxLength"/> characters.</summary>
    public string WorkflowName { get; set; } = string.Empty;

    public const int WorkflowNameMaxLength = 120;

    public AssistantRunWatchStatus Status { get; set; } = AssistantRunWatchStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>While <see cref="AssistantRunWatchStatus.Claimed"/>, when the claim lapses.</summary>
    public DateTime? ClaimedUntil { get; set; }

    public int Attempts { get; set; }
    public DateTime? FiredAt { get; set; }

    /// <summary>The <c>assistant_messages</c> row written when the watch fired.</summary>
    public Guid? WakeMessageId { get; set; }

    /// <summary>A short code for why a watch ended without firing.</summary>
    public string? EndReason { get; set; }
}
