using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// YouTube Shorts through the Data API v3 (docs/social-publishing.md). A Short is a normal upload with a vertical shape under
/// the length limit. Scopes: <c>youtube.upload</c> to post and <c>youtube.readonly</c> for <c>videos.list</c> statistics and the
/// channel identity, nothing more. Until Google audits the app its uploads are forced private; the dashboard says so, and a
/// <c>public</c> request is still sent as asked (Google decides). A resumable upload session is opened, then the video is
/// streamed from the object store in ONE request, never held in memory or on disk. Quota: an upload costs 1,600 units and a
/// statistics read 1; both are counted in <see cref="SocialQuotaCounter"/> so reads cannot starve uploads.
/// Stateless; inert without client id and secret.
/// </summary>
public sealed class YouTubeSocialPlatform : ISocialPlatform
{
    public const string HttpClientName = "social-youtube";
    public const int UploadQuotaUnits = 1600;
    public const int ReadQuotaUnits = 1;
    public const string UploadBucket = "youtube.upload";
    public const string ReadBucket = "youtube.read";
    public const string UploadScope = "https://www.googleapis.com/auth/youtube.upload";
    public const string ReadScope = "https://www.googleapis.com/auth/youtube.readonly";

    private const string Name = "YouTube";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SocialPublishingOptions _options;
    private readonly SocialQuotaCounter _quota;
    private readonly TimeProvider _clock;

    public YouTubeSocialPlatform(HttpClient http, IOptions<SocialPublishingOptions> options, SocialQuotaCounter? quota = null, TimeProvider? clock = null)
    {
        _http = http;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
        _quota = quota ?? new SocialQuotaCounter(_clock);
    }

    public SocialPlatformKind Kind => SocialPlatformKind.YouTube;

    public bool IsConfigured => _options.IsConfigured(Kind);

    public IReadOnlyList<string> Scopes { get; } = [UploadScope, ReadScope];

    private YouTubeSocialOptions Cfg => _options.YouTube;

    // ------------------------------------------------------------------ OAuth

    public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri)
    {
        EnsureConfigured();
        string query = string.Join('&',
            $"client_id={Uri.EscapeDataString(Cfg.ClientId!)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri.AbsoluteUri)}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(string.Join(' ', Scopes))}",
            "access_type=offline",
            "prompt=consent",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(codeChallenge)}",
            "code_challenge_method=S256");
        return new Uri($"{Cfg.AuthorizeUrl}?{query}");
    }

    public Task<SocialTokenSet> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) =>
        TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
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
        if (!_quota.TryTake(ReadBucket, ReadQuotaUnits, Cfg.MetricsQuotaUnitsPerDay))
            throw new SocialApiException("YouTube read quota for today is used up.", SocialErrorKind.RateLimited);
        using HttpRequestMessage request = Authorized(HttpMethod.Get, $"{Cfg.ApiBaseUrl}/youtube/v3/channels?part=snippet&mine=true", accessToken);
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, ClassifyGoogle, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, false, ct);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new SocialApiException("This Google account has no YouTube channel.", SocialErrorKind.NotFound);
        JsonElement channel = items[0];
        string? id = SocialHttp.GetString(channel, "id");
        if (string.IsNullOrEmpty(id)) throw new SocialApiException("YouTube sent no channel id.", SocialErrorKind.InvalidResponse);
        string? avatar = null;
        string? title = null;
        if (channel.TryGetProperty("snippet", out JsonElement snippet))
        {
            title = SocialHttp.GetString(snippet, "title");
            if (snippet.TryGetProperty("thumbnails", out JsonElement thumbs) && thumbs.TryGetProperty("default", out JsonElement def))
                avatar = SocialHttp.GetString(def, "url");
        }

        return new SocialAccountInfo(id, title, avatar);
    }

    public async Task RevokeAsync(SocialTokenSet tokens, CancellationToken ct)
    {
        EnsureConfigured();
        string token = tokens.RefreshToken ?? tokens.AccessToken;
        using HttpRequestMessage request = new(HttpMethod.Post, Cfg.RevokeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token })
        };
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, null, ct);
    }

    // ------------------------------------------------------------------ publish

    public async Task<SocialPublishResult> PublishAsync(string accessToken, SocialPublishRequest request, CancellationToken ct)
    {
        EnsureConfigured();
        // Our own share of the Google project's quota. A refusal here happens before anything is sent.
        int uploadLimit = Math.Max(UploadQuotaUnits, Cfg.QuotaUnitsPerDay - Cfg.MetricsQuotaUnitsPerDay);
        if (!_quota.TryTake(UploadBucket, UploadQuotaUnits, uploadLimit))
            throw new SocialApiException("YouTube upload quota for today is used up.", SocialErrorKind.RateLimited);

        Uri session = await OpenSessionAsync(accessToken, request, ct);

        await using Stream video = await request.OpenVideoAsync(ct);
        using HttpRequestMessage put = new(HttpMethod.Put, session);
        StreamContent content = new(video);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(SafeContentType(request.ContentType));
        if (request.ContentLength > 0) content.Headers.ContentLength = request.ContentLength;
        put.Content = content;

        // The session URL is self-authorising: no bearer token goes with the bytes. From here on the video may exist.
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, put, Name, true, ct), Name, true, ClassifyGoogle, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, true, ct);
        string? id = SocialHttp.GetString(doc.RootElement, "id");
        if (string.IsNullOrEmpty(id))
            throw new SocialApiException("YouTube accepted the upload but sent no video id.", SocialErrorKind.InvalidResponse, response.StatusCode, postMayExist: true);
        return new SocialPublishResult(id, $"https://www.youtube.com/shorts/{id}");
    }

    private async Task<Uri> OpenSessionAsync(string accessToken, SocialPublishRequest request, CancellationToken ct)
    {
        string privacy = request.Options.TryGetValue("privacy", out string? p) && p is "public" or "unlisted" or "private" ? p! : "private";
        bool ai = request.Options.TryGetValue("aiGenerated", out string? a) && a == "true";
        var metadata = new
        {
            snippet = new
            {
                title = TitleFor(request),
                description = DescriptionFor(request),
                categoryId = "22"
            },
            status = new
            {
                privacyStatus = privacy,
                selfDeclaredMadeForKids = false,
                containsSyntheticMedia = ai,
                embeddable = true
            }
        };

        using HttpRequestMessage init = Authorized(HttpMethod.Post,
            $"{Cfg.ApiBaseUrl}/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status", accessToken);
        init.Content = new StringContent(JsonSerializer.Serialize(metadata, Json), Encoding.UTF8, "application/json");
        init.Headers.TryAddWithoutValidation("X-Upload-Content-Type", SafeContentType(request.ContentType));
        if (request.ContentLength > 0) init.Headers.TryAddWithoutValidation("X-Upload-Content-Length", request.ContentLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, init, Name, false, ct), Name, false, ClassifyGoogle, ct);
        Uri? location = response.Headers.Location;
        if (location is null || !SocialHttp.IsHttps(location.AbsoluteUri, out Uri session))
            throw new SocialApiException("YouTube sent no usable upload address.", SocialErrorKind.InvalidResponse, response.StatusCode);
        return session;
    }

    internal static string TitleFor(SocialPublishRequest request)
    {
        string title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title!
            : (request.Body ?? string.Empty).Split('\n', 2)[0];
        title = title.Replace('<', ' ').Replace('>', ' ').Trim();
        if (title.Length == 0) title = "Untitled";
        return title.Length <= 100 ? title : title[..100].TrimEnd();
    }

    internal static string DescriptionFor(SocialPublishRequest request)
    {
        StringBuilder sb = new((request.Body ?? string.Empty).Replace('<', ' ').Replace('>', ' '));
        if (request.Hashtags.Count > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(string.Join(' ', request.Hashtags.Select(t => t.StartsWith('#') ? t : "#" + t)));
        }

        return sb.Length <= 5000 ? sb.ToString() : sb.ToString(0, 5000);
    }

    // ------------------------------------------------------------------ metrics

    public async Task<SocialMetricsSnapshot> ReadMetricsAsync(string accessToken, string remotePostId, CancellationToken ct)
    {
        EnsureConfigured();
        if (!_quota.TryTake(ReadBucket, ReadQuotaUnits, Cfg.MetricsQuotaUnitsPerDay))
            throw new SocialApiException("YouTube read quota for today is used up.", SocialErrorKind.RateLimited);
        using HttpRequestMessage request = Authorized(HttpMethod.Get,
            $"{Cfg.ApiBaseUrl}/youtube/v3/videos?part=statistics&id={Uri.EscapeDataString(remotePostId)}", accessToken);
        using HttpResponseMessage response = await SocialHttp.EnsureSuccessAsync(
            await SocialHttp.SendAsync(_http, request, Name, false, ct), Name, false, ClassifyGoogle, ct);
        using JsonDocument doc = await SocialHttp.ReadJsonAsync(response, Name, false, ct);
        return ParseStatistics(doc.RootElement);
    }

    /// <summary><c>videos.list</c> statistics. Counts are strings; a hidden like or comment count is absent, which is null, never 0. YouTube gives no shares or saves.</summary>
    internal static SocialMetricsSnapshot ParseStatistics(JsonElement root)
    {
        if (!root.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new SocialApiException("YouTube no longer lists this video.", SocialErrorKind.NotFound);
        if (!items[0].TryGetProperty("statistics", out JsonElement stats))
            return new SocialMetricsSnapshot(null, null, null, null, null, "youtube.videos.list");
        return new SocialMetricsSnapshot(
            SocialHttp.GetCount(stats, "viewCount"),
            SocialHttp.GetCount(stats, "likeCount"),
            SocialHttp.GetCount(stats, "commentCount"),
            null,
            null,
            "youtube.videos.list");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Google reports an exhausted quota as 403 with reason <c>quotaExceeded</c> or <c>dailyLimitExceeded</c> (or <c>rateLimitExceeded</c>): that is a limit, not a refusal.</summary>
    internal static SocialErrorKind? ClassifyGoogle(HttpStatusCode status, string body)
    {
        if ((int)status == 403 && (body.Contains("quotaExceeded", StringComparison.Ordinal)
            || body.Contains("dailyLimitExceeded", StringComparison.Ordinal)
            || body.Contains("rateLimitExceeded", StringComparison.Ordinal)
            || body.Contains("userRateLimitExceeded", StringComparison.Ordinal)))
            return SocialErrorKind.RateLimited;
        return null;
    }

    private static string SafeContentType(string contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out _) ? contentType : "application/octet-stream";

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string accessToken)
    {
        HttpRequestMessage message = new(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return message;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured) throw new SocialApiException("YouTube is not configured.", SocialErrorKind.NotConfigured);
    }
}
