
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// C9b acceptance: the purge removes the rows and the blob keys of a due account, anonymises the
/// usage rows it keeps, leaves a shared team workspace alone, waits out the grace period, and is
/// resumable when a step fails. The object store is a mocked IAmazonS3.
///
/// <para>
/// <b>What this file cannot cover:</b> it runs on the in-memory provider, which ignores transactions
/// (<c>InMemoryEventId.TransactionIgnoredWarning</c> below is that fact written down), so
/// <see cref="Purge_is_resumable_because_a_failed_step_defers_the_whole_account"/> fails the sweep
/// <i>before</i> any row is deleted and can say nothing about a failure after one. The resumability
/// of a failure at the commit itself — C11 F-2, where the purge used to report success over stranded
/// content — is covered on a provider that honours transactions by
/// <see cref="AccountPurgeTransactionTests"/>, and that is the file a change to the commit order has
/// to keep green.
/// </para>
/// </summary>
public class AccountPurgeServiceTests
{
    private sealed class Storage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public bool FailListings { get; set; }
    }

    private static IAmazonS3 BuildS3(Storage storage)
    {
        var mock = new Mock<IAmazonS3>();
        mock.Setup(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ListObjectsV2Request request, CancellationToken _) =>
            {
                if (storage.FailListings)
                    throw new AmazonS3Exception("store down") { StatusCode = HttpStatusCode.InternalServerError };
                return new ListObjectsV2Response
                {
                    S3Objects = storage.Objects.Keys
                        .Where(k => k.StartsWith(request.Prefix, StringComparison.Ordinal))
                        .OrderBy(k => k)
                        .Select(k => new S3Object { Key = k, LastModified = DateTime.UtcNow })
                        .ToList(),
                    IsTruncated = false
                };
            });

        mock.Setup(s => s.DeleteObjectsAsync(It.IsAny<DeleteObjectsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DeleteObjectsRequest, CancellationToken>((request, _) =>
            {
                foreach (KeyVersion key in request.Objects)
                {
                    storage.Deleted.Add(key.Key);
                    storage.Objects.Remove(key.Key);
                }
            })
            .ReturnsAsync(new DeleteObjectsResponse());
        return mock.Object;
    }

    private static (AccountPurgeService Service, IServiceProvider Provider, Storage Storage) Build(
        bool enabled = true, bool cloud = true)
    {
        var storage = new Storage();
        string databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton(BuildS3(storage));
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddScoped<IAccountBillingCanceller, AccountBillingCanceller>();
        var vectors = new Mock<IVectorIndexService>();
        vectors.Setup(v => v.DeleteProjectCollectionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);
        services.AddSingleton(vectors.Object);
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<ISpeechSynthesisClientFactory>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        IServiceProvider provider = services.BuildServiceProvider();

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:BucketName"] = "test-bucket",
            ["Tenancy:Mode"] = cloud ? "Cloud" : "SelfHosted"
        }).Build();
        var options = new AccountOptions
        {
            Purge = new AccountPurgeOptions { Enabled = enabled, GraceDays = 30, PurgeUnverified = true }
        };
        var service = new AccountPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            configuration,
            NullLogger<AccountPurgeService>.Instance);
        return (service, provider, storage);
    }

    private sealed record Seeded(Guid UserId, Guid OrgId, Guid ProjectId, Guid TeamOrgId);

    /// <summary>
    /// One user whose account is due: a personal workspace with content, a team workspace shared with
    /// somebody else, and one row in each of the tables C0 says to anonymise or to retain.
    /// </summary>
    private static async Task<Seeded> SeedAsync(IServiceProvider provider, Storage storage, DateTime requestedAt)
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid teamOrgId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid threadId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        string firstKey = "projects/" + projectId + "/userFiles/small.txt";
        string secondKey = "projects/" + projectId + "/outputFiles/render.mp4";
        storage.Objects[firstKey] = "hello"u8.ToArray();
        storage.Objects[secondKey] = new byte[32];

        using IServiceScope scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        DateTime now = DateTime.UtcNow;
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = userId,
            Email = "ada@example.com",
            DisplayName = "Ada",
            PasswordHash = "hash",
            CreatedAt = now.AddDays(-100),
            EmailVerifiedAt = now.AddDays(-100),
            DeletionRequestedAt = requestedAt
        });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = otherUserId,
            Email = "bob@example.com",
            DisplayName = "Bob",
            PasswordHash = "hash",
            CreatedAt = now.AddDays(-100),
            EmailVerifiedAt = now.AddDays(-100)
        });
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Ada workspace",
            Slug = "ada",
            Kind = OrganizationKind.Personal,
            CreatedByUserId = userId,
            CreatedAt = now.AddDays(-100),
            UpdatedAt = now.AddDays(-100)
        });
        db.Organizations.Add(new Organization
        {
            Id = teamOrgId,
            Name = "Acme",
            Slug = "acme",
            Kind = OrganizationKind.Team,
            CreatedByUserId = otherUserId,
            CreatedAt = now.AddDays(-100),
            UpdatedAt = now.AddDays(-100)
        });
        db.OrganizationMemberships.AddRange(
            new OrganizationMembership { OrganizationId = orgId, UserId = userId, Role = OrganizationRole.Owner, CreatedAt = now.AddDays(-100) },
            new OrganizationMembership { OrganizationId = teamOrgId, UserId = userId, Role = OrganizationRole.Admin, CreatedAt = now.AddDays(-90) },
            new OrganizationMembership { OrganizationId = teamOrgId, UserId = otherUserId, Role = OrganizationRole.Owner, CreatedAt = now.AddDays(-90) });
        db.LegalAcceptances.Add(new LegalAcceptance
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Document = LegalDocument.Terms,
            Version = "2026-10",
            AcceptedAt = now.AddDays(-100)
        });
        db.RunRateEvents.AddRange(
            new RunRateEvent { Id = Guid.NewGuid(), UserId = userId, ProjectId = projectId, Bucket = "paid", CreatedAt = now.AddMinutes(-5) },
            new RunRateEvent { Id = Guid.NewGuid(), UserId = otherUserId, ProjectId = projectId, Bucket = "paid", CreatedAt = now.AddMinutes(-5) });
        db.UserTokens.Add(new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = UserTokenPurpose.VerifyEmail,
            TokenHash = "tok",
            CreatedAt = now.AddDays(-100),
            ExpiresAt = now.AddDays(-99),
            UsedAt = now.AddDays(-100)
        });
        db.OrganizationInvites.Add(new OrganizationInvite
        {
            Id = Guid.NewGuid(),
            OrganizationId = teamOrgId,
            Email = "ada@example.com",
            Role = OrganizationRole.Member,
            TokenHash = "invite-to-ada",
            InvitedByUserId = otherUserId,
            CreatedAt = now.AddDays(-10),
            ExpiresAt = now.AddDays(1)
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Launch video",
            OwnerId = userId,
            OrganizationId = orgId,
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        });
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = firstKey,
            Category = "userFiles",
            OriginalFileName = "small.txt",
            MimeType = "text/plain",
            SizeBytes = 5,
            StorageBucket = "test-bucket",
            UploadedAt = now.AddDays(-90)
        });
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            ProjectId = projectId,
            Name = "Promo",
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = stepId,
            WorkflowDefinitionId = workflowId,
            StepOrder = 1,
            Label = "Script",
            StepType = StepType.Agent
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            OrganizationId = orgId,
            Status = ExecutionStatus.Passed,
            StartedAt = now.AddDays(-89),
            CompletedAt = now.AddDays(-89),
            InitiatedByUserId = userId,
            UserRequest = "make me a promo"
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = stepId,
            Status = StepStatus.Completed,
            ExecutedAt = now.AddDays(-89),
            Output = "the script"
        });
        db.WorkflowStepCacheEntries.Add(new WorkflowStepCacheEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CacheKey = "cache-key",
            StepType = StepType.Agent,
            AgentType = AgentType.ScriptwriterAgent,
            Output = "a cached transcript",
            CreatedAt = now.AddDays(-89)
        });
        db.DecisionObservations.Add(new DecisionObservation
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            ProjectId = projectId,
            OrganizationId = orgId,
            StepOrder = 1,
            AgentType = AgentType.ScriptwriterAgent,
            Site = "EditRoom",
            Question = "keep this shot?",
            OptionsHash = "abc",
            Kind = DecisionKind.Choice,
            Answer = "yes",
            Mode = "Shadow",
            CreatedAt = now.AddDays(-89)
        });
        db.ExternalGenerationJobs.Add(new ExternalGenerationJob
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepResultId = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = orgId,
            ClipId = "clip-1",
            RequestHash = "hash",
            ProviderId = Guid.NewGuid(),
            Status = ExternalGenerationJobStatus.Succeeded
        });
        db.AssistantThreads.Add(new AssistantThread
        {
            Id = threadId,
            UserId = userId,
            OrganizationId = orgId,
            Title = "help",
            CreatedAt = now.AddDays(-80),
            UpdatedAt = now.AddDays(-80)
        });
        db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            Role = "user",
            Content = "how do I render?",
            Sequence = 1,
            CreatedAt = now.AddDays(-80)
        });
        db.AssistantRunWatches.Add(new AssistantRunWatch
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            ExecutionId = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = orgId,
            ProjectId = Guid.NewGuid(),
            WorkflowName = "Film",
            CreatedAt = now.AddDays(-80),
            ExpiresAt = now.AddDays(-79)
        });
        db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "My agent",
            Description = "custom",
            SystemPrompt = "you write scripts",
            AgentType = AgentType.Custom,
            OwnerId = userId,
            OrganizationId = orgId,
            CreatedAt = now.AddDays(-80)
        });
        db.EditingStyles.Add(new EditingStyle
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            ProjectId = projectId,
            Name = "My style",
            ConfigJson = "{}",
            CreatedAt = now.AddDays(-80),
            UpdatedAt = now.AddDays(-80)
        });
        db.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ProjectId = projectId,
            ExecutionId = executionId,
            UserId = userId,
            Source = UsageSource.Workflow,
            Kind = UsageKind.LlmInputTokens,
            Quantity = 1000,
            ModelName = "gpt-4o",
            CreditsMillis = 12,
            CostUsd = 0.01m,
            IdempotencyKey = "usage-1",
            OccurredAt = now.AddDays(-89)
        });
        db.CreditAccounts.Add(new CreditAccount
        {
            Id = accountId,
            OrganizationId = orgId,
            BalanceMillis = 900,
            ReservedMillis = 0,
            UpdatedAt = now.AddDays(-89)
        });
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            OrganizationId = orgId,
            Kind = CreditLedgerEntryKind.Grant,
            BalanceDeltaMillis = 1000,
            BalanceAfterMillis = 1000,
            CreatedAt = now.AddDays(-90)
        });
        db.TrialClaims.Add(new TrialClaim
        {
            Id = Guid.NewGuid(),
            EmailSha256 = new string('a', 64),
            OrganizationId = orgId,
            ClaimedAt = now.AddDays(-90)
        });
        db.Plans.Add(new Plan { Id = planId, Key = "creator", Name = "Creator", Kind = PlanKind.Subscription });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            Seats = 1,
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            PlanId = planId,
            Provider = "paddle",
            ProviderSubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            Seats = 1,
            CreatedAt = now.AddDays(-90),
            UpdatedAt = now.AddDays(-90)
        });
        db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Provider = "paddle",
            ProviderCustomerId = "ctm_1",
            Email = "ada@example.com",
            CreatedAt = now.AddDays(-90)
        });
        await db.SaveChangesAsync();
        return new Seeded(userId, orgId, projectId, teamOrgId);
    }

    private static async Task<T> WithDbAsync<T>(IServiceProvider provider, Func<InferenceApiDbContext, Task<T>> read)
    {
        using IServiceScope scope = provider.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>());
    }

    [Fact]
    public async Task Purge_is_deferred_while_a_project_has_an_output_marker_and_no_quarantined_row()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        await WithDbAsync(provider, async db =>
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.ProjectId)).CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(0);
        storage.Deleted.Should().BeEmpty();
        (await WithDbAsync(provider, db => db.Projects.IgnoreQueryFilters().CountAsync())).Should().Be(1);
        (await WithDbAsync(provider, db => db.WorkflowStepResults.CountAsync())).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_deferral_for_an_output_marker_alone_does_not_say_evidence_is_present()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        await WithDbAsync(provider, async db =>
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.ProjectId)).CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeUserResult result = await service.PurgeUserAsync(seeded.UserId, DateTime.UtcNow, CancellationToken.None);

        result.Purged.Should().BeFalse();
        result.Failures.Should().ContainSingle().Which.Should().NotContain("evidence is present").And.Contain("marker");
    }

    [Fact]
    public async Task Purge_removes_rows_and_blob_keys_anonymises_usage_and_cancels_what_it_owns()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(1);
        run.ProjectsPurged.Should().Be(1);
        run.ObjectsDeleted.Should().Be(2, "both objects under the project prefix are gone");
        run.CollectionsDeleted.Should().Be(2, "the project's vector collections go with it");
        run.SubscriptionsCancelled.Should().Be(1, "the internal subscription has no provider to cancel");
        run.SubscriptionsRetainedForVendor.Should().Be(1,
            "a live paddle subscription is B9b's to cancel, and the purge says so instead of pretending it is gone");

        storage.Deleted.Should().HaveCount(2);
        storage.Deleted.Should().OnlyContain(k => k.StartsWith("projects/" + seeded.ProjectId + "/", StringComparison.Ordinal));

        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(1, "only Bob is left");
        (await WithDbAsync(provider, db => db.Projects.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ProjectFiles.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.WorkflowDefinitions.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.WorkflowSteps.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.WorkflowExecutions.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.WorkflowStepResults.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.WorkflowStepCacheEntries.CountAsync())).Should().Be(0,
            "cached step outputs hold transcripts and have no foreign key to the project");
        (await WithDbAsync(provider, db => db.DecisionObservations.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ExternalGenerationJobs.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.AssistantThreads.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.AssistantMessages.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.AssistantRunWatches.CountAsync())).Should().Be(0, "the user's run watches go with their threads");
        (await WithDbAsync(provider, db => db.AgentDefinitions.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.EditingStyles.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.LegalAcceptances.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.UserTokens.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.RunRateEvents.CountAsync())).Should().Be(1,
            "the purged user's run-limit rows go, Bob's stay");
        (await WithDbAsync(provider, db => db.OrganizationMemberships.CountAsync())).Should().Be(1,
            "the team membership goes, Bob's stays");

        // What C0 says to keep: the usage row, without the person.
        List<UsageEvent> usage = await WithDbAsync(provider, db => db.UsageEvents.ToListAsync());
        usage.Should().ContainSingle("the bookkeeping row is retained, keyed by the organization");
        usage[0].UserId.Should().BeNull("C0 anonymises usage rather than deleting it");
        usage[0].OrganizationId.Should().Be(seeded.OrgId);

        // The trial claim is a hash and stays, so the address cannot claim a second trial.
        (await WithDbAsync(provider, db => db.TrialClaims.CountAsync())).Should().Be(1);

        // The billing record keeps its trail but loses the address.
        List<BillingCustomer> customers = await WithDbAsync(provider, db => db.BillingCustomers.ToListAsync());
        customers.Should().ContainSingle();
        customers[0].Email.Should().BeNull();

        // The personal workspace is soft-deleted and de-identified; the shared team workspace is not.
        List<Organization> orgs = await WithDbAsync(provider, db => db.Organizations.ToListAsync());
        orgs.Should().HaveCount(2);
        Organization personal = orgs.Single(o => o.Id == seeded.OrgId);
        personal.DeletedAt.Should().NotBeNull();
        personal.Name.Should().Be(AccountPurgeService.AnonymisedOrganizationName,
            "a Personal workspace is named after its owner");
        Organization team = orgs.Single(o => o.Id == seeded.TeamOrgId);
        team.DeletedAt.Should().BeNull("Bob still works there");
        team.Name.Should().Be("Acme");

        // The invite addressed to the user is gone; the invoice trail stays.
        (await WithDbAsync(provider, db => db.OrganizationInvites.CountAsync())).Should().Be(0);
        List<Subscription> subscriptions = await WithDbAsync(provider, db => db.Subscriptions.ToListAsync());
        subscriptions.Should().Contain(s => s.Provider == "paddle" && s.Status == SubscriptionStatus.Active);
        subscriptions.Should().Contain(s => s.Provider == null && s.Status == SubscriptionStatus.Canceled);
        (await WithDbAsync(provider, db => db.CreditLedgerEntries.CountAsync())).Should().Be(1,
            "the credit ledger is retained for bookkeeping");
    }

    [Fact]
    public async Task Purge_stops_the_subscription_immediately_but_waits_out_the_grace_period()
    {
        var (service, provider, storage) = Build();
        await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-5));

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(0);
        run.UsersDeferred.Should().Be(1);
        run.SubscriptionsCancelled.Should().Be(1, "nobody should pay for a workspace they have left");
        run.ObjectsDeleted.Should().Be(0);

        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(2, "the account is still there");
        (await WithDbAsync(provider, db => db.Projects.IgnoreQueryFilters().CountAsync())).Should().Be(1);
        storage.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Purge_keeps_a_team_workspace_that_still_has_other_members()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));

        Guid teamProjectId = Guid.NewGuid();
        using (IServiceScope scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project
            {
                Id = teamProjectId,
                Name = "Team video",
                OwnerId = seeded.UserId,
                OrganizationId = seeded.TeamOrgId,
                CreatedAt = DateTime.UtcNow.AddDays(-50),
                UpdatedAt = DateTime.UtcNow.AddDays(-50)
            });
            await db.SaveChangesAsync();
        }

        await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        (await WithDbAsync(provider, db => db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == teamProjectId)))
            .Should().Be(1, "a team workspace with other members is not part of the account's purge");
        (await WithDbAsync(provider, db => db.Organizations.CountAsync(o => o.Id == seeded.TeamOrgId && o.DeletedAt == null)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Purge_is_resumable_because_a_failed_step_defers_the_whole_account()
    {
        var (service, provider, storage) = Build();
        await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        storage.FailListings = true;

        AccountPurgeRunResult first = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        first.UsersPurged.Should().Be(0);
        first.UsersDeferred.Should().Be(1);
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(2,
            "the row is the worklist: it stays until every content step has succeeded");
        (await WithDbAsync(provider, db => db.Projects.IgnoreQueryFilters().CountAsync())).Should().Be(1,
            "nothing is half-deleted by a failed sweep either");

        storage.FailListings = false;
        AccountPurgeRunResult second = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        second.UsersPurged.Should().Be(1);
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Unverified_accounts_are_purged_in_cloud_tenancy_only()
    {
        foreach (bool cloud in new[] { true, false })
        {
            var (service, provider, storage) = Build(cloud: cloud);

            using (IServiceScope scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                db.ApplicationUsers.Add(new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    Email = "spam@example.com",
                    DisplayName = "spam",
                    PasswordHash = "hash",
                    CreatedAt = DateTime.UtcNow.AddDays(-30),
                    EmailVerifiedAt = null
                });
                await db.SaveChangesAsync();
            }

            AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

            if (cloud)
            {
                run.UsersPurged.Should().Be(1, "an unverified cloud account is purged after a week");
                (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(0);
            }
            else
            {
                run.UsersConsidered.Should().Be(0, "self-host keeps every account, verified or not");
                (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(1);
            }
        }
    }

    [Fact]
    public async Task Purge_does_nothing_when_it_is_switched_off()
    {
        var (service, provider, storage) = Build(enabled: false);
        await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersConsidered.Should().Be(0);
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync())).Should().Be(2);
    }

    [Fact]
    public async Task Purge_removes_guest_review_links_decisions_and_guest_comments_of_the_purged_workspace_and_the_users_own_links()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));

        Guid personalTimeline = Guid.NewGuid();
        Guid personalLink = Guid.NewGuid();
        Guid teamProject = Guid.NewGuid();
        Guid teamTimeline = Guid.NewGuid();
        Guid teamLink = Guid.NewGuid();
        Guid otherLink = Guid.NewGuid();
        Guid teamOwner = (await WithDbAsync(provider, db => db.OrganizationMemberships.IgnoreQueryFilters()
            .Where(m => m.OrganizationId == seeded.TeamOrgId && m.Role == OrganizationRole.Owner).Select(m => m.UserId).FirstAsync()));
        using (IServiceScope scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Projects.Add(new Project { Id = teamProject, Name = "Team", OwnerId = teamOwner, OrganizationId = seeded.TeamOrgId });
            db.EditTimelines.AddRange(
                new EditTimelineEntity { Id = personalTimeline, ProjectId = seeded.ProjectId, Name = "Mine", DocumentJson = "{}" },
                new EditTimelineEntity { Id = teamTimeline, ProjectId = teamProject, Name = "Shared", DocumentJson = "{}" });
            EditReviewLink Link(Guid id, Guid project, Guid org, Guid timeline, Guid creator) => new()
            {
                Id = id,
                ProjectId = project,
                OrganizationId = org,
                TimelineId = timeline,
                CreatedByUserId = creator,
                TokenHash = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(),
                RenderExecutionId = Guid.NewGuid(),
                ExpiresAt = DateTime.UtcNow.AddDays(5),
            };
            db.EditReviewLinks.AddRange(
                Link(personalLink, seeded.ProjectId, seeded.OrgId, personalTimeline, seeded.UserId),
                Link(teamLink, teamProject, seeded.TeamOrgId, teamTimeline, seeded.UserId),
                Link(otherLink, teamProject, seeded.TeamOrgId, teamTimeline, teamOwner));
            db.EditReviewDecisions.AddRange(
                new EditReviewDecision { Id = Guid.NewGuid(), LinkId = personalLink, ProjectId = seeded.ProjectId, TimelineId = personalTimeline, Decision = EditReviewDecisionKind.Approved, GuestName = "G" },
                new EditReviewDecision { Id = Guid.NewGuid(), LinkId = teamLink, ProjectId = teamProject, TimelineId = teamTimeline, Decision = EditReviewDecisionKind.Approved, GuestName = "G" },
                new EditReviewDecision { Id = Guid.NewGuid(), LinkId = otherLink, ProjectId = teamProject, TimelineId = teamTimeline, Decision = EditReviewDecisionKind.Approved, GuestName = "G" });
            db.EditTimelineComments.AddRange(
                new EditTimelineComment { Id = Guid.NewGuid(), ProjectId = seeded.ProjectId, TimelineId = personalTimeline, AuthorUserId = Guid.Empty, AuthorName = "G", GuestName = "G", GuestLinkId = personalLink, Frame = 1, Body = "x" },
                new EditTimelineComment { Id = Guid.NewGuid(), ProjectId = teamProject, TimelineId = teamTimeline, AuthorUserId = Guid.Empty, AuthorName = "G", GuestName = "G", GuestLinkId = otherLink, Frame = 1, Body = "y" });
            await db.SaveChangesAsync();
        }

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(1);
        (await WithDbAsync(provider, db => db.EditReviewLinks.IgnoreQueryFilters().Select(l => l.Id).ToListAsync()))
            .Should().BeEquivalentTo([otherLink], "the purged workspace's links and every link the user created are gone; a colleague's link stays");
        (await WithDbAsync(provider, db => db.EditReviewDecisions.Select(d => d.LinkId).ToListAsync()))
            .Should().BeEquivalentTo([otherLink]);
        (await WithDbAsync(provider, db => db.EditTimelineComments.IgnoreQueryFilters().Select(c => c.GuestLinkId).ToListAsync()))
            .Should().BeEquivalentTo([otherLink], "the purged project's guest comments went with it; the shared workspace's content stays");
    }

    [Fact]
    public async Task Purge_removes_the_projects_stored_moderation_verdicts()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        await WithDbAsync(provider, async db =>
        {
            db.OutputModerationVerdicts.Add(new ReelBolt.Inference.Api.Services.Moderation.OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = seeded.OrgId,
                ProjectId = seeded.ProjectId,
                StorageKey = "projects/" + seeded.ProjectId + "/outputFiles/render.mp4",
                SourceVersion = "e1:32",
                State = "Allow",
                ModelSnapshot = "omni-moderation-2024-09-26"
            });
            await db.SaveChangesAsync();
            return 0;
        });

        await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        (await WithDbAsync(provider, db => db.OutputModerationVerdicts.CountAsync())).Should().Be(0);
    }

    private static ApiKey ServiceKey(Guid orgId, Guid serviceUserId, Guid creator) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = orgId,
        CreatedByUserId = creator,
        ServiceUserId = serviceUserId,
        Name = "ci",
        KeyId = Guid.NewGuid().ToString("N")[..12],
        SecretHash = new string('a', 64),
        LastFour = "abcd",
        Scopes = ["runs.read"],
    };

    private static async Task<Guid> AddServiceAccountAsync(IServiceProvider provider, Guid orgId, Guid creator)
    {
        Guid id = Guid.NewGuid();
        await WithDbAsync(provider, async db =>
        {
            db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = id,
                Email = $"apikey+{id:N}@service.invalid",
                DisplayName = "ci",
                IsServiceAccount = true,
                EmailVerifiedAt = DateTime.UtcNow
            });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = orgId, UserId = id, Role = OrganizationRole.Member });
            db.ApiKeys.Add(ServiceKey(orgId, id, creator));
            await db.SaveChangesAsync();
            return 0;
        });
        return id;
    }

    [Fact]
    public async Task Service_accounts_do_not_count_as_members_and_go_with_a_purged_workspace()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        Guid soloOrg = Guid.NewGuid();
        await WithDbAsync(provider, async db =>
        {
            db.Organizations.Add(new Organization { Id = soloOrg, Name = "Solo", Slug = "solo-" + soloOrg.ToString("N")[..6], Kind = OrganizationKind.Team });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = soloOrg, UserId = seeded.UserId, Role = OrganizationRole.Owner });
            await db.SaveChangesAsync();
            return 0;
        });
        Guid inPersonal = await AddServiceAccountAsync(provider, seeded.OrgId, seeded.UserId);
        Guid inSolo = await AddServiceAccountAsync(provider, soloOrg, seeded.UserId);
        Guid inShared = await AddServiceAccountAsync(provider, seeded.TeamOrgId, seeded.UserId);

        AccountPurgeUserResult result = await service.PurgeUserAsync(seeded.UserId, DateTime.UtcNow, CancellationToken.None);

        result.Purged.Should().BeTrue();
        (await WithDbAsync(provider, db => db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == soloOrg))).DeletedAt.Should().NotBeNull();
        (await WithDbAsync(provider, db => db.ApplicationUsers.Where(u => u.Id == inPersonal || u.Id == inSolo).CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ApiKeys.IgnoreQueryFilters().Where(k => k.ServiceUserId == inPersonal || k.ServiceUserId == inSolo).CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.OrganizationMemberships.Where(m => m.UserId == inPersonal || m.UserId == inSolo).CountAsync())).Should().Be(0);
        // The shared team workspace keeps living, and so does its key.
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync(u => u.Id == inShared))).Should().Be(1);
        (await WithDbAsync(provider, db => db.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.ServiceUserId == inShared))).Should().Be(1);
    }

    [Fact]
    public async Task A_run_a_key_started_goes_with_the_purged_workspace_before_its_service_user_and_key()
    {
        // M7/M8: the Go org delete is soft and no longer removes service accounts, so the purge is what removes the
        // service user, its membership, its key and the rows the key created (children before the project).
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        Guid svc = await AddServiceAccountAsync(provider, seeded.OrgId, seeded.UserId);
        Guid keyRowId = await WithDbAsync(provider, db => db.ApiKeys.IgnoreQueryFilters().Where(k => k.ServiceUserId == svc).Select(k => k.Id).SingleAsync());
        Guid wf = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        await WithDbAsync(provider, async db =>
        {
            db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = wf, ProjectId = seeded.ProjectId, Name = "w" });
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = runId,
                ProjectId = seeded.ProjectId,
                WorkflowDefinitionId = wf,
                OrganizationId = seeded.OrgId,
                InitiatedByUserId = svc,
                ApiKeyId = keyRowId,
                Initiator = ExecutionInitiator.Api
            });
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeUserResult result = await service.PurgeUserAsync(seeded.UserId, DateTime.UtcNow, CancellationToken.None);

        result.Purged.Should().BeTrue();
        (await WithDbAsync(provider, db => db.WorkflowExecutions.IgnoreQueryFilters().CountAsync(e => e.Id == runId))).Should().Be(0);
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync(u => u.Id == svc))).Should().Be(0);
        (await WithDbAsync(provider, db => db.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.Id == keyRowId))).Should().Be(0);
        (await WithDbAsync(provider, db => db.OrganizationMemberships.CountAsync(m => m.UserId == svc))).Should().Be(0);
    }

    [Fact]
    public async Task A_workspace_with_only_the_leaving_person_and_service_accounts_is_purged_but_a_second_person_keeps_it()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        // TeamOrgId has another person: a service account beside them must not tip it into "sole member".
        await AddServiceAccountAsync(provider, seeded.TeamOrgId, seeded.UserId);

        await service.PurgeUserAsync(seeded.UserId, DateTime.UtcNow, CancellationToken.None);

        (await WithDbAsync(provider, db => db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == seeded.TeamOrgId))).DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_service_account_is_never_a_purge_subject_even_with_a_deletion_request_or_unverified()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-1));
        Guid svc = await AddServiceAccountAsync(provider, seeded.TeamOrgId, seeded.UserId);
        await WithDbAsync(provider, async db =>
        {
            ApplicationUser u = await db.ApplicationUsers.SingleAsync(x => x.Id == svc);
            u.DeletionRequestedAt = DateTime.UtcNow.AddDays(-400);
            u.EmailVerifiedAt = null;
            u.CreatedAt = DateTime.UtcNow.AddDays(-400);
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeUserResult direct = await service.PurgeUserAsync(svc, DateTime.UtcNow, CancellationToken.None);
        await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        direct.Purged.Should().BeFalse();
        (await WithDbAsync(provider, db => db.ApplicationUsers.CountAsync(u => u.Id == svc))).Should().Be(1);
        (await WithDbAsync(provider, db => db.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.ServiceUserId == svc))).Should().Be(1);
    }

    [Fact]
    public async Task Purge_removes_marketplace_recipes_ratings_reports_and_preview_objects_of_the_account()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        Guid otherUser = Guid.NewGuid();
        Guid otherOrg = Guid.NewGuid();
        Guid ownRecipe = Guid.NewGuid();
        Guid orgRecipe = Guid.NewGuid();
        Guid foreignRecipe = Guid.NewGuid();
        storage.Objects[$"marketplace/{ownRecipe}/preview.mp4"] = [1];
        storage.Objects[$"marketplace/{ownRecipe}/poster.jpg"] = [1];
        storage.Objects[$"marketplace/{orgRecipe}/preview.mp4"] = [1];
        storage.Objects[$"marketplace/{foreignRecipe}/preview.mp4"] = [1];

        await WithDbAsync(provider, async db =>
        {
            MarketplaceRecipe Recipe(Guid id, Guid user, Guid org) => new()
            {
                Id = id,
                AuthorUserId = user,
                AuthorOrgId = org,
                Name = "r",
                RatingSum = 9,
                RatingCount = 2,
                CopyOrgs = 3
            };
            db.MarketplaceRecipes.AddRange(
                Recipe(ownRecipe, seeded.UserId, seeded.OrgId),
                Recipe(orgRecipe, otherUser, seeded.OrgId),
                Recipe(foreignRecipe, otherUser, otherOrg));
            db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = Guid.NewGuid(), RecipeId = ownRecipe, N = 1 });
            db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = ownRecipe, VersionId = Guid.NewGuid(), OrgId = otherOrg });
            db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = foreignRecipe, VersionId = Guid.NewGuid(), OrgId = seeded.OrgId });
            db.MarketplaceRatings.AddRange(
                new MarketplaceRating { Id = Guid.NewGuid(), RecipeId = ownRecipe, UserId = otherUser, Stars = 5 },
                new MarketplaceRating { Id = Guid.NewGuid(), RecipeId = foreignRecipe, UserId = seeded.UserId, Stars = 4 },
                new MarketplaceRating { Id = Guid.NewGuid(), RecipeId = foreignRecipe, UserId = otherUser, Stars = 5 });
            db.MarketplaceReports.AddRange(
                new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = foreignRecipe, ReporterUserId = seeded.UserId, Reason = ReportReason.Spam },
                new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = foreignRecipe, ReporterUserId = otherUser, Reason = ReportReason.Spam });
            db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), RecipeId = ownRecipe, Action = "publish", ActorKind = "author", ActorUserId = seeded.UserId });
            await db.SaveChangesAsync();
            return 0;
        });

        await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        storage.Deleted.Should().Contain($"marketplace/{ownRecipe}/preview.mp4")
            .And.Contain($"marketplace/{ownRecipe}/poster.jpg")
            .And.Contain($"marketplace/{orgRecipe}/preview.mp4");
        storage.Deleted.Should().NotContain($"marketplace/{foreignRecipe}/preview.mp4");

        (await WithDbAsync(provider, db => db.MarketplaceRecipes.Select(r => r.Id).ToListAsync()))
            .Should().Equal(foreignRecipe);
        (await WithDbAsync(provider, db => db.MarketplaceRecipeVersions.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.MarketplaceCopies.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.MarketplaceRatings.Select(r => r.UserId).ToListAsync()))
            .Should().Equal(otherUser);
        (await WithDbAsync(provider, db => db.MarketplaceReports.Select(r => r.ReporterUserId).ToListAsync()))
            .Should().Equal(otherUser);
        MarketplaceRecipe kept = await WithDbAsync(provider, db => db.MarketplaceRecipes.SingleAsync());
        kept.RatingSum.Should().Be(5, "the purged user's 4 stars left the counters");
        kept.RatingCount.Should().Be(1);
        kept.CopyOrgs.Should().Be(2, "the purged organization's copy row went and it left the distinct-copier counter");
        MarketplaceAuditEntry audit = await WithDbAsync(provider, db => db.MarketplaceAudit.SingleAsync());
        audit.ActorUserId.Should().BeNull("the trail stays, without the person");
    }

    [Fact]
    public async Task Purge_removes_the_projects_avatars_before_their_reference_files()
    {
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        await WithDbAsync(provider, async db =>
        {
            ProjectFile file = await db.ProjectFiles.IgnoreQueryFilters().FirstAsync(f => f.ProjectId == seeded.ProjectId);
            ProjectAvatar avatar = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(db, seeded.ProjectId, "Anna", file);
            db.ProjectAvatarVendorAssets.Add(new ProjectAvatarVendorAsset { Id = Guid.NewGuid(), AvatarId = avatar.Id, RemoteAssetId = "mm_file://x" });
            db.ProjectAvatarEvents.Add(new ProjectAvatarEvent
            {
                Id = Guid.NewGuid(),
                AvatarId = avatar.Id,
                ProjectId = seeded.ProjectId,
                OrganizationId = seeded.OrgId,
                Kind = AvatarEventKind.Created,
                ActorUserId = seeded.UserId,
                OutcomesJson = "{\"via\":\"app\"}"
            });
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(1);
        (await WithDbAsync(provider, db => db.ProjectAvatars.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ProjectAvatarReferences.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ProjectAvatarVendorAssets.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ProjectFiles.IgnoreQueryFilters().CountAsync())).Should().Be(0);
        // The audit event is kept for its 3 years (AV-7), without the person.
        ProjectAvatarEvent kept = (await WithDbAsync(provider, db => db.ProjectAvatarEvents.ToListAsync())).Single();
        kept.ActorUserId.Should().BeNull();
        kept.ProjectId.Should().Be(seeded.ProjectId);
    }

    [Fact]
    public async Task Purge_removes_the_workspace_avatar_library_objects_and_rows_of_the_purged_workspace_only()
    {
        // docs/design/avatar-objects.md "The workspace avatar library": the library's objects live under the ORGANIZATION
        // prefix, outside every project prefix, and an import holds its item (NO ACTION), so the imports go first.
        var (service, provider, storage) = Build();
        Seeded seeded = await SeedAsync(provider, storage, DateTime.UtcNow.AddDays(-31));
        Guid itemId = Guid.NewGuid(), referenceId = Guid.NewGuid(), teamItemId = Guid.NewGuid();
        string ownKey = AvatarLibraryStorage.ReferenceKey(seeded.OrgId, itemId, referenceId, "png");
        string ownRelease = AvatarLibraryStorage.ConsentDocumentKey(seeded.OrgId, itemId, "pdf");
        string teamKey = AvatarLibraryStorage.ReferenceKey(seeded.TeamOrgId, teamItemId, Guid.NewGuid(), "png");
        storage.Objects[ownKey] = [1];
        storage.Objects[ownRelease] = [2];
        storage.Objects[teamKey] = [3];
        await WithDbAsync(provider, async db =>
        {
            AvatarLibraryItem Item(Guid id, Guid org) => new()
            {
                Id = id,
                OrganizationId = org,
                Name = "Anna",
                ConsentStatement = "s",
                ConsentStatementVersion = "v",
                ConsentLocale = "en",
                ConsentSubjectName = "Anna",
                ConsentSubjectAdult = true,
                ConsentAttestedByUserId = seeded.UserId,
                ConsentAttestedAt = DateTime.UtcNow,
                RetentionUntil = DateTime.UtcNow.AddDays(30),
                CreatedByUserId = seeded.UserId
            };
            db.AvatarLibraryItems.AddRange(Item(itemId, seeded.OrgId), Item(teamItemId, seeded.TeamOrgId));
            db.AvatarLibraryReferences.Add(new AvatarLibraryReference
            {
                Id = referenceId,
                LibraryItemId = itemId,
                Kind = AvatarReferenceKind.Image,
                Role = AvatarReferenceRole.Face,
                ContentSha256 = new string('a', 64),
                StorageKey = ownKey,
                MimeType = "image/png",
                FileName = "anna.png",
                SizeBytes = 1
            });
            ProjectFile file = await db.ProjectFiles.IgnoreQueryFilters().FirstAsync(f => f.ProjectId == seeded.ProjectId);
            ProjectAvatar import = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(db, seeded.ProjectId, "Anna", file);
            import.LibraryItemId = itemId;
            await db.SaveChangesAsync();
            return 0;
        });

        AccountPurgeRunResult run = await service.RunOnceAsync(DateTime.UtcNow, CancellationToken.None);

        run.UsersPurged.Should().Be(1);
        storage.Deleted.Should().Contain([ownKey, ownRelease]);
        storage.Objects.Keys.Should().Contain(teamKey, "a team workspace that stays keeps its library");
        (await WithDbAsync(provider, db => db.AvatarLibraryItems.IgnoreQueryFilters().Select(i => i.Id).ToListAsync())).Should().Equal(teamItemId);
        (await WithDbAsync(provider, db => db.AvatarLibraryReferences.CountAsync())).Should().Be(0);
        (await WithDbAsync(provider, db => db.ProjectAvatars.CountAsync())).Should().Be(0);
    }
}
