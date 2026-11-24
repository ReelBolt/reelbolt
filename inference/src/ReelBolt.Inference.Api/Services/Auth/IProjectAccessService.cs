using Microsoft.AspNetCore.Mvc;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>What the caller wants to do with a project.</summary>
public enum ProjectPermission
{
    /// <summary>Read the project and anything under it.</summary>
    View,

    /// <summary>Change files, workflows, outputs; run executions.</summary>
    Edit,

    /// <summary>Delete the project: org Admin+ or the project's creator.</summary>
    Delete
}

public enum ProjectAccessOutcome
{
    Found,

    /// <summary>Absent, in another organization, or the caller has no organization (D5: existence never leaks).</summary>
    NotFound,

    /// <summary>In the caller's organization but the role is too low.</summary>
    Forbidden
}

public sealed record ProjectAccessResult(ProjectAccessOutcome Outcome, Project? Project)
{
    public bool IsFound => Outcome == ProjectAccessOutcome.Found;

    /// <summary>The HTTP result for a non-Found outcome; null when found.</summary>
    public ActionResult? Failure => Outcome switch
    {
        ProjectAccessOutcome.Found => null,
        ProjectAccessOutcome.Forbidden => new ForbidResult(),
        _ => new NotFoundResult()
    };
}

/// <summary>
/// The one authority for "can this caller touch project X". Enforced explicitly here and, behind it,
/// by the EF tenant query filter on <c>Project</c>. Platform authority is deliberately not consulted:
/// a platform admin sees only their active organization's projects.
/// </summary>
public interface IProjectAccessService
{
    Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct);

    /// <summary>Synchronous twin of <see cref="GetAsync"/> for the assistant's synchronous tool bodies.</summary>
    ProjectAccessResult Get(Guid projectId, ProjectPermission permission);

    /// <summary>
    /// Whether the caller may perform <paramref name="permission"/> on an already-loaded project: the
    /// same matrix <see cref="GetAsync"/> applies, without another query. A response that already holds
    /// the project uses it to tell the web what the caller may do. The default asks <see cref="Get"/> so
    /// implementations written before this member keep working; the real service answers from the
    /// current user without touching the database.
    /// </summary>
    bool Allows(Project project, ProjectPermission permission) => Get(project.Id, permission).IsFound;

    /// <summary>
    /// Role check for routes with no project yet (list, create, dashboard): NotFound without an
    /// active organization, Forbidden for a role without project access (Billing).
    /// </summary>
    ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission);
}

public static class ProjectAccessOutcomeExtensions
{
    /// <summary>The HTTP result for a non-Found outcome; null when found.</summary>
    public static ActionResult? ToFailure(this ProjectAccessOutcome outcome) => outcome switch
    {
        ProjectAccessOutcome.Found => null,
        ProjectAccessOutcome.Forbidden => new ForbidResult(),
        _ => new NotFoundResult()
    };
}
