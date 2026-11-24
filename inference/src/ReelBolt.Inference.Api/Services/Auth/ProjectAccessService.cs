using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Auth;

/// <summary>Permission matrix: docs/design/tenancy.md. Billing has no project access at all.</summary>
public sealed class ProjectAccessService(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    ReelBolt.Inference.Api.Services.ApiKeys.IApiKeyContext? apiKeyContext = null) : IProjectAccessService
{
    public async Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct)
    {
        if (currentUser.OrganizationId is null || currentUser.OrgRole is null)
            return new ProjectAccessResult(ProjectAccessOutcome.NotFound, null);

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        return Evaluate(project, permission);
    }

    public ProjectAccessResult Get(Guid projectId, ProjectPermission permission)
    {
        if (currentUser.OrganizationId is null || currentUser.OrgRole is null)
            return new ProjectAccessResult(ProjectAccessOutcome.NotFound, null);

        Project? project = db.Projects.FirstOrDefault(p => p.Id == projectId);
        return Evaluate(project, permission);
    }

    /// <inheritdoc />
    public bool Allows(Project project, ProjectPermission permission)
    {
        // The project was loaded through the tenant filter; the explicit comparison keeps this
        // verdict as strict as Evaluate's when a caller hands in a project from elsewhere.
        if (currentUser.OrgRole is not { } role || project.OrganizationId != currentUser.OrganizationId)
            return false;

        return Allowed(role, permission, project.OwnerId == currentUser.UserId);
    }

    private ProjectAccessResult Evaluate(Project? project, ProjectPermission permission)
    {
        // The tenant query filter already scopes the lookup to the active org; the explicit
        // comparison is the second, independent enforcement.
        if (project is null || project.OrganizationId != currentUser.OrganizationId)
            return new ProjectAccessResult(ProjectAccessOutcome.NotFound, null);

        // A project-restricted API key sees only its listed projects; any other one does not exist for it.
        if (apiKeyContext?.AllowedProjectIds is { } allowed && !allowed.Contains(project.Id))
            return new ProjectAccessResult(ProjectAccessOutcome.NotFound, null);

        return Allowed(currentUser.OrgRole!.Value, permission, project.OwnerId == currentUser.UserId)
            ? new ProjectAccessResult(ProjectAccessOutcome.Found, project)
            : new ProjectAccessResult(ProjectAccessOutcome.Forbidden, null);
    }

    public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission)
    {
        if (currentUser.OrganizationId is null || currentUser.OrgRole is not { } role)
            return ProjectAccessOutcome.NotFound;

        // A project-restricted key cannot create projects (nor anything else organization-wide).
        if (apiKeyContext?.AllowedProjectIds is not null)
            return ProjectAccessOutcome.Forbidden;

        // A project that does not exist yet has no creator: Delete is judged on role alone.
        return Allowed(role, permission, isCreator: false)
            ? ProjectAccessOutcome.Found
            : ProjectAccessOutcome.Forbidden;
    }

    /// <summary>The pure role matrix, exposed for tests.</summary>
    public static bool Allowed(OrganizationRole role, ProjectPermission permission, bool isCreator) => role switch
    {
        OrganizationRole.Owner or OrganizationRole.Admin => true,
        OrganizationRole.Member => permission != ProjectPermission.Delete || isCreator,
        _ => false // Billing, and any role added later, is denied until the matrix says otherwise
    };
}
