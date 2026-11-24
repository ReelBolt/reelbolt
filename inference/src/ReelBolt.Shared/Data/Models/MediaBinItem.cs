namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One media reference placed in one bin (table <c>media_bin_items</c>, Inference-API-owned).
///
/// <para>
/// A bin item is a <b>pointer, not a copy</b>: it names media that already exists elsewhere and
/// owns nothing. It carries the same closed <see cref="MediaSourceKind"/> discriminator as
/// <see cref="MediaDerivative"/>, so the panel can hold both an uploaded <c>ProjectFile</c> and an
/// unregistered step-result render without inventing a second reference shape.
/// </para>
/// <para>
/// There is deliberately <b>no ownership FK on either source column</b> and <b>no soft-delete
/// column</b>: the source may be a step result, which is deletable and must not be held hostage by
/// a UI pin (the same rationale as <see cref="MediaDerivative.SourceStepResultId"/>), and a pin is
/// not content — removing it is a row delete. The API verifies at write time that the referenced
/// media belongs to the bin's own project, which is the check that actually prevents a cross-project
/// leak; a dangling pointer left by a later delete is resolved to "absent" on read.
/// </para>
/// </summary>
public class MediaBinItem
{
    public Guid Id { get; set; }

    /// <summary>The bin holding this item. Cascade-deleted with the bin (bins are soft-deleted by
    /// the API, so this only fires if a bin row is ever physically removed).</summary>
    public Guid BinId { get; set; }

    /// <summary>Which of the two columns below carries the reference.</summary>
    public MediaSourceKind SourceKind { get; set; }

    /// <summary>The referenced <c>ProjectFile.Id</c> when <see cref="SourceKind"/> is
    /// <see cref="MediaSourceKind.ProjectFile"/>; null otherwise.</summary>
    public Guid? SourceProjectFileId { get; set; }

    /// <summary>The referenced <c>WorkflowStepResult.Id</c> when <see cref="SourceKind"/> is
    /// <see cref="MediaSourceKind.StepResult"/>; null otherwise, and never a foreign key.</summary>
    public Guid? SourceStepResultId { get; set; }

    /// <summary>Display order inside the bin, ascending.</summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
