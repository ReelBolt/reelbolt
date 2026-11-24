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
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing;

/// <summary>
/// the round-3 confirmation caps. Sliding
/// windows, refused attempts increment nothing, IPv6 keyed on the /64, and the domain and
/// global caps as alarm thresholds that still let a bot-checked site forward through.
/// </summary>
public sealed class MarketingCapRound3Tests : IDisposable
{
    private const string V1 = "2026-10-marketing-v1";
    private const string SiteToken = "site-forward-token-0123456789";
    private readonly MarketingHarness _h = new();

    public MarketingCapRound3Tests() => _h.Options.SiteForwardToken = SiteToken;

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

    /// <summary>A direct anonymous subscribe from <paramref name="ip"/> (nginx's X-Real-IP), no site token.</summary>
    private Task<int?> Direct(string email, string ip) =>
        Post(email, new Dictionary<string, string> { ["X-Real-IP"] = ip });

    /// <summary>A subscribe the marketing site forwarded after its bot check.</summary>
    private Task<int?> SiteForward(string email, string visitorIp) => Post(email, new Dictionary<string, string>
    {
        ["X-Real-IP"] = "10.9.9.9",
        ["X-ReelBolt-Client-IP"] = visitorIp,
        ["X-ReelBolt-Site-Token"] = SiteToken,
    });

    private async Task<int?> Post(string email, IDictionary<string, string> headers)
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
        IActionResult result = await controller.Subscribe(new MarketingSubscribeRequest(email, V1, "siteFooter"), CancellationToken.None);
        return (result as IStatusCodeActionResult)?.StatusCode;
    }

    private void At(int hour, int minute, int second = 0, int dayOffset = 0) =>
        _h.Clock.Now = new DateTimeOffset(2026, 10, 12, hour, minute, second, TimeSpan.Zero).AddDays(dayOffset);

    // --- C5: sliding windows hold a window-edge burst to about one window's worth ---------------

    [Fact]
    public async Task A_burst_of_N_just_before_an_ip_window_edge_and_N_just_after_gets_about_N_not_2N()
    {
        At(9, 59, 58);
        for (int i = 0; i < 5; i++)
        {
            (await Direct($"before{i}@d{i}.example", "203.0.113.70")).Should().Be(StatusCodes.Status202Accepted);
        }

        At(10, 0, 2);
        for (int i = 0; i < 5; i++)
        {
            (await Direct($"after{i}@e{i}.example", "203.0.113.70")).Should().Be(StatusCodes.Status202Accepted);
        }

        _h.Outbox.Sent.Should().HaveCount(5, "the previous hour still weighs ~100% four seconds after the edge");
    }

    [Fact]
    public async Task The_previous_window_weighs_less_as_the_current_one_elapses()
    {
        At(9, 0);
        for (int i = 0; i < 5; i++)
        {
            await Direct($"early{i}@d{i}.example", "203.0.113.71");
        }

        _h.Outbox.Sent.Should().HaveCount(5);

        // 10:30: estimate = 0 + 5 × 0.5 = 2.5, so two more fit under 5 (2.5 + 1 ≤ 5, 3.5 + 1 ≤ 5, 4.5 + 1 > 5).
        At(10, 30);
        for (int i = 0; i < 5; i++)
        {
            await Direct($"half{i}@e{i}.example", "203.0.113.71");
        }

        _h.Outbox.Sent.Should().HaveCount(7);
    }

    // --- C5: refused attempts increment nothing, so they cannot extend a lockout ----------------

    [Fact]
    public async Task An_attackers_refused_attempts_do_not_extend_a_mailbox_lockout()
    {
        // The attacker spends the victim's three mails for the day (one per cooldown) ...
        string[] variants = ["victim@gmail.com", "v.ictim@gmail.com", "victim+a@gmail.com"];
        for (int i = 0; i < variants.Length; i++)
        {
            At(9, 20 * i);
            await Direct(variants[i], $"198.51.100.{i + 1}");
        }

        _h.Outbox.Sent.Should().HaveCount(3, "cooldown 10 min, at most 3 per day per canonical mailbox");

        // ... then keeps hammering it from fresh IPs for the rest of the day, every attempt refused.
        for (int i = 0; i < 56; i++)
        {
            _h.Clock.Now = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero).AddMinutes(15 * i);
            await Direct($"victim+spam{i}@gmail.com", $"198.51.100.{i + 10}");
        }

        _h.Outbox.Sent.Should().HaveCount(3);

        // Next day at noon the sliding estimate is 0 + 3 × 0.5 = 1.5: the genuine request gets its mail.
        // Had the 56 refusals been counted, the estimate would be (3 + 56) × 0.5 and the lockout would hold.
        At(12, 0, dayOffset: 1);
        await Direct("victim@gmail.com", "203.0.113.99");

        _h.Outbox.Sent.Should().HaveCount(4);
        _h.Outbox.Sent[^1].To.Should().Be("victim@gmail.com");
    }

    [Fact]
    public async Task The_mailbox_cooldown_is_ten_minutes_not_a_day()
    {
        At(9, 0);
        await Direct("cool@example.com", "203.0.113.80");
        At(9, 5);
        await Direct("cool+x@example.com", "203.0.113.81");
        _h.Outbox.Sent.Should().HaveCount(1, "within the cooldown");

        At(9, 25);
        await Direct("cool+y@example.com", "203.0.113.82");
        _h.Outbox.Sent.Should().HaveCount(2, "the cooldown is over well before 24 h");
    }

    [Fact]
    public async Task A_refused_send_does_not_spend_the_literal_addresss_cooldown()
    {
        _h.Options.ConfirmPerIpPerHour = 1;
        At(9, 0);
        await Direct("first@example.com", "203.0.113.83");
        await Direct("second@example.com", "203.0.113.83"); // refused by the IP cap
        _h.Outbox.Sent.Should().HaveCount(1);

        // From another IP, a minute later: the refused attempt claimed nothing on the row.
        At(9, 1);
        await Direct("second@example.com", "203.0.113.84");
        _h.Outbox.Sent.Should().HaveCount(2);
    }

    // --- N-04: IPv6 keyed on the /64 -----------------------------------------------------------

    [Fact]
    public async Task Rotating_addresses_inside_one_ipv6_64_share_one_ip_bucket()
    {
        for (int i = 0; i < 8; i++)
        {
            await Direct($"six{i}@d{i}.example", $"2001:db8:1:2::{i + 1:x}");
        }

        _h.Outbox.Sent.Should().HaveCount(5, "one /64 is one IP for the cap");

        await Direct("other64@x.example", "2001:db8:1:3::1");
        _h.Outbox.Sent.Should().HaveCount(6, "another /64 has its own bucket");
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2:ffff:ffff:ffff:ffff")]
    [InlineData("2001:DB8:1:2::1", "2001:db8:1:2:0:0:0:9")]
    [InlineData("203.0.113.5", "::ffff:203.0.113.5")]
    public void Ip_buckets_key_ipv6_on_the_64_and_ipv4_on_the_32(string a, string b)
    {
        MailRateBuckets.ConfirmIp(a).Should().Be(MailRateBuckets.ConfirmIp(b));
    }

    [Theory]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:3::1")]
    [InlineData("203.0.113.5", "203.0.113.6")]
    public void Different_64s_and_different_ipv4s_get_different_buckets(string a, string b)
    {
        MailRateBuckets.ConfirmIp(a).Should().NotBe(MailRateBuckets.ConfirmIp(b));
    }

    // --- C6: domain and global caps are alarm thresholds, not a lockout -------------------------

    [Fact]
    public async Task Junk_beyond_the_domain_alarm_does_not_stop_a_site_forwarded_subscribe()
    {
        for (int i = 0; i < 40; i++)
        {
            (await Direct($"junk{i}@victim-corp.example", $"198.51.100.{i + 1}")).Should().Be(StatusCodes.Status202Accepted);
        }

        _h.Outbox.Sent.Should().HaveCount(30, "above the domain alarm, direct anonymous calls get no mail");

        (await SiteForward("real.person@victim-corp.example", "192.0.2.10")).Should().Be(StatusCodes.Status202Accepted);
        _h.Outbox.Sent.Should().HaveCount(31, "the bot-checked site forward still gets its mail");
        _h.Outbox.Sent[^1].To.Should().Be("real.person@victim-corp.example");

        (await Direct("late-direct@victim-corp.example", "198.51.100.200")).Should().Be(StatusCodes.Status202Accepted);
        _h.Outbox.Sent.Should().HaveCount(31);
    }

    [Fact]
    public async Task Junk_beyond_the_global_alarm_does_not_stop_a_site_forwarded_subscribe()
    {
        _h.Options.ConfirmGlobalPerDay = 3;
        for (int i = 0; i < 6; i++)
        {
            await Direct($"g{i}@domain{i}.example", $"192.0.2.{i + 1}");
        }

        _h.Outbox.Sent.Should().HaveCount(3);

        await SiteForward("genuine@elsewhere.example", "198.51.100.77");
        _h.Outbox.Sent.Should().HaveCount(4);
    }

    // --- Logs: no address in any log line on the marketing path ---------------------------

    [Fact]
    public async Task A_counter_store_failure_whose_message_carries_an_address_logs_no_at_sign()
    {
        _h.Counters.Fail = true;
        _h.Counters.FailMessage = "duplicate key: victim@example.com <victim@example.com> token=abc";

        (await Direct("victim@example.com", "203.0.113.9")).Should().Be(StatusCodes.Status202Accepted);

        _h.Outbox.Sent.Should().BeEmpty();
        _h.Logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        _h.Logger.Entries.Should().AllSatisfy(e => e.Message.Should().NotContain("@").And.NotContain("token=abc"));
    }

    // --- C6: the hard ceiling refuses everyone, with one alert per window -----------------------

    [Fact]
    public async Task The_global_hard_ceiling_refuses_even_site_forwards_and_alerts_once()
    {
        _h.Options.ConfirmGlobalPerDay = 2;
        _h.Options.ConfirmGlobalHardCeilingPerDay = 4;
        for (int i = 0; i < 2; i++)
        {
            await Direct($"d{i}@d{i}.example", $"192.0.2.{i + 1}");
        }

        for (int i = 0; i < 5; i++)
        {
            await SiteForward($"s{i}@s{i}.example", $"198.51.100.{i + 1}");
        }

        _h.Outbox.Sent.Should().HaveCount(4, "2 under the alarm, then 2 bot-checked up to the ceiling, then none");
        _h.Logger.Entries.Where(e => e.EventId == MarketingConsentService.GlobalHardCeilingReached)
            .Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public void Non_positive_round_3_settings_fall_back_to_the_defaults()
    {
        MarketingOptions options = new()
        {
            ConfirmMailboxCooldownMinutes = 0,
            ConfirmMailboxPerDay = -1,
            ConfirmGlobalHardCeilingPerDay = 0,
        };

        options.EffectiveConfirmMailboxCooldownMinutes.Should().Be(10);
        options.EffectiveConfirmMailboxPerDay.Should().Be(3);
        options.EffectiveConfirmGlobalHardCeilingPerDay.Should().Be(1000);
    }

    // --- C6: alarm warnings and meters ---------------------------------------------------------

    [Fact]
    public async Task The_domain_alarm_warns_once_per_window_without_an_address()
    {
        for (int i = 0; i < 40; i++)
        {
            await Direct($"w{i}@alarmed.example", $"198.51.100.{i + 1}");
        }

        _h.Logger.Entries.Where(e => e.EventId == MarketingConsentService.DomainAlarmReached)
            .Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);

        // The next hour's window warns again (its own marker), still once.
        _h.Clock.Advance(TimeSpan.FromHours(1));
        for (int i = 0; i < 5; i++)
        {
            await Direct($"x{i}@alarmed.example", $"198.51.101.{i + 1}");
        }

        _h.Logger.Entries.Where(e => e.EventId == MarketingConsentService.DomainAlarmReached).Should().HaveCount(2);
        _h.Logger.Entries.Should().AllSatisfy(e => e.Message.Should().NotContain("@"));
    }

    [Fact]
    public async Task The_alarm_meter_counts_refused_direct_calls_and_sent_site_forwards()
    {
        string domain = "m" + Guid.NewGuid().ToString("N")[..8] + ".example";
        List<(string Alarm, string Outcome)> seen = [];
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "reelbolt.marketing.confirm_alarm")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? alarm = null, outcome = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "alarm") alarm = tag.Value as string;
                if (tag.Key == "outcome") outcome = tag.Value as string;
            }

            lock (seen)
            {
                seen.Add((alarm!, outcome!));
            }
        });
        listener.Start();

        _h.Options.ConfirmPerDomainPerHour = 1;
        await Direct($"a@{domain}", "203.0.113.31");
        await Direct($"b@{domain}", "203.0.113.32");
        await SiteForward($"c@{domain}", "203.0.113.33");

        lock (seen)
        {
            seen.Should().Contain(("domain", "refused")).And.Contain(("domain", "sent"));
        }
    }

    // --- N-05: one startup warning when the site forward token is unset -------------------------

    [Theory]
    [InlineData(null, 1)]
    [InlineData("  ", 1)]
    [InlineData(SiteToken, 0)]
    public async Task An_unset_site_forward_token_logs_one_startup_warning(string? token, int warnings)
    {
        RecordingLogger<MarketingStartupChecks> logger = new();
        MarketingStartupChecks checks = new(
            Microsoft.Extensions.Options.Options.Create(new MarketingOptions { SiteForwardToken = token }),
            MailLogRef.ForKey("k"),
            logger);

        await checks.StartAsync(CancellationToken.None);

        logger.Entries.Where(e => e.EventId == MarketingStartupChecks.SiteForwardTokenMissing && e.Level == LogLevel.Warning)
            .Should().HaveCount(warnings);
    }

    [Fact]
    public async Task An_unset_log_ref_key_logs_one_startup_warning()
    {
        RecordingLogger<MarketingStartupChecks> logger = new();
        MarketingStartupChecks checks = new(
            Microsoft.Extensions.Options.Options.Create(new MarketingOptions { SiteForwardToken = SiteToken }),
            new MailLogRef(Microsoft.Extensions.Options.Options.Create(new MailOptions())),
            logger);

        await checks.StartAsync(CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.EventId == MarketingStartupChecks.LogRefKeyMissing);
    }

    // --- C4: the keyed recipient reference -----------------------------------------------------

    [Theory]
    [InlineData("test-log-key", "Victim+x@Gmail.com", "rcpt=aa31e47adc42")]
    [InlineData("other-key", "Victim+x@Gmail.com", "rcpt=c5ed0310f4ca")]
    [InlineData("test-log-key", "v.i.c.t.i.m@googlemail.com", "rcpt=aa31e47adc42")]
    public void The_log_ref_matches_the_shared_C4_vectors(string key, string address, string expected)
    {
        MailLogRef.ForKey(key).For(address).Should().Be(expected);
    }

    [Fact]
    public void A_configured_key_is_used_and_an_unset_one_is_random_per_process()
    {
        new MailLogRef(Microsoft.Extensions.Options.Options.Create(new MailOptions { LogRefKey = "test-log-key" }))
            .For("victim@gmail.com").Should().Be("rcpt=aa31e47adc42");

        MailLogRef a = new(Microsoft.Extensions.Options.Options.Create(new MailOptions()));
        MailLogRef b = new(Microsoft.Extensions.Options.Options.Create(new MailOptions { LogRefKey = "" }));
        a.KeyConfigured.Should().BeFalse();
        a.For("victim@gmail.com").Should().NotBe(b.For("victim@gmail.com")).And.NotBe("rcpt=aa31e47adc42");
    }

    [Theory]
    [InlineData("550 5.1.1 <victim@example.com>: user unknown", "550 5.1.1 [address] user unknown")]
    [InlineData("rcpt victim@example.com refused", "rcpt [address] refused")]
    [InlineData("GET https://x.test/app/marketing/confirm?token=abc.def&x=1 failed", "GET https://x.test/app/marketing/confirm?token=[redacted]&x=1 failed")]
    public void Redact_removes_addresses_and_tokens(string input, string expected)
    {
        MailLogRef.Redact(input).Should().Be(expected);
    }
}
