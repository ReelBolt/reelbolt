#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>The YouTube adapter against hand-written fixture answers. No live call, no credential.</summary>
public class SocialYouTubeAdapterTests
{
    private const string SessionUrl = "https://upload.fixture.test/session/abc";

    private static (YouTubeSocialPlatform Platform, FixtureHandler Handler, SocialPublishingOptions Options, SocialQuotaCounter Quota) Make(
        Action<SocialPublishingOptions>? configure = null)
    {
        SocialPublishingOptions options = new();
        options.YouTube.ClientId = "yt-client";
        options.YouTube.ClientSecret = "yt-secret";
        configure?.Invoke(options);
        FixtureHandler handler = new();
        ManualTimeProvider clock = new();
        SocialQuotaCounter quota = new(clock);
        return (new YouTubeSocialPlatform(new HttpClient(handler), Options.Create(options), quota, clock), handler, options, quota);
    }

    private static SocialPublishRequest Request(byte[]? video = null, Dictionary<string, string>? options = null, string? title = "My Short") =>
        new(title, "Body text", ["#one", "two"], (video ?? new byte[3000]).Length, "video/mp4",
            _ => Task.FromResult<Stream>(new MemoryStream(video ?? new byte[3000])),
            options ?? new Dictionary<string, string> { ["privacy"] = "unlisted", ["aiGenerated"] = "true" });

    private static void ExpectUpload(FixtureHandler handler) => handler
        .Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl))
        .Enqueue(HttpStatusCode.OK, "{\"id\":\"vid123\"}");

    [Fact]
    public async Task Upload_HappyPath_OpensResumableSession_ThenStreamsTheVideo()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        ExpectUpload(handler);
        byte[] video = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();

        SocialPublishResult result = await platform.PublishAsync("tok-1", Request(video), CancellationToken.None);

        Assert.Equal("vid123", result.RemotePostId);
        Assert.Equal("https://www.youtube.com/shorts/vid123", result.RemotePostUrl);
        Assert.Equal(2, handler.Seen.Count);
        SeenRequest init = handler.Seen[0];
        Assert.Equal(HttpMethod.Post, init.Method);
        Assert.Contains("uploadType=resumable", init.Uri.Query);
        Assert.Equal("Bearer tok-1", init.Authorization);
        Assert.Equal("5000", init.Headers["X-Upload-Content-Length"]);
        using JsonDocument meta = JsonDocument.Parse(init.Body);
        Assert.Equal("My Short", meta.RootElement.GetProperty("snippet").GetProperty("title").GetString());
        Assert.Equal("Body text\n\n#one #two", meta.RootElement.GetProperty("snippet").GetProperty("description").GetString());
        JsonElement status = meta.RootElement.GetProperty("status");
        Assert.Equal("unlisted", status.GetProperty("privacyStatus").GetString());
        Assert.True(status.GetProperty("containsSyntheticMedia").GetBoolean());
        Assert.False(status.GetProperty("selfDeclaredMadeForKids").GetBoolean());

        SeenRequest put = handler.Seen[1];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal(SessionUrl, put.Uri.AbsoluteUri);
        Assert.Null(put.Authorization); // the session URL is self-authorising: no bearer with the bytes
        Assert.Equal(5000, put.BodyBytes);
    }

    [Fact]
    public async Task Upload_PrivacyDefaultsToPrivate_WhenNotAskedOrUnknown()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        ExpectUpload(handler);
        await platform.PublishAsync("t", Request(options: new Dictionary<string, string> { ["privacy"] = "connections" }), CancellationToken.None);
        using JsonDocument meta = JsonDocument.Parse(handler.Seen[0].Body);
        Assert.Equal("private", meta.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString());
    }

    [Fact]
    public async Task Upload_Unauthorized_IsAnUnauthorizedErrorThatDidNotCreateAPost()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Invalid Credentials secret-body\"}}");

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("expired", Request(), CancellationToken.None));

        Assert.Equal(SocialErrorKind.Unauthorized, ex.Kind);
        Assert.False(ex.PostMayExist);
        Assert.DoesNotContain("expired", ex.Message);
        Assert.DoesNotContain("secret-body", ex.Message);
    }

    [Fact]
    public async Task Upload_AfterA401_RefreshesOnceAndRetriesOnce_ThroughTheConnectionService()
    {
        using EngineFixture fx = new();
        fx.Options.YouTube.ClientId = "yt-client";
        fx.Options.YouTube.ClientSecret = "yt-secret";
        FixtureHandler handler = new();
        handler.Enqueue(HttpStatusCode.Unauthorized)                                                    // first init: token revoked early
            .Enqueue(HttpStatusCode.OK, "{\"access_token\":\"fresh-token\",\"expires_in\":3600}")      // refresh
            .Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl))           // second init
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"vid9\"}");                                          // upload
        YouTubeSocialPlatform platform = new(new HttpClient(handler), Options.Create(fx.Options), new SocialQuotaCounter(), fx.Clock);
        SocialConnectionService connections = new(
            fx.NewDb(), new SocialPlatformRegistry([platform]), Options.Create(fx.Options), fx.Secrets, new SocialRefreshLock(),
            NullLogger<SocialConnectionService>.Instance, fx.Clock);
        SocialAccount account = fx.Account;

        SocialPublishResult result = await connections.CallAsync(account, token => platform.PublishAsync(token, Request(), CancellationToken.None), CancellationToken.None);

        Assert.Equal("vid9", result.RemotePostId);
        Assert.Equal("Bearer access-1", handler.Seen[0].Authorization);
        Assert.Contains("grant_type=refresh_token", handler.Seen[1].Body);
        Assert.Contains("refresh_token=refresh-1", handler.Seen[1].Body);
        Assert.Equal("Bearer fresh-token", handler.Seen[2].Authorization);
    }

    [Fact]
    public async Task Upload_RateLimited_CarriesRetryAfter_AndIsNotAPost()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));

        Assert.Equal(SocialErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
        Assert.False(ex.PostMayExist);
    }

    [Fact]
    public async Task Upload_GoogleQuotaExceeded403_IsRateLimited_NotARefusal()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.Forbidden, "{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}");

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));

        Assert.Equal(SocialErrorKind.RateLimited, ex.Kind);
        Assert.False(ex.PostMayExist);
    }

    [Fact]
    public async Task Upload_QuotaCounter_RefusesBeforeAnyRequest_OnceOurShareIsSpent()
    {
        // 3,600 units a day, 400 kept for reads: exactly two 1,600-unit uploads fit.
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, SocialQuotaCounter quota) = Make(o => o.YouTube.QuotaUnitsPerDay = 3600);
        ExpectUpload(handler);
        ExpectUpload(handler);
        await platform.PublishAsync("t", Request(), CancellationToken.None);
        await platform.PublishAsync("t", Request(), CancellationToken.None);
        int sent = handler.Seen.Count;

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));

        Assert.Equal(SocialErrorKind.RateLimited, ex.Kind);
        Assert.False(ex.PostMayExist);
        Assert.Equal(sent, handler.Seen.Count);
        Assert.Equal(3200, quota.Used(YouTubeSocialPlatform.UploadBucket));
    }

    [Fact]
    public async Task Upload_FailureAfterTheVideoWasSent_MayHaveCreatedAPost()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();

        // 5xx on the upload request.
        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl)).Enqueue(HttpStatusCode.ServiceUnavailable);
        SocialApiException fiveHundred = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.Equal(SocialErrorKind.ServerError, fiveHundred.Kind);
        Assert.True(fiveHundred.PostMayExist);

        // The connection drops while the bytes (or the answer) travel.
        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl)).EnqueueNetworkFailure();
        SocialApiException dropped = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.True(dropped.PostMayExist);

        // A 200 whose body cannot be read, or has no id.
        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl)).Enqueue(HttpStatusCode.OK, "not json");
        SocialApiException garbage = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.Equal(SocialErrorKind.InvalidResponse, garbage.Kind);
        Assert.True(garbage.PostMayExist);
        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl)).Enqueue(HttpStatusCode.OK, "{}");
        SocialApiException noId = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.True(noId.PostMayExist);
    }

    [Fact]
    public async Task Upload_FailureBeforeTheVideoWasSent_DidNotCreateAPost()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.EnqueueNetworkFailure();
        SocialApiException network = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.False(network.PostMayExist);

        handler.Enqueue(HttpStatusCode.OK, "{}"); // no Location header
        SocialApiException noSession = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.Equal(SocialErrorKind.InvalidResponse, noSession.Kind);
        Assert.False(noSession.PostMayExist);

        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri("http://insecure.test/s")); // not https
        SocialApiException insecure = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.False(insecure.PostMayExist);

        // The platform saying no to the upload is a refusal, not an open outcome.
        handler.Enqueue(HttpStatusCode.OK, "{}", r => r.Headers.Location = new Uri(SessionUrl)).Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
        SocialApiException refused = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.Equal(SocialErrorKind.Forbidden, refused.Kind);
        Assert.False(refused.PostMayExist);
    }

    [Fact]
    public async Task Inert_WithoutCredentials()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make(o => { o.YouTube.ClientId = null; o.YouTube.ClientSecret = null; });
        Assert.False(platform.IsConfigured);
        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(), CancellationToken.None));
        Assert.Equal(SocialErrorKind.NotConfigured, ex.Kind);
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task Metrics_ParsesStatisticStrings_AndLeavesWhatYouTubeDoesNotGiveNull()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.OK, "{\"items\":[{\"id\":\"vid123\",\"statistics\":{\"viewCount\":\"1234\",\"likeCount\":\"56\",\"commentCount\":\"7\",\"favoriteCount\":\"0\"}}]}");

        SocialMetricsSnapshot snap = await platform.ReadMetricsAsync("t", "vid123", CancellationToken.None);

        Assert.Equal(1234, snap.Views);
        Assert.Equal(56, snap.Likes);
        Assert.Equal(7, snap.Comments);
        Assert.Null(snap.Shares);
        Assert.Null(snap.Saves);
        Assert.Equal("youtube.videos.list", snap.Source);
        Assert.Contains("part=statistics", handler.Seen[0].Uri.Query);
        Assert.Contains("id=vid123", handler.Seen[0].Uri.Query);
    }

    [Fact]
    public async Task Metrics_HiddenLikeCount_IsNullNotZero()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.OK, "{\"items\":[{\"statistics\":{\"viewCount\":\"10\",\"commentCount\":\"0\"}}]}");

        SocialMetricsSnapshot snap = await platform.ReadMetricsAsync("t", "v", CancellationToken.None);

        Assert.Equal(10, snap.Views);
        Assert.Null(snap.Likes);
        Assert.Equal(0, snap.Comments); // a real zero stays a zero
    }

    [Fact]
    public async Task Metrics_VideoGone_IsNotFound()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.OK, "{\"items\":[]}");
        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.ReadMetricsAsync("t", "v", CancellationToken.None));
        Assert.Equal(SocialErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Metrics_ReadQuota_StopsReadsAtOurShare_WithoutARequest()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make(o => o.YouTube.MetricsQuotaUnitsPerDay = 2);
        handler.Enqueue(HttpStatusCode.OK, "{\"items\":[{\"statistics\":{}}]}").Enqueue(HttpStatusCode.OK, "{\"items\":[{\"statistics\":{}}]}");
        await platform.ReadMetricsAsync("t", "a", CancellationToken.None);
        await platform.ReadMetricsAsync("t", "b", CancellationToken.None);

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.ReadMetricsAsync("t", "c", CancellationToken.None));

        Assert.Equal(SocialErrorKind.RateLimited, ex.Kind);
        Assert.Equal(2, handler.Seen.Count);
    }

    [Fact]
    public void AuthorizationUrl_AsksOnlyForUploadAndReadonly_WithPkceAndOfflineAccess()
    {
        (YouTubeSocialPlatform platform, _, _, _) = Make();
        Uri url = platform.BuildAuthorizationUrl("st", "chal", new Uri("https://reelbolt.ai/api/v1/social/youtube/oauth/callback"));
        string text = url.AbsoluteUri;
        Assert.Contains("youtube.upload", text);
        Assert.Contains("youtube.readonly", text);
        Assert.Contains("access_type=offline", text);
        Assert.Contains("code_challenge=chal", text);
        Assert.Contains("code_challenge_method=S256", text);
        Assert.Contains("state=st", text);
        Assert.Equal(2, platform.Scopes.Count);
    }

    [Fact]
    public async Task Tokens_ExchangeAndRefresh_AreParsed_AndARefusedGrantNeedsReconnect()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.OK, "{\"access_token\":\"a1\",\"refresh_token\":\"r1\",\"expires_in\":3599,\"scope\":\"x y\"}")
            .Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");

        SocialTokenSet set = await platform.ExchangeCodeAsync("code-1", "verifier-1", new Uri("https://reelbolt.ai/cb"), CancellationToken.None);
        Assert.Equal("a1", set.AccessToken);
        Assert.Equal("r1", set.RefreshToken);
        Assert.NotNull(set.AccessTokenExpiresAt);
        Assert.Equal("x y", set.Scope);
        Assert.Contains("code_verifier=verifier-1", handler.Seen[0].Body);
        Assert.Contains("client_secret=yt-secret", handler.Seen[0].Body);

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.RefreshAsync("r1", CancellationToken.None));
        Assert.Equal(SocialErrorKind.Unauthorized, ex.Kind);
        Assert.DoesNotContain("r1", ex.Message);
    }

    [Fact]
    public async Task Account_IsTheChannelId_NeverTheEmail_AndRevokeSendsTheRefreshToken()
    {
        (YouTubeSocialPlatform platform, FixtureHandler handler, _, _) = Make();
        handler.Enqueue(HttpStatusCode.OK, "{\"items\":[{\"id\":\"UC123\",\"snippet\":{\"title\":\"My Channel\",\"thumbnails\":{\"default\":{\"url\":\"https://img.test/a.png\"}}}}]}")
            .Enqueue(HttpStatusCode.OK, "{}");

        SocialAccountInfo info = await platform.GetAccountAsync("t", CancellationToken.None);
        Assert.Equal("UC123", info.AccountId);
        Assert.Equal("My Channel", info.DisplayName);
        Assert.Equal("https://img.test/a.png", info.AvatarUrl);

        await platform.RevokeAsync(new SocialTokenSet("acc", "ref", null, null), CancellationToken.None);
        Assert.Contains("token=ref", handler.Seen[1].Body);
    }
}
