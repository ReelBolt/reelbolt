#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// F11 step 6: the periodic spend report off the B ledger and the alert it raises when a threshold is
/// crossed. The two properties worth a test are that the window is exactly the window (a report that
/// counted a neighbouring day's events would alert on a spend nobody made) and that the alert path is
/// best effort — a webhook that is down must not take the report down with it.
/// </summary>
public class DailySpendReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    [Fact]
    public void The_report_is_off_by_default()
    {
        // Self-host has no platform budget to watch; the shipped default must not start logging
        // reports (or POSTing webhooks) in an install nobody asked.
        new SpendReportOptions().Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_window_sums_only_what_falls_inside_it_and_ranks_the_organizations()
    {
        using ServiceProvider provider = Build(
            Event(OrgA, 3m, hoursAgo: 2),
            Event(OrgA, 1m, hoursAgo: 20),
            Event(OrgB, 5m, hoursAgo: 3),
            // Outside the window on both sides: neither may appear in any figure.
            Event(OrgB, 100m, hoursAgo: 30),
            Event(OrgB, 100m, hoursAgo: -1));

        DailySpendReportService service = Service(provider, new SpendReportOptions());

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.From.Should().Be(Now.UtcDateTime.AddHours(-24));
        window.To.Should().Be(Now.UtcDateTime);
        window.TotalCostUsd.Should().Be(9m);
        window.TotalEvents.Should().Be(3);
        window.TopOrganizations.Select(o => o.OrganizationId).Should().Equal(OrgB, OrgA);
        window.TopOrganizations[0].CostUsd.Should().Be(5m);
    }

    [Fact]
    public async Task Unattributed_events_are_counted_separately()
    {
        // An event with no organization is a metering bug; folding it into the total is how it stops
        // being noticed.
        using ServiceProvider provider = Build(
            Event(OrgA, 2m, hoursAgo: 1),
            Event(Guid.Empty, 4m, hoursAgo: 1, source: UsageSource.Unattributed));

        DailySpendReportService service = Service(provider, new SpendReportOptions());

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.UnattributedEvents.Should().Be(1);
        window.TotalEvents.Should().Be(2, "an unattributed event is still a spend we incurred");
    }

    [Fact]
    public async Task An_empty_window_reports_zero_rather_than_failing()
    {
        using ServiceProvider provider = Build();
        DailySpendReportService service = Service(provider, new SpendReportOptions());

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.TotalCostUsd.Should().Be(0m);
        window.TopOrganizations.Should().BeEmpty();
    }

    [Fact]
    public async Task Crossing_the_platform_threshold_raises_one_alert()
    {
        var webhook = new RecordingHandler();
        using ServiceProvider provider = Build(Event(OrgA, 30m, hoursAgo: 1), Event(OrgB, 30m, hoursAgo: 1));
        DailySpendReportService service = Service(
            provider,
            new SpendReportOptions { DailyThresholdUsd = 50m, AlertWebhookUrl = "https://alerts.internal/spend" },
            webhook);

        await service.ReportOnceAsync(Now);

        webhook.Requests.Should().Be(1);
        List<SpendReportAlert>? posted = JsonSerializer.Deserialize<List<SpendReportAlert>>(
            webhook.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        posted.Should().ContainSingle();
        posted![0].Scope.Should().Be("platform");
        posted[0].CostUsd.Should().Be(60m);
        posted[0].ThresholdUsd.Should().Be(50m);
    }

    [Fact]
    public async Task Staying_under_the_threshold_alerts_nobody()
    {
        var webhook = new RecordingHandler();
        using ServiceProvider provider = Build(Event(OrgA, 10m, hoursAgo: 1));
        DailySpendReportService service = Service(
            provider, new SpendReportOptions { DailyThresholdUsd = 50m, AlertWebhookUrl = "https://alerts.internal/spend" }, webhook);

        await service.ReportOnceAsync(Now);

        webhook.Requests.Should().Be(0);
    }

    [Fact]
    public async Task The_per_organisation_threshold_names_the_organisation_that_crossed_it()
    {
        var webhook = new RecordingHandler();
        using ServiceProvider provider = Build(Event(OrgA, 40m, hoursAgo: 1), Event(OrgB, 1m, hoursAgo: 1));
        DailySpendReportService service = Service(
            provider,
            new SpendReportOptions { OrganizationThresholdUsd = 10m, AlertWebhookUrl = "https://alerts.internal/spend" },
            webhook);

        await service.ReportOnceAsync(Now);

        List<SpendReportAlert>? posted = JsonSerializer.Deserialize<List<SpendReportAlert>>(
            webhook.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        posted.Should().ContainSingle();
        posted![0].Scope.Should().Be("organization");
        posted[0].OrganizationId.Should().Be(OrgA);
    }

    [Fact]
    public async Task A_webhook_that_is_down_does_not_take_the_report_down()
    {
        // The alert is an observation. An alerts endpoint that is itself down must not stop the
        // report being produced — that is the only way the operator ever finds out.
        var webhook = new RecordingHandler { Throw = true };
        using ServiceProvider provider = Build(Event(OrgA, 60m, hoursAgo: 1));
        DailySpendReportService service = Service(
            provider,
            new SpendReportOptions { DailyThresholdUsd = 1m, AlertWebhookUrl = "https://alerts.internal/spend" },
            webhook);

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.TotalCostUsd.Should().Be(60m);
    }

    [Fact]
    public async Task Every_enabled_platform_capability_with_no_price_in_force_is_an_error_section()
    {
        // a coverage gap is money. The report lists every one, at Error, beside the unpriced usage it caused.
        using ServiceProvider provider = Build(
            Unpriced(Event(OrgA, 0m, hoursAgo: 1)),
            Event(OrgA, 1m, hoursAgo: 1));
        ModelCapabilityView unpriced = Capability("Mystery chat");
        ModelCapabilityView priced = Capability("Priced chat");
        ModelCapabilityView disabled = Capability("Disabled chat", enabled: false);
        ModelCapabilityView byo = Capability("Org chat", organizationId: OrgA);
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.AddFlat(unpriced, priced, disabled, byo);
            foreach (UsageKind unit in new[] { UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens })
            {
                db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = priced.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.000001m,
                    EffectiveFrom = Now.UtcDateTime.AddDays(-1)
                });
            }

            db.SaveChanges();
        }

        CapturingLogger log = new();
        SpendReportWindow window = await Service(provider, new SpendReportOptions(), logger: log).ReportOnceAsync(Now);

        window.PriceGaps.Select(g => g.CapabilityId).Should().Equal(unpriced.Id);
        window.UnpricedEvents.Should().Be(1);
        log.Errors.Should().ContainSingle().Which.Should().Contain("Mystery chat");
    }

    [Fact]
    public async Task A_fully_priced_catalog_reports_no_gap_and_logs_no_error()
    {
        using ServiceProvider provider = Build(Event(OrgA, 1m, hoursAgo: 1));
        CapturingLogger log = new();

        SpendReportWindow window = await Service(provider, new SpendReportOptions(), logger: log).ReportOnceAsync(Now);

        window.PriceGaps.Should().BeEmpty();
        log.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Report_WebhookLine_AndAlertWhenStuck()
    {
        var webhook = new RecordingHandler();
        var logger = new CapturingLogger();
        using ServiceProvider provider = Build(db =>
        {
            db.BillingWebhookEvents.AddRange(
                Webhook("evt_1", hoursAgo: 1, "Applied"),
                Webhook("evt_2", hoursAgo: 5, "Duplicate"),
                Webhook("evt_3", hoursAgo: 6, "Unresolved"),
                Webhook("reconcile:sub:sub_1:1", hoursAgo: 2, "Applied"),
                Webhook("reconcile:txn:txn_1", hoursAgo: 2, "Duplicate"),
                // Outside the window: counted nowhere.
                Webhook("evt_old", hoursAgo: 30, "Unresolved"));
            db.BillingReconcileWatches.AddRange(
                new BillingReconcileWatch { OrganizationId = OrgA, ChangeKind = "change_plan", ChangeRequestedAt = Now.UtcDateTime.AddHours(-1) },
                // Younger than the stuck threshold: its webhook may still come.
                new BillingReconcileWatch { OrganizationId = OrgB, ChangeKind = "seats", ChangeRequestedAt = Now.UtcDateTime.AddMinutes(-2) },
                new BillingReconcileWatch { OrganizationId = Guid.NewGuid(), ChangeKind = "overdue", ConsecutiveFailures = 2 });
        });
        DailySpendReportService service = Service(
            provider, new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/spend" }, webhook, logger);

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.BillingWebhooks.Should().Be(new BillingWebhookReport(Received: 3, Unresolved: 1, Reconciled: 1, Stuck: 1, StuckAfterMinutes: 10, Failing: 1));
        logger.Messages.Should().Contain(
            "Billing webhooks over 24h: 3 received, 1 unresolved, 1 reconciled, 1 waiting past 10 min, 1 failing to reconcile; 0 signature failures, 0 processing failures since the last report (this instance)");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("billing_webhooks"));

        webhook.Requests.Should().Be(1);
        List<SpendReportAlert>? posted = JsonSerializer.Deserialize<List<SpendReportAlert>>(
            webhook.Bodies[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        SpendReportAlert alert = posted.Should().ContainSingle().Subject;
        alert.Scope.Should().Be("billing_webhooks");
        alert.OrganizationId.Should().BeNull();
        alert.Detail.Should().Contain("1 waiting past 10 min");
    }

    [Fact]
    public async Task Report_NoAlertWhenClean()
    {
        var webhook = new RecordingHandler();
        var logger = new CapturingLogger();
        using ServiceProvider provider = Build(db =>
        {
            db.BillingWebhookEvents.AddRange(Webhook("evt_1", hoursAgo: 1, "Applied"), Webhook("evt_2", hoursAgo: 3, "Stale"));
            db.BillingReconcileWatches.Add(new BillingReconcileWatch { OrganizationId = OrgA, ChangeKind = "overdue", LastOutcome = "InSync" });
        });
        DailySpendReportService service = Service(
            provider, new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/spend" }, webhook, logger);

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.BillingWebhooks.Should().Be(new BillingWebhookReport(2, 0, 0, 0, 10, 0));
        logger.Messages.Should().Contain(
            "Billing webhooks over 24h: 2 received, 0 unresolved, 0 reconciled, 0 waiting past 10 min, 0 failing to reconcile; 0 signature failures, 0 processing failures since the last report (this instance)");
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        webhook.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Report_AbandonedCheckout_NoAlert()
    {
        var webhook = new RecordingHandler();
        var logger = new CapturingLogger();
        using ServiceProvider provider = Build(db =>
        {
            db.BillingWebhookEvents.Add(Webhook("evt_1", hoursAgo: 1, "Applied"));
            // Rechecked, and Paddle has no live subscription: the member walked away from the checkout.
            db.BillingReconcileWatches.Add(new BillingReconcileWatch
            {
                OrganizationId = OrgA,
                ChangeKind = "checkout",
                ChangeRequestedAt = Now.UtcDateTime.AddHours(-3),
                LastCheckedAt = Now.UtcDateTime.AddMinutes(-20),
                LastOutcome = "NothingToReconcile"
            });
            // Not checked yet: still counts.
            db.BillingReconcileWatches.Add(new BillingReconcileWatch
            {
                OrganizationId = OrgB,
                ChangeKind = "checkout",
                ChangeRequestedAt = Now.UtcDateTime.AddHours(-1)
            });
        });
        DailySpendReportService service = Service(
            provider, new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/spend" }, webhook, logger);

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.BillingWebhooks!.Stuck.Should().Be(1, "only the checkout never checked counts");

        using ServiceProvider abandonedOnly = Build(db => db.BillingReconcileWatches.Add(new BillingReconcileWatch
        {
            OrganizationId = OrgA,
            ChangeKind = "checkout",
            ChangeRequestedAt = Now.UtcDateTime.AddHours(-3),
            LastCheckedAt = Now.UtcDateTime.AddMinutes(-20),
            LastOutcome = "NothingToReconcile"
        }));
        var quietHook = new RecordingHandler();
        var quietLog = new CapturingLogger();
        SpendReportWindow quiet = await Service(
            abandonedOnly, new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/spend" }, quietHook, quietLog)
            .ReportOnceAsync(Now);

        quiet.BillingWebhooks.Should().Be(new BillingWebhookReport(0, 0, 0, 0, 10, 0));
        quietLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        quietHook.Requests.Should().Be(0);
    }

    [Fact]
    public async Task Report_IncludesFailureCounts_AndAlerts()
    {
        // QA-2: signature and processing failures only lived in a counter; the report now names them and alerts.
        var webhook = new RecordingHandler();
        var logger = new CapturingLogger();
        BillingWebhookFailureTotals totals = new();
        using ServiceProvider provider = Build(db => db.BillingWebhookEvents.Add(Webhook("evt_1", hoursAgo: 1, "Applied")));
        DailySpendReportService service = Service(
            provider, new SpendReportOptions { AlertWebhookUrl = "https://alerts.internal/spend" }, webhook, logger, totals);
        totals.RecordSignatureFailure();
        totals.RecordSignatureFailure();
        totals.RecordSignatureFailure();
        totals.RecordProcessingFailure();

        SpendReportWindow window = await service.ReportOnceAsync(Now);

        window.BillingWebhooks.Should().Be(new BillingWebhookReport(1, 0, 0, 0, 10, 0, SignatureFailures: 3, ProcessingFailures: 1));
        logger.Messages.Should().Contain(
            "Billing webhooks over 24h: 1 received, 0 unresolved, 0 reconciled, 0 waiting past 10 min, 0 failing to reconcile; 3 signature failures, 1 processing failures since the last report (this instance)");
        SpendReportAlert alert = JsonSerializer.Deserialize<List<SpendReportAlert>>(
            webhook.Bodies.Should().ContainSingle().Subject, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Should().ContainSingle().Subject;
        alert.Scope.Should().Be("billing_webhooks");
        alert.Detail.Should().Contain("3 signature failures, 1 processing failures since the last report (this instance)");

        // The next report counts from this one: nothing new, nothing to alert on.
        SpendReportWindow next = await service.ReportOnceAsync(Now.AddHours(24));

        next.BillingWebhooks!.SignatureFailures.Should().Be(0);
        next.BillingWebhooks.ProcessingFailures.Should().Be(0);
        webhook.Requests.Should().Be(1);
    }

    // --- harness ---------------------------------------------------------------------------------

    private static UsageEvent Unpriced(UsageEvent row)
    {
        row.Unpriced = true;
        return row;
    }

    private static ModelCapabilityView Capability(string name, bool enabled = true, Guid? organizationId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ModelName = name.ToLowerInvariant().Replace(' ', '-'),
        Kind = InferenceProviderKind.Anthropic,
        Capability = InferenceProviderCapability.Chat,
        Endpoint = "https://api.anthropic.com",
        IsEnabled = enabled,
        OrganizationId = organizationId
    };

    private static BillingWebhookEvent Webhook(string eventId, int hoursAgo, string outcome) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "paddle",
        EventId = eventId,
        EventType = "subscription.updated",
        ReceivedAt = Now.UtcDateTime.AddHours(-hoursAgo),
        ProcessedAt = Now.UtcDateTime.AddHours(-hoursAgo),
        Outcome = outcome
    };

    private sealed class CapturingLogger : ILogger<DailySpendReportService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IEnumerable<string> Errors => Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static UsageEvent Event(Guid organizationId, decimal cost, int hoursAgo, UsageSource source = UsageSource.Workflow) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        Source = source,
        Kind = UsageKind.LlmInputTokens,
        Quantity = 1m,
        ProviderKind = InferenceProviderKind.Anthropic,
        ModelName = "claude-x",
        CreditsMillis = (long)(cost * 100),
        CostUsd = cost,
        IdempotencyKey = Guid.NewGuid().ToString(),
        OccurredAt = Now.UtcDateTime.AddHours(-hoursAgo)
    };

    private static ServiceProvider Build(params UsageEvent[] events) => Build(_ => { }, events);

    private static ServiceProvider Build(Action<InferenceApiDbContext> seed, params UsageEvent[] events)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string dbName = Guid.NewGuid().ToString();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();

        using (InferenceApiDbContext db = provider.GetRequiredService<InferenceApiDbContext>())
        {
            db.UsageEvents.AddRange(events);
            seed(db);
            db.SaveChanges();
        }

        return provider;
    }

    private static DailySpendReportService Service(
        IServiceProvider provider, SpendReportOptions options, RecordingHandler? webhook = null,
        ILogger<DailySpendReportService>? logger = null, BillingWebhookFailureTotals? failureTotals = null)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            logger ?? NullLogger<DailySpendReportService>.Instance,
            webhook is null ? null : new StubHttpClientFactory(webhook),
            new FixedClock(Now),
            failureTotals ?? new BillingWebhookFailureTotals());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public int Requests => Bodies.Count;

        public bool Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw)
            {
                throw new HttpRequestException("the alert endpoint is unreachable");
            }

            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
