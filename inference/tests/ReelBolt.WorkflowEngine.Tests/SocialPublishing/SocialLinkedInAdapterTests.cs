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
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>The LinkedIn adapter against hand-written fixture answers. No live call, no credential.</summary>
public class SocialLinkedInAdapterTests
{
    private const int Part = LinkedInSocialPlatform.PartSize;

    private static (LinkedInSocialPlatform Platform, FixtureHandler Handler) Make(bool configured = true)
    {
        SocialPublishingOptions options = new();
        if (configured)
        {
            options.LinkedIn.ClientId = "li-client";
            options.LinkedIn.ClientSecret = "li-secret";
        }

        FixtureHandler handler = new();
        return (new LinkedInSocialPlatform(new HttpClient(handler), Options.Create(options), new ManualTimeProvider()), handler);
    }

    private static SocialPublishRequest Request(int size, string? body = "Hello (world) #x", Dictionary<string, string>? options = null) =>
        new("A video", body, ["#launch", "ai video"], size, "video/mp4",
            _ => Task.FromResult<Stream>(new MemoryStream(Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray())),
            options ?? new Dictionary<string, string>());

    private static string InitAnswer(int size)
    {
        string second = size > Part
            ? $",{{\"uploadUrl\":\"https://upload.li.test/p2\",\"firstByte\":{Part},\"lastByte\":{size - 1}}}"
            : string.Empty;
        int firstLast = Math.Min(size, Part) - 1;
        return "{\"value\":{\"video\":\"urn:li:video:V1\",\"uploadToken\":\"utok\",\"uploadInstructions\":[" +
               $"{{\"uploadUrl\":\"https://upload.li.test/p1\",\"firstByte\":0,\"lastByte\":{firstLast}}}{second}]}}}}";
    }

    private static void ExpectUserInfo(FixtureHandler h) => h.Enqueue(HttpStatusCode.OK, "{\"sub\":\"member42\",\"name\":\"Ada\",\"picture\":\"https://img.li/a.png\"}");

    [Fact]
    public async Task Upload_HappyPath_TwoParts_Finalize_ThenPost()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        int size = Part + 1000;
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, InitAnswer(size))
            .Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-1\""))
            .Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag-2\""))
            .Enqueue(HttpStatusCode.OK, "{}")
            .Enqueue(HttpStatusCode.Created, "", r => r.Headers.Add("x-restli-id", "urn:li:share:777"));

        SocialPublishResult result = await platform.PublishAsync("tok", Request(size, options: new Dictionary<string, string> { ["privacy"] = "public" }), CancellationToken.None);

        Assert.Equal("urn:li:share:777", result.RemotePostId);
        Assert.Contains("urn%3Ali%3Ashare%3A777", result.RemotePostUrl);
        Assert.Equal(6, h.Seen.Count);

        SeenRequest init = h.Seen[1];
        Assert.Contains("action=initializeUpload", init.Uri.Query);
        Assert.Equal("Bearer tok", init.Authorization);
        Assert.Equal("202511", init.Headers["LinkedIn-Version"]);
        Assert.Equal("2.0.0", init.Headers["X-Restli-Protocol-Version"]);
        using (JsonDocument doc = JsonDocument.Parse(init.Body))
        {
            JsonElement req = doc.RootElement.GetProperty("initializeUploadRequest");
            Assert.Equal("urn:li:person:member42", req.GetProperty("owner").GetString());
            Assert.Equal(size, req.GetProperty("fileSizeBytes").GetInt64());
        }

        // Parts go to the pre-signed addresses, in order, without the bearer token.
        Assert.Equal("https://upload.li.test/p1", h.Seen[2].Uri.AbsoluteUri);
        Assert.Equal(Part, h.Seen[2].BodyBytes);
        Assert.Null(h.Seen[2].Authorization);
        Assert.Equal("https://upload.li.test/p2", h.Seen[3].Uri.AbsoluteUri);
        Assert.Equal(1000, h.Seen[3].BodyBytes);

        using (JsonDocument fin = JsonDocument.Parse(h.Seen[4].Body))
        {
            JsonElement f = fin.RootElement.GetProperty("finalizeUploadRequest");
            Assert.Equal("urn:li:video:V1", f.GetProperty("video").GetString());
            Assert.Equal("utok", f.GetProperty("uploadToken").GetString());
            Assert.Equal(["etag-1", "etag-2"], f.GetProperty("uploadedPartIds").EnumerateArray().Select(e => e.GetString()).ToArray());
        }

        using JsonDocument post = JsonDocument.Parse(h.Seen[5].Body);
        JsonElement root = post.RootElement;
        Assert.Equal("urn:li:person:member42", root.GetProperty("author").GetString());
        Assert.Equal("PUBLIC", root.GetProperty("visibility").GetString());
        Assert.Equal("PUBLISHED", root.GetProperty("lifecycleState").GetString());
        Assert.Equal("urn:li:video:V1", root.GetProperty("content").GetProperty("media").GetProperty("id").GetString());
        // Reserved characters of the body are escaped; the hashtags stay tags.
        Assert.Equal("Hello \\(world\\) \\#x\n\n#launch #aivideo", root.GetProperty("commentary").GetString());
    }

    [Fact]
    public async Task Post_ConnectionsPrivacy_IsConnectionsVisibility()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""))
            .Enqueue(HttpStatusCode.OK, "{}").Enqueue(HttpStatusCode.Created, "", r => r.Headers.Add("x-restli-id", "urn:li:share:1"));

        await platform.PublishAsync("t", Request(100, options: new Dictionary<string, string> { ["privacy"] = "connections" }), CancellationToken.None);

        using JsonDocument post = JsonDocument.Parse(h.Seen[^1].Body);
        Assert.Equal("CONNECTIONS", post.RootElement.GetProperty("visibility").GetString());
    }

    [Theory]
    [InlineData(null, "CONNECTIONS")]
    [InlineData("unlisted", "CONNECTIONS")]
    [InlineData("private", "CONNECTIONS")]
    [InlineData("connections", "CONNECTIONS")]
    [InlineData("PUBLIC", "CONNECTIONS")]
    [InlineData("public", "PUBLIC")]
    public async Task Visibility_FailsClosed_OnlyAnExplicitPublicIsPublic(string? privacy, string expected)
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""))
            .Enqueue(HttpStatusCode.OK, "{}").Enqueue(HttpStatusCode.Created, "", r => r.Headers.Add("x-restli-id", "urn:li:share:1"));
        Dictionary<string, string> options = privacy is null ? [] : new() { ["privacy"] = privacy };

        await platform.PublishAsync("t", Request(100, options: options), CancellationToken.None);

        using JsonDocument post = JsonDocument.Parse(h.Seen[^1].Body);
        Assert.Equal(expected, post.RootElement.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task HostileHashtagsAndBody_CannotInjectMarkupOrMentions()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""))
            .Enqueue(HttpStatusCode.OK, "{}").Enqueue(HttpStatusCode.Created, "", r => r.Headers.Add("x-restli-id", "urn:li:share:1"));
        SocialPublishRequest request = new("T", "a@{urn:li:organization:1|Evil} [x](y) <b>*_~|\\", ["#ok_tag", "#x]{urn:li:person:9}", "(a)@b", "\\#", "###", "  "], 100, "video/mp4",
            _ => Task.FromResult<Stream>(new MemoryStream(new byte[100])), new Dictionary<string, string>());

        await platform.PublishAsync("t", request, CancellationToken.None);

        using JsonDocument post = JsonDocument.Parse(h.Seen[^1].Body);
        string text = post.RootElement.GetProperty("commentary").GetString()!;
        string[] parts = text.Split("\n\n");
        Assert.Equal("a\\@\\{urn:li:organization:1\\|Evil\\} \\[x\\]\\(y\\) \\<b\\>\\*\\_\\~\\|\\\\", parts[0]);
        Assert.Equal("#ok_tag #xurnliperson9 #ab", parts[1]);
    }

    [Fact]
    public async Task Post_FailureAfterTheFinalRequestWasSent_MayHaveCreatedAPost()
    {
        foreach (Action<FixtureHandler> fail in new Action<FixtureHandler>[]
        {
            h => h.Enqueue(HttpStatusCode.BadGateway),
            h => h.EnqueueNetworkFailure(),
            h => h.Enqueue(HttpStatusCode.Created) // 201 without the post id header
        })
        {
            (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
            ExpectUserInfo(h);
            h.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""))
                .Enqueue(HttpStatusCode.OK, "{}");
            fail(h);

            SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(100), CancellationToken.None));

            Assert.True(ex.PostMayExist);
        }
    }

    [Fact]
    public async Task Failure_BeforeTheFinalRequest_OrAPlainRefusal_DidNotCreateAPost()
    {
        // Upload part fails.
        (LinkedInSocialPlatform p1, FixtureHandler h1) = Make();
        ExpectUserInfo(h1);
        h1.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.InternalServerError);
        Assert.False((await Assert.ThrowsAsync<SocialApiException>(() => p1.PublishAsync("t", Request(100), CancellationToken.None))).PostMayExist);

        // The post request itself is refused with a 4xx.
        (LinkedInSocialPlatform p2, FixtureHandler h2) = Make();
        ExpectUserInfo(h2);
        h2.Enqueue(HttpStatusCode.OK, InitAnswer(100)).Enqueue(HttpStatusCode.OK, "", r => r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"e\""))
            .Enqueue(HttpStatusCode.OK, "{}").Enqueue(HttpStatusCode.UnprocessableEntity, "{\"message\":\"duplicate\"}");
        SocialApiException refused = await Assert.ThrowsAsync<SocialApiException>(() => p2.PublishAsync("t", Request(100), CancellationToken.None));
        Assert.False(refused.PostMayExist);
        Assert.Equal(SocialErrorKind.Forbidden, refused.Kind);

        // An expired token on the post request is Unauthorized (the engine refreshes or asks to reconnect), not a post.
        (LinkedInSocialPlatform p3, FixtureHandler h3) = Make();
        h3.Enqueue(HttpStatusCode.Unauthorized);
        SocialApiException expired = await Assert.ThrowsAsync<SocialApiException>(() => p3.PublishAsync("old", Request(100), CancellationToken.None));
        Assert.Equal(SocialErrorKind.Unauthorized, expired.Kind);
        Assert.False(expired.PostMayExist);
        Assert.DoesNotContain("old", expired.Message);
    }

    [Fact]
    public async Task Upload_RateLimited_CarriesRetryAfter()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(100), CancellationToken.None));

        Assert.Equal(SocialErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
        Assert.False(ex.PostMayExist);
    }

    [Fact]
    public async Task Upload_RangesThatDoNotCoverTheVideo_AreRefusedBeforeAnyBytesAreSent()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, InitAnswer(100).Replace("\"lastByte\":99", "\"lastByte\":10"));

        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(100), CancellationToken.None));

        Assert.Equal(SocialErrorKind.InvalidResponse, ex.Kind);
        Assert.Equal(2, h.Seen.Count);
    }

    [Fact]
    public async Task Metrics_AreNotAvailable_AllNull_AndNothingIsSent()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();

        SocialMetricsSnapshot snap = await platform.ReadMetricsAsync("t", "urn:li:share:1", CancellationToken.None);

        Assert.Null(snap.Views);
        Assert.Null(snap.Likes);
        Assert.Null(snap.Comments);
        Assert.Null(snap.Shares);
        Assert.Null(snap.Saves);
        Assert.Equal("linkedin.unavailable", snap.Source);
        Assert.Empty(h.Seen);
    }

    [Fact]
    public async Task Tokens_SixtyDayExpiry_NoRefreshToken_AndRefusedRefreshNeedsReconnect()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        h.Enqueue(HttpStatusCode.OK, "{\"access_token\":\"a\",\"expires_in\":5184000,\"scope\":\"w_member_social\"}")
            .Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}");

        SocialTokenSet set = await platform.ExchangeCodeAsync("code", "ignored-verifier", new Uri("https://reelbolt.ai/cb"), CancellationToken.None);

        Assert.Null(set.RefreshToken);
        Assert.Equal(TimeSpan.FromDays(60), set.AccessTokenExpiresAt!.Value - new ManualTimeProvider().UtcNow);
        Assert.DoesNotContain("code_verifier", h.Seen[0].Body);
        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.RefreshAsync("r", CancellationToken.None));
        Assert.Equal(SocialErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task Account_IsTheMemberId_AndRevokeSendsCredentials()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make();
        ExpectUserInfo(h);
        h.Enqueue(HttpStatusCode.OK, "");

        SocialAccountInfo info = await platform.GetAccountAsync("t", CancellationToken.None);
        await platform.RevokeAsync(new SocialTokenSet("acc", null, null, null), CancellationToken.None);

        Assert.Equal("member42", info.AccountId);
        Assert.Equal("Ada", info.DisplayName);
        Assert.Contains("token=acc", h.Seen[1].Body);
        Assert.Contains("client_id=li-client", h.Seen[1].Body);
    }

    [Fact]
    public void AuthorizationUrl_AsksForMemberPosting_Only()
    {
        (LinkedInSocialPlatform platform, _) = Make();
        string url = platform.BuildAuthorizationUrl("st", "chal", new Uri("https://reelbolt.ai/cb")).AbsoluteUri;
        Assert.Contains("w_member_social", url);
        Assert.Contains("state=st", url);
        Assert.DoesNotContain("r_organization", url);
        Assert.DoesNotContain("w_organization", url);
    }

    [Fact]
    public async Task Inert_WithoutCredentials()
    {
        (LinkedInSocialPlatform platform, FixtureHandler h) = Make(configured: false);
        Assert.False(platform.IsConfigured);
        SocialApiException ex = await Assert.ThrowsAsync<SocialApiException>(() => platform.PublishAsync("t", Request(100), CancellationToken.None));
        Assert.Equal(SocialErrorKind.NotConfigured, ex.Kind);
        Assert.Empty(h.Seen);
    }
}
