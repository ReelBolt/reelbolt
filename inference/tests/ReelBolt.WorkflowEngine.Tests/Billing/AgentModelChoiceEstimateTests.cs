#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// agent-model-choice, QA: a workspace's per-agent model choice flows into the cost estimate and the hold.
/// The estimate (Inference API) and the hold (engine) both price a cold step through <see cref="ProfilePriceTable"/>,
/// which asks the REAL <see cref="InferenceProviderResolver"/> for the agent's chat model and prices that model; so the
/// workspace choice (organization_agent_provider_overrides) moves the quote with no estimator change. The price service
/// here is a stub that charges by model code, so a quote can only move if the resolved model moved.
/// </summary>
public sealed class AgentModelChoiceEstimateTests
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");
    private static readonly ProfileQuantity OneMillionInput = new(UsageKind.LlmInputTokens, ProfilePriceTarget.Chat, 1_000_000m);

    private const long CheapPerTokenMillis = 1;
    private const long PricierPerTokenMillis = 7;

    private readonly ModelCapabilityView _cheap = Chat("Cheap model", "cheap-m");
    private readonly ModelCapabilityView _pricier = Chat("Pricier model", "pricier-m");

    [Fact]
    public async Task The_estimate_prices_the_recommended_model_until_the_workspace_picks_another()
    {
        // Platform recommendation: the built-in definition points at the cheap model (resolver step 3).
        Dictionary<AgentType, Guid?> recommended = new() { [AgentType.AuthorAgent] = _cheap.Id };

        long untouched = await QuoteAsync(OrgA, recommended, orgChoices: null);
        long picked = await QuoteAsync(OrgA, recommended, new() { [OrgA] = new() { [AgentType.AuthorAgent] = _pricier.Id } });
        long otherWorkspace = await QuoteAsync(OrgB, recommended, new() { [OrgA] = new() { [AgentType.AuthorAgent] = _pricier.Id } });

        untouched.Should().Be(1_000_000 * CheapPerTokenMillis, "no workspace choice: the recommended model is what runs");
        picked.Should().Be(1_000_000 * PricierPerTokenMillis, "the workspace choice beats the recommendation, so the quote follows it");
        otherWorkspace.Should().Be(untouched, "another workspace's choice never moves this workspace's estimate");
    }

    [Fact]
    public async Task A_choice_for_one_agent_does_not_move_another_agents_estimate()
    {
        Dictionary<AgentType, Guid?> recommended = new()
        {
            [AgentType.AuthorAgent] = _cheap.Id,
            [AgentType.DirectorAgent] = _cheap.Id
        };
        Dictionary<Guid, Dictionary<AgentType, Guid?>> choice = new() { [OrgA] = new() { [AgentType.AuthorAgent] = _pricier.Id } };

        long director = await QuoteAsync(OrgA, recommended, choice, AgentType.DirectorAgent);

        director.Should().Be(1_000_000 * CheapPerTokenMillis);
    }

    [Fact]
    public async Task A_choice_pointing_at_a_model_that_is_now_unpriced_is_estimated_on_the_recommended_model_not_free()
    {
        // Billing on: the platform model the workspace picked lost its price after the pick. The resolver treats it as
        // absent (it would refuse to run it), so the run, and therefore the estimate, falls back to the recommendation.
        Dictionary<AgentType, Guid?> recommended = new() { [AgentType.AuthorAgent] = _cheap.Id };

        long quote = await QuoteAsync(
            OrgA, recommended, new() { [OrgA] = new() { [AgentType.AuthorAgent] = _pricier.Id } },
            unpriced: [_pricier.Id]);

        quote.Should().Be(1_000_000 * CheapPerTokenMillis);
    }

    private async Task<long> QuoteAsync(
        Guid org,
        Dictionary<AgentType, Guid?> builtIn,
        Dictionary<Guid, Dictionary<AgentType, Guid?>>? orgChoices,
        AgentType agent = AgentType.AuthorAgent,
        Guid[]? unpriced = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IInferenceProviderStore>(new Store([_cheap, _pricier], builtIn, orgChoices, unpriced ?? []));
        ServiceProvider sp = services.BuildServiceProvider();
        InferenceProviderResolver resolver = new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Enabled"] = "true" }).Build(),
            new Passthrough(),
            NullLogger<InferenceProviderResolver>.Instance,
            new Tenant(org));

        IProfileQuantityPricer pricer = await ProfilePriceTable.BuildAsync(
            [ProfilePriceKey.Of(OneMillionInput, agent, null)], resolver, new ByModelPrices(),
            NullLogger.Instance, CancellationToken.None);

        return pricer.Price(OneMillionInput, agent, null).CreditsMillis;
    }

    private static ModelCapabilityView Chat(string name, string code) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.DeepSeek,
        Capability = InferenceProviderCapability.Chat,
        Endpoint = "https://provider.example",
        ModelName = code,
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private sealed class Tenant(Guid org) : InferenceTenantAccessorBase
    {
        protected override Guid? AmbientOrganizationId => org;
    }

    private sealed class Passthrough : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext ?? string.Empty;
            return ciphertext != null;
        }
    }

    private sealed class Store(
        IReadOnlyList<ModelCapabilityView> rows,
        Dictionary<AgentType, Guid?> builtIn,
        Dictionary<Guid, Dictionary<AgentType, Guid?>>? orgChoices,
        Guid[] unpriced) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelCapabilityView>>(rows.ToList());

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(
                organizationId is { } o && orgChoices is not null && orgChoices.TryGetValue(o, out Dictionary<AgentType, Guid?>? mine)
                    ? mine
                    : new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(builtIn);

        public Task<IReadOnlySet<Guid>> LoadUnpricedCapabilityIdsAsync(DateTime at, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(unpriced.ToHashSet());
    }

    /// <summary>Charges per token by model code, so a quote moves only when the model moved.</summary>
    private sealed class ByModelPrices : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
        {
            long perToken = request.ModelCode == "cheap-m" ? CheapPerTokenMillis
                : request.ModelCode == "pricier-m" ? PricierPerTokenMillis
                : throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);
            return ValueTask.FromResult(new PriceQuote((long)(request.Quantity * perToken), 0m, null));
        }

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(
            decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote(0, costUsd, null));

        public void Invalidate()
        {
        }
    }
}
