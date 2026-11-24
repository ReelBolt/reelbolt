#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.GitHub;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// Which project a verified delivery touches, and the queue that carries it there.
///
/// <para>
/// The payload can name a repository, a branch and a commit; it can never name a project. The project
/// is the one the repository is linked to, and a push to any other branch, another installation, or an
/// unlinked repository is settled without doing anything.
/// </para>
/// </summary>
public class GitHubDeliveryProcessorTests
{
    private static async Task<(InferenceApiDbContext Db, ProjectGitHubLink Link)> SeededAsync(bool linked = true, string? branch = null)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);

        ProjectGitHubLink link = GitHubTestData.NewLink(project.Id, organizationId, ownerId);
        link.Branch = branch;
        if (linked) db.ProjectGitHubLinks.Add(link);

        await db.SaveChangesAsync();
        return (db, link);
    }

    private static GitHubDeliveryProcessor Processor(InferenceApiDbContext db, IGitHubRepositoryPullService? pull = null) =>
        new(db, pull ?? new NoopPullService(), new NoopGitHubAccountService(), NullLogger<GitHubDeliveryProcessor>.Instance);

    private sealed class NoopPullService : IGitHubRepositoryPullService
    {
        public Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", commitSha, 0, 0, 0, 0));

        public Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", null, 0, 0, 0, 0));
    }

    private static GitHubWebhookDelivery Delivery(string payload, string eventName = "push", long? installationId = GitHubTestData.InstallationId) => new()
    {
        Id = Guid.NewGuid(),
        DeliveryId = "d-1",
        Event = eventName,
        InstallationId = installationId,
        PayloadJson = payload,
        ReceivedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task An_event_that_is_not_a_push_is_ignored()
    {
        (InferenceApiDbContext db, _) = await SeededAsync();

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(Delivery("""{"zen":"x"}""", eventName: "ping"), CancellationToken.None);

        plan.Outcome.Should().Be("ignored_event");
        plan.RequiresPull.Should().BeFalse();
    }

    [Fact]
    public async Task A_push_to_a_repository_that_is_not_linked_is_ignored()
    {
        (InferenceApiDbContext db, _) = await SeededAsync(linked: false);

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc")), CancellationToken.None);

        plan.Outcome.Should().Be("no_link");
    }

    [Fact]
    public async Task A_push_to_another_branch_of_the_linked_repository_is_ignored()
    {
        (InferenceApiDbContext db, _) = await SeededAsync();

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc", branch: "feature/other")), CancellationToken.None);

        plan.Outcome.Should().Be("ignored_ref");
    }

    [Fact]
    public async Task A_push_that_arrives_under_another_installation_is_ignored()
    {
        (InferenceApiDbContext db, _) = await SeededAsync();

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc", installationId: 999999), installationId: 999999), CancellationToken.None);

        plan.Outcome.Should().Be("installation_mismatch");
    }

    [Fact]
    public async Task A_push_that_repeats_the_commit_already_applied_is_ignored()
    {
        (InferenceApiDbContext db, ProjectGitHubLink link) = await SeededAsync();
        link.LastPulledCommit = "abc";
        await db.SaveChangesAsync();

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc")), CancellationToken.None);

        plan.Outcome.Should().Be("unchanged");
    }

    [Fact]
    public async Task A_branch_deletion_is_ignored()
    {
        (InferenceApiDbContext db, _) = await SeededAsync();

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload(new string('0', 40), deleted: true)), CancellationToken.None);

        plan.Outcome.Should().Be("ignored_ref");
    }

    [Fact]
    public async Task A_normal_push_names_the_linked_project_and_the_commit()
    {
        (InferenceApiDbContext db, ProjectGitHubLink link) = await SeededAsync();
        DateTime when = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        GitHubDeliveryPlan plan = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("deadbeef", timestamp: when)), CancellationToken.None);

        plan.RequiresPull.Should().BeTrue();
        plan.Link!.ProjectId.Should().Be(link.ProjectId);
        plan.CommitSha.Should().Be("deadbeef");
        plan.PushedAt.Should().Be(when);
    }

    [Fact]
    public async Task A_push_to_a_custom_branch_is_matched_on_that_branch()
    {
        (InferenceApiDbContext db, _) = await SeededAsync(branch: "release/1");

        GitHubDeliveryPlan accepted = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc", branch: "release/1")), CancellationToken.None);
        GitHubDeliveryPlan refused = await Processor(db).PlanAsync(
            Delivery(GitHubTestData.PushPayload("abc", branch: "main")), CancellationToken.None);

        accepted.RequiresPull.Should().BeTrue();
        refused.Outcome.Should().Be("ignored_ref");
    }
}

/// <summary>
/// The delivery queue. The webhook acknowledges in milliseconds; this is the half that may take
/// minutes, and it has to survive a restart, a GitHub outage and a permanently bad delivery.
/// </summary>
public class GitHubPushSyncServiceTests
{
    private sealed class ScriptedPullService : IGitHubRepositoryPullService
    {
        public List<string> Commits { get; } = [];
        public int Failures { get; set; }

        public Task<GitHubPullOutcome> PullAsync(ProjectGitHubLink link, string commitSha, DateTime? pushedAt, string? deliveryId, CancellationToken ct)
        {
            Commits.Add(commitSha);
            if (Failures > 0)
            {
                Failures--;
                throw new GitHubApiException("GitHub said no.", System.Net.HttpStatusCode.BadGateway);
            }

            return Task.FromResult(new GitHubPullOutcome("pulled", commitSha, 1, 0, 0, 0));
        }

        public Task<GitHubPullOutcome> PullBranchAsync(ProjectGitHubLink link, bool force, CancellationToken ct) =>
            Task.FromResult(new GitHubPullOutcome("pulled", null, 0, 0, 0, 0));
    }

    private static async Task<(InferenceApiDbContext Db, GitHubPushSyncService Worker, ScriptedPullService Pull)> NewAsync(
        int maxAttempts = 3,
        params string[] payloads)
    {
        TestTenant tenant = new();
        InferenceApiDbContext db = GitHubTestDb.New(tenant);

        Guid ownerId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(ownerId);
        Project project = GitHubTestData.NewProject(organizationId, ownerId);
        db.Projects.Add(project);
        db.ProjectGitHubLinks.Add(GitHubTestData.NewLink(project.Id, organizationId, ownerId));

        int index = 0;
        foreach (string payload in payloads)
        {
            db.GitHubWebhookDeliveries.Add(new GitHubWebhookDelivery
            {
                Id = Guid.NewGuid(),
                DeliveryId = $"d-{index++}",
                Event = "push",
                InstallationId = GitHubTestData.InstallationId,
                PayloadJson = payload,
                ReceivedAt = DateTime.UtcNow.AddSeconds(index)
            });
        }

        await db.SaveChangesAsync();

        ScriptedPullService pull = new();
        GitHubDeliveryProcessor processor = new(db, pull, new NoopGitHubAccountService(), NullLogger<GitHubDeliveryProcessor>.Instance);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IGitHubDeliveryProcessor>(processor)
            .AddSingleton(tenant)
            .BuildServiceProvider();

        GitHubPushSyncService worker = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GitHubOptions { AppId = "1", PrivateKeyPem = "x", WebhookSecret = "s", MaxDeliveryAttempts = maxAttempts }),
            NullLogger<GitHubPushSyncService>.Instance);

        return (db, worker, pull);
    }

    [Fact]
    public async Task A_queued_delivery_is_pulled_once_and_settled()
    {
        (InferenceApiDbContext db, GitHubPushSyncService worker, ScriptedPullService pull) =
            await NewAsync(3, GitHubTestData.PushPayload("commit-1"));

        int processed = await worker.SweepOnceAsync(CancellationToken.None);

        processed.Should().Be(1);
        pull.Commits.Should().Equal("commit-1");

        GitHubWebhookDelivery delivery = await db.GitHubWebhookDeliveries.SingleAsync();
        delivery.ProcessedAt.Should().NotBeNull();
        delivery.Outcome.Should().Be("pulled");
        delivery.Attempts.Should().Be(1);
        delivery.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_settled_delivery_is_not_picked_up_again()
    {
        (InferenceApiDbContext db, GitHubPushSyncService worker, ScriptedPullService pull) =
            await NewAsync(3, GitHubTestData.PushPayload("commit-1"));

        await worker.SweepOnceAsync(CancellationToken.None);
        int again = await worker.SweepOnceAsync(CancellationToken.None);

        again.Should().Be(0);
        pull.Commits.Should().HaveCount(1);
        db.GitHubWebhookDeliveries.Count().Should().Be(1);
    }

    [Fact]
    public async Task A_failing_pull_is_retried_and_then_parked_with_its_reason()
    {
        (InferenceApiDbContext db, GitHubPushSyncService worker, ScriptedPullService pull) =
            await NewAsync(maxAttempts: 2, GitHubTestData.PushPayload("commit-1"));

        pull.Failures = 99;

        await worker.SweepOnceAsync(CancellationToken.None);
        GitHubWebhookDelivery afterFirst = await db.GitHubWebhookDeliveries.SingleAsync();
        afterFirst.ProcessedAt.Should().BeNull("the attempt budget is not spent yet, so the delivery stays queued");
        afterFirst.Attempts.Should().Be(1);
        afterFirst.Error.Should().NotBeNull();

        await worker.SweepOnceAsync(CancellationToken.None);
        GitHubWebhookDelivery afterSecond = await db.GitHubWebhookDeliveries.SingleAsync();
        afterSecond.ProcessedAt.Should().NotBeNull("the budget is spent, so parking the delivery stops an endless retry loop");
        afterSecond.Outcome.Should().Be("failed");
        afterSecond.Attempts.Should().Be(2);

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_whose_repository_is_no_longer_linked_is_settled_without_work()
    {
        (InferenceApiDbContext db, GitHubPushSyncService worker, ScriptedPullService pull) =
            await NewAsync(3, GitHubTestData.PushPayload("commit-1", repositoryId: 111111111));

        await worker.SweepOnceAsync(CancellationToken.None);

        GitHubWebhookDelivery delivery = await db.GitHubWebhookDeliveries.SingleAsync();
        delivery.Outcome.Should().Be("no_link");
        delivery.ProcessedAt.Should().NotBeNull();
        pull.Commits.Should().BeEmpty();
    }
}
