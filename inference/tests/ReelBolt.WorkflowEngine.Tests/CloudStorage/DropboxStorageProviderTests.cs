#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// <see cref="DropboxStorageProvider"/> against a fake Dropbox API (no network, no credentials).
/// In the "CloudHttp" collection because it replaces the process-wide <see cref="CloudHttp.Delay"/>.
/// </summary>
[Collection("CloudHttp")]
public sealed class DropboxStorageProviderTests : IDisposable
{
    private const string AppKey = "dropbox-app-key-123";
    private const string AppSecret = "dropbox-app-secret-value";
    private const string AccessToken = "sl.access-token-value";
    private const string RefreshToken = "refresh-token-value-xyz";
    private const string TeamNs = "7001";

    private readonly Func<TimeSpan, CancellationToken, Task> _originalDelay;
    private readonly List<TimeSpan> _delays = [];
    private readonly FakeDropboxHandler _handler = new();

    public DropboxStorageProviderTests()
    {
        _originalDelay = CloudHttp.Delay;
        CloudHttp.Delay = (wait, _) =>
        {
            lock (_delays) _delays.Add(wait);
            return Task.CompletedTask;
        };
    }

    public void Dispose() => CloudHttp.Delay = _originalDelay;

    private DropboxStorageProvider Provider(bool configured = true)
    {
        var options = new CloudStorageOptions();
        if (configured)
        {
            options.Dropbox.ClientId = AppKey;
            options.Dropbox.ClientSecret = AppSecret;
        }

        return new DropboxStorageProvider(new HttpClient(_handler), Options.Create(options), NullLogger<DropboxStorageProvider>.Instance);
    }

    // ------------------------------------------------------------------ OAuth

    [Fact]
    public void Authorization_url_requests_offline_access_pkce_and_read_scopes()
    {
        Uri url = Provider().BuildAuthorizationUrl("state-xyz", "challenge-abc", new Uri("https://reelbolt.test/api/v1/cloud-storage/dropbox/oauth/callback"));

        url.GetLeftPart(UriPartial.Path).Should().Be("https://www.dropbox.com/oauth2/authorize");
        Dictionary<string, string> q = ParseForm(url.Query);
        q["client_id"].Should().Be(AppKey);
        q["redirect_uri"].Should().Be("https://reelbolt.test/api/v1/cloud-storage/dropbox/oauth/callback");
        q["response_type"].Should().Be("code");
        q["token_access_type"].Should().Be("offline");
        q["scope"].Should().Be("account_info.read files.metadata.read files.content.read");
        q["state"].Should().Be("state-xyz");
        q["code_challenge"].Should().Be("challenge-abc");
        q["code_challenge_method"].Should().Be("S256");
        url.AbsoluteUri.Should().NotContain(AppSecret);
    }

    [Fact]
    public void Unconfigured_provider_refuses_with_NotConfigured()
    {
        DropboxStorageProvider provider = Provider(configured: false);

        provider.IsConfigured.Should().BeFalse();
        Action act = () => provider.BuildAuthorizationUrl("s", "c", new Uri("https://reelbolt.test/cb"));
        act.Should().Throw<CloudStorageApiException>().Which.Kind.Should().Be(CloudStorageErrorKind.NotConfigured);
    }

    [Fact]
    public async Task Exchange_posts_a_form_to_the_token_endpoint_and_parses_the_token_set()
    {
        _handler.Respond = r => Json("""
            {"access_token":"sl.new","expires_in":14400,"token_type":"bearer","refresh_token":"new-refresh","scope":"account_info.read files.content.read","account_id":"dbid:A1"}
            """);

        DateTime before = DateTime.UtcNow;
        CloudTokenSet tokens = await Provider().ExchangeCodeAsync("auth-code-1", "verifier-1", new Uri("https://reelbolt.test/cb"), CancellationToken.None);

        tokens.AccessToken.Should().Be("sl.new");
        tokens.RefreshToken.Should().Be("new-refresh");
        tokens.Scope.Should().Be("account_info.read files.content.read");
        tokens.AccessTokenExpiresAt.Should().BeOnOrAfter(before.AddSeconds(14400)).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(14400));

        RecordedRequest request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://api.dropboxapi.com/oauth2/token");
        Dictionary<string, string> form = ParseForm(request.Body!);
        form["grant_type"].Should().Be("authorization_code");
        form["code"].Should().Be("auth-code-1");
        form["code_verifier"].Should().Be("verifier-1");
        form["redirect_uri"].Should().Be("https://reelbolt.test/cb");
        form["client_id"].Should().Be(AppKey);
        form["client_secret"].Should().Be(AppSecret);
    }

    [Fact]
    public async Task Refresh_leaves_the_refresh_token_null_because_dropbox_does_not_rotate()
    {
        _handler.Respond = r => Json("""{"access_token":"sl.refreshed","expires_in":14400,"token_type":"bearer"}""");

        CloudTokenSet tokens = await Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        tokens.AccessToken.Should().Be("sl.refreshed");
        tokens.RefreshToken.Should().BeNull();
        RecordedRequest request = _handler.Requests.Single();
        request.Uri.Query.Should().BeEmpty("no secret goes into a URL");
        Dictionary<string, string> form = ParseForm(request.Body!);
        form["grant_type"].Should().Be("refresh_token");
        form["refresh_token"].Should().Be(RefreshToken);
    }

    [Fact]
    public async Task A_refused_refresh_token_is_Unauthorized_and_the_message_carries_no_secret()
    {
        _handler.Respond = r => Json(
            $$"""{"error":"invalid_grant","error_description":"refresh token is invalid or revoked {{RefreshToken}} {{AppSecret}}"}""",
            HttpStatusCode.BadRequest);

        Func<Task> act = () => Provider().RefreshAsync(RefreshToken, CancellationToken.None);

        CloudStorageApiException ex = (await act.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        ex.ToString().Should().NotContain(RefreshToken).And.NotContain(AppSecret);
        _handler.Requests.Should().HaveCount(1, "a refused grant is not retried");
    }

    [Fact]
    public async Task Secrets_never_appear_in_exception_messages()
    {
        _handler.Respond = r => Json($$"""{"error":"server_error","echo":"{{r.Body}}"}""", HttpStatusCode.InternalServerError);
        Func<Task> exchange = () => Provider().ExchangeCodeAsync("the-auth-code", "the-verifier", new Uri("https://reelbolt.test/cb"), CancellationToken.None);
        CloudStorageApiException ex1 = (await exchange.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex1.Kind.Should().Be(CloudStorageErrorKind.ServerError);

        _handler.Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{" + AccessToken) };
        Func<Task> account = () => Provider().GetAccountAsync(AccessToken, CancellationToken.None);
        CloudStorageApiException ex2 = (await account.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex2.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);

        _handler.Respond = r => Json("""{"error_summary":"expired_access_token/","error":{".tag":"expired_access_token"}}""", HttpStatusCode.Unauthorized);
        Func<Task> list = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 10, CancellationToken.None);
        CloudStorageApiException ex3 = (await list.Should().ThrowAsync<CloudStorageApiException>()).Which;
        ex3.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);

        foreach (CloudStorageApiException ex in new[] { ex1, ex2, ex3 })
        {
            string text = ex.ToString();
            foreach (string secret in new[] { AppSecret, AccessToken, RefreshToken, "the-auth-code", "the-verifier" })
                text.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task Account_is_a_post_with_a_literal_null_json_body()
    {
        _handler.Respond = r => Json(AccountJson(rootNs: "100", homeNs: "100"));

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.Should().Be(new CloudAccountInfo("dbid:AAA", "ann@example.com", "Ann Example"));
        RecordedRequest request = _handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Uri.AbsoluteUri.Should().Be("https://api.dropboxapi.com/2/users/get_current_account");
        request.Body.Should().Be("null");
        request.ContentType.Should().Be("application/json");
        request.Authorization.Should().Be("Bearer " + AccessToken);
        request.PathRoot.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_posts_with_the_access_token_and_ignores_a_401()
    {
        _handler.Respond = r => Json("""{"error_summary":"invalid_access_token/"}""", HttpStatusCode.Unauthorized);

        await Provider().RevokeAsync(new CloudTokenSet(AccessToken, RefreshToken, null, null), CancellationToken.None);

        RecordedRequest request = _handler.Requests.Single();
        request.Uri.AbsoluteUri.Should().Be("https://api.dropboxapi.com/2/auth/token/revoke");
        request.Authorization.Should().Be("Bearer " + AccessToken);
    }

    // ------------------------------------------------------------------ browsing

    [Fact]
    public async Task Drives_are_the_home_only_for_a_personal_account()
    {
        _handler.Respond = r => Json(AccountJson(rootNs: "100", homeNs: "100"));

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().Equal(new CloudDrive(null, "Dropbox", CloudDriveKind.Personal));
    }

    [Fact]
    public async Task Drives_add_the_team_space_for_a_team_member()
    {
        _handler.Respond = r => Json(AccountJson(rootNs: TeamNs, homeNs: "100"));

        IReadOnlyList<CloudDrive> drives = await Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        drives.Should().Equal(
            new CloudDrive(null, "Dropbox", CloudDriveKind.Personal),
            new CloudDrive(TeamNs, "Team space", CloudDriveKind.TeamSpace));
    }

    [Fact]
    public async Task GetFolder_names_the_path_from_get_metadata()
    {
        _handler.Respond = r => Json("""{".tag":"folder","id":"id:F1","name":"Clips","path_lower":"/projects/clips","path_display":"/Projects/Clips"}""");

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "id:F1"), CancellationToken.None);

        folder.Should().Be(new CloudFolderInfo("id:F1", "Clips", "Dropbox/Projects/Clips"));
        RecordedRequest request = _handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be("/2/files/get_metadata");
        request.JsonBody().GetProperty("path").GetString().Should().Be("id:F1");
        request.PathRoot.Should().BeNull();
    }

    [Fact]
    public async Task GetFolder_in_the_team_space_sends_the_path_root_header()
    {
        _handler.Respond = r => Json("""{".tag":"folder","id":"id:T1","name":"Brand","path_lower":"/marketing/brand","path_display":"/Marketing/Brand"}""");

        CloudFolderInfo folder = await Provider().GetFolderAsync(AccessToken, new CloudLocation(TeamNs, "id:T1"), CancellationToken.None);

        folder.DisplayPath.Should().Be("Team space/Marketing/Brand");
        string header = _handler.Requests.Single().PathRoot!;
        using JsonDocument doc = JsonDocument.Parse(header);
        doc.RootElement.GetProperty(".tag").GetString().Should().Be("namespace_id");
        doc.RootElement.GetProperty("namespace_id").GetString().Should().Be(TeamNs);
    }

    [Fact]
    public async Task GetFolder_of_the_root_needs_no_call()
    {
        CloudFolderInfo home = await Provider().GetFolderAsync(AccessToken, new CloudLocation(null, null), CancellationToken.None);
        CloudFolderInfo team = await Provider().GetFolderAsync(AccessToken, new CloudLocation(TeamNs, null), CancellationToken.None);

        home.Should().Be(new CloudFolderInfo(null, "Dropbox", "Dropbox"));
        team.Should().Be(new CloudFolderInfo(null, "Team space", "Team space"));
        _handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_root_that_is_gone_or_is_a_file_is_NotFound_never_an_empty_listing()
    {
        _handler.Respond = r => Json("""{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict);
        Func<Task> gone = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "id:GONE"), 10, CancellationToken.None);
        (await gone.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
        _handler.Requests.Should().HaveCount(1, "a 409 is not retried and no listing follows");

        _handler.Respond = r => Json("""{".tag":"file","id":"id:FILE","name":"a.mp4","path_lower":"/a.mp4","path_display":"/a.mp4","size":3,"rev":"r1","content_hash":"h"}""");
        Func<Task> file = () => Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "id:FILE"), 10, CancellationToken.None);
        (await file.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);

        _handler.Respond = r => Json("""{"error_summary":"path_lookup/not_found/"}""", HttpStatusCode.Conflict);
        Func<Task> folder = () => Provider().GetFolderAsync(AccessToken, new CloudLocation(null, "id:GONE"), CancellationToken.None);
        (await folder.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);
    }

    [Fact]
    public async Task ListFolder_follows_the_cursor_and_returns_folders_first_by_name()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/2/files/list_folder" => Json("""
                {"entries":[
                  {".tag":"file","id":"id:x2","name":"b.mp4","path_lower":"/b.mp4","path_display":"/b.mp4","size":10,"rev":"r","content_hash":"h"},
                  {".tag":"folder","id":"id:d2","name":"beta","path_lower":"/beta","path_display":"/beta"}
                ],"cursor":"c1","has_more":true}
                """),
            "/2/files/list_folder/continue" => Json("""
                {"entries":[
                  {".tag":"folder","id":"id:d1","name":"Alpha","path_lower":"/alpha","path_display":"/Alpha"},
                  {".tag":"file","id":"id:x1","name":"a.mp4","path_lower":"/a.mp4","path_display":"/a.mp4","size":0,"rev":"r","content_hash":"h"}
                ],"cursor":"c2","has_more":false}
                """),
            _ => NotFound()
        };

        CloudFolderPage page = await Provider().ListFolderAsync(AccessToken, new CloudLocation(null, null), 3, CancellationToken.None);

        page.Folder.Should().Be(new CloudFolderInfo(null, "Dropbox", "Dropbox"));
        page.Items.Select(i => (i.Id, i.IsFolder)).Should().Equal(("id:d1", true), ("id:d2", true), ("id:x1", false));
        page.Truncated.Should().BeTrue();

        JsonElement first = _handler.Requests[0].JsonBody();
        first.GetProperty("path").GetString().Should().Be("");
        first.GetProperty("recursive").GetBoolean().Should().BeFalse();
        _handler.Requests[1].JsonBody().GetProperty("cursor").GetString().Should().Be("c1");
    }

    // ------------------------------------------------------------------ syncing

    [Fact]
    public async Task A_zero_byte_file_is_listed_and_downloads_as_an_empty_stream()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/2/files/list_folder" => Json("""
                {"entries":[{".tag":"file","id":"id:EMPTY","name":".gitkeep","path_lower":"/.gitkeep","path_display":"/.gitkeep","size":0,"rev":"015a","content_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","is_downloadable":true}],
                 "cursor":"c","has_more":false}
                """),
            "/2/files/download" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) },
            _ => NotFound()
        };
        DropboxStorageProvider provider = Provider();

        CloudTreeListing listing = await provider.ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Truncated.Should().BeFalse();
        listing.Unsupported.Should().Be(0);
        CloudFileEntry entry = listing.Files.Should().ContainSingle().Which;
        entry.Should().Be(new CloudFileEntry("id:EMPTY", ".gitkeep", 0, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", null));

        await using CloudDownload download = await provider.OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        using var buffer = new MemoryStream();
        await download.Content.CopyToAsync(buffer);
        buffer.Length.Should().Be(0);
        download.Length.Should().Be(0);

        RecordedRequest get = _handler.Requests.Last();
        get.Method.Should().Be(HttpMethod.Post);
        get.Uri.AbsoluteUri.Should().Be("https://content.dropboxapi.com/2/files/download");
        get.ApiArg.Should().Be("""{"path":"id:EMPTY"}""");
        get.Body.Should().BeNull("a content-download call has no body");
        get.Authorization.Should().Be("Bearer " + AccessToken);
        get.PathRoot.Should().BeNull();
    }

    [Fact]
    public async Task The_tree_strips_the_root_prefix_matched_on_path_lower_and_keeps_display_casing()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/2/files/get_metadata" => Json("""{".tag":"folder","id":"id:ROOT","name":"Footage","path_lower":"/work/footage","path_display":"/work/footage"}"""),
            "/2/files/list_folder" => Json("""
                {"entries":[
                  {".tag":"folder","id":"id:ROOT","name":"Footage","path_lower":"/work/footage","path_display":"/Work/Footage"},
                  {".tag":"folder","id":"id:S","name":"Day One","path_lower":"/work/footage/day one","path_display":"/Work/Footage/Day One"},
                  {".tag":"file","id":"id:A","name":"Take-01.MOV","path_lower":"/work/footage/day one/take-01.mov","path_display":"/work/footage/day one/Take-01.MOV","size":5,"rev":"r1","content_hash":"h1"},
                  {".tag":"file","id":"id:B","name":"Notes.TXT","path_lower":"/work/footage/notes.txt","path_display":"/Work/Footage/Notes.TXT","size":2,"rev":"r2"}
                ],"cursor":"c","has_more":false}
                """),
            _ => NotFound()
        };

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, "id:ROOT"), 100, CancellationToken.None);

        listing.Files.Should().Equal(
            new CloudFileEntry("id:A", "Day One/Take-01.MOV", 5, "h1", null),
            new CloudFileEntry("id:B", "Notes.TXT", 2, "r2", null));
        listing.Unsupported.Should().Be(0);

        JsonElement body = _handler.Requests.Single(x => x.Uri.AbsolutePath == "/2/files/list_folder").JsonBody();
        body.GetProperty("path").GetString().Should().Be("id:ROOT");
        body.GetProperty("recursive").GetBoolean().Should().BeTrue();
        body.GetProperty("include_deleted").GetBoolean().Should().BeFalse();
        body.GetProperty("include_non_downloadable_files").GetBoolean().Should().BeTrue();
        body.GetProperty("limit").GetInt32().Should().Be(2000);
    }

    [Fact]
    public async Task Non_downloadable_unsafe_names_and_case_duplicates_are_counted_unsupported()
    {
        _handler.Respond = r => Json("""
            {"entries":[
              {".tag":"file","id":"id:P","name":"Plan.paper","path_lower":"/plan.paper","path_display":"/Plan.paper","size":0,"rev":"r","is_downloadable":false},
              {".tag":"file","id":"id:C","name":"bad\u0007.txt","path_lower":"/bad\u0007.txt","path_display":"/bad\u0007.txt","size":1,"rev":"r","content_hash":"h"},
              {".tag":"file","id":"id:BS","name":"a\\b.txt","path_lower":"/a\\b.txt","path_display":"/a\\b.txt","size":1,"rev":"r","content_hash":"h"},
              {".tag":"file","id":"id:1","name":"Readme.md","path_lower":"/readme.md","path_display":"/Readme.md","size":1,"rev":"r","content_hash":"h1"},
              {".tag":"file","id":"id:2","name":"README.md","path_lower":"/readme.md","path_display":"/README.md","size":1,"rev":"r","content_hash":"h2"},
              {".tag":"folder","id":"id:BADF","name":"x\u0001y","path_lower":"/x\u0001y","path_display":"/x\u0001y"},
              {".tag":"file","id":"id:IN","name":"in.txt","path_lower":"/x\u0001y/in.txt","path_display":"/x\u0001y/in.txt","size":1,"rev":"r","content_hash":"h"}
            ],"cursor":"c","has_more":false}
            """);

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Files.Select(f => (f.ItemId, f.RelativePath)).Should().Equal(("id:1", "Readme.md"));
        listing.Unsupported.Should().Be(6);
    }

    [Fact]
    public async Task The_tree_follows_the_continue_cursor()
    {
        _handler.Respond = r => r.Uri.AbsolutePath == "/2/files/list_folder"
            ? Json("""{"entries":[{".tag":"file","id":"id:1","name":"one.txt","path_lower":"/one.txt","path_display":"/one.txt","size":1,"rev":"r1"}],"cursor":"cur-1","has_more":true}""")
            : r.JsonBody().GetProperty("cursor").GetString() == "cur-1"
                ? Json("""{"entries":[{".tag":"file","id":"id:2","name":"two.txt","path_lower":"/two.txt","path_display":"/two.txt","size":1,"rev":"r2"}],"cursor":"cur-2","has_more":true}""")
                : Json("""{"entries":[{".tag":"file","id":"id:3","name":"three.txt","path_lower":"/three.txt","path_display":"/three.txt","size":1,"rev":"r3"}],"cursor":"cur-3","has_more":false}""");

        CloudTreeListing listing = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 100, CancellationToken.None);

        listing.Files.Select(f => f.RelativePath).Should().Equal("one.txt", "two.txt", "three.txt");
        _handler.Requests.Select(x => x.Uri.AbsolutePath).Should().Equal(
            "/2/files/list_folder", "/2/files/list_folder/continue", "/2/files/list_folder/continue");
    }

    [Fact]
    public async Task The_tree_stops_at_max_entries_and_reports_truncation()
    {
        _handler.Respond = r => Json("""
            {"entries":[
              {".tag":"file","id":"id:1","name":"1.txt","path_lower":"/1.txt","path_display":"/1.txt","size":1,"rev":"a"},
              {".tag":"file","id":"id:2","name":"2.txt","path_lower":"/2.txt","path_display":"/2.txt","size":1,"rev":"b"},
              {".tag":"file","id":"id:3","name":"3.txt","path_lower":"/3.txt","path_display":"/3.txt","size":1,"rev":"c"}
            ],"cursor":"c","has_more":false}
            """);

        CloudTreeListing truncated = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 2, CancellationToken.None);
        truncated.Files.Should().HaveCount(2);
        truncated.Truncated.Should().BeTrue();

        CloudTreeListing exact = await Provider().ListTreeAsync(AccessToken, new CloudLocation(null, null), 3, CancellationToken.None);
        exact.Files.Should().HaveCount(3);
        exact.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Team_space_listing_and_download_carry_the_path_root_header()
    {
        _handler.Respond = r => r.Uri.AbsolutePath switch
        {
            "/2/files/list_folder" => Json("""{"entries":[{".tag":"file","id":"id:T","name":"t.txt","path_lower":"/t.txt","path_display":"/t.txt","size":1,"rev":"r"}],"cursor":"c","has_more":false}"""),
            "/2/files/download" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("t") },
            _ => NotFound()
        };
        DropboxStorageProvider provider = Provider();
        var team = new CloudLocation(TeamNs, null);

        CloudTreeListing listing = await provider.ListTreeAsync(AccessToken, team, 10, CancellationToken.None);
        await using (CloudDownload _ = await provider.OpenDownloadAsync(AccessToken, team, listing.Files.Single(), CancellationToken.None))
        {
        }

        _handler.Requests.Should().HaveCount(2);
        _handler.Requests.Should().OnlyContain(x => x.PathRoot == "{\".tag\":\"namespace_id\",\"namespace_id\":\"" + TeamNs + "\"}");
    }

    [Fact]
    public async Task The_download_argument_header_is_ascii_only()
    {
        _handler.Respond = r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        var entry = new CloudFileEntry("/Café/日本.txt", "Café/日本.txt", 1, "r", null);

        await using (CloudDownload _ = await Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None))
        {
        }

        string header = _handler.Requests.Single().ApiArg!;
        header.Should().Be("{\"path\":\"/Caf\\u00e9/\\u65e5\\u672c.txt\"}");
        header.All(c => c <= '\u007E').Should().BeTrue("an HTTP header value must be ASCII");
        using JsonDocument doc = JsonDocument.Parse(header);
        doc.RootElement.GetProperty("path").GetString().Should().Be("/Café/日本.txt");
    }

    [Fact]
    public void AsciiJson_escapes_every_character_above_0x7E_including_surrogate_pairs()
    {
        string escaped = DropboxStorageProvider.AsciiJson("{\"path\":\"/a\u007Fé🎬\"}");

        escaped.Should().Be("{\"path\":\"/a\\u007f\\u00e9\\ud83c\\udfac\"}");
        JsonDocument.Parse(escaped).RootElement.GetProperty("path").GetString().Should().Be("/a\u007Fé🎬");
    }

    // ------------------------------------------------------------------ errors

    [Fact]
    public async Task A_429_is_retried_and_then_succeeds()
    {
        int calls = 0;
        _handler.Respond = r =>
        {
            if (++calls > 1) return Json(AccountJson("100", "100"));
            HttpResponseMessage limited = Json("""{"error_summary":"too_many_requests/"}""", HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return limited;
        };

        CloudAccountInfo account = await Provider().GetAccountAsync(AccessToken, CancellationToken.None);

        account.AccountId.Should().Be("dbid:AAA");
        _handler.Requests.Should().HaveCount(2);
        _delays.Should().Equal(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_401_is_Unauthorized()
    {
        _handler.Respond = r => Json("""{"error_summary":"expired_access_token/"}""", HttpStatusCode.Unauthorized);

        Func<Task> act = () => Provider().ListDrivesAsync(AccessToken, CancellationToken.None);

        (await act.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Unauthorized);
        _handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_409_not_found_download_is_NotFound_and_other_409s_are_InvalidResponse()
    {
        var entry = new CloudFileEntry("id:X", "x.mp4", 1, "r", null);

        _handler.Respond = r => Json("""{"error_summary":"path/not_found/."}""", HttpStatusCode.Conflict);
        Func<Task> gone = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        (await gone.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.NotFound);

        _handler.Respond = r => Json("""{"error_summary":"path/restricted_content/"}""", HttpStatusCode.Conflict);
        Func<Task> restricted = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        (await restricted.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.Forbidden);

        _handler.Respond = r => Json("""{"error_summary":"path/malformed_path/"}""", HttpStatusCode.Conflict);
        Func<Task> other = () => Provider().OpenDownloadAsync(AccessToken, new CloudLocation(null, null), entry, CancellationToken.None);
        (await other.Should().ThrowAsync<CloudStorageApiException>()).Which.Kind.Should().Be(CloudStorageErrorKind.InvalidResponse);

        _handler.Requests.Should().HaveCount(3, "no 409 is retried");
    }

    // ------------------------------------------------------------------ fake Dropbox API

    private static string AccountJson(string rootNs, string homeNs) => $$$"""
        {"account_id":"dbid:AAA","name":{"given_name":"Ann","surname":"Example","display_name":"Ann Example"},"email":"ann@example.com",
         "root_info":{".tag":"{{{(rootNs == homeNs ? "user" : "team")}}}","root_namespace_id":"{{{rootNs}}}","home_namespace_id":"{{{homeNs}}}"}}
        """;

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        Json("""{"error_summary":"path/not_found/"}""", HttpStatusCode.Conflict);

    private static Dictionary<string, string> ParseForm(string query)
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

    private sealed record RecordedRequest(
        HttpMethod Method, Uri Uri, string? Authorization, string? ContentType, string? Body, string? PathRoot, string? ApiArg)
    {
        public JsonElement JsonBody() => JsonDocument.Parse(Body!).RootElement.Clone();
    }

    private sealed class FakeDropboxHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentType?.ToString(),
                body,
                Header(request, "Dropbox-API-Path-Root"),
                Header(request, "Dropbox-API-Arg"));
            lock (Requests) Requests.Add(recorded);
            HttpResponseMessage response = Respond(recorded);
            response.RequestMessage = request;
            return response;
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
    }
}
