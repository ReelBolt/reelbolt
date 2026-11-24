#nullable enable

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The recommended model per built-in agent (agent-model-choice): the shipped seed is well formed and points at
/// models the price book knows, and <see cref="AgentRecommendedModelSeeder"/> fills a built-in definition's NULL
/// <c>inference_provider_id</c> only from an enabled, fully priced PLATFORM Chat capability, never overwriting a value.
/// </summary>
public sealed class AgentRecommendedModelSeederTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _dbName = Guid.NewGuid().ToString();

    // ------------------------------------------------------------------ the shipped seed

    [Fact]
    public void Shipped_seed_names_the_agreed_model_for_each_agent()
    {
        AgentRecommendationSeed seed = AgentRecommendationSeed.LoadDefault();

        seed.Validate().Should().BeEmpty();
        AgentType[] film = [AgentType.AuthorAgent, AgentType.DirectorAgent, AgentType.ScriptwriterAgent,
            AgentType.AnimationStrategyAgent, AgentType.RemotionComponentTranslator, AgentType.ReviewAgent];
        AgentType[] analyzers = [AgentType.CodeStructureAnalyzer, AgentType.DependencyAnalyzer,
            AgentType.ComponentInventoryAnalyzer, AgentType.RouteAndApiAnalyzer, AgentType.StyleAndThemeExtractor,
            AgentType.FileSummarizerAgent];

        seed.Entries.Select(e => e.AgentType).Should().BeEquivalentTo([.. film, .. analyzers]);
        seed.Entries.Where(e => film.Contains(e.AgentType)).Should().OnlyContain(e =>
            e.Kind == InferenceProviderKind.DeepSeek && e.ModelCode == "deepseek-flash"
            && e.Why == "Best results on promo and video writing in our comparison");
        seed.Entries.Where(e => analyzers.Contains(e.AgentType)).Should().OnlyContain(e =>
            e.Kind == InferenceProviderKind.OpenAICompatible && e.ModelCode == "gpt-6-luna"
            && e.Why == "Reads code just as well at a fraction of the cost");
    }

    [Fact]
    public void Every_shipped_recommendation_is_a_Chat_model_in_the_price_book()
    {
        ModelCatalogSeed catalog = ModelCatalogSeed.LoadDefault();

        foreach (AgentRecommendation entry in AgentRecommendationSeed.LoadDefault().Entries)
        {
            catalog.Capabilities.Should().Contain(c => c.Kind == entry.Kind && c.ModelCode == entry.ModelCode
                && c.Capability == "Chat", $"{entry.AgentType} points at {entry.Kind}/{entry.ModelCode}");
        }
    }

    [Theory]
    [InlineData("""{"entries":[{"agentType":"Nope","kind":"DeepSeek","modelCode":"m","why":"w"}]}""")]
    [InlineData("""{"entries":[{"agentType":"Custom","kind":"DeepSeek","modelCode":"m","why":"w"}]}""")]
    [InlineData("""{"entries":[{"agentType":"AuthorAgent","kind":"Nope","modelCode":"m","why":"w"}]}""")]
    [InlineData("""{"entries":[{"agentType":"AuthorAgent","kind":"DeepSeek","modelCode":"","why":"w"}]}""")]
    [InlineData("""{"entries":[{"agentType":"AuthorAgent","kind":"DeepSeek","modelCode":"m","why":" "}]}""")]
    [InlineData("""{"entries":[{"agentType":"AuthorAgent","kind":"DeepSeek","modelCode":"m","why":"w"},{"agentType":"AuthorAgent","kind":"DeepSeek","modelCode":"n","why":"w"}]}""")]
    [InlineData("""not json""")]
    public void A_malformed_seed_is_refused(string json)
    {
        Action act = () =>
        {
            AgentRecommendationSeed seed = AgentRecommendationSeed.Parse(json);
            seed.Validate().Should().BeEmpty();
        };

        act.Should().Throw<Exception>();
    }

    // ------------------------------------------------------------------ the seeder

    private static AgentRecommendationSeed OneEntry(AgentType agent = AgentType.AuthorAgent) => AgentRecommendationSeed.Parse(
        $$"""{"entries":[{"agentType":"{{agent}}","kind":"DeepSeek","modelCode":"deepseek-flash","why":"Because."}]}""");

    [Fact]
    public async Task Fills_a_null_built_in_from_the_matching_priced_platform_chat_model()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        int filled = await RunAsync(OneEntry());

        filled.Should().Be(1);
        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().Be(cap);
    }

    [Fact]
    public async Task Is_idempotent_and_invalidates_the_resolver_after_writing()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);
        Mock<IInferenceProviderResolver> resolver = new();

        (await RunAsync(OneEntry(), resolver: resolver.Object)).Should().Be(1);
        (await RunAsync(OneEntry(), resolver: resolver.Object)).Should().Be(0);

        resolver.Verify(r => r.InvalidateAll(), Times.Once);
    }

    [Fact]
    public async Task Never_overwrites_an_existing_choice()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true);
        Guid chosen = Guid.NewGuid();
        await AddAgentAsync(AgentType.AuthorAgent, chosen);
        Mock<IInferenceProviderResolver> resolver = new();

        (await RunAsync(OneEntry(), resolver: resolver.Object)).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().Be(chosen);
        resolver.Verify(r => r.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task Skips_when_the_model_is_absent()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "some-other-model", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("model")]
    [InlineData("account")]
    public async Task Skips_a_disabled_model(string disabled)
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true,
            capabilityEnabled: disabled != "capability", modelEnabled: disabled != "model", accountEnabled: disabled != "account");
        await AddAgentAsync(AgentType.AuthorAgent, null);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task Skips_an_unpriced_model()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: false);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task Skips_a_partly_priced_model()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true, onlyUnit: UsageKind.LlmInputTokens);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task Ignores_an_organization_owned_model_and_a_non_chat_capability()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true, organizationId: Guid.NewGuid());
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true, capability: InferenceProviderCapability.Vision);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task Ignores_a_custom_agent_of_the_same_type_and_a_kind_mismatch()
    {
        await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "deepseek-flash", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);
        await AddAgentAsync(AgentType.AuthorAgent, null, builtIn: false);

        (await RunAsync(OneEntry())).Should().Be(0);

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task The_off_switch_writes_nothing()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);
        IConfiguration off = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AgentRecommendedModelSeeder.EnabledKey] = "false" }).Build();

        using (InferenceApiDbContext db = CreateContext())
        {
            await AgentRecommendedModelSeeder.SeedAsync(db, off, NullLogger.Instance);
        }

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().BeNull();
    }

    [Fact]
    public async Task The_boot_entry_point_is_on_by_default()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", priced: true);
        await AddAgentAsync(AgentType.AuthorAgent, null);

        using (InferenceApiDbContext db = CreateContext())
        {
            await AgentRecommendedModelSeeder.SeedAsync(db, new ConfigurationBuilder().Build(), NullLogger.Instance);
        }

        (await ProviderOfAsync(AgentType.AuthorAgent)).Should().NotBeNull();
    }

    // ------------------------------------------------------------------ helpers

    private InferenceApiDbContext CreateContext() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(_dbName)
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private async Task<int> RunAsync(AgentRecommendationSeed seed, IInferenceProviderResolver? resolver = null)
    {
        using InferenceApiDbContext db = CreateContext();
        return await AgentRecommendedModelSeeder.RunAsync(db, seed, Now, NullLogger.Instance, resolver);
    }

    private async Task AddAgentAsync(AgentType type, Guid? providerId, bool builtIn = true)
    {
        using InferenceApiDbContext db = CreateContext();
        db.AgentDefinitions.Add(new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = type.ToString(),
            AgentType = type,
            IsBuiltIn = builtIn,
            OrganizationId = builtIn ? null : Guid.NewGuid(),
            InferenceProviderId = providerId,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid?> ProviderOfAsync(AgentType type)
    {
        using InferenceApiDbContext db = CreateContext();
        return (await db.AgentDefinitions.AsNoTracking().SingleAsync(a => a.AgentType == type && a.IsBuiltIn)).InferenceProviderId;
    }

    private async Task<Guid> AddCapabilityAsync(InferenceProviderKind kind, string modelCode, bool priced,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat, Guid? organizationId = null,
        bool capabilityEnabled = true, bool modelEnabled = true, bool accountEnabled = true, UsageKind? onlyUnit = null)
    {
        using InferenceApiDbContext db = CreateContext();
        InferenceProvider provider = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = $"{kind} {Guid.NewGuid():N}",
            Kind = kind,
            Endpoint = "",
            IsEnabled = accountEnabled,
        };
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            OrganizationId = organizationId,
            ModelCode = modelCode,
            DisplayName = modelCode,
            IsEnabled = modelEnabled,
        };
        ModelCapability cap = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            OrganizationId = organizationId,
            Capability = capability,
            IsEnabled = capabilityEnabled,
        };
        db.InferenceProviders.Add(provider);
        db.InferenceModels.Add(model);
        db.ModelCapabilities.Add(cap);
        if (priced)
        {
            UsageKind[] units = onlyUnit is { } one ? [one] : ProviderCapabilityUsage.ByCapability[capability];
            foreach (UsageKind unit in units)
            {
                db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = cap.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.000001m,
                    EffectiveFrom = Now.AddDays(-1),
                });
            }
        }

        await db.SaveChangesAsync();
        return cap.Id;
    }
}
