namespace ReelBolt.Shared.Data.Models;

/// <summary>A folder an organization files projects into. One level in v1: <see cref="ParentFolderId"/> is always null.</summary>
public class ProjectFolder
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Lower-cased invariant Name: the target of the case-insensitive unique index (EF cannot model lower(name)).</summary>
    public string NameKey { get; set; } = string.Empty;
    public Guid? ParentFolderId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
