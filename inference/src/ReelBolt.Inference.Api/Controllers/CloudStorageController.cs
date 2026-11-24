using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Connected cloud-drive accounts: the providers on offer, connecting (OAuth), listing, disconnecting,
/// and browsing a connection's drives and folders for the folder picker (docs/design/cloud-drive-sync.md
/// "HTTP API").
///
/// <para>
/// <b>Owner-scoped.</b> A connection is the caller's own, in the active workspace: every lookup filters
/// on the caller's user id AND active organization, and anything else — another person's connection,
/// the same person's connection in another workspace, an id that does not exist — is the same
/// <c>404 connection_not_found</c>, so this API is never an existence oracle for somebody else's account.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/cloud-storage")]
[Authorize]
public class CloudStorageController : ControllerBase
{
    /// <summary>Most items one folder listing returns to the picker.</summary>
    public const int MaxFolderItems = 500;

    /// <summary>Where the browser lands after connecting when the start request named no valid page.</summary>
    public const string DefaultReturnPath = "/app/settings";

    private readonly InferenceApiDbContext _db;
    private readonly ICloudStorageProviderRegistry _providers;
    private readonly ICloudStorageConnectionService _connections;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<CloudStorageController> _logger;

    public CloudStorageController(
        InferenceApiDbContext db,
        ICloudStorageProviderRegistry providers,
        ICloudStorageConnectionService connections,
        ICurrentUser currentUser,
        ILogger<CloudStorageController> logger)
    {
        _db = db;
        _providers = providers;
        _connections = connections;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>All three providers, and whether this deployment has credentials for each.</summary>
    [HttpGet("providers")]
    public ActionResult<IReadOnlyList<CloudStorageProviderResponse>> Providers() =>
        Ok(CloudStorageProviders.All
            .Select(kind => new CloudStorageProviderResponse(
                CloudStorageProviders.Slug(kind),
                CloudStorageProviders.DisplayName(kind),
                _providers.Get(kind) is { IsConfigured: true }))
            .ToList());

    /// <summary>The caller's connections in the active workspace, newest first.</summary>
    [HttpGet("connections")]
    public async Task<ActionResult<IReadOnlyList<CloudStorageConnectionResponse>>> Connections(CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId) return Ok(Array.Empty<CloudStorageConnectionResponse>());
        Guid userId = _currentUser.UserId;

        List<CloudStorageConnection> rows = await _db.CloudStorageConnections
            .Where(c => c.UserId == userId && c.OrganizationId == organizationId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        return Ok(rows.Select(c => new CloudStorageConnectionResponse(
            c.Id,
            CloudStorageProviders.Slug(c.Provider),
            c.AccountEmail,
            c.AccountDisplayName,
            c.CreatedAt,
            c.LastError)).ToList());
    }

    /// <summary>Disconnects (best-effort revoke, then delete). Links that used it stay, without a connection; their files stay.</summary>
    [HttpDelete("connections/{id:guid}")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId) return ConnectionNotFound();
        bool removed = await _connections.DisconnectAsync(id, _currentUser.UserId, organizationId, ct);
        return removed ? NoContent() : ConnectionNotFound();
    }

    /// <summary>
    /// Begins connecting an account: answers the URL of the provider's consent screen, which the dashboard
    /// navigates to. <c>503 provider_not_configured</c> without the vendor credentials,
    /// <c>503 public_base_url_missing</c> without <c>PUBLIC_BASE_URL</c> (no redirect URI can be built).
    /// </summary>
    [HttpPost("{provider}/oauth/start")]
    public async Task<ActionResult<CloudOAuthStartResponse>> Start(string provider, [FromBody] CloudOAuthStartRequest? request, CancellationToken ct)
    {
        if (!CloudStorageProviders.TryParseSlug(provider, out CloudStorageProviderKind kind)) return NotFound(new { error = "provider_not_found" });
        if (_currentUser.OrganizationId is not { } organizationId)
            return NotFound(new { error = "no_workspace", message = "Choose a workspace before connecting an account." });

        CloudOAuthStart start = await _connections.StartAsync(kind, _currentUser.UserId, organizationId, request?.ReturnTo, ct);
        return start.Error switch
        {
            null => Ok(new CloudOAuthStartResponse(start.AuthorizeUrl!)),
            CloudOAuthStart.PublicBaseUrlMissing => StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = start.Error,
                message = "Connecting accounts is not finished on this server: its public address is not set. Ask your administrator."
            }),
            _ => NotConfigured(kind)
        };
    }

    /// <summary>
    /// Where the provider sends the browser back. Anonymous in the routing sense — it is a top-level
    /// navigation from the vendor — but it only completes when the signed-in person making it is the one
    /// who started the flow (the session cookie travels with that navigation). Every answer is a redirect
    /// to a same-origin <c>/app/…</c> path taken from our own ticket (never from this request), with
    /// <c>?cloud=connected&amp;provider=…</c> or <c>?cloud=error&amp;provider=…&amp;reason=…</c>.
    /// </summary>
    [HttpGet("{provider}/oauth/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(
        string provider,
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        if (!CloudStorageProviders.TryParseSlug(provider, out CloudStorageProviderKind kind)) return NotFound(new { error = "provider_not_found" });

        Guid? caller = null;
        if (_currentUser.IsAuthenticated)
        {
            try { caller = _currentUser.UserId; }
            catch (InvalidOperationException) { caller = null; }
        }

        CloudOAuthCompletion completion;
        try
        {
            completion = await _connections.CompleteAsync(kind, state, code, error, caller, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Land the person on a page that can explain itself, not on a raw 500 of an API route.
            _logger.LogError(ex, "The {Provider} account-connection callback failed unexpectedly", kind);
            completion = new CloudOAuthCompletion(CloudOAuthOutcome.ExchangeFailed, null);
        }

        if (!completion.Succeeded)
            _logger.LogInformation("A {Provider} account connection finished as {Outcome}", kind, completion.Code);

        string target = completion.ReturnPath ?? DefaultReturnPath;
        string separator = target.Contains('?') ? "&" : "?";
        string slug = CloudStorageProviders.Slug(kind);
        string outcome = completion.Succeeded
            ? $"cloud=connected&provider={slug}"
            : $"cloud=error&provider={slug}&reason={Uri.EscapeDataString(completion.Code)}";
        return Redirect(target + separator + outcome);
    }

    /// <summary>The spaces a connection can pick a folder from, its own space first.</summary>
    [HttpGet("connections/{id:guid}/drives")]
    public async Task<ActionResult<IReadOnlyList<CloudDriveResponse>>> Drives(Guid id, CancellationToken ct)
    {
        (CloudStorageConnection? connection, ICloudStorageProvider? provider, ActionResult? refusal) = await ResolveAsync(id, ct);
        if (refusal is not null) return refusal;

        try
        {
            IReadOnlyList<CloudDrive> drives = await _connections.CallAsync(
                connection!, token => provider!.ListDrivesAsync(token, ct), ct);
            return Ok(drives.Select(d => new CloudDriveResponse(d.DriveId, d.Name, CloudStorageHttp.DriveKind(d.Kind))).ToList());
        }
        catch (CloudStorageApiException ex)
        {
            return CloudStorageHttp.Failure(this, _logger, connection!.Provider, ex);
        }
    }

    /// <summary>
    /// One level of a folder for the picker: <paramref name="driveId"/> absent is the account's own space,
    /// <paramref name="folderId"/> absent that drive's root. At most <see cref="MaxFolderItems"/> items.
    /// </summary>
    [HttpGet("connections/{id:guid}/folders")]
    public async Task<ActionResult<CloudFolderListResponse>> Folders(
        Guid id,
        [FromQuery] string? driveId,
        [FromQuery] string? folderId,
        CancellationToken ct)
    {
        if (!CloudIds.TryNormalize(driveId, out string? drive) || !CloudIds.TryNormalize(folderId, out string? folder))
            return BadRequest(new { error = CloudIds.InvalidError, message = "The drive or folder is not valid." });

        (CloudStorageConnection? connection, ICloudStorageProvider? provider, ActionResult? refusal) = await ResolveAsync(id, ct);
        if (refusal is not null) return refusal;

        try
        {
            CloudFolderPage page = await _connections.CallAsync(
                connection!, token => provider!.ListFolderAsync(token, new CloudLocation(drive, folder), MaxFolderItems, ct), ct);
            return Ok(new CloudFolderListResponse(
                new CloudFolderResponse(page.Folder.FolderId, page.Folder.Name, page.Folder.DisplayPath),
                page.Items.Take(MaxFolderItems)
                    .Select(i => new CloudItemResponse(i.Id, i.Name, i.IsFolder, i.Size, i.ModifiedAt))
                    .ToList(),
                page.Truncated || page.Items.Count > MaxFolderItems));
        }
        catch (CloudStorageApiException ex)
        {
            return CloudStorageHttp.Failure(this, _logger, connection!.Provider, ex);
        }
    }

    private async Task<(CloudStorageConnection?, ICloudStorageProvider?, ActionResult?)> ResolveAsync(Guid id, CancellationToken ct)
    {
        if (_currentUser.OrganizationId is not { } organizationId) return (null, null, ConnectionNotFound());

        CloudStorageConnection? connection = await _connections.FindOwnedAsync(id, _currentUser.UserId, organizationId, ct);
        if (connection is null) return (null, null, ConnectionNotFound());

        ICloudStorageProvider? provider = _providers.Get(connection.Provider);
        if (provider is not { IsConfigured: true }) return (null, null, NotConfigured(connection.Provider));

        return (connection, provider, null);
    }

    private NotFoundObjectResult ConnectionNotFound() =>
        NotFound(new { error = "connection_not_found", message = "That connected account could not be found." });

    private ObjectResult NotConfigured(CloudStorageProviderKind kind) => CloudStorageHttp.NotConfigured(this, kind);
}

/// <summary>How the cloud-storage controllers answer a provider failure. Shared so both say the same thing.</summary>
internal static class CloudStorageHttp
{
    /// <summary>The status of a provider failure (<c>provider_error</c>): 424, never 502, for the reason <see cref="ProjectGitHubController"/> gives.</summary>
    public const int ProviderFailureStatus = StatusCodes.Status424FailedDependency;

    public static ObjectResult NotConfigured(ControllerBase controller, CloudStorageProviderKind kind) =>
        controller.StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = CloudOAuthStart.ProviderNotConfigured,
            message = $"{CloudStorageProviders.DisplayName(kind)} is not set up on this server. Ask your administrator."
        });

    /// <summary>
    /// <c>409 reconnect_required</c> (the account's access is gone), <c>404 folder_not_found</c> (gone or not
    /// visible — the same answer, so it is not an existence oracle), <c>503 provider_not_configured</c>, and
    /// otherwise <c>424 provider_error</c> with a plain sentence.
    /// </summary>
    public static ObjectResult Failure(ControllerBase controller, ILogger logger, CloudStorageProviderKind kind, CloudStorageApiException ex)
    {
        string name = CloudStorageProviders.DisplayName(kind);
        switch (ex.Kind)
        {
            case CloudStorageErrorKind.Unauthorized:
                return controller.Conflict(new
                {
                    error = "reconnect_required",
                    message = $"{name} no longer accepts ReelBolt's access to this account. Reconnect it in Settings."
                });
            case CloudStorageErrorKind.NotFound or CloudStorageErrorKind.Forbidden:
                return controller.NotFound(new
                {
                    error = "folder_not_found",
                    message = $"That folder could not be found on {name}, or this account cannot open it."
                });
            case CloudStorageErrorKind.NotConfigured:
                return NotConfigured(controller, kind);
            default:
                logger.LogWarning("{Provider} failed a browse request: {Kind} {Message}", kind, ex.Kind, ex.Message);
                return controller.StatusCode(ProviderFailureStatus, new
                {
                    error = "provider_error",
                    message = CloudSyncService.InPlainWords(name, ex.Kind)
                });
        }
    }

    public static string DriveKind(CloudDriveKind kind) => kind switch
    {
        CloudDriveKind.SharedDrive => "sharedDrive",
        CloudDriveKind.SharePointLibrary => "sharePointLibrary",
        CloudDriveKind.TeamSpace => "teamSpace",
        _ => "personal"
    };
}
