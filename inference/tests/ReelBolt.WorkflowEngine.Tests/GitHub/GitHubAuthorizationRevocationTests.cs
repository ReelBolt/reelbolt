#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// What happens when a person revokes ReelBolt in their GitHub settings.
///
/// <para>
/// GitHub sends one <c>github_app_authorization</c> delivery per revocation, and a GitHub App cannot
/// unsubscribe from it. It is the only signal that a stored user access token has stopped working, so
/// the documented response is to stop calling the API for that person — which here means destroying the
/// credential, not merely ignoring it.
/// </para>
///
/// <para>
/// The second half of the rule is what this must <b>not</b> do. A revoked OAuth grant is about a
/// person; it says nothing about the app's installations, and projects are pulled with an installation
/// token. Detaching a team's repository because one member revoked their personal authorization would
/// be a silent outage.
/// </para>
/// </summary>
public class GitHubAuthorizationRevocationTests
{
    /// <summary>The payload GitHub sends, as documented for the event.</summary>
    private static string RevokedPayload(long senderId = 4242, string action = "revoked") =>
        $"{{\"action\":\"{action}\",\"sender\":{{\"login\":\"octocat\",\"id\":{senderId},\"type\":\"User\",\"site_admin\":false}}}}";

    private sealed record Fixture(
        InferenceApiDbContext Db,
        TestTenant Tenant,
        FakeGitHubAppClient GitHub,
        FakeSecretProtector Secrets,
        GitHubAccountService Accounts,
        GitHubDeliveryProcessor Processor,
        GitHubWebhookController Controller,
        Guid UserId,
        Guid OrganizationId);

    private static async Task<Fixture> NewAsync(bool seedLinkedProject = false)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);

        FakeGitHubAppClient gitHub = new() { IsConfigured = true, IsUserOAuthConfigured = true };
        FakeSecretProtector secrets = new();
        GitHubOptions options = new()
        {
            AppId = "123456",
            PrivateKeyPem = "unused-in-tests",
            WebhookSecret = GitHubTestData.WebhookSecret,
            ClientId = "Iv1.abc",
            ClientSecret = "secret",
            OAuthCallbackUrl = "https://reel.example.com/api/v1/github/oauth/callback"
        };

        IOptions<GitHubOptions> configured = Microsoft.Extensions.Options.Options.Create(options);
        GitHubAccountService accounts = new(db, gitHub, configured, secrets, NullLogger<GitHubAccountService>.Instance);

        // A connected account holding a real (fake-protected) token, so "revoked" can be shown to
        // destroy something rather than to flip a flag.
        db.GitHubAccountLinks.Add(new GitHubAccountLink
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = organizationId,
            GitHubUserId = 4242,
            Login = "octocat",
            ProtectedAccessToken = secrets.Protect("ghu_access_token_value"),
            ProtectedRefreshToken = secrets.Protect("ghr_refresh_token_value"),
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(8)
        });

        tenant.BeginOrganizationScope(organizationId);

        if (seedLinkedProject)
        {
            Project project = GitHubTestData.NewProject(organizationId, userId);
            db.Projects.Add(project);
            db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(project.Id, organizationId, userId));
        }

        await db.SaveChangesAsync();

        GitHubDeliveryProcessor processor = new(
            db,
            new NoopPullService(),
            accounts,
            NullLogger<GitHubDeliveryProcessor>.Instance);

        GitHubWebhookController controller = new(
            db,
            new GitHubWebhookVerifier(configured),
            processor,
            NullLogger<GitHubWebhookController>.Instance,
            tenant)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        return new Fixture(db, tenant, gitHub, secrets, accounts, processor, controller, userId, organizationId);
    }

    private static GitHubWebhookDelivery Delivery(string payload, string eventName = GitHubDeliveryProcessor.EventAuthorization) => new()
    {
        Id = Guid.NewGuid(),
        DeliveryId = "revocation-1",
        Event = eventName,
        PayloadJson = payload,
        ReceivedAt = DateTime.UtcNow
    };

    private static string Sign(string body)
    {
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(GitHubTestData.WebhookSecret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    [Fact]
    public async Task A_revocation_destroys_the_stored_credential()
    {
        Fixture fixture = await NewAsync();

        GitHubDeliveryPlan plan = await fixture.Processor.PlanAsync(Delivery(RevokedPayload()), CancellationToken.None);

        plan.Outcome.Should().Be("authorization_revoked");
        plan.RequiresPull.Should().BeFalse("there is nothing to pull; the delivery is settled here");

        GitHubAccountLink link = await fixture.Db.GitHubAccountLinks.SingleAsync();
        link.RevokedAt.Should().NotBeNull();
        link.RevokedReason.Should().Be("user_revoked");

        // The credential is gone, not merely marked unusable: the columns are wiped, so no future bug
        // can reach a token GitHub has already stopped honouring.
        link.ProtectedAccessToken.Should().BeEmpty();
        link.ProtectedRefreshToken.Should().BeNull();
        link.AccessTokenExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task A_revocation_leaves_linked_projects_alone()
    {
        Fixture fixture = await NewAsync(seedLinkedProject: true);

        await fixture.Processor.PlanAsync(Delivery(RevokedPayload()), CancellationToken.None);

        // Pulls use the app's installation token, which this event does not touch, so a push must keep
        // arriving after somebody revokes their personal authorization.
        (await fixture.Db.ProjectGitHubLinks.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_revocation_for_an_unknown_account_settles_without_touching_anything()
    {
        Fixture fixture = await NewAsync();

        GitHubDeliveryPlan plan = await fixture.Processor.PlanAsync(Delivery(RevokedPayload(senderId: 999_999)), CancellationToken.None);

        plan.Outcome.Should().Be("authorization_unknown");
        (await fixture.Db.GitHubAccountLinks.SingleAsync()).RevokedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("created")]
    [InlineData("")]
    public async Task An_authorization_event_that_is_not_a_revocation_is_ignored(string action)
    {
        Fixture fixture = await NewAsync();
        string payload = action.Length == 0 ? "{\"sender\":{\"id\":4242}}" : RevokedPayload(action: action);

        GitHubDeliveryPlan plan = await fixture.Processor.PlanAsync(Delivery(payload), CancellationToken.None);

        plan.Outcome.Should().Be("ignored_event");
        (await fixture.Db.GitHubAccountLinks.SingleAsync()).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_payload_without_a_sender_id_is_ignored_rather_than_guessed_at()
    {
        Fixture fixture = await NewAsync();

        GitHubDeliveryPlan plan = await fixture.Processor.PlanAsync(
            Delivery("{\"action\":\"revoked\",\"sender\":{\"login\":\"octocat\"}}"),
            CancellationToken.None);

        plan.Outcome.Should().Be("ignored_event");
        (await fixture.Db.GitHubAccountLinks.SingleAsync()).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_push_delivery_is_still_a_push()
    {
        Fixture fixture = await NewAsync(seedLinkedProject: true);

        // The new branch in PlanAsync must not swallow the event it was added next to.
        GitHubDeliveryPlan plan = await fixture.Processor.PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc123"), eventName: "push"),
            CancellationToken.None);

        plan.Outcome.Should().Be("pulled");
        plan.RequiresPull.Should().BeTrue();
    }

    [Fact]
    public async Task The_webhook_endpoint_settles_a_signed_revocation_with_200()
    {
        Fixture fixture = await NewAsync();
        string body = RevokedPayload();

        GitHubWebhookController controller = fixture.Controller;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Hub-Signature-256"] = Sign(body);
        controller.ControllerContext.HttpContext.Request.Headers["X-GitHub-Event"] = GitHubDeliveryProcessor.EventAuthorization;
        controller.ControllerContext.HttpContext.Request.Headers["X-GitHub-Delivery"] = "delivery-revocation-1";

        IActionResult result = await controller.Webhook(CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        JsonSerializer.Serialize(ok.Value).Should().Contain("authorization_revoked");

        // A signed revocation is a 200, not a 202: nothing is owed to the worker.
        GitHubWebhookDelivery delivery = await fixture.Db.GitHubWebhookDeliveries.SingleAsync();
        delivery.Outcome.Should().Be("authorization_revoked");
        delivery.ProcessedAt.Should().NotBeNull();

        (await fixture.Db.GitHubAccountLinks.SingleAsync()).RevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unsigned_revocation_never_reaches_the_account()
    {
        Fixture fixture = await NewAsync();
        string body = RevokedPayload();

        GitHubWebhookController controller = fixture.Controller;
        controller.ControllerContext.HttpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        controller.ControllerContext.HttpContext.Request.Headers["X-Hub-Signature-256"] = "sha256=deadbeef";
        controller.ControllerContext.HttpContext.Request.Headers["X-GitHub-Event"] = GitHubDeliveryProcessor.EventAuthorization;

        IActionResult result = await controller.Webhook(CancellationToken.None);

        ((ObjectResult)result).StatusCode.Should().Be(401);
        (await fixture.Db.GitHubAccountLinks.SingleAsync()).RevokedAt.Should().BeNull();
        (await fixture.Db.GitHubWebhookDeliveries.CountAsync()).Should().Be(0, "a refused delivery leaves no trace");
    }

    private sealed class NoopPullService : IGitHubRepositoryPullService
    {
        public Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", commitSha, 0, 0, 0, 0));

        public Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", null, 0, 0, 0, 0));
    }
}
