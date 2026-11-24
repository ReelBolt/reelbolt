namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Editor v2, P6: the user's own notes on one media source — keywords, a free note, a colour label,
/// a favourite star and a flag. One row per (project, source); absent means "nothing written".
/// </summary>
/// <remarks>
/// The source is addressed exactly as <see cref="MediaBinItem"/> addresses it (a kind plus the id of
/// that kind's table), with no FK on either source column for the same reason: a step result is
/// deletable, and an annotation must not hold it hostage. Project ownership is checked at the
/// controller through the source lookup, which is the check that keeps project A from annotating
/// project B's file. Smart bins (<see cref="MediaBin.SmartFilterJson"/>) read these rows.
/// </remarks>
public class MediaAnnotation
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public MediaSourceKind SourceKind { get; set; }

    public Guid? SourceProjectFileId { get; set; }

    public Guid? SourceStepResultId { get; set; }

    /// <summary>A JSON array of lower-cased, trimmed, distinct keywords (at most 32, each at most 64 chars).</summary>
    public string KeywordsJson { get; set; } = "[]";

    /// <summary>Free text, at most 2000 characters.</summary>
    public string? Note { get; set; }

    /// <summary>One of the closed label words (<c>red</c>, <c>orange</c>, <c>yellow</c>, <c>green</c>, <c>blue</c>, <c>violet</c>, <c>grey</c>), or null.</summary>
    public string? ColorLabel { get; set; }

    public bool Favourite { get; set; }

    public bool Flag { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
