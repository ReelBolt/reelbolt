using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library (global avatars)"): the
/// people and objects every project of ONE organization may import. A library item belongs to the caller's ACTIVE
/// organization and is never visible to another one; there is no platform stock library.
///
/// <para>
/// <b>Who may do what.</b> List and preview: any member of the organization (Billing has no content access and is
/// refused). Publish, rename, confirm again, withdraw consent and delete: Owner or Admin
/// (<see cref="OrgAuthority.IsOrganizationAdmin"/>, read from the live role on every call, in the app only). Importing
/// into a project is a project route (<c>POST /api/v1/projects/{projectId}/avatars/import</c>).
/// </para>
/// <para>
/// <b>Answers.</b> While neither avatar switch is on, every action answers <c>404</c> before it reads anything. An
/// item of another organization, or of a kind whose switch is off, does not exist for the caller: <c>404</c> for
/// everyone, whatever their role. Inside the caller's own organization a role too low is <c>403</c>. Refusals carry
/// <c>{ code, error, message, … }</c> like the project avatar routes; a statement refusal also carries the required
/// text, its version and both language twins. No response carries a storage key.
/// </para>
/// <para>
/// Every rule that is not about WHO is in <see cref="AvatarLibraryService"/>, shared with the import route, the
/// retention sweep and the assistant.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/avatar-library")]
[Authorize]
public class AvatarLibraryController : ControllerBase
{
    private readonly AvatarLibraryService _library;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;

    public AvatarLibraryController(AvatarLibraryService library, ICurrentUser currentUser, IProjectAccessService projectAccess)
    {
        _library = library;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
    }

    /// <summary>
    /// The organization's items, by name, and the library requirements. With <c>?projectId=</c> (a project the caller
    /// may see) each item also says whether that project already holds an import.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AvatarLibraryListResponse>> List([FromQuery] Guid? projectId, CancellationToken ct)
    {
        if (Member(out Guid organizationId) is { } refused) return refused;
        if (await ProjectRefusalAsync(projectId, ProjectPermission.View, ct) is { } denied) return denied;

        bool canManage = CanManage;
        IReadOnlyList<AvatarLibraryItemView> items = await _library.ListAsync(organizationId, projectId, ct);
        return Ok(new AvatarLibraryListResponse(
            [.. items.Select(i => AvatarLibraryItemResponse.From(i, canManage))],
            AvatarLibraryRequirementsResponse.For(_library.Enabled, canManage)));
    }

    [HttpGet("{itemId:guid}")]
    public async Task<ActionResult<AvatarLibraryItemResponse>> Get(Guid itemId, [FromQuery] Guid? projectId, CancellationToken ct)
    {
        if (Member(out Guid organizationId) is { } refused) return refused;
        if (await ProjectRefusalAsync(projectId, ProjectPermission.View, ct) is { } denied) return denied;

        AvatarLibraryItemView? item = await _library.GetAsync(organizationId, itemId, projectId, ct);
        return item is null ? NotFound() : Ok(AvatarLibraryItemResponse.From(item, CanManage));
    }

    /// <summary>
    /// One PICTURE of an item, for the list and the import picker. Pictures only: a video or a recording of a library
    /// item is never served from here (<c>404</c>). The bytes leave under <see cref="SafeContentPolicy"/>.
    /// </summary>
    [HttpGet("{itemId:guid}/references/{referenceId:guid}/content")]
    public async Task<IActionResult> Picture(Guid itemId, Guid referenceId, CancellationToken ct)
    {
        if (Member(out Guid organizationId) is { } refused) return refused;

        AvatarLibraryPicture? picture = await _library.OpenPictureAsync(organizationId, itemId, referenceId, ct);
        if (picture is null) return NotFound();

        ServedContent served = SafeContentPolicy.DecideByMime(picture.MimeType);
        if (!served.Inline || !served.ContentType.StartsWith("image/", StringComparison.Ordinal))
        {
            await picture.Content.DisposeAsync();
            return NotFound();
        }

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(inline: true, "picture");
        // An item's files never change (new files are a new item), but access to them can end (a revoke, a delete,
        // leaving the workspace): private, and not for long.
        Response.Headers.CacheControl = "private, max-age=300";
        return File(picture.Content, served.ContentType);
    }

    /// <summary>
    /// Saves a usable project avatar to the library (Owner or Admin, verified email, Edit on its project) and turns it
    /// into a linked import of the new item. <c>201</c> with the item.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<AvatarLibraryItemResponse>> Publish([FromBody] PublishToAvatarLibraryRequest request, CancellationToken ct)
    {
        if (Member(out Guid organizationId) is { } refused) return refused;
        // The project first: another organization's project is 404 for everyone, whatever their role here.
        ProjectAccessResult access = await _projectAccess.GetAsync(request.ProjectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;
        if (project.OrganizationId != organizationId) return NotFound();
        if (!CanManage) return Forbid();
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        AvatarLibraryResult<AvatarLibraryItemView> result = await _library.PublishAsync(
            project, request.AvatarId,
            new AvatarLibraryAttestation(request.ConsentStatement, request.ConsentLocale, request.SubjectIsAdult, request.RetentionDays),
            _currentUser.UserId, ct);
        if (result.NotFound) return NotFound();
        if (result.Refusal is { } refusal) return refusal.ToResult();
        return CreatedAtAction(nameof(Get), new { itemId = result.Value!.Item.Id }, AvatarLibraryItemResponse.From(result.Value, canManage: true));
    }

    /// <summary>Renames or re-describes an item; every import follows.</summary>
    [HttpPatch("{itemId:guid}")]
    public async Task<ActionResult<AvatarLibraryItemResponse>> Update(Guid itemId, [FromBody] UpdateAvatarLibraryItemRequest request, CancellationToken ct)
    {
        if (await ManagerAsync(itemId, anyKind: false, ct) is { Refusal: { } refused }) return refused;
        Guid organizationId = _currentUser.OrganizationId!.Value;

        return ToResult(await _library.UpdateAsync(organizationId, itemId, request.Name, request.Description, _currentUser.UserId, ct));
    }

    /// <summary>Confirms an item again under the workspace statement (a person: the keep-until date starts again). No file changes.</summary>
    [HttpPost("{itemId:guid}/reconfirm")]
    public async Task<ActionResult<AvatarLibraryItemResponse>> Reconfirm(Guid itemId, [FromBody] ReconfirmAvatarLibraryItemRequest request, CancellationToken ct)
    {
        if (await ManagerAsync(itemId, anyKind: false, ct) is { Refusal: { } refused }) return refused;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;
        Guid organizationId = _currentUser.OrganizationId!.Value;

        return ToResult(await _library.ReconfirmAsync(
            organizationId, itemId,
            new AvatarLibraryAttestation(request.ConsentStatement, request.ConsentLocale, request.SubjectIsAdult, request.RetentionDays),
            _currentUser.UserId, ct));
    }

    /// <summary>Withdraws consent for a person's item: it and every import of it are unusable from this instant. Idempotent.</summary>
    [HttpPost("{itemId:guid}/revoke-consent")]
    public async Task<ActionResult<AvatarLibraryItemResponse>> RevokeConsent(Guid itemId, CancellationToken ct)
    {
        if (await ManagerAsync(itemId, anyKind: false, ct) is { Refusal: { } refused }) return refused;
        Guid organizationId = _currentUser.OrganizationId!.Value;

        return ToResult(await _library.RevokeAsync(organizationId, itemId, _currentUser.UserId, ct));
    }

    /// <summary>
    /// Deletes an item, its imports in every project and its stored files, and reports what was removed. Works on an
    /// item of either kind while any switch is on. The report says what actually happened, never what was meant to:
    /// when an import could not be removed, <c>imports.failed</c> is above zero, the item and its objects are kept
    /// (<c>objects</c> stays at zero) and the delete can be asked again.
    /// </summary>
    [HttpDelete("{itemId:guid}")]
    public async Task<ActionResult<DeleteAvatarLibraryItemResponse>> Delete(Guid itemId, CancellationToken ct)
    {
        if (await ManagerAsync(itemId, anyKind: true, ct) is { Refusal: { } refused }) return refused;
        Guid organizationId = _currentUser.OrganizationId!.Value;

        AvatarLibraryDeleteReport? report = await _library.DeleteAsync(organizationId, itemId, _currentUser.UserId, AvatarLibraryDeleteCause.User, ct);
        if (report is null) return NotFound();

        return Ok(new DeleteAvatarLibraryItemResponse(
            new AvatarLibraryDeleteCountsResponse(report.ImportsDeleted, report.ImportsFailed),
            new AvatarLibraryDeleteCountsResponse(report.ObjectsDeleted, report.ObjectsFailed)));
    }

    // ------------------------------------------------------------------ who

    private bool CanManage => OrgAuthority.IsOrganizationAdmin(_currentUser.OrgRole);

    /// <summary>
    /// Null when the library exists and the caller is a member with content access; otherwise the refusal. Neither
    /// switch on, no active organization or no role in it: <c>404</c>. Billing: <c>403</c>.
    /// </summary>
    private ActionResult? Member(out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (!_library.Enabled) return NotFound();
        if (_currentUser.OrganizationId is not { } active || _currentUser.OrgRole is null) return NotFound();
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();
        organizationId = active;
        return null;
    }

    /// <summary>
    /// The gate of the item actions an Owner or Admin takes. Existence is decided BEFORE the role, so an item of
    /// another organization is <c>404</c> for everyone and a role too low is <c>403</c> only inside the caller's own.
    /// </summary>
    private async Task<ManagerGate?> ManagerAsync(Guid itemId, bool anyKind, CancellationToken ct)
    {
        if (Member(out Guid organizationId) is { } refused) return new ManagerGate(refused);
        if (!await _library.ExistsAsync(organizationId, itemId, anyKind, ct)) return new ManagerGate(NotFound());
        return CanManage ? null : new ManagerGate(Forbid());
    }

    private sealed record ManagerGate(ActionResult Refusal);

    /// <summary>The optional <c>projectId</c> of a read: a project the caller may not see is refused as that project's own routes refuse it.</summary>
    private async Task<ActionResult?> ProjectRefusalAsync(Guid? projectId, ProjectPermission permission, CancellationToken ct)
    {
        if (projectId is not { } id) return null;
        ProjectAccessResult access = await _projectAccess.GetAsync(id, permission, ct);
        if (access.Failure is { } denied) return denied;
        return access.Project!.OrganizationId == _currentUser.OrganizationId ? null : NotFound();
    }

    private ActionResult<AvatarLibraryItemResponse> ToResult(AvatarLibraryResult<AvatarLibraryItemView> result)
    {
        if (result.NotFound) return NotFound();
        if (result.Refusal is { } refusal) return refusal.ToResult();
        return Ok(AvatarLibraryItemResponse.From(result.Value!, CanManage));
    }
}
