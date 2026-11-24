#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B12's delta: a residual Paddle-vs-ledger difference is a <b>named</b> finding rather than silence, and
/// the ledger's unattributed <c>usage_events</c> rows are countable. These pin the classifier that turns a
/// reconcile result into named findings (both directions) and the attribution query.
/// </summary>
public sealed class BillingDriftReportTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ReconcileResult Result(ReconcileOutcome outcome, string[] drift, string? error = null, int paymentsUnapplied = 0)
        => new(outcome, drift, 0, 0, error, paymentsUnapplied);

    // --- the classifier: a difference is named, silence is not an option ---------------------------------

    [Fact]
    public void A_provider_subscription_the_ledger_lacks_is_named()
    {
        IReadOnlyList<BillingDriftFinding> findings = BillingDriftReport.Classify(Org,
            Result(ReconcileOutcome.Stale, [SubscriptionDrift.SubscriptionMissing], "subscription event stale: the provider's state was not applied"));

        findings.Should().ContainSingle().Which.Class.Should().Be(BillingDriftClass.SubscriptionMissing);
    }

    [Fact]
    public void A_ledger_subscription_the_provider_lacks_is_named()
    {
        // The reverse: the row carries a provider subscription id Paddle answers 404 for.
        IReadOnlyList<BillingDriftFinding> findings = BillingDriftReport.Classify(Org,
            Result(ReconcileOutcome.ProviderUnavailable, [SubscriptionDrift.LedgerOnlyField], "provider refused: HTTP 404 subscription_not_found"));

        findings.Should().ContainSingle().Which.Class.Should().Be(BillingDriftClass.LedgerOnly);
    }

    [Fact]
    public void A_provider_transaction_the_ledger_did_not_apply_is_named()
    {
        // Paddle holds a completed upgrade payment the processor did not apply; the subscription itself is
        // in sync, so only the payment is a finding.
        IReadOnlyList<BillingDriftFinding> findings = BillingDriftReport.Classify(Org,
            Result(ReconcileOutcome.InSync, [], paymentsUnapplied: 2));

        findings.Should().ContainSingle().Which.Class.Should().Be(BillingDriftClass.TransactionUnapplied);
    }

    [Fact]
    public void A_healed_change_is_not_a_finding()
    {
        BillingDriftReport.Classify(Org, Result(ReconcileOutcome.Healed, [SubscriptionDrift.Plan])).Should().BeEmpty();
    }

    [Fact]
    public void A_young_change_left_to_its_webhook_is_not_a_finding()
    {
        BillingDriftReport.Classify(Org, Result(ReconcileOutcome.TooRecent, [SubscriptionDrift.Plan])).Should().BeEmpty();
    }

    [Fact]
    public void An_unreadable_provider_is_a_finding()
    {
        // The comparison could not be made at all: that must not read as "in sync".
        IReadOnlyList<BillingDriftFinding> findings = BillingDriftReport.Classify(Org,
            Result(ReconcileOutcome.ProviderUnavailable, [], "provider error: HTTP 503"));

        findings.Should().ContainSingle().Which.Class.Should().Be(BillingDriftClass.Unreadable);
    }

    [Fact]
    public void A_stale_drift_is_named_per_field()
    {
        IReadOnlyList<BillingDriftFinding> findings = BillingDriftReport.Classify(Org,
            Result(ReconcileOutcome.Stale, [SubscriptionDrift.Plan, SubscriptionDrift.Seats], "stale"));

        findings.Select(f => f.Class).Should().Equal(BillingDriftClass.Plan, BillingDriftClass.Seats);
    }

    [Fact]
    public void The_report_names_its_classes_for_an_operator()
    {
        BillingDriftReport report = new(
            Candidates: 3,
            Findings:
            [
                new(BillingDriftClass.LedgerOnly, Org, "provider refused: HTTP 404"),
                new(BillingDriftClass.LedgerOnly, Guid.NewGuid(), "provider refused: HTTP 404"),
                new(BillingDriftClass.SubscriptionMissing, Guid.NewGuid(), "stale")
            ],
            UnattributedUsageEvents: 4);

        report.HasDrift.Should().BeTrue();
        report.ByClass[BillingDriftClass.LedgerOnly].Should().Be(2);
        report.DescribeClasses().Should().Be("ledger_only=2, subscription_missing=1");
    }

    // --- the unattributed usage_events query --------------------------------------------------------------

    [Fact]
    public async Task The_attribution_query_counts_rows_with_no_attributable_org_or_user()
    {
        using InferenceApiDbContext db = NewDb();
        db.UsageEvents.AddRange(
            Event(Guid.NewGuid(), UsageSource.Workflow, userId: Guid.NewGuid()),
            Event(WellKnownOrganizations.PlatformId, UsageSource.Unattributed, userId: null),
            Event(WellKnownOrganizations.PlatformId, UsageSource.Unattributed, userId: null),
            Event(Guid.NewGuid(), UsageSource.Assistant, userId: null));
        await db.SaveChangesAsync();

        UsageAttributionReport report = await BillingUsageAttribution.QueryAsync(db, Now.AddHours(-24), Now, CancellationToken.None);

        report.TotalEvents.Should().Be(4);
        report.UnattributedEvents.Should().Be(2);
        report.NoOrganizationEvents.Should().Be(2);
        report.NoUserEvents.Should().Be(3);
        report.BySource.Should().Contain(new UsageAttributionBucket("Unattributed", 2));
    }

    [Fact]
    public async Task The_attribution_query_counts_only_its_window()
    {
        using InferenceApiDbContext db = NewDb();
        db.UsageEvents.AddRange(
            Event(WellKnownOrganizations.PlatformId, UsageSource.Unattributed, userId: null),
            Event(WellKnownOrganizations.PlatformId, UsageSource.Unattributed, userId: null, at: Now.AddHours(-30)));
        await db.SaveChangesAsync();

        UsageAttributionReport report = await BillingUsageAttribution.QueryAsync(db, Now.AddHours(-24), Now, CancellationToken.None);

        report.UnattributedEvents.Should().Be(1, "the event 30 hours ago is outside the window");
    }

    // --- harness ------------------------------------------------------------------------------------------

    private static InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static UsageEvent Event(Guid organizationId, UsageSource source, Guid? userId, DateTime? at = null) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        UserId = userId,
        Source = source,
        Kind = UsageKind.LlmInputTokens,
        Quantity = 1m,
        ProviderKind = InferenceProviderKind.Anthropic,
        ModelName = "claude-x",
        CreditsMillis = 100,
        CostUsd = 0.01m,
        IdempotencyKey = Guid.NewGuid().ToString(),
        OccurredAt = at ?? Now.AddHours(-1)
    };
}
