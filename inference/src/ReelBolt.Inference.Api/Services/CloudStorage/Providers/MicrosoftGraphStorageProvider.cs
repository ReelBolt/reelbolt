using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage.Providers;

/// <summary>
/// OneDrive (personal and for Business) and SharePoint document libraries through Microsoft Graph v1.0,
/// read-only (docs/design/cloud-drive-sync.md, "OneDrive / SharePoint specifics"). Every request goes
/// through <see cref="CloudHttp.SendAsync"/>. Microsoft rotates refresh tokens, so a refresh returns the
/// new one. A personal Microsoft account cannot call <c>/me/followedSites</c>; that answer means "no
/// SharePoint", not an error. Downloads answer 302 to a pre-authenticated URL on another host: the named
/// client never follows redirects, and the second request is a new one that carries no bearer token.
/// The constructor signature is fixed — the DI registration depends on it.
/// </summary>
public sealed class MicrosoftGraphStorageProvider : ICloudStorageProvider
{
    public const string HttpClientName = "CloudStorage.OneDrive";

    internal const string Scopes = "offline_access User.Read Files.Read.All Sites.Read.All";
    internal const string PersonalDriveName = "OneDrive";

    private const string ItemFields = "id,name,size,file,folder,package,remoteItem,root,cTag,eTag,lastModifiedDateTime,parentReference";
    private const string ChildFields = "id,name,size,file,folder,package,remoteItem,cTag,eTag,lastModifiedDateTime,parentReference";
    private const string PageSize = "200";

    /// <summary>Bounds the breadth-first walk even when a tree is all folders and no files.</summary>
    private const int MaxFoldersPerTree = 10_000;

    /// <summary>How many children the picker reads from one folder before it stops and reports truncation.</summary>
    private const int MaxPickerChildren = 10_000;

    /// <summary>Bounds any one paged collection (sites, libraries) against a server that pages forever.</summary>
    private const int MaxPagesPerCollection = 500;

    private readonly HttpClient _http;
    private readonly CloudStorageOptions _options;
    private readonly ILogger<MicrosoftGraphStorageProvider> _logger;

    public MicrosoftGraphStorageProvider(HttpClient http, IOptions<CloudStorageOptions> options, ILogger<MicrosoftGraphStorageProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public CloudStorageProviderKind Kind => CloudStorageProviderKind.OneDrive;

    public bool IsConfigured => _options.OneDrive.IsConfigured;

    private MicrosoftGraphOptions Ms => _options.OneDrive;

    // ------------------------------------------------------------------ OAuth

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri)
    {
        MicrosoftGraphOptions o = RequireConfigured();
        string query = Query(
            ("client_id", o.ClientId),
            ("response_type", "code"),
            ("redirect_uri", redirectUri.AbsoluteUri),
            ("response_mode", "query"),
            ("scope", Scopes),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"));
        return new Uri(AuthorityUrl("authorize") + "?" + query);
    }

    public Task<CloudTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        MicrosoftGraphOptions o = RequireConfigured();
        return TokenRequestAsync(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", codeVerifier),
                new("redirect_uri", redirectUri.AbsoluteUri),
                new("scope", Scopes),
                new("client_id", o.ClientId!),
                new("client_secret", o.ClientSecret!),
            ],
            "POST oauth2/v2.0/token (authorization_code)",
            ct);
    }

    public Task<CloudTokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        MicrosoftGraphOptions o = RequireConfigured();
        // Microsoft rotates refresh tokens: the result carries the new one, which the caller stores.
        return TokenRequestAsync(
            [
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
                new("scope", Scopes),
                new("client_id", o.ClientId!),
                new("client_secret", o.ClientSecret!),
            ],
            "POST oauth2/v2.0/token (refresh_token)",
            ct);
    }

    public async Task<CloudAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        const string label = "GET /me";
        string url = Graph("/me", ("$select", "id,displayName,mail,userPrincipalName"));
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        JsonElement root = doc.RootElement;

        string? id = Str(root, "id");
        if (string.IsNullOrEmpty(id)) throw Invalid(label);
        string? mail = Str(root, "mail");
        string? email = !string.IsNullOrWhiteSpace(mail) ? mail : Str(root, "userPrincipalName");
        return new CloudAccountInfo(id, string.IsNullOrWhiteSpace(email) ? null : email, Str(root, "displayName"));
    }

    /// <summary>The Microsoft identity platform has no revocation endpoint for v2.0 tokens: nothing to do.</summary>
    public Task RevokeAsync(CloudTokenSet tokens, CancellationToken ct) => Task.CompletedTask;

    // ------------------------------------------------------------------ browsing

    public async Task<IReadOnlyList<CloudDrive>> ListDrivesAsync(string accessToken, CancellationToken ct)
    {
        const string driveLabel = "GET /me/drive";
        string driveUrl = Graph("/me/drive", ("$select", "id,name,driveType"));
        using (HttpResponseMessage response = await GetAsync(accessToken, driveUrl, driveLabel, ct))
        using (JsonDocument doc = await ReadJsonAsync(response, driveLabel, ct))
        {
            if (string.IsNullOrEmpty(Str(doc.RootElement, "id"))) throw Invalid(driveLabel);
        }

        var drives = new List<CloudDrive> { new(null, PersonalDriveName, CloudDriveKind.Personal) };

        List<(string Id, string Name)> sites;
        try
        {
            sites = await CollectAsync(
                accessToken,
                Graph("/me/followedSites", ("$select", "id,displayName")),
                "GET /me/followedSites",
                (e, label) =>
                {
                    string? id = Str(e, "id");
                    if (string.IsNullOrEmpty(id)) throw Invalid(label);
                    string? name = Str(e, "displayName");
                    return (id, string.IsNullOrWhiteSpace(name) ? id : name);
                },
                ct);
        }
        catch (CloudStorageApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // A personal Microsoft account has no SharePoint: followedSites is refused. Not an error.
            _logger.LogDebug("Microsoft Graph followedSites is not available for this account (HTTP {Status}); listing OneDrive only.", (int?)ex.StatusCode);
            return drives;
        }

        foreach ((string siteId, string siteName) in sites)
        {
            List<(string Id, string Name)> libraries;
            try
            {
                libraries = await CollectAsync(
                    accessToken,
                    Graph($"/sites/{CloudHttp.Esc(siteId)}/drives", ("$select", "id,name,driveType")),
                    "GET /sites/{id}/drives",
                    (e, label) =>
                    {
                        string? id = Str(e, "id");
                        if (string.IsNullOrEmpty(id)) throw Invalid(label);
                        string? name = Str(e, "name");
                        return (id, string.IsNullOrWhiteSpace(name) ? id : name);
                    },
                    ct);
            }
            catch (CloudStorageApiException ex) when (ex.Kind is CloudStorageErrorKind.Forbidden or CloudStorageErrorKind.NotFound)
            {
                // A followed site the person can no longer open: leave it out rather than break the picker.
                _logger.LogDebug("Skipping a followed SharePoint site whose libraries are not visible (HTTP {Status}).", (int?)ex.StatusCode);
                continue;
            }

            foreach ((string libraryId, string libraryName) in libraries)
                drives.Add(new CloudDrive(libraryId, $"{siteName} — {libraryName}", CloudDriveKind.SharePointLibrary));
        }

        return drives;
    }

    public async Task<CloudFolderInfo> GetFolderAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        string driveName = await GetDriveNameAsync(accessToken, location.DriveId, ct);
        DriveItem folder = await GetFolderItemAsync(accessToken, location, ct);

        if (folder.IsRoot || IsDriveRoot(location)) return new CloudFolderInfo(location.FolderId, driveName, driveName);

        string parent = ParentDisplayPath(folder.ParentPath);
        return new CloudFolderInfo(location.FolderId, folder.Name, driveName + parent + "/" + folder.Name);
    }

    public async Task<CloudFolderPage> ListFolderAsync(string accessToken, CloudLocation location, int maxItems, CancellationToken ct)
    {
        if (maxItems < 0) maxItems = 0;
        CloudFolderInfo folder = await GetFolderAsync(accessToken, location, ct);

        var folders = new List<DriveItem>();
        var files = new List<DriveItem>();
        int read = 0;
        bool truncated = false;
        string? url = Graph(ItemPath(location) + "/children", ("$select", ChildFields), ("$top", PageSize));
        while (url is not null)
        {
            (List<DriveItem> page, string? next) = await GetChildrenPageAsync(accessToken, url, ct);
            foreach (DriveItem item in page)
            {
                read++;
                if (item.IsPackage || item.IsRemote) continue; // nothing the sync could pull
                if (item.IsFolder) folders.Add(item);
                else if (item.IsFile) files.Add(item);
            }

            if (next is not null && read >= MaxPickerChildren)
            {
                truncated = true;
                break;
            }

            url = next;
        }

        var items = new List<CloudItem>(folders.Count + files.Count);
        foreach (DriveItem f in SortByName(folders))
            items.Add(new CloudItem(f.Id, f.Name, true, null, f.ModifiedAt, null));
        foreach (DriveItem f in SortByName(files))
            items.Add(new CloudItem(f.Id, f.Name, false, f.Size ?? 0, f.ModifiedAt, f.MimeType));

        if (items.Count > maxItems)
        {
            items.RemoveRange(maxItems, items.Count - maxItems);
            truncated = true;
        }

        return new CloudFolderPage(folder, items, truncated);
    }

    // ------------------------------------------------------------------ syncing

    public async Task<CloudTreeListing> ListTreeAsync(string accessToken, CloudLocation root, int maxEntries, CancellationToken ct)
    {
        // Validate the root first: a folder that was deleted (or is now a file) must fail the sync, not
        // list as empty — an empty listing would make the sync delete every file it pulled from it.
        DriveItem rootItem = await GetFolderItemAsync(accessToken, root, ct);

        string driveBase = DriveBase(root.DriveId);
        var queue = new Queue<(string ItemPath, string Prefix)>();
        queue.Enqueue((ItemPath(root), string.Empty));
        var visitedFolders = new HashSet<string>(StringComparer.Ordinal) { rootItem.Id };
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<CloudFileEntry>();
        int unsupported = 0;
        bool truncated = false;

        while (queue.Count > 0 && !truncated)
        {
            (string itemPath, string prefix) = queue.Dequeue();
            string? url = Graph(itemPath + "/children", ("$select", ChildFields), ("$top", PageSize));
            while (url is not null && !truncated)
            {
                (List<DriveItem> children, string? next) = await GetChildrenPageAsync(accessToken, url, ct);

                foreach (DriveItem item in children)
                {
                    // OneNote notebooks (package) and shared-with-me shortcuts (remoteItem) are never
                    // followed: no downloadable content, and a shortcut could leave the folder or loop.
                    if (!IsSafeName(item.Name) || item.IsPackage || item.IsRemote) { unsupported++; continue; }

                    if (item.IsFolder)
                    {
                        if (!visitedFolders.Add(item.Id)) continue;
                        if (visitedFolders.Count > MaxFoldersPerTree) { truncated = true; break; }
                        queue.Enqueue(($"{driveBase}/items/{CloudHttp.Esc(item.Id)}", prefix + item.Name + "/"));
                        continue;
                    }

                    string? revision = !string.IsNullOrEmpty(item.CTag) ? item.CTag : item.ETag;
                    string relativePath = prefix + item.Name;
                    if (!item.IsFile || string.IsNullOrEmpty(revision) || !paths.Add(relativePath))
                    {
                        // No content, no revision to compare, or a second item under a path already taken.
                        unsupported++;
                        continue;
                    }

                    if (files.Count >= maxEntries) { truncated = true; break; }
                    files.Add(new CloudFileEntry(item.Id, relativePath, item.Size ?? 0, revision, item.MimeType));
                }

                url = next;
            }
        }

        return new CloudTreeListing(files, truncated, unsupported);
    }

    public async Task<CloudDownload> OpenDownloadAsync(string accessToken, CloudLocation root, CloudFileEntry file, CancellationToken ct)
    {
        const string label = "GET /items/{id}/content";
        string url = Graph($"{DriveBase(root.DriveId)}/items/{CloudHttp.Esc(file.ItemId)}/content");

        HttpResponseMessage response = await CloudHttp.SendAsync(
            _http,
            () => BearerGet(url, accessToken),
            label,
            ct,
            completion: HttpCompletionOption.ResponseHeadersRead,
            returnRedirects: true);

        if (IsRedirect(response.StatusCode))
        {
            Uri? location = response.Headers.Location;
            response.Dispose();

            // The Location is a pre-authenticated URL on another host (it carries its own short-lived
            // token, so it is never logged). It gets a NEW request with no Authorization header: the
            // bearer token must never reach a host that is not Graph.
            if (location is null || !location.IsAbsoluteUri || location.Scheme != Uri.UriSchemeHttps)
            {
                throw new CloudStorageApiException(
                    $"{label} redirected to a location that is not an absolute https URL.",
                    CloudStorageErrorKind.InvalidResponse,
                    HttpStatusCode.Redirect);
            }

            response = await CloudHttp.SendAsync(
                _http,
                () => new HttpRequestMessage(HttpMethod.Get, location),
                "GET pre-authenticated download URL",
                ct,
                completion: HttpCompletionOption.ResponseHeadersRead);
        }

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

    // ------------------------------------------------------------------ helpers: Graph calls

    private async Task<string> GetDriveNameAsync(string accessToken, string? driveId, CancellationToken ct)
    {
        if (driveId is null) return PersonalDriveName;

        const string label = "GET /drives/{id}";
        string url = Graph($"/drives/{CloudHttp.Esc(driveId)}", ("$select", "id,name"));
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);
        string? name = Str(doc.RootElement, "name");
        return string.IsNullOrWhiteSpace(name) ? driveId : name;
    }

    /// <summary>The folder named by <paramref name="location"/>; NotFound unless it exists and carries the <c>folder</c> facet.</summary>
    private async Task<DriveItem> GetFolderItemAsync(string accessToken, CloudLocation location, CancellationToken ct)
    {
        const string label = "GET /items/{id}";
        string url = Graph(ItemPath(location), ("$select", ItemFields));
        DriveItem item;
        using (HttpResponseMessage response = await GetAsync(accessToken, url, label, ct))
        using (JsonDocument doc = await ReadJsonAsync(response, label, ct))
        {
            item = ParseItem(doc.RootElement, label);
        }

        if (!item.IsFolder || item.IsPackage || item.IsRemote)
        {
            throw new CloudStorageApiException(
                $"{label}: the item is not a folder of the selected drive.",
                CloudStorageErrorKind.NotFound,
                HttpStatusCode.NotFound);
        }

        return item;
    }

    private async Task<(List<DriveItem> Items, string? NextLink)> GetChildrenPageAsync(string accessToken, string url, CancellationToken ct)
    {
        const string label = "GET /items/{id}/children";
        using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
        using JsonDocument doc = await ReadJsonAsync(response, label, ct);

        var items = new List<DriveItem>();
        if (doc.RootElement.TryGetProperty("value", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw Invalid(label);
            foreach (JsonElement e in list.EnumerateArray()) items.Add(ParseItem(e, label));
        }

        return (items, NextLink(doc.RootElement, url, label));
    }

    /// <summary>Every item of a paged Graph collection (<c>value</c> + <c>@odata.nextLink</c>).</summary>
    private async Task<List<T>> CollectAsync<T>(
        string accessToken, string firstUrl, string label, Func<JsonElement, string, T> parse, CancellationToken ct)
    {
        var result = new List<T>();
        string? url = firstUrl;
        for (int page = 0; url is not null; page++)
        {
            if (page >= MaxPagesPerCollection) throw Invalid(label);
            using HttpResponseMessage response = await GetAsync(accessToken, url, label, ct);
            using JsonDocument doc = await ReadJsonAsync(response, label, ct);
            if (doc.RootElement.TryGetProperty("value", out JsonElement list))
            {
                if (list.ValueKind != JsonValueKind.Array) throw Invalid(label);
                foreach (JsonElement e in list.EnumerateArray()) result.Add(parse(e, label));
            }

            url = NextLink(doc.RootElement, url, label);
        }

        return result;
    }

    /// <summary>
    /// The next page's URL, which Graph gives absolute. It is followed only when it is https on the
    /// configured Graph host: a link elsewhere would send the bearer token to that host.
    /// </summary>
    private string? NextLink(JsonElement root, string previous, string label)
    {
        if (!root.TryGetProperty("@odata.nextLink", out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid(label);
        string? next = value.GetString();
        if (string.IsNullOrEmpty(next)) return null;

        if (!Uri.TryCreate(next, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(Ms.GraphBaseUrl, UriKind.Absolute, out Uri? graph)
            || !string.Equals(uri.Host, graph.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != graph.Port)
        {
            throw new CloudStorageApiException(
                $"{label} returned a next-page link that is not https on the Graph host; it was not followed.",
                CloudStorageErrorKind.InvalidResponse);
        }

        if (string.Equals(uri.AbsoluteUri, previous, StringComparison.Ordinal)) throw Invalid(label); // would loop forever
        return uri.AbsoluteUri;
    }

    private Task<HttpResponseMessage> GetAsync(string accessToken, string url, string label, CancellationToken ct) =>
        CloudHttp.SendAsync(_http, () => BearerGet(url, accessToken), label, ct);

    private static HttpRequestMessage BearerGet(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<CloudTokenSet> TokenRequestAsync(KeyValuePair<string, string>[] form, string label, CancellationToken ct)
    {
        string tokenUrl = AuthorityUrl("token");
        using HttpResponseMessage response = await CloudHttp.SendAsync(
            _http,
            () => new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) },
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

    /// <summary>
    /// A refused code or refresh token (<c>invalid_grant</c>, or <c>interaction_required</c> when the
    /// tenant now demands MFA/consent) means the person must connect again.
    /// </summary>
    internal static CloudStorageErrorKind? ClassifyTokenError(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) || string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && Str(doc.RootElement, "error") is "invalid_grant" or "interaction_required")
            {
                return CloudStorageErrorKind.Unauthorized;
            }
        }
        catch (JsonException)
        {
            // Not JSON: default mapping.
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers: mapping

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private static bool IsSafeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Any(char.IsControl);

    private static bool IsDriveRoot(CloudLocation location) =>
        location.FolderId is null || location.FolderId == "root";

    /// <summary><c>/me/drive</c> for the account's own OneDrive, <c>/drives/{id}</c> for any other drive.</summary>
    private static string DriveBase(string? driveId) =>
        driveId is null ? "/me/drive" : $"/drives/{CloudHttp.Esc(driveId)}";

    private static string ItemPath(CloudLocation location) =>
        IsDriveRoot(location)
            ? DriveBase(location.DriveId) + "/root"
            : $"{DriveBase(location.DriveId)}/items/{CloudHttp.Esc(location.FolderId!)}";

    /// <summary>
    /// The parent's path inside its drive from <c>parentReference.path</c> (<c>/drive/root:/A/B</c> or
    /// <c>/drives/{id}/root:/A/B</c>, percent-encoded): <c>/A/B</c>, or empty for the drive's root.
    /// </summary>
    internal static string ParentDisplayPath(string? parentPath)
    {
        if (string.IsNullOrEmpty(parentPath)) return string.Empty;
        int marker = parentPath.IndexOf("root:", StringComparison.Ordinal);
        if (marker < 0 || !(parentPath.StartsWith("/drive/", StringComparison.Ordinal) || parentPath.StartsWith("/drives/", StringComparison.Ordinal)))
            return string.Empty;

        string rest = parentPath[(marker + "root:".Length)..];
        try
        {
            rest = Uri.UnescapeDataString(rest);
        }
        catch (UriFormatException)
        {
            // Leave it as Graph sent it.
        }

        rest = rest.TrimEnd('/');
        if (rest.Length > 0 && rest[0] != '/') rest = "/" + rest;
        return rest;
    }

    private static IEnumerable<DriveItem> SortByName(IEnumerable<DriveItem> items) =>
        items.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal);

    private static DriveItem ParseItem(JsonElement e, string label)
    {
        if (e.ValueKind != JsonValueKind.Object) throw Invalid(label);
        string? id = Str(e, "id");
        if (string.IsNullOrEmpty(id)) throw Invalid(label);

        bool isFile = e.TryGetProperty("file", out JsonElement file) && file.ValueKind == JsonValueKind.Object;

        DateTime? modified = null;
        if (Str(e, "lastModifiedDateTime") is { } m
            && DateTime.TryParse(m, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            modified = parsed;
        }

        string? parentPath = e.TryGetProperty("parentReference", out JsonElement parent) ? Str(parent, "path") : null;

        return new DriveItem(
            id,
            Str(e, "name") ?? string.Empty,
            Long(e, "size"),
            IsFolder: Facet(e, "folder"),
            IsFile: isFile,
            IsPackage: Facet(e, "package"),
            IsRemote: Facet(e, "remoteItem"),
            IsRoot: Facet(e, "root"),
            CTag: Str(e, "cTag"),
            ETag: Str(e, "eTag"),
            ModifiedAt: modified,
            MimeType: isFile ? Str(file, "mimeType") : null,
            ParentPath: parentPath);
    }

    private static bool Facet(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Object;

    // ------------------------------------------------------------------ helpers: plumbing

    private MicrosoftGraphOptions RequireConfigured()
    {
        if (!IsConfigured)
            throw new CloudStorageApiException("OneDrive / SharePoint is not configured (CloudStorage:OneDrive).", CloudStorageErrorKind.NotConfigured);
        return Ms;
    }

    private string AuthorityUrl(string endpoint) =>
        $"{Ms.AuthorityBaseUrl.TrimEnd('/')}/{CloudHttp.Esc(string.IsNullOrWhiteSpace(Ms.Tenant) ? "common" : Ms.Tenant.Trim())}/oauth2/v2.0/{endpoint}";

    /// <summary>A Graph URL. Query keys are constants (OData's <c>$select</c> is sent literally); values are escaped.</summary>
    private string Graph(string path, params (string Key, string? Value)[] query)
    {
        string url = Ms.GraphBaseUrl.TrimEnd('/') + path;
        string q = string.Join("&", query.Where(p => p.Value is not null).Select(p => $"{p.Key}={CloudHttp.Esc(p.Value!)}"));
        return q.Length == 0 ? url : url + "?" + q;
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

    private sealed record DriveItem(
        string Id,
        string Name,
        long? Size,
        bool IsFolder,
        bool IsFile,
        bool IsPackage,
        bool IsRemote,
        bool IsRoot,
        string? CTag,
        string? ETag,
        DateTime? ModifiedAt,
        string? MimeType,
        string? ParentPath);
}
