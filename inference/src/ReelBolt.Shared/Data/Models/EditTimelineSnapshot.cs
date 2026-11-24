namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A full document snapshot at one revision (table <c>edit_timeline_snapshots</c>,
/// Inference-API-owned). Snapshots and ops share the same revision namespace, so
/// <c>(TimelineId, Revision)</c> is unique here too.
/// </summary>
public class EditTimelineSnapshot
{
    public Guid Id { get; set; }

    /// <summary>The timeline this snapshot belongs to. Cascade-deleted with the timeline.</summary>
    public Guid TimelineId { get; set; }

    /// <summary>The revision this snapshot captures.</summary>
    public int Revision { get; set; }

    /// <summary>The serialized timeline document at this revision (jsonb).</summary>
    public string DocumentJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user-given name (trimmed, 1..80 chars, no control characters); null for automatic checkpoints.</summary>
    public string? Name { get; set; }

    /// <summary>Why the snapshot exists: one of <see cref="EditTimelineSnapshotCause"/>'s camelCase constants.</summary>
    public string Cause { get; set; } = EditTimelineSnapshotCause.Milestone;

    /// <summary>The user whose action produced the snapshot, when one did.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>
    /// When the automatic-checkpoint prune may delete this row. Null = kept for good: named versions
    /// and revision 1 (the origin, so every listed point stays restorable and the origin is never lost).
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>The closed set of snapshot causes (the wire strings, camelCase).</summary>
public static class EditTimelineSnapshotCause
{
    public const string Auto = "auto";
    public const string Idle = "idle";
    public const string BeforeRestore = "beforeRestore";
    public const string BeforeProposal = "beforeProposal";
    public const string BeforeRender = "beforeRender";
    public const string Milestone = "milestone";
    public const string Duplicate = "duplicate";
    public const string Import = "import";
    public const string Named = "named";
}
