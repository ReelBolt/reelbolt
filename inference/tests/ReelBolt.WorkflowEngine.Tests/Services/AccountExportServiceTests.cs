
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
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
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// C9a acceptance: an export contains every C0 table (one JSON document per section), the user's
/// original files up to the packing cap, and a manifest; the request path is throttled to one export
/// per 24 hours and expired archives are swept.
/// </summary>
public class AccountExportServiceTests
{
    /// <summary>An in-memory stand-in for the object store behind a mocked IAmazonS3.</summary>
    private sealed class Storage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTime> Modified { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = [];
        public string? UploadedKey { get; set; }
    }

    private sealed class RecordingMail : IAccountMailClient
    {
        public List<AccountMail> Sent { get; } = [];
        public bool Accepted { get; set; } = true;

        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            Sent.Add(mail);
            return Task.FromResult(Accepted);
        }
    }

    private static IAmazonS3 BuildS3(Storage storage)
    {
        var mock = new Mock<IAmazonS3>();
        mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                using var buffer = new MemoryStream();
                request.InputStream.CopyTo(buffer);
                storage.Objects[request.Key] = buffer.ToArray();
                storage.Modified[request.Key] = DateTime.UtcNow;
                storage.UploadedKey = request.Key;
            })
            .ReturnsAsync(new PutObjectResponse());

        mock.Setup(s => s.ListObjectsV2Async(It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ListObjectsV2Request request, CancellationToken _) => new ListObjectsV2Response
            {
                S3Objects = storage.Objects.Keys
                    .Where(k => k.StartsWith(request.Prefix, StringComparison.Ordinal))
                    .OrderBy(k => k)
                    .Select(k => new S3Object { Key = k, LastModified = storage.Modified[k] })
                    .ToList(),
                IsTruncated = false
            });

        mock.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetObjectRequest request, CancellationToken _) => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(storage.Objects[request.Key])
            });

        mock.Setup(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string key, CancellationToken _) =>
            {
                if (!storage.Modified.TryGetValue(key, out DateTime at))
                    throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
                return new GetObjectMetadataResponse { LastModified = at };
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

        mock.Setup(s => s.GetPreSignedURLAsync(It.IsAny<GetPreSignedUrlRequest>()))
            .ReturnsAsync("https://store.test/export.zip");
        return mock.Object;
    }

    private static (AccountExportService Service, IServiceProvider Provider, Storage Storage, RecordingMail Mail) Build(
        AccountExportOptions options)
    {
        var storage = new Storage();
        var mail = new RecordingMail();
        string databaseName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        // One database name for every scope, or each scope would get its own empty store.
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddSingleton(BuildS3(storage));
        services.AddKeyedSingleton<IAmazonS3>(S3ClientFactory.PresignServiceKey, BuildS3(new Storage()));
        services.AddSingleton<IAccountMailClient>(mail);
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        IServiceProvider provider = services.BuildServiceProvider();

        var accountOptions = new AccountOptions { Export = options };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["MinIO:BucketName"] = "test-bucket" }).Build();
        var service = new AccountExportService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(accountOptions),
            configuration,
            NullLogger<AccountExportService>.Instance);
        return (service, provider, storage, mail);
    }

    private sealed record Seeded(Guid UserId, Guid OrgId, Guid ProjectId);

    /// <summary>Seeds one user with a personal workspace and one row in every C0 section.</summary>
    private static async Task<Seeded> SeedAsync(IServiceProvider provider, Storage storage)
    {
        Guid userId = Guid.NewGuid();
        Guid orgId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid threadId = Guid.NewGuid();
        Guid planId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();

        string smallKey = "projects/" + projectId + "/userFiles/small.txt";
        string bigKey = "projects/" + projectId + "/outputFiles/render.mp4";
        storage.Objects[smallKey] = "hello"u8.ToArray();
        storage.Modified[smallKey] = DateTime.UtcNow;
        storage.Objects[bigKey] = new byte[4096];
        storage.Modified[bigKey] = DateTime.UtcNow;

        using IServiceScope scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = userId,
            Email = "ada@example.com",
            DisplayName = "Ada",
            PasswordHash = "$2a$10$notarealhash",
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            EmailVerifiedAt = DateTime.UtcNow.AddDays(-10)
        });
        db.Organizations.Add(new Organization
        {
            Id = orgId,
            Name = "Ada workspace",
            Slug = "ada",
            Kind = OrganizationKind.Personal,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10)
        });
        db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = orgId,
            UserId = userId,
            Role = OrganizationRole.Owner,
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        db.LegalAcceptances.Add(new LegalAcceptance
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Document = LegalDocument.Terms,
            Version = "2026-10",
            AcceptedAt = DateTime.UtcNow.AddDays(-10),
            Ip = "203.0.113.9"
        });
        db.OrganizationInvites.Add(new OrganizationInvite
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Email = "ada@example.com",
            Role = OrganizationRole.Member,
            TokenHash = "hash-invite",
            InvitedByUserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-6),
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Launch video",
            Description = "promo",
            OwnerId = userId,
            OrganizationId = orgId,
            CreatedAt = DateTime.UtcNow.AddDays(-9),
            UpdatedAt = DateTime.UtcNow.AddDays(-9)
        });
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = smallKey,
            Category = "userFiles",
            OriginalFileName = "small.txt",
            OriginalPath = "notes/small.txt",
            MimeType = "text/plain",
            SizeBytes = 5,
            StorageBucket = "test-bucket",
            UploadedAt = DateTime.UtcNow.AddDays(-9),
            AgentSummary = "a small note"
        });
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            StorageKey = bigKey,
            Category = "outputFiles",
            OriginalFileName = "render.mp4",
            MimeType = "video/mp4",
            SizeBytes = 4096,
            StorageBucket = "test-bucket",
            UploadedAt = DateTime.UtcNow.AddDays(-9)
        });
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            SourceProjectFileId = Guid.NewGuid(),
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = "projects/" + projectId + "/agentFiles/proxy.mp4",
            StorageBucket = "test-bucket",
            CreatedAt = DateTime.UtcNow.AddDays(-9)
        });
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            ProjectId = projectId,
            Name = "Promo",
            CreatedAt = DateTime.UtcNow.AddDays(-9),
            UpdatedAt = DateTime.UtcNow.AddDays(-9)
        });
        db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = stepId,
            WorkflowDefinitionId = workflowId,
            StepOrder = 1,
            Label = "Script",
            StepType = StepType.Agent,
            ContinueCompositionJson = "{\"executionId\":\"0e5a1c2d-0000-4000-8000-00000000c0de\"}",
            RegisterOutputAsProjectFile = true
        });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            OrganizationId = orgId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddDays(-8),
            CompletedAt = DateTime.UtcNow.AddDays(-8),
            InitiatedByUserId = userId,
            UserRequest = "make me a promo",
            RunBriefJson = "{\"version\":1,\"language\":\"it\"}"
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            WorkflowStepId = stepId,
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddDays(-8),
            Output = "the script",
            OutputStorageKey = bigKey
        });
        db.ReviewScores.Add(new ReviewScore
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = executionId,
            IterationNumber = 1,
            Score = 8,
            Comments = "solid",
            CreatedAt = DateTime.UtcNow.AddDays(-8)
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
            Status = ExternalGenerationJobStatus.Succeeded,
            CostEstimate = 0.5m,
            StorageKey = "JOB-KEY-MARK",
            CreatedAt = DateTime.UtcNow.AddDays(-8)
        });
        db.AssistantThreads.Add(new AssistantThread
        {
            Id = threadId,
            UserId = userId,
            OrganizationId = orgId,
            ProjectId = projectId,
            Title = "help",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            UpdatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = threadId,
            Role = "user",
            Content = "how do I render?",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = "My agent",
            Description = "custom",
            SystemPrompt = "you write scripts",
            AgentType = AgentType.ScriptwriterAgent,
            OwnerId = userId,
            OrganizationId = orgId,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.EditingStyles.Add(new EditingStyle
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            ProjectId = projectId,
            Name = "My style",
            ConfigJson = "{}",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            UpdatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "Narrator",
            Source = ProjectVoiceSource.Library,
            RemoteVoiceId = "voice-1",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Term = "Novara",
            Spelling = "Novàra"
        });
        Guid timelineId = Guid.NewGuid();
        db.EditTimelines.Add(new EditTimelineEntity
        {
            Id = timelineId,
            ProjectId = projectId,
            Name = "v2",
            DocumentJson = "{}",
            Revision = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            UpdatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.EditTimelineOps.Add(new EditTimelineOp
        {
            Id = Guid.NewGuid(),
            TimelineId = timelineId,
            Revision = 1,
            OpsJson = "[]",
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.EditTimelineSnapshots.Add(new EditTimelineSnapshot
        {
            Id = Guid.NewGuid(),
            TimelineId = timelineId,
            Revision = 1,
            DocumentJson = "{}",
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        });
        db.AddFlat(new ModelCapabilityView
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Name = "My Azure",
            Kind = InferenceProviderKind.AzureOpenAI,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = "https://x.openai.azure.com",
            ModelName = "gpt-4o",
            ApiKeyEncrypted = "ENCRYPTED-SECRET",
            ApiKeyLastFour = "abcd",
            CreatedAt = DateTime.UtcNow.AddDays(-7)
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
            OccurredAt = DateTime.UtcNow.AddDays(-8)
        });
        db.CreditAccounts.Add(new CreditAccount
        {
            Id = accountId,
            OrganizationId = orgId,
            BalanceMillis = 900,
            ReservedMillis = 0,
            UpdatedAt = DateTime.UtcNow.AddDays(-8)
        });
        db.CreditLedgerEntries.Add(new CreditLedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            OrganizationId = orgId,
            Kind = CreditLedgerEntryKind.Grant,
            BalanceDeltaMillis = 1000,
            BalanceAfterMillis = 1000,
            Note = "trial",
            CreatedAt = DateTime.UtcNow.AddDays(-9)
        });
        db.Plans.Add(new Plan { Id = planId, Key = "creator", Name = "Creator", Kind = PlanKind.Subscription });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            PlanId = planId,
            Provider = "paddle",
            ProviderSubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            Seats = 1,
            PeriodStart = DateTime.UtcNow.AddDays(-20),
            PeriodEnd = DateTime.UtcNow.AddDays(10),
            CreatedAt = DateTime.UtcNow.AddDays(-20),
            UpdatedAt = DateTime.UtcNow.AddDays(-20)
        });
        db.BillingCustomers.Add(new BillingCustomer
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            Provider = "paddle",
            ProviderCustomerId = "ctm_1",
            Email = "ada@example.com",
            CreatedAt = DateTime.UtcNow.AddDays(-20)
        });
        db.BillingWebhookEvents.Add(new BillingWebhookEvent
        {
            Id = Guid.NewGuid(),
            Provider = "paddle",
            EventId = "evt_1",
            EventType = "subscription.created",
            PayloadJson = "{\"secret\":\"provider payload\"}",
            ReceivedAt = DateTime.UtcNow.AddDays(-20),
            ProcessedAt = DateTime.UtcNow.AddDays(-20)
        });
        Guid consentId = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = consentId,
            Email = "ada@example.com",
            UserId = userId,
            Status = MarketingConsentStatus.OptedIn,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.Signup,
            ConsentedAt = DateTime.UtcNow.AddDays(-10),
            ConfirmedAt = DateTime.UtcNow.AddDays(-10),
            Ip = "203.0.113.5",
            FirstName = "Ada",
            LastName = "Lovelace",
            NameSource = MarketingNameSource.Admin
        });
        db.MarketingConsentEvents.Add(new MarketingConsentEvent
        {
            Id = Guid.NewGuid(),
            ConsentId = consentId,
            Email = "ada@example.com",
            UserId = userId,
            Action = MarketingConsentAction.Granted,
            Source = MarketingConsentSource.Signup,
            WordingVersion = "2026-10-marketing-v1",
            Ip = "203.0.113.5",
            OccurredAt = DateTime.UtcNow.AddDays(-10)
        });
        // Somebody else's consent never appears in Ada's archive.
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = "grace@example.com",
            Status = MarketingConsentStatus.Pending,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.SiteFooter
        });
        await db.SaveChangesAsync();
        return new Seeded(userId, orgId, projectId);
    }

    private static Dictionary<string, string> ReadArchive(byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var reader = new StreamReader(e.Open());
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Build_leaves_out_result_json_and_derivative_keys_for_a_project_with_an_output_marker()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { MaxPackedBytes = 1024 });
        Seeded seeded = await SeedAsync(provider, storage);
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.ProjectId)).CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
            (await db.WorkflowExecutions.IgnoreQueryFilters().SingleAsync()).ResultJson = "{\"video\":\"RESULT-KEY-MARK\"}";
            await db.SaveChangesAsync();
        }

        AccountExportBuildResult result = await service.BuildAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        Dictionary<string, string> entries = ReadArchive(storage.Objects[result.StorageKey]);
        entries["data/workflow-executions.json"].Should().NotContain("RESULT-KEY-MARK");
        entries["data/workflow-executions.json"].Should().Contain("make me a promo", "the run itself is still listed");
        entries["data/media-derivatives.json"].Should().NotContain("proxy.mp4");
    }

    [Fact]
    public async Task Build_leaves_out_the_storage_key_of_an_external_job_whose_run_is_not_served()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { MaxPackedBytes = 1024 });
        Seeded seeded = await SeedAsync(provider, storage);
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == seeded.ProjectId)).CsamOutputHoldAt = DateTime.UtcNow.AddDays(1);
            await db.SaveChangesAsync();
        }

        AccountExportBuildResult result = await service.BuildAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        Dictionary<string, string> entries = ReadArchive(storage.Objects[result.StorageKey]);
        entries["data/external-generation-jobs.json"].Should().NotContain("JOB-KEY-MARK");
        entries["data/external-generation-jobs.json"].Should().Contain("clip-1", "the job itself is still listed");
    }

    [Fact]
    public async Task Build_keeps_the_storage_key_of_an_external_job_in_a_project_without_a_marker()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { MaxPackedBytes = 1024 });
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportBuildResult result = await service.BuildAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        ReadArchive(storage.Objects[result.StorageKey])["data/external-generation-jobs.json"].Should().Contain("JOB-KEY-MARK");
    }

    [Fact]
    public async Task Build_writes_one_document_per_C0_section_the_original_files_and_a_manifest()
    {
        var options = new AccountExportOptions { MaxPackedBytes = 1024 };
        var (service, provider, storage, mail) = Build(options);
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportBuildResult result = await service.BuildAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        result.Sections.Should().Be(AccountExportSections.All.Count);
        result.FilesPacked.Should().Be(1, "the small upload fits under the cap");
        result.FilesSkipped.Should().Be(1, "the 4 KiB render does not fit under a 1 KiB cap");
        result.Mailed.Should().BeTrue();
        result.StorageKey.Should().StartWith("exports/" + seeded.UserId.ToString("N") + "/");

        Dictionary<string, string> entries = ReadArchive(storage.Objects[result.StorageKey]);
        foreach (AccountExportSection section in AccountExportSections.All)
        {
            entries.Should().ContainKey("data/" + section.FileName);
        }

        entries.Should().ContainKey("manifest.json");
        entries.Keys.Should().Contain(k => k.StartsWith("files/", StringComparison.Ordinal));

        entries["data/profile.json"].Should().Contain("ada@example.com");
        entries["data/profile.json"].Should().NotContain("notarealhash", "a password hash is never exported");
        entries["data/inference-providers.json"].Should().NotContain("ENCRYPTED-SECRET");
        entries["data/inference-providers.json"].Should().Contain("My Azure");

        entries["data/projects.json"].Should().Contain("Launch video");
        entries["data/workflow-executions.json"].Should().Contain("make me a promo");
        entries["data/workflow-executions.json"].Should().Contain("language", "the run's brief travels with the run");
        using (JsonDocument steps = JsonDocument.Parse(entries["data/workflow-steps.json"]))
        {
            // Both step options (launch-film T-006) are the user's own configuration: exported verbatim.
            JsonElement step = steps.RootElement.EnumerateArray().Single();
            step.GetProperty("continueCompositionJson").GetString().Should().Contain("0e5a1c2d-0000-4000-8000-00000000c0de");
            step.GetProperty("registerOutputAsProjectFile").GetBoolean().Should().BeTrue();
        }

        entries["data/assistant-messages.json"].Should().Contain("how do I render?");
        entries["data/subscriptions.json"].Should().Contain("paddle");
        entries["data/usage-events.json"].Should().Contain("gpt-4o");
        entries["data/billing-webhook-events.json"].Should().NotContain("provider payload");

        // saas-promotions T-004: the consent row and its events (GDPR 15/20), never someone else's.
        using (JsonDocument consent = JsonDocument.Parse(entries["data/marketing-consent.json"]))
        {
            JsonElement row = consent.RootElement.GetProperty("consent");
            row.GetProperty("status").GetString().Should().Be("optedIn");
            row.GetProperty("wordingVersion").GetString().Should().Be("2026-10-marketing-v1");
            row.GetProperty("ip").GetString().Should().Be("203.0.113.5");
            row.GetProperty("firstName").GetString().Should().Be("Ada");
            row.GetProperty("lastName").GetString().Should().Be("Lovelace");
            row.GetProperty("nameSource").GetString().Should().Be("Admin");
            JsonElement events = consent.RootElement.GetProperty("events");
            events.GetArrayLength().Should().Be(1);
            events[0].GetProperty("action").GetString().Should().Be("granted");
        }

        entries["data/marketing-consent.json"].Should().NotContain("grace@example.com");

        using JsonDocument manifest = JsonDocument.Parse(entries["manifest.json"]);
        manifest.RootElement.GetProperty("sections").GetArrayLength().Should().Be(AccountExportSections.All.Count);
        manifest.RootElement.GetProperty("notExported").GetArrayLength()
            .Should().Be(AccountExportSections.DeliberatelyExcluded.Count);
        manifest.RootElement.GetProperty("files").GetProperty("skipped").GetArrayLength().Should().Be(1);

        mail.Sent.Should().ContainSingle();
        mail.Sent[0].Template.Should().Be("export_ready");
        mail.Sent[0].To.Should().Be("ada@example.com");
        mail.Sent[0].Data.Link.Should().Be("https://store.test/export.zip");
    }

    [Fact]
    public async Task Build_exports_the_welcome_answers_and_the_signup_attribution_of_this_person_only()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { MaxPackedBytes = 1024 });
        Seeded seeded = await SeedAsync(provider, storage);
        Guid visitor = Guid.NewGuid();
        Guid someoneElse = Guid.NewGuid();
        DateTime answered = new(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc);
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.UserOnboardingProfiles.Add(new UserOnboardingProfile
            {
                UserId = seeded.UserId,
                Persona = "educator",
                Material = "footage",
                SuggestedPersona = "creator",
                AnsweredAt = answered,
                CreatedAt = answered.AddMinutes(-5),
                UpdatedAt = answered
            });
            db.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = someoneElse, Persona = "nonprofit", Material = "idea" });
            db.UserAttributions.Add(new ReelBolt.Inference.Api.Services.Telemetry.UserAttribution
            {
                UserId = seeded.UserId,
                SourceLabel = "hackernews",
                UtmSource = "hackernews",
                UtmMedium = "community",
                UtmCampaign = "launch-2026-11",
                UtmContent = "hn-launch",
                LandingPath = "/for/educators",
                VisitorId = visitor,
                FirstTouchAt = new DateTimeOffset(answered.AddHours(-1)),
                CreatedAt = new DateTimeOffset(answered.AddMinutes(-5)),
                Capture = "url"
            });
            db.UserAttributions.Add(new ReelBolt.Inference.Api.Services.Telemetry.UserAttribution
            {
                UserId = someoneElse,
                SourceLabel = "tiktok",
                UtmContent = "tt-bio",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        AccountExportBuildResult result = await service.BuildAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        Dictionary<string, string> entries = ReadArchive(storage.Objects[result.StorageKey]);
        using (JsonDocument document = JsonDocument.Parse(entries["data/onboarding-profile.json"]))
        {
            JsonElement profile = document.RootElement.GetProperty("profile");
            profile.GetProperty("persona").GetString().Should().Be("educator");
            profile.GetProperty("material").GetString().Should().Be("footage");
            profile.GetProperty("suggestedPersona").GetString().Should().Be("creator");
            profile.GetProperty("answeredAt").GetDateTime().Should().Be(answered);
            profile.TryGetProperty("skippedAt", out _).Should().BeFalse("the export omits what never happened");
            profile.TryGetProperty("userId", out _).Should().BeFalse("the archive is already this person's");
        }

        entries["data/onboarding-profile.json"].Should().NotContain("nonprofit", "another person's answers never appear");

        using (JsonDocument document = JsonDocument.Parse(entries["data/signup-attribution.json"]))
        {
            JsonElement attribution = document.RootElement.GetProperty("attribution");
            attribution.GetProperty("sourceLabel").GetString().Should().Be("hackernews");
            attribution.GetProperty("utmSource").GetString().Should().Be("hackernews");
            attribution.GetProperty("utmMedium").GetString().Should().Be("community");
            attribution.GetProperty("utmCampaign").GetString().Should().Be("launch-2026-11");
            attribution.GetProperty("utmContent").GetString().Should().Be("hn-launch");
            attribution.GetProperty("landingPath").GetString().Should().Be("/for/educators");
            attribution.GetProperty("visitorId").GetGuid().Should().Be(visitor);
            attribution.GetProperty("capture").GetString().Should().Be("url");
            attribution.TryGetProperty("utmTerm", out _).Should().BeFalse();
        }

        entries["data/signup-attribution.json"].Should().NotContain("tt-bio").And.NotContain("tiktok");

        using JsonDocument manifest = JsonDocument.Parse(entries["manifest.json"]);
        manifest.RootElement.GetProperty("sections").EnumerateArray().Select(e => e.GetProperty("file").GetString())
            .Should().Contain(["data/onboarding-profile.json", "data/signup-attribution.json"]);
    }

    [Fact]
    public async Task Build_writes_both_funnel_documents_empty_for_a_person_who_was_never_asked_or_attributed()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { MaxPackedBytes = 1024 });
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportBuildResult result = await service.BuildAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        Dictionary<string, string> entries = ReadArchive(storage.Objects[result.StorageKey]);
        foreach ((string file, string member) in new[] { ("data/onboarding-profile.json", "profile"), ("data/signup-attribution.json", "attribution") })
        {
            using JsonDocument document = JsonDocument.Parse(entries[file]);
            // The export omits null properties: no row means no (or a null) member.
            (document.RootElement.TryGetProperty(member, out JsonElement row) ? row.ValueKind : JsonValueKind.Null)
                .Should().Be(JsonValueKind.Null, $"{file} has nothing to say about this person");
        }
    }

    [Fact]
    public async Task S02_an_unverified_account_does_not_export_the_consent_registered_under_its_email()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions());
        Guid mallory = Guid.NewGuid();
        using (IServiceScope scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = mallory,
                Email = "victim@example.com",
                DisplayName = "Mallory",
                CreatedAt = DateTime.UtcNow,
                EmailVerifiedAt = null
            });
            // An anonymous subscriber's confirmed consent: not Mallory's, whatever address she typed.
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = "victim@example.com",
                Status = MarketingConsentStatus.OptedIn,
                WordingVersion = "2026-10-marketing-v1",
                Source = MarketingConsentSource.SiteFooter,
                ConsentedAt = DateTime.UtcNow.AddDays(-3),
                ConfirmedAt = DateTime.UtcNow.AddDays(-3),
                Ip = "198.51.100.77"
            });
            await db.SaveChangesAsync();
        }

        AccountExportBuildResult result = await service.BuildAsync(mallory, "victim@example.com", "Mallory", CancellationToken.None);

        string section = ReadArchive(storage.Objects[result.StorageKey])["data/marketing-consent.json"];
        using JsonDocument consent = JsonDocument.Parse(section);
        // The export omits null properties: no consent means no (or a null) "consent" member.
        (consent.RootElement.TryGetProperty("consent", out JsonElement row) ? row.ValueKind : JsonValueKind.Null)
            .Should().Be(JsonValueKind.Null);
        section.Should().NotContain("198.51.100.77");
    }

    [Fact]
    public async Task Request_is_accepted_and_a_second_one_is_refused_while_it_builds()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions());
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportRequestResult first = await service.RequestExportAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);
        first.Status.Should().Be(AccountExportRequestStatus.Accepted);
        storage.Objects.Should().ContainKey(service.MarkerKey(seeded.UserId),
            "the request is recorded in the object store before the build starts");

        AccountExportRequestResult second = await service.RequestExportAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);
        second.Status.Should().Be(AccountExportRequestStatus.AlreadyRunning,
            "the first export has not been built yet (no worker runs in this test)");
    }

    [Fact]
    public async Task Request_is_throttled_to_one_per_24_hours()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { ThrottleHours = 24 });
        Seeded seeded = await SeedAsync(provider, storage);

        // An hour ago: inside the window.
        storage.Objects[service.MarkerKey(seeded.UserId)] = [];
        storage.Modified[service.MarkerKey(seeded.UserId)] = DateTime.UtcNow.AddHours(-1);

        AccountExportRequestResult tooSoon = await service.RequestExportAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);
        tooSoon.Status.Should().Be(AccountExportRequestStatus.TooSoon);
        tooSoon.RetryAfter.Should().NotBeNull();
        tooSoon.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.FromHours(22));
        tooSoon.RetryAfter!.Value.Should().BeLessThan(TimeSpan.FromHours(24));

        // A day and an hour ago: the window has passed.
        storage.Modified[service.MarkerKey(seeded.UserId)] = DateTime.UtcNow.AddHours(-25);
        AccountExportRequestResult accepted = await service.RequestExportAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);
        accepted.Status.Should().Be(AccountExportRequestStatus.Accepted);
    }

    [Fact]
    public async Task Request_is_disabled_when_the_feature_is_off()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { Enabled = false });
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportRequestResult result = await service.RequestExportAsync(
            seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);

        result.Status.Should().Be(AccountExportRequestStatus.Disabled);
        storage.Objects.Should().NotContainKey(service.MarkerKey(seeded.UserId));
    }

    [Fact]
    public async Task Sweep_deletes_archives_past_the_link_lifetime_and_keeps_fresh_ones()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { LinkTtlDays = 7 });
        Seeded seeded = await SeedAsync(provider, storage);

        string stale = service.UserPrefix(seeded.UserId) + "/" + Guid.NewGuid().ToString("N") + ".zip";
        string fresh = service.UserPrefix(seeded.UserId) + "/" + Guid.NewGuid().ToString("N") + ".zip";
        storage.Objects[stale] = [1, 2, 3];
        storage.Modified[stale] = DateTime.UtcNow.AddDays(-8);
        storage.Objects[fresh] = [1, 2, 3];
        storage.Modified[fresh] = DateTime.UtcNow.AddDays(-1);
        storage.Objects[service.MarkerKey(seeded.UserId)] = [];
        storage.Modified[service.MarkerKey(seeded.UserId)] = DateTime.UtcNow;

        int deleted = await service.SweepExpiredAsync(CancellationToken.None);

        deleted.Should().Be(1);
        storage.Deleted.Should().BeEquivalentTo(new[] { stale });
        storage.Objects.Should().ContainKey(fresh);
        storage.Objects.Should().ContainKey(service.MarkerKey(seeded.UserId));
    }

    [Fact]
    public async Task FindLatest_returns_the_newest_archive()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions());
        Seeded seeded = await SeedAsync(provider, storage);

        string older = service.UserPrefix(seeded.UserId) + "/" + Guid.NewGuid().ToString("N") + ".zip";
        string newer = service.UserPrefix(seeded.UserId) + "/" + Guid.NewGuid().ToString("N") + ".zip";
        storage.Objects[older] = [1];
        storage.Modified[older] = DateTime.UtcNow.AddDays(-2);
        storage.Objects[newer] = [1];
        storage.Modified[newer] = DateTime.UtcNow.AddHours(-1);

        (await service.FindLatestAsync(seeded.UserId, CancellationToken.None)).Should().Be(newer);
        (await service.CreateDownloadLinkAsync(seeded.UserId, TimeSpan.FromMinutes(15), CancellationToken.None))
            .Should().Be("https://store.test/export.zip");
    }

    [Fact]
    public async Task Status_says_preparing_after_a_request_and_ready_once_the_archive_lands()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions { ThrottleHours = 24, LinkTtlDays = 7 });
        Seeded seeded = await SeedAsync(provider, storage);

        AccountExportStatus before = await service.GetStatusAsync(seeded.UserId, CancellationToken.None);
        before.Preparing.Should().BeFalse();
        before.LatestCreatedAt.Should().BeNull();
        before.NextRequestAt.Should().BeNull("nothing was asked for, so nothing is throttled");

        await service.RequestExportAsync(seeded.UserId, "ada@example.com", "Ada", CancellationToken.None);
        AccountExportStatus asked = await service.GetStatusAsync(seeded.UserId, CancellationToken.None);
        asked.Preparing.Should().BeTrue();
        asked.NextRequestAt.Should().BeAfter(DateTime.UtcNow.AddHours(23));

        // The archive lands after the request: it is the newest copy, and nothing is pending.
        string archive = service.UserPrefix(seeded.UserId) + "/" + Guid.NewGuid().ToString("N") + ".zip";
        storage.Objects[archive] = [1, 2, 3];
        storage.Modified[archive] = DateTime.UtcNow.AddSeconds(5);
        storage.Modified[service.MarkerKey(seeded.UserId)] = DateTime.UtcNow.AddMinutes(-1);
        AccountExportStatus ready = await service.GetStatusAsync(seeded.UserId, CancellationToken.None);
        ready.LatestCreatedAt.Should().NotBeNull();
        ready.LatestExpiresAt.Should().BeCloseTo(ready.LatestCreatedAt!.Value.AddDays(7), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Status_stops_saying_preparing_when_a_request_never_produced_an_archive()
    {
        var (service, provider, storage, _) = Build(new AccountExportOptions());
        Seeded seeded = await SeedAsync(provider, storage);

        // A request three hours ago with no archive: the build failed. Saying "preparing" forever
        // would be a lie; the throttle still holds.
        storage.Objects[service.MarkerKey(seeded.UserId)] = [];
        storage.Modified[service.MarkerKey(seeded.UserId)] = DateTime.UtcNow.AddHours(-3);

        AccountExportStatus status = await service.GetStatusAsync(seeded.UserId, CancellationToken.None);
        status.Preparing.Should().BeFalse();
        status.NextRequestAt.Should().NotBeNull();
    }
}
