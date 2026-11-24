#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// A real (Sqlite) Inference API database holding the given PLATFORM capabilities, with the pre-catalog rate-card seed
/// in <c>rate_cards</c> (or none), after the <see cref="ModelCatalogUpgrader"/> has ported their prices — i.e. what an
/// upgraded install meters against — plus the REAL <see cref="ModelPriceService"/> over it.
/// </summary>
internal sealed class PortedCatalog : IAsyncDisposable
{
    public static readonly DateTime SeedEffective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly Dictionary<(InferenceProviderKind, InferenceProviderCapability, string), Guid> _ids;

    private PortedCatalog(SqliteConnection connection, ServiceProvider services,
        Dictionary<(InferenceProviderKind, InferenceProviderCapability, string), Guid> ids)
    {
        _connection = connection;
        _services = services;
        _ids = ids;
        Prices = new ModelPriceService(new EfModelPriceSource<InferenceApiDbContext>(services.GetRequiredService<IServiceScopeFactory>()));
    }

    public ModelPriceService Prices { get; }

    /// <summary>A fresh context over the same database (dispose it).</summary>
    public InferenceApiDbContext NewContext() => _services.CreateScope().ServiceProvider.GetRequiredService<InferenceApiDbContext>();

    /// <summary>
    /// Creates the database, one platform provider account per kind and one model + capability per entry, writes the
    /// <paramref name="cards"/> (default: the whole legacy seed incl. compute) and runs the one-time port.
    /// </summary>
    public static async Task<PortedCatalog> CreateAsync(
        IEnumerable<(InferenceProviderKind Kind, InferenceProviderCapability Capability, string ModelCode)> capabilities,
        IEnumerable<RateCard>? cards = null)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(connection));
        ServiceProvider provider = services.BuildServiceProvider();

        Dictionary<(InferenceProviderKind, InferenceProviderCapability, string), Guid> ids = [];
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            await db.Database.EnsureCreatedAsync();

            Dictionary<InferenceProviderKind, Guid> accounts = [];
            Dictionary<(InferenceProviderKind, string), Guid> models = [];
            foreach ((InferenceProviderKind kind, InferenceProviderCapability capability, string code) in capabilities.Distinct())
            {
                if (!accounts.TryGetValue(kind, out Guid account))
                {
                    account = Guid.NewGuid();
                    accounts[kind] = account;
                }

                Guid capabilityId = Guid.NewGuid();
                ids[(kind, capability, code)] = capabilityId;
                bool newModel = !models.TryGetValue((kind, code), out Guid modelId);
                if (newModel) modelId = Guid.NewGuid();
                models[(kind, code)] = modelId;

                if (newModel)
                {
                    db.AddFlat(new ModelCapabilityView
                    {
                        Id = capabilityId,
                        ProviderId = account,
                        ProviderName = kind.ToString(),
                        ModelId = modelId,
                        Name = $"{kind} {code}",
                        Kind = kind,
                        Capability = capability,
                        ModelName = code,
                        Endpoint = "https://x"
                    });
                }
                else
                {
                    db.Add(new ModelCapability { Id = capabilityId, ModelId = modelId, Capability = capability });
                }
            }

            db.RateCards.AddRange(cards ?? LegacyRateCardSeed.Cards(SeedEffective).Concat(LegacyRateCardSeed.ComputeCards(SeedEffective)));
            await db.SaveChangesAsync();

            ModelCatalogUpgrader upgrader = new(db, new NoKeys(), new ConfigurationBuilder().Build(), NullLogger<ModelCatalogUpgrader>.Instance);
            await upgrader.EnsureComputeProviderAsync();
            await upgrader.PortPricesOnceAsync();
            ids[(ComputeMetering.ProviderKind, InferenceProviderCapability.Compute, WellKnownModelCatalog.ComputeModelCode)] =
                WellKnownModelCatalog.ComputeCapabilityId;
        }

        return new PortedCatalog(connection, provider, ids);
    }

    public Guid CapabilityId(InferenceProviderKind kind, InferenceProviderCapability capability, string code) =>
        _ids[(kind, capability, code)];

    /// <summary>Prices <paramref name="quantity"/> units through the capability configured for (kind, capability, code).</summary>
    public ValueTask<PriceQuote> PriceAsync(
        InferenceProviderKind kind, InferenceProviderCapability capability, string configuredCode, UsageKind unit,
        decimal quantity = 1, string? variant = null, string? modelThatRan = null,
        UsageBillingMode mode = UsageBillingMode.Platform) =>
        Prices.PriceAsync(new PriceRequest(
            CapabilityId(kind, capability, configuredCode), null, kind, capability, modelThatRan ?? configuredCode, unit, variant,
            mode, quantity));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>No key ring: nothing here decrypts (porting never needs a key).</summary>
    private sealed class NoKeys : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
