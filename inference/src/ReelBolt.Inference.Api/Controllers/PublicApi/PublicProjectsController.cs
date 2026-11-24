using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.PublicApi.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>
/// <c>/api/public/v1/projects</c>: the organization's projects, narrowed to the key's project list. Create
/// calls the dashboard's own <see cref="IProjectCreationService"/>.
/// </summary>
[Route("api/public/v1/projects")]
public sealed class PublicProjectsController : PublicResourceControllerBase
{
    public const string ProjectRestrictedKeyCode = "project_restricted_key";
    private const int MaxNameLength = 200;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IApiKeyContext _keyContext;
    private readonly IProjectCreationService _creation;

    public PublicProjectsController(
        InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess,
        IApiKeyContext keyContext, IProjectCreationService creation)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _keyContext = keyContext;
        _creation = creation;
    }

    [RequireScope(ApiScopes.ProjectsRead)]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> List([FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        // The role matrix, not AuthorizeOrganization: that refuses every project-restricted key, and a
        // restricted key lists exactly its own projects. Billing still has no project access (403).
        if (_currentUser.OrganizationId is not Guid organizationId || _currentUser.OrgRole is not { } role)
            return NotFoundError();
        if (!ProjectAccessService.Allowed(role, ProjectPermission.View, isCreator: false))
            return PublicError(StatusCodes.Status403Forbidden, PublicApiErrorCodes.Forbidden,
                PublicApiErrorCodes.MessageForStatus(StatusCodes.Status403Forbidden));

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<Project> query = _db.Projects.AsNoTracking().Where(p => p.OrganizationId == organizationId);
        if (_keyContext.AllowedProjectIds is { } allowed)
        {
            Guid[] ids = [.. allowed];
            query = query.Where(p => ids.Contains(p.Id));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!PublicApiPagination.TryDecodeCursor(cursor, out DateTime at, out Guid after))
                return InvalidRequest("The cursor is not valid.");
            query = query.Where(p => p.CreatedAt < at || (p.CreatedAt == at && p.Id.CompareTo(after) < 0));
        }

        List<Project> rows = await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        string? next = more ? PublicApiPagination.EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null;
        return Ok(new PublicApiPage<PublicProjectView>([.. rows.Select(ToView)], next));
    }

    [RequireScope(ApiScopes.ProjectsRead)]
    [HttpGet("{projectId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        return Ok(ToView(access.Project!));
    }

    [RequireScope(ApiScopes.ProjectsWrite)]
    [Idempotent]
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Create([FromBody] PublicCreateProjectRequest request, CancellationToken ct)
    {
        // A project-restricted key cannot create projects: a new project is outside its list by definition.
        if (_keyContext.AllowedProjectIds is not null)
            return PublicError(StatusCodes.Status403Forbidden, ProjectRestrictedKeyCode,
                "This API key is limited to specific projects and cannot create new ones.");
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied)
            return PublicRefusal(denied);

        string name = request?.Name?.Trim() ?? string.Empty;
        if (name.Length == 0) return InvalidRequest("name is required.");
        if (name.Length > MaxNameLength) return InvalidRequest($"name must be at most {MaxNameLength} characters.");

        ProjectCreationResult created = await _creation.CreateAsync(
            new ProjectCreationInput(name, request!.Description), ct);
        if (created.Failure is { } failure) return PublicRefusal(failure);
        return StatusCode(StatusCodes.Status201Created, ToView(created.Project!));
    }

    private static PublicProjectView ToView(Project p) =>
        new(Id(p.Id), p.Name, p.Description, Utc(p.CreatedAt), Utc(p.UpdatedAt));
}
