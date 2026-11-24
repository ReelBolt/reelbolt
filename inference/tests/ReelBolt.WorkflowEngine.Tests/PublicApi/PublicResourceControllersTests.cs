using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Controllers.PublicApi.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.ApiKeys.ApiKeyTestSupport;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>T-006: the public v1 resource controllers (projects, files, templates, workflows).</summary>
public class PublicResourceControllersTests
{
    private sealed class Rig
    {
        public InferenceApiDbContext Db { get; } = NewDb();
        public Guid OrgId { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public FakeUser User { get; }
        public FakeKeyContext Key { get; }
        public ProjectAccessService Access { get; }

        public Rig(OrganizationRole role = OrganizationRole.Member, Guid[]? keyProjects = null)
        {
            User = new FakeUser(OrgId, role, UserId);
            Key = new FakeKeyContext(Guid.NewGuid(), keyProjects);
            Access = new ProjectAccessService(Db, User, Key);
            // The built-in agents every template step resolves (a project create provisions the auto templates).
            foreach (AgentType type in Enum.GetValues<AgentType>())
                Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = true });
            Db.SaveChanges();
        }

        public Project AddProject(string name = "P", Guid? owner = null, Guid? org = null, DateTime? created = null)
        {
            Project p = new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                OwnerId = owner ?? UserId,
                OrganizationId = org ?? OrgId,
                Status = ProjectStatus.Draft,
                CreatedAt = created ?? DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(p);
            Db.SaveChanges();
            return p;
        }

        public ProjectFile AddFile(Project p, string name = "a.txt", CsamScanStatus csam = CsamScanStatus.Clean,
            FileModerationStatus moderation = FileModerationStatus.Clean, DateTime? at = null)
        {
            ProjectFile f = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = p.Id,
                OriginalFileName = name,
                OriginalPath = name,
                StorageKey = $"projects/{p.Id}/userFiles/{name}",
                MimeType = "text/plain",
                SizeBytes = 5,
                CsamStatus = csam,
                ModerationStatus = moderation,
                UploadedAt = at ?? DateTime.UtcNow
            };
            Db.ProjectFiles.Add(f);
            Db.SaveChanges();
            return f;
        }

        public WorkflowTemplateProvisioningService Templates() =>
            new(Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());

        public PublicProjectsController Projects() =>
            new(Db, User, Access, Key, new ProjectCreationService(Db, User, Templates()));

        public PublicFilesController Files(
            IProjectFileDeletionService? deletion = null, IDirectUploadService? uploads = null,
            IDirectUploadFinalizer? finalizer = null, IOutputLinkService? links = null, IUrlImportService? urlImport = null) =>
            new(Db, User, Access, deletion ?? Mock.Of<IProjectFileDeletionService>(), uploads, finalizer, urlImport, links)
            {
                ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
            };

        public PublicWorkflowsController Workflows() => new(Db, User, Access, Templates());
    }

    private static int Status(ActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200
    };

    private static JsonElement Body(ActionResult r) =>
        JsonSerializer.SerializeToElement(((ObjectResult)r).Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string ErrorCode(ActionResult r) => Body(r).GetProperty("error").GetProperty("code").GetString()!;

    // ---- scope table ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(PublicProjectsController), "List", "projects.read")]
    [InlineData(typeof(PublicProjectsController), "Get", "projects.read")]
    [InlineData(typeof(PublicProjectsController), "Create", "projects.write")]
    [InlineData(typeof(PublicFilesController), "List", "files.read")]
    [InlineData(typeof(PublicFilesController), "Get", "files.read")]
    [InlineData(typeof(PublicFilesController), "CreateUpload", "files.write")]
    [InlineData(typeof(PublicFilesController), "CompleteUpload", "files.write")]
    [InlineData(typeof(PublicFilesController), "Import", "files.write")]
    [InlineData(typeof(PublicFilesController), "DownloadLink", "files.read")]
    [InlineData(typeof(PublicFilesController), "Delete", "files.write")]
    [InlineData(typeof(PublicTemplatesController), "List", "workflows.read")]
    [InlineData(typeof(PublicWorkflowsController), "List", "workflows.read")]
    [InlineData(typeof(PublicWorkflowsController), "Get", "workflows.read")]
    [InlineData(typeof(PublicWorkflowsController), "Create", "workflows.write")]
    [InlineData(typeof(PublicWorkflowsController), "Delete", "workflows.write")]
    public void Each_action_requires_exactly_the_planned_scope(Type controller, string action, string scope)
    {
        MethodInfo m = controller.GetMethod(action, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        m.GetCustomAttributes<RequireScopeAttribute>().SelectMany(a => a.AnyOf).Should().Equal(scope);
    }

    [Theory]
    [InlineData(typeof(PublicProjectsController), "Create")]
    [InlineData(typeof(PublicFilesController), "CreateUpload")]
    [InlineData(typeof(PublicFilesController), "CompleteUpload")]
    [InlineData(typeof(PublicFilesController), "Import")]
    [InlineData(typeof(PublicWorkflowsController), "Create")]
    public void Every_create_post_is_idempotent(Type controller, string action)
    {
        controller.GetMethod(action)!.GetCustomAttribute<IdempotentAttribute>().Should().NotBeNull();
    }

    // ---- projects ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Projects_list_is_newest_first_and_paged_with_a_cursor()
    {
        Rig rig = new(OrganizationRole.Owner);
        DateTime t = DateTime.UtcNow;
        Project oldest = rig.AddProject("old", created: t.AddHours(-3));
        Project mid = rig.AddProject("mid", created: t.AddHours(-2));
        Project newest = rig.AddProject("new", created: t.AddHours(-1));

        JsonElement page1 = Body(await rig.Projects().List(2, null, CancellationToken.None));
        page1.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal("new", "mid");
        string cursor = page1.GetProperty("nextCursor").GetString()!;

        JsonElement page2 = Body(await rig.Projects().List(2, cursor, CancellationToken.None));
        page2.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()).Should().Equal(oldest.Id.ToString());
        page2.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        _ = (mid, newest);
    }

    [Fact]
    public async Task Projects_list_of_a_restricted_key_holds_only_its_projects_and_never_a_foreign_org()
    {
        Rig rig = new(OrganizationRole.Owner);
        Project own = rig.AddProject("own");
        rig.AddProject("other");
        rig.AddProject("foreign", org: Guid.NewGuid());
        FakeKeyContext key = new(Guid.NewGuid(), [own.Id]);
        PublicProjectsController c = new(rig.Db, rig.User, new ProjectAccessService(rig.Db, rig.User, key), key,
            new ProjectCreationService(rig.Db, rig.User, rig.Templates()));

        JsonElement data = Body(await c.List(null, null, CancellationToken.None)).GetProperty("data");
        data.EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal("own");
    }

    [Fact]
    public async Task Projects_get_of_a_project_outside_the_key_or_the_org_is_404_in_the_envelope()
    {
        Rig rig = new(OrganizationRole.Owner);
        Project foreign = rig.AddProject("f", org: Guid.NewGuid());
        ActionResult r = await rig.Projects().Get(foreign.Id, CancellationToken.None);
        Status(r).Should().Be(404);
        ErrorCode(r).Should().Be("not_found");

        Project other = rig.AddProject("o");
        Rig restricted = new(OrganizationRole.Owner, [Guid.NewGuid()]);
        PublicProjectsController c = new(rig.Db, rig.User, new ProjectAccessService(rig.Db, rig.User, restricted.Key), restricted.Key,
            new ProjectCreationService(rig.Db, rig.User, rig.Templates()));
        Status(await c.Get(other.Id, CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Projects_create_is_refused_for_a_project_restricted_key()
    {
        Rig rig = new(OrganizationRole.Owner, [Guid.NewGuid()]);
        ActionResult r = await rig.Projects().Create(new PublicCreateProjectRequest("x", null), CancellationToken.None);
        Status(r).Should().Be(403);
        ErrorCode(r).Should().Be("project_restricted_key");
        rig.Db.Projects.Should().BeEmpty();
    }

    [Fact]
    public async Task Projects_create_by_a_member_key_makes_a_project_the_key_user_owns()
    {
        Rig rig = new(OrganizationRole.Member);
        ActionResult r = await rig.Projects().Create(new PublicCreateProjectRequest(" Launch ", "d"), CancellationToken.None);
        Status(r).Should().Be(201);
        JsonElement b = Body(r);
        b.GetProperty("name").GetString().Should().Be("Launch");
        Project saved = rig.Db.Projects.Single();
        saved.OwnerId.Should().Be(rig.UserId);
        saved.OrganizationId.Should().Be(rig.OrgId);
        b.GetProperty("id").GetString().Should().Be(saved.Id.ToString());
    }

    [Fact]
    public async Task Projects_create_with_no_name_is_400_and_billing_is_403_on_list_and_create()
    {
        Rig rig = new(OrganizationRole.Member);
        ActionResult bad = await rig.Projects().Create(new PublicCreateProjectRequest("  ", null), CancellationToken.None);
        Status(bad).Should().Be(400);
        ErrorCode(bad).Should().Be("invalid_request");

        Rig billing = new(OrganizationRole.Billing);
        Status(await billing.Projects().List(null, null, CancellationToken.None)).Should().Be(403);
        Status(await billing.Projects().Create(new PublicCreateProjectRequest("x", null), CancellationToken.None)).Should().Be(403);
    }

    // ---- files ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Files_list_hides_quarantined_files_and_reports_plain_status()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        rig.AddFile(p, "ok.txt");
        rig.AddFile(p, "pending.txt", csam: CsamScanStatus.Pending);
        rig.AddFile(p, "csam.txt", csam: CsamScanStatus.Quarantined);
        rig.AddFile(p, "mod.txt", moderation: FileModerationStatus.Quarantined);

        JsonElement data = Body(await rig.Files().List(p.Id, null, null, null, null, CancellationToken.None)).GetProperty("data");
        data.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("status").GetString())
            .Should().BeEquivalentTo(new Dictionary<string, string?> { ["ok.txt"] = "ready", ["pending.txt"] = "checking" });
        data[0].EnumerateObject().Select(o => o.Name).Should().BeEquivalentTo("id", "name", "path", "mimeType", "sizeBytes", "createdAt", "status");
    }

    [Fact]
    public async Task Files_get_of_a_quarantined_file_and_of_another_project_is_404()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        ProjectFile bad = rig.AddFile(p, csam: CsamScanStatus.Quarantined);
        ProjectFile good = rig.AddFile(p, "g.txt");
        Status(await rig.Files().Get(p.Id, bad.Id, CancellationToken.None)).Should().Be(404);
        Status(await rig.Files().Get(p.Id, good.Id, CancellationToken.None)).Should().Be(200);

        Project foreign = rig.AddProject("f", org: Guid.NewGuid());
        Status(await rig.Files().Get(foreign.Id, good.Id, CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Files_of_a_project_outside_the_keys_list_are_404_everywhere()
    {
        Rig rig = new(OrganizationRole.Owner);
        Project p = rig.AddProject();
        ProjectFile f = rig.AddFile(p);
        Rig restricted = new(OrganizationRole.Owner, [Guid.NewGuid()]);
        PublicFilesController c = new(rig.Db, rig.User, new ProjectAccessService(rig.Db, rig.User, restricted.Key),
            Mock.Of<IProjectFileDeletionService>());
        Status(await c.List(p.Id, null, null, null, null, CancellationToken.None)).Should().Be(404);
        Status(await c.Get(p.Id, f.Id, CancellationToken.None)).Should().Be(404);
        Status(await c.DownloadLink(p.Id, f.Id, CancellationToken.None)).Should().Be(404);
        Status(await c.Delete(p.Id, f.Id, CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Download_link_is_minted_only_after_the_gates_and_names_a_project_file()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        ProjectFile good = rig.AddFile(p, "g.txt");
        ProjectFile bad = rig.AddFile(p, "b.txt", csam: CsamScanStatus.Quarantined);
        ProjectFile modBad = rig.AddFile(p, "m.txt", moderation: FileModerationStatus.Quarantined);

        Mock<IOutputLinkService> links = new();
        OutputLinkTarget? target = null;
        links.Setup(l => l.Create(It.IsAny<OutputLinkTarget>(), It.IsAny<Guid>(), It.IsAny<TimeSpan?>()))
            .Callback<OutputLinkTarget, Guid, TimeSpan?>((t, _, _) => target = t)
            .Returns(new OutputLink("https://x/api/v1/output-links/tok", DateTimeOffset.UtcNow.AddMinutes(15)));

        Status(await rig.Files(links: links.Object).DownloadLink(p.Id, bad.Id, CancellationToken.None)).Should().Be(404);
        Status(await rig.Files(links: links.Object).DownloadLink(p.Id, modBad.Id, CancellationToken.None)).Should().Be(404);
        links.Verify(l => l.Create(It.IsAny<OutputLinkTarget>(), It.IsAny<Guid>(), It.IsAny<TimeSpan?>()), Times.Never);

        ActionResult ok = await rig.Files(links: links.Object).DownloadLink(p.Id, good.Id, CancellationToken.None);
        Status(ok).Should().Be(200);
        Body(ok).GetProperty("url").GetString().Should().Contain("output-links");
        target.Should().Be(new OutputLinkTarget(p.Id, OutputLinkKind.ProjectFile, good.Id, null));
        links.Verify(l => l.Create(It.IsAny<OutputLinkTarget>(), rig.UserId, null), Times.Once);
    }

    [Fact]
    public async Task Files_delete_follows_the_dashboard_rules_member_deletes_billing_cannot()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        ProjectFile f = rig.AddFile(p);
        Mock<IProjectFileDeletionService> deletion = new();
        deletion.Setup(d => d.DeleteAsync(It.IsAny<Project>(), It.IsAny<ProjectFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IActionResult?)null);
        Status(await rig.Files(deletion.Object).Delete(p.Id, f.Id, CancellationToken.None)).Should().Be(204);
        deletion.Verify(d => d.DeleteAsync(It.Is<Project>(x => x.Id == p.Id), It.Is<ProjectFile>(x => x.Id == f.Id), It.IsAny<CancellationToken>()), Times.Once);

        Rig billing = new(OrganizationRole.Billing);
        Project bp = billing.AddProject();
        ProjectFile bf = billing.AddFile(bp);
        deletion.Invocations.Clear();
        Status(await billing.Files(deletion.Object).Delete(bp.Id, bf.Id, CancellationToken.None)).Should().Be(403);
        deletion.Verify(d => d.DeleteAsync(It.IsAny<Project>(), It.IsAny<ProjectFile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Files_delete_picture_copy_refusal_is_the_envelope_409()
    {
        Rig rig = new(OrganizationRole.Owner);
        Project p = rig.AddProject();
        ProjectFile f = rig.AddFile(p);
        Mock<IProjectFileDeletionService> deletion = new();
        deletion.Setup(d => d.DeleteAsync(It.IsAny<Project>(), It.IsAny<ProjectFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConflictObjectResult(new { code = "STORAGE_QUOTA_EXCEEDED", message = "no room" }));
        ActionResult r = await rig.Files(deletion.Object).Delete(p.Id, f.Id, CancellationToken.None);
        Status(r).Should().Be(409);
        ErrorCode(r).Should().Be("STORAGE_QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task Upload_create_rejects_a_missing_size_and_returns_an_opaque_id_for_a_single_put()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        Mock<IDirectUploadService> uploads = new();
        Status(await rig.Files(uploads: uploads.Object).CreateUpload(p.Id, new PublicCreateUploadRequest("a.mp4", null, null, null), CancellationToken.None))
            .Should().Be(400);

        DirectUploadTicket ticket = new("tok", DirectUploadMode.SinglePut, p.Id, Guid.NewGuid(), "k", "a.mp4", null, "video/mp4", 10,
            DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(), "https://store/put", null, 0, 0,
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }, null, null, null, null);
        uploads.Setup(u => u.CreateAsync(It.IsAny<DirectUploadRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(ticket);
        ActionResult r = await rig.Files(uploads: uploads.Object).CreateUpload(
            p.Id, new PublicCreateUploadRequest("a.mp4", "clips/raw", 10, "video/mp4"), CancellationToken.None);
        Status(r).Should().Be(201);
        Body(r).GetProperty("mode").GetString().Should().Be("single");
        Body(r).GetProperty("parts")[0].GetProperty("url").GetString().Should().Be("https://store/put");
        uploads.Verify(u => u.CreateAsync(It.Is<DirectUploadRequest>(d =>
            d.ProjectId == p.Id && d.CallerKey == rig.UserId.ToString("D") && d.RelativePath == "clips/raw/a.mp4"), It.IsAny<CancellationToken>()), Times.Once);
        PublicUploadId.TryDecode(Body(r).GetProperty("uploadId").GetString(), p.Id)!.UploadToken.Should().Be("tok");
    }

    [Fact]
    public async Task Upload_complete_refuses_a_garbage_or_foreign_upload_id_without_finishing_anything()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        Mock<IDirectUploadService> uploads = new();
        Mock<IDirectUploadFinalizer> finalizer = new();
        uploads.Setup(u => u.Verify(It.IsAny<DirectUploadTicketEcho>(), It.IsAny<string>())).Returns((DirectUploadTicket?)null);

        PublicFilesController c = rig.Files(uploads: uploads.Object, finalizer: finalizer.Object);
        ActionResult garbage = await c.CompleteUpload(p.Id, "not-an-id", new PublicCompleteUploadRequest([]), CancellationToken.None);
        Status(garbage).Should().Be(400);
        ErrorCode(garbage).Should().Be("invalid_upload");

        Guid fileId = Guid.NewGuid();
        string? key = DirectUploadService.DeriveKey(p.Id, fileId, "a.txt", null);
        DirectUploadTicket t = new("tok", DirectUploadMode.SinglePut, p.Id, fileId, key!, "a.txt", null, "text/plain", 1, 1, "u", null, 0, 0,
            new Dictionary<string, string>(), null, null, null, null);
        ActionResult unverified = await c.CompleteUpload(p.Id, PublicUploadId.Encode(t), new PublicCompleteUploadRequest([]), CancellationToken.None);
        Status(unverified).Should().Be(400);
        finalizer.Verify(f => f.FinalizeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DirectUploadTicket>(), It.IsAny<DirectUploadCompletion>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Upload_complete_maps_the_finalizer_outcomes()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        ProjectFile file = rig.AddFile(p, "a.txt");
        Guid fileId = Guid.NewGuid();
        string key = DirectUploadService.DeriveKey(p.Id, fileId, "a.txt", null)!;
        DirectUploadTicket t = new("tok", DirectUploadMode.SinglePut, p.Id, fileId, key, "a.txt", null, "text/plain", 1, 1, "u", null, 0, 0,
            new Dictionary<string, string>(), null, null, null, null);
        Mock<IDirectUploadService> uploads = new();
        uploads.Setup(u => u.Verify(It.IsAny<DirectUploadTicketEcho>(), It.IsAny<string>())).Returns(t);
        Mock<IDirectUploadFinalizer> finalizer = new();
        DirectUploadFinalizeResult result = new(DirectUploadFinalizeOutcome.Created, file);
        finalizer.Setup(f => f.FinalizeAsync(p.Id, rig.UserId, t, It.IsAny<DirectUploadCompletion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => result);
        string id = PublicUploadId.Encode(t);
        PublicFilesController c = rig.Files(uploads: uploads.Object, finalizer: finalizer.Object);

        Status(await c.CompleteUpload(p.Id, id, new PublicCompleteUploadRequest([new PublicCompletedPart(1, "e")]), CancellationToken.None)).Should().Be(201);
        result = new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Replayed, file);
        Status(await c.CompleteUpload(p.Id, id, null, CancellationToken.None)).Should().Be(200);
        result = new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Quarantined);
        ActionResult q = await c.CompleteUpload(p.Id, id, null, CancellationToken.None);
        Status(q).Should().Be(422);
        ErrorCode(q).Should().Be("FILE_UNAVAILABLE");
        result = new DirectUploadFinalizeResult(DirectUploadFinalizeOutcome.Refused, Refused: new DirectUploadRefusedException("UPLOAD_INCOMPLETE", "parts missing", 409));
        ActionResult refused = await c.CompleteUpload(p.Id, id, null, CancellationToken.None);
        Status(refused).Should().Be(409);
        ErrorCode(refused).Should().Be("UPLOAD_INCOMPLETE");
    }

    [Fact]
    public async Task Import_without_the_service_is_501_and_a_billing_key_never_reaches_it()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        Status(await rig.Files().Import(p.Id, new PublicImportFileRequest("https://x/a.png", null, null), CancellationToken.None)).Should().BeOneOf(403, 501);

        Rig billing = new(OrganizationRole.Billing);
        Project bp = billing.AddProject();
        Status(await billing.Files().Import(bp.Id, new PublicImportFileRequest("https://x/a.png", null, null), CancellationToken.None)).Should().Be(403);
    }

    [Fact]
    public async Task Import_busy_429_carries_retry_after_ratelimit_reset_and_resets_at()
    {
        Rig rig = new(OrganizationRole.Owner);
        Project p = rig.AddProject();
        Mock<IUrlImportService> busy = new();
        busy.Setup(i => i.ImportAsync(It.IsAny<Project>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UrlImportResult.Refused(UrlImportRefusal.Busy("Too many imports are running right now; try again in a minute.")));
        PublicFilesController files = rig.Files(urlImport: busy.Object);

        ObjectResult refused = (ObjectResult)await files.Import(p.Id, new PublicImportFileRequest("https://x/a.png", null, null), CancellationToken.None);

        PublicApi429Tests.AssertConsistent(files.Response, refused);
        ErrorCode(refused).Should().Be("IMPORT_BUSY");
    }

    // ---- templates and workflows --------------------------------------------------------------------------

    [Fact]
    public async Task Templates_list_carries_key_name_cost_class_and_input_flag()
    {
        Rig rig = new();
        JsonElement data = Body(await new PublicTemplatesController(rig.Templates()).List(CancellationToken.None)).GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThan(0);
        data[0].EnumerateObject().Select(o => o.Name).Should().BeEquivalentTo("key", "name", "description", "costClass", "requiresUserInput");
    }

    [Fact]
    public async Task Templates_list_leaves_out_the_hidden_superseded_avatar_keys()
    {
        Rig rig = new();
        JsonElement data = Body(await new PublicTemplatesController(rig.Templates()).List(CancellationToken.None)).GetProperty("data");
        string[] keys = [.. data.EnumerateArray().Select(t => t.GetProperty("key").GetString()!)];
        keys.Should().NotContain([WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey]);
    }

    [Fact]
    public async Task Workflow_view_exposes_each_typed_inputs_id_and_type()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        // narrated-story ships typed inputs in the served edition.
        JsonElement created = Body(await rig.Workflows().Create(p.Id, new PublicCreateWorkflowRequest(WorkflowTemplateCatalog.NarratedStoryTemplateKey, null), CancellationToken.None));
        JsonElement view = Body(await rig.Workflows().Get(p.Id, Guid.Parse(created.GetProperty("id").GetString()!), CancellationToken.None));

        JsonElement typed = view.GetProperty("appInputs").EnumerateArray().First(i => i.GetProperty("kind").GetString() == "input");
        typed.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        typed.GetProperty("type").GetString().Should().NotBeNullOrWhiteSpace();

        JsonElement schema = view.GetProperty("inputSchema");
        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").TryGetProperty(typed.GetProperty("id").GetString()!, out _).Should().BeTrue();
    }

    private static WorkflowDefinition NewWorkflow(Project p, WorkflowDefinitionKind kind, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = p.Id,
        Name = name,
        Kind = kind,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Workflows_hidden_kinds_are_404_and_never_listed()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        WorkflowDefinition std = NewWorkflow(p, WorkflowDefinitionKind.Standard, "std");
        WorkflowDefinition rev = NewWorkflow(p, WorkflowDefinitionKind.CompositionRevision, "rev");
        WorkflowDefinition snap = NewWorkflow(p, WorkflowDefinitionKind.ExecutionSnapshot, "snap");
        rig.Db.WorkflowDefinitions.AddRange(std, rev, snap);
        await rig.Db.SaveChangesAsync();

        JsonElement data = Body(await rig.Workflows().List(p.Id, null, null, CancellationToken.None)).GetProperty("data");
        data.EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal("std");
        Status(await rig.Workflows().Get(p.Id, std.Id, CancellationToken.None)).Should().Be(200);
        Status(await rig.Workflows().Get(p.Id, rev.Id, CancellationToken.None)).Should().Be(404);
        Status(await rig.Workflows().Get(p.Id, snap.Id, CancellationToken.None)).Should().Be(404);
        Status(await rig.Workflows().Delete(p.Id, rev.Id, CancellationToken.None)).Should().Be(404);
    }

    [Fact]
    public async Task Workflows_of_a_foreign_org_or_billing_role_are_refused()
    {
        Rig rig = new(OrganizationRole.Member);
        Project foreign = rig.AddProject("f", org: Guid.NewGuid());
        Status(await rig.Workflows().List(foreign.Id, null, null, CancellationToken.None)).Should().Be(404);

        Rig billing = new(OrganizationRole.Billing);
        Project bp = billing.AddProject();
        Status(await billing.Workflows().List(bp.Id, null, null, CancellationToken.None)).Should().Be(403);
        Status(await billing.Workflows().Create(bp.Id, new PublicCreateWorkflowRequest("quick-win-promo", null), CancellationToken.None)).Should().Be(403);
    }

    [Fact]
    public async Task Workflows_create_from_a_template_returns_it_and_keeps_egress_consent_off()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        ActionResult r = await rig.Workflows().Create(p.Id, new PublicCreateWorkflowRequest(WorkflowTemplateCatalog.StarterTemplateKey, "My promo"), CancellationToken.None);
        Status(r).Should().Be(201);
        JsonElement b = Body(r);
        b.GetProperty("name").GetString().Should().Be("My promo");
        b.GetProperty("templateKey").GetString().Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
        b.GetProperty("steps").GetArrayLength().Should().BeGreaterThan(0);
        b.GetProperty("steps")[0].EnumerateObject().Select(o => o.Name).Should().BeEquivalentTo("order", "type", "name");

        // The API never sets the egress consent: the stored step configs carry no enabled egress flag.
        List<WorkflowStep> steps = rig.Db.WorkflowSteps.Where(s => s.WorkflowDefinitionId == Guid.Parse(b.GetProperty("id").GetString()!)).ToList();
        steps.Should().NotBeEmpty();
        steps.SelectMany(s => new[] { s.VideoGenerateConfigJson, s.GraphicsRoomConfigJson, s.ThumbnailGenerateConfigJson })
            .Where(j => j is not null)
            .Should().OnlyContain(j => !j!.Replace(" ", "").Contains("\"allowSourceMediaEgress\":true", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Workflows_create_validates_the_template_key()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        Status(await rig.Workflows().Create(p.Id, new PublicCreateWorkflowRequest(null, null), CancellationToken.None)).Should().Be(400);
        ActionResult unknown = await rig.Workflows().Create(p.Id, new PublicCreateWorkflowRequest("no-such", null), CancellationToken.None);
        Status(unknown).Should().Be(404);
        ErrorCode(unknown).Should().Be("template_not_found");
    }

    [Fact]
    public async Task Workflows_delete_removes_an_idle_workflow_and_refuses_one_with_an_active_run()
    {
        Rig rig = new(OrganizationRole.Member);
        Project p = rig.AddProject();
        WorkflowDefinition idle = NewWorkflow(p, WorkflowDefinitionKind.Standard, "idle");
        WorkflowDefinition busy = NewWorkflow(p, WorkflowDefinitionKind.Standard, "busy");
        rig.Db.WorkflowDefinitions.AddRange(idle, busy);
        rig.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = p.Id,
            WorkflowDefinitionId = busy.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        });
        await rig.Db.SaveChangesAsync();

        Status(await rig.Workflows().Delete(p.Id, idle.Id, CancellationToken.None)).Should().Be(204);
        ActionResult r = await rig.Workflows().Delete(p.Id, busy.Id, CancellationToken.None);
        Status(r).Should().Be(409);
        ErrorCode(r).Should().Be("WORKFLOW_RUN_ACTIVE");
    }
}
