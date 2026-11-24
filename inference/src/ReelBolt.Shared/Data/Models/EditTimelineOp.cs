namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One revision's batch of client-authored edit operations (table <c>edit_timeline_ops</c>,
/// Inference-API-owned). The op log is append-only: every PATCH that advances a timeline's
/// revision writes exactly one row, so <c>(TimelineId, Revision)</c> is unique.
/// </summary>
public class EditTimelineOp
{
    public Guid Id { get; set; }

    /// <summary>The timeline this op batch belongs to. Cascade-deleted with the timeline.</summary>
    public Guid TimelineId { get; set; }

    /// <summary>The revision this op batch produced.</summary>
    public int Revision { get; set; }

    /// <summary>The client's <c>ops</c> array, verbatim (jsonb).</summary>
    public string OpsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
