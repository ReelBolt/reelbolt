namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>
/// One saved post text: the text for one platform in one language for one finished video of a project. The video is
/// named by <see cref="OutputId"/> (a step result id, or the project file id of an editor render). Owned by the
/// Inference API (<c>post_texts</c>); the engine does not map it. A project child reached only through the
/// project-authorized routes (no tenant filter), cascade-deleted with the project.
/// </summary>
public class PostTextRow
{
    public const int MaxPerProject = 500;

    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid OutputId { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>The hashtags without the leading <c>#</c>, as a JSON array of strings.</summary>
    public string HashtagsJson { get; set; } = "[]";

    /// <summary>The tone asked for when the text was written (free word list item); null when none was chosen.</summary>
    public string? Tone { get; set; }

    /// <summary>True once the user changed the written text by hand.</summary>
    public bool Edited { get; set; }

    public Guid? UpdatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
