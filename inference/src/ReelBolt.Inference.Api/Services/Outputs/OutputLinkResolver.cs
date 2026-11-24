using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>The object a verified link resolves to, server-side only: the key never leaves the API.</summary>
public sealed record ResolvedOutputObject(Guid ProjectId, string StorageKey, string ContentType, string FileName);

/// <summary>
/// Turns verified link claims into the object to stream, after re-checking authority from the
/// database — the link itself proves only that it was minted, not that it is still deserved.
/// </summary>
public interface IOutputLinkResolver
{
    /// <summary>The object to stream, or <c>null</c> for any reason at all (the caller answers 404 without saying which).</summary>
    Task<ResolvedOutputObject?> ResolveAsync(OutputLinkClaims claims, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The route is anonymous, so there is no request tenant: every query names the project explicitly
/// and ignores only the tenant filter (soft-delete filters still apply). Authority is the project
/// role matrix (<see cref="ProjectAccessService.Allowed"/>) over the user's LIVE membership in the
/// project's organization — a user removed from the organization, demoted to Billing, deleted, or
/// whose tokens were invalidated after the link was minted gets nothing.
/// </para>
/// <para>
/// Key scope: a step result must be one of the project's addressable outputs
/// (<see cref="ProjectOutputKeys.IsAddressableOutput"/>: <c>outputFiles/</c> or <c>agentFiles/generated/</c>), a project file and a
/// derivative under <c>projects/{p}/</c>, and a filmstrip sheet is derived from its row exactly as
/// <c>MediaController</c> derives it.
/// </para>
/// </remarks>
public sealed class OutputLinkResolver(InferenceApiDbContext db, IFileStorageService storage) : IOutputLinkResolver
{
    private static readonly string[] Tenant = [InferenceApiDbContext.TenantFilterName];

    public async Task<ResolvedOutputObject?> ResolveAsync(OutputLinkClaims claims, CancellationToken ct)
    {
        OutputLinkTarget target = claims.Target;

        ApplicationUser? user = await db.ApplicationUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == claims.UserId, ct);
        if (user is null || user.DeletionRequestedAt is not null) return null;
        if (user.TokensInvalidatedAt is { } invalidated &&
            new DateTimeOffset(DateTime.SpecifyKind(invalidated, DateTimeKind.Utc)) >= claims.IssuedAt)
            return null;

        Project? project = await db.Projects.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == target.ProjectId, ct);
        if (project is null) return null;

        // A soft-deleted organization keeps its memberships (the Go API only stamps DeletedAt), so the
        // membership check alone would keep serving its links. A missing organization row is refused too.
        bool organizationLive = await db.Organizations.AsNoTracking()
            .AnyAsync(o => o.Id == project.OrganizationId && o.DeletedAt == null, ct);
        if (!organizationLive) return null;

        OrganizationMembership? membership = await db.OrganizationMemberships.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == project.OrganizationId && m.UserId == user.Id, ct);
        if (membership is null || !ProjectAccessService.Allowed(membership.Role, ProjectPermission.View, project.OwnerId == user.Id))
            return null;

        string projectPrefix = $"projects/{project.Id}/";
        return target.Kind switch
        {
            OutputLinkKind.StepResult => await ResolveRenderAsync(project.Id, target.Id, ct),
            OutputLinkKind.ProjectFile => await ResolveFileAsync(project.Id, target.Id, projectPrefix, ct),
            OutputLinkKind.Derivative => await ResolveDerivativeAsync(project.Id, target.Id, target.SheetIndex, projectPrefix, ct),
            _ => null
        };
    }

    private async Task<ResolvedOutputObject?> ResolveRenderAsync(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        string? key = await db.WorkflowStepResults.IgnoreQueryFilters(Tenant).AsNoTracking()
            .Where(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId)
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);
        if (!ProjectOutputKeys.IsAddressableOutput(projectId, key)) return null;
        if (await OutputGate.IsResultHeldAsync(db, stepResultId, ct)) return null;

        string fileName = Path.GetFileName(key);
        return new ResolvedOutputObject(projectId, key, OutputMediaTypes.FromFileName(fileName) ?? "application/octet-stream", fileName);
    }

    private async Task<ResolvedOutputObject?> ResolveFileAsync(Guid projectId, Guid fileId, string projectPrefix, CancellationToken ct)
    {
        ProjectFile? file = await db.ProjectFiles.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId
                                      && f.CsamStatus != CsamScanStatus.Quarantined
                                      && f.ModerationStatus != FileModerationStatus.Quarantined, ct);
        if (file is null || string.IsNullOrEmpty(file.StorageKey) || !file.StorageKey.StartsWith(projectPrefix, StringComparison.Ordinal))
            return null;

        if (await OutputGate.IsFileHeldAsync(db, file, ct)) return null;
        string fileName = string.IsNullOrWhiteSpace(file.OriginalFileName) ? Path.GetFileName(file.StorageKey) : file.OriginalFileName;
        return new ResolvedOutputObject(projectId, file.StorageKey, file.MimeType, fileName);
    }

    private async Task<ResolvedOutputObject?> ResolveDerivativeAsync(
        Guid projectId, Guid derivativeId, int? sheetIndex, string projectPrefix, CancellationToken ct)
    {
        MediaDerivative? row = await db.MediaDerivatives.IgnoreQueryFilters(Tenant).AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == derivativeId && d.ProjectId == projectId && d.DeletedAt == null, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.StorageKey) || !row.StorageKey.StartsWith(projectPrefix, StringComparison.Ordinal))
            return null;

        if (await OutputGate.IsDerivativeHeldAsync(db, row, ct)) return null;
        if (sheetIndex is null)
        {
            string leaf = Path.GetFileName(row.StorageKey);
            string type = string.IsNullOrWhiteSpace(row.MimeType) ? OutputMediaTypes.FromFileName(leaf) ?? "application/octet-stream" : row.MimeType;
            return new ResolvedOutputObject(projectId, row.StorageKey, type, leaf);
        }

        if (sheetIndex < 0) return null;

        if (row.Kind == MediaDerivativeKind.Stills)
        {
            IReadOnlyList<StillsIndex.Still> stills = await StillsIndex.ReadAsync(storage, row, ct);
            if (sheetIndex >= stills.Count) return null;
            string stillKey = stills[sheetIndex.Value].StorageKey;
            string stillLeaf = Path.GetFileName(stillKey);
            return new ResolvedOutputObject(projectId, stillKey, OutputMediaTypes.FromFileName(stillLeaf) ?? "image/jpeg", stillLeaf);
        }

        if (row.Kind != MediaDerivativeKind.Filmstrip) return null;

        FilmstripSheets.Sidecar? sidecar = await FilmstripSheets.TryReadAsync(storage, projectId, row.IndexStorageKey, ct);
        IReadOnlyList<FilmstripSheets.Sheet> sheets = sidecar?.Sheets ?? [];
        if (sheetIndex >= sheets.Count) return null;
        if (!FilmstripSheets.TryDeriveSheetKey(row.StorageKey, sheets[sheetIndex.Value].StorageLeaf, projectPrefix, out string sheetKey))
            return null;

        string sheetLeaf = Path.GetFileName(sheetKey);
        return new ResolvedOutputObject(projectId, sheetKey, OutputMediaTypes.FromFileName(sheetLeaf) ?? "application/octet-stream", sheetLeaf);
    }
}
