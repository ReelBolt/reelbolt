using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>A workspace's storage: bytes used and its plan's limit (null = unlimited).</summary>
public sealed record StorageUsage(long UsedBytes, long? LimitBytes);

/// <summary>One project's share of a workspace's storage.</summary>
public sealed record ProjectStorageUsage(Guid ProjectId, string Name, long UsedBytes);

/// <summary>
/// The one definition of "how much storage a workspace uses" (docs/design/local-folder-sync-and-storage.md
/// Part 2): the sum of <c>project_files.size_bytes</c> over the workspace's projects — uploads, files
/// synced from every source, agent files and workflow outputs alike — plus each picture's private copy of a
/// deleted project photo (decision D10, <see cref="EditImageLayer.CopySizeBytes"/>), once per (picture, copy),
/// because that copy is the user's photo living on, plus each of the organization's brand-library assets
/// (<c>design_system_library_assets.size_bytes</c> through their owning library). Other image-editor working
/// layers and render scratch have no <c>project_files</c> row and are not counted. Used by
/// <see cref="PlanGateService"/> (the quota), the billing summary and <c>GET /api/v1/storage/usage</c>,
/// so the number a person sees is the number the quota enforces.
/// <para>
/// Every query names the organization explicitly and bypasses the ambient tenant filter, so the answer
/// is the same in a dashboard request, a device request and a background sync.
/// </para>
/// </summary>
public interface IStorageUsageService
{
    /// <summary>Bytes the workspace's project files take.</summary>
    Task<long> GetUsedBytesAsync(Guid organizationId, CancellationToken ct);

    /// <summary>Bytes used plus the plan's limit.</summary>
    Task<StorageUsage> GetUsageAsync(Guid organizationId, CancellationToken ct);

    /// <summary>The <paramref name="take"/> projects using the most storage, largest first.</summary>
    Task<IReadOnlyList<ProjectStorageUsage>> GetTopProjectsAsync(Guid organizationId, int take, CancellationToken ct);
}

/// <inheritdoc />
public sealed class StorageUsageService(InferenceApiDbContext db, IOrganizationEntitlements entitlements) : IStorageUsageService
{
    public const long BytesPerGb = 1024L * 1024L * 1024L;

    /// <summary>The plan's limit in bytes, or null when the plan has none.</summary>
    public static long? LimitBytesOf(OrganizationEntitlementSet set) =>
        set.StorageGb == OrganizationEntitlementSet.UnlimitedInt ? null : set.StorageGb * BytesPerGb;

    private static readonly string[] TenantFilter = [InferenceApiDbContext.TenantFilterName];

    public async Task<long> GetUsedBytesAsync(Guid organizationId, CancellationToken ct)
    {
        IQueryable<Project> projects = db.Projects.IgnoreQueryFilters(TenantFilter);
        long files = await db.ProjectFiles
            .IgnoreQueryFilters(TenantFilter)
            // Built-in library sounds copied into a project are free of quota.
            .Where(f => f.SourceKind != ReelBolt.Shared.Billing.ProjectFileQuota.SfxLibrarySourceKind)
            .Where(f => projects.Any(p => p.Id == f.ProjectId && p.OrganizationId == organizationId))
            .SumAsync(f => (long?)f.SizeBytes, ct) ?? 0;
        long copies = (await PictureCopyBytesByProjectAsync(organizationId, ct)).Values.Sum();

        // A library asset is storage the organization owns just like a project file, so it counts against the
        // same quota. The asset table carries no organization column; the filtered library row is the boundary.
        long libraryAssets = await db.DesignSystemLibraryAssets
            .Join(
                db.DesignSystemLibraries
                    .IgnoreQueryFilters(TenantFilter)
                    .Where(l => l.OrganizationId == organizationId),
                asset => asset.LibraryId,
                library => library.Id,
                (asset, library) => asset.SizeBytes)
            .SumAsync(size => (long?)size, ct) ?? 0;

        // Project versioning: the protected copies and stored documents of the org's projects.
        IQueryable<Guid> orgProjectIds = projects.Where(p => p.OrganizationId == organizationId).Select(p => p.Id);
        long historyBlobs = await db.ProjectHistoryBlobs
            .Where(b => orgProjectIds.Contains(b.ProjectId))
            .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0;
        long historyDocuments = await db.ProjectHistoryDocuments
            .Where(d => orgProjectIds.Contains(d.ProjectId))
            .SumAsync(d => (long?)d.SizeBytes, ct) ?? 0;

        // The workspace avatar library (docs/design/avatar-objects.md): the library's own copies of an item's media are
        // storage the organization owns, so they count like a brand-library asset. The reference table carries no
        // organization column; the filtered item row is the boundary.
        long avatarLibrary = await db.AvatarLibraryReferences
            .Join(
                db.AvatarLibraryItems
                    .IgnoreQueryFilters(TenantFilter)
                    .Where(i => i.OrganizationId == organizationId),
                reference => reference.LibraryItemId,
                item => item.Id,
                (reference, item) => reference.SizeBytes)
            .SumAsync(size => (long?)size, ct) ?? 0;

        return files + copies + libraryAssets + avatarLibrary + historyBlobs + historyDocuments;
    }

    /// <summary>
    /// The bytes of the pictures' private copies (D10) per project: one copy per (live picture, key), however
    /// many of its live layers share it.
    /// </summary>
    private async Task<Dictionary<Guid, long>> PictureCopyBytesByProjectAsync(Guid organizationId, CancellationToken ct)
    {
        IQueryable<Project> projects = db.Projects.IgnoreQueryFilters(TenantFilter).Where(p => p.OrganizationId == organizationId);
        var copies = await db.EditImageLayers
            .IgnoreQueryFilters(TenantFilter)
            .Where(l => l.CopySizeBytes != null)
            .Join(
                db.EditImageDocuments.IgnoreQueryFilters(TenantFilter).Where(d => projects.Any(p => p.Id == d.ProjectId)),
                l => l.DocumentId,
                d => d.Id,
                (l, d) => new { d.ProjectId, l.DocumentId, l.StorageKey, Bytes = l.CopySizeBytes!.Value })
            .Distinct()
            .ToListAsync(ct);

        return copies
            .GroupBy(c => c.ProjectId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Bytes));
    }

    public async Task<StorageUsage> GetUsageAsync(Guid organizationId, CancellationToken ct)
    {
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        return new StorageUsage(await GetUsedBytesAsync(organizationId, ct), LimitBytesOf(set));
    }

    public async Task<IReadOnlyList<ProjectStorageUsage>> GetTopProjectsAsync(Guid organizationId, int take, CancellationToken ct)
    {
        var top = await db.ProjectFiles
            .IgnoreQueryFilters(TenantFilter)
            .Where(f => f.SourceKind != SfxLibraryProjectAdder.SourceKind)
            .Join(
                db.Projects.IgnoreQueryFilters(TenantFilter).Where(p => p.OrganizationId == organizationId),
                f => f.ProjectId,
                p => p.Id,
                (f, p) => new { p.Id, p.Name, f.SizeBytes })
            .GroupBy(x => new { x.Id, x.Name })
            .Select(g => new { g.Key.Id, g.Key.Name, UsedBytes = g.Sum(x => x.SizeBytes) })
            .ToListAsync(ct);

        // Pictures' private copies (D10) count toward their project too, so the ranking is done after adding them.
        Dictionary<Guid, long> copies = await PictureCopyBytesByProjectAsync(organizationId, ct);
        List<ProjectStorageUsage> usage = [.. top.Select(x => new ProjectStorageUsage(x.Id, x.Name, x.UsedBytes + copies.GetValueOrDefault(x.Id)))];
        HashSet<Guid> listed = [.. usage.Select(u => u.ProjectId)];
        List<Guid> copyOnly = [.. copies.Keys.Where(id => !listed.Contains(id))];
        if (copyOnly.Count > 0)
        {
            usage.AddRange(await db.Projects.IgnoreQueryFilters(TenantFilter)
                .Where(p => copyOnly.Contains(p.Id))
                .Select(p => new ProjectStorageUsage(p.Id, p.Name, 0))
                .ToListAsync(ct));
            usage = [.. usage.Select(u => copyOnly.Contains(u.ProjectId) ? u with { UsedBytes = copies[u.ProjectId] } : u)];
        }

        return usage
            .OrderByDescending(x => x.UsedBytes)
            .ThenBy(x => x.Name)
            .Take(Math.Max(0, take))
            .ToList();
    }

    /// <summary>A size the way the dashboard writes one: binary units, at most one decimal ("4.2 GB").</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} bytes"
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
