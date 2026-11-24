using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The project side of the workspace avatar library (docs/design/avatar-objects.md "The workspace avatar library
/// (global avatars)"): importing a library item into the project, and what a LINKED import may and may not do on the
/// project's own avatar routes. A linked import follows its library item, so its name, description, consent, files and
/// label are changed in the library; here only its voice link is the project's own, and deleting it removes the import
/// and its copied files and leaves the library item alone.
/// </summary>
public partial class ProjectAvatarsController
{
    /// <summary>
    /// Imports a library item of the project's organization: a linked project avatar with its own copies of the item's
    /// files (<c>201</c>). Edit on the project and a verified email; everything else (the kind's switch and plan
    /// feature, usability, the limits, the storage quota) is the library service's, shared with every other caller.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<ProjectAvatarResponse>> ImportFromLibrary(
        Guid projectId, [FromBody] ImportLibraryAvatarRequest request, [FromServices] AvatarLibraryService library, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        AvatarLibraryResult<ProjectAvatar> result = await library.ImportAsync(access.Project!, request.LibraryItemId, _currentUser.UserId, ct);
        if (result.NotFound) return NotFound();
        if (result.Refusal is { } refusal) return refusal.ToResult();

        ProjectAvatar avatar = result.Value!;
        return CreatedAtAction(nameof(Get), new { projectId, avatarId = avatar.Id },
            (await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }

    /// <summary>
    /// The <c>library</c> part of the requirements: the two workspace statements, and whether the caller may manage the
    /// library (an Owner or Admin of the project's organization, read from the live role on every call).
    /// </summary>
    private AvatarLibraryRequirementsResponse LibraryRequirements(Project project) =>
        AvatarLibraryRequirementsResponse.For(
            _options.AnyEnabled,
            _currentUser.OrganizationId == project.OrganizationId && OrgAuthority.IsOrganizationAdmin(_currentUser.OrgRole));

    /// <summary>
    /// A plain edit of a linked import takes only the voice link: its name, description, disclosure label and, for a
    /// variant, its label and variant description and an object's identity facts are its library item's. Null for an avatar that is not an import, or
    /// an edit that touches none of them.
    /// </summary>
    private static ObjectResult? LinkedImportEditRefusal(ProjectAvatar avatar, UpdateProjectAvatarRequest request) =>
        avatar.IsLibraryImport
        && (request.Name is not null || request.Description is not null || request.DisclosureLabel is not null || request.DisclosesOtherwise
            || request.VariantLabel is not null || request.VariantDescription is not null || request.IdentityFacts is not null)
            ? FromLibrary()
            : null;

    private static ObjectResult FromLibrary() =>
        ConflictCode("AVATAR_FROM_LIBRARY",
            "This one comes from the workspace library and follows it. Change it in the workspace library, or remove it from this project.");
}
