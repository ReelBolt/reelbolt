#nullable enable
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// brevo-contact-sync T-001 hardening: names are cleared on every withdrawal path with no Brevo, campaign or sync switch
/// involved, the sweeper clears them too, a refused form name is silent, and a Pending form-named row is never pushed.
/// </summary>
public sealed class MarketingNameWithdrawalTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private static readonly MarketingRequestEvidence Evidence = new("203.0.113.7", "TestAgent/1.0");
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private Guid Add(string email, MarketingConsentStatus status = MarketingConsentStatus.OptedIn, Guid? userId = null)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid id = Guid.NewGuid();
        db.MarketingConsents.Add(new MarketingConsent
        {
            Id = id,
            Email = email,
            UserId = userId,
            Status = status,
            WordingVersion = V1,
            Source = MarketingConsentSource.SiteFooter,
            ConsentedAt = _h.Now,
            ConfirmedAt = status == MarketingConsentStatus.OptedIn ? _h.Now : null,
            RequestedAt = _h.Now,
            FirstName = "Maria",
            LastName = "Rossi",
            NameSource = MarketingNameSource.Form,
            NameUpdatedAt = _h.Now.AddDays(-1),
        });
        db.SaveChanges();
        return id;
    }

    private MarketingConsent Get(Guid id)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Id == id);
    }

    private void AssertCleared(Guid id)
    {
        MarketingConsent row = Get(id);
        row.Status.Should().Be(MarketingConsentStatus.Withdrawn);
        row.FirstName.Should().BeNull();
        row.LastName.Should().BeNull();
        row.NameSource.Should().Be(MarketingNameSource.Form, "the source is kept");
        row.NameUpdatedAt.Should().Be(_h.Now);
    }

    [Fact]
    public async Task Opt_out_clears_the_name()
    {
        Guid user = _h.AddUser("o@example.com", _h.Now);
        Guid id = Add("o@example.com", userId: user);
        await using InferenceApiDbContext db = _h.NewDb();
        await _h.NewService(db).SetAsync(user, "o@example.com", false, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None);
        AssertCleared(id);
    }

    [Fact]
    public async Task Unsubscribe_link_clears_the_name()
    {
        Guid id = Add("u@example.com");
        string token = _h.Tokens.CreateUnsubscribeToken(id);
        await using InferenceApiDbContext db = _h.NewDb();
        (await _h.NewService(db).UnsubscribeAsync(token, Evidence, CancellationToken.None)).Should().BeTrue();
        AssertCleared(id);
    }

    [Theory]
    [InlineData(MarketingConsentSource.BrevoUnsubscribe)]
    [InlineData(MarketingConsentSource.EmailComplaint)]
    [InlineData(MarketingConsentSource.BrevoBlocklist)]
    public async Task Provider_withdrawal_clears_the_name(MarketingConsentSource source)
    {
        Guid id = Add("p@example.com");
        await using InferenceApiDbContext db = _h.NewDb();
        (await _h.NewService(db).WithdrawFromProviderAsync("p@example.com", source, CancellationToken.None)).Should().BeTrue();
        AssertCleared(id);
    }

    [Fact]
    public async Task Re_opt_in_does_not_resurrect_a_name()
    {
        Guid user = _h.AddUser("r@example.com", _h.Now);
        Guid id = Add("r@example.com", userId: user);
        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).SetAsync(user, "r@example.com", false, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None);
        }

        await using (InferenceApiDbContext db = _h.NewDb())
        {
            await _h.NewService(db).SetAsync(user, "r@example.com", true, MarketingConsentSource.Settings, V1, Evidence, CancellationToken.None);
        }

        Get(id).FirstName.Should().BeNull();
    }

    [Fact]
    public async Task The_sweeper_clears_withdrawn_names_without_any_brevo_setting()
    {
        Guid id = Add("s@example.com", MarketingConsentStatus.Withdrawn);
        ServiceCollection services = new();
        services.AddScoped(_ => _h.NewDb());
        await using ServiceProvider provider = services.BuildServiceProvider();
        PendingConsentSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_h.Options), NullLogger<PendingConsentSweeper>.Instance);
        await sweeper.SweepOnceAsync(_h.Clock.GetUtcNow().UtcDateTime, CancellationToken.None);
        Get(id).FirstName.Should().BeNull();
        Get(id).LastName.Should().BeNull();
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("http://x")]
    [InlineData("a@b")]
    [InlineData("J.R.")]
    public async Task A_refused_form_name_is_silent_and_not_stored(string name)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingSubscribeOutcome outcome = await _h.NewService(db).SubscribeAsync(
            "n@example.com", V1, MarketingConsentSource.SiteFooter, Evidence, CancellationToken.None, null, name);
        outcome.Should().Be(MarketingSubscribeOutcome.Accepted);
        using InferenceApiDbContext check = _h.NewDb();
        check.MarketingConsents.Single(c => c.Email == "n@example.com").FirstName.Should().BeNull();
    }

    [Fact]
    public async Task A_pending_form_named_row_is_never_pushed_to_brevo()
    {
        Add("pending@example.com", MarketingConsentStatus.Pending);
        FakeBrevo brevo = new();
        MarketingCampaignOptions opt = new()
        {
            BrevoCampaignApiKey = "secret-key",
            BrevoApiBase = "https://brevo.example.test/v3",
            BrevoListId = 10,
            BrevoMinCallIntervalMs = 0,
        };
        var options = Options.Create(opt);
        await using InferenceApiDbContext db = _h.NewDb();
        BrevoCampaignClient client = new(new HttpClient(brevo), options, MailLogRef.Ephemeral(), new RecordingLogger<BrevoCampaignClient>());
        MarketingConsentService consents = _h.NewService(db);
        BrevoContactGuard guard = new(db, consents, client, options);
        BrevoErasureOutbox outbox = new(db, consents, guard, client, new TestProtector(), options, new RecordingLogger<BrevoErasureOutbox>(), _h.Clock);
        ContactSyncService sync = new(
            db, new MarketingNameReconciler(db, _h.Clock), guard, outbox, consents, client, options,
            Options.Create(new MarketingContactSyncOptions { Enabled = true }), new ContactSyncSchedule(),
            new RecordingLogger<ContactSyncService>(), _h.Clock);

        _h.Clock.Advance(TimeSpan.FromSeconds(10));
        ContactSyncResult r = await sync.PassAsync(CancellationToken.None, false);

        r.Pushed.Should().Be(0);
        brevo.Calls.Where(c => c.Method == "POST" && c.Path == "contacts").Should().BeEmpty();
        (await consents.SendableRecipientsAsync(CancellationToken.None)).Recipients.Should().BeEmpty();
    }
}
