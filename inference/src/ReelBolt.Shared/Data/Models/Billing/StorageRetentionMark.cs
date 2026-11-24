namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>
/// One object the F10 per-plan retention job has SOFT-MARKED for deletion: it is past its
/// organization's <c>RetentionDays</c>, no longer referenced by anything the Inference API owns,
/// and is kept on disk for <c>Billing:StorageRetention:GraceDays</c> (7 by default) before the
/// purge pass deletes it with an S3 multi-object delete.
/// <para>
/// A row here is the durable half of the two-phase delete: without it a restart, a downgrade or a
/// plan change would have to re-derive "was this object already marked, and when?" from the object
/// store. The grace period is what makes the delete recoverable — an operator can restore the
/// object during the window, and a mis-marked plan takes a week to become a real loss rather than
/// a moment.
/// </para>
/// <para>
/// Owned by the Inference API (<c>storage_retention_marks</c>, history <c>__EFMigrationsHistory_Api</c>).
/// </para>
/// </summary>
public class StorageRetentionMark
{
    public Guid Id { get; set; }

    /// <summary>The owning organization — the plan whose <c>RetentionDays</c> marked it, and the tenant key the sweep scopes by.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The project whose <c>projects/{projectId}/</c> prefix holds the object.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Full object key, e.g. <c>projects/{projectId}/outputFiles/{executionId}/render.mp4</c>.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>
    /// Storage category the key lives under. The retention pass only ever <em>discovers</em> candidates under
    /// <c>outputFiles</c> and <c>agentFiles</c>, but it honours a mark in any category: a mark written by a
    /// user's own delete is an explicit instruction to remove that key, not a retention decision.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>When the object was marked. The purge pass deletes marks at least <c>GraceDays</c> old.</summary>
    public DateTime MarkedAt { get; set; }

    /// <summary>
    /// When the object was actually deleted, or <c>null</c> while it is still inside the grace period.
    /// <para>
    /// A purged row is kept while an engine-owned reference may still name the deleted object: it is
    /// how an object referenced only by <c>workflow_step_results</c> (whose key this service must not
    /// clear) is prevented from being marked and deleted again on every run, and it is the durable
    /// worklist the engine-side slice reads. Once nothing references the key any more the row is
    /// garbage-collected by the next sweep.
    /// </para>
    /// </summary>
    public DateTime? PurgedAt { get; set; }
}
