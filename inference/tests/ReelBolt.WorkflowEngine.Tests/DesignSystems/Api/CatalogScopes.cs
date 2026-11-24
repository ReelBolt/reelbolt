#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// A scope factory serving the API context, a provider resolver and a price service, for the REAL
/// <c>WorkflowCostEstimator</c>. <see cref="For"/> wires one default Chat model priced from the compiled-in
/// <see cref="ModelPriceCatalog"/> rows (what the platform provider seeding writes to
/// <c>model_capability_prices</c>); a model with no catalog price has no rows.
/// </summary>
internal sealed class CatalogScopes(InferenceApiDbContext db, IInferenceProviderResolver resolver, IModelPriceService? prices)
    : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public static CatalogScopes For(InferenceApiDbContext db, InferenceProviderKind kind, string model, Guid? byoOrganizationId = null)
    {
        Guid capability = Guid.NewGuid();
        Guid account = Guid.NewGuid();
        return new CatalogScopes(
            db,
            Resolver(capability, account, kind, model, byoOrganizationId),
            new ModelPriceService(new CatalogSource(capability, account, kind, model)));
    }

    public static IInferenceProviderResolver Resolver(Guid capability, Guid account, InferenceProviderKind kind, string model, Guid? byoOrganizationId = null)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(
                capability, "platform", kind, "https://x", model, "key", 60, OrganizationId: byoOrganizationId, IsPlatformManaged: byoOrganizationId is null, AccountId: account));
        resolver.Setup(r => r.ResolveSpeechSynthesisAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedSpeechSynthesisProvider?)null);
        resolver.Setup(r => r.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedTranscriptionProvider?)null);
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedVideoGenerationProvider?)null);
        return resolver.Object;
    }

    public IServiceScope CreateScope() => this;

    public IServiceProvider ServiceProvider => this;

    public object? GetService(Type serviceType) =>
        serviceType == typeof(InferenceApiDbContext) ? db
        : serviceType == typeof(IInferenceProviderResolver) ? resolver
        : serviceType == typeof(IModelPriceService) ? prices
        : null;

    public void Dispose()
    {
    }

    /// <summary>
    /// The snapshot the platform provider seeding would produce for one default Chat model: a capability plus the
    /// compiled-in <see cref="ModelPriceCatalog"/> rows for it (none when the catalog has no price), the platform
    /// multiplier 1, and the 1.2 markup and 0.006 credit value.
    /// </summary>
    internal sealed class CatalogSource(Guid capability, Guid account, InferenceProviderKind kind, string model) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct)
        {
            DateTime effective = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            ModelPrice? price = ModelPriceCatalog.All.FirstOrDefault(m =>
                m.ProviderKind == kind && m.Capability == InferenceProviderCapability.Chat && m.Model == model);
            List<ModelCapabilityPrice> rows = [.. (price?.UsdPerUnit ?? new Dictionary<UsageKind, decimal>()).Select(u => new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = capability,
                Unit = u.Key,
                CostUsdPerUnit = u.Value,
                EffectiveFrom = effective,
            })];
            List<RateMultiplier> multipliers =
            [
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Platform, Multiplier = 1m, EffectiveFrom = effective },
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Byo, Multiplier = 0m, EffectiveFrom = effective },
                new() { Id = Guid.NewGuid(), BillingMode = UsageBillingMode.Runner, Multiplier = 0m, EffectiveFrom = effective },
            ];
            return Task.FromResult(new ModelPriceSnapshot(
                [new PricedCapability(capability, account, null, kind, InferenceProviderCapability.Chat, model)],
                rows, multipliers));
        }
    }
}
