#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Auth;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// brevo-marketing-campaigns QA fixes: the real purge and the real registrations (mutation survivors E2b and E1b),
/// and the real host start with the default compose values. Fake or no Brevo; nothing leaves the process.
/// </summary>
public sealed class MarketingQaRegressionTests : IDisposable
{
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ----- E2b: the real AccountPurgeService stages the protected erasure -----

    private sealed class BreakingProtector : ISecretProtector
    {
        public string Protect(string plaintext) => throw new InvalidOperationException("no key");

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            return false;
        }
    }

    // The purge touches most of the model, so it runs on the in-memory provider like AccountPurgeServiceTests
    // (which ignores transactions; the rollback itself is covered by AccountPurgeTransactionTests).
    private readonly string _purgeDb = Guid.NewGuid().ToString();

    private InferenceApiDbContext PurgeDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(_purgeDb)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)).Options);

    private AccountPurgeService PurgeService(ISecretProtector? protector)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => PurgeDb());
        services.AddSingleton(Mock.Of<IAmazonS3>());
        services.AddSingleton<ITenantContext>(new TenantContext(new HttpContextAccessor()));
        services.AddScoped<IAccountBillingCanceller, AccountBillingCanceller>();
        services.AddSingleton(Mock.Of<IVectorIndexService>());
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<ISpeechSynthesisClientFactory>());
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IMarketingSuppressionHasher>(_h.Suppression);
        if (protector is not null) services.AddSingleton(protector);
        ServiceProvider provider = services.BuildServiceProvider();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MinIO:BucketName"] = "test-bucket",
            ["Tenancy:Mode"] = "Cloud",
        }).Build();
        return new AccountPurgeService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AccountOptions { Purge = new AccountPurgeOptions { Enabled = true, GraceDays = 30, PurgeUnverified = true } }),
            configuration,
            NullLogger<AccountPurgeService>.Instance);
    }

    private Guid SyncedUser(string email)
    {
        Guid user = Guid.NewGuid();
        using InferenceApiDbContext db = PurgeDb();
        db.ApplicationUsers.Add(new ApplicationUser { Id = user, Email = email, DisplayName = "U", EmailVerifiedAt = _h.Now.AddDays(-1), DeletionRequestedAt = _h.Now.AddDays(-40) });
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserId = user,
            Status = MarketingConsentStatus.OptedIn,
            WordingVersion = "2026-10-marketing-v1",
            Source = MarketingConsentSource.SiteContact,
            ConsentedAt = DateTime.UtcNow,
            ConfirmedAt = DateTime.UtcNow,
            RequestedAt = DateTime.UtcNow,
            BrevoSyncedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        return user;
    }

    [Fact]
    public async Task The_real_purge_of_a_synced_consent_stages_a_protected_erasure_row()
    {
        Guid user = SyncedUser("gone@example.test");
        TestProtector protector = new();

        AccountPurgeUserResult result = await PurgeService(protector).PurgeUserAsync(user, _h.Now, CancellationToken.None);

        result.Purged.Should().BeTrue(string.Join("; ", result.Failures));
        await using InferenceApiDbContext check = PurgeDb();
        check.ApplicationUsers.Any(u => u.Id == user).Should().BeFalse();
        check.MarketingConsents.Any(c => c.Email == "gone@example.test").Should().BeFalse();
        MarketingBrevoErasure row = check.MarketingBrevoErasures.AsNoTracking().Should().ContainSingle().Which;
        row.ProtectedEmail.Should().NotContain("gone@example.test");
        protector.TryUnprotect(row.ProtectedEmail, out string plain).Should().BeTrue();
        plain.Should().Be("gone@example.test");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_real_purge_defers_and_keeps_everything_when_the_protector_is_missing_or_fails(bool registeredButFailing)
    {
        Guid user = SyncedUser("gone@example.test");

        AccountPurgeUserResult result = await PurgeService(registeredButFailing ? new BreakingProtector() : null)
            .PurgeUserAsync(user, _h.Now, CancellationToken.None);

        result.Purged.Should().BeFalse("without the erasure row the contact would stay at Brevo, so the purge must defer");
        await using InferenceApiDbContext check = PurgeDb();
        check.ApplicationUsers.Any(u => u.Id == user).Should().BeTrue();
        check.MarketingConsents.Any(c => c.Email == "gone@example.test").Should().BeTrue();
        check.MarketingBrevoErasures.Should().BeEmpty();
    }

    // ----- E1b and the real startup with the default compose values -----

    private static CompositionRootHarness Root(string name, Dictionary<string, string?> extra)
        => CompositionRootHarness.Build(name, (services, configuration) => services.AddInferenceApiServices(configuration), extra);

    [Fact]
    public void The_erasure_drain_resolves_from_the_real_registration_to_the_outbox()
    {
        using CompositionRootHarness root = Root("brevo-drain", new Dictionary<string, string?>());
        using IServiceScope scope = root.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBrevoErasureDrain>().Should().BeOfType<BrevoErasureOutbox>();
        scope.ServiceProvider.GetRequiredService<MarketingCampaignPipeline>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public async Task The_host_starts_with_empty_list_ids_and_the_marketing_workers_do_not_fault(string enabled)
    {
        // Compose passes Marketing__Campaigns__BrevoListId=${BREVO_LIST_ID:-0}; an operator's .env can leave it empty.
        using CompositionRootHarness root = Root("brevo-empty-ids-" + enabled, new Dictionary<string, string?>
        {
            ["Marketing:Campaigns:Enabled"] = enabled,
            ["Marketing:Campaigns:BrevoListId"] = "",
            ["Marketing:Campaigns:BrevoTestListId"] = "",
            ["Marketing:Campaigns:BrevoCampaignApiKey"] = "",
            ["Marketing:Campaigns:ListSendEnabled"] = "false",
        });

        root.Services.GetRequiredService<IStartupValidator>().Validate();
        MarketingCampaignOptions o = root.Services.GetRequiredService<IOptions<MarketingCampaignOptions>>().Value;
        o.BrevoListId.Should().Be(0);
        o.BrevoTestListId.Should().Be(0);

        List<BackgroundService> workers = root.Services.GetServices<IHostedService>().OfType<BackgroundService>()
            .Where(s => s.GetType().Namespace == typeof(MarketingCampaignWorker).Namespace).ToList();
        workers.Select(w => w.GetType()).Should().Contain([typeof(MarketingCampaignWorker), typeof(BrevoErasureWorker), typeof(PendingConsentSweeper)]);
        using CancellationTokenSource stop = new();
        foreach (BackgroundService w in workers)
        {
            await w.StartAsync(stop.Token);
        }

        await Task.Delay(300);
        foreach (BackgroundService w in workers)
        {
            (w.ExecuteTask?.IsFaulted ?? false).Should().BeFalse($"{w.GetType().Name} must not fault on the default configuration");
        }

        await stop.CancelAsync();
        foreach (BackgroundService w in workers)
        {
            await w.StopAsync(CancellationToken.None);
        }

        // The erasure worker's pass reads the options (blank key: inert) without binding errors.
        BrevoErasureWorker erasure = workers.OfType<BrevoErasureWorker>().Single();
        await erasure.PassAsync(CancellationToken.None);
    }
}
