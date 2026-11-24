using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

// Identity facts of an object avatar (docs/design/avatar-objects.md "Object fidelity"): the owner can have the Vision
// provider read them from the pictures again (for an avatar made before they existed, or after the pictures changed).
public partial class ProjectAvatarsController
{
    /// <summary>
    /// Reads the object's colour, make and model, marks and scale from its pictures (Edit). Without <c>overwrite</c> only
    /// fields the owner left empty are filled. <c>409 VISION_PROVIDER_REQUIRED</c> with no Vision provider, and
    /// <c>502 IDENTITY_FACTS_UNAVAILABLE</c> when the provider could not read anything (nothing is changed).
    /// </summary>
    [HttpPost("{avatarId:guid}/identity-facts/derive")]
    public async Task<ActionResult<ProjectAvatarResponse>> DeriveIdentityFacts(
        Guid projectId, Guid avatarId, [FromBody] DeriveIdentityFactsRequest? request, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectAvatar? avatar = await FindAsync(projectId, avatarId, ct);
        if (avatar is null) return NotFound();
        if (avatar.IsLibraryImport) return FromLibrary();
        if (avatar.SubjectKind != AvatarSubjectKind.Object) return Bad("field_not_for_people", "Identity facts belong to objects only.");
        if (_identityDescriber is null)
            return AvatarRefusal.Conflict("VISION_PROVIDER_REQUIRED", "Reading the object's details needs a Vision provider.").ToResult();
        if (await _checks.VisionRefusalAsync(ct) is { } noVision) return noVision.ToResult();

        string[] keys = await (from r in _db.ProjectAvatarReferences
                               join f in _db.ProjectFiles.IgnoreQueryFilters() on r.ProjectFileId equals f.Id
                               where r.AvatarId == avatar.Id && r.Kind == AvatarReferenceKind.Image && f.ProjectId == projectId
                               orderby r.Ordinal
                               select f.StorageKey).Take(AvatarIdentityDescriber.MaxPictures).ToArrayAsync(ct);
        List<byte[]> pictures = [];
        foreach (string key in keys)
        {
            if (await _checks.ReadPictureAsync(projectId, key, ct) is { } bytes) pictures.Add(bytes);
        }

        AvatarIdentityFacts? derived = await _identityDescriber.DescribeAsync(access.Project!.OrganizationId, projectId, pictures, ct);
        if (derived is null)
            return AvatarRefusal.Unavailable("IDENTITY_FACTS_UNAVAILABLE", "The details could not be read from the pictures. Type them instead, or try again.").ToResult();

        AvatarIdentityFacts? current = AvatarIdentityFacts.Parse(avatar.IdentityFacts);
        bool overwrite = request?.Overwrite == true;
        AvatarIdentityFacts? merged = current is null || overwrite
            ? derived
            : AvatarIdentityFacts.Normalize(
                current.Colour ?? derived.Colour, current.MakeModel ?? derived.MakeModel,
                current.Marks ?? derived.Marks, current.Scale ?? derived.Scale);
        avatar.IdentityFacts = merged?.ToJson();
        avatar.UpdatedAt = DateTime.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConflictCode("AVATAR_CHANGED", "The avatar was changed meanwhile. Reload it and try again.");
        }

        return Ok((await ToResponsesAsync(projectId, [avatar], ct))[0]);
    }
}
