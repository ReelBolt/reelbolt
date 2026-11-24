#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The webhook endpoint, driven over the real MVC action with a real body and real headers.
///
/// <para>
/// The central assertion is the ordering: an unverified delivery is refused <b>and writes nothing</b>.
/// If the write happened first, a forged request could at least create work, and the endpoint would be
/// an unauthenticated write primitive — which is exactly what the signature check exists to prevent.
/// </para>
/// </summary>
public class GitHubWebhookControllerTests
{
    private sealed class RecordingPullService : IGitHubRepositoryPullService
    {
        public int Calls { get; private set; }

        public Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new GitHubPullOutcome("pulled", commitSha, 1, 0, 0, 0));
        }

        public Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new GitHubPullOutcome("pulled", link.LastPulledCommit, 1, 0, 0, 0));
        }
    }

    private static string Sign(string body)
    {
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(GitHubTestData.WebhookSecret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static GitHubWebhookController NewController(
        InferenceApiDbContext db,
        TestTenant tenant,
        IGitHubRepositoryPullService pull,
        string? secret = GitHubTestData.WebhookSecret)
    {
        GitHubWebhookVerifier verifier = new(Options.Create(new GitHubOptions
        {
            AppId = "123456",
            PrivateKeyPem = "unused-in-tests",
            WebhookSecret = secret
        }));

        GitHubDeliveryProcessor processor = new(db, pull, new NoopGitHubAccountService(), NullLogger<GitHubDeliveryProcessor>.Instance);
        GitHubWebhookController controller = new(db, verifier, processor, NullLogger<GitHubWebhookController>.Instance, tenant)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return controller;
    }

    private static async Task<IActionResult> Post(
        GitHubWebhookController controller,
        string body,
        string? signature,
        string? eventName = "push",
        string? deliveryId = "delivery-1")
    {
        DefaultHttpContext context = (DefaultHttpContext)controller.HttpContext;
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        if (eventName is not null) context.Request.Headers["X-GitHub-Event"] = eventName;
        if (deliveryId is not null) context.Request.Headers["X-GitHub-Delivery"] = deliveryId;
        if (signature is not null) context.Request.Headers["X-Hub-Signature-256"] = signature;

        return await controller.Webhook(CancellationToken.None);
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        StatusCodeResult statusCode => statusCode.StatusCode,
        _ => 200
    };

    private static string OutcomeOf(IActionResult result) => FieldOf(result, "outcome");

    private static string FieldOf(IActionResult result, string name) =>
        result is ObjectResult { Value: { } value }
            ? value.GetType().GetProperty(name)?.GetValue(value)?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>A project in its own organization, with or without a link to the test repository.</summary>
    private static async Task<(InferenceApiDbContext Db, TestTenant Tenant, Guid ProjectId, bool Linked)> SeededAsync(bool linked)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);

        if (linked)
            db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(project.Id, organizationId, ownerId));

        await db.SaveChangesAsync();
        return (db, tenant, project.Id, linked);
    }

    // ------------------------------------------------------------------ the security requirement

    [Fact]
    public async Task An_unsigned_delivery_is_refused_and_writes_nothing()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: true);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        IActionResult result = await Post(controller, GitHubTestData.PushPayload("abc123"), signature: null);

        StatusOf(result).Should().Be(401);
        FieldOf(result, "error").Should().Be("invalid_signature");

        db.GitHubWebhookDeliveries.Count().Should().Be(0,
            "the signature is checked before the delivery row is written; a refused request must leave no trace");
        pull.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_wrongly_signed_delivery_is_refused_and_writes_nothing()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: true);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        string body = GitHubTestData.PushPayload("abc123");
        IActionResult result = await Post(controller, body, signature: Sign(body + " "));

        StatusOf(result).Should().Be(401);
        db.GitHubWebhookDeliveries.Count().Should().Be(0);
        pull.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_unconfigured_deployment_answers_503_rather_than_accepting()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: true);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull, secret: null);

        string body = GitHubTestData.PushPayload("abc123");
        IActionResult result = await Post(controller, body, signature: Sign(body));

        StatusOf(result).Should().Be(503);
        db.GitHubWebhookDeliveries.Count().Should().Be(0);
    }

    // ------------------------------------------------------------------ routing verified deliveries

    [Fact]
    public async Task A_signed_ping_is_acknowledged_and_needs_no_work()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: false);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        const string body = """{"zen":"Keep it logically awesome."}""";
        IActionResult result = await Post(controller, body, Sign(body), eventName: "ping");

        StatusOf(result).Should().Be(200);
        OutcomeOf(result).Should().Be("ignored_event");

        GitHubWebhookDelivery delivery = await db.GitHubWebhookDeliveries.SingleAsync();
        delivery.ProcessedAt.Should().NotBeNull();
        delivery.PayloadJson.Should().Be(body, "the verified payload is kept verbatim, which is what a replay can be audited against");
        pull.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_redelivered_webhook_is_a_no_op()
    {
        (InferenceApiDbContext db, TestTenant tenant, Guid projectId, _) = await SeededAsync(linked: true);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        string body = GitHubTestData.PushPayload("abc123");
        IActionResult first = await Post(controller, body, Sign(body), deliveryId: "delivery-dup");
        IActionResult second = await Post(controller, body, Sign(body), deliveryId: "delivery-dup");

        StatusOf(first).Should().Be(202, "the pull is owed and queued");
        StatusOf(second).Should().Be(200);
        OutcomeOf(second).Should().Be("duplicate");

        db.GitHubWebhookDeliveries.Count().Should().Be(1,
            "GitHub redelivers on any doubt; the delivery id is the idempotency key and a replay must not queue a second pull");
        projectId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_push_for_an_unlinked_repository_is_recorded_and_settles()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: false);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        string body = GitHubTestData.PushPayload("abc123");
        IActionResult result = await Post(controller, body, Sign(body));

        StatusOf(result).Should().Be(200);
        OutcomeOf(result).Should().Be("no_link");
        pull.Calls.Should().Be(0);

        GitHubWebhookDelivery delivery = await db.GitHubWebhookDeliveries.SingleAsync();
        delivery.ProcessedAt.Should().NotBeNull();
        delivery.RepositoryFullName.Should().Be(GitHubTestData.RepositoryFullName);
        delivery.InstallationId.Should().Be(GitHubTestData.InstallationId);
    }

    [Fact]
    public async Task A_push_for_a_linked_repository_is_queued_for_the_worker_and_not_pulled_here()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: true);
        RecordingPullService pull = new();
        GitHubWebhookController controller = NewController(db, tenant, pull);

        string body = GitHubTestData.PushPayload("abc123");
        IActionResult result = await Post(controller, body, Sign(body));

        StatusOf(result).Should().Be(202, "GitHub abandons a slow delivery, so the pull is acknowledged and queued rather than run inline");
        OutcomeOf(result).Should().Be("queued");
        pull.Calls.Should().Be(0);

        GitHubWebhookDelivery delivery = await db.GitHubWebhookDeliveries.SingleAsync();
        delivery.ProcessedAt.Should().BeNull("a null ProcessedAt is the worker's work queue");
    }

    [Fact]
    public async Task A_verified_body_that_is_not_json_is_rejected()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: false);
        GitHubWebhookController controller = NewController(db, tenant, new RecordingPullService());

        const string body = "definitely not json";
        IActionResult result = await Post(controller, body, Sign(body));

        StatusOf(result).Should().Be(400);
        db.GitHubWebhookDeliveries.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_without_an_event_header_is_rejected()
    {
        (InferenceApiDbContext db, TestTenant tenant, _, _) = await SeededAsync(linked: false);
        GitHubWebhookController controller = NewController(db, tenant, new RecordingPullService());

        const string body = """{"zen":"x"}""";
        IActionResult result = await Post(controller, body, Sign(body), eventName: null);

        StatusOf(result).Should().Be(400);
        db.GitHubWebhookDeliveries.Count().Should().Be(0);
    }

    /// <summary>
    /// A signature is a signature over a body, not over a URL: there is no token, cookie or project id
    /// a caller could put in the request to change which project is touched. Asserted against the
    /// controller's source because it is the property that keeps this endpoint from being a way to
    /// reach an arbitrary project.
    /// </summary>
    [Fact]
    public void The_webhook_never_resolves_a_project_from_the_request()
    {
        string path = Path.Combine(SourceRoot(), "Controllers", "GitHubWebhookController.cs");
        string source = File.ReadAllText(path);

        source.Should().NotContain("ProjectPermission",
            "the webhook has no caller identity to authorize, so it must not pretend to check project permission");
        source.Should().NotContain("projectId",
            "a project is reached only through the repository link the signature proved");
    }

    private static string SourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir);

        dir.Should().NotBeNull();
        return Path.Combine(dir!, "src", "ReelBolt.Inference.Api");
    }
}
