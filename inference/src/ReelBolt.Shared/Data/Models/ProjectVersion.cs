using System.Globalization;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One immutable version of a project: a manifest of the project's files, workflows, timelines,
/// design system, pronunciation glossary and name/description at one moment. Owned by the
/// Inference API (table <c>project_versions</c>); reached only through an authorized project, so it
/// carries no tenant filter of its own.
/// </summary>
public class ProjectVersion
{
    public Guid Id { get; set; }

    /// <summary>The owning project. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Per-project sequence number, starting at 1; unique inside the project.</summary>
    public int Number { get; set; }

    /// <summary>User-facing name (1..80 trimmed). Null for an automatic version.</summary>
    public string? Name { get; set; }

    /// <summary>One of <see cref="ProjectVersionCause"/> values, persisted verbatim.</summary>
    public string Cause { get; set; } = ProjectVersionCause.Auto;

    /// <summary>Who asked for the version; null for a background capture.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the automatic version may be deleted. Null means it is kept until deleted explicitly
    /// (a named version).
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Lowercase hex SHA-256 of the sorted canonical item list.</summary>
    public string ManifestDigest { get; set; } = string.Empty;

    public int ItemCount { get; set; }

    /// <summary>Sum of the file items' sizes; informational.</summary>
    public long TotalBytes { get; set; }

    public ICollection<ProjectVersionItem> Items { get; set; } = new List<ProjectVersionItem>();
}

/// <summary>The closed set of <see cref="ProjectVersion.Cause"/> values.</summary>
public static class ProjectVersionCause
{
    /// <summary>A user saved it with a name.</summary>
    public const string Named = "named";

    /// <summary>An automatic checkpoint (idle capture).</summary>
    public const string Auto = "auto";

    /// <summary>Taken before a run starts.</summary>
    public const string BeforeRun = "beforeRun";

    /// <summary>Taken before an assistant/MCP write is applied.</summary>
    public const string BeforeAssistant = "beforeAssistant";

    /// <summary>The safety version taken before a restore.</summary>
    public const string BeforeRestore = "beforeRestore";

    /// <summary>The periodic idle capture.</summary>
    public const string Idle = "idle";

    public static bool IsValid(string? value) =>
        value is Named or Auto or BeforeRun or BeforeAssistant or BeforeRestore or Idle;
}

/// <summary>Validation for <see cref="ProjectVersion.Name"/>: 1..80 trimmed, no control or Unicode
/// <see cref="UnicodeCategory.Format"/> characters.</summary>
public static class ProjectVersionName
{
    public const int MaxLength = 80;

    /// <summary>True when <paramref name="name"/> is a valid version name.</summary>
    public static bool IsValid(string? name)
    {
        if (name is null)
            return false;

        string trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxLength)
            return false;

        foreach (char c in trimmed)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                return false;
        }

        return true;
    }

    /// <summary>The trimmed name, or <see cref="ArgumentException"/> when it is not valid.</summary>
    public static string Normalize(string? name)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"A version name must be 1..{MaxLength} characters after trimming and contain no control or format characters.",
                nameof(name));

        return name!.Trim();
    }
}
