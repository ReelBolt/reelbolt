namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A user's saved editing style (see docs/editing-styles.md). Owned by the Inference API
/// (<c>editing_styles</c>, <c>__EFMigrationsHistory_Api</c>). The WorkflowEngine never reads this
/// table: a run carries a SNAPSHOT of the chosen style on its execution row
/// (<see cref="WorkflowExecution.StyleConfigJson"/>), so editing or deleting a style later never
/// changes what a past run meant.
///
/// <para>
/// <b>Owner-scoped.</b> A style belongs to exactly one user (<see cref="OwnerId"/>) and is
/// optionally pinned to one of that user's projects (<see cref="ProjectId"/>, cascade-deleted with
/// it). Another user's style is reported as not found, never forbidden.
/// </para>
/// </summary>
public class EditingStyle
{
    public Guid Id { get; set; }

    /// <summary>The user who owns the style.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>When set, the style is offered only inside this project; null offers it everywhere.</summary>
    public Guid? ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The style itself, as <c>StyleConfig</c> JSON (<c>StyleJson.Options</c>), notes already sanitized.</summary>
    public string ConfigJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
