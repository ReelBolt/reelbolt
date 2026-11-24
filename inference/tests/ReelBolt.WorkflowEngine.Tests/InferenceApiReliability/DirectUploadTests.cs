using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InferenceApiReliability;

/// <summary>
/// The browser's direct upload (docs/direct-upload.md): ask for a presigned PUT, PUT the bytes to
/// storage, tell the API it is done. The bytes never cross this service, so every property that used
/// to be enforced BY transporting them — the caller's role, the plan's quota, the size that actually
/// arrived, the media rule R22 — has to be enforced explicitly here instead. These are those
/// assertions, one per check, driven through the real controller action rather than a helper.
///
/// <para>
/// Nothing here is a real object store or a real HMAC deployment: the ticket is minted by a real
/// <see cref="ConfirmationTokenService"/> with a process-local key, and the HEAD is answered by an
/// in-memory stand-in, which is exactly what makes it possible to assert on what the endpoint DELETED
/// and what it REFUSED to record.
/// </para>
/// </summary>
public class DirectUploadTests
{
    private static readonly Guid OtherOrg = Guid.Parse("dddddddd-0000-0000-0000-0000000000ff");

    private sealed class Harness
    {
        public Guid OwnerId { get; } = Guid.NewGuid();

        /// <summary>A Member of the SAME organization as the owner: holds Edit, so a refusal for them
        /// is a refusal about the ticket and not about the role.</summary>
        public Guid ColleagueId { get; } = Guid.NewGuid();

        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public FakeFileStorage Storage { get; } = new();
        public FakePresignedUrlService Presign { get; } = new();
        public ConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public ChannelBackgroundTaskQueue<FileSummarizationTask> Queue { get; } = new(100);
        public StubPlanGate PlanGate { get; } = new();
        public TestClock Clock { get; } = new();

        public Harness()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"direct-upload-{Guid.NewGuid():N}")
                .Options);
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "P",
                OwnerId = OwnerId,
                OrganizationId = TestTenancy.OrgOf(OwnerId)
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();
        }

        /// <summary>A controller with presigning wired, as every shipped composition has it.</summary>
        public ProjectFilesController Controller(Guid asUser) => Build(new User(asUser, Project.OrganizationId, OrganizationRole.Member), Presign, Tokens);

        /// <summary>The same controller for a caller of another role, so a refusal can be shown to be
        /// about the ROLE and not about a hard-wired rejection.</summary>
        public ProjectFilesController ControllerFor(User user) => Build(user, Presign, Tokens);

        /// <summary>A deployment with nothing to presign with (the 501 case).</summary>
        public ProjectFilesController ControllerWithoutPresigning(Guid asUser) =>
            Build(new User(asUser, Project.OrganizationId, OrganizationRole.Member), null, null);

        private ProjectFilesController Build(ICurrentUser user, IPresignedUrlService presignedUrls, IConfirmationTokenService confirmationTokens)
        {
            var controller = new ProjectFilesController(
                Db, user, TestTenancy.Access(Db, user), Storage, Queue, Publish.Object,
                new VectorSearchQueryService(Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(), Mock.Of<IVectorIndexService>()),
                PlanGate, presignedUrls, confirmationTokens, null, null, Clock);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        /// <summary>The bytes "the browser PUT" to storage, at the key the ticket named.</summary>
        public void PutToStorage(string key, int byteCount) => Storage.Objects[key] = new byte[byteCount];

        public Task<ActionResult<DirectUploadTicket>> Ticket(Guid projectId, string fileName = "clip.mp4", long sizeBytes = 4, string contentType = "video/mp4", string relativePath = null, Guid asUser = default, ProjectFilesController controller = null) =>
            (controller ?? Controller(asUser == default ? OwnerId : asUser)).CreateUploadUrl(
                projectId,
                new CreateDirectUploadRequest(fileName, sizeBytes, contentType, relativePath),
                CancellationToken.None);

        public Task<ActionResult<ProjectFileResponse>> Complete(DirectUploadTicket ticket, long sizeBytes = 4, string fileName = "clip.mp4", string relativePath = null, Guid asUser = default) =>
            Controller(asUser == default ? OwnerId : asUser).CompleteUpload(
                Project.Id,
                new CompleteDirectUploadRequest(
                    ticket.UploadToken, ticket.Key, ticket.FileId, fileName, sizeBytes, ticket.ExpiresAtUnix, ticket.ContentType, relativePath),
                CancellationToken.None);
    }

    private sealed class User(Guid id, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    /// <summary>A plan gate that refuses the upload when the test queues a violation, and records every
    /// size it was asked about — which is how "the claim at presign, the real size at finalisation" is
    /// asserted rather than assumed.</summary>
    private sealed class StubPlanGate : IPlanGateService
    {
        public Queue<PlanGateViolation> Violations { get; } = new();
        public List<long> UploadSizes { get; } = new();

        public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            UploadSizes.Add(incomingBytes);
            return Task.FromResult(Violations.Count > 0 ? Violations.Dequeue() : null);
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    private static PlanGateViolation OverQuota() => new(
        PlanGate.StorageCode, "storage", "This upload would exceed the plan's storage quota.", 413, 100, 99);

    private static async Task<DirectUploadTicket> TicketOf(Task<ActionResult<DirectUploadTicket>> call)
    {
        ActionResult<DirectUploadTicket> result = await call;
        result.Result.Should().BeOfType<OkObjectResult>("a ticket is what a permitted upload gets");
        return (DirectUploadTicket)((OkObjectResult)result.Result!).Value!;
    }

    /// <summary>The \u0060IActionResult\u0060 the action produced, whichever shape it came in.</summary>
    private static async Task<object> Outcome(Task<ActionResult<DirectUploadTicket>> call) => (await call).Result;

    private static async Task<object> Outcome(Task<ActionResult<ProjectFileResponse>> call) => (await call).Result;

    /// <summary>
    /// The status a caller observes. A refusal from the project access service arrives as
    /// NotFoundResult/ForbidResult and a refusal this endpoint composes as an ObjectResult, so both
    /// are read here rather than by asserting one CLR type.
    /// </summary>
    private static int StatusOf(object result) => result switch
    {
        null => 0,
        NotFoundResult => StatusCodes.Status404NotFound,
        ForbidResult => StatusCodes.Status403Forbidden,
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        _ => 0
    };

    /// <summary>The machine-readable code of a refusal body.</summary>
    private static string CodeOf(object result)
    {
        result.Should().BeAssignableTo<ObjectResult>("a refusal by this endpoint carries a body");
        object body = ((ObjectResult)result).Value!;
        return body.GetType().GetProperty("code")!.GetValue(body)!.ToString()!;
    }

    // ------------------------------------------------------------------ the ticket

    [Fact]
    public async Task The_server_chooses_the_key_and_the_client_is_only_told_about_it()
    {
        var h = new Harness();

        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, fileName: "clip.mov", sizeBytes: 4096, contentType: "application/octet-stream"));

        ticket.Key.Should().StartWith($"projects/{h.Project.Id}/userFiles/",
            "the upload can only ever write inside its own project's prefix");
        ticket.Key.Should().EndWith($"{ticket.FileId}.mov",
            "the key embeds the server-generated file id, so two uploads can never collide on one object");

        h.Presign.BrowserPuts.Should().ContainSingle().Which.Should().Be((h.Project.Id, ticket.Key, "video/quicktime",
            ProjectFilesController.UploadUrlTtl));
        h.Presign.BrowserPuts[0].ContentType.Should().Be("video/quicktime",
            "R22: a generic application/octet-stream is resolved from the extension, so a .mov never reaches the text pipeline");
        ticket.ContentType.Should().Be("video/quicktime",
            "and the PUT's signed Content-Type is that resolved value, not the client's claim");
        ticket.MaxBytes.Should().Be(ProjectFilesController.MaxDirectUploadBytes);
    }

    [Fact]
    public async Task The_ticket_outlives_its_url_but_not_by_long()
    {
        var h = new Harness();

        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id));

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix);
        expiresAt.Should().Be(h.Clock.Now.Add(ProjectFilesController.UploadTicketTtl));
        expiresAt.Should().BeAfter(h.Clock.Now.Add(ProjectFilesController.UploadUrlTtl),
            "finalisation must not race the URL's expiry: the ticket is valid for a grace period after the PUT window closes");
    }

    [Fact]
    public async Task A_project_in_another_organization_is_404_and_a_wrong_role_is_403_like_every_other_surface()
    {
        var h = new Harness();
        Project foreign = new() { Id = Guid.NewGuid(), Name = "Theirs", OwnerId = Guid.NewGuid(), OrganizationId = OtherOrg };
        h.Db.Projects.Add(foreign);
        h.Db.SaveChanges();

        StatusOf(await Outcome(h.Ticket(foreign.Id))).Should().Be(404,
            "D5: another organization's project must not even be confirmed to exist");

        var billing = new User(Guid.NewGuid(), h.Project.OrganizationId, OrganizationRole.Billing);
        ActionResult<DirectUploadTicket> refused = await h.ControllerFor(billing).CreateUploadUrl(
            h.Project.Id, new CreateDirectUploadRequest("clip.mp4", 4), CancellationToken.None);
        refused.Result.Should().BeOfType<ForbidResult>("Billing has no project access at all");

        // The positive control: the same controller and the same project ARE served for a Member, so
        // the 403 above is about the role rather than about a route that always refuses.
        StatusOf(await Outcome(h.Ticket(h.Project.Id))).Should().Be(200);
        h.Presign.BrowserPuts.Should().ContainSingle("only the permitted caller ever got a signed URL");
    }

    [Fact]
    public async Task The_quota_refuses_the_claim_before_any_url_exists_with_the_plan_gates_own_body()
    {
        var h = new Harness();
        h.PlanGate.Violations.Enqueue(OverQuota());

        object refusal = await Outcome(h.Ticket(h.Project.Id, sizeBytes: 500));

        StatusOf(refusal).Should().Be(413);
        CodeOf(refusal).Should().Be(PlanGate.StorageCode, "the machine-readable STORAGE_QUOTA_EXCEEDED body, unchanged");
        h.PlanGate.UploadSizes.Should().BeEquivalentTo([500L], "the check ran against the size the client claimed");
        h.Presign.BrowserPuts.Should().BeEmpty("a refusal must not leave a signed write capability in the wild");
    }

    [Fact]
    public async Task A_claimed_size_over_the_ceiling_is_refused_before_the_quota_is_consulted()
    {
        var h = new Harness();

        object refusal = await Outcome(h.Ticket(h.Project.Id, sizeBytes: ProjectFilesController.MaxDirectUploadBytes + 1));

        StatusOf(refusal).Should().Be(413);
        CodeOf(refusal).Should().Be("FILE_TOO_LARGE");
        h.PlanGate.UploadSizes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deployment_with_no_presigning_answers_501_so_the_client_keeps_its_multipart_route()
    {
        var h = new Harness();

        object ticketRefusal = await Outcome(h.ControllerWithoutPresigning(h.OwnerId)
            .CreateUploadUrl(h.Project.Id, new CreateDirectUploadRequest("clip.mp4", 4), CancellationToken.None));

        object completeRefusal = await Outcome(h.ControllerWithoutPresigning(h.OwnerId)
            .CompleteUpload(h.Project.Id, new CompleteDirectUploadRequest("t", "k", Guid.NewGuid(), "clip.mp4", 4, 0), CancellationToken.None));

        StatusOf(ticketRefusal).Should().Be(501);
        StatusOf(completeRefusal).Should().Be(501);
        CodeOf(ticketRefusal).Should().Be(ProjectFilesController.DirectUploadDisabledCode);
    }

    // ------------------------------------------------------------------ finalisation

    [Fact]
    public async Task Finalisation_records_the_file_from_the_bytes_that_actually_arrived()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, fileName: "notes.txt", sizeBytes: 12, contentType: "text/plain"));
        h.PutToStorage(ticket.Key, 12);

        ActionResult<ProjectFileResponse> result = await h.Complete(ticket, sizeBytes: 12, fileName: "notes.txt");

        StatusOf(result.Result).Should().Be(201);
        ProjectFileResponse recorded = (ProjectFileResponse)((ObjectResult)result.Result!).Value!;
        recorded.Id.Should().Be(ticket.FileId);
        recorded.StorageKey.Should().NotBe(ticket.Key, "the presigned key is only a staging key");
        recorded.StorageKey.Should().StartWith($"projects/{h.Project.Id}/userFiles/");
        recorded.SizeBytes.Should().Be(12);
        recorded.MimeType.Should().Be("text/plain");
        recorded.Category.Should().Be("userFiles");

        ProjectFile row = h.Db.ProjectFiles.Single();
        row.StorageBucket.Should().Be(h.Storage.BucketName, "the recorded bucket is the one the store actually uses");
        row.StoragePrefix.Should().Be($"projects/{h.Project.Id}/userFiles/");
        row.SummaryStatus.Should().Be(SummaryStatus.Pending, "a text upload is summarized, exactly as the multipart route does");
        h.Queue.IsTracked(new FileSummarizationTask(row.Id)).Should().BeTrue();
        h.Publish.Verify(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_video_upload_is_never_handed_to_the_summarizer_or_the_chunker()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, fileName: "clip.mp4", sizeBytes: 9, contentType: "video/mp4"));
        h.PutToStorage(ticket.Key, 9);

        await h.Complete(ticket, sizeBytes: 9, fileName: "clip.mp4");

        ProjectFile row = h.Db.ProjectFiles.Single();
        row.SummaryStatus.Should().Be(SummaryStatus.Done);
        row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        h.Queue.IsTracked(new FileSummarizationTask(row.Id)).Should().BeFalse("R22: a raw source video never enters the text pipeline");
        h.Publish.Verify(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_retried_completion_returns_the_first_rows_answer_instead_of_a_second_row()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, fileName: "notes.txt", sizeBytes: 3, contentType: "text/plain"));
        h.PutToStorage(ticket.Key, 3);

        await h.Complete(ticket, sizeBytes: 3, fileName: "notes.txt");
        ActionResult<ProjectFileResponse> retry = await h.Complete(ticket, sizeBytes: 3, fileName: "notes.txt");

        StatusOf(retry.Result).Should().Be(200, "a double submit is the same upload, not a second file");
        h.Db.ProjectFiles.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_completion_with_no_object_behind_it_is_a_conflict_and_records_nothing()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id));

        object refusal = await Outcome(h.Complete(ticket));

        StatusOf(refusal).Should().Be(409);
        CodeOf(refusal).Should().Be("UPLOAD_OBJECT_MISSING");
        h.Db.ProjectFiles.Should().BeEmpty("a row pointing at an object that does not exist is the one outcome finalisation must never produce");
    }

    [Fact]
    public async Task A_key_the_ticket_did_not_issue_is_refused_without_touching_storage()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id));
        h.PutToStorage(ticket.Key, 4);
        DirectUploadTicket forged = ticket with { Key = $"projects/{h.Project.Id}/userFiles/{Guid.NewGuid()}.mp4" };

        object refusal = await Outcome(h.Complete(forged));

        StatusOf(refusal).Should().Be(400);
        CodeOf(refusal).Should().Be("UPLOAD_KEY_MISMATCH");
        h.Storage.DeletedKeys.Should().BeEmpty("the server never touches a key its own ticket did not name");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_callers_ticket_is_refused()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id));
        h.PutToStorage(ticket.Key, 4);

        object refusal = await Outcome(h.Complete(ticket, asUser: h.ColleagueId));

        StatusOf(refusal).Should().Be(400);
        CodeOf(refusal).Should().Be("UPLOAD_TICKET_INVALID",
            "the ticket is bound to the caller it was minted for, even inside one organization");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_ticket_is_refused_and_the_object_it_left_behind_is_discarded()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id));
        h.PutToStorage(ticket.Key, 4);
        h.Clock.Advance(ProjectFilesController.UploadTicketTtl + TimeSpan.FromMinutes(1));

        object refusal = await Outcome(h.Complete(ticket));

        StatusOf(refusal).Should().Be(410);
        CodeOf(refusal).Should().Be("UPLOAD_TICKET_EXPIRED");
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(ticket.Key);
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upload_whose_bytes_are_not_the_size_the_ticket_was_issued_for_is_deleted_rather_than_recorded()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, sizeBytes: 100, contentType: "video/mp4"));
        h.PutToStorage(ticket.Key, 37);

        object refusal = await Outcome(h.Complete(ticket, sizeBytes: 100));

        StatusOf(refusal).Should().Be(409);
        CodeOf(refusal).Should().Be("UPLOAD_SIZE_MISMATCH");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key,
            "a truncated upload is refused AND its bytes are removed, never recorded");
        h.Storage.Objects.Should().BeEmpty("the relocated copy is removed too");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upload_that_overshot_the_quota_is_deleted_rather_than_recorded()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, sizeBytes: 4096, contentType: "video/mp4"));
        h.PutToStorage(ticket.Key, 4096);
        h.PlanGate.Violations.Enqueue(OverQuota());

        object refusal = await Outcome(h.Complete(ticket, sizeBytes: 4096));

        StatusOf(refusal).Should().Be(413);
        CodeOf(refusal).Should().Be(PlanGate.StorageCode);
        h.PlanGate.UploadSizes.Should().BeEquivalentTo([4096L, 4096L],
            "the claim is checked before the URL is issued and the REAL size again after the bytes land");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key,
            "a presigned PUT cannot carry a maximum length, so the check that matters is the one after it");
        h.Storage.Objects.Should().BeEmpty("the relocated copy is removed too");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_upload_keeps_its_path_in_the_key_and_its_name_in_the_row()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(
            h.Project.Id, fileName: "clip.mp4", sizeBytes: 4, contentType: "video/mp4", relativePath: "broll/day1/clip.mp4"));
        h.PutToStorage(ticket.Key, 4);

        await h.Complete(ticket, sizeBytes: 4, fileName: "clip.mp4", relativePath: "broll/day1/clip.mp4");

        ticket.Key.Should().Be($"projects/{h.Project.Id}/userFiles/broll/day1/{ticket.FileId}.mp4");
        ProjectFile row = h.Db.ProjectFiles.Single();
        row.OriginalPath.Should().Be("broll/day1/clip.mp4");
        row.DirectoryPath.Should().Be("broll/day1");
        row.OriginalFileName.Should().Be("clip.mp4");
    }

    [Fact]
    public async Task A_path_traversal_in_the_ticket_is_refused()
    {
        var h = new Harness();
        DirectUploadTicket ticket = await TicketOf(h.Ticket(h.Project.Id, fileName: "clip.mp4"));

        object refusal = await Outcome(h.Controller(h.OwnerId).CompleteUpload(
            h.Project.Id,
            new CompleteDirectUploadRequest(ticket.UploadToken, ticket.Key, ticket.FileId, "clip.mp4", 4, ticket.ExpiresAtUnix, ticket.ContentType, "../../etc/passwd"),
            CancellationToken.None));

        StatusOf(refusal).Should().Be(400);
        CodeOf(refusal).Should().Be("UPLOAD_TICKET_INVALID");
    }

    [Fact]
    public async Task A_client_chosen_key_never_becomes_the_recorded_key()
    {
        var h = new Harness();
        string attackerKey = $"projects/{h.Project.Id}/userFiles/{Guid.NewGuid()}.mp4";
        h.Storage.Objects[attackerKey] = Encoding.UTF8.GetBytes("payload");

        // No ticket for that key exists at all: the shape the endpoint refuses is precisely a caller
        // supplying a key of its own choosing.
        object refusal = await Outcome(h.Controller(h.OwnerId).CompleteUpload(
            h.Project.Id,
            new CompleteDirectUploadRequest("00", attackerKey, Guid.NewGuid(), "evil.mp4", 7, long.MaxValue, "video/mp4", null),
            CancellationToken.None));

        StatusOf(refusal).Should().Be(400);
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.DeletedKeys.Should().BeEmpty();
    }
}