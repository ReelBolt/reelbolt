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
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Inference.Api.Services.FirstRunReminder;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.FirstRunReminder;

internal sealed class RecordingReminderSender : IFirstRunReminderSender
{
    public List<FirstRunReminderMessage> Sent { get; } = [];

    public string? NotReadyReason { get; set; }

    public Task<FirstRunReminderSendResult> SendAsync(FirstRunReminderMessage message, CancellationToken ct)
    {
        Sent.Add(message);
        return Task.FromResult(new FirstRunReminderSendResult(FirstRunReminderSendOutcome.Sent));
    }
}

public sealed class FirstRunReminderSweeperTests : IDisposable
{
    private readonly MarketingHarness _h = new();
    private readonly RecordingReminderSender _sender = new();
    private readonly FirstRunReminderOptions _opt = new() { Enabled = true, DryRun = false, ConsentMode = FirstRunReminderConsentMode.ServiceMessage, ServiceMessageCounselReviewed = true };

    public FirstRunReminderSweeperTests()
    {
        // workflow_executions is engine-owned (excluded from the API's migrations), so EnsureCreated omits it.
        using InferenceApiDbContext db = _h.NewDb();
        db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS workflow_executions (id TEXT NOT NULL PRIMARY KEY, project_id TEXT NOT NULL)");
    }

    public void Dispose() => _h.Dispose();

    private ReelBolt.Shared.Tenancy.ITenantContext? _tenant;

    private FirstRunReminderSweeper NewSweeper()
    {
        ServiceCollection services = new();
        services.AddScoped(_ => _tenant is null ? _h.NewDb() : _h.NewDb(_tenant));
        services.AddSingleton<IMarketingSuppressionHasher>(_h.Suppression);
        if (_tenant is not null)
        {
            services.AddSingleton(_tenant);
        }

        services.AddSingleton<IFirstRunReminderSender>(_sender);
        services.AddSingleton(Options.Create(new MarketingOptions { PublicBaseUrl = MarketingHarness.BaseUrl }));
        services.AddSingleton(Options.Create(new CloudStorageOptions()));
        services.AddSingleton(Options.Create(new MarketingCampaignOptions { FooterControllerName = "Jane Doe", FooterAddress = "1 Test Street, Turin" }));
        services.AddSingleton<IMarketingLinkTokens>(_h.Tokens);
        ServiceProvider provider = services.BuildServiceProvider();
        return new FirstRunReminderSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_opt), NullLogger<FirstRunReminderSweeper>.Instance, _h.Clock);
    }

    private (Guid user, Guid project) Seed(string email, TimeSpan projectAge, bool verified = true, bool admin = false, bool deleted = false, string? locale = null, bool consent = true)
    {
        using InferenceApiDbContext db = _h.NewDb();
        Guid orgId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = orgId, Name = "o", Slug = "o-" + orgId.ToString("N"), Kind = OrganizationKind.Personal });
        db.ApplicationUsers.Add(new ApplicationUser
        {
            Id = userId,
            Email = email,
            DisplayName = "U",
            IsAdmin = admin,
            Locale = locale,
            EmailVerifiedAt = verified ? _h.Now.AddDays(-1) : null,
            DeletionRequestedAt = deleted ? _h.Now.AddMinutes(-5) : null,
        });
        Guid projectId = AddProject(db, userId, orgId, _h.Now - projectAge);
        if (consent)
        {
            db.MarketingConsents.Add(new MarketingConsent
            {
                Id = Guid.NewGuid(),
                Email = email,
                UserId = userId,
                Status = MarketingConsentStatus.OptedIn,
                ConfirmedAt = _h.Now.AddDays(-1),
                WordingVersion = "v",
                Source = MarketingConsentSource.Settings,
            });
        }

        db.SaveChanges();
        return (userId, projectId);
    }

    private static Guid AddProject(InferenceApiDbContext db, Guid userId, Guid orgId, DateTime createdAt)
    {
        Guid id = Guid.NewGuid();
        db.Projects.Add(new Project { Id = id, Name = "p", OwnerId = userId, OrganizationId = orgId, CreatedAt = createdAt, UpdatedAt = createdAt });
        return id;
    }

    private void AddExecution(Guid projectId)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.Database.ExecuteSqlRaw("INSERT INTO workflow_executions (id, project_id) VALUES ({0}, {1})", Guid.NewGuid().ToString().ToUpperInvariant(), projectId.ToString().ToUpperInvariant());
    }

    private void SetConsent(string email, MarketingConsentStatus status)
    {
        using InferenceApiDbContext db = _h.NewDb();
        db.MarketingConsents.Add(new MarketingConsent { Id = Guid.NewGuid(), Email = email, Status = status, ConfirmedAt = _h.Now.AddDays(-1), WordingVersion = "v", Source = MarketingConsentSource.Settings });
        db.SaveChanges();
    }

    private Task<FirstRunReminderPassResult> Pass() => NewSweeper().SweepOnceAsync(_h.Now, CancellationToken.None);

    private int Rows()
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.FirstRunReminders.Count();
    }

    [Fact]
    public async Task Sends_once_for_a_first_project_in_the_window_and_never_again()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90), locale: "it-IT");

        FirstRunReminderPassResult first = await Pass();
        FirstRunReminderPassResult second = await Pass();

        first.Sent.Should().Be(1);
        second.Sent.Should().Be(0);
        _sender.Sent.Should().HaveCount(1);
        _sender.Sent[0].Subject.Should().Contain("primo montaggio");
        _sender.Sent[0].Html.Should().Contain("Jane Doe").And.NotContain("<img");
        _sender.Sent[0].RcptRef.Should().StartWith("rcpt=");
        Rows().Should().Be(1);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(150)]
    public async Task Skips_a_project_outside_the_window(int minutes)
    {
        Seed("a@example.com", TimeSpan.FromMinutes(minutes));
        (await Pass()).Sent.Should().Be(0);
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_a_user_who_already_ran_something()
    {
        (_, Guid project) = Seed("a@example.com", TimeSpan.FromMinutes(90));
        AddExecution(project);
        (await Pass()).Sent.Should().Be(0);
    }

    [Fact]
    public async Task Skips_a_user_whose_older_project_means_this_is_not_the_first()
    {
        (Guid user, _) = Seed("a@example.com", TimeSpan.FromMinutes(90));
        using (InferenceApiDbContext db = _h.NewDb())
        {
            Guid org = db.Projects.IgnoreQueryFilters().Single(p => p.OwnerId == user).OrganizationId;
            AddProject(db, user, org, _h.Now.AddDays(-3));
            db.SaveChanges();
        }

        (await Pass()).Sent.Should().Be(0);
    }

    [Fact]
    public async Task Skips_already_reminded_unverified_admin_deleted_and_excluded_accounts()
    {
        (Guid reminded, _) = Seed("reminded@example.com", TimeSpan.FromMinutes(90));
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.FirstRunReminders.Add(new FirstRunReminderRecord { UserId = reminded, ClaimedAt = _h.Now.AddDays(-5), State = FirstRunReminderStates.Sent });
            db.SaveChanges();
        }

        Seed("unverified@example.com", TimeSpan.FromMinutes(90), verified: false);
        Seed("admin@example.com", TimeSpan.FromMinutes(90), admin: true);
        Seed("deleted@example.com", TimeSpan.FromMinutes(90), deleted: true);
        Seed("qa@internal.test", TimeSpan.FromMinutes(90));
        _opt.ExcludedEmailDomains.Add("internal.test");

        (await Pass()).Sent.Should().Be(0);
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Skips_a_platform_organization_member()
    {
        (Guid user, _) = Seed("ops@example.com", TimeSpan.FromMinutes(90));
        using (InferenceApiDbContext db = _h.NewDb())
        {
            Guid org = Guid.NewGuid();
            db.Organizations.Add(new Organization { Id = org, Name = "plat", Slug = "plat", Kind = OrganizationKind.Platform });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = org, UserId = user, Role = OrganizationRole.Owner });
            db.SaveChanges();
        }

        (await Pass()).Sent.Should().Be(0);
    }

    [Fact]
    public async Task Marketing_opt_in_mode_needs_an_opted_in_row_and_withdrawal_blocks_both_modes()
    {
        Seed("none@example.com", TimeSpan.FromMinutes(90), consent: false);
        Seed("in@example.com", TimeSpan.FromMinutes(95), consent: false);
        Seed("out@example.com", TimeSpan.FromMinutes(100), consent: false);
        SetConsent("in@example.com", MarketingConsentStatus.OptedIn);
        SetConsent("out@example.com", MarketingConsentStatus.Withdrawn);

        _opt.ConsentMode = FirstRunReminderConsentMode.RequireMarketingOptIn;
        (await Pass()).Sent.Should().Be(1);
        _sender.Sent.Should().ContainSingle().Which.UnsubscribeUrl.Should().Contain("/app/unsubscribe?token=u1.");

        // Service reading: still nobody without a consent row (no opt-out path), and the withdrawn one never.
        _opt.ConsentMode = FirstRunReminderConsentMode.ServiceMessage;
        (await Pass()).Sent.Should().Be(0);
        _sender.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Flag_off_does_zero_work()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _opt.Enabled = false;

        FirstRunReminderPassResult r = await Pass();

        r.Ran.Should().BeFalse();
        r.Examined.Should().Be(0);
        _sender.Sent.Should().BeEmpty();
        Rows().Should().Be(0);
    }

    [Fact]
    public async Task Dry_run_counts_but_sends_nothing_and_claims_nothing()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _opt.DryRun = true;

        FirstRunReminderPassResult r = await Pass();

        r.WouldSend.Should().Be(1);
        r.Sent.Should().Be(0);
        _sender.Sent.Should().BeEmpty();
        Rows().Should().Be(0);
    }

    [Fact]
    public async Task Daily_cap_stops_the_pass()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        Seed("b@example.com", TimeSpan.FromMinutes(91));
        Seed("c@example.com", TimeSpan.FromMinutes(92));
        _opt.DailyCap = 2;

        FirstRunReminderPassResult r = await Pass();
        FirstRunReminderPassResult again = await Pass();

        r.Sent.Should().Be(2);
        r.CapReached.Should().BeTrue();
        again.Sent.Should().Be(0);
        _sender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task Quiet_hours_send_nothing()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _opt.QuietHoursTimeZone = "UTC";
        _opt.QuietHoursStartHour = 8;
        _opt.QuietHoursEndHour = 10;

        FirstRunReminderPassResult r = await Pass();

        r.QuietHours.Should().BeTrue();
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_footer_identity_fails_closed()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        ServiceCollection services = new();
        services.AddScoped(_ => _h.NewDb());
        services.AddSingleton<IFirstRunReminderSender>(_sender);
        services.AddSingleton(Options.Create(new MarketingOptions { PublicBaseUrl = MarketingHarness.BaseUrl }));
        services.AddSingleton(Options.Create(new CloudStorageOptions()));
        services.AddSingleton(Options.Create(new MarketingCampaignOptions()));
        using ServiceProvider provider = services.BuildServiceProvider();
        FirstRunReminderSweeper sweeper = new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(_opt), NullLogger<FirstRunReminderSweeper>.Instance, _h.Clock);

        FirstRunReminderPassResult r = await sweeper.SweepOnceAsync(_h.Now, CancellationToken.None);

        r.IdentityMissing.Should().BeTrue();
        _sender.Sent.Should().BeEmpty();
        Rows().Should().Be(0);
    }

    [Fact]
    public async Task Defaults_are_off_and_dry_run_and_the_no_op_sender_sends_nothing()
    {
        FirstRunReminderOptions d = new();
        d.Enabled.Should().BeFalse();
        d.DryRun.Should().BeTrue();
        d.ConsentMode.Should().Be(FirstRunReminderConsentMode.RequireMarketingOptIn);
        FirstRunReminderSendResult r = await new NoOpFirstRunReminderSender().SendAsync(
            FirstRunReminderMail.Build("a@b.co", "rcpt=x", "en", "https://x/y", new("N", null, "A"), null), CancellationToken.None);
        r.Outcome.Should().Be(FirstRunReminderSendOutcome.NotSent);
    }

    [Fact]
    public async Task Finds_candidates_under_a_non_system_tenant_context_because_the_pass_opens_a_system_scope()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _tenant = new ReelBolt.Inference.Api.Services.Auth.TenantContext(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _tenant.IsSystem.Should().BeFalse();

        FirstRunReminderPassResult r = await Pass();

        r.Examined.Should().Be(1);
        r.Sent.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_sender_key_claims_nothing()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _sender.NotReadyReason = "brevo_key_missing";

        FirstRunReminderPassResult r = await Pass();

        r.SenderNotReady.Should().Be("brevo_key_missing");
        r.Claimed.Should().Be(0);
        Rows().Should().Be(0);
        _sender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_honors_the_daily_cap()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        Seed("b@example.com", TimeSpan.FromMinutes(91));
        Seed("c@example.com", TimeSpan.FromMinutes(92));
        _opt.DryRun = true;
        _opt.DailyCap = 2;

        FirstRunReminderPassResult r = await Pass();

        r.WouldSend.Should().Be(2);
        r.CapReached.Should().BeTrue();
    }

    [Fact]
    public async Task Opt_in_mode_applies_the_campaign_sendable_rule()
    {
        _opt.ConsentMode = FirstRunReminderConsentMode.RequireMarketingOptIn;
        Seed("ok@example.com", TimeSpan.FromMinutes(90));
        Seed("unconfirmed@example.com", TimeSpan.FromMinutes(91), consent: false);
        Seed("suppressed@example.com", TimeSpan.FromMinutes(92));
        using (InferenceApiDbContext db = _h.NewDb())
        {
            db.MarketingConsents.Add(new MarketingConsent { Id = Guid.NewGuid(), Email = "unconfirmed@example.com", Status = MarketingConsentStatus.OptedIn, WordingVersion = "v", Source = MarketingConsentSource.Settings });
            db.MarketingSuppressions.Add(new MarketingSuppression { Id = Guid.NewGuid(), EmailHmac = _h.Suppression.Hash("suppressed@example.com") });
            db.SaveChanges();
        }

        FirstRunReminderPassResult r = await Pass();

        r.Sent.Should().Be(1);
        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Service_mode_sends_with_an_opt_out_link_to_a_pending_row_and_skips_an_address_with_no_row()
    {
        Seed("norow@example.com", TimeSpan.FromMinutes(90), consent: false);
        Seed("pending@example.com", TimeSpan.FromMinutes(91), consent: false);
        SetConsent("pending@example.com", MarketingConsentStatus.Pending);

        FirstRunReminderPassResult r = await Pass();

        r.Sent.Should().Be(1);
        _sender.Sent.Should().ContainSingle().Which.UnsubscribeUrl.Should().Contain("/app/unsubscribe?token=u1.");
    }

    [Fact]
    public async Task Ineligible_owners_ahead_in_the_window_do_not_starve_an_eligible_one()
    {
        // 230 admins fill more than one page of the window ahead of the one eligible user.
        for (int i = 0; i < 230; i++)
        {
            Seed($"admin{i}@example.com", TimeSpan.FromMinutes(119) - TimeSpan.FromSeconds(i), admin: true, consent: false);
        }

        Seed("late@example.com", TimeSpan.FromMinutes(61));

        FirstRunReminderPassResult r = await Pass();

        r.Sent.Should().Be(1);
        _sender.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Service_message_mode_does_nothing_without_the_counsel_acknowledgement()
    {
        Seed("a@example.com", TimeSpan.FromMinutes(90));
        _opt.ServiceMessageCounselReviewed = false;

        FirstRunReminderPassResult r = await Pass();

        r.SenderNotReady.Should().Be("service_message_not_approved");
        _sender.Sent.Should().BeEmpty();
        Rows().Should().Be(0);
    }

    [Fact]
    public void Mail_has_en_and_it_text_a_link_and_no_tracking()
    {
        FirstRunReminderIdentity id = new("Jane Doe", "IT123", "1 Test Street");
        FirstRunReminderMessage en = FirstRunReminderMail.Build("a@b.co", "rcpt=x", "de", "https://x/app/projects/1/workflows", id, "https://x/u");
        FirstRunReminderMessage it = FirstRunReminderMail.Build("a@b.co", "rcpt=x", "it", "https://x/app/projects/1/workflows", id, null);

        en.Text.Should().Contain("first cut").And.Contain("https://x/app/projects/1/workflows").And.Contain("https://x/u");
        it.Text.Should().Contain("primo montaggio").And.NotContain("https://x/u");
        en.Html.Should().NotContain("<img").And.NotContain("pixel");
    }
}
