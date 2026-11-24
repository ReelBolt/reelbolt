using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// A project's links to cloud-drive folders (docs/design/cloud-drive-sync.md "HTTP API"): at most one
/// per provider. Every action resolves the project through <see cref="IProjectAccessService"/> first —
/// <see cref="ProjectPermission.View"/> to read, <see cref="ProjectPermission.Edit"/> for everything
/// else — and a link is only ever looked up within that project, so another workspace's project or link
/// is a 404.
///
/// <para>
/// Nothing here syncs inline. Linking, changing the selection and "Sync now" only queue a sync on
/// <see cref="CloudSyncQueue"/> and answer at once; the dashboard watches <c>syncing</c> and reads the
/// outcome off the link. Unlinking removes the link and nothing else: the files it wrote stay, as when a
/// GitHub repository is unlinked.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/cloud-links")]
[Authorize]
public class ProjectCloudLinksController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICloudStorageProviderRegistry _providers;
    private readonly ICloudStorageConnectionService _connections;
    private readonly CloudSyncQueue _queue;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ProjectCloudLinksController> _logger;
    private readonly Services.Telemetry.IProductEventEmitter? _productEvents;

    public ProjectCloudLinksController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        ICloudStorageProviderRegistry providers,
        ICloudStorageConnectionService connections,
        CloudSyncQueue queue,
        ICurrentUser currentUser,
        ILogger<ProjectCloudLinksController> logger,
        Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _db = db;
        _projectAccess = projectAccess;
        _providers = providers;
        _connections = connections;
        _queue = queue;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>The project's links, one per provider at most.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectCloudLinkResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<ProjectCloudLink> links = await _db.ProjectCloudLinks
            .Where(l => l.ProjectId == projectId)
            .OrderBy(l => l.Provider)
            .ToListAsync(ct);

        List<Guid> connectionIds = links.Where(l => l.ConnectionId != null).Select(l => l.ConnectionId!.Value).Distinct().ToList();
        Dictionary<Guid, string?> emails = await _db.CloudStorageConnections
            .Where(c => connectionIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.AccountEmail ?? c.AccountDisplayName, ct);

        return Ok(links.Select(l => ToResponse(l, l.ConnectionId is { } id && emails.TryGetValue(id, out string? email) ? email : null)).ToList());
    }

    /// <summary>
    /// Links a folder of one of the caller's own connections and queues its first sync (<c>201</c>).
    /// <c>409 already_linked</c> when the project already mirrors a folder of that provider;
    /// <c>404 connection_not_found</c> / <c>folder_not_found</c>; <c>400 invalid_include_paths</c> / <c>invalid_id</c>.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProjectCloudLinkResponse>> Create(
        Guid projectId,
        [FromBody] CreateProjectCloudLinkRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (!CloudIds.TryNormalize(request.DriveId, out string? driveId) || !CloudIds.TryNormalize(request.FolderId, out string? folderId))
            return BadRequest(new { error = CloudIds.InvalidError, message = "The drive or folder is not valid." });

        if (!GitHubIncludePaths.TryNormalize(request.IncludePaths, out List<string> includePaths, out string? reason))
            return BadRequest(new { error = GitHubIncludePaths.InvalidError, message = reason });

        // The caller's own connection, in the workspace the project belongs to (which is the active one).
        CloudStorageConnection? connection = await _connections.FindOwnedAsync(
            request.ConnectionId, _currentUser.UserId, project.OrganizationId, ct);
        if (connection is null)
            return NotFound(new { error = "connection_not_found", message = "That connected account could not be found." });

        if (_providers.Get(connection.Provider) is not { IsConfigured: true } provider)
            return CloudStorageHttp.NotConfigured(this, connection.Provider);

        if (await _db.ProjectCloudLinks.AnyAsync(l => l.ProjectId == projectId && l.Provider == connection.Provider, ct))
            return AlreadyLinked(connection.Provider);

        CloudFolderInfo folder;
        try
        {
            folder = await _connections.CallAsync(
                connection, token => provider.GetFolderAsync(token, new CloudLocation(driveId, folderId), ct), ct);
        }
        catch (CloudStorageApiException ex)
        {
            return CloudStorageHttp.Failure(this, _logger, connection.Provider, ex);
        }

        DateTime now = DateTime.UtcNow;
        ProjectCloudLink link = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = project.OrganizationId,
            Provider = connection.Provider,
            ConnectionId = connection.Id,
            DriveId = driveId,
            FolderId = folder.FolderId ?? folderId,
            FolderDisplayPath = Truncate(folder.DisplayPath, 2000),
            IncludePathsJson = GitHubIncludePaths.Serialize(includePaths),
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.ProjectCloudLinks.Add(link);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A second tab linked the same provider a moment earlier; the unique index caught it.
            _db.Entry(link).State = EntityState.Detached;
            return AlreadyLinked(connection.Provider);
        }

        _queue.Enqueue(link.Id, force: true, manual: false, out _);
        _logger.LogInformation("Linked {Provider} folder to project {ProjectId} (link {LinkId}); first sync queued", link.Provider, projectId, link.Id);

        // product-analytics: the person connected a source (activation.source_connected, once per user).
        // Only after the link is saved, and never from a sync. Best effort.
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                _currentUser.UserId, project.OrganizationId, Services.Telemetry.ProductEventNames.SourceConnected, ct);
        }

        return Created($"/api/v1/projects/{projectId}/cloud-links/{link.Id}", ToResponse(link, connection.AccountEmail ?? connection.AccountDisplayName));
    }

    /// <summary>
    /// Changes the selection. Queues a full re-sync only when the normalized selection actually changed,
    /// so re-sending the same choice never forces a sync.
    /// </summary>
    [HttpPatch("{linkId:guid}")]
    public async Task<ActionResult<ProjectCloudLinkResponse>> Update(
        Guid projectId,
        Guid linkId,
        [FromBody] UpdateProjectCloudLinkRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectCloudLink? link = await _db.ProjectCloudLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.ProjectId == projectId, ct);
        if (link is null) return LinkNotFound();

        if (request.IncludePaths is not null)
        {
            if (!GitHubIncludePaths.TryNormalize(request.IncludePaths, out List<string> includePaths, out string? reason))
                return BadRequest(new { error = GitHubIncludePaths.InvalidError, message = reason });

            string? serialized = GitHubIncludePaths.Serialize(includePaths);
            if (!string.Equals(serialized, link.IncludePathsJson, StringComparison.Ordinal))
            {
                link.IncludePathsJson = serialized;
                link.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                _queue.Enqueue(link.Id, force: true, manual: false, out _);
            }
        }

        return Ok(ToResponse(link, await AccountLabelAsync(link, ct)));
    }

    /// <summary>Removes the link. The files it synced stay in the project.</summary>
    [HttpDelete("{linkId:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid linkId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ProjectCloudLink? link = await _db.ProjectCloudLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.ProjectId == projectId, ct);
        if (link is null) return LinkNotFound();

        _db.ProjectCloudLinks.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Unlinked {Provider} folder from project {ProjectId}", link.Provider, projectId);
        return NoContent();
    }

    /// <summary>
    /// "Sync now": queues a sync and answers <c>202</c> at once — <c>queued</c>, or <c>already_syncing</c>
    /// when one is queued or running. <c>429 sync_too_soon</c> (with <c>Retry-After</c>) within
    /// <see cref="CloudSyncQueue.ManualCooldown"/> of the previous "Sync now" of the link.
    /// </summary>
    [HttpPost("{linkId:guid}/sync")]
    public async Task<ActionResult<CloudSyncQueuedResponse>> Sync(Guid projectId, Guid linkId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        bool exists = await _db.ProjectCloudLinks.AnyAsync(l => l.Id == linkId && l.ProjectId == projectId, ct);
        if (!exists) return LinkNotFound();

        switch (_queue.Enqueue(linkId, force: true, manual: true, out TimeSpan retryAfter))
        {
            case CloudSyncEnqueueResult.TooSoon:
                int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    error = "sync_too_soon",
                    message = "A sync was started a moment ago. Wait a few seconds, then try again.",
                    retryAfterSeconds = seconds
                });
            case CloudSyncEnqueueResult.AlreadySyncing:
                return Accepted(new CloudSyncQueuedResponse("already_syncing"));
            default:
                return Accepted(new CloudSyncQueuedResponse("queued"));
        }
    }

    private async Task<string?> AccountLabelAsync(ProjectCloudLink link, CancellationToken ct)
    {
        if (link.ConnectionId is not { } connectionId) return null;
        return await _db.CloudStorageConnections
            .Where(c => c.Id == connectionId)
            .Select(c => c.AccountEmail ?? c.AccountDisplayName)
            .FirstOrDefaultAsync(ct);
    }

    private ProjectCloudLinkResponse ToResponse(ProjectCloudLink link, string? accountEmail)
    {
        IReadOnlyList<string>? includePaths;
        try
        {
            IReadOnlyList<string> parsed = GitHubIncludePaths.Parse(link.IncludePathsJson);
            includePaths = parsed.Count == 0 ? null : parsed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            // Shown as "everything" would be a lie; an empty choice makes the dashboard ask again.
            _logger.LogError(ex, "The stored file selection of cloud link {LinkId} is unreadable", link.Id);
            includePaths = [];
        }

        return new ProjectCloudLinkResponse(
            link.Id,
            CloudStorageProviders.Slug(link.Provider),
            link.ConnectionId,
            accountEmail,
            link.DriveId,
            link.FolderId,
            link.FolderDisplayPath,
            includePaths,
            link.LastSyncedAt,
            link.LastSyncedFileCount,
            link.LastError,
            _queue.IsSyncing(link.Id));
    }

    private ConflictObjectResult AlreadyLinked(CloudStorageProviderKind kind) =>
        Conflict(new
        {
            error = "already_linked",
            message = $"This project already syncs a {CloudStorageProviders.DisplayName(kind)} folder. Unlink it first to choose another."
        });

    private NotFoundObjectResult LinkNotFound() =>
        NotFound(new { error = "link_not_found", message = "That linked folder could not be found." });

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length > max ? value[..max] : value;
}
