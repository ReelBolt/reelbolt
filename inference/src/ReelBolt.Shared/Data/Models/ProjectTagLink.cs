namespace ReelBolt.Shared.Data.Models;

/// <summary>Joins a project to a tag. Composite key (ProjectId, TagId); cascades from both sides. Guarded at the project, so it carries no tenant filter.</summary>
public class ProjectTagLink
{
    public Guid ProjectId { get; set; }
    public Guid TagId { get; set; }
    public Project Project { get; set; } = null!;
    public ProjectTag Tag { get; set; } = null!;
}
