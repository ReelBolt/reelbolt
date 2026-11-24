using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary><c>GET /api/v1/storage/usage</c>: one project's share of the workspace's storage.</summary>
public sealed record ProjectStorageUsageResponse(Guid ProjectId, string Name, long UsedBytes);

/// <summary>The workspace's storage: bytes used, the plan's limit (null = unlimited) and the largest projects.</summary>
public sealed record StorageUsageResponse(
    long UsedBytes, long? LimitBytes, IReadOnlyList<ProjectStorageUsageResponse> ByProject, VideoSourceLimitsResponse? VideoSourceLimits = null,
    ZipUnpackLimitsResponse? ZipUnpackLimits = null);

/// <summary>The biggest zip ReelBolt will unpack, in whole MB (the size of the file as uploaded, never bytes).</summary>
public sealed record ZipUnpackLimitsResponse(long MaxMb)
{
    public static ZipUnpackLimitsResponse Current { get; } = new(UnpackLimits.MaxArchiveBytes / (1024 * 1024));
}

/// <summary>The biggest video the platform can edit, in the whole units a sentence uses (never bytes).</summary>
public sealed record VideoSourceLimitsResponse(long MaxGb, int MaxMinutes)
{
    public static VideoSourceLimitsResponse From(VideoSourceLimits limits) => new(
        long.Parse(VideoSourceLimits.FormatGigabytes(limits.MaxBytes), System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(VideoSourceLimits.FormatMinutes(limits.MaxSeconds), System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// How much storage the caller's active workspace uses (docs/design/local-folder-sync-and-storage.md
/// Part 2), for the usage bar on the Billing and Settings pages and the warning banner on project pages.
/// Any member of the workspace may read it — unlike the billing summary it is not a billing document, and
/// a person who hits the quota needs to see why. Works with billing off too (the limit is then null).
/// </summary>
[ApiController]
[Route("api/v1/storage")]
[Authorize]
public sealed class StorageUsageController(ICurrentUser currentUser, IStorageUsageService storageUsage, IConfiguration? configuration = null) : ControllerBase
{
    /// <summary>How many projects <see cref="Usage"/> lists, largest first.</summary>
    public const int TopProjects = 20;

    [HttpGet("usage")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<StorageUsageResponse>> Usage(CancellationToken ct)
    {
        if (currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();

        StorageUsage usage = await storageUsage.GetUsageAsync(orgId, ct);
        IReadOnlyList<ProjectStorageUsage> top = await storageUsage.GetTopProjectsAsync(orgId, TopProjects, ct);
        return Ok(new StorageUsageResponse(
            usage.UsedBytes,
            usage.LimitBytes,
            top.Select(p => new ProjectStorageUsageResponse(p.ProjectId, p.Name, p.UsedBytes)).ToList(),
            VideoSourceLimitsResponse.From(configuration is null ? VideoSourceLimits.Default : VideoSourceLimits.From(configuration)),
            ZipUnpackLimitsResponse.Current));
    }
}
