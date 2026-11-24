#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Compute;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// <c>activation.file_uploaded</c> (docs/design/new-user-funnel.md section 4, docs/product-events.md): the
/// first file a person adds by their own upload, once per user. It is an event because <c>project_files</c>
/// cannot say it (a row names no uploader, a move rewrites its <c>uploaded_at</c>, a delete removes it).
/// These tests show each of the five user upload paths writes it (form upload, a direct upload's completion,
/// an import from a link, and the assistant's upload and import tools), that a refusal, a replay and a
/// quarantined file do not, and that no other code path names the event.
/// </summary>
public class FirstUploadEventTests
{
    private const string FileUploaded = "activation.file_uploaded";

    private static readonly byte[] Clip = Enumerable.Range(0, 2048).Select(i => (byte)i).ToArray();

    private sealed class User(Guid id, Guid? organizationId, OrganizationRole? role, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
        public bool EmailVerified { get; } = verified;
    }

    private sealed class FormFile(string fileName, string contentType, byte[] content) : IFormFile
    {
        public string ContentType { get; } = contentType;
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => content.Length;
        public string Name => "file";
        public string FileName { get; } = fileName;
        public Stream OpenReadStream() => new MemoryStream(content, writable: false);
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>The post-add path of a file whose scan matched: the row is marked quarantined, as the real path does.</summary>
    private sealed class QuarantiningEffects : IProjectFileAddedEffects
    {
        public Task RunAsync(ProjectFile file, Guid userId, CancellationToken ct)
        {
            file.CsamStatus = CsamScanStatus.Quarantined;
            return Task.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public UrlImportHarness Import { get; } = new();
        public RecordingProductEventEmitter Events { get; } = new();
        public UploadsObjectStore DirectStorage { get; } = new();
        public FakePresignedUrlService Presign { get; } = new();
        public ConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public ChannelBackgroundTaskQueue<FileSummarizationTask> Queue { get; } = new(100);

        public Project Project => Import.Project;

        public User Member(bool verified = true) => new(Guid.NewGuid(), Project.OrganizationId, OrganizationRole.Member, verified);

        public ProjectFilesController Controller(
            ICurrentUser user, IFileStorageService? storage = null, IProjectFileAddedEffects? effects = null, bool withEvents = true)
        {
            IFileStorageService store = storage ?? DirectStorage;
            ProjectFilesController controller = new(
                Import.Db, user, TestTenancy.Access(Import.Db, user), store, Queue, Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                Import.PlanGate,
                Presign,
                Tokens,
                urlImport: Import.Service,
                fileAddedEffects: effects ?? new ProjectFileAddedEffects(Publish.Object, Queue, store),
                productEvents: withEvents ? Events : null);
            DefaultHttpContext http = new();
            http.Request.Form = new FormCollection(new Dictionary<string, StringValues>());
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            return controller;
        }

        /// <summary>A store the form upload can post bytes through.</summary>
        public static IFileStorageService FormStorage()
        {
            Mock<IFileStorageService> storage = new();
            storage
                .Setup(s => s.UploadAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new StoredFileObject(
                    "projects/p/userFiles/clip.mp4", "bucket", "projects/p/userFiles", null, "userFiles", "clip.mp4", "clip.mp4", null, "clip.mp4"));
            return storage.Object;
        }

        public AssistantUploadTools Tools(ICurrentUser user) => new(
            new ProjectAccessService(Import.Db, user), user, Tokens,
            new DirectUploadService(Import.Db, DirectStorage, Presign, Tokens, new UploadsMultipartStore(), Import.PlanGate, new UploadsClock()),
            Import.Service, new RecordingFileAddedEffects(), Import.Db, Events);

        public async Task<JsonObject> Call(ICurrentUser user, string tool, params (string Key, object? Value)[] arguments)
        {
            AIFunction function = Tools(user).GetTools(new AssistantToolContext(user.UserId, Project.Id)).Single(t => t.Name == tool);
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
    }

    private static int StatusOf(IActionResult? result) => result switch
    {
        NotFoundResult => 404,
        ForbidResult => 403,
        ObjectResult o => o.StatusCode ?? 200,
        _ => 0
    };

    private static (Guid, Guid?, string) Expected(Rig rig, ICurrentUser user) => (user.UserId, rig.Project.OrganizationId, FileUploaded);

    // ------------------------------------------------------------------ the form upload

    [Fact]
    public async Task The_form_upload_records_the_callers_first_upload()
    {
        Rig rig = new();
        User member = rig.Member();

        ActionResult<ProjectFileResponse> result = await rig.Controller(member, Rig.FormStorage())
            .Upload(rig.Project.Id, new FormFile("clip.mp4", "video/mp4", [0, 0, 0, 0x18]), CancellationToken.None);

        StatusOf(result.Result).Should().Be(201);
        rig.Events.Firsts.Should().Equal(Expected(rig, member));
        rig.Events.Appends.Should().BeEmpty("a first upload is a once-per-user claim, never a plain append");
    }

    [Fact]
    public async Task A_quarantined_form_upload_records_nothing()
    {
        Rig rig = new();

        ActionResult<ProjectFileResponse> result = await rig.Controller(rig.Member(), Rig.FormStorage(), new QuarantiningEffects())
            .Upload(rig.Project.Id, new FormFile("clip.mp4", "video/mp4", [0, 0, 0, 0x18]), CancellationToken.None);

        StatusOf(result.Result).Should().Be(422);
        rig.Events.Firsts.Should().BeEmpty("a file that cannot be used is not an upload the funnel counts");
    }

    [Fact]
    public async Task A_caller_without_edit_rights_uploads_nothing_and_records_nothing()
    {
        Rig rig = new();
        User outsider = new(Guid.NewGuid(), Guid.NewGuid(), OrganizationRole.Owner);

        ActionResult<ProjectFileResponse> result = await rig.Controller(outsider, Rig.FormStorage())
            .Upload(rig.Project.Id, new FormFile("clip.mp4", "video/mp4", [0, 0, 0, 0x18]), CancellationToken.None);

        StatusOf(result.Result).Should().Be(404);
        rig.Events.Firsts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ direct upload

    private static async Task<(ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket Ticket, CompleteDirectUploadRequest Completion)> TicketAsync(
        Rig rig, ProjectFilesController controller)
    {
        ActionResult<ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket> created = await controller.CreateUploadUrl(
            rig.Project.Id, new CreateDirectUploadRequest("clip.mp4", 9, "video/mp4", null), CancellationToken.None);
        var ticket = (ReelBolt.Inference.Api.Controllers.Dto.DirectUploadTicket)((OkObjectResult)created.Result!).Value!;
        return (ticket, new CompleteDirectUploadRequest(
            ticket.UploadToken, ticket.Key, ticket.FileId, "clip.mp4", 9, ticket.ExpiresAtUnix, ticket.ContentType, null));
    }

    [Fact]
    public async Task A_direct_upload_records_it_when_the_completion_writes_the_row_and_not_on_a_replay()
    {
        Rig rig = new();
        User member = rig.Member();
        ProjectFilesController controller = rig.Controller(member);

        (var ticket, CompleteDirectUploadRequest completion) = await TicketAsync(rig, controller);
        rig.Events.Firsts.Should().BeEmpty("asking for a place to upload is not an upload");
        rig.DirectStorage.Put(ticket.Key, 9);

        StatusOf((await controller.CompleteUpload(rig.Project.Id, completion, CancellationToken.None)).Result).Should().Be(201);
        rig.Events.Firsts.Should().Equal(Expected(rig, member));

        StatusOf((await controller.CompleteUpload(rig.Project.Id, completion, CancellationToken.None)).Result).Should().Be(200);
        rig.Events.Firsts.Should().HaveCount(1, "a repeated completion is the same upload again");
    }

    [Fact]
    public async Task A_refused_direct_upload_completion_records_nothing()
    {
        Rig rig = new();
        ProjectFilesController controller = rig.Controller(rig.Member());
        (_, CompleteDirectUploadRequest completion) = await TicketAsync(rig, controller);

        // Nothing was ever PUT: the object the ticket names does not exist.
        ActionResult<ProjectFileResponse> result = await controller.CompleteUpload(rig.Project.Id, completion, CancellationToken.None);

        StatusOf(result.Result).Should().BeGreaterThanOrEqualTo(400);
        rig.Import.Db.ProjectFiles.Should().BeEmpty();
        rig.Events.Firsts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_quarantined_direct_upload_records_nothing()
    {
        Rig rig = new();
        ProjectFilesController controller = rig.Controller(rig.Member(), effects: new QuarantiningEffects());
        (var ticket, CompleteDirectUploadRequest completion) = await TicketAsync(rig, controller);
        rig.DirectStorage.Put(ticket.Key, 9);

        StatusOf((await controller.CompleteUpload(rig.Project.Id, completion, CancellationToken.None)).Result).Should().Be(422);
        rig.Events.Firsts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ import from a link

    [Fact]
    public async Task An_import_from_a_link_records_it()
    {
        Rig rig = new();
        User member = rig.Member();
        rig.Import.Upstream.Serve("https://cdn.example.com/samples/bakery.mp4", Clip);

        ActionResult<ProjectFileResponse> result = await rig.Controller(member).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/samples/bakery.mp4", null, null), CancellationToken.None);

        StatusOf(result.Result).Should().Be(201);
        rig.Events.Firsts.Should().Equal(Expected(rig, member));
    }

    [Fact]
    public async Task A_refused_import_and_an_unverified_caller_record_nothing()
    {
        Rig rig = new();
        rig.Import.Upstream.Status("https://cdn.example.com/gone.mp4", System.Net.HttpStatusCode.NotFound);
        rig.Import.Upstream.Serve("https://cdn.example.com/ok.mp4", Clip);

        StatusOf((await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/gone.mp4", null, null), CancellationToken.None)).Result)
            .Should().Be(422);
        StatusOf((await rig.Controller(rig.Member()).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest("http://cdn.example.com/ok.mp4", null, null), CancellationToken.None)).Result)
            .Should().Be(400);
        StatusOf((await rig.Controller(rig.Member(verified: false)).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/ok.mp4", null, null), CancellationToken.None)).Result)
            .Should().Be(403);

        rig.Import.Db.ProjectFiles.Should().BeEmpty();
        rig.Events.Firsts.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ the assistant's tools (chat and MCP)

    [Fact]
    public async Task The_assistants_upload_tool_records_it_once_for_the_upload_and_not_for_the_proposal_or_a_replay()
    {
        Rig rig = new();
        User member = rig.Member();

        JsonObject proposal = await rig.Call(member, "ProposeUploadFile", ("fileName", "Soundtrack.mp3"), ("sizeBytes", 1400L));
        rig.Events.Firsts.Should().BeEmpty("a proposal uploads nothing");
        rig.DirectStorage.Put(rig.Presign.BrowserPuts.Single().Key, 1400);
        string token = proposal["uploadToken"]!.GetValue<string>();

        JsonObject first = await rig.Call(member, "ApplyUploadFile", ("uploadToken", token));
        first["error"].Should().BeNull(first.ToJsonString());
        rig.Events.Firsts.Should().Equal(Expected(rig, member));

        JsonObject again = await rig.Call(member, "ApplyUploadFile", ("uploadToken", token));
        again["fileId"]!.GetValue<string>().Should().Be(first["fileId"]!.GetValue<string>());
        rig.Events.Firsts.Should().HaveCount(1, "a second apply returns the same file: it is not another upload");
    }

    [Fact]
    public async Task The_assistants_import_tool_records_it_and_a_refused_import_does_not()
    {
        Rig rig = new();
        User member = rig.Member();
        const string gone = "https://cdn.example.com/gone.mp4";
        const string url = "https://cdn.example.com/plates/room.mp4";
        rig.Import.Upstream.Status(gone, System.Net.HttpStatusCode.NotFound);
        rig.Import.Upstream.Serve(url, Clip);

        string goneToken = (await rig.Call(member, "ProposeImportFileFromUrl", ("url", gone)))["confirmationToken"]!.GetValue<string>();
        (await rig.Call(member, "ApplyImportFileFromUrl", ("url", gone), ("confirmationToken", goneToken)))["code"]!.GetValue<string>()
            .Should().Be("IMPORT_FAILED");
        string token = (await rig.Call(member, "ProposeImportFileFromUrl", ("url", url)))["confirmationToken"]!.GetValue<string>();
        rig.Events.Firsts.Should().BeEmpty("neither a proposal nor a refused import is an upload");

        JsonObject applied = await rig.Call(member, "ApplyImportFileFromUrl", ("url", url), ("confirmationToken", token));

        applied["error"].Should().BeNull(applied.ToJsonString());
        rig.Events.Firsts.Should().Equal(Expected(rig, member));
    }

    // ------------------------------------------------------------------ once per user, best effort, nowhere else

    [Fact]
    public async Task With_the_real_emitter_the_row_is_written_once_per_user_and_a_failing_log_never_fails_the_upload()
    {
        Rig rig = new();
        RecordingProductEventLog log = new();
        User first = rig.Member();
        User second = rig.Member();
        rig.Import.Upstream.Serve("https://cdn.example.com/a.mp4", Clip);
        rig.Import.Upstream.Serve("https://cdn.example.com/b.mp4", Clip);
        rig.Import.Upstream.Serve("https://cdn.example.com/c.mp4", Clip);

        ProjectFilesController As(ICurrentUser user)
        {
            ProjectFilesController controller = new(
                rig.Import.Db, user, TestTenancy.Access(rig.Import.Db, user), rig.DirectStorage, rig.Queue, rig.Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                rig.Import.PlanGate,
                urlImport: rig.Import.Service,
                productEvents: new ProductEventEmitter(log, NullLogger<ProductEventEmitter>.Instance));
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        async Task<int> Import(ICurrentUser user, string name) => StatusOf((await As(user).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest($"https://cdn.example.com/{name}.mp4", null, null), CancellationToken.None)).Result);

        (await Import(first, "a")).Should().Be(201);
        (await Import(first, "b")).Should().Be(201);
        (await Import(second, "c")).Should().Be(201);

        log.Rows.Select(r => (r.UserId, r.OrganizationId, r.Event, r.Source, r.Detail)).Should().Equal(
            (first.UserId, rig.Project.OrganizationId, FileUploaded, "server", null),
            (second.UserId, rig.Project.OrganizationId, FileUploaded, "server", null));

        // The log is down: the upload still succeeds and nothing is recorded.
        log.Throw = true;
        rig.Import.Upstream.Serve("https://cdn.example.com/d.mp4", Clip);
        (await Import(rig.Member(), "d")).Should().Be(201);
        log.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Without_an_emitter_every_upload_path_still_works()
    {
        Rig rig = new();
        rig.Import.Upstream.Serve("https://cdn.example.com/a.mp4", Clip);
        StatusOf((await rig.Controller(rig.Member(), withEvents: false).ImportFromUrl(
            rig.Project.Id, new ImportProjectFileFromUrlRequest("https://cdn.example.com/a.mp4", null, null), CancellationToken.None)).Result)
            .Should().Be(201);
        StatusOf((await rig.Controller(rig.Member(), Rig.FormStorage(), withEvents: false)
            .Upload(rig.Project.Id, new FormFile("clip.mp4", "video/mp4", [0, 0, 0, 0x18]), CancellationToken.None)).Result)
            .Should().Be(201);
        rig.Events.Firsts.Should().BeEmpty();
    }

    [Fact]
    public void Only_the_user_upload_paths_name_the_event()
    {
        // A background sync (GitHub, a cloud drive, a computer folder), an unpacked archive entry, a restored
        // file, a design import's extracted asset and an agent's own file are not a person's upload. The
        // event name is spelled once, and read in exactly these two files.
        string api = Path.Combine(RepositoryRoot(), "inference", "src", "ReelBolt.Inference.Api");
        string[] readers = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("ProductEventNames.FileUploaded", StringComparison.Ordinal)
                    || (text.Contains("\"" + FileUploaded + "\"", StringComparison.Ordinal) && !path.EndsWith("ProductEventNames.cs", StringComparison.Ordinal));
            })
            .Select(path => Path.GetRelativePath(api, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        readers.Should().Equal("Agents/Tools/AssistantUploadTools.cs", "Controllers/ProjectFilesController.cs");
        ProductEventNames.FileUploaded.Should().Be(FileUploaded);
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(FirstUploadEventTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
