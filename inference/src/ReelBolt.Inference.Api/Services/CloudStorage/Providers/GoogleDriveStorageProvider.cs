using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage.Providers;

/// <summary>
/// Google Drive (Drive API v3): My Drive and shared drives, read-only (docs/design/cloud-drive-sync.md,
/// "Google Drive specifics"). Every request goes through <see cref="CloudHttp.SendAsync"/>; Google's
/// <c>403 rateLimitExceeded</c>/<c>userRateLimitExceeded</c> is classified as a rate limit and retried.
/// Native Google documents are exported (Docs → Markdown, Sheets → CSV, Slides → PDF, Drawings → PNG)
/// with the export's extension appended to the path; every other <c>application/vnd.google-apps.*</c>
/// item (shortcuts, forms, sites, maps…) has no downloadable content and is counted as unsupported.
/// The constructor signature is fixed — the DI registration depends on it.
/// </summary>
public sealed class GoogleDriveStorageProvider : ICloudStorageProvider
{
    public const string HttpClientName = "CloudStorage.GoogleDrive";

    internal const string Scope = "https://www.googleapis.com/auth/drive.readonly";
    internal const string FolderMimeType = "application/vnd.google-apps.folder";
    internal const string MyDriveName = "My Drive";

    private const string NativeMimePrefix = "application/vnd.google-apps.";
    private const string ListFields = "nextPageToken,files(id,name,mimeType,size,md5Checksum,version,modifiedTime)";
    private const string FolderFields = "id,name,mimeType,parents,driveId,trashed";

    /// <summary>How far <see cref="GetFolderAsync"/> walks up the parents to name a folder.</summary>
    private const int MaxParentHops = 32;

    /// <summary>Bounds the breadth-first walk even when a tree is all folders and no files.</summary>
    private const int MaxFoldersPerTree = 10_000;

    private static readonly IReadOnlyDictionary<string, (string MimeType, string Extension)> Exports =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["application/vnd.google-apps.document"] = ("text/markdown", ".md"),
            ["application/vnd.google-apps.spreadsheet"] = ("text/csv", ".csv"),
            ["application/vnd.google-apps.presentation"] = ("application/pdf", ".pdf"),
            ["application/vnd.google-apps.drawing"] = ("image/png", ".png"),
        };

    private static readonly HashSet<string> RateLimitReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "rateLimitExceeded",
        "userRateLimitExceeded",
        "RATE_LIMIT_EXCEEDED",
    };

    private readonly HttpClient _http;
    private readonly CloudStorageOptions _options;
    private readonly ILogger<GoogleDriveStorageProvider> _logger;

    public GoogleDriveStorageProvider(HttpClient http, IOptions<CloudStorageOptions> options, ILogger<GoogleDriveStorageProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public CloudStorageProviderKind Kind => CloudStorageProviderKind.GoogleDrive;

    public bool IsConfigured => _options.GoogleDrive.IsConfigured;

    private GoogleDriveOptions Google => _options.GoogleDrive;

    // ------------------------------------------------------------------ OAuth

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri)
    {
        GoogleDriveOptions g = RequireConfigured();
        string query = Query(
            ("client_id", g.ClientId),
            ("redirect_uri", redirectUri.AbsoluteUri),
            ("response_type", "code"),
            ("scope", Scope),
            ("access_type", "offline"),
            ("prompt", "consent"),
            ("include_granted_scopes", "true"),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"));
        return new Uri(WithQuery(g.AuthorizeUrl, query));
    }

    public Task<CloudTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        GoogleDriveOptions g = RequireConfigured();
        return TokenRequestAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.AbsoluteUri),
                new("client_id", g.ClientId!),
                new("client_secret", g.ClientSecret!),
            ],
            "POST oauth2 token (authorization_code)",
            ct);
    }

    public Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        GoogleDriveOptions g = RequireConfigured();
        // Google does not rotate refresh tokens: RefreshToken stays null unless Google sends one.
        return TokenRequestAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", g.ClientId!),
                new("client_secret", g.ClientSecret!),
            ],
            "POST oauth2 token (refresh_token)",
            ct);
    }

    public async Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        const string label = "GET /drive/v3/about";
        string url = Api("/drive/v3/about", ("fields", "user(permissionId,emailAddress,displayName)"));
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);

        if (!doc.RootElement.TryGetProperty("user", out JsonElement user) || user.ValueKind != JsonValueKind.Object)
            throw Invalid(label);
        string? id = Str(user, "permissionId");
        if (string.IsNullOrEmpty(id)) throw Invalid(label);
        return new CloudAccountInfo(id, Str(user, "emailAddress"), Str(user, "displayName"));
    }

    public async Task RevokeAsync(CloudTokenSet tokens, CancellationToken ct)
    {
        // Revoking the refresh token also revokes every access token issued from it.
        string? token = !string.IsNullOrEmpty(tokens.RefreshToken) ? tokens.RefreshToken : tokens.AccessToken;
        if (string.IsNullOrEmpty(token)) return;

        try
        {
            using HttpResponseMessage _ = await CloudHttp.SendAsync(
                _http,
                () => new HttpRequestMessage(HttpMethod.Post, Google.RevokeUrl)
                {
                    Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)])
                },
                "POST oauth2 revoke",
                ct);
        }
        catch (CloudStorageApiException ex)
        {
            // An already-invalid token answers 400 invalid_token; revocation is best effort either way.
            _logger.LogInformation("Google Drive token revocation was not confirmed ({Kind}, HTTP {Status}).", ex.Kind, (int?)ex.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Google Drive token revocation could not reach Google ({ExceptionType}).", ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------ browsing

    public async Task<IReadOnlyList<CloudDrive>> ListDrivesAsync(string accessToken, CancellationToken ct)
    {
        const string label = "GET /drive/v3/drives";
        var drives = new List<CloudDrive> { new(null, MyDriveName, CloudDriveKind.Personal) };
        string? pageToken = null;
        do
        {
            string url = Api(
                "/drive/v3/drives",
                ("pageSize", "100"),
                ("fields", "nextPageToken,drives(id,name)"),
                ("pageToken", pageToken));
            using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
            using JsonDocument doc = await ReadJsonAsync(response, label, ct);

            if (doc.RootElement.TryGetProperty("drives", out JsonElement list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw Invalid(label);
                foreach (JsonElement drive in list.EnumerateArray())
                {
                    string? id = Str(drive, "id");
                    if (string.IsNullOrEmpty(id)) throw Invalid(label);
                    string? name = Str(drive, "name");
                    drives.Add(new CloudDrive(id, string.IsNullOrEmpty(name) ? id : name, CloudDriveKind.SharedDrive));
                }
            }

            pageToken = NextPage(doc.RootElement, pageToken, label);
        }
        while (pageToken is not null);

        return drives;
    }

    public async Task<CloudFolderInfo> GetFolderAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        string driveName = location.DriveId is null
            ? MyDriveName
            : await GetSharedDriveNameAsync(accessToken, location.DriveId, ct);
        if (IsDriveRoot(location)) return new CloudFolderInfo(null, driveName, driveName);

        DriveFile folder = await GetFolderItemAsync(accessToken, location, ct);
        if (folder.Parents.Count == 0)
        {
            // The My Drive root addressed by its own id.
            return new CloudFolderInfo(location.FolderId, driveName, driveName);
        }

        var segments = new List<string> { folder.Name };
        string? parentId = folder.Parents[0];
        for (int hop = 0; parentId is not null; hop++)
        {
            if (parentId == location.DriveId) { parentId = null; break; } // the shared drive's root
            if (hop >= MaxParentHops) break;

            DriveFile parent;
            try
            {
                parent = await GetFileAsync(accessToken, parentId, "id,name,parents", ct);
            }
            catch (CloudStorageApiException ex) when (ex.Kind is CloudStorageErrorKind.NotFound or CloudStorageErrorKind.Forbidden)
            {
                // A folder shared with the person: its parents are not theirs to see.
                parentId = null;
                break;
            }

            if (parent.Parents.Count == 0) { parentId = null; break; } // My Drive's root
            segments.Add(parent.Name);
            parentId = parent.Parents[0];
        }

        if (parentId is not null) segments.Add("…"); // the walk hit its bound
        segments.Add(driveName);
        segments.Reverse();
        return new CloudFolderInfo(location.FolderId, folder.Name, string.Join("/", segments));
    }

    public async Task<CloudFolderPage> ListFolderAsync(string accessToken, CloudLocation location, int maxItems, CancellationToken ct)
    {
        if (maxItems < 0) maxItems = 0;
        CloudFolderInfo folder = await GetFolderAsync(accessToken, location, ct);
        string parentId = ContainerId(location);

        List<DriveFile> folders = await CollectChildrenAsync(
            accessToken, location, parentId, $" and mimeType = '{FolderMimeType}'", maxItems + 1, ct);
        bool truncated = folders.Count > maxItems;
        List<DriveFile> files = [];
        if (!truncated)
        {
            int remaining = maxItems - folders.Count;
            files = await CollectChildrenAsync(
                accessToken, location, parentId, $" and mimeType != '{FolderMimeType}'", remaining + 1, ct);
            truncated = files.Count > remaining;
        }

        var items = new List<CloudItem>(Math.Min(maxItems, folders.Count + files.Count));
        foreach (DriveFile f in SortByName(folders))
            items.Add(new CloudItem(f.Id, f.Name, true, null, f.ModifiedAt, f.MimeType));
        foreach (DriveFile f in SortByName(files))
            items.Add(new CloudItem(f.Id, f.Name, false, f.MimeType.StartsWith(NativeMimePrefix, StringComparison.Ordinal) ? null : f.Size ?? 0, f.ModifiedAt, f.MimeType));
        if (items.Count > maxItems) items.RemoveRange(maxItems, items.Count - maxItems);

        return new CloudFolderPage(folder, items, truncated);
    }

    // ------------------------------------------------------------------ syncing

    public async Task<CloudTreeListing> ListTreeAsync(string accessToken, CloudLocation root, int maxEntries, CancellationToken ct)
    {
        // Validate the root first: a folder that was deleted or trashed must fail the sync, not list as
        // empty (which would make the sync delete every file it pulled from it).
        if (!IsDriveRoot(root)) await GetFolderItemAsync(accessToken, root, ct);
        else if (root.DriveId is not null) await GetSharedDriveNameAsync(accessToken, root.DriveId, ct);

        string rootId = ContainerId(root);
        var queue = new Queue<(string Id, string Prefix)>();
        queue.Enqueue((rootId, string.Empty));
        var visitedFolders = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<CloudFileEntry>();
        int unsupported = 0;
        bool truncated = false;

        while (queue.Count > 0 && !truncated)
        {
            (string folderId, string prefix) = queue.Dequeue();
            string? pageToken = null;
            do
            {
                (List<DriveFile> children, string? next) = await ListChildrenPageAsync(
                    accessToken, root, folderId, string.Empty, pageToken, 1000, ct);

                foreach (DriveFile item in children)
                {
                    if (!IsSafeName(item.Name)) { unsupported++; continue; }

                    if (item.MimeType == FolderMimeType)
                    {
                        if (!visitedFolders.Add(item.Id)) continue; // multi-parent folder already queued
                        if (visitedFolders.Count > MaxFoldersPerTree) { truncated = true; break; }
                        queue.Enqueue((item.Id, prefix + item.Name + "/"));
                        continue;
                    }

                    CloudFileEntry? entry = ToEntry(item, prefix);
                    if (entry is null || !paths.Add(entry.RelativePath))
                    {
                        // No downloadable content, or a second item under a path already taken
                        // (Drive allows two files with the same name in one folder).
                        unsupported++;
                        continue;
                    }

                    if (files.Count >= maxEntries) { truncated = true; break; }
                    files.Add(entry);
                }

                pageToken = next;
            }
            while (pageToken is not null && !truncated);
        }

        return new CloudTreeListing(files, truncated, unsupported);
    }

    public async Task<CloudDownload> OpenDownloadAsync(string accessToken, CloudLocation root, CloudFileEntry file, CancellationToken ct)
    {
        string label;
        string url;
        if (file.ExportMimeType is { } exportMime)
        {
            label = "GET /drive/v3/files/{id}/export";
            url = Api($"/drive/v3/files/{CloudHttp.Esc(file.ItemId)}/export", ("mimeType", exportMime));
        }
        else
        {
            label = "GET /drive/v3/files/{id}?alt=media";
            url = Api($"/drive/v3/files/{CloudHttp.Esc(file.ItemId)}", ("alt", "media"), ("supportsAllDrives", "true"));
        }

        HttpResponseMessage response = await GetAsync(accessToken, url, label, ct, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            Stream content = await response.Content.ReadAsStreamAsync(ct);
            return new CloudDownload(
                content,
                response.Content.Headers.ContentLength,
                response.Content.Headers.ContentType?.MediaType,
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ helpers: Drive calls

    private async Task<string> GetSharedDriveNameAsync(string accessToken, string driveId, CancellationToken ct)
    {
        const string label = "GET /drive/v3/drives/{id}";
        string url = Api($"/drive/v3/drives/{CloudHttp.Esc(driveId)}", ("fields", "id,name"));
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        string? name = Str(doc.RootElement, "name");
        return string.IsNullOrEmpty(name) ? driveId : name;
    }

    /// <summary>The folder named by <paramref name="location"/>; NotFound unless it is a live folder of that drive.</summary>
    private async Task<DriveFile> GetFolderItemAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        DriveFile folder = await GetFileAsync(accessToken, location.FolderId!, FolderFields, ct);
        if (folder.MimeType != FolderMimeType || folder.Trashed || !string.Equals(folder.DriveId, location.DriveId, StringComparison.Ordinal))
        {
            throw new CloudStorageApiException(
                "GET /drive/v3/files/{id}: the item is not a folder of the selected drive, or is in the trash.",
                CloudStorageErrorKind.NotFound,
                HttpStatusCode.NotFound);
        }

        return folder;
    }

    private async Task<DriveFile> GetFileAsync(string accessToken, string fileId, string fields, CancellationToken ct)
    {
        const string label = "GET /drive/v3/files/{id}";
        string url = Api($"/drive/v3/files/{CloudHttp.Esc(fileId)}", ("fields", fields), ("supportsAllDrives", "true"));
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        return ParseFile(doc.RootElement, label);
    }

    private async Task<List<DriveFile>> CollectChildrenAsync(
        string accessToken, CloudLocation location, string parentId, string extraQuery, int limit, CancellationToken ct)
    {
        var result = new List<DriveFile>();
        string? pageToken = null;
        do
        {
            int pageSize = Math.Clamp(limit - result.Count, 1, 1000);
            (List<DriveFile> page, string? next) = await ListChildrenPageAsync(
                accessToken, location, parentId, extraQuery, pageToken, pageSize, ct, orderBy: "name");
            result.AddRange(page);
            pageToken = next;
        }
        while (pageToken is not null && result.Count < limit);

        return result;
    }

    private async Task<(List<DriveFile> Files, string? NextPageToken)> ListChildrenPageAsync(
        string accessToken,
        CloudLocation location,
        string parentId,
        string extraQuery,
        string? pageToken,
        int pageSize,
        CancellationToken ct,
        string? orderBy = null)
    {
        const string label = "GET /drive/v3/files";
        string q = $"'{EscapeQueryLiteral(parentId)}' in parents and trashed = false{extraQuery}";
        bool sharedDrive = location.DriveId is not null;
        string url = Api(
            "/drive/v3/files",
            ("q", q),
            ("fields", ListFields),
            ("pageSize", pageSize.ToString(CultureInfo.InvariantCulture)),
            ("supportsAllDrives", "true"),
            ("includeItemsFromAllDrives", sharedDrive ? "true" : null),
            ("corpora", sharedDrive ? "drive" : null),
            ("driveId", location.DriveId),
            ("orderBy", orderBy),
            ("pageToken", pageToken));

        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);

        var files = new List<DriveFile>();
        if (doc.RootElement.TryGetProperty("files", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw Invalid(label);
            foreach (JsonElement item in list.EnumerateArray()) files.Add(ParseFile(item, label));
        }

        return (files, NextPage(doc.RootElement, pageToken, label));
    }

    private Task<HttpResponseMessage> GetAsync(
        string accessToken,
        string url,
        string label,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead) =>
        CloudHttp.SendAsync(
            _http,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                return request;
            },
            label,
            ct,
            ClassifyApiError,
            completion);

    private async Task<CloudTokenSet> TokenRequestAsync(KeyValuePair<string, string>[] form, string label, CancellationToken ct)
    {
        using HttpResponseMessage response = await CloudHttp.SendAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Post, Google.TokenUrl) { Content = new FormUrlEncodedContent(form) },
            label,
            ct,
            ClassifyTokenError);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        JsonElement root = doc.RootElement;

        string? accessToken = Str(root, "access_token");
        if (string.IsNullOrEmpty(accessToken)) throw Invalid(label);

        DateTime? expiresAt = null;
        if (Long(root, "expires_in") is { } seconds && seconds > 0) expiresAt = DateTime.UtcNow.AddSeconds(seconds);

        string? refreshToken = Str(root, "refresh_token");
        return new CloudTokenSet(
            accessToken,
            string.IsNullOrEmpty(refreshToken) ? null : refreshToken,
            expiresAt,
            Str(root, "scope"));
    }

    // ------------------------------------------------------------------ helpers: classification

    /// <summary>Google's quota answers are 403s with a reason; they are rate limits, retried like a 429.</summary>
    internal static CloudStorageErrorKind? ClassifyApiError(HttpStatusCode status, string body)
    {
        if (status != HttpStatusCode.Forbidden || string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (Str(error, "status") == "RESOURCE_EXHAUSTED") return CloudStorageErrorKind.RateLimited;
            foreach (string arrayName in new[] { "errors", "details" })
            {
                if (!error.TryGetProperty(arrayName, out JsonElement entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    if (Str(entry, "reason") is { } reason && RateLimitReasons.Contains(reason)) return CloudStorageErrorKind.RateLimited;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: default mapping.
        }

        return null;
    }

    /// <summary>A refused code or refresh token (<c>invalid_grant</c>) means the person must connect again.</summary>
    internal static CloudStorageErrorKind? ClassifyTokenError(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) || string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && Str(doc.RootElement, "error") == "invalid_grant")
                return CloudStorageErrorKind.Unauthorized;
        }
        catch (JsonException)
        {
            // Not JSON: default mapping.
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers: mapping

    private static CloudFileEntry? ToEntry(DriveFile item, string prefix)
    {
        string? versionRevision = item.Version is { Length: > 0 } version
            ? "v" + version
            : item.ModifiedAt is { } modified ? "t" + modified.ToString("O", CultureInfo.InvariantCulture) : null;

        if (Exports.TryGetValue(item.MimeType, out (string MimeType, string Extension) export))
        {
            if (versionRevision is null) return null;
            string name = item.Name.EndsWith(export.Extension, StringComparison.OrdinalIgnoreCase)
                ? item.Name
                : item.Name + export.Extension;
            return new CloudFileEntry(item.Id, prefix + name, 0, versionRevision, item.MimeType, export.MimeType);
        }

        // Shortcuts, forms, sites, maps, jamboards…: nothing to download.
        if (item.MimeType.StartsWith(NativeMimePrefix, StringComparison.Ordinal)) return null;

        string? revision = !string.IsNullOrEmpty(item.Md5) ? item.Md5 : versionRevision;
        if (revision is null) return null;
        return new CloudFileEntry(item.Id, prefix + item.Name, item.Size ?? 0, revision, item.MimeType);
    }

    private static bool IsSafeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Any(char.IsControl);

    private static bool IsDriveRoot(CloudLocation location) =>
        location.FolderId is null
        || location.FolderId == location.DriveId
        || (location.DriveId is null && location.FolderId == "root");

    /// <summary>The id whose children a location lists: <c>root</c> for My Drive, the drive id for a shared drive's root.</summary>
    private static string ContainerId(CloudLocation location) =>
        IsDriveRoot(location) ? location.DriveId ?? "root" : location.FolderId!;

    private static IEnumerable<DriveFile> SortByName(IEnumerable<DriveFile> items) =>
        items.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal);

    /// <summary>Escapes a value inside a single-quoted Drive query literal (<c>'</c> and <c>\</c>).</summary>
    internal static string EscapeQueryLiteral(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private static DriveFile ParseFile(JsonElement e, string label)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalid(label);
        string? id = Str(e, "id");
        if (string.IsNullOrEmpty(id)) throw Invalid(label);

        var parents = new List<string>();
        if (e.TryGetProperty("parents", out JsonElement p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement parent in p.EnumerateArray())
            {
                if (parent.ValueKind == JsonValueKind.String && parent.GetString() is { Length: > 0 } pid) parents.Add(pid);
            }
        }

        DateTime? modified = null;
        if (Str(e, "modifiedTime") is { } m
            && DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            modified = parsed;
        }

        return new DriveFile(
            id,
            Str(e, "name") ?? string.Empty,
            Str(e, "mimeType") ?? "application/octet-stream",
            Long(e, "size"),
            Str(e, "md5Checksum"),
            Str(e, "version"),
            modified,
            parents,
            Str(e, "driveId"),
            e.TryGetProperty("trashed", out JsonElement t) && t.ValueKind == JsonValueKind.True);
    }

    private static string? NextPage(JsonElement root, string? previous, string label)
    {
        string? next = Str(root, "nextPageToken");
        if (string.IsNullOrEmpty(next)) return null;
        if (next == previous) throw Invalid(label); // a page token that does not advance would loop forever
        return next;
    }

    // ------------------------------------------------------------------ helpers: plumbing

    private GoogleDriveOptions RequireConfigured()
    {
        if (!IsConfigured)
            throw new CloudStorageApiException("Google Drive is not configured (CloudStorage:GoogleDrive).", CloudStorageErrorKind.NotConfigured);
        return Google;
    }

    private string Api(string path, params (string Key, string? Value)[] query) =>
        WithQuery(Google.ApiBaseUrl.TrimEnd('/') + path, Query(query));

    private static string WithQuery(string baseUrl, string query) =>
        query.Length == 0 ? baseUrl : baseUrl + (baseUrl.Contains('?') ? "&" : "?") + query;

    private static string Query(params (string Key, string? Value)[] pairs) =>
        string.Join("&", pairs.Where(p => p.Value is not null).Select(p => $"{CloudHttp.Esc(p.Key)}={CloudHttp.Esc(p.Value!)}"));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string label, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            // The parser's message can quote the body; never let remote text (or a token) reach the log.
            throw Invalid(label);
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw Invalid(label);
        }

        return doc;
    }

    private static CloudStorageApiException Invalid(string label) =>
        new($"{label} returned a response of an unexpected shape.", CloudStorageErrorKind.InvalidResponse);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null
            }
            : null;

    private static long? Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s)) return s;
        return null;
    }

    private sealed record DriveFile(
        string Id,
        string Name,
        string MimeType,
        long? Size,
        string? Md5,
        string? Version,
        DateTime? ModifiedAt,
        IReadOnlyList<string> Parents,
        string? DriveId,
        bool Trashed);
}
