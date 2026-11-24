using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// LinkedIn member posting (docs/social-publishing.md): scope <c>w_member_social</c> (plus <c>openid profile</c> to learn the
/// member id), the Videos API for the upload (parts of at most 4 MB streamed from the object store, one part in memory at a
/// time) and the Posts API for the post. The access token lasts about 60 days and only approved apps get a refresh token: with
/// none, the connection layer marks the account <c>reconnect_needed</c> when the token runs out. Member post analytics are
/// not available to a plain "Share on LinkedIn" app, so metrics are always null here, never 0. LinkedIn's web flow takes no
/// PKCE, so the challenge is not sent. Stateless; inert without client id and secret.
/// </summary>
public sealed class LinkedInSocialPlatform : ISocialPlatform
{
    public const string HttpClientName = "social-linkedin";
    public const int PartSize = 4 * 1024 * 1024;

    private const string Name = "LinkedIn";

    private readonly HttpClient _http;
    private readonly SocialPublishingOptions _options;
    private readonly TimeProvider _clock;

    public LinkedInSocialPlatform(HttpClient http, IOptions<SocialPublishingOptions> options, TimeProvider? clock = null)
    {
        _http = http;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    public SocialPlatformKind Kind => SocialPlatformKind.LinkedIn;

    public bool IsConfigured => _options.IsConfigured(Kind);

    public IReadOnlyList<string> Scopes { get; } = ["openid", "profile", "w_member_social"];

    private LinkedInSocialOptions Cfg => _options.LinkedIn;

    // ------------------------------------------------------------------ OAuth

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri)
    {
        EnsureConfigured();
        string query = string.Join('&',
            "response_type=code",
            $"client_id={Uri.EscapeDataString(Cfg.ClientId!)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"scope={Uri.EscapeDataString(string.Join(' ', Scopes))}");
        return new Uri($"{Cfg.AuthorizeUrl}?{query}");
    }

    public Task<SocialTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri
        }, ct);

    public Task<SocialTokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenRequestAsync(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, ct);

    private async Task<SocialTokenSet> TokenRequestAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        EnsureConfigured();
        form["client_id"] = Cfg.ClientId!;
        form["client_secret"] = Cfg.ClientSecret!;
        using HttpRequestMessage request = new(HttpMethod.Post, Cfg.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, SocialHttp.ClassifyTokenEndpoint, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, false, ct);
        return SocialHttp.ParseTokens(doc.RootElement, Name, _clock);
    }

    public async Task<SocialAccountInfo> GetAccountAsync(string accessToken, CancellationToken ct)
    {
        EnsureConfigured();
        (string id, string? name, string? picture) = await GetMemberAsync(accessToken, ct);
        return new SocialAccountInfo(id, name, picture);
    }

    private async Task<(string Id, string? Name, string? Picture)> GetMemberAsync(string accessToken, CancellationToken ct)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Get, $"{Cfg.ApiBaseUrl}/v2/userinfo", accessToken);
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, null, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, false, ct);
        string? sub = SocialHttp.GetString(doc.RootElement, "sub");
        if (string.IsNullOrEmpty(sub)) throw new SocialApiException("LinkedIn sent no member id.", SocialErrorKind.InvalidResponse);
        return (sub, SocialHttp.GetString(doc.RootElement, "name"), SocialHttp.GetString(doc.RootElement, "picture"));
    }

    public async Task RevokeAsync(SocialTokenSet tokens, CancellationToken ct)
    {
        EnsureConfigured();
        using HttpRequestMessage request = new(HttpMethod.Post, Cfg.RevokeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = Cfg.ClientId!,
                ["client_secret"] = Cfg.ClientSecret!,
                ["token"] = tokens.RefreshToken ?? tokens.AccessToken
            })
        };
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, null, ct);
    }

    // ------------------------------------------------------------------ publish

    public async Task<SocialPublishResult> PublishAsync(string accessToken, SocialPublishRequest request, CancellationToken ct)
    {
        EnsureConfigured();
        (string memberId, _, _) = await GetMemberAsync(accessToken, ct);
        string author = $"urn:li:person:{memberId}";

        (string videoUrn, string uploadToken, List<UploadPart> parts) = await InitializeUploadAsync(accessToken, author, request.ContentLength, ct);

        List<string> etags = await UploadPartsAsync(parts, request, ct);

        await FinalizeUploadAsync(accessToken, videoUrn, uploadToken, etags, ct);

        return await CreatePostAsync(accessToken, author, videoUrn, request, ct);
    }

    private sealed record UploadPart(Uri Url, long FirstByte, long LastByte);

    private async Task<(string VideoUrn, string UploadToken, List<UploadPart> Parts)> InitializeUploadAsync(
        string accessToken, string author, long size, CancellationToken ct)
    {
        if (size <= 0) throw new SocialApiException("The video size is unknown.", SocialErrorKind.InvalidResponse);
        using HttpRequestMessage init = Restli(HttpMethod.Post, $"{Cfg.ApiBaseUrl}/rest/videos?action=initializeUpload", accessToken);
        init.Content = JsonBody(new
        {
            initializeUploadRequest = new { owner = author, fileSizeBytes = size, uploadCaptions = false, uploadThumbnail = false }
        });
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, init, Name, false, ct), Name, false, null, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, false, ct);
        if (!doc.RootElement.TryGetProperty("value", out JsonElement value))
            throw new SocialApiException("LinkedIn sent no upload instructions.", SocialErrorKind.InvalidResponse);
        string? video = SocialHttp.GetString(value, "video");
        string uploadToken = SocialHttp.GetString(value, "uploadToken") ?? string.Empty;
        if (string.IsNullOrEmpty(video) || !value.TryGetProperty("uploadInstructions", out JsonElement instructions) || instructions.ValueKind != JsonValueKind.Array)
            throw new SocialApiException("LinkedIn sent no upload instructions.", SocialErrorKind.InvalidResponse);

        List<UploadPart> parts = [];
        foreach (JsonElement item in instructions.EnumerateArray())
        {
            if (!SocialHttp.IsHttps(SocialHttp.GetString(item, "uploadUrl"), out Uri url))
                throw new SocialApiException("LinkedIn sent an unusable upload address.", SocialErrorKind.InvalidResponse);
            long first = SocialHttp.GetCount(item, "firstByte") ?? 0;
            long last = SocialHttp.GetCount(item, "lastByte") ?? -1;
            if (last < first || last - first + 1 > PartSize)
                throw new SocialApiException("LinkedIn sent an unusable upload range.", SocialErrorKind.InvalidResponse);
            parts.Add(new UploadPart(url, first, last));
        }

        parts.Sort((a, b) => a.FirstByte.CompareTo(b.FirstByte));
        if (parts.Count == 0 || parts[0].FirstByte != 0 || parts[^1].LastByte != size - 1)
            throw new SocialApiException("LinkedIn sent upload ranges that do not cover the video.", SocialErrorKind.InvalidResponse);
        return (video, uploadToken, parts);
    }

    private async Task<List<string>> UploadPartsAsync(List<UploadPart> parts, SocialPublishRequest request, CancellationToken ct)
    {
        List<string> etags = [];
        await using Stream video = await request.OpenVideoAsync(ct);
        byte[] buffer = new byte[PartSize];
        foreach (UploadPart part in parts)
        {
            int length = (int)(part.LastByte - part.FirstByte + 1);
            int read = 0;
            while (read < length)
            {
                int n = await video.ReadAsync(buffer.AsMemory(read, length - read), ct);
                if (n == 0) break;
                read += n;
            }

            if (read != length)
                throw new SocialApiException("The video ended before LinkedIn's upload range did.", SocialErrorKind.InvalidResponse);

            // Pre-signed address: no bearer token goes with the bytes.
            using HttpRequestMessage put = new(HttpMethod.Put, part.Url) { Content = new ByteArrayContent(buffer, 0, length) };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
                await SocialHttp.SendAsync(_http, put, Name, false, ct), Name, false, null, ct);
            string? etag = response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out IEnumerable<string>? raw) ? raw.FirstOrDefault() : null);
            if (string.IsNullOrEmpty(etag))
                throw new SocialApiException("LinkedIn sent no part id for an uploaded part.", SocialErrorKind.InvalidResponse);
            etags.Add(etag.Trim('"'));
        }

        return etags;
    }

    private async Task FinalizeUploadAsync(string accessToken, string videoUrn, string uploadToken, List<string> etags, CancellationToken ct)
    {
        using HttpRequestMessage finalize = Restli(HttpMethod.Post, $"{Cfg.ApiBaseUrl}/rest/videos?action=finalizeUpload", accessToken);
        finalize.Content = JsonBody(new { finalizeUploadRequest = new { video = videoUrn, uploadToken, uploadedPartIds = etags } });
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, finalize, Name, false, ct), Name, false, null, ct);
    }

    private async Task<SocialPublishResult> CreatePostAsync(string accessToken, string author, string videoUrn, SocialPublishRequest request, CancellationToken ct)
    {
        string visibility = request.Options.TryGetValue("privacy", out string? p) && p == "public" ? "PUBLIC" : "CONNECTIONS"; // fail closed: only an explicit "public" is public
        var post = new
        {
            author,
            commentary = CommentaryFor(request),
            visibility,
            distribution = new { feedDistribution = "MAIN_FEED", targetEntities = Array.Empty<string>(), thirdPartyDistributionChannels = Array.Empty<string>() },
            content = new { media = new { title = TitleFor(request), id = videoUrn } },
            lifecycleState = "PUBLISHED",
            isReshareDisabledByAuthor = false
        };

        using HttpRequestMessage create = Restli(HttpMethod.Post, $"{Cfg.ApiBaseUrl}/rest/posts", accessToken);
        create.Content = JsonBody(post);
        // This is the final request: from the moment it is sent the post may exist.
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, create, Name, true, ct), Name, true, null, ct);
        string? id = response.Headers.TryGetValues("x-restli-id", out IEnumerable<string>? ids) ? ids.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(id))
            throw new SocialApiException("LinkedIn created the post but sent no post id.", SocialErrorKind.InvalidResponse, response.StatusCode, postMayExist: true);
        return new SocialPublishResult(id, $"https://www.linkedin.com/feed/update/{Uri.EscapeDataString(id)}/");
    }

    private static string TitleFor(SocialPublishRequest request)
    {
        string title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title! : (request.Body ?? string.Empty).Split('\n', 2)[0];
        title = title.Trim();
        if (title.Length == 0) title = "Video";
        return title.Length <= 200 ? title : title[..200].TrimEnd();
    }

    /// <summary>The text of the post. LinkedIn's "little text" format reserves <c>| { } @ [ ] ( ) &lt; &gt; # \ * _ ~</c>: they are escaped in the body so they stay plain text; the hashtags are added unescaped so they stay tags.</summary>
    internal static string CommentaryFor(SocialPublishRequest request)
    {
        StringBuilder sb = new();
        foreach (char ch in request.Body ?? string.Empty)
        {
            if ("|{}@[]()<>#\\*_~".Contains(ch)) sb.Append('\\');
            sb.Append(ch);
        }

        if (request.Hashtags.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(string.Join(' ', request.Hashtags.Select(PostTextValidator.NormalizeHashtag).Where(t => t is not null).Select(t => "#" + t)));
        }

        return sb.Length <= 3000 ? sb.ToString() : sb.ToString(0, 3000);
    }

    // ------------------------------------------------------------------ metrics

    /// <summary>Member post analytics need a scope a plain Share-on-LinkedIn app is not granted: every number is null (not available), and nothing is sent.</summary>
    public Task<SocialMetricsSnapshot> ReadMetricsAsync(string accessToken, string remotePostId, CancellationToken ct) =>
        Task.FromResult(new SocialMetricsSnapshot(null, null, null, null, null, "linkedin.unavailable"));

    // ------------------------------------------------------------------ helpers

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private HttpRequestMessage Restli(HttpMethod method, string url, string accessToken)
    {
        HttpRequestMessage message = Authorized(method, url, accessToken);
        message.Headers.TryAddWithoutValidation("LinkedIn-Version", Cfg.ApiVersion);
        message.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        return message;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        HttpRequestMessage message = new(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return message;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new SocialApiException("LinkedIn is not configured.", SocialErrorKind.NotConfigured);
    }
}
