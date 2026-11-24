namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One entry in a project's pronunciation glossary: <see cref="Term"/> is spoken as
/// <see cref="Spelling"/> (a written-accent form such as "Novàra", or a respelling in letters such as "Sciaomi").
/// Owned by the Inference API (<c>project_pronunciation_entries</c>); the WorkflowEngine maps the table read-only and
/// substitutes the spelling into the text sent to the speech provider (the text only: it is never put in a provider
/// pronunciation dictionary and never shown in captions). Every spelling is content-moderated when it is saved.
/// Project-scoped by construction: cascade-deleted with the project, reached only through the project access check.
/// </summary>
public class ProjectPronunciationEntry
{
    /// <summary>Most entries one project may hold.</summary>
    public const int MaxEntriesPerProject = 200;

    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>The word as written in the script, NFC-normalised and trimmed.</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>How it should be spoken: a written-accent form or a respelling in letters (no digits, links or markup).</summary>
    public string Spelling { get; set; } = string.Empty;

    public bool CaseSensitive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
