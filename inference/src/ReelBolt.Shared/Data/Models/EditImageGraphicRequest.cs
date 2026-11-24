namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One request to DESIGN a still graphic for a picture (thumbnail) document — a title treatment, a
/// badge, a price tag, an arrow — carried out by an execution of the project's hidden
/// <see cref="WorkflowDefinitionKind.GraphicDesign"/> workflow (one <see cref="StepType.DesignGraphic"/>
/// step, <see cref="AgentType.GraphicDesigner"/>). See docs/editor.md "Designed graphics".
///
/// <para>
/// <b>Who writes what.</b> The Inference API owns the table (migration <c>AddEditImageGraphicRequests</c>)
/// and writes the request; the WorkflowEngine maps it with <c>ExcludeFromMigrations()</c> and writes the
/// <c>Result*</c> columns once the PNG is rendered, verified and uploaded; the Inference API then
/// SETTLES the request — inserts the PNG as a layer of the document and records
/// <see cref="ResultLayerId"/> — the next time anyone reads it. The engine never touches the document's
/// own tables: a layer document is an interactive editor's state, and the layer stack arithmetic (z-order,
/// revision, retention) stays in one service. The run's status lives on the execution row, never here.
/// </para>
/// <para>
/// No tenant query filter: like <c>composition_revisions</c> it hangs off a project, every read path
/// resolves the project through <c>IProjectAccessService</c> first and constrains the document to it
/// (<c>TenantBoundaryInventoryTests</c>). <see cref="OrganizationId"/> is denormalized, never a guard.
/// </para>
/// </summary>
public class EditImageGraphicRequest
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>The organization that owns the project, denormalized at creation. Never an authorization input.</summary>
    public Guid OrganizationId { get; set; }

    /// <summary>The picture document the graphic is for. Cascade-deleted with it.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>The request's own execution (unique). Its sandbox is keyed by this id.</summary>
    public Guid ExecutionId { get; set; }

    /// <summary>The project's hidden <see cref="WorkflowDefinitionKind.GraphicDesign"/> workflow.</summary>
    public Guid WorkflowDefinitionId { get; set; }

    /// <summary>
    /// What the user asked for, verbatim (bounded): the graphic to design, or — when
    /// <see cref="RevisesLayerId"/> is set — the change to make to it. Quoted to the agent as data.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>One of <c>GraphicDesignRules.Placements</c>: where on the picture the graphic goes.</summary>
    public string Placement { get; set; } = "center";

    /// <summary>The render frame, in pixels — the size of the Remotion <c>Still</c>.</summary>
    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    /// <summary>
    /// True when the frame is the graphic's own box (a size was asked for); false when the frame is the
    /// whole picture and <see cref="Placement"/> tells the designer where on it to draw.
    /// </summary>
    public bool Sized { get; set; }

    /// <summary>Canvas pixels of the frame's top-left corner (where frame pixel 0,0 lands on the picture).</summary>
    public double FrameX { get; set; }

    public double FrameY { get; set; }

    /// <summary>Canvas pixels per frame pixel (1 unless a canvas larger than the render cap was scaled down).</summary>
    public double FrameScale { get; set; } = 1;

    /// <summary>Canvas pixels of the region the graphic is expected to occupy — where the editor shows the work in progress.</summary>
    public double TargetX { get; set; }

    public double TargetY { get; set; }

    public double TargetWidth { get; set; }

    public double TargetHeight { get; set; }

    /// <summary>"Change this graphic": the designed layer being revised; null for a new graphic.</summary>
    public Guid? RevisesLayerId { get; set; }

    /// <summary>The composition workspace version a revision starts from (from the revised layer's provenance).</summary>
    public Guid? BaseVersionId { get; set; }

    public Guid RequestedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---------------------------------------------------------------- written by the engine

    /// <summary>The verified, cropped PNG: <c>projects/{p}/agentFiles/image-editor/{documentId}/graphic-{guid}.png</c>.</summary>
    public string? ResultStorageKey { get; set; }

    /// <summary>The PNG's pixel size after the transparent margins were cropped away.</summary>
    public int? ResultWidth { get; set; }

    public int? ResultHeight { get; set; }

    /// <summary>Frame pixels of the crop's top-left corner (where the PNG sat inside the rendered frame).</summary>
    public int? ResultOffsetX { get; set; }

    public int? ResultOffsetY { get; set; }

    /// <summary>The composition workspace the graphic's source was recorded in, and its version.</summary>
    public Guid? ResultWorkspaceId { get; set; }

    public Guid? ResultVersionId { get; set; }

    public int? ResultVersionNumber { get; set; }

    /// <summary>The composition the PNG was rendered from (the source's <c>Still</c> id).</summary>
    public string? ResultCompositionId { get; set; }

    /// <summary>The designer's plain-language summary of what it drew.</summary>
    public string? ResultSummary { get; set; }

    /// <summary>The chat model that designed it (provenance).</summary>
    public string? ResultModelName { get; set; }

    public DateTime? ResultRecordedAt { get; set; }

    // ---------------------------------------------------------------- written by the Inference API

    /// <summary>
    /// The layer the result became, once settled. A concurrency token in the API's mapping, so two
    /// readers settling the same request at once insert ONE layer: the loser's update matches no row.
    /// </summary>
    public Guid? ResultLayerId { get; set; }

    public DateTime? SettledAt { get; set; }
}
