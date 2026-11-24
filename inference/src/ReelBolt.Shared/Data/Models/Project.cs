namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A video generation project owned by a user.
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid OwnerId { get; set; }

    /// <summary>The organization (workspace) the project belongs to. Never null after the A1 backfill.</summary>
    public Guid OrganizationId { get; set; }

    public ProjectStatus Status { get; set; } = ProjectStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the project last gained a quarantined file (CSAM or moderation). Written by the API in the same save as
    /// the verdict and never cleared, so deleting or releasing the row does not reopen reuse of earlier output.
    /// </summary>
    public DateTime? ContentQuarantinedAt { get; set; }

    /// <summary>
    /// When an editor of the project agreed that its video frames may be sent to the segmentation vendor to find
    /// a named object (magic blur / mask). Null = not agreed. Written only by the UI-only
    /// <c>object-effects/consent</c> routes; the assistant and MCP only read it, never grant it.
    /// </summary>
    public DateTime? ObjectEffectsConsentAt { get; set; }

    /// <summary>
    /// When an editor of the project agreed that a source video may be uploaded to a hosted AI model to enhance it (AI upscale,
    /// AI smooth slow motion). Null = not agreed. Written only by the UI-only <c>video-enhance/consent</c> routes; the assistant
    /// and MCP only read it, never grant it. The <c>/enhance</c> route writes <c>allowSourceMediaEgress: true</c> into a hosted
    /// step only when this is set. Column <c>video_enhance_consent_at</c> (API-owned).
    /// </summary>
    public DateTime? VideoEnhanceConsentAt { get; set; }

    /// <summary>
    /// When an editor of the project agreed that the reference pictures and videos of its avatars (people and objects)
    /// may be sent to the AI providers the project uses from the editors and the assistant
    /// (docs/design/avatar-objects.md). Null = not agreed. Written only by the UI-only <c>avatars/media-consent</c>
    /// routes; the assistant and MCP only read it, never grant it. A workflow step keeps its own
    /// <c>allowSourceMediaEgress</c> switch. Column <c>subject_media_consent_at</c> (API-owned; the engine does not map it).
    /// </summary>
    public DateTime? SubjectMediaConsentAt { get; set; }

    /// <summary>
    /// Who recorded <see cref="SubjectMediaConsentAt"/>; null when it is not recorded. Column
    /// <c>subject_media_consent_by_user_id</c> (API-owned; the engine does not map it).
    /// </summary>
    public Guid? SubjectMediaConsentByUserId { get; set; }

    /// <summary>
    /// The project's choice to analyse each new video when it is uploaded: the engine then keeps what the silence, shot,
    /// frame grid and event passes find, so a later workflow does not decode the video again. Off by default. Changed
    /// through the project update route (Edit access). Read in one place, <c>UploadAnalysisSetting</c>, together with
    /// the global switch <c>MediaAnalysis:OnUpload:Enabled</c>. Column <c>analyze_on_upload</c> (API-owned; the engine
    /// does not map it).
    /// </summary>
    public bool AnalyzeOnUpload { get; set; }

    /// <summary>
    /// When the project last gained a file with a CSAM verdict. Written by the API in the same save as the verdict,
    /// moves forward only. Only the operator clears it (the reuse marker above stays). Moderation verdicts do not write it. Output made before it is not served.
    /// </summary>
    public DateTime? CsamOutputHoldAt { get; set; }

    /// <summary>The folder the project is filed in; null = unfiled. FK ON DELETE SET NULL.</summary>
    public Guid? FolderId { get; set; }
    public ApplicationUser Owner { get; set; } = null!;
    public ProjectFolder? Folder { get; set; }
    public ICollection<ProjectTagLink> TagLinks { get; set; } = new List<ProjectTagLink>();
    public ICollection<ProjectFile> Files { get; set; } = new List<ProjectFile>();
    public ICollection<WorkflowDefinition> WorkflowDefinitions { get; set; } = new List<WorkflowDefinition>();
    public ICollection<WorkflowExecution> WorkflowExecutions { get; set; } = new List<WorkflowExecution>();
}
