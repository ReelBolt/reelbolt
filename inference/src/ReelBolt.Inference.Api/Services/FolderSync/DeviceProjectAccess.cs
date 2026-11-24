using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.FolderSync;

public enum DeviceAccessOutcome
{
    Found,

    /// <summary>Absent, or in another organization than the device's (D5: existence never leaks).</summary>
    NotFound,

    /// <summary>The pairing user is no longer a member of the device's organization, or may not edit.</summary>
    OwnerLacksAccess
}

public sealed record DeviceAccessResult(DeviceAccessOutcome Outcome, Project? Project);

/// <summary>
/// "May this device touch project X": a device acts as its <b>pairing user inside its own
/// organization</b> (docs/design/local-folder-sync-and-storage.md, "Authority"). Every call re-reads
/// the pairing user's membership and role from the database — the user-id-parameterised twin of
/// <see cref="ProjectAccessService"/>, deliberately not built on the request's <c>ICurrentUser</c>,
/// because a device request carries no user token at all. The permission matrix is
/// <see cref="ProjectAccessService.Allowed"/>, so a role change applies to devices exactly as to people.
/// </summary>
public sealed class DeviceProjectAccess(InferenceApiDbContext db)
{
    /// <summary>The pairing user's current role in the device's organization, or null when no longer a member.</summary>
    public async Task<OrganizationRole?> PairingUserRoleAsync(DeviceContext device, CancellationToken ct)
    {
        OrganizationMembership? membership = await db.OrganizationMemberships
            .FirstOrDefaultAsync(m => m.UserId == device.PairingUserId && m.OrganizationId == device.OrganizationId, ct);
        return membership?.Role;
    }

    /// <summary>True when the pairing user is still a member who may edit projects (Owner, Admin, Member).</summary>
    public async Task<bool> PairingUserMayEditAsync(DeviceContext device, CancellationToken ct) =>
        await PairingUserRoleAsync(device, ct) is { } role && ProjectAccessService.Allowed(role, ProjectPermission.Edit, isCreator: false);

    /// <summary>Edit access to one project of the device's organization.</summary>
    public async Task<DeviceAccessResult> CheckProjectAsync(DeviceContext device, Guid projectId, CancellationToken ct)
    {
        // Looked up explicitly in the device's organization (the tenant filter is a second guard): a
        // project of another organization is answered exactly like one that does not exist.
        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null || project.OrganizationId != device.OrganizationId)
            return new DeviceAccessResult(DeviceAccessOutcome.NotFound, null);

        if (await PairingUserRoleAsync(device, ct) is not { } role ||
            !ProjectAccessService.Allowed(role, ProjectPermission.Edit, project.OwnerId == device.PairingUserId))
        {
            return new DeviceAccessResult(DeviceAccessOutcome.OwnerLacksAccess, null);
        }

        return new DeviceAccessResult(DeviceAccessOutcome.Found, project);
    }

    /// <summary>The projects of the device's organization its pairing user can edit; empty when they no longer may.</summary>
    public async Task<IReadOnlyList<Project>> EditableProjectsAsync(DeviceContext device, CancellationToken ct)
    {
        if (!await PairingUserMayEditAsync(device, ct)) return [];
        return await db.Projects
            .Where(p => p.OrganizationId == device.OrganizationId)
            .OrderBy(p => p.Name)
            .ToListAsync(ct);
    }
}
