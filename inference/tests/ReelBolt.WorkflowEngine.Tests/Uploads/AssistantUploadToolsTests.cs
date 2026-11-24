#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using Moq;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The assistant/MCP binary I/O tools (T-002): <c>ProposeUploadFile</c>/<c>ApplyUploadFile</c> over the
/// real <see cref="DirectUploadService"/> (real ticket MAC, in-memory object and multipart stores), and
/// <c>ProposeImportFileFromUrl</c>/<c>ApplyImportFileFromUrl</c> over the real <see cref="UrlImportService"/>
/// with a stub network. Every refusal is shown to write nothing.
/// </summary>
public class AssistantUploadToolsTests
{
    private const long MiB = 1024 * 1024;

    private sealed class User(Guid id, Guid? organizationId, OrganizationRole? role, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; set; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified { get; } = verified;
    }

    private sealed class Rig
    {
        public Rig(bool verified = true, IPresignedUrlService? presigner = null)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"assistant-upload-{Guid.NewGuid():N}")
                .Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch Promo", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
            OtherOrgProject = new Project { Id = Guid.NewGuid(), Name = "Someone else's", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
            Db.Projects.AddRange(Project, OtherOrgProject);
            Db.SaveChanges();

            Caller = new User(Guid.NewGuid(), Project.OrganizationId, OrganizationRole.Member, verified);
            Uploads = new DirectUploadService(Db, Storage, presigner ?? Presign, Tokens, Multipart, PlanGate, Clock);
            Importer = new UrlImportService(
                Db, ImportStorage, Effects, Options.Create(new UrlImportOptions { MaxBytes = 10 * MiB, TimeoutSeconds = 30 }),
                new CapturingLogger<UrlImportService>(), PlanGate, Upstream);
        }

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public Project OtherOrgProject { get; }
        public User Caller { get; }
        public UploadsObjectStore Storage { get; } = new();
        public ImportObjectStore ImportStorage { get; } = new();
        public FakePresignedUrlService Presign { get; } = new();
        public ConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public UploadsMultipartStore Multipart { get; } = new();
        public UploadsPlanGate PlanGate { get; } = new();
        public UploadsClock Clock { get; } = new();
        public RecordingFileAddedEffects Effects { get; } = new();
        public StubUpstream Upstream { get; } = new();
        public DirectUploadService Uploads { get; }
        public UrlImportService Importer { get; }

        public AssistantUploadTools Tools(ICurrentUser? asUser = null)
        {
            ICurrentUser user = asUser ?? Caller;
            return new AssistantUploadTools(new ProjectAccessService(Db, user), user, Tokens, Uploads, Importer, Effects, Db);
        }

        public AssistantToolContext Context(ICurrentUser? asUser = null) => new((asUser ?? Caller).UserId, Project.Id);

        public async Task<JsonObject> Call(string tool, params (string Key, object? Value)[] arguments) =>
            await CallAs(Caller, tool, arguments);

        public async Task<JsonObject> CallAs(ICurrentUser user, string tool, params (string Key, object? Value)[] arguments)
        {
            AIFunction function = Tools(user).GetTools(Context(user)).Single(t => t.Name == tool);
            AIFunctionArguments args = new();
            foreach ((string key, object? value) in arguments)
                args[key] = value;
            object? raw = await function.InvokeAsync(args);
            string text = raw switch
            {
                JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw?.ToString() ?? string.Empty
            };
            return JsonNode.Parse(text)!.AsObject();
        }

        public bool NoFileRecorded => !Db.ProjectFiles.Any();
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Rewrite(string uploadToken, Action<JsonObject> edit)
    {
        string[] parts = uploadToken.Split('.');
        string b64 = parts[1].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        JsonObject payload = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!.AsObject();
        edit(payload);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{parts[0]}.{encoded}";
    }

    // ================================================================= ProposeUploadFile

    [Fact]
    public async Task A_small_file_gets_one_presigned_PUT_and_no_storage_key_outside_its_url()
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "Soundtrack.mp3"), ("sizeBytes", 1400000L), ("contentType", "audio/mpeg"), ("directoryPath", "audio"));

        answer["error"].Should().BeNull(answer.ToJsonString());
        answer["projectName"]!.GetValue<string>().Should().Be("Launch Promo");
        answer["method"]!.GetValue<string>().Should().Be("PUT");
        answer["url"]!.GetValue<string>().Should().StartWith("https://objects.test/browser-put");
        answer["parts"].Should().BeNull();
        answer["partCount"]!.GetValue<int>().Should().Be(1);
        answer["headers"]!["Content-Type"]!.GetValue<string>().Should().Be("audio/mpeg");
        answer["maxBytes"]!.GetValue<long>().Should().Be(1400000L);
        answer["expiresAt"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        answer["instructions"]!.GetValue<string>().Should().Contain("PUT").And.Contain("ApplyUploadFile");
        answer["uploadToken"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        string key = rig.Presign.BrowserPuts.Single().Key;
        key.Should().StartWith($"projects/{rig.Project.Id}/userFiles/audio/");
        answer.Select(p => p.Key).Should().NotContain(k => k.Contains("key", StringComparison.OrdinalIgnoreCase) && k != "uploadToken");
        answer["url"]!.GetValue<string>().Should().Contain(Uri.EscapeDataString(key), "the PUT url is the one place the key may appear");
        JsonObject withoutUrl = answer.DeepClone().AsObject();
        withoutUrl.Remove("url");
        withoutUrl.ToJsonString().Should().NotContain(key).And.NotContain(Uri.EscapeDataString(key)).And.NotContain("projects/");
        rig.NoFileRecorded.Should().BeTrue("a proposal records nothing");
    }

    /// <summary>
    /// QA-P0-12: with the REAL presigner (SigV4 signing is local; no network), the storage key and the
    /// bucket appear in the answer only inside the PUT url, and nowhere else: not as a field, not in the
    /// decoded uploadToken, not in the instructions.
    /// </summary>
    [Fact]
    public async Task With_a_real_presigner_the_key_and_bucket_appear_only_inside_the_PUT_url()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:Endpoint"] = "http://garage:3900",
            ["MinIO:PublicEndpoint"] = "https://s3.example.com",
            ["MinIO:AccessKey"] = "AK",
            ["MinIO:SecretKey"] = "SK-SK-SK-SK-SK-SK-SK",
            ["MinIO:BucketName"] = "reelbolt-media-bucket",
            ["MinIO:Region"] = "garage"
        }).Build();
        string publicEndpoint = S3ClientFactory.ResolvePublicEndpoint(config);
        PresignedUrlService real = new(new Mock<IAmazonS3>().Object, S3ClientFactory.Create(config, publicEndpoint), "reelbolt-media-bucket", publicEndpoint);
        Rig rig = new(presigner: real);

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "Soundtrack.mp3"), ("sizeBytes", 1400000L), ("contentType", "audio/mpeg"), ("directoryPath", "audio"));

        answer["error"].Should().BeNull(answer.ToJsonString());
        string url = answer["url"]!.GetValue<string>();
        url.Should().StartWith("https://s3.example.com/reelbolt-media-bucket/projects/").And.Contain("X-Amz-Signature");
        string keyPath = new Uri(url).AbsolutePath["/reelbolt-media-bucket/".Length..];
        string key = Uri.UnescapeDataString(keyPath);
        key.Should().StartWith($"projects/{rig.Project.Id}/userFiles/audio/");

        JsonObject withoutUrl = answer.DeepClone().AsObject();
        withoutUrl.Remove("url");
        string token = withoutUrl["uploadToken"]!.GetValue<string>();
        string b64 = token["rbu1.".Length..].Replace('-', '+').Replace('_', '/');
        string decodedToken = Encoding.UTF8.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
        string[] haystacks = [withoutUrl.ToJsonString(), decodedToken];
        foreach (string haystack in haystacks)
        {
            haystack.Should().NotContain(key).And.NotContain(keyPath).And.NotContain(Uri.EscapeDataString(key));
            haystack.Should().NotContain("reelbolt-media-bucket").And.NotContain("X-Amz-").And.NotContain("projects/");
            haystack.Should().NotContain("storageKey").And.NotContain("storage_key").And.NotContain("\"key\"");
        }
    }

    [Fact]
    public async Task A_proposed_PUT_url_lives_at_most_15_minutes()
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));

        rig.Presign.BrowserPuts.Single().Ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        DateTimeOffset.Parse(answer["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeOnOrBefore(rig.Clock.Now + TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Proposed_part_urls_live_at_most_30_minutes()
    {
        Rig rig = new();

        await rig.Call("ProposeUploadFile", ("fileName", "master.mov"), ("sizeBytes", 300 * MiB));

        rig.Multipart.Presigned.Should().NotBeEmpty().And.OnlyContain(p => p.Ttl <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task A_re_PUT_after_apply_cannot_change_the_recorded_file_and_a_second_apply_returns_the_same_file()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        string token = proposal["uploadToken"]!.GetValue<string>();
        string presignedKey = rig.Presign.BrowserPuts.Single().Key;
        rig.Storage.Put(presignedKey, 100);

        JsonObject first = await rig.Call("ApplyUploadFile", ("uploadToken", token));
        rig.Storage.Put(presignedKey, 50_000);
        JsonObject again = await rig.Call("ApplyUploadFile", ("uploadToken", token));

        first["error"].Should().BeNull(first.ToJsonString());
        again["fileId"]!.GetValue<string>().Should().Be(first["fileId"]!.GetValue<string>());
        ProjectFile row = rig.Db.ProjectFiles.Single();
        row.StorageKey.Should().NotBe(presignedKey);
        row.SizeBytes.Should().Be(100);
        rig.Storage.Sizes[row.StorageKey].Should().Be(100);
        rig.Storage.Sizes.Should().NotContainKey(presignedKey);
        rig.Effects.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Stripping_the_relocation_flag_from_the_upload_token_is_refused()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 100);
        string stripped = Rewrite(proposal["uploadToken"]!.GetValue<string>(), payload => payload.Remove("rl"));

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", stripped));

        applied["code"]!.GetValue<string>().Should().Be("UPLOAD_TICKET_INVALID");
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_large_file_gets_every_part_presigned_with_its_part_size()
    {
        Rig rig = new();
        long size = 300 * MiB;

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "master.mov"), ("sizeBytes", size));

        answer["url"].Should().BeNull();
        answer["partSizeBytes"]!.GetValue<long>().Should().Be(64 * MiB);
        answer["partCount"]!.GetValue<int>().Should().Be(5);
        JsonArray parts = answer["parts"]!.AsArray();
        parts.Select(p => p!["partNumber"]!.GetValue<int>()).Should().Equal(1, 2, 3, 4, 5);
        parts.Should().OnlyContain(p => p!["url"]!.GetValue<string>().StartsWith("https://objects.test/part/"));
        answer["instructions"]!.GetValue<string>().Should().Contain("ETag");
        rig.Multipart.Created.Should().ContainSingle();
    }

    [Fact]
    public async Task More_than_100_parts_is_refused_before_any_multipart_upload_is_opened()
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "huge.mov"), ("sizeBytes", 101 * 64 * MiB));

        answer["error"]!.GetValue<string>().Should().Contain("100");
        answer["uploadToken"].Should().BeNull();
        rig.Multipart.Created.Should().BeEmpty();
        rig.PlanGate.UploadChecks.Should().BeEmpty();
    }

    [Fact]
    public async Task The_quota_is_checked_on_the_claimed_size()
    {
        Rig rig = new();
        rig.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 5000L));

        answer["code"]!.GetValue<string>().Should().Be("STORAGE_QUOTA_EXCEEDED");
        answer["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        answer["uploadToken"].Should().BeNull();
        rig.Presign.BrowserPuts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_of_another_organization_is_not_found_and_nothing_is_presigned()
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 10L), ("projectId", rig.OtherOrgProject.Id));

        answer["error"]!.GetValue<string>().Should().Be("project not found");
        rig.Presign.BrowserPuts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unverified_email_is_refused_for_every_write()
    {
        Rig rig = new(verified: false);

        (await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 10L)))["code"]!.GetValue<string>().Should().Be("EMAIL_NOT_VERIFIED");
        (await rig.Call("ApplyUploadFile", ("uploadToken", "rbu1.x")))["code"]!.GetValue<string>().Should().Be("EMAIL_NOT_VERIFIED");
        (await rig.Call("ProposeImportFileFromUrl", ("url", "https://cdn.example.com/a.mp4")))["code"]!.GetValue<string>().Should().Be("EMAIL_NOT_VERIFIED");
        (await rig.Call("ApplyImportFileFromUrl", ("url", "https://cdn.example.com/a.mp4"), ("confirmationToken", "x")))["code"]!.GetValue<string>().Should().Be("EMAIL_NOT_VERIFIED");
        rig.Presign.BrowserPuts.Should().BeEmpty();
        rig.Upstream.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../etc", "a.mp4")]
    [InlineData(null, "../a.mp4")]
    [InlineData(null, "")]
    public async Task A_traversing_or_empty_path_is_refused(string? directoryPath, string fileName)
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeUploadFile", ("fileName", fileName), ("sizeBytes", 10L), ("directoryPath", directoryPath));

        answer["error"].Should().NotBeNull();
        rig.Presign.BrowserPuts.Should().BeEmpty();
    }

    // ================================================================= ApplyUploadFile

    [Fact]
    public async Task Apply_records_the_single_PUT_upload_once_and_is_idempotent()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "Soundtrack.mp3"), ("sizeBytes", 1400L), ("directoryPath", "audio"));
        string token = proposal["uploadToken"]!.GetValue<string>();
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 1400);

        JsonObject first = await rig.Call("ApplyUploadFile", ("uploadToken", token));
        JsonObject again = await rig.Call("ApplyUploadFile", ("uploadToken", token));

        first["error"].Should().BeNull(first.ToJsonString());
        first["fileName"]!.GetValue<string>().Should().Be("Soundtrack.mp3");
        first["directoryPath"]!.GetValue<string>().Should().Be("audio");
        first["sizeBytes"]!.GetValue<long>().Should().Be(1400);
        first["mimeType"]!.GetValue<string>().Should().Be("audio/mpeg");
        Guid fileId = Guid.Parse(first["fileId"]!.GetValue<string>());
        again["fileId"]!.GetValue<string>().Should().Be(fileId.ToString());
        rig.Db.ProjectFiles.Should().ContainSingle(f => f.Id == fileId && f.Category == "userFiles" && f.ProjectId == rig.Project.Id);
        rig.Effects.Runs.Should().Equal((fileId, rig.Caller.UserId));
        first.ToJsonString().Should().NotContain("storageKey").And.NotContain(rig.Presign.BrowserPuts.Single().Key);
    }

    [Fact]
    public async Task Apply_completes_a_multipart_upload_with_the_parts_and_etags_given()
    {
        Rig rig = new();
        long size = 300 * MiB;
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "master.mov"), ("sizeBytes", size));
        string token = proposal["uploadToken"]!.GetValue<string>();
        rig.Storage.Put(rig.Multipart.Created.Single().Key, size);
        JsonElement parts = Json("""[{"partNumber":1,"etag":"\"e1\""},{"partNumber":2,"etag":"\"e2\""},{"partNumber":3,"etag":"\"e3\""},{"partNumber":4,"etag":"\"e4\""},{"partNumber":5,"etag":"\"e5\""}]""");

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", token), ("parts", parts));

        applied["error"].Should().BeNull(applied.ToJsonString());
        rig.Multipart.Completed.Single().Parts.Select(p => p.ETag).Should().Equal("\"e1\"", "\"e2\"", "\"e3\"", "\"e4\"", "\"e5\"");
        rig.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Apply_without_every_part_is_refused_and_records_nothing()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "master.mov"), ("sizeBytes", 300 * MiB));

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", proposal["uploadToken"]!.GetValue<string>()), ("parts", Json("""[{"partNumber":1,"etag":"e1"}]""")));

        applied["code"]!.GetValue<string>().Should().Be("UPLOAD_INCOMPLETE");
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Theory]
    [InlineData("s")]
    [InlineData("f")]
    [InlineData("n")]
    [InlineData("p")]
    [InlineData("e")]
    public async Task A_tampered_upload_token_is_refused_before_storage_is_consulted(string field)
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        string token = proposal["uploadToken"]!.GetValue<string>();
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 100);
        string tampered = Rewrite(token, payload => payload[field] = field switch
        {
            "s" => 5000L,
            "f" => Guid.NewGuid().ToString(),
            "n" => "other.mp4",
            "p" => rig.Project.Id == Guid.Empty ? null : Guid.NewGuid().ToString(),
            _ => payload[field]!.GetValue<long>() + 86400
        });

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", tampered));

        applied["error"].Should().NotBeNull();
        rig.NoFileRecorded.Should().BeTrue();
        rig.Storage.MetadataReads.Should().Be(0, "a forged ticket never reaches storage");
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("rbu1.%%%")]
    [InlineData("rbu1.e30")]
    public async Task A_malformed_upload_token_is_refused(string token)
    {
        Rig rig = new();

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", token));

        applied["error"].Should().NotBeNull();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task Another_users_upload_token_is_refused()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 100);
        User colleague = new(Guid.NewGuid(), rig.Project.OrganizationId, OrganizationRole.Member);

        JsonObject applied = await rig.CallAs(colleague, "ApplyUploadFile", ("uploadToken", proposal["uploadToken"]!.GetValue<string>()));

        applied["error"].Should().NotBeNull();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_token_applied_against_a_different_project_is_refused()
    {
        Rig rig = new();
        Project second = new() { Id = Guid.NewGuid(), Name = "Second", OwnerId = Guid.NewGuid(), OrganizationId = rig.Project.OrganizationId };
        rig.Db.Projects.Add(second);
        rig.Db.SaveChanges();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 100);

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", proposal["uploadToken"]!.GetValue<string>()), ("projectId", second.Id));

        applied["error"].Should().NotBeNull();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_who_lost_access_to_the_project_cannot_apply_their_own_token()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        rig.Storage.Put(rig.Presign.BrowserPuts.Single().Key, 100);
        rig.Caller.OrganizationId = Guid.NewGuid();

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", proposal["uploadToken"]!.GetValue<string>()));

        applied["error"]!.GetValue<string>().Should().Be("project not found");
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task A_size_that_is_not_what_was_proposed_is_refused_and_discarded()
    {
        Rig rig = new();
        JsonObject proposal = await rig.Call("ProposeUploadFile", ("fileName", "a.mp4"), ("sizeBytes", 100L));
        string key = rig.Presign.BrowserPuts.Single().Key;
        rig.Storage.Put(key, 9999);

        JsonObject applied = await rig.Call("ApplyUploadFile", ("uploadToken", proposal["uploadToken"]!.GetValue<string>()));

        applied["code"]!.GetValue<string>().Should().Be("UPLOAD_SIZE_MISMATCH");
        rig.Storage.DeletedKeys.Should().Contain(key);
        rig.NoFileRecorded.Should().BeTrue();
    }

    // ================================================================= URL import

    [Theory]
    [InlineData("http://cdn.example.com/a.mp4")]
    [InlineData("https://user:pw@cdn.example.com/a.mp4")]
    [InlineData("https://127.0.0.1/a.mp4")]
    [InlineData("https://10.2.3.4/a.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/a.mp4")]
    [InlineData("https://nas/a.mp4")]
    public async Task ProposeImport_refuses_a_non_public_url_and_issues_no_token(string url)
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeImportFileFromUrl", ("url", url));

        answer["code"]!.GetValue<string>().Should().Be("IMPORT_URL_REFUSED");
        answer["confirmationToken"].Should().BeNull();
        rig.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ProposeImport_makes_no_request_and_shows_only_the_host()
    {
        Rig rig = new();

        JsonObject answer = await rig.Call("ProposeImportFileFromUrl", ("url", "https://cdn.example.com/secret/path/clip.mp4?X-Amz-Signature=abc"), ("directoryPath", "plates"));

        answer["confirmationToken"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        answer["source"]!.GetValue<string>().Should().Be("https://cdn.example.com");
        answer["fileName"]!.GetValue<string>().Should().Be("clip.mp4");
        answer["targetPath"]!.GetValue<string>().Should().Be("plates/clip.mp4");
        answer["projectName"]!.GetValue<string>().Should().Be("Launch Promo");
        answer.ToJsonString().Should().NotContain("X-Amz-Signature").And.NotContain("secret/path");
        rig.Upstream.Requests.Should().BeEmpty();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyImport_with_the_proposed_token_imports_the_file()
    {
        Rig rig = new();
        const string url = "https://cdn.example.com/plates/room.mp4?sig=1";
        rig.Upstream.Serve(url, new byte[1234]);
        JsonObject proposal = await rig.Call("ProposeImportFileFromUrl", ("url", url), ("directoryPath", "plates"));

        JsonObject applied = await rig.Call("ApplyImportFileFromUrl",
            ("url", url), ("directoryPath", "plates"), ("confirmationToken", proposal["confirmationToken"]!.GetValue<string>()));

        applied["error"].Should().BeNull(applied.ToJsonString());
        applied["fileName"]!.GetValue<string>().Should().Be("room.mp4");
        applied["directoryPath"]!.GetValue<string>().Should().Be("plates");
        applied["sizeBytes"]!.GetValue<long>().Should().Be(1234);
        applied["mimeType"]!.GetValue<string>().Should().Be("video/mp4");
        rig.Db.ProjectFiles.Should().ContainSingle(f => f.Id == Guid.Parse(applied["fileId"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("fileName")]
    [InlineData("directoryPath")]
    [InlineData("token")]
    [InlineData("project")]
    public async Task ApplyImport_refuses_a_token_that_does_not_match_and_fetches_nothing(string change)
    {
        Rig rig = new();
        Project second = new() { Id = Guid.NewGuid(), Name = "Second", OwnerId = Guid.NewGuid(), OrganizationId = rig.Project.OrganizationId };
        rig.Db.Projects.Add(second);
        rig.Db.SaveChanges();
        const string url = "https://cdn.example.com/a.mp4";
        rig.Upstream.Serve(url, new byte[10]);
        rig.Upstream.Serve("https://evil.example.com/a.mp4", new byte[10]);
        string token = (await rig.Call("ProposeImportFileFromUrl", ("url", url)))["confirmationToken"]!.GetValue<string>();

        JsonObject applied = await rig.Call("ApplyImportFileFromUrl",
            ("url", change == "url" ? "https://evil.example.com/a.mp4" : url),
            ("fileName", change == "fileName" ? "renamed.mp4" : null),
            ("directoryPath", change == "directoryPath" ? "elsewhere" : null),
            ("confirmationToken", change == "token" ? token[..^2] + "AA" : token),
            ("projectId", change == "project" ? second.Id : (Guid?)null));

        applied["error"].Should().NotBeNull();
        rig.Upstream.Requests.Should().BeEmpty();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyImport_token_stops_verifying_when_the_caller_switches_organization()
    {
        Rig rig = new();
        const string url = "https://cdn.example.com/a.mp4";
        string token = (await rig.Call("ProposeImportFileFromUrl", ("url", url)))["confirmationToken"]!.GetValue<string>();
        rig.Caller.OrganizationId = Guid.NewGuid();

        JsonObject applied = await rig.Call("ApplyImportFileFromUrl", ("url", url), ("confirmationToken", token));

        applied["error"].Should().NotBeNull();
        rig.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyImport_passes_a_refusal_through_with_its_code()
    {
        Rig rig = new();
        const string url = "https://cdn.example.com/gone.mp4";
        rig.Upstream.Status(url, System.Net.HttpStatusCode.NotFound);
        string token = (await rig.Call("ProposeImportFileFromUrl", ("url", url)))["confirmationToken"]!.GetValue<string>();

        JsonObject applied = await rig.Call("ApplyImportFileFromUrl", ("url", url), ("confirmationToken", token));

        applied["code"]!.GetValue<string>().Should().Be("IMPORT_FAILED");
        applied["upstreamStatus"]!.GetValue<int>().Should().Be(404);
        applied["error"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        rig.NoFileRecorded.Should().BeTrue();
    }

    [Fact]
    public void The_tool_set_is_exactly_the_four_gated_pairs_with_descriptions()
    {
        Rig rig = new();

        IReadOnlyList<AIFunction> tools = rig.Tools().GetTools(rig.Context());

        tools.Select(t => t.Name).Should().Equal("ProposeUploadFile", "ApplyUploadFile", "ProposeImportFileFromUrl", "ApplyImportFileFromUrl");
        tools.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Description));
        tools.Should().OnlyContain(t => t.JsonSchema.GetRawText().Contains("projectId"));
    }
}
