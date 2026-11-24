using ReelBolt.Inference.Api.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly WorkflowTemplateProvisioningService _workflowTemplateProvisioningService;
    private readonly StepProviderOwnershipValidator _providerOwnership;
    private readonly IProductEventEmitter? _productEvents;
    private readonly IProjectCreationService? _projectCreation;
    private readonly Services.Storage.IObjectListing? _objectListing;

    private readonly ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? _historyEraser;
    private readonly ILogger<ProjectsController> _logger;

    private readonly Services.Uploads.OrganizationStorageLock _storageLock;

    public ProjectsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        WorkflowTemplateProvisioningService workflowTemplateProvisioningService,
        StepProviderOwnershipValidator? providerOwnership = null,
        IProductEventEmitter? productEvents = null,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? historyEraser = null,
        ILogger<ProjectsController>? logger = null,
        IProjectCreationService? projectCreation = null,
        Services.Storage.IObjectListing? objectListing = null,
        Services.Uploads.OrganizationStorageLock? storageLock = null)
    {
        _storageLock = storageLock ?? new Services.Uploads.OrganizationStorageLock();
        _historyEraser = historyEraser;
        _objectListing = objectListing;
        _productEvents = productEvents;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectsController>.Instance;
        _projectCreation = projectCreation;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _workflowTemplateProvisioningService = workflowTemplateProvisioningService;
        // Optional so the tests that construct this controller directly keep compiling; the app
        // registers it (InferenceApiServiceCollectionExtensions).
        _providerOwnership = providerOwnership ?? new StepProviderOwnershipValidator(db);
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<ProjectResponse>>> List(
        [FromQuery] Guid[]? tagId, [FromQuery] Guid? folderId, [FromQuery] bool unfiled, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.View).ToFailure() is { } denied) return denied;

        // The tenant filter scopes this to the active organization: every member sees the team's projects.
        IQueryable<Project> query = _db.Projects;
        if ((tagId?.Length ?? 0) > ProjectOrganizationRules.MaxTagFilters)
            return BadRequest(new { code = "TOO_MANY_TAG_FILTERS", message = $"Filter by at most {ProjectOrganizationRules.MaxTagFilters} tags." });
        // Repeatable tagId is an AND: the project must carry every listed tag.
        foreach (Guid tag in (tagId ?? []).Distinct())
        {
            Guid t = tag;
            query = query.Where(p => p.TagLinks.Any(l => l.TagId == t));
        }
        if (folderId is { } f) query = query.Where(p => p.FolderId == f);
        else if (unfiled) query = query.Where(p => p.FolderId == null);

        List<Project> rows = await query.OrderByDescending(p => p.CreatedAt).AsNoTracking().ToListAsync(ct);
        Dictionary<Guid, List<ProjectTagRef>> tags = await ProjectOrganizationRules.LoadTagsAsync(_db, rows.Select(p => p.Id).ToList(), ct);
        return Ok(rows.Select(p => ToResponse(p, tags.GetValueOrDefault(p.Id))).ToList());
    }

    [HttpGet("{id:guid}")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<ProjectResponse>> Get(Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(id, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;
        return Ok(await ToResponseAsync(project, ct));
    }

    private static ProjectResponse ToResponse(Project p, IReadOnlyList<ProjectTagRef>? tags) =>
        new(p.Id, p.Name, p.Description, p.Status.ToString(), p.CreatedAt, p.UpdatedAt, p.FolderId, tags ?? [], p.AnalyzeOnUpload);

    private async Task<ProjectResponse> ToResponseAsync(Project p, CancellationToken ct)
    {
        Dictionary<Guid, List<ProjectTagRef>> tags = await ProjectOrganizationRules.LoadTagsAsync(_db, [p.Id], ct);
        return ToResponse(p, tags.GetValueOrDefault(p.Id));
    }

    [HttpPost]
    public async Task<ActionResult<ProjectResponse>> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;

        IProjectCreationService creation = _projectCreation
            ?? new ProjectCreationService(_db, _currentUser, _workflowTemplateProvisioningService, _productEvents);
        ProjectCreationResult created = await creation.CreateAsync(
            new ProjectCreationInput(request.Name, request.Description, request.FolderId, request.TagIds, request.StarterTemplateKey), ct);
        if (created.Failure is { } failure) return failure;
        Project project = created.Project!;

        ProjectResponse response = await ToResponseAsync(project, ct);
        return CreatedAtAction(nameof(Get), new { id = project.Id }, response);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProjectResponse>> Update(Guid id, [FromBody] UpdateProjectRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(id, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        project.Name = request.Name;
        project.Description = request.Description;
        // Null keeps the stored choice. The global switch (MediaAnalysis:OnUpload:Enabled) is applied where an upload
        // is published, so the choice is kept while the operator has the feature off.
        if (request.AnalyzeOnUpload is { } analyzeOnUpload) project.AnalyzeOnUpload = analyzeOnUpload;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(project, ct));
    }

    /// <summary>Replaces the project's tag set and sets/clears its folder in one save.</summary>
    [HttpPut("{id:guid}/organization")]
    public async Task<ActionResult<ProjectResponse>> SetOrganization(Guid id, [FromBody] ProjectOrganizationRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(id, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;
        Guid orgId = project.OrganizationId;

        Guid[] tagIds = (request.TagIds ?? []).Distinct().ToArray();
        if (tagIds.Length > ProjectOrganizationRules.MaxTagsPerProject)
            return Conflict(new { code = "PROJECT_TAG_LIMIT_REACHED", message = "A project can carry at most 20 tags." });
        if (tagIds.Length > 0
            && await _db.ProjectTags.CountAsync(t => tagIds.Contains(t.Id) && t.OrganizationId == orgId, ct) != tagIds.Length)
            return NotFound();
        if (request.FolderId is { } folderId
            && !await _db.ProjectFolders.AnyAsync(f => f.Id == folderId && f.OrganizationId == orgId, ct))
            return NotFound();

        List<ProjectTagLink> existing = await _db.ProjectTagLinks.Where(l => l.ProjectId == id).ToListAsync(ct);
        _db.ProjectTagLinks.RemoveRange(existing.Where(l => !tagIds.Contains(l.TagId)));
        foreach (Guid tagId in tagIds.Where(t => existing.All(l => l.TagId != t)))
            _db.ProjectTagLinks.Add(new ProjectTagLink { ProjectId = id, TagId = tagId });
        project.FolderId = request.FolderId;
        project.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(await ToResponseAsync(project, ct));
    }

    /// <summary>Bulk tag/folder change over up to 100 projects: one save (one transaction), all or nothing, 404 on any foreign id.</summary>
    [HttpPost("organize")]
    public async Task<IActionResult> Organize([FromBody] OrganizeProjectsRequest request, CancellationToken ct)
    {
        if (_projectAccess.AuthorizeOrganization(ProjectPermission.Edit).ToFailure() is { } denied) return denied;
        Guid orgId = _currentUser.OrganizationId!.Value;

        Guid[] projectIds = (request.ProjectIds ?? []).Distinct().ToArray();
        if (projectIds.Length is < 1 or > ProjectOrganizationRules.MaxOrganizeProjects)
            return BadRequest(new { code = "PROJECT_IDS_INVALID", message = "Select between 1 and 100 projects." });
        Guid[] add = (request.AddTagIds ?? []).Distinct().ToArray();
        Guid[] remove = (request.RemoveTagIds ?? []).Distinct().ToArray();
        if (request.FolderId is not null && request.ClearFolder)
            return BadRequest(new { code = "FOLDER_CHOICE_INVALID", message = "Set a folder or clear it, not both." });

        List<Project> projects = await _db.Projects.Where(p => projectIds.Contains(p.Id) && p.OrganizationId == orgId).ToListAsync(ct);
        if (projects.Count != projectIds.Length) return NotFound();
        Guid[] allTags = add.Union(remove).ToArray();
        if (allTags.Length > 0
            && await _db.ProjectTags.CountAsync(t => allTags.Contains(t.Id) && t.OrganizationId == orgId, ct) != allTags.Length)
            return NotFound();
        if (request.FolderId is { } folderId
            && !await _db.ProjectFolders.AnyAsync(f => f.Id == folderId && f.OrganizationId == orgId, ct))
            return NotFound();

        List<ProjectTagLink> links = await _db.ProjectTagLinks.Where(l => projectIds.Contains(l.ProjectId)).ToListAsync(ct);
        DateTime now = DateTime.UtcNow;
        foreach (Project project in projects)
        {
            HashSet<Guid> tags = links.Where(l => l.ProjectId == project.Id).Select(l => l.TagId).ToHashSet();
            foreach (Guid tagId in remove)
                if (tags.Remove(tagId))
                    _db.ProjectTagLinks.Remove(links.First(l => l.ProjectId == project.Id && l.TagId == tagId));
            foreach (Guid tagId in add)
                if (tags.Add(tagId))
                    _db.ProjectTagLinks.Add(new ProjectTagLink { ProjectId = project.Id, TagId = tagId });
            if (tags.Count > ProjectOrganizationRules.MaxTagsPerProject)
                return Conflict(new { code = "PROJECT_TAG_LIMIT_REACHED", message = "A project can carry at most 20 tags." });
            if (request.FolderId is { } target) project.FolderId = target;
            else if (request.ClearFolder) project.FolderId = null;
            project.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(id, ProjectPermission.Delete, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if ((await ProjectOutputHold.ReadAsync(_db, id, ct)).IsActive)
            return Conflict(new { code = "PROJECT_UNAVAILABLE", message = "This project cannot be deleted right now." });

        // The history rows cascade with the project, so the protected copies they name are removed first:
        // afterwards nothing would remember their keys. A failure stops the delete — the project row
        // stays, so the next attempt can still find and remove those objects. (A project whose output
        // hold is set was already refused above.)
        if (_historyEraser is not null)
        {
            try
            {
                await _historyEraser.DeleteProjectObjectsAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting the history objects of project {ProjectId} failed; the project was kept", id);
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    code = "PROJECT_DELETE_FAILED",
                    message = "The project could not be deleted right now. Try again."
                });
            }
        }

        // The project's avatars go with it by cascade; their audit events stay (docs/design/avatars.md).
        await Services.Avatars.ProjectAvatarEvents.AddForProjectDeleteAsync(_db, project, _currentUser.UserId, ct, _objectListing);
        // The strips of moments its analyses kept are frames of the project's videos that no row names: marked here,
        // so the purge removes them once the project is gone.
        await Services.Background.StorageRetentionService.MarkEventStripsForProjectDeleteAsync(_db, project, _objectListing, ct);
        // The same for its analysis pass cache: a frame grid entry holds small pictures of the project's videos,
        // and the age sweep only reaches projects that still exist.
        await Services.Background.StorageRetentionService.MarkAnalysisCacheForProjectDeleteAsync(_db, project, _objectListing, ct);
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Moves a project into another organization (A10). Both sides must be Owner/Admin: the caller's
    /// role in the project's CURRENT organization comes from the active membership
    /// (<see cref="ICurrentUser.OrgRole"/>), and the role in the target organization is read from
    /// <c>organization_memberships</c>, because the token only ever carries the active one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The move is refused with <c>409</c> when any step of any of the project's workflows names an
    /// inference provider that belongs to the current organization — after the move that row is
    /// invisible to the project, so the step would silently resolve to the target organization's
    /// default instead of the provider its author chose. The refusal LISTS those references rather
    /// than repointing them: silently rewriting an author's provider choice is not a transfer.
    /// Platform-managed rows are never listed (every organization sees them) and neither is a row the
    /// target organization already owns (after the move it is the project's own).
    /// </para>
    /// <para>
    /// Provider references only. Org-scoped CUSTOM AGENTS referenced by these workflows are the
    /// analogous case, checked by the agent surface (A8) rather than here.
    /// </para>
    /// <para>
    /// A target organization the caller cannot administer answers <c>404</c>, never <c>403</c>: the
    /// same D5 rule the rest of the tenancy surface follows — a caller learns nothing about an
    /// organization they have no standing in.
    /// </para>
    /// </remarks>
    [HttpPost("{id:guid}/transfer")]
    public async Task<IActionResult> Transfer(Guid id, [FromBody] TransferProjectRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(id, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // Admin+ in the SOURCE organization. Read from the active membership (never a cached value),
        // and deliberately stricter than the Edit permission the access check above grants a Member:
        // handing a project to another tenant is an administrative act.
        if (!IsOrgAdmin(_currentUser.OrgRole)) return Forbid();

        if (request.OrganizationId == project.OrganizationId)
        {
            return BadRequest(new { error = "The project already belongs to that organization." });
        }

        bool targetExists = await _db.Organizations.AnyAsync(o => o.Id == request.OrganizationId, ct);
        OrganizationRole? targetRole = await _db.OrganizationMemberships
            .AsNoTracking()
            .Where(m => m.OrganizationId == request.OrganizationId && m.UserId == _currentUser.UserId)
            .Select(m => (OrganizationRole?)m.Role)
            .FirstOrDefaultAsync(ct);

        if (!targetExists || !IsOrgAdmin(targetRole))
            return NotFound();

        IReadOnlyList<DanglingProviderReference> dangling =
            await _providerOwnership.FindDanglingAsync(project.Id, request.OrganizationId, ct);

        if (dangling.Count > 0)
        {
            return Conflict(new
            {
                error =
                    "Transfer refused: this project's workflows reference inference providers owned by the "
                    + "current organization, which would not be visible in the target organization. "
                    + "Repoint those steps at a provider of the target organization (or a platform-managed "
                    + "one) and try again.",
                danglingProviderReferences = dangling
            });
        }

        // A project with an avatar saved to this organization's library stays: a later verdict could no longer reach
        // that library copy once the project belongs to another organization (docs/csam.md).
        // The check and the move run inside the source organization's storage critical section (the one publish and import
        // take), in one transaction, so a publish cannot add a copy between the check and the save. Only this one
        // organization lock is ever held by the transfer, so it cannot deadlock with another holder.
        await using Services.Uploads.OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, project.OrganizationId, ct);
        if (await Services.Avatars.Library.AvatarLibraryTransferGuard.HasLibraryCopyAsync(_db, project.Id, project.OrganizationId, ct))
        {
            return Conflict(new
            {
                code = Services.Avatars.Library.AvatarLibraryTransferGuard.RefusalCode,
                message = Services.Avatars.Library.AvatarLibraryTransferGuard.RefusalMessage
            });
        }

        // The hold on this project's files follows its library copies: the copies stay in the SOURCE organization's
        // library, where the digest rule no longer sees a file that moved away. Stamped in the same save as the move
        // (a transfer is not refused for a quarantined project today; it is allowed and keeps the hold).
        await Services.Avatars.Library.AvatarLibraryHoldMarker.StampProjectAsync(_db, project.Id, ct);
        project.OrganizationId = request.OrganizationId;
        project.UpdatedAt = DateTime.UtcNow;
        // Tags and folders belong to one organization; neither follows the project out.
        project.FolderId = null;
        _db.ProjectTagLinks.RemoveRange(await _db.ProjectTagLinks.Where(l => l.ProjectId == project.Id).ToListAsync(ct));

        // A design-system library belongs to one organization; the link must not follow the project out.
        List<DesignSystem> linked = await _db.DesignSystems
            .Where(s => s.ProjectId == project.Id && s.ParentLibraryId != null)
            .ToListAsync(ct);
        foreach (DesignSystem system in linked)
        {
            system.ParentLibraryId = null;
            system.UpdatedAt = project.UpdatedAt;
        }

        await _db.SaveChangesAsync(ct);
        await guard.CommitAsync(ct);

        return Ok(new TransferProjectResponse(project.Id, project.OrganizationId));
    }

    /// <summary>Owner or Admin — the roles that may administer an organization (A2's role matrix).</summary>
    private static bool IsOrgAdmin(OrganizationRole? role) =>
        role is OrganizationRole.Owner or OrganizationRole.Admin;
}
