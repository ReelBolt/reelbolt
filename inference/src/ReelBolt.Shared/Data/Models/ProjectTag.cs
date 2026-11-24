namespace ReelBolt.Shared.Data.Models;

/// <summary>A free-form label an organization attaches to its projects (one level, no hierarchy). Unique per organization on lower(name).</summary>
public class ProjectTag
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Lower-cased invariant Name: the target of the case-insensitive unique index (EF cannot model lower(name)).</summary>
    public string NameKey { get; set; } = string.Empty;
    /// <summary>A word from the closed palette (see ProjectOrganizationRules.Palette).</summary>
    public string Color { get; set; } = "gray";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<ProjectTagLink> Links { get; set; } = new List<ProjectTagLink>();
}
