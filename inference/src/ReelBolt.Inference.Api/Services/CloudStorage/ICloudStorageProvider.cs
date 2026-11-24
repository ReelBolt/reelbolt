using System.Net;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// One file-sharing platform, as the generic sync sees it (docs/design/cloud-drive-sync.md). Every
/// provider-specific detail — OAuth endpoints, scopes, paging, revisions, native-document export,
/// download redirects — lives behind this interface; the connection service, the sync service, the
/// controllers and the dashboard are provider-agnostic.
///
/// <para>
/// <b>Contract every implementation keeps.</b>
/// <list type="bullet">
/// <item>Stateless and thread-safe: registered as a singleton, given its own named <see cref="HttpClient"/>.</item>
/// <item>Talks only to the vendor hosts in <see cref="CloudStorageOptions"/>; never follows a redirect to a
/// non-https URL; percent-escapes every id it puts into a URL.</item>
/// <item>Never logs or puts into an exception message a token, a code, a verifier or a client secret.</item>
/// <item>Maps every non-success answer to <see cref="CloudStorageApiException"/> with the right
/// <see cref="CloudStorageErrorKind"/> (see <see cref="CloudHttp"/>), and an unexpected body shape to
/// <see cref="CloudStorageErrorKind.InvalidResponse"/>.</item>
/// <item><b>A zero-byte file is a valid file.</b> It is listed, downloaded and written like any other. (The
/// GitHub pull once treated an empty blob as a broken response and could never finish; every provider has
/// a test with an empty file.)</item>
/// <item>Paths in <see cref="CloudFileEntry.RelativePath"/> use <c>/</c>, are relative to the listed root, never
/// start or end with <c>/</c>, and contain no <c>.</c>/<c>..</c> segment; an entry whose name would break
/// that (a name containing <c>/</c> or a control character) is left out and counted in
/// <see cref="CloudTreeListing.Unsupported"/>.</item>
/// </list>
/// </para>
/// </summary>
public interface ICloudStorageProvider
{
    CloudStorageProviderKind Kind { get; }

    /// <summary>True when the client id and secret are configured. An unconfigured provider is hidden and every endpoint answers 503.</summary>
    bool IsConfigured { get; }

    // ------------------------------------------------------------------ OAuth (authorization code + PKCE S256)

    /// <summary>The URL the browser is sent to. Requests offline access (a refresh token) and the read-only scopes.</summary>
    Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri);

    Task<CloudTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct);

    /// <summary>
    /// New access token from a refresh token. A refresh token the provider refuses (<c>invalid_grant</c>)
    /// is <see cref="CloudStorageErrorKind.Unauthorized"/>: the person must reconnect. When the provider
    /// rotates refresh tokens the new one is in the result; otherwise <see cref="CloudTokenSet.RefreshToken"/> is null.
    /// </summary>
    Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct);

    Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct);

    /// <summary>Best effort; a provider with no revocation endpoint does nothing. Never throws for an already-invalid token.</summary>
    Task RevokeAsync(CloudTokenSet tokens, CancellationToken ct);

    // ------------------------------------------------------------------ browsing (the folder picker)

    /// <summary>The spaces the account can pick a folder from. The account's own space first, with <see cref="CloudDrive.DriveId"/> null.</summary>
    Task<IReadOnlyList<CloudDrive>> ListDrivesAsync(string accessToken, CancellationToken ct);

    /// <summary>Validates a folder and names it. <see cref="CloudStorageErrorKind.NotFound"/> when it is gone, is not a folder, or is not visible.</summary>
    Task<CloudFolderInfo> GetFolderAsync(string accessToken, CloudLocation location, CancellationToken ct);

    /// <summary>One level of a folder, folders first then files, each sorted by name; at most <paramref name="maxItems"/> (then <c>Truncated</c>).</summary>
    Task<CloudFolderPage> ListFolderAsync(string accessToken, CloudLocation location, int maxItems, CancellationToken ct);

    // ------------------------------------------------------------------ syncing

    /// <summary>
    /// Every file under <paramref name="root"/>, recursively. Stops and sets <c>Truncated</c> after
    /// <paramref name="maxEntries"/> files rather than walking an unbounded tree. Shortcuts/aliases are
    /// not followed (no cycles, no escaping the folder). Items with no downloadable content are left out
    /// and counted in <see cref="CloudTreeListing.Unsupported"/>.
    /// </summary>
    Task<CloudTreeListing> ListTreeAsync(string accessToken, CloudLocation root, int maxEntries, CancellationToken ct);

    /// <summary>
    /// Opens a file's content (or its export, when <see cref="CloudFileEntry.ExportMimeType"/> is set).
    /// The caller disposes the result and enforces the byte limit while reading.
    /// </summary>
    Task<CloudDownload> OpenDownloadAsync(string accessToken, CloudLocation root, CloudFileEntry file, CancellationToken ct);
}

/// <summary>Tokens as the provider issued them. <see cref="AccessTokenExpiresAt"/> is UTC; null means "not stated".</summary>
public sealed record CloudTokenSet(string AccessToken, string? RefreshToken, DateTime? AccessTokenExpiresAt, string? Scope);

/// <summary><see cref="AccountId"/> is the provider's stable id, never the email.</summary>
public sealed record CloudAccountInfo(string AccountId, string? Email, string? DisplayName);

public enum CloudDriveKind
{
    /// <summary>The account's own space: My Drive, the user's OneDrive, the Dropbox home.</summary>
    Personal = 0,

    /// <summary>A Google shared drive.</summary>
    SharedDrive = 1,

    /// <summary>A SharePoint site's document library.</summary>
    SharePointLibrary = 2,

    /// <summary>A Dropbox team space.</summary>
    TeamSpace = 3
}

/// <summary>A space to pick a folder from. <see cref="DriveId"/> null is the account's own space.</summary>
public sealed record CloudDrive(string? DriveId, string Name, CloudDriveKind Kind);

/// <summary>A folder: <see cref="DriveId"/> null = the account's own space, <see cref="FolderId"/> null = that drive's root.</summary>
public sealed record CloudLocation(string? DriveId, string? FolderId);

public sealed record CloudFolderInfo(string? FolderId, string Name, string DisplayPath);

public sealed record CloudItem(string Id, string Name, bool IsFolder, long? Size, DateTime? ModifiedAt, string? MimeType);

public sealed record CloudFolderPage(CloudFolderInfo Folder, IReadOnlyList<CloudItem> Items, bool Truncated);

/// <summary>
/// One file of a listing. <see cref="Revision"/> changes whenever the content changes (a content hash
/// where the provider has one, else its version/etag); the sync skips a file whose stored revision
/// equals it. <see cref="Size"/> is 0 when unknown (a native document before export).
/// <see cref="ExportMimeType"/> is set for a native document that is exported instead of downloaded, and
/// then <see cref="RelativePath"/> already carries the export's extension.
/// </summary>
public sealed record CloudFileEntry(
    string ItemId,
    string RelativePath,
    long Size,
    string Revision,
    string? MimeType,
    string? ExportMimeType = null);

public sealed record CloudTreeListing(IReadOnlyList<CloudFileEntry> Files, bool Truncated, int Unsupported);

/// <summary>An open download. Dispose it.</summary>
public sealed class CloudDownload : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public CloudDownload(Stream content, long? length, string? contentType, IDisposable? owner = null)
    {
        Content = content;
        Length = length;
        ContentType = contentType;
        _owner = owner;
    }

    public Stream Content { get; }

    public long? Length { get; }

    public string? ContentType { get; }

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
        _owner?.Dispose();
    }
}

public enum CloudStorageErrorKind
{
    /// <summary>401, or a refused refresh token: refresh once, then the person must reconnect.</summary>
    Unauthorized,

    /// <summary>403 that is not a rate limit: no permission on that item.</summary>
    Forbidden,

    /// <summary>404/409-not-found: the item is gone or not visible.</summary>
    NotFound,

    /// <summary>429, or a provider-specific rate-limit 403/503, after <see cref="CloudHttp"/> gave up retrying.</summary>
    RateLimited,

    /// <summary>5xx after retries.</summary>
    ServerError,

    /// <summary>The client id/secret are not configured.</summary>
    NotConfigured,

    /// <summary>A 2xx whose body did not have the expected shape, or any other 4xx.</summary>
    InvalidResponse
}

/// <summary>
/// A provider failure. <see cref="Exception.Message"/> is operator detail (REST path, status) for the log
/// and must never contain a secret; the sync service re-words by <see cref="Kind"/> for the person.
/// </summary>
public sealed class CloudStorageApiException : Exception
{
    public CloudStorageApiException(string message, CloudStorageErrorKind kind, HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public CloudStorageErrorKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
