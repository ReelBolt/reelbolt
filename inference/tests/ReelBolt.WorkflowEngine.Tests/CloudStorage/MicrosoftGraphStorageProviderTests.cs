using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.CloudStorage.Providers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage;

/// <summary>
/// <see cref="MicrosoftGraphStorageProvider"/> against a fake Microsoft identity platform and Graph API
/// (no network, no credentials). In the "CloudHttp" collection because it replaces the process-wide
/// <see cref="CloudHttp.Delay"/>.
/// </summary>
[Collection("CloudHttp")]
public sealed class MicrosoftGraphStorageProviderTests : IDisposable
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private const string ClientSecret = "graph~client~secret~value";
    private const string AccessToken = "eyJ0eXAi.graph-access-token";
    private const string RefreshToken = "M.R3_BAY.refresh-token-value";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string ChildSelect = "id,name,size,file,folder,package,remoteItem,cTag,eTag,lastModifiedDateTime,parentReference";

    private readonly Func<TimeSpan, CancellationToken, Task> _originalDelay;
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeGraphHandler _handler = new();

    public MicrosoftGraphStorageProviderTests()
    {
        _originalDelay = CloudHttp.Delay;
        CloudHttp.Delay = (wait, _) =>
        {
            lock (_delays) _delays.Add(wait);
            return Task.CompletedTask;
        };
    }

    public void Dispose() => CloudHttp.Delay = _originalDelay;

    private MicrosoftGraphStorageProvider Provider(bool configured = true, string tenant = "common")
    {
        var options = new CloudStorageOptions();
        options.OneDrive.Tenant = tenant;
        if (configured)
        {
            options.OneDrive.ClientId = ClientId;
            options.OneDrive.ClientSecret = ClientSecret;
        }

        return new MicrosoftGraphStorageProvider(
            new HttpClient(_handler),
            Options.Create(options),
            NullLogger<MicrosoftGraphStorageProvider>.Instance);
    }

    // ------------------------------------------------------------------ OAuth

    [Fact]
    public void Authorization_url_is_v2_with_pkce_query_response_mode_and_read_only_scopes()
    {
        Uri url = Provider(tenant: "organizations").BuildAuthorizationUrl(
            "state-xyz", "challenge-abc", new Uri("https://reelbolt.test/api/v1/cloud-storage/onedrive/oauth/callback"));

        url.GetLeftPart(UriPartial.Path).Should().Be("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize");
        Dictionary<string, string> q = ParseQuery(url.Query);
        q["client_id"].Should().Be(ClientId);
        q["response_type"].Should().Be("code");
        q["redirect_uri"].Should().Be("https://reelbolt.test/api/v1/cloud-storage/onedrive/oauth/callback");
        q["response_mode"].Should().Be("query");
        q["scope"].Should().Be("offline_access User.Read Files.Read.All Sites.Read.All");
        q["state"].Should().Be("state-xyz");
        q["code_challenge"].Should().Be("challenge-abc");
        q["code_challenge_method"].Should().Be("S256");
        url.AbsoluteUri.Should().NotContain(ClientSecret);
    }

    [Fact]
    public void Unconfigured_provider_refuses_with_NotConfigured()
    {
        MicrosoftGraphStorageProvider provider = Provider(configured: false);

        provider.IsConfigured.Should().BeFalse();
        Action act = () => provider.BuildAuthorizationUrl("s", "c", new Uri("https://reelbolt.test/cb"));
        act.Should().Throw<CloudStorageApiException>().Which.Kind.Should().Be(CloudStorageErrorKind.NotConfigured);
    }

    [Fact]
    public async Task Exchange_posts_a_form_to_the_tenant_token_endpoint()
    {
        _handler.Respond = r => Json("""
            {"token_type":"Bearer","scope":"Files.Read.All Sites.Read.All User.Read","expires_in":3600,"access_token":"at-new","refresh_token":"rt-new"}
            """);

        DateTime before = DateTime.UtcNow;
        CloudTokenSet tokens = await Provider().ExchangeCodeAsync("auth-code-1", "verifier-1", new Uri("https://reelbolt.test/cb"), CancellationToken.None);

        tokens.AccessToken.Should().Be("at-new");
        tokens.RefreshToken.Should().Be("rt-new");
        tokens.Scope.Should().Be("Files.Read.All Sites.Read.All User.Read");
        tokens.AccessTokenExpiresAt.Should().BeOnOrAfter(before.AddSeconds(3600)).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(3600));

        RecordedRequest request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://login.microsoftonline.com/common/oauth2/v2.0/token");
        request.Authorization.Should().BeNull();
        Dictionary<string, string> form = ParseQuery(request.Body!);
        form["grant_type"].Should().Be("authorization_code");
        form["code"].Should().Be("auth-code-1");
        form["code_verifier"].Should().Be("verifier-1");
        form["redirect_uri"].Should().Be("https://reelbolt.test/cb");
        form["client_id"].Should().Be(ClientId);
        form["client_secret"].Should().Be(ClientSecret);
        form["scope"].Should().Be("offline_access User.Read Files.Read.All Sites.Read.All");
    }

    [Fact]
    public async Task Refresh_returns_the_rotated_refresh_token()
    {
        _handler.Respond = r => Json("""{"token_type":"Bearer","expires_in":3600,"access_token":"at-refreshed","refresh_token":"rt-rotated"}""");

        CloudTokenSet tokens = await Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        tokens.AccessToken.Should().Be("at-refreshed");
        tokens.RefreshToken.Should().Be("rt-rotated", "Microsoft rotates refresh tokens and the new one must be stored");
        tokens.AccessTokenExpiresAt.Should().NotBeNull();
        RecordedRequest request = _handler.Requests.Single();
        request.Uri.Query.Should().BeEmpty("no secret goes into a URL");
        Dictionary<string, string> form = ParseQuery(request.Body!);
        form["grant_type"].Should().Be("refresh_token");
        form["refresh_token"].Should().Be(RefreshToken);
        form["client_secret"].Should().Be(ClientSecret);
    }

    [Fact]
    public async Task A_refused_refresh_token_is_Unauthorized_and_the_message_carries_no_secret()
    {
        _handler.Respond = r => Json(
            $$"""{"error":"invalid_grant","error_description":"AADSTS70000: The refresh token {{RefreshToken}} was revoked. {{ClientSecret}}"}""",
            HttpStatusCode.BadRequest);

        Func<Task> act = () => Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        ex.ToString().Should().NotContain(RefreshToken).And.NotContain(ClientSecret);
        _handler.Requests.Should().HaveCount(1, "a refused grant is not retried");
    }

    [Fact]
    public async Task Secrets_never_appear_in_exception_messages()
    {
        // A token endpoint that fails and echoes everything it was sent.
        _handler.Respond = r => Json($$"""{"error":"server_error","echo":"{{r.Body}}"}""", HttpStatusCode.InternalServerError);
        Func<Task> exchange = () => Provider().ExchangeCodeAsync("the-auth-code", "the-verifier", new Uri("https://reelbolt.test/cb"), CancellationToken.None);
        CloudStorageApiException ex1 = (await exchange.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex1.Kind.Should().Be(CloudStorageErrorKind.ServerError);

        // A Graph answer that is not JSON at all, quoting the bearer token.
        _handler.Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{" + AccessToken) };
        Func<Task> account = () => Provider().GetAccountAsync(AccessToken, CancellationToken.None);
        CloudStorageApiException ex2 = (await account.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex2.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);

        // A redirect to a plain-http URL carrying a pre-authentication token.
        _handler.Respond = r => Redirect("http://contoso.sharepoint.com/download.aspx?tempauth=temp-auth-secret");
        Func<Task> download = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), new CloudFileEntry("f", "a.txt", 1, "c", null), CancellationToken.None);
        CloudStorageApiException ex3 = (await download.Should().ThrowAsync<CloudStorageApiException>()).Which;

        // A 401 on a listing.
        _handler.Respond = r => Json("""{"error":{"code":"InvalidAuthenticationToken","message":"Access token has expired."}}""", HttpStatusCode.Unauthorized);
        Func<Task> list = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 10, CancellationToken.None);
        CloudStorageApiException ex4 = (await list.Should().ThrowAsync<CloudStorageApiException>()).Which;

        foreach (CloudStorageApiException ex in new[] { ex1, ex2, ex3, ex4 })
        {
            string text = ex.ToString();
            foreach (string secret in new[] { ClientSecret, AccessToken, RefreshToken, "the-auth-code", "the-verifier", "temp-auth-secret" })
                text.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Account_is_the_graph_id_and_email_falls_back_to_the_user_principal_name()
    {
        _handler.Respond = r => Json("""{"id":"48d31887-5fad-4d73-a9f5-3c356e68a038","displayName":"Ann Lee","mail":null,"userPrincipalName":"ann@contoso.onmicrosoft.com"}""");

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.Should().Be(new CloudAccountInfo("48d31887-5fad-4d73-a9f5-3c356e68a038", "ann@contoso.onmicrosoft.com", "Ann Lee"));
        RecordedRequest request = _handler.Requests.Single();
        request.Path.Should().Be("/v1.0/me");
        ParseQuery(request.Uri.Query)["$select"].Should().Be("id,displayName,mail,userPrincipalName");
        request.Authorization.Should().Be("Bearer " + AccessToken);

        _handler.Respond = r => Json("""{"id":"u2","displayName":"Bo","mail":"bo@contoso.com","userPrincipalName":"bo_contoso.com#EXT#@x.onmicrosoft.com"}""");
        (await Provider().GetAccountAsync(AccessToken, CancellationToken.None)).Email.Should().Be("bo@contoso.com");
    }

    [Fact]
    public async Task Revoke_is_a_no_op()
    {
        await Provider().RevokeAsync(new CloudTokenSet(AccessToken, RefreshToken, null, null), CancellationToken.None);

        _handler.Requests.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ browsing

    [Fact]
    public async Task Drives_of_a_personal_account_tolerate_a_refused_followedSites()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/me/drive" => Json("""{"id":"8a1b2c3d4e5f","name":"OneDrive","driveType":"personal"}"""),
            "/v1.0/me/followedSites" => Json("""{"error":{"code":"accessDenied","message":"Access denied"}}""", HttpStatusCode.Forbidden),
            _ => NotFound()
        };

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().Equal(new CloudDrive(null, "OneDrive", CloudDriveKind.Personal));
        _handler.Requests.Should().HaveCount(2, "a refused followedSites is not retried");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Drives_treat_followedSites_400_and_404_as_no_sharepoint(HttpStatusCode status)
    {
        _handler.Respond = r => r.Path == "/v1.0/me/drive"
            ? Json("""{"id":"d","name":"OneDrive","driveType":"personal"}""")
            : Json("""{"error":{"code":"BadRequest"}}""", status);

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().ContainSingle().Which.Kind.Should().Be(CloudDriveKind.Personal);
    }

    [Fact]
    public async Task Drives_list_the_libraries_of_every_followed_site_across_pages()
    {
        _handler.Respond = r =>
        {
            Dictionary<string, string> q = QueryOf(r);
            return r.Path switch
            {
                "/v1.0/me/drive" => Json("""{"id":"b!personal","name":"OneDrive","driveType":"business"}"""),
                "/v1.0/me/followedSites" when q.ContainsKey("$skiptoken") => Json("""{"value":[{"id":"contoso.sharepoint.com,s2","displayName":"Design"}]}"""),
                "/v1.0/me/followedSites" => Json($$"""{"value":[{"id":"contoso.sharepoint.com,s1","displayName":"Marketing"}],"@odata.nextLink":"{{Graph}}/me/followedSites?$skiptoken=abc"}"""),
                "/v1.0/sites/contoso.sharepoint.com,s1/drives" => Json("""{"value":[{"id":"b!lib1","name":"Documents","driveType":"documentLibrary"},{"id":"b!lib2","name":"Footage","driveType":"documentLibrary"}]}"""),
                "/v1.0/sites/contoso.sharepoint.com,s2/drives" => Json("""{"error":{"code":"accessDenied"}}""", HttpStatusCode.Forbidden),
                _ => NotFound()
            };
        };

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().Equal(
            new CloudDrive(null, "OneDrive", CloudDriveKind.Personal),
            new CloudDrive("b!lib1", "Marketing — Documents", CloudDriveKind.SharePointLibrary),
            new CloudDrive("b!lib2", "Marketing — Footage", CloudDriveKind.SharePointLibrary));
        RecordedRequest sites = _handler.Requests.First(x => x.Path == "/v1.0/me/followedSites");
        ParseQuery(sites.Uri.Query)["$select"].Should().Be("id,displayName");
        RecordedRequest libraries = _handler.Requests.First(x => x.Path.EndsWith("/drives", StringComparison.Ordinal));
        ParseQuery(libraries.Uri.Query)["$select"].Should().Be("id,name,driveType");
        libraries.Uri.AbsoluteUri.Should().Contain("/sites/contoso.sharepoint.com%2Cs1/drives", "the site id is percent-escaped in the path");
        _handler.Requests.Should().OnlyContain(x => x.Authorization == "Bearer " + AccessToken);
    }

    [Fact]
    public async Task GetFolder_names_the_path_from_the_parent_reference_under_the_drive_name()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/drives/b!lib1" => Json("""{"id":"b!lib1","name":"Documents"}"""),
            "/v1.0/drives/b!lib1/items/f-clips" => Json("""{"id":"f-clips","name":"Clips","folder":{"childCount":3},"parentReference":{"driveId":"b!lib1","path":"/drives/b!lib1/root:/Projects/Spring%20Launch"}}"""),
            "/v1.0/me/drive/items/f-top" => Json("""{"id":"f-top","name":"Top","folder":{},"parentReference":{"path":"/drive/root:"}}"""),
            _ => NotFound()
        };

        CloudFolderInfo shared = await Provider().GetFolderAsync(AccessToken, new CloudLocation("b!lib1", "f-clips"), CancellationToken.None);
        shared.Should().Be(new CloudFolderInfo("f-clips", "Clips", "Documents/Projects/Spring Launch/Clips"));

        CloudFolderInfo personal = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "f-top"), CancellationToken.None);
        personal.Should().Be(new CloudFolderInfo("f-top", "Top", "OneDrive/Top"));
    }

    [Fact]
    public async Task GetFolder_of_the_root_is_the_drive_itself()
    {
        _handler.Respond = r => r.Path == "/v1.0/me/drive/root"
            ? Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}""")
            : NotFound();

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, null), CancellationToken.None);

        folder.Should().Be(new CloudFolderInfo(null, "OneDrive", "OneDrive"));
    }

    [Fact]
    public async Task GetFolder_of_a_file_or_a_missing_item_is_NotFound()
    {
        _handler.Respond = r => r.Path == "/v1.0/me/drive/items/a-file"
            ? Json("""{"id":"a-file","name":"x.mp4","file":{"mimeType":"video/mp4"},"size":10}""")
            : NotFound();

        Func<Task> file = () => Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "a-file"), CancellationToken.None);
        (await file.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);

        Func<Task> missing = () => Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "gone"), CancellationToken.None);
        (await missing.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
    }

    [Fact]
    public async Task ListFolder_returns_folders_first_then_files_by_name_and_truncates()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/me/drive/root" => Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}"""),
            "/v1.0/me/drive/root/children" => Json("""
                {"value":[
                  {"id":"x2","name":"b.mp4","size":10,"file":{"mimeType":"video/mp4"},"cTag":"c2"},
                  {"id":"d2","name":"beta","folder":{"childCount":0}},
                  {"id":"x1","name":"a.mp4","size":0,"file":{"mimeType":"video/mp4"},"cTag":"c1"},
                  {"id":"nb","name":"Notebook","package":{"type":"oneNote"}},
                  {"id":"d1","name":"Alpha","folder":{"childCount":2}},
                  {"id":"x3","name":"c.mp4","size":5,"file":{"mimeType":"video/mp4"},"cTag":"c3"}
                ]}
                """),
            _ => NotFound()
        };

        CloudFolderPage page = await Provider().ListFolderAsync(AccessToken, new CloudLocation(null, null), 4, CancellationToken.None);

        page.Folder.Should().Be(new CloudFolderInfo(null, "OneDrive", "OneDrive"));
        page.Items.Select(i => (i.Id, i.IsFolder)).Should().Equal(("d1", true), ("d2", true), ("x1", false), ("x2", false));
        page.Items[2].Size.Should().Be(0);
        page.Items[2].MimeType.Should().Be("video/mp4");
        page.Truncated.Should().BeTrue();
    }

    // ------------------------------------------------------------------ syncing

    [Fact]
    public async Task A_zero_byte_file_is_listed_and_downloads_as_an_empty_stream()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/me/drive/root" => Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}"""),
            "/v1.0/me/drive/root/children" => Json("""{"value":[{"id":"empty-1","name":".gitkeep","size":0,"file":{"mimeType":"application/octet-stream"},"cTag":"\"c:{E1},1\"","eTag":"\"{E1},1\""}]}"""),
            "/v1.0/me/drive/items/empty-1/content" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
            _ => NotFound()
        };
        MicrosoftGraphStorageProvider provider = Provider();

        CloudTreeListing listing = await provider.ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Truncated.Should().BeFalse();
        listing.Unsupported.Should().Be(0);
        CloudFileEntry entry = listing.Files.Should().ContainSingle().Which;
        entry.Should().Be(new CloudFileEntry("empty-1", ".gitkeep", 0, "\"c:{E1},1\"", "application/octet-stream"));

        await using CloudDownload download = await provider.OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Length.Should().Be(0);
        download.Length.Should().Be(0);

        RecordedRequest get = _handler.Requests.Last();
        get.Path.Should().Be("/v1.0/me/drive/items/empty-1/content");
        get.Authorization.Should().Be("Bearer " + AccessToken);
    }

    [Fact]
    public async Task A_302_download_follows_the_location_with_a_new_request_that_carries_no_bearer_token()
    {
        const string preAuthenticated = "https://contoso-my.sharepoint.com/personal/ann/_layouts/15/download.aspx?UniqueId=abc&tempauth=pre-auth";
        _handler.Respond = r => r.Uri.Host switch
        {
            "graph.microsoft.com" => Redirect(preAuthenticated),
            "contoso-my.sharepoint.com" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("clip-bytes")) { Headers = { ContentType = new("video/mp4") } }
            },
            _ => NotFound()
        };
        var entry = new CloudFileEntry("item-9", "take.mp4", 10, "c9", "video/mp4");

        await using CloudDownload download = await Provider().OpenDownloadAsync(AccessToken, new CloudLocation("b!lib1", "f-root"), entry, CancellationToken.None);

        (await new StreamReader(download.Content).ReadToEndAsync()).Should().Be("clip-bytes");
        download.ContentType.Should().Be("video/mp4");
        _handler.Requests.Should().HaveCount(2);
        RecordedRequest first = _handler.Requests[0];
        first.Path.Should().Be("/v1.0/drives/b!lib1/items/item-9/content");
        first.Authorization.Should().Be("Bearer " + AccessToken);
        RecordedRequest second = _handler.Requests[1];
        second.Uri.AbsoluteUri.Should().Be(preAuthenticated);
        second.Authorization.Should().BeNull("the bearer token must never reach the pre-authenticated download host");
    }

    [Fact]
    public async Task A_302_to_an_empty_file_downloads_as_an_empty_stream()
    {
        _handler.Respond = r => r.Uri.Host == "graph.microsoft.com"
            ? Redirect("https://public.dm.files.1drv.com/y4m-empty")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        await using CloudDownload download = await Provider().OpenDownloadAsync(
            AccessToken, new CloudLocation(null, null), new CloudFileEntry("e", ".gitkeep", 0, "c", null), CancellationToken.None);

        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Length.Should().Be(0);
        _handler.Requests[1].Authorization.Should().BeNull();
    }

    [Fact]
    public async Task A_redirect_to_a_non_https_location_is_refused()
    {
        _handler.Respond = r => Redirect("http://contoso.sharepoint.com/download.aspx?x=1");

        Func<Task> act = () => Provider().OpenDownloadAsync(
            AccessToken, new CloudLocation(null, null), new CloudFileEntry("f", "a.txt", 1, "c", null), CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);
        _handler.Requests.Should().HaveCount(1, "the plain-http location is never requested");
    }

    [Fact]
    public async Task The_tree_follows_next_links_and_walks_subfolders_breadth_first()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/drives/b!lib1/items/top" => Json("""{"id":"top","name":"Top","folder":{"childCount":2}}"""),
            "/v1.0/drives/b!lib1/items/top/children" when QueryOf(r).ContainsKey("$skiptoken") =>
                Json("""{"value":[{"id":"two","name":"two.txt","size":2,"file":{"mimeType":"text/plain"},"eTag":"e2"}]}"""),
            "/v1.0/drives/b!lib1/items/top/children" =>
                Json($$$"""{"value":[{"id":"sub","name":"Sub","folder":{"childCount":1}},{"id":"one","name":"one.txt","size":1,"file":{"mimeType":"text/plain"},"cTag":"c1","eTag":"e1"}],"@odata.nextLink":"{{{Graph}}}/drives/b!lib1/items/top/children?$select=id&$top=200&$skiptoken=p2"}"""),
            "/v1.0/drives/b!lib1/items/sub/children" =>
                Json("""{"value":[{"id":"deep","name":"inner.txt","size":4,"file":{"mimeType":"text/plain"},"cTag":"c4"}]}"""),
            _ => NotFound()
        };

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation("b!lib1", "top"), 100, CancellationToken.None);

        listing.Files.Should().Equal(
            new CloudFileEntry("one", "one.txt", 1, "c1", "text/plain"),
            new CloudFileEntry("two", "two.txt", 2, "e2", "text/plain"),
            new CloudFileEntry("deep", "Sub/inner.txt", 4, "c4", "text/plain"));
        listing.Truncated.Should().BeFalse();
        listing.Unsupported.Should().Be(0);

        RecordedRequest firstPage = _handler.Requests.First(x => x.Path == "/v1.0/drives/b!lib1/items/top/children");
        Dictionary<string, string> q = ParseQuery(firstPage.Uri.Query);
        q["$select"].Should().Be(ChildSelect);
        q["$top"].Should().Be("200");
        _handler.Requests.Should().Contain(x => x.Uri.Query.Contains("skiptoken=p2", StringComparison.Ordinal));
        _handler.Requests.Should().OnlyContain(x => x.Authorization == "Bearer " + AccessToken);
    }

    [Theory]
    [InlineData("https://evil.example.com/v1.0/me/drive/root/children?$skiptoken=p2")]
    [InlineData("http://graph.microsoft.com/v1.0/me/drive/root/children?$skiptoken=p2")]
    [InlineData("/v1.0/me/drive/root/children?$skiptoken=p2")]
    public async Task A_next_link_off_the_graph_host_or_not_https_is_refused(string nextLink)
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/me/drive/root" => Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}"""),
            "/v1.0/me/drive/root/children" => Json($$"""{"value":[{"id":"a","name":"a.txt","size":1,"file":{},"cTag":"c"}],"@odata.nextLink":"{{nextLink}}"}"""),
            _ => Json("""{"value":[]}""")
        };

        Func<Task> act = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);
        _handler.Requests.Should().HaveCount(2, "the foreign link is never requested");
        _handler.Requests.Should().OnlyContain(x => x.Uri.Host == "graph.microsoft.com" && x.Uri.Scheme == "https");
    }

    [Fact]
    public async Task Packages_remote_items_unsafe_names_and_duplicate_paths_are_counted_unsupported()
    {
        _handler.Respond = r => r.Path switch
        {
            "/v1.0/me/drive/root" => Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}"""),
            "/v1.0/me/drive/root/children" => Json("""
                {"value":[
                  {"id":"nb","name":"Notes","package":{"type":"oneNote"},"cTag":"n"},
                  {"id":"rf","name":"Shared footage","remoteItem":{"id":"r1","folder":{"childCount":9}}},
                  {"id":"rfile","name":"shared.mp4","remoteItem":{"id":"r2","file":{}},"file":{"mimeType":"video/mp4"},"cTag":"r"},
                  {"id":"bs","name":"a\\b.txt","size":1,"file":{},"cTag":"b"},
                  {"id":"ctl","name":"bad\u0007name.txt","size":1,"file":{},"cTag":"x"},
                  {"id":"ok","name":"ok.txt","size":1,"file":{"mimeType":"text/plain"},"cTag":"o"},
                  {"id":"dup","name":"ok.txt","size":1,"file":{"mimeType":"text/plain"},"cTag":"d"},
                  {"id":"norev","name":"norev.txt","size":1,"file":{}}
                ]}
                """),
            _ => NotFound()
        };

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Files.Select(f => f.ItemId).Should().Equal("ok");
        listing.Unsupported.Should().Be(7);
        _handler.Requests.Should().HaveCount(2, "neither a package nor a shared shortcut is followed");
    }

    [Fact]
    public async Task A_gone_root_folder_fails_with_NotFound_instead_of_listing_empty()
    {
        _handler.Respond = r => NotFound();

        Func<Task> act = () => Provider().ListTreeAsync(AccessToken, new CloudLocation("b!lib1", "deleted"), 100, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.NotFound);
        _handler.Requests.Should().ContainSingle().Which.Path.Should().Be("/v1.0/drives/b!lib1/items/deleted");
    }

    [Fact]
    public async Task A_root_that_is_now_a_file_fails_with_NotFound()
    {
        _handler.Respond = r => r.Path == "/v1.0/me/drive/items/was-folder"
            ? Json("""{"id":"was-folder","name":"Footage","size":3,"file":{"mimeType":"text/plain"},"cTag":"c"}""")
            : Json("""{"value":[]}""");

        Func<Task> act = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "was-folder"), 100, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
        _handler.Requests.Should().HaveCount(1, "no children are listed under a root that is not a folder");
    }

    [Fact]
    public async Task The_tree_stops_at_max_entries_and_reports_truncation()
    {
        _handler.Respond = r => r.Path == "/v1.0/me/drive/root"
            ? Json("""{"id":"ROOTID","name":"root","folder":{},"root":{}}""")
            : Json("""
                {"value":[
                  {"id":"1","name":"1.txt","size":1,"file":{},"cTag":"a"},
                  {"id":"2","name":"2.txt","size":1,"file":{},"cTag":"b"},
                  {"id":"3","name":"3.txt","size":1,"file":{},"cTag":"c"}
                ]}
                """);

        CloudTreeListing truncated = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 2, CancellationToken.None);
        truncated.Files.Should().HaveCount(2);
        truncated.Truncated.Should().BeTrue();

        CloudTreeListing exact = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 3, CancellationToken.None);
        exact.Files.Should().HaveCount(3);
        exact.Truncated.Should().BeFalse();
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public async Task A_429_with_retry_after_is_retried_and_then_succeeds()
    {
        int calls = 0;
        _handler.Respond = r =>
        {
            if (++calls > 1) return Json("""{"id":"u1","displayName":"Ann","mail":"ann@contoso.com"}""");
            HttpResponseMessage throttled = Json("""{"error":{"code":"activityLimitReached"}}""", HttpStatusCode.TooManyRequests);
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return throttled;
        };

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.AccountId.Should().Be("u1");
        _handler.Requests.Should().HaveCount(2);
        _delays.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_401_is_Unauthorized()
    {
        _handler.Respond = r => Json("""{"error":{"code":"InvalidAuthenticationToken"}}""", HttpStatusCode.Unauthorized);

        Func<Task> act = () => Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_404_download_is_NotFound()
    {
        _handler.Respond = r => NotFound();
        var entry = new CloudFileEntry("gone", "a.mp4", 10, "c", "video/mp4");

        Func<Task> act = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.NotFound);
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ fake Graph API

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        Json("""{"error":{"code":"itemNotFound","message":"The resource could not be found."}}""", HttpStatusCode.NotFound);

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static Dictionary<string, string> QueryOf(RecordedRequest request) => ParseQuery(request.Uri.Query);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }

        return result;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
    {
        /// <summary>The path with percent-escapes decoded (ids such as <c>b!lib1</c> go out as <c>b%21lib1</c>).</summary>
        public string Path => Uri.UnescapeDataString(Uri.AbsolutePath);
    }

    private sealed class FakeGraphHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
            lock (Requests) Requests.Add(recorded);
            HttpResponseMessage response = Respond(recorded);
            response.RequestMessage = request;
            return response;
        }
    }
}
