namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Editor v2, P9: one user's editor preferences — a custom keymap and saved panel layouts. Per USER,
/// not per project (a shortcut follows the editor, not the footage), one row per user. Both blobs are
/// bounded JSON the client owns the meaning of; the server checks shape and size only
/// (<see cref="ReelBolt.Shared.Editing.EditorPreferencesValidator"/>).
/// </summary>
public class EditorUserPreference
{
    public Guid UserId { get; set; }

    /// <summary>JSON object: action id → key chord (e.g. <c>"blade": "B"</c>). At most 200 entries.</summary>
    public string KeymapJson { get; set; } = "{}";

    /// <summary>JSON array of saved layouts <c>{ name, workspace, sizes, leftTab, rightTab }</c>. At most 50.</summary>
    public string LayoutsJson { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Editor v2, P9: a named preset a project's editors share — a transition, an effect stack or a
/// mastering target. <see cref="Kind"/> is one of <c>transition</c>/<c>effect</c>/<c>mastering</c>;
/// <see cref="PayloadJson"/> is validated per kind against the SAME closed catalogues and ranges the
/// timeline validator uses (<see cref="ReelBolt.Shared.Editing.EditorPresetValidator"/>), so a preset
/// can never carry a value a document could not.
/// </summary>
public class EditorPreset
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Editor v2, P9: a timecoded review comment on an edit timeline. <see cref="Frame"/> (and the optional
/// <see cref="DurationFrames"/>) are timeline frames — integer, like every position in the document.
/// A reply names its thread's root in <see cref="ParentId"/> (one level). Soft-deleted by
/// <see cref="DeletedAt"/>, so a thread keeps its shape when a comment in it is removed.
/// </summary>
public class EditTimelineComment
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid TimelineId { get; set; }

    public Guid? ParentId { get; set; }

    public Guid AuthorUserId { get; set; }

    /// <summary>The author's display name at the time of writing (their email).</summary>
    public string AuthorName { get; set; } = string.Empty;

    public long Frame { get; set; }

    public long? DurationFrames { get; set; }

    /// <summary>Plain text, at most 4000 characters.</summary>
    public string Body { get; set; } = string.Empty;

    public bool Resolved { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Guest review (Wave 4): the review link a guest wrote this comment through; null for a member's
    /// comment. A guest comment has <see cref="AuthorUserId"/> = <see cref="Guid.Empty"/>, so every
    /// "is the author" equality check fails for it.
    /// </summary>
    public Guid? GuestLinkId { get; set; }

    /// <summary>The guest's self-asserted display name (1..80), plain text; null for a member's comment.</summary>
    public string? GuestName { get; set; }
}
