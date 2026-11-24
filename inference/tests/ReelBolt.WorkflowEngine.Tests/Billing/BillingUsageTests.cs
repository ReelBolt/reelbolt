using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B10b-UI: the read-only usage aggregate over <c>usage_events</c>. Every figure is what the ledger
/// recorded, so the tests assert the roll-up, the org scope, the window, and that our own provider
/// cost is never handed to a non-admin.
/// </summary>
public sealed class BillingUsageTests : IDisposable
{
    private static readonly DateTime Day1 = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day2 = new(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _orgA = Guid.NewGuid();
    private readonly Guid _orgB = Guid.NewGuid();
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _owner = Guid.NewGuid();

    public BillingUsageTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.AddRange(
            new Organization { Id = _orgA, Name = "A", Slug = "a", Kind = OrganizationKind.Team },
            new Organization { Id = _orgB, Name = "B", Slug = "b", Kind = OrganizationKind.Team });
        db.ApplicationUsers.Add(new ApplicationUser { Id = _owner, Email = "o@x.y", DisplayName = "o" });
        db.Projects.Add(new Project { Id = _projectA, OrganizationId = _orgA, OwnerId = _owner, Name = "Promo", UpdatedAt = Day1 });
        db.UsageEvents.AddRange(
            Event(_orgA, Day1, UsageKind.LlmInputTokens, UsageBillingMode.Platform, 1_500, 0.25m, 1_000, "gpt-x", _projectA, "a1"),
            Event(_orgA, Day1, UsageKind.LlmOutputTokens, UsageBillingMode.Platform, 500, 0.05m, 250, "gpt-x", _projectA, "a2"),
            Event(_orgA, Day2, UsageKind.RenderOutputSeconds, UsageBillingMode.Runner, 2_000, 0m, 60, string.Empty, null, "a3"),
            // Another organization: must never appear in the caller's roll-up.
            Event(_orgB, Day1, UsageKind.LlmInputTokens, UsageBillingMode.Platform, 999_000, 9.99m, 1, "gpt-x", null, "b1"));
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private static UsageEvent Event(
        Guid org, DateTime at, UsageKind kind, UsageBillingMode mode, long creditsMillis, decimal cost,
        decimal quantity, string model, Guid? projectId, string key) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            ProjectId = projectId,
            Source = UsageSource.Workflow,
            Kind = kind,
            BillingMode = mode,
            CreditsMillis = creditsMillis,
            CostUsd = cost,
            Quantity = quantity,
            ProviderKind = InferenceProviderKind.OpenAICompatible,
            ModelName = model,
            IdempotencyKey = key,
            OccurredAt = at
        };

    private sealed class FakeUser(Guid? org, OrganizationRole? role, bool platformAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => platformAdmin;
    }

    private sealed class FakeTenant : ITenantContext
    {
        public Guid? OrganizationId => null;
        public bool IsSystem => true;
        public IDisposable BeginSystemScope() => new Nop();
        public IDisposable BeginOrganizationScope(Guid organizationId) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private sealed class NoProviders : IBillingProvider
    {
        public string Name => "none";
        public bool IsConfigured => false;
        public IReadOnlyList<int> TopUpPacks => [];
        public string? TopUpPriceId(int credits) => null;
        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct) => throw new NotSupportedException();
        public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers) => throw new NotSupportedException();
        public Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct) => throw new NotSupportedException();
        public Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private BillingController Controller(
        bool enabled = true,
        Guid? org = null,
        OrganizationRole? role = OrganizationRole.Member,
        bool platformAdmin = false)
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db,
            new FakeUser(org ?? _orgA, role, platformAdmin),
            Options.Create(new BillingOptions { Enabled = enabled }),
            [new NoProviders()],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance),
            new FakeTenant(),
            Options.Create(new PaddleOptions()),
            NullLogger<BillingController>.Instance);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return c;
    }

    private static BillingUsageResponse Body(IActionResult result)
        => ((OkObjectResult)result).Value.Should().BeOfType<BillingUsageResponse>().Subject;

    private static Task<IActionResult> Call(
        BillingController c, string? groupBy = "day", DateTime? from = null, DateTime? to = null, Guid? executionId = null)
        => c.Usage(from ?? Day1, to ?? Day2.AddDays(1), groupBy, executionId, default);

    [Fact]
    public async Task Disabled_Is404_LikeEveryOtherBillingEndpoint()
        => (await Call(Controller(enabled: false))).Should().BeOfType<NotFoundResult>();

    [Fact]
    public async Task NoActiveOrganization_Is404()
        => (await Call(Controller(org: null, role: null))).Should().BeOfType<NotFoundResult>();

    [Fact]
    public async Task UnknownGroupBy_Is400()
    {
        BadRequestObjectResult bad = (BadRequestObjectResult)await Call(Controller(), "hourly");
        bad.Value!.ToString().Should().Contain("unknown_group_by");
    }

    [Fact]
    public async Task Window_MustBeOrderedAndBounded()
    {
        (await Call(Controller(), "day", Day2, Day1)).Should().BeOfType<BadRequestObjectResult>();
        (await Call(Controller(), "day", Day1.AddDays(-400), Day1)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Defaults_ToTheLastThirtyDaysStartingAtTheEnd()
    {
        BillingUsageResponse body = Body(await Controller().Usage(null, null, null, null, default));

        body.From.Should().BeCloseTo(DateTime.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));
        body.GroupBy.Should().Be("day");
    }

    [Fact]
    public async Task Totals_AndModeSplit_ComeFromTheLedger_AndExcludeOtherOrganizations()
    {
        BillingUsageResponse body = Body(await Call(Controller(), "kind"));

        body.Totals.Credits.Should().Be(4.0);
        body.Totals.Events.Should().Be(3);
        body.ByBillingMode.Select(m => (m.Mode, m.Credits)).Should()
            .BeEquivalentTo([("Platform", 2.0), ("Runner", 2.0)]);
        body.Totals.CostUsd.Should().BeNull("the caller is an org member, not a platform admin");
    }

    [Fact]
    public async Task GroupsBySource_LabelledWithTheSourceName_BiggestSpenderFirst()
    {
        using (InferenceApiDbContext db = NewDb())
        {
            UsageEvent assistant = Event(_orgA, Day1, UsageKind.LlmOutputTokens, UsageBillingMode.Platform, 3_000, 0.01m, 10, "claude", null, "a4");
            assistant.Source = UsageSource.Assistant;
            UsageEvent mcp = Event(_orgA, Day2, UsageKind.LlmOutputTokens, UsageBillingMode.Platform, 1_000, 0.01m, 5, "claude", null, "a5");
            mcp.Source = UsageSource.Mcp;
            UsageEvent editor = Event(_orgB, Day1, UsageKind.ImageEditImages, UsageBillingMode.Platform, 50_000, 0.5m, 1, "flux", null, "b2");
            editor.Source = UsageSource.Editor;
            db.UsageEvents.AddRange(assistant, mcp, editor);
            db.SaveChanges();
        }

        BillingUsageResponse body = Body(await Call(Controller(), "source"));

        body.GroupBy.Should().Be("source");
        body.Groups.Select(g => (g.Key, g.Label, g.Credits, g.Events)).Should().Equal(
            ("Workflow", "Workflow", 4.0, 3),
            ("Assistant", "Assistant", 3.0, 1),
            ("Mcp", "Mcp", 1.0, 1));
        body.Totals.Credits.Should().Be(8.0, "another organization's editor spend is never in the roll-up");
    }

    [Fact]
    public async Task AnUnknownAxis_ListsSourceAmongTheSupportedOnes()
    {
        IActionResult result = await Call(Controller(), "colour");

        string json = System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)result).Value);
        json.Should().Contain("\"source\"");
    }

    [Fact]
    public async Task GroupsByDay_Chronologically_WithTheUnitMixPerDay()
    {
        BillingUsageResponse body = Body(await Call(Controller(), "day"));

        body.Groups.Select(g => g.Key).Should().Equal("2026-01-01", "2026-01-02");
        body.Groups[0].Credits.Should().Be(2.0);
        body.Groups[0].Events.Should().Be(2);
        body.Groups[0].Units.Should().BeEquivalentTo([
            new UsageUnitDto(nameof(UsageKind.LlmInputTokens), 1000),
            new UsageUnitDto(nameof(UsageKind.LlmOutputTokens), 250)
        ]);
        body.Groups[1].Units.Should().BeEquivalentTo([new UsageUnitDto(nameof(UsageKind.RenderOutputSeconds), 60)]);
    }

    [Fact]
    public async Task GroupsByProject_WithAName_AndAnExplicitBucketWhenNothingIsAttributed()
    {
        BillingUsageResponse body = Body(await Call(Controller(), "project"));

        body.Groups.Should().HaveCount(2);
        body.Groups.Should().ContainSingle(g => g.Label == "Promo" && g.Credits == 2.0);
        // Runner usage carries no project: it is named, not silently dropped.
        body.Groups.Should().ContainSingle(g => g.Label == "Not attributed to a project" && g.Credits == 2.0);
    }

    [Fact]
    public async Task GroupsByModel_AndKeepsAModelLessBucket()
    {
        BillingUsageResponse body = Body(await Call(Controller(), "model"));

        body.Groups.Should().ContainSingle(g => g.Key == "gpt-x" && g.Credits == 2.0 && g.Events == 2);
        body.Groups.Should().ContainSingle(g => g.Label == "Not model-specific" && g.Credits == 2.0);
    }

    [Fact]
    public async Task GroupsByKind_AndByMode()
    {
        BillingUsageResponse byKind = Body(await Call(Controller(), "kind"));
        byKind.Groups.Select(g => (g.Key, g.Credits)).Should().BeEquivalentTo([
            (nameof(UsageKind.LlmInputTokens), 1.5),
            (nameof(UsageKind.LlmOutputTokens), 0.5),
            (nameof(UsageKind.RenderOutputSeconds), 2.0)
        ]);

        BillingUsageResponse byMode = Body(await Call(Controller(), "mode"));
        byMode.Groups.Select(g => g.Key).Should().BeEquivalentTo("Platform", "Runner");
    }

    [Fact]
    public async Task ProviderCost_IsReportedToPlatformAdminsOnly()
    {
        BillingUsageResponse member = Body(await Call(Controller()));

        BillingUsageResponse admin = Body(await Call(Controller(platformAdmin: true)));

        member.Totals.CostUsd.Should().BeNull();
        member.Groups.Should().OnlyContain(g => g.CostUsd == null);
        admin.Totals.CostUsd.Should().Be(0.3);
        admin.Groups.Should().Contain(g => g.CostUsd == 0.3);
    }

    [Fact]
    public async Task ExecutionFilter_NarrowsToThatRunsRows_AndWidensTheDefaultWindow()
    {
        Guid execution = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            await db.UsageEvents.Where(e => e.IdempotencyKey == "a1")
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ExecutionId, execution));
        }

        BillingUsageResponse body = Body(await Call(Controller(), "kind", executionId: execution));

        body.Totals.Credits.Should().Be(1.5);
        body.Totals.Events.Should().Be(1);
    }

    [Fact]
    public async Task EmptyWindow_IsAnEmptyRollUp_NotAnError()
    {
        BillingUsageResponse body = Body(await Call(Controller(), "day", Day1.AddYears(-1), Day1.AddYears(-1).AddDays(1)));

        body.Totals.Should().Be(new UsageTotalsDto(0, 0, null));
        body.Groups.Should().BeEmpty();
        body.ByBillingMode.Should().BeEmpty();
    }
}
