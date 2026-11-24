#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The reconciler as the API's own registration builds it (<c>AddInferenceApiServices</c>), with Billing:Provider=Stripe, both
/// providers configured and no local subscription row: it must ask Stripe, never the first configured provider (Paddle).
/// Regression: the factory lambda did not pass the optional provider selection.
/// </summary>
public sealed class BillingReconcilerCompositionTests : IDisposable
{
    private static readonly Guid Org = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly NamedProvider _paddle = new("paddle");
    private readonly NamedProvider _stripe = new("stripe");

    public BillingReconcilerCompositionTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task WithStripeSelectedAndNoSubscriptionRow_TheProductionWiredReconciler_AsksStripeNotPaddle()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "reconciler-stripe-selected",
            (services, configuration) =>
            {
                services.AddInferenceApiServices(configuration);
                services.RemoveAll<IBillingProvider>();
                services.AddSingleton<IBillingProvider>(_paddle);
                services.AddSingleton<IBillingProvider>(_stripe);
                foreach (ServiceDescriptor d in services.Where(d => d.ServiceType == typeof(InferenceApiDbContext)
                    || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(InferenceApiDbContext)))).ToList())
                {
                    services.Remove(d);
                }

                services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
            },
            new Dictionary<string, string?>
            {
                ["Billing:Enabled"] = "true",
                ["Billing:Provider"] = "Stripe"
            });

        using (IServiceScope seed = root.Services.CreateScope())
        {
            InferenceApiDbContext db = seed.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            db.Database.EnsureCreated();
            db.Organizations.Add(new Organization { Id = Org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
            db.BillingCustomers.Add(new BillingCustomer { Id = Guid.NewGuid(), OrganizationId = Org, Provider = "stripe", ProviderCustomerId = "cus_1" });
            await db.SaveChangesAsync();
        }

        using IServiceScope scope = root.Services.CreateScope();
        IBillingReconciler reconciler = scope.ServiceProvider.GetRequiredService<IBillingReconciler>();

        ReconcileResult result = await reconciler.ReconcileAsync(Org, ReconcileTrigger.Admin, force: true, CancellationToken.None);

        _stripe.FindLiveCalls.Should().Equal("cus_1");
        _paddle.FindLiveCalls.Should().BeEmpty("a Stripe checkout must never be looked up at Paddle");
        result.Error.Should().NotBe(BillingReconciler.NoCustomerError);
    }

    private sealed class NamedProvider(string name) : IBillingProvider
    {
        public List<string> FindLiveCalls { get; } = [];

        public string Name => name;
        public bool IsConfigured => true;
        public IReadOnlyList<int> TopUpPacks => [];
        public string? TopUpPriceId(int credits) => null;

        public Task<string?> FindLiveSubscriptionIdAsync(string providerCustomerId, Guid organizationId, CancellationToken ct)
        {
            FindLiveCalls.Add(providerCustomerId);
            return Task.FromResult<string?>(null);
        }

        public Task<CheckoutSession> CreateCheckoutAsync(CheckoutRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasLiveSubscriptionAsync(string providerCustomerId, Guid organizationId, CancellationToken ct) => throw new NotSupportedException();
        public Task<PortalSession> CreatePortalSessionAsync(string providerCustomerId, string? providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public BillingEvent VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers) => throw new NotSupportedException();
        public Task CancelSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ResumeSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task ChangePlanAsync(string providerSubscriptionId, IReadOnlyList<BillingLineItem> items, PlanChangeTiming timing, PlanChangeDiscount discount, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderSubscriptionState> GetSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<BillingSnapshot> SnapshotSubscriptionAsync(string providerSubscriptionId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BillingSnapshot>> CompletedUpgradeTransactionsAsync(string providerSubscriptionId, DateTime since, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> CreateDiscountAsync(ProviderDiscountSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateDiscountAsync(string discountId, bool? active, DateTime? expiresAt, bool clearExpiry, CancellationToken ct) => throw new NotSupportedException();
        public Task<PricePreview> PreviewPriceAsync(IReadOnlyList<BillingLineItem> items, string? discountId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderTransactionCancel> CancelTransactionAsync(string providerTransactionId, CancellationToken ct) => throw new NotSupportedException();
    }
}
