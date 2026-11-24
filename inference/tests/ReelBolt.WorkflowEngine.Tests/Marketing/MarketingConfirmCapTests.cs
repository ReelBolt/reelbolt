#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// canonical-mailbox limit keys, the persistent
/// <c>mail_rate_counters</c> caps on the anonymous confirmation mail, and the trusted site
/// forward of the client IP.
/// </summary>
public sealed class MarketingConfirmCapTests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private const string SiteToken = "site-forward-token-0123456789";
    private readonly MarketingHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class Anonymous : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
        public string Email => string.Empty;
        public bool IsAuthenticated => false;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => false;
    }

    private async Task<MarketingSubscribeOutcome> Subscribe(string email, string ip)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        return await _h.NewService(db).SubscribeAsync(
            email, V1, MarketingConsentSource.SiteFooter, new MarketingRequestEvidence(ip, "TestAgent/1.0"), CancellationToken.None);
    }

    /// <summary>A subscribe through the controller action, with the given request headers.</summary>
    private async Task<int?> PostSubscribe(string email, IDictionary<string, string> headers)
    {
        await using InferenceApiDbContext db = _h.NewDb();
        MarketingConsentController controller = new(_h.NewService(db), _h.Wording, new Anonymous());
        DefaultHttpContext http = new();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        foreach ((string name, string value) in headers)
        {
            http.Request.Headers[name] = value;
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        IActionResult result = await controller.Subscribe(
            new MarketingSubscribeRequest(email, V1, "siteFooter"), CancellationToken.None);
        return (result as IStatusCodeActionResult)?.StatusCode;
    }

    private string? StoredIp(string email)
    {
        using InferenceApiDbContext db = _h.NewDb();
        return db.MarketingConsents.AsNoTracking().Single(c => c.Email == email).Ip;
    }

    // --- C1: canonical mailbox -----------------------------------------------------------------

    [Theory]
    [InlineData("Victim@Gmail.com", "victim@gmail.com")]
    [InlineData(" v.i.c.t.i.m+news@googlemail.com ", "victim@gmail.com")]
    [InlineData("victim+1@gmail.com", "victim@gmail.com")]
    [InlineData("a.b+x@example.com", "a.b@example.com")]
    [InlineData("a+b+c@Example.COM", "a@example.com")]
    [InlineData("+tag@example.com", "+tag@example.com")]
    [InlineData("no-at-sign", "no-at-sign")]
    [InlineData("\"q@x\"@example.com", "\"q@x\"@example.com")]
    public void CanonicalMailbox_matches_the_shared_C1_vectors(string input, string canonical)
    {
        MarketingEmail.CanonicalMailbox(input).Should().Be(canonical);
    }

    [Fact]
    public void Storage_normalization_stays_trim_and_lowercase()
    {
        MarketingEmail.Normalize(" V.ictim+News@GoogleMail.com ").Should().Be("v.ictim+news@googlemail.com");
    }

    // --- Mailbox cap ---------------------------------------------------------------------------

    [Fact]
    public async Task Five_plus_dot_and_googlemail_variants_of_one_gmail_inbox_get_exactly_one_mail()
    {
        string[] variants =
        [
            "victim@gmail.com",
            "v.ictim@gmail.com",
            "victim+promo@gmail.com",
            "Victim@googlemail.com",
            "v.i.c.t.i.m+news@googlemail.com",
        ];

        foreach (string variant in variants)
        {
            (await Subscribe(variant, "203.0.113.7")).Should().Be(MarketingSubscribeOutcome.Accepted);
        }

        _h.Outbox.Sent.Should().ContainSingle("every variant reaches the same inbox");
        _h.Outbox.Sent[0].To.Should().Be("victim@gmail.com", "the mail still goes to the address given");
    }

    [Fact]
    public async Task A_restarted_service_over_the_same_counters_does_not_reset_the_mailbox_cap()
    {
        await Subscribe("victim@gmail.com", "203.0.113.7");
        _h.Outbox.Sent.Should().ContainSingle();

        // A new process: new DbContext, new service, nothing in memory; only the shared table stays.
        await Subscribe("vic.tim+again@gmail.com", "198.51.100.20");

        _h.Outbox.Sent.Should().ContainSingle("the canonical mailbox already had its mail this window");
    }

    [Fact]
    public async Task A_restarted_service_over_the_same_counters_does_not_reset_the_ip_cap()
    {
        for (int i = 0; i < 5; i++)
        {
            await Subscribe($"first{i}@example.com", "203.0.113.7");
        }

        _h.Outbox.Sent.Should().HaveCount(5);
        await Subscribe("after-restart@example.com", "203.0.113.7");
        _h.Outbox.Sent.Should().HaveCount(5);
    }

    // --- IP cap --------------------------------------------------------------------------------

    [Fact]
    public async Task A_burst_of_ten_addresses_from_one_ip_gets_five_mails_and_ten_202s()
    {
        List<int?> statuses = [];
        for (int i = 0; i < 10; i++)
        {
            statuses.Add(await PostSubscribe($"burst{i}@example.com", new Dictionary<string, string> { ["X-Real-IP"] = "203.0.113.50" }));
        }

        statuses.Should().AllSatisfy(s => s.Should().Be(StatusCodes.Status202Accepted));
        _h.Outbox.Sent.Should().HaveCount(5);
    }

    [Fact]
    public async Task The_ip_cap_slides_and_reopens_once_the_previous_hour_has_weighed_out()
    {
        // T-024: sliding, not fixed, windows. An hour later the previous hour still weighs 100%.
        for (int i = 0; i < 6; i++)
        {
            await Subscribe($"hour{i}@example.com", "203.0.113.7");
        }

        _h.Outbox.Sent.Should().HaveCount(5);
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await Subscribe("next-hour@example.com", "203.0.113.7");
        _h.Outbox.Sent.Should().HaveCount(5);
        _h.Clock.Advance(TimeSpan.FromHours(1));
        await Subscribe("two-hours-on@example.com", "203.0.113.7");
        _h.Outbox.Sent.Should().HaveCount(6);
    }

    // --- Domain cap ----------------------------------------------------------------------------

    [Fact]
    public async Task Direct_calls_get_at_most_thirty_mails_an_hour_per_domain_from_any_number_of_ips()
    {
        for (int i = 0; i < 35; i++)
        {
            await Subscribe($"user{i}@victim-corp.example", $"198.51.100.{i + 1}");
        }

        _h.Outbox.Sent.Should().HaveCount(30);
        await Subscribe("other@elsewhere.example", "198.51.100.200");
        _h.Outbox.Sent.Should().HaveCount(31, "another domain is not affected");
    }

    // --- Global cap ----------------------------------------------------------------------------

    [Fact]
    public async Task The_global_cap_stops_mail_and_logs_one_warning_per_window()
    {
        _h.Options.ConfirmGlobalPerDay = 3;
        for (int i = 0; i < 6; i++)
        {
            (await Subscribe($"g{i}@domain{i}.example", $"192.0.2.{i + 1}")).Should().Be(MarketingSubscribeOutcome.Accepted);
        }

        _h.Outbox.Sent.Should().HaveCount(3);
        _h.Logger.Entries
            .Where(e => e.EventId.Name == "MarketingConfirmGlobalCapReached")
            .Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning);
        _h.Logger.Entries.Should().NotContain(e => e.Message.Contains("@"), "no log line carries an address");
    }

    [Fact]
    public async Task Every_refusal_increments_the_cap_hit_counter_tagged_with_the_cap()
    {
        List<string> caps = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.marketing.confirm_cap_hit")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "cap" && tag.Value is string cap)
                {
                    lock (caps)
                    {
                        caps.Add(cap);
                    }
                }
            }
        });
        listener.Start();

        // Unique values, so measurements from tests running in parallel cannot match.
        string marker = Guid.NewGuid().ToString("N")[..8];
        await Subscribe($"cap{marker}@gmail.com", "203.0.113.90");
        await Subscribe($"cap{marker}+x@gmail.com", "203.0.113.90");

        lock (caps)
        {
            caps.Should().Contain("mailbox");
        }
    }

    [Fact]
    public void Non_positive_cap_settings_fall_back_to_the_defaults()
    {
        MarketingOptions options = new()
        {
            ConfirmPerIpPerHour = 0,
            ConfirmPerDomainPerHour = -1,
            ConfirmGlobalPerDay = 0,
        };

        options.EffectiveConfirmPerIpPerHour.Should().Be(5);
        options.EffectiveConfirmPerDomainPerHour.Should().Be(30);
        options.EffectiveConfirmGlobalPerDay.Should().Be(200);
        new MarketingOptions { ConfirmGlobalPerDay = 50 }.EffectiveConfirmGlobalPerDay.Should().Be(50);
    }

    // --- Store failure -------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_counter_store_sends_no_mail_and_still_answers_202()
    {
        _h.Counters.Fail = true;

        int? status = await PostSubscribe("fail-closed@example.com", new Dictionary<string, string> { ["X-Real-IP"] = "203.0.113.8" });

        status.Should().Be(StatusCodes.Status202Accepted);
        _h.Outbox.Sent.Should().BeEmpty();
        _h.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        _h.Logger.Entries.Should().NotContain(e => e.Message.Contains("fail-closed"), "the address is never logged");
    }

    // --- C3: trusted site forward --------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    [InlineData("")]
    public async Task A_forged_client_ip_header_without_the_right_token_is_ignored(string? token)
    {
        _h.Options.SiteForwardToken = SiteToken;
        for (int i = 0; i < 8; i++)
        {
            Dictionary<string, string> headers = new()
            {
                ["X-Real-IP"] = "203.0.113.66",
                ["X-ReelBolt-Client-IP"] = $"192.0.2.{i + 1}",
            };
            if (token is not null)
            {
                headers["X-ReelBolt-Site-Token"] = token;
            }

            (await PostSubscribe($"forged{i}@example.com", headers)).Should().Be(StatusCodes.Status202Accepted);
        }

        _h.Outbox.Sent.Should().HaveCount(5, "the cap is keyed by X-Real-IP, which the forger cannot rotate");
        StoredIp("forged0@example.com").Should().Be("203.0.113.66");
    }

    [Fact]
    public async Task The_client_ip_header_is_ignored_when_no_site_token_is_configured()
    {
        _h.Options.SiteForwardToken = "";
        await PostSubscribe("unconfigured@example.com", new Dictionary<string, string>
        {
            ["X-Real-IP"] = "203.0.113.67",
            ["X-ReelBolt-Client-IP"] = "192.0.2.9",
            ["X-ReelBolt-Site-Token"] = "",
        });

        StoredIp("unconfigured@example.com").Should().Be("203.0.113.67");
    }

    [Fact]
    public async Task With_the_right_token_the_forwarded_client_ip_keys_the_cap_and_is_recorded()
    {
        _h.Options.SiteForwardToken = SiteToken;
        Dictionary<string, string> Site(string clientIp) => new()
        {
            ["X-Real-IP"] = "10.9.9.9", // the site server, the same for every visitor
            ["X-ReelBolt-Client-IP"] = clientIp,
            ["X-ReelBolt-Site-Token"] = SiteToken,
        };

        // Seven visitors through the one site server: each is its own IP, so none is capped.
        for (int i = 0; i < 7; i++)
        {
            await PostSubscribe($"visitor{i}@example.com", Site($"198.51.100.{i + 1}"));
        }

        _h.Outbox.Sent.Should().HaveCount(7);
        StoredIp("visitor3@example.com").Should().Be("198.51.100.4");

        // One visitor submitting many addresses is capped on the forwarded IP.
        for (int i = 0; i < 6; i++)
        {
            await PostSubscribe($"same{i}@example.com", Site("192.0.2.200"));
        }

        _h.Outbox.Sent.Should().HaveCount(7 + 5);
    }

    // --- Sweeper -------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweeper_deletes_counter_rows_older_than_two_days()
    {
        DateTime now = _h.Now;
        await using (InferenceApiDbContext seed = _h.NewDb())
        {
            seed.MailRateCounters.AddRange(
                new MailRateCounter { Bucket = "confirm:global", WindowStart = now.AddDays(-3), Count = 9, UpdatedAt = now.AddDays(-3) },
                new MailRateCounter { Bucket = "relay:global", WindowStart = now.AddDays(-2).AddHours(-1), Count = 4, UpdatedAt = now.AddDays(-2) },
                new MailRateCounter { Bucket = "confirm:global", WindowStart = now.AddDays(-1), Count = 2, UpdatedAt = now.AddDays(-1) },
                new MailRateCounter { Bucket = "confirm:domain:example.com", WindowStart = now, Count = 1, UpdatedAt = now });
            await seed.SaveChangesAsync();
        }

        ServiceCollection services = new();
        services.AddScoped(_ => _h.NewDb());
        await using ServiceProvider provider = services.BuildServiceProvider();
        PendingConsentSweeper sweeper = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(_h.Options),
            NullLogger<PendingConsentSweeper>.Instance);

        await sweeper.SweepOnceAsync(now, CancellationToken.None);

        await using InferenceApiDbContext db = _h.NewDb();
        db.MailRateCounters.AsNoTracking().Select(c => c.Count).ToList().Should().BeEquivalentTo([2, 1]);
    }
}
