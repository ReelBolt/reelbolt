#nullable enable

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
/// <see cref="GoogleDriveStorageProvider"/> against a fake Drive API (no network, no credentials).
/// In the "CloudHttp" collection because it replaces the process-wide <see cref="CloudHttp.Delay"/>.
/// </summary>
[Collection("CloudHttp")]
public sealed class GoogleDriveStorageProviderTests : IDisposable
{
    private const string ClientId = "client-id-123.apps.googleusercontent.com";
    private const string ClientSecret = "GOCSPX-super-secret-value";
    private const string AccessToken = "ya29.access-token-value";
    private const string RefreshToken = "1//refresh-token-value";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const string EmptyMd5 = "d41d8cd98f00b204e9800998ecf8427e";

    private readonly Func<TimeSpan, CancellationToken, Task> _originalDelay;
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeDriveHandler _handler = new();

    public GoogleDriveStorageProviderTests()
    {
        _originalDelay = CloudHttp.Delay;
        CloudHttp.Delay = (wait, _) =>
        {
            lock (_delays) _delays.Add(wait);
            return Task.CompletedTask;
        };
    }

    public void Dispose() => CloudHttp.Delay = _originalDelay;

    private GoogleDriveStorageProvider Provider(bool configured = true)
    {
        var options = new CloudStorageOptions();
        if (configured)
        {
            options.GoogleDrive.ClientId = ClientId;
            options.GoogleDrive.ClientSecret = ClientSecret;
        }

        return new GoogleDriveStorageProvider(
            new HttpClient(_handler),
            Options.Create(options),
            NullLogger<GoogleDriveStorageProvider>.Instance);
    }

    // ------------------------------------------------------------------ OAuth

    [Fact]
    public void Authorization_url_requests_offline_consent_pkce_and_drive_readonly()
    {
        Uri url = Provider().BuildAuthorizationUrl("state-xyz", "challenge-abc", new Uri("https://reelbolt.test/api/v1/cloud-storage/google-drive/oauth/callback"));

        url.GetLeftPart(UriPartial.Path).Should().Be("https://accounts.google.com/o/oauth2/v2/auth");
        Dictionary<string, string> q = ParseQuery(url.Query);
        q["client_id"].Should().Be(ClientId);
        q["redirect_uri"].Should().Be("https://reelbolt.test/api/v1/cloud-storage/google-drive/oauth/callback");
        q["response_type"].Should().Be("code");
        q["scope"].Should().Be("https://www.googleapis.com/auth/drive.readonly");
        q["access_type"].Should().Be("offline");
        q["prompt"].Should().Be("consent");
        q["include_granted_scopes"].Should().Be("true");
        q["state"].Should().Be("state-xyz");
        q["code_challenge"].Should().Be("challenge-abc");
        q["code_challenge_method"].Should().Be("S256");
        url.AbsoluteUri.Should().NotContain(ClientSecret);
    }

    [Fact]
    public void Unconfigured_provider_refuses_with_NotConfigured()
    {
        GoogleDriveStorageProvider provider = Provider(configured: false);

        provider.IsConfigured.Should().BeFalse();
        Action act = () => provider.BuildAuthorizationUrl("s", "c", new Uri("https://reelbolt.test/cb"));
        act.Should().Throw<CloudStorageApiException>().Which.Kind.Should().Be(CloudStorageErrorKind.NotConfigured);
    }

    [Fact]
    public async Task Exchange_posts_a_form_and_parses_the_token_set()
    {
        _handler.Respond = r => Json("""
            {"access_token":"ya29.new","expires_in":3599,"refresh_token":"1//new-refresh","scope":"https://www.googleapis.com/auth/drive.readonly","token_type":"Bearer"}
            """);

        DateTime before = DateTime.UtcNow;
        CloudTokenSet tokens = await Provider().ExchangeCodeAsync("auth-code-1", "verifier-1", new Uri("https://reelbolt.test/cb"), CancellationToken.None);

        tokens.AccessToken.Should().Be("ya29.new");
        tokens.RefreshToken.Should().Be("1//new-refresh");
        tokens.Scope.Should().Be("https://www.googleapis.com/auth/drive.readonly");
        tokens.AccessTokenExpiresAt.Should().BeOnOrAfter(before.AddSeconds(3599)).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(3599));

        RecordedRequest request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://oauth2.googleapis.com/token");
        Dictionary<string, string> form = ParseQuery(request.Body!);
        form["grant_type"].Should().Be("authorization_code");
        form["code"].Should().Be("auth-code-1");
        form["code_verifier"].Should().Be("verifier-1");
        form["redirect_uri"].Should().Be("https://reelbolt.test/cb");
        form["client_id"].Should().Be(ClientId);
        form["client_secret"].Should().Be(ClientSecret);
    }

    [Fact]
    public async Task Refresh_parses_the_access_token_and_leaves_the_refresh_token_null_when_google_does_not_rotate()
    {
        _handler.Respond = r => Json("""{"access_token":"ya29.refreshed","expires_in":3600,"token_type":"Bearer"}""");

        CloudTokenSet tokens = await Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        tokens.AccessToken.Should().Be("ya29.refreshed");
        tokens.RefreshToken.Should().BeNull();
        tokens.AccessTokenExpiresAt.Should().NotBeNull();
        Dictionary<string, string> form = ParseQuery(_handler.Requests.Single().Body!);
        form["grant_type"].Should().Be("refresh_token");
        form["refresh_token"].Should().Be(RefreshToken);
        _handler.Requests.Single().Uri.Query.Should().BeEmpty("no secret goes into a URL");
    }

    [Fact]
    public async Task A_refused_refresh_token_is_Unauthorized_and_the_message_carries_no_secret()
    {
        _handler.Respond = r => Json(
            $$"""{"error":"invalid_grant","error_description":"Token has been expired or revoked: {{RefreshToken}} {{ClientSecret}}"}""",
            HttpStatusCode.BadRequest);

        Func<Task> act = () => Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        ex.ToString().Should().NotContain(RefreshToken).And.NotContain(ClientSecret).And.NotContain(ClientId);
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

        // An API answer that is not JSON at all, quoting the bearer token.
        _handler.Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{" + AccessToken) };
        Func<Task> account = () => Provider().GetAccountAsync(AccessToken, CancellationToken.None);
        CloudStorageApiException ex2 = (await account.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex2.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);

        // A 401 on a listing.
        _handler.Respond = r => Json("""{"error":{"code":401,"message":"Invalid Credentials"}}""", HttpStatusCode.Unauthorized);
        Func<Task> list = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 10, CancellationToken.None);
        CloudStorageApiException ex3 = (await list.Should().ThrowAsync<CloudStorageApiException>()).Which;

        foreach (CloudStorageApiException ex in new[] { ex1, ex2, ex3 })
        {
            string text = ex.ToString();
            foreach (string secret in new[] { ClientSecret, AccessToken, RefreshToken, "the-auth-code", "the-verifier" })
                text.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Account_is_the_permission_id()
    {
        _handler.Respond = r => Json("""{"user":{"permissionId":"0123456789","emailAddress":"ann@example.com","displayName":"Ann"}}""");

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.Should().Be(new CloudAccountInfo("0123456789", "ann@example.com", "Ann"));
        RecordedRequest request = _handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be("/drive/v3/about");
        ParseQuery(request.Uri.Query)["fields"].Should().Be("user(permissionId,emailAddress,displayName)");
        request.Authorization.Should().Be("Bearer " + AccessToken);
    }

    [Fact]
    public async Task Revoke_posts_the_refresh_token_and_swallows_an_invalid_token_answer()
    {
        _handler.Respond = r => Json("""{"error":"invalid_token"}""", HttpStatusCode.BadRequest);

        await Provider().RevokeAsync(new CloudTokenSet(AccessToken, RefreshToken, null, null), CancellationToken.None);

        RecordedRequest request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://oauth2.googleapis.com/revoke");
        ParseQuery(request.Body!)["token"].Should().Be(RefreshToken);
    }

    // ------------------------------------------------------------------ browsing

    [Fact]
    public async Task Drives_are_my_drive_then_every_page_of_shared_drives()
    {
        _handler.Respond = r => QueryOf(r).TryGetValue("pageToken", out string? token) && token == "p2"
            ? Json("""{"drives":[{"id":"0AD2","name":"Design"}]}""")
            : Json("""{"nextPageToken":"p2","drives":[{"id":"0AD1","name":"Marketing"}]}""");

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().Equal(
            new CloudDrive(null, "My Drive", CloudDriveKind.Personal),
            new CloudDrive("0AD1", "Marketing", CloudDriveKind.SharedDrive),
            new CloudDrive("0AD2", "Design", CloudDriveKind.SharedDrive));
        _handler.Requests.Should().HaveCount(2);
        _handler.Requests.Should().OnlyContain(x => x.Uri.AbsolutePath == "/drive/v3/drives");
    }

    [Fact]
    public async Task GetFolder_names_the_path_by_walking_parents_up_to_my_drive()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/drive/v3/files/f-inner" => Json($$"""{"id":"f-inner","name":"Clips","mimeType":"{{FolderMime}}","parents":["f-mid"]}"""),
            "/drive/v3/files/f-mid" => Json("""{"id":"f-mid","name":"Projects","parents":["0AROOT"]}"""),
            "/drive/v3/files/0AROOT" => Json("""{"id":"0AROOT","name":"My Drive"}"""),
            _ => NotFound()
        };

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "f-inner"), CancellationToken.None);

        folder.Should().Be(new CloudFolderInfo("f-inner", "Clips", "My Drive/Projects/Clips"));
        ParseQuery(_handler.Requests[0].Uri.Query)["supportsAllDrives"].Should().Be("true");
    }

    [Fact]
    public async Task GetFolder_in_a_shared_drive_stops_at_the_drive_root()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/drive/v3/drives/0AD1" => Json("""{"id":"0AD1","name":"Marketing"}"""),
            "/drive/v3/files/f-a" => Json($$"""{"id":"f-a","name":"Assets","mimeType":"{{FolderMime}}","parents":["0AD1"],"driveId":"0AD1"}"""),
            _ => NotFound()
        };

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation("0AD1", "f-a"), CancellationToken.None);

        folder.Should().Be(new CloudFolderInfo("f-a", "Assets", "Marketing/Assets"));
        _handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetFolder_walk_is_bounded_to_32_hops()
    {
        _handler.Respond = r =>
        {
            string id = r.Uri.AbsolutePath["/drive/v3/files/".Length..];
            int n = int.Parse(id[1..]);
            return Json($$"""{"id":"{{id}}","name":"n{{n}}","mimeType":"{{FolderMime}}","parents":["f{{n + 1}}"]}""");
        };

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "f0"), CancellationToken.None);

        _handler.Requests.Should().HaveCount(33, "the folder itself plus at most 32 parents");
        folder.DisplayPath.Should().StartWith("My Drive/…/").And.EndWith("/n0");
    }

    [Fact]
    public async Task GetFolder_of_a_file_or_a_missing_item_is_NotFound()
    {
        _handler.Respond = r => r.Uri.AbsolutePath == "/drive/v3/files/a-file"
            ? Json("""{"id":"a-file","name":"x.mp4","mimeType":"video/mp4","parents":["root"]}""")
            : NotFound();

        Func<Task> file = () => Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "a-file"), CancellationToken.None);
        (await file.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);

        Func<Task> missing = () => Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "gone"), CancellationToken.None);
        (await missing.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
    }

    [Fact]
    public async Task ListFolder_returns_folders_first_then_files_by_name_and_truncates()
    {
        _handler.Respond = r =>
        {
            string q = QueryOf(r)["q"];
            return q.Contains($"mimeType = '{FolderMime}'", StringComparison.Ordinal)
                ? Json($$"""{"files":[{"id":"d2","name":"beta","mimeType":"{{FolderMime}}"},{"id":"d1","name":"Alpha","mimeType":"{{FolderMime}}"}]}""")
                : Json("""{"files":[{"id":"x2","name":"b.mp4","mimeType":"video/mp4","size":"10"},{"id":"x1","name":"a.mp4","mimeType":"video/mp4","size":"0"},{"id":"x3","name":"c.mp4","mimeType":"video/mp4","size":"5"}]}""");
        };

        CloudFolderPage page = await Provider().ListFolderAsync(AccessToken, new CloudLocation(null, null), 4, CancellationToken.None);

        page.Folder.Should().Be(new CloudFolderInfo(null, "My Drive", "My Drive"));
        page.Items.Select(i => (i.Id, i.IsFolder)).Should().Equal(("d1", true), ("d2", true), ("x1", false), ("x2", false));
        page.Items[2].Size.Should().Be(0);
        page.Truncated.Should().BeTrue();
        ParseQuery(_handler.Requests[0].Uri.Query)["q"].Should().StartWith("'root' in parents and trashed = false");
    }

    // ------------------------------------------------------------------ syncing

    [Fact]
    public async Task A_zero_byte_file_is_listed_and_downloads_as_an_empty_stream()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/drive/v3/files" => Json($$"""{"files":[{"id":"empty-1","name":".gitkeep","mimeType":"application/octet-stream","size":"0","md5Checksum":"{{EmptyMd5}}","version":"3"}]}"""),
            "/drive/v3/files/empty-1" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
            _ => NotFound()
        };
        GoogleDriveStorageProvider provider = Provider();

        CloudTreeListing listing = await provider.ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Truncated.Should().BeFalse();
        listing.Unsupported.Should().Be(0);
        CloudFileEntry entry = listing.Files.Should().ContainSingle().Which;
        entry.Should().Be(new CloudFileEntry("empty-1", ".gitkeep", 0, EmptyMd5, "application/octet-stream"));

        await using CloudDownload download = await provider.OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Length.Should().Be(0);
        download.Length.Should().Be(0);

        RecordedRequest get = _handler.Requests.Last();
        get.Uri.AbsolutePath.Should().Be("/drive/v3/files/empty-1");
        Dictionary<string, string> q = ParseQuery(get.Uri.Query);
        q["alt"].Should().Be("media");
        q["supportsAllDrives"].Should().Be("true");
        get.Authorization.Should().Be("Bearer " + AccessToken);
    }

    [Fact]
    public async Task Native_documents_are_exported_with_the_extension_appended()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/drive/v3/files" => Json("""
                {"files":[
                  {"id":"doc","name":"Script","mimeType":"application/vnd.google-apps.document","version":"12"},
                  {"id":"sheet","name":"Budget","mimeType":"application/vnd.google-apps.spreadsheet","version":"4"},
                  {"id":"slides","name":"Pitch","mimeType":"application/vnd.google-apps.presentation","version":"7"},
                  {"id":"drawing","name":"Logo","mimeType":"application/vnd.google-apps.drawing","version":"2"},
                  {"id":"bin","name":"take.mp4","mimeType":"video/mp4","size":"2048","md5Checksum":"abc123","version":"9"}
                ]}
                """),
            "/drive/v3/files/doc/export" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Script", Encoding.UTF8, "text/markdown") },
            _ => NotFound()
        };
        GoogleDriveStorageProvider provider = Provider();

        CloudTreeListing listing = await provider.ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Files.Should().BeEquivalentTo(new[]
        {
            new CloudFileEntry("doc", "Script.md", 0, "v12", "application/vnd.google-apps.document", "text/markdown"),
            new CloudFileEntry("sheet", "Budget.csv", 0, "v4", "application/vnd.google-apps.spreadsheet", "text/csv"),
            new CloudFileEntry("slides", "Pitch.pdf", 0, "v7", "application/vnd.google-apps.presentation", "application/pdf"),
            new CloudFileEntry("drawing", "Logo.png", 0, "v2", "application/vnd.google-apps.drawing", "image/png"),
            new CloudFileEntry("bin", "take.mp4", 2048, "abc123", "video/mp4"),
        });

        await using CloudDownload download = await provider.OpenDownloadAsync(AccessToken, new CloudLocation(null, null), listing.Files[0], CancellationToken.None);
        (await new StreamReader(download.Content).ReadToEndAsync()).Should().Be("# Script");
        RecordedRequest export = _handler.Requests.Last();
        export.Uri.AbsolutePath.Should().Be("/drive/v3/files/doc/export");
        ParseQuery(export.Uri.Query)["mimeType"].Should().Be("text/markdown");
    }

    [Fact]
    public async Task Shortcuts_forms_and_unsafe_names_are_counted_unsupported()
    {
        _handler.Respond = r => Json("""
            {"files":[
              {"id":"s","name":"Link to footage","mimeType":"application/vnd.google-apps.shortcut","version":"1"},
              {"id":"f","name":"Survey","mimeType":"application/vnd.google-apps.form","version":"1"},
              {"id":"slash","name":"a/b.mp4","mimeType":"video/mp4","size":"1","md5Checksum":"m1"},
              {"id":"ctl","name":"bad\u0007name.txt","mimeType":"text/plain","size":"1","md5Checksum":"m2"},
              {"id":"ok","name":"ok.txt","mimeType":"text/plain","size":"1","md5Checksum":"m3"}
            ]}
            """);

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Files.Select(f => f.ItemId).Should().Equal("ok");
        listing.Unsupported.Should().Be(4);
        _handler.Requests.Should().HaveCount(1, "a shortcut is never followed");
    }

    [Fact]
    public async Task Listing_a_shared_drive_sends_the_shared_drive_parameters()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/drive/v3/drives/0AD1" => Json("""{"id":"0AD1","name":"Marketing"}"""),
            "/drive/v3/files" => Json("""{"files":[{"id":"a","name":"a.txt","mimeType":"text/plain","size":"3","md5Checksum":"m"}]}"""),
            _ => NotFound()
        };

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation("0AD1", null), 100, CancellationToken.None);

        listing.Files.Should().ContainSingle();
        RecordedRequest list = _handler.Requests.Single(x => x.Uri.AbsolutePath == "/drive/v3/files");
        Dictionary<string, string> q = ParseQuery(list.Uri.Query);
        q["supportsAllDrives"].Should().Be("true");
        q["includeItemsFromAllDrives"].Should().Be("true");
        q["corpora"].Should().Be("drive");
        q["driveId"].Should().Be("0AD1");
        q["q"].Should().Be("'0AD1' in parents and trashed = false");
        q["pageSize"].Should().Be("1000");
        q["fields"].Should().Be("nextPageToken,files(id,name,mimeType,size,md5Checksum,version,modifiedTime)");
    }

    [Fact]
    public async Task Listing_my_drive_does_not_send_the_shared_drive_corpus()
    {
        _handler.Respond = r => Json("""{"files":[]}""");

        await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        Dictionary<string, string> q = ParseQuery(_handler.Requests.Single().Uri.Query);
        q["supportsAllDrives"].Should().Be("true");
        q.Should().NotContainKey("corpora").And.NotContainKey("driveId");
    }

    [Fact]
    public async Task The_tree_follows_next_page_tokens_and_walks_subfolders_breadth_first()
    {
        _handler.Respond = r =>
        {
            Dictionary<string, string> q = QueryOf(r);
            if (r.Uri.AbsolutePath == "/drive/v3/files/top")
                return Json($$"""{"id":"top","name":"Top","mimeType":"{{FolderMime}}","parents":["root"]}""");
            if (q["q"].StartsWith("'sub'", StringComparison.Ordinal))
                return Json("""{"files":[{"id":"deep","name":"inner.txt","mimeType":"text/plain","size":"4","md5Checksum":"m4"}]}""");
            return q.TryGetValue("pageToken", out string? token) && token == "page-2"
                ? Json("""{"files":[{"id":"two","name":"two.txt","mimeType":"text/plain","size":"2","md5Checksum":"m2"}]}""")
                : Json($$"""{"nextPageToken":"page-2","files":[{"id":"sub","name":"Sub","mimeType":"{{FolderMime}}"},{"id":"one","name":"one.txt","mimeType":"text/plain","size":"1","md5Checksum":"m1"}]}""");
        };

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "top"), 100, CancellationToken.None);

        listing.Files.Select(f => f.RelativePath).Should().Equal("one.txt", "two.txt", "Sub/inner.txt");
        listing.Truncated.Should().BeFalse();
        _handler.Requests.Where(x => x.Uri.AbsolutePath == "/drive/v3/files").Should().HaveCount(3);
    }

    [Fact]
    public async Task The_tree_stops_at_max_entries_and_reports_truncation()
    {
        _handler.Respond = r => Json("""
            {"files":[
              {"id":"1","name":"1.txt","mimeType":"text/plain","size":"1","md5Checksum":"a"},
              {"id":"2","name":"2.txt","mimeType":"text/plain","size":"1","md5Checksum":"b"},
              {"id":"3","name":"3.txt","mimeType":"text/plain","size":"1","md5Checksum":"c"}
            ]}
            """);

        CloudTreeListing truncated = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 2, CancellationToken.None);
        truncated.Files.Should().HaveCount(2);
        truncated.Truncated.Should().BeTrue();

        CloudTreeListing exact = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 3, CancellationToken.None);
        exact.Files.Should().HaveCount(3);
        exact.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task The_root_folder_is_validated_so_a_trashed_folder_fails_instead_of_listing_empty()
    {
        _handler.Respond = r => Json($$"""{"id":"top","name":"Top","mimeType":"{{FolderMime}}","parents":["root"],"trashed":true}""");

        Func<Task> act = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "top"), 100, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
    }

    [Fact]
    public async Task Quotes_and_backslashes_in_ids_are_escaped_inside_the_query()
    {
        _handler.Respond = r => r.Uri.AbsolutePath == "/drive/v3/files"
            ? Json("""{"files":[]}""")
            : Json($$"""{"id":"x","name":"Odd","mimeType":"{{FolderMime}}","parents":["root"]}""");

        await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, @"it's\odd"), 10, CancellationToken.None);

        RecordedRequest get = _handler.Requests[0];
        get.Uri.AbsoluteUri.Should().Contain("/drive/v3/files/it%27s%5Codd?", "the id is percent-escaped in the path");
        string q = ParseQuery(_handler.Requests[1].Uri.Query)["q"];
        q.Should().Be(@"'it\'s\\odd' in parents and trashed = false");
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public async Task A_rate_limit_403_is_retried_and_then_succeeds()
    {
        int calls = 0;
        _handler.Respond = r => ++calls == 1
            ? Json("""{"error":{"code":403,"message":"Rate Limit Exceeded","errors":[{"domain":"usageLimits","reason":"rateLimitExceeded"}]}}""", HttpStatusCode.Forbidden)
            : Json("""{"user":{"permissionId":"p1"}}""");

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.AccountId.Should().Be("p1");
        _handler.Requests.Should().HaveCount(2);
        _delays.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_persistent_user_rate_limit_403_ends_as_RateLimited()
    {
        _handler.Respond = r => Json("""{"error":{"code":403,"errors":[{"reason":"userRateLimitExceeded"}]}}""", HttpStatusCode.Forbidden);

        Func<Task> act = () => Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.RateLimited);
        _handler.Requests.Should().HaveCount(CloudHttp.MaxAttempts);
    }

    [Fact]
    public async Task A_permission_403_is_Forbidden_and_not_retried()
    {
        _handler.Respond = r => Json("""{"error":{"code":403,"errors":[{"reason":"cannotDownloadFile"}]}}""", HttpStatusCode.Forbidden);
        var entry = new CloudFileEntry("f1", "a.mp4", 10, "m", "video/mp4");

        Func<Task> act = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Forbidden);
        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_401_is_Unauthorized()
    {
        _handler.Respond = r => Json("""{"error":{"code":401,"message":"Invalid Credentials"}}""", HttpStatusCode.Unauthorized);

        Func<Task> act = () => Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_404_download_is_NotFound()
    {
        _handler.Respond = r => NotFound();
        var entry = new CloudFileEntry("gone", "a.mp4", 10, "m", "video/mp4");

        Func<Task> act = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.NotFound);
        ex.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ------------------------------------------------------------------ fake Drive API

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        Json("""{"error":{"code":404,"message":"File not found."}}""", HttpStatusCode.NotFound);

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

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

    private sealed class FakeDriveHandler : HttpMessageHandler
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
