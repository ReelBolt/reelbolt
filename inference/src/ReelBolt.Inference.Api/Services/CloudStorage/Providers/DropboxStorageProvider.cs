using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage.Providers;

/// <summary>
/// Dropbox (API v2): the person's own space and, for a team member, the team space, read-only
/// (docs/design/cloud-drive-sync.md, "Dropbox specifics"). Every request goes through
/// <see cref="CloudHttp.SendAsync"/>; Dropbox's <c>409</c> endpoint errors are classified from their
/// <c>error_summary</c>. Folder ids are Dropbox file ids (<c>id:…</c>, they survive renames); a null
/// folder id is the space's root (<c>""</c>). A call into the team space carries
/// <c>Dropbox-API-Path-Root</c>. The constructor signature is fixed — the DI registration depends on it.
/// </summary>
public sealed class DropboxStorageProvider : ICloudStorageProvider
{
    public const string HttpClientName = "CloudStorage.Dropbox";

    internal const string Scope = "account_info.read files.metadata.read files.content.read";
    internal const string HomeName = "Dropbox";
    internal const string TeamSpaceName = "Team space";
    internal const string PathRootHeader = "Dropbox-API-Path-Root";
    internal const string ApiArgHeader = "Dropbox-API-Arg";

    private const int ListLimit = 2000;

    /// <summary>Bounds a one-level listing for the folder picker (it has to be read whole to sort it).</summary>
    private const int MaxFolderEntriesScanned = 10_000;

    /// <summary>Bounds the recursive walk even when a tree is all folders or unsupported items.</summary>
    private const int MaxTreeEntriesScanned = 200_000;

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly HttpClient _http;
    private readonly CloudStorageOptions _options;
    private readonly ILogger<DropboxStorageProvider> _logger;

    public DropboxStorageProvider(HttpClient http, IOptions<CloudStorageOptions> options, ILogger<DropboxStorageProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public CloudStorageProviderKind Kind => CloudStorageProviderKind.Dropbox;

    public bool IsConfigured => _options.Dropbox.IsConfigured;

    private DropboxOptions Dropbox => _options.Dropbox;

    // ------------------------------------------------------------------ OAuth

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri)
    {
        DropboxOptions d = RequireConfigured();
        string query = Query(
            ("client_id", d.ClientId),
            ("redirect_uri", redirectUri.AbsoluteUri),
            ("response_type", "code"),
            ("token_access_type", "offline"),
            ("scope", Scope),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"));
        return new Uri(d.AuthorizeUrl + (d.AuthorizeUrl.Contains('?') ? "&" : "?") + query);
    }

    public Task<CloudTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        DropboxOptions d = RequireConfigured();
        return TokenRequestAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.AbsoluteUri),
                new("client_id", d.ClientId!),
                new("client_secret", d.ClientSecret!),
            ],
            "POST /oauth2/token (authorization_code)",
            ct);
    }

    public Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        DropboxOptions d = RequireConfigured();
        // Dropbox does not rotate refresh tokens: RefreshToken stays null unless Dropbox sends one.
        return TokenRequestAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("client_id", d.ClientId!),
                new("client_secret", d.ClientSecret!),
            ],
            "POST /oauth2/token (refresh_token)",
            ct);
    }

    public async Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        Account account = await GetCurrentAccountAsync(accessToken, ct);
        return new CloudAccountInfo(account.AccountId, account.Email, account.DisplayName);
    }

    public async Task RevokeAsync(CloudTokenSet tokens, CancellationToken ct)
    {
        // Revoking the access token also disables the refresh token it came from.
        if (string.IsNullOrEmpty(tokens.AccessToken)) return;
        try
        {
            using HttpResponseMessage _ = await PostRpcAsync(tokens.AccessToken, "/2/auth/token/revoke", "null", null, "POST /2/auth/token/revoke", ct);
        }
        catch (CloudStorageApiException ex)
        {
            // 401: the token is already invalid. Revocation is best effort either way.
            _logger.LogInformation("Dropbox token revocation was not confirmed ({Kind}, HTTP {Status}).", ex.Kind, (int?)ex.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogInformation("Dropbox token revocation could not reach Dropbox ({ExceptionType}).", ex.GetType().Name);
        }
    }

    // ------------------------------------------------------------------ browsing

    public async Task<IReadOnlyList<CloudDrive>> ListDrivesAsync(string accessToken, CancellationToken ct)
    {
        Account account = await GetCurrentAccountAsync(accessToken, ct);
        var drives = new List<CloudDrive> { new(null, HomeName, CloudDriveKind.Personal) };
        if (!string.IsNullOrEmpty(account.RootNamespaceId)
            && !string.IsNullOrEmpty(account.HomeNamespaceId)
            && account.RootNamespaceId != account.HomeNamespaceId)
        {
            drives.Add(new CloudDrive(account.RootNamespaceId, TeamSpaceName, CloudDriveKind.TeamSpace));
        }

        return drives;
    }

    public async Task<CloudFolderInfo> GetFolderAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        string driveName = DriveName(location);
        if (string.IsNullOrEmpty(location.FolderId)) return new CloudFolderInfo(null, driveName, driveName);

        Entry folder = await GetFolderEntryAsync(accessToken, location, ct);
        string displayPath = !string.IsNullOrEmpty(folder.PathDisplay)
            ? driveName + "/" + folder.PathDisplay.Trim('/')
            : driveName + "/" + folder.Name;
        return new CloudFolderInfo(location.FolderId, folder.Name, displayPath);
    }

    public async Task<CloudFolderPage> ListFolderAsync(string accessToken, CloudLocation location, int maxItems, CancellationToken ct)
    {
        if (maxItems < 0) maxItems = 0;
        CloudFolderInfo folder = await GetFolderAsync(accessToken, location, ct);

        var folders = new List<Entry>();
        var files = new List<Entry>();
        bool capped = false;
        await foreach (Entry entry in ListAsync(accessToken, location, recursive: false, ct))
        {
            if (entry.Tag == "folder") folders.Add(entry);
            else if (entry.Tag == "file") files.Add(entry);
            if (folders.Count + files.Count >= MaxFolderEntriesScanned) { capped = true; break; }
        }

        var items = new List<CloudItem>();
        foreach (Entry f in SortByName(folders)) items.Add(new CloudItem(f.Id!, f.Name, true, null, null, null));
        foreach (Entry f in SortByName(files)) items.Add(new CloudItem(f.Id!, f.Name, false, f.Size ?? 0, f.ServerModified, null));

        bool truncated = capped || items.Count > maxItems;
        if (items.Count > maxItems) items.RemoveRange(maxItems, items.Count - maxItems);
        return new CloudFolderPage(folder, items, truncated);
    }

    // ------------------------------------------------------------------ syncing

    public async Task<CloudTreeListing> ListTreeAsync(string accessToken, CloudLocation root, int maxEntries, CancellationToken ct)
    {
        // Resolve (and so validate) the root first: a folder that was deleted, or is now a file, must fail
        // the sync rather than list as empty (which would make the sync delete every file it pulled).
        string rootLower = string.Empty;
        if (!string.IsNullOrEmpty(root.FolderId))
        {
            Entry folder = await GetFolderEntryAsync(accessToken, root, ct);
            rootLower = (folder.PathLower ?? string.Empty).TrimEnd('/');
        }

        int rootSegments = rootLower.Length == 0 ? 0 : rootLower.Trim('/').Split('/').Length;
        var folderPaths = new Dictionary<string, string>(StringComparer.Ordinal); // path_lower → relative path
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<CloudFileEntry>();
        int unsupported = 0;
        bool truncated = false;
        int scanned = 0;

        await foreach (Entry entry in ListAsync(accessToken, root, recursive: true, ct))
        {
            if (++scanned > MaxTreeEntriesScanned) { truncated = true; break; }
            if (entry.Tag is not ("file" or "folder")) continue; // "deleted" (not requested anyway)

            string? relative = RelativePath(entry, rootLower, rootSegments, folderPaths);
            if (relative is null)
            {
                // The root folder itself (a recursive listing includes it) or an entry outside it.
                if (entry.PathLower is { } pl && pl.TrimEnd('/') == rootLower) continue;
                if (entry.Tag == "file") unsupported++;
                continue;
            }

            if (entry.Tag == "folder")
            {
                if (IsSafeRelativePath(relative) && entry.PathLower is { } folderLower)
                    folderPaths[folderLower.TrimEnd('/')] = relative;
                else
                    unsupported++;
                continue;
            }

            if (!IsSafeRelativePath(relative) || entry.IsDownloadable == false || string.IsNullOrEmpty(entry.Id))
            {
                unsupported++;
                continue;
            }

            string? revision = !string.IsNullOrEmpty(entry.ContentHash) ? entry.ContentHash : entry.Rev;
            if (string.IsNullOrEmpty(revision)) { unsupported++; continue; }

            // Dropbox paths are case-insensitive: two entries differing only in case are one path here.
            if (!paths.Add(relative)) { unsupported++; continue; }

            if (files.Count >= maxEntries) { truncated = true; break; }
            files.Add(new CloudFileEntry(entry.Id, relative, entry.Size ?? 0, revision, null));
        }

        return new CloudTreeListing(files, truncated, unsupported);
    }

    public async Task<CloudDownload> OpenDownloadAsync(string accessToken, CloudLocation root, CloudFileEntry file, CancellationToken ct)
    {
        const string label = "POST /2/files/download";
        string url = Dropbox.ContentBaseUrl.TrimEnd('/') + "/2/files/download";
        string apiArg = AsciiJson(JsonSerializer.Serialize(new { path = file.ItemId }, RelaxedJson));

        HttpResponseMessage response = await CloudHttp.SendAsync(
            _http,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Headers.TryAddWithoutValidation(ApiArgHeader, apiArg);
                AddPathRoot(request, root.DriveId);
                return request;
            },
            label,
            ct,
            ClassifyApiError,
            HttpCompletionOption.ResponseHeadersRead);
        try
        {
            Stream content = await response.Content.ReadAsStreamAsync(ct);
            return new CloudDownload(content, response.Content.Headers.ContentLength, response.Content.Headers.ContentType?.MediaType, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------ helpers: Dropbox calls

    private async Task<Account> GetCurrentAccountAsync(string accessToken, CancellationToken ct)
    {
        const string label = "POST /2/users/get_current_account";
        using HttpResponseMessage response = await PostRpcAsync(accessToken, "/2/users/get_current_account", "null", null, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        JsonElement root = doc.RootElement;

        string? accountId = Str(root, "account_id");
        if (string.IsNullOrEmpty(accountId)) throw Invalid(label);
        string? displayName = root.TryGetProperty("name", out JsonElement name) ? Str(name, "display_name") : null;
        string? rootNs = null;
        string? homeNs = null;
        if (root.TryGetProperty("root_info", out JsonElement info))
        {
            rootNs = Str(info, "root_namespace_id");
            homeNs = Str(info, "home_namespace_id");
        }

        return new Account(accountId, Str(root, "email"), displayName, rootNs, homeNs);
    }

    /// <summary>The folder named by <paramref name="location"/>; NotFound when it is gone, deleted or a file.</summary>
    private async Task<Entry> GetFolderEntryAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        const string label = "POST /2/files/get_metadata";
        string body = JsonSerializer.Serialize(new { path = location.FolderId, include_deleted = false }, RelaxedJson);
        using HttpResponseMessage response = await PostRpcAsync(accessToken, "/2/files/get_metadata", body, location.DriveId, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        Entry entry = ParseEntry(doc.RootElement, label);
        if (entry.Tag != "folder")
        {
            throw new CloudStorageApiException(
                $"{label}: the item is not a folder (or was deleted).", CloudStorageErrorKind.NotFound, HttpStatusCode.NotFound);
        }

        return entry;
    }

    /// <summary><c>list_folder</c> then <c>list_folder/continue</c> until <c>has_more</c> is false.</summary>
    private async IAsyncEnumerable<Entry> ListAsync(
        string accessToken, CloudLocation location, bool recursive, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string label = "POST /2/files/list_folder";
        string path = "/2/files/list_folder";
        string body = JsonSerializer.Serialize(
            new
            {
                path = location.FolderId ?? string.Empty,
                recursive,
                include_deleted = false,
                include_non_downloadable_files = true,
                limit = ListLimit,
            },
            RelaxedJson);

        while (true)
        {
            List<Entry> entries = [];
            bool hasMore;
            string? cursor;
            using (HttpResponseMessage response = await PostRpcAsync(accessToken, path, body, location.DriveId, label, ct))
            using (JsonDocument doc = await ReadJsonAsync(response, label, ct))
            {
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("entries", out JsonElement list) || list.ValueKind != JsonValueKind.Array) throw Invalid(label);
                foreach (JsonElement e in list.EnumerateArray()) entries.Add(ParseEntry(e, label));
                hasMore = root.TryGetProperty("has_more", out JsonElement more) && more.ValueKind == JsonValueKind.True;
                cursor = Str(root, "cursor");
            }

            foreach (Entry entry in entries) yield return entry;
            if (!hasMore) yield break;
            if (string.IsNullOrEmpty(cursor)) throw Invalid(label);

            label = "POST /2/files/list_folder/continue";
            path = "/2/files/list_folder/continue";
            body = JsonSerializer.Serialize(new { cursor }, RelaxedJson);
        }
    }

    private Task<HttpResponseMessage> PostRpcAsync(string accessToken, string path, string jsonBody, string? driveId, string label, CancellationToken ct)
    {
        string url = Dropbox.ApiBaseUrl.TrimEnd('/') + path;
        return CloudHttp.SendAsync(
            _http,
            () =>
            {
                var content = new StringContent(jsonBody, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                AddPathRoot(request, driveId);
                return request;
            },
            label,
            ct,
            ClassifyApiError);
    }

    private async Task<CloudTokenSet> TokenRequestAsync(KeyValuePair<string, string>[] form, string label, CancellationToken ct)
    {
        string url = Dropbox.ApiBaseUrl.TrimEnd('/') + "/oauth2/token";
        using HttpResponseMessage response = await CloudHttp.SendAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) },
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
        return new CloudTokenSet(accessToken, string.IsNullOrEmpty(refreshToken) ? null : refreshToken, expiresAt, Str(root, "scope"));
    }

    private static void AddPathRoot(HttpRequestMessage request, string? driveId)
    {
        if (string.IsNullOrEmpty(driveId)) return;
        string value = AsciiJson(JsonSerializer.Serialize(
            new Dictionary<string, string> { [".tag"] = "namespace_id", ["namespace_id"] = driveId },
            RelaxedJson));
        request.Headers.TryAddWithoutValidation(PathRootHeader, value);
    }

    // ------------------------------------------------------------------ helpers: classification

    /// <summary>
    /// Dropbox reports endpoint errors as <c>409</c> with an <c>error_summary</c> such as
    /// <c>path/not_found/..</c>. A missing item is NotFound; content Dropbox will not serve is Forbidden
    /// (the sync skips and counts it); any other 409 is a request we got wrong.
    /// </summary>
    internal static CloudStorageErrorKind? ClassifyApiError(HttpStatusCode status, string body)
    {
        if (status != HttpStatusCode.Conflict) return null;
        string summary = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) summary = Str(doc.RootElement, "error_summary") ?? string.Empty;
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        if (summary.StartsWith("path/not_found", StringComparison.Ordinal)
            || summary.StartsWith("path_lookup/not_found", StringComparison.Ordinal))
        {
            return CloudStorageErrorKind.NotFound;
        }

        if (summary.StartsWith("path/restricted_content", StringComparison.Ordinal)
            || summary.StartsWith("path/unsupported_content_type", StringComparison.Ordinal))
        {
            return CloudStorageErrorKind.Forbidden;
        }

        return CloudStorageErrorKind.InvalidResponse;
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

    // ------------------------------------------------------------------ helpers: paths

    /// <summary>
    /// The entry's path relative to the listed root, or null when it is the root itself or outside it.
    /// The root prefix is matched on <c>path_lower</c>; the parent is the relative path already recorded
    /// for the parent folder (so a file and its folder agree on casing), else <c>path_display</c>'s
    /// segments after the root's; the last segment is <c>name</c>, whose casing Dropbox guarantees.
    /// </summary>
    private static string? RelativePath(Entry entry, string rootLower, int rootSegments, Dictionary<string, string> folderPaths)
    {
        string? lower = entry.PathLower?.TrimEnd('/');
        if (string.IsNullOrEmpty(lower) || !lower.StartsWith(rootLower + "/", StringComparison.Ordinal)) return null;

        int slash = lower.LastIndexOf('/');
        string parentLower = lower[..slash];
        string parentRelative;
        if (parentLower == rootLower)
        {
            parentRelative = string.Empty;
        }
        else if (folderPaths.TryGetValue(parentLower, out string? known))
        {
            parentRelative = known;
        }
        else
        {
            if (string.IsNullOrEmpty(entry.PathDisplay)) return null;
            string[] display = entry.PathDisplay.Trim('/').Split('/');
            if (display.Length <= rootSegments + 1) return null;
            parentRelative = string.Join("/", display[rootSegments..^1]);
        }

        return parentRelative.Length == 0 ? entry.Name : parentRelative + "/" + entry.Name;
    }

    private static bool IsSafeRelativePath(string relative) => relative.Split('/').All(IsSafeName);

    private static bool IsSafeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.IndexOf('\\') < 0
        && !name.Any(char.IsControl);

    private static string DriveName(CloudLocation location) => location.DriveId is null ? HomeName : TeamSpaceName;

    private static IEnumerable<Entry> SortByName(IEnumerable<Entry> items) =>
        items.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Name, StringComparer.Ordinal);

    /// <summary>
    /// HTTP header values must be ASCII: every character above <c>0x7E</c> becomes a <c>\uXXXX</c> escape
    /// (each UTF-16 unit separately, so a surrogate pair stays valid JSON).
    /// </summary>
    internal static string AsciiJson(string json)
    {
        var sb = new StringBuilder(json.Length);
        foreach (char c in json)
        {
            if (c > '\u007E') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers: parsing and plumbing

    private static Entry ParseEntry(JsonElement e, string label)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalid(label);
        string? tag = Str(e, ".tag");
        if (string.IsNullOrEmpty(tag)) throw Invalid(label);
        string? id = Str(e, "id");
        if (tag is "file" or "folder" && string.IsNullOrEmpty(id)) throw Invalid(label);

        DateTime? modified = null;
        if (Str(e, "server_modified") is { } m
            && DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            modified = parsed;
        }

        bool? downloadable = e.TryGetProperty("is_downloadable", out JsonElement d)
            ? d.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

        return new Entry(
            tag,
            id,
            Str(e, "name") ?? string.Empty,
            Str(e, "path_lower"),
            Str(e, "path_display"),
            Long(e, "size"),
            Str(e, "content_hash"),
            Str(e, "rev"),
            downloadable,
            modified);
    }

    private DropboxOptions RequireConfigured()
    {
        if (!IsConfigured)
            throw new CloudStorageApiException("Dropbox is not configured (CloudStorage:Dropbox).", CloudStorageErrorKind.NotConfigured);
        return Dropbox;
    }

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

    private sealed record Account(string AccountId, string? Email, string? DisplayName, string? RootNamespaceId, string? HomeNamespaceId);

    private sealed record Entry(
        string Tag,
        string? Id,
        string Name,
        string? PathLower,
        string? PathDisplay,
        long? Size,
        string? ContentHash,
        string? Rev,
        bool? IsDownloadable,
        DateTime? ServerModified);
}
