#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The fail-closed pricing rule on the assistant's provider tools (platform scope): while billing is on, switching a platform model on
/// without a price in force is a plain refusal that writes nothing, and creating one the price book cannot price leaves
/// it switched off and says so. With billing off nothing changes.
/// </summary>
public sealed class AssistantUnpricedProviderToolTests
{
    private const string PublicEndpoint = "https://8.8.8.8/v1";

    private sealed class CountingDb(DbContextOptions<InferenceApiDbContext> options) : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }
        public void ResetSaveCount() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class PlatformAdmin(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    private sealed record Harness(CountingDb Db, AssistantToolProvider Provider, AssistantToolContext Context, Mock<IInferenceProviderResolver> Resolver);

    private static Harness Create(bool billingOn = true)
    {
        CountingDb db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid adminId = Guid.NewGuid();
        PlatformAdmin admin = new(adminId);
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(billingOn);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, admin, adminTools: tokens =>
            new AssistantAdminTools(db, tokens, admin, new FakeSecretProtector(), providerResolver: resolver.Object, priceBook: new PriceBook(db)));
        return new Harness(db, provider, new AssistantToolContext(adminId), resolver);
    }

    private static ModelCapabilityView SeedDisabledUnpriced(InferenceApiDbContext db)
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            Name = "Mystery",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "mystery-model",
            IsEnabled = false
        };
        db.AddFlat(row);
        db.SaveChanges();
        return row;
    }

    private static AIFunctionArguments Update(Guid id, bool? isEnabled, string? token = null) => new()
    {
        ["providerId"] = id,
        ["name"] = null,
        ["kind"] = null,
        ["endpoint"] = null,
        ["modelName"] = null,
        ["apiKey"] = null,
        ["isDefault"] = null,
        ["isEnabled"] = isEnabled,
        ["timeoutSeconds"] = null,
        ["capability"] = null,
        ["personalOAuthMode"] = null,
        ["confirmationToken"] = token
    };

    private static AIFunctionArguments CreateArgs(string modelName, string? token = null)
    {
        AIFunctionArguments args = new()
        {
            ["name"] = "Fresh",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = modelName,
            ["apiKey"] = "sk-live-key-4321",
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false
        };
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private static string Text(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => raw.ToString()!
    };

    private static AIFunction Tool(Harness h, string name) => h.Provider.GetTools(h.Context).Single(t => t.Name == name);

    private static bool StoredEnabled(InferenceApiDbContext db, Guid id) =>
        db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Single(c => c.Id == id).IsEnabled;

    [Fact]
    public async Task Switching_an_unpriced_platform_model_on_is_refused_at_propose_and_apply_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedDisabledUnpriced(h.Db);

        string refusedProposal = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true)));
        JsonDocument.Parse(refusedProposal).RootElement.GetProperty("error").GetString().Should().Contain("no price");

        // A token minted while billing was off (or before the last price lapsed) is no way around it at apply time.
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true)));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);

        h.Db.ResetSaveCount();
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(Update(row.Id, true, token)));
        JsonDocument.Parse(applied).RootElement.GetProperty("error").GetString().Should().Contain("no price");
        h.Db.SaveCount.Should().Be(0);
        StoredEnabled(h.Db, row.Id).Should().BeFalse();
    }

    /// <summary>A switched-off, PRICED Chat row: switching it on is fine on its own.</summary>
    private static ModelCapabilityView SeedDisabledPriced(InferenceApiDbContext db, Guid? providerId = null)
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = providerId ?? Guid.Empty,
            Name = "Priced",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "priced-model",
            IsEnabled = false
        };
        db.AddFlat(row);
        // Priced = every unit a Chat capability meters has a price in force (the resolver's rule, T-026).
        foreach (UsageKind unit in new[] { UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens })
        {
            db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = row.Id,
                Unit = unit,
                CostUsdPerUnit = 0.000001m,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
        }

        db.SaveChanges();
        return row;
    }

    private static string? ErrorOf(string json) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

    /// <summary>Proposes with billing off (so a token exists), then applies with it on: the apply must re-judge.</summary>
    private static async Task<string> ApplyMintedWhileBillingOffAsync(Harness h, Guid id)
    {
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(id, true)));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);
        h.Db.ResetSaveCount();
        return Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(Update(id, true, token)));
    }

    [Fact]
    public async Task Switching_a_row_on_is_refused_when_it_would_switch_on_its_model_with_an_unpriced_capability()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedDisabledPriced(h.Db);
        InferenceModel model = h.Db.InferenceModels.IgnoreQueryFilters().Single(m => m.Id == h.Db.ModelCapabilities.IgnoreQueryFilters().Single(c => c.Id == row.Id).ModelId);
        model.IsEnabled = false;
        ModelCapability sibling = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            Capability = InferenceProviderCapability.Vision,
            IsEnabled = true,
            PriceSource = ModelPriceSource.Table,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        h.Db.ModelCapabilities.Add(sibling);
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();

        ErrorOf(Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true))))
            .Should().Contain("Vision").And.Contain("no price");

        string applied = await ApplyMintedWhileBillingOffAsync(h, row.Id);
        ErrorOf(applied).Should().Contain("Vision");
        h.Db.SaveCount.Should().Be(0);
        StoredEnabled(h.Db, row.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Switching_a_row_on_is_refused_when_it_would_switch_on_an_account_with_an_unpriced_model()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedDisabledPriced(h.Db);
        Guid accountId = h.Db.InferenceModels.IgnoreQueryFilters()
            .Single(m => m.Id == h.Db.ModelCapabilities.IgnoreQueryFilters().Single(c => c.Id == row.Id).ModelId).ProviderId;
        ModelCapabilityView sibling = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            Name = "Mystery",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "mystery-model",
            IsEnabled = true
        };
        h.Db.ChangeTracker.Clear();
        h.Db.InferenceProviders.Attach(h.Db.InferenceProviders.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == accountId));
        h.Db.AddFlat(sibling);
        h.Db.InferenceProviders.IgnoreQueryFilters().Single(p => p.Id == accountId).IsEnabled = false;
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();

        ErrorOf(Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true))))
            .Should().Contain("Mystery").And.Contain("no price");

        string applied = await ApplyMintedWhileBillingOffAsync(h, row.Id);
        ErrorOf(applied).Should().Contain("Mystery");
        h.Db.SaveCount.Should().Be(0);
        StoredEnabled(h.Db, row.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Switching_a_priced_row_on_is_allowed_when_nothing_unpriced_comes_on_with_it()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedDisabledPriced(h.Db);
        h.Db.ChangeTracker.Clear();

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true)));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(Update(row.Id, true, token)));

        applied.Should().Contain("\"updated\":true");
        StoredEnabled(h.Db, row.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_model_the_price_book_cannot_price_leaves_it_switched_off_and_says_so()
    {
        Harness h = Create();

        string proposed = Text(await Tool(h, "ProposeCreateInferenceProvider").InvokeAsync(CreateArgs("unknown-model-x")));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        string applied = Text(await Tool(h, "ApplyCreateInferenceProvider").InvokeAsync(CreateArgs("unknown-model-x", token)));

        using JsonDocument reply = JsonDocument.Parse(applied);
        reply.RootElement.GetProperty("created").GetBoolean().Should().BeTrue();
        reply.RootElement.GetProperty("isEnabled").GetBoolean().Should().BeFalse();
        reply.RootElement.GetProperty("note").GetString().Should().Contain("switched OFF");
        StoredEnabled(h.Db, reply.RootElement.GetProperty("id").GetGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task With_billing_off_an_unpriced_model_can_be_switched_on()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = SeedDisabledUnpriced(h.Db);

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(Update(row.Id, true)));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(Update(row.Id, true, token)));

        applied.Should().Contain("\"updated\":true");
        StoredEnabled(h.Db, row.Id).Should().BeTrue();
    }

    // ------------------------------------------------------------------ T-037: a zero-priced self-hosted default never points at a hosted endpoint

    private const string LocalEndpoint = "http://whisper:8000/v1";

    /// <summary>An enabled, local OpenAICompatible Chat row carrying the seed-owned zero row the seeder writes for a self-hosted default.</summary>
    private static ModelCapabilityView SeedSelfHostedZero(InferenceApiDbContext db, InferenceProviderCapability capability = InferenceProviderCapability.Chat,
        string endpoint = LocalEndpoint, string modelName = "local", UsageKind[]? units = null)
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            Name = "Selfhosted",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = capability,
            Endpoint = endpoint,
            ModelName = modelName,
            IsEnabled = true
        };
        db.AddFlat(row);
        foreach (UsageKind unit in units ?? ChatUnits)
        {
            db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = row.Id,
                Unit = unit,
                CostUsdPerUnit = 0m,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1),
                Notes = "seed: model-catalog 2026.10.17.1",
            });
        }
        db.SaveChanges();
        return row;
    }

    /// <summary>Every unit a Chat capability meters: the seed prices a self-hosted default on all of them.</summary>
    private static readonly UsageKind[] ChatUnits = [UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens];

    /// <summary>The provider-edit arguments with only the endpoint (and token) set.</summary>
    private static AIFunctionArguments UpdateEndpoint(Guid id, string endpoint, string? token = null)
    {
        AIFunctionArguments args = Update(id, isEnabled: null);
        args["endpoint"] = endpoint;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private static string StoredEndpoint(InferenceApiDbContext db, Guid id) =>
        db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Include(c => c.Model).ThenInclude(m => m.Provider)
            .Single(c => c.Id == id).Model.Provider.Endpoint;

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_refused_at_propose_and_apply_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);

        string refusedProposal = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, PublicEndpoint)));
        ErrorOf(refusedProposal).Should().Contain("priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.");

        // A token minted while billing was off is no way around it at apply time.
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, PublicEndpoint)));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);

        h.Db.ResetSaveCount();
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, PublicEndpoint, token)));
        ErrorOf(applied).Should().Contain("priced at zero as a self-hosted default");
        h.Db.SaveCount.Should().Be(0);
        StoredEndpoint(h.Db, row.Id).Should().Be(LocalEndpoint);
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_another_local_endpoint_is_proposed()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, "http://embeddings:80/v1")));

        ErrorOf(proposed).Should().BeNull();
        JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_proposed_with_billing_off()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, PublicEndpoint)));

        ErrorOf(proposed).Should().BeNull();
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_proposed_once_an_admin_price_replaces_the_zero()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);
        // An admin price on every unit closes the seed's zero rows (InferenceProviderAdminService.CreatePriceAsync).
        DateTime adminFrom = DateTime.UtcNow;
        foreach (ModelCapabilityPrice zero in h.Db.ModelCapabilityPrices.Where(p => p.ModelCapabilityId == row.Id).ToList())
        {
            zero.EffectiveTo = adminFrom;
        }

        foreach (UsageKind unit in ChatUnits)
        {
            h.Db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = row.Id,
                Unit = unit,
                CostUsdPerUnit = 0.000002m,
                EffectiveFrom = adminFrom,
            });
        }

        h.Db.SaveChanges();

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateEndpoint(row.Id, PublicEndpoint)));

        ErrorOf(proposed).Should().BeNull();
        JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>The provider-edit arguments with only the kind (and token) set.</summary>
    private static AIFunctionArguments UpdateKind(Guid id, string kind, string? token = null)
    {
        AIFunctionArguments args = Update(id, isEnabled: null);
        args["kind"] = kind;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private static InferenceProviderKind StoredKind(InferenceApiDbContext db, Guid id) =>
        db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Include(c => c.Model).ThenInclude(m => m.Provider)
            .Single(c => c.Id == id).Model.Provider.Kind;

    /// <summary>
    /// The kind alone is judged: the endpoint stays hosted (a row that already holds a zero is the case the gate must still
    /// refuse), and only the kind moves away from OpenAICompatible/OpenJev.
    /// </summary>
    [Fact]
    public async Task Changing_a_zero_priced_row_to_a_kind_that_is_not_zero_priced_is_refused_at_propose_and_apply()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db, endpoint: PublicEndpoint);

        string refusedProposal = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateKind(row.Id, "DeepSeek")));
        ErrorOf(refusedProposal).Should().Contain("priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.");

        // A token minted while billing was off is no way around it at apply time.
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateKind(row.Id, "DeepSeek")));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);

        h.Db.ResetSaveCount();
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(UpdateKind(row.Id, "DeepSeek", token)));
        ErrorOf(applied).Should().Contain("priced at zero as a self-hosted default");
        h.Db.SaveCount.Should().Be(0);
        StoredKind(h.Db, row.Id).Should().Be(InferenceProviderKind.OpenAICompatible);
    }

    [Fact]
    public async Task Changing_a_self_hosted_decision_to_openjev_on_a_local_endpoint_is_proposed()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db, InferenceProviderCapability.Decision);

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateKind(row.Id, "OpenJev")));

        ErrorOf(proposed).Should().BeNull();
        JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
    }

    // ------------------------------------------------------------------ T-038 SEC-18: a model-code or capability edit is judged by the same gate

    private const string IdentityRefusal = "This model is priced at zero as a self-hosted default; add a price for it before changing its model.";
    private const string SelfHostedRefusal = "priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.";

    /// <summary>The provider-edit arguments with only the model code (and token) set.</summary>
    private static AIFunctionArguments UpdateModelName(Guid id, string modelName, string? token = null)
    {
        AIFunctionArguments args = Update(id, isEnabled: null);
        args["modelName"] = modelName;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    /// <summary>The provider-edit arguments with only the capability (and token) set.</summary>
    private static AIFunctionArguments ChangeCapability(Guid id, string capability, string? token = null)
    {
        AIFunctionArguments args = Update(id, isEnabled: null);
        args["capability"] = capability;
        if (token is not null) args["confirmationToken"] = token;
        return args;
    }

    private static string StoredModelName(InferenceApiDbContext db, Guid id) =>
        db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Include(c => c.Model).Single(c => c.Id == id).Model.ModelCode;

    private static InferenceProviderCapability StoredCapability(InferenceApiDbContext db, Guid id) =>
        db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Single(c => c.Id == id).Capability;

    [Fact]
    public async Task Renaming_a_zero_priced_self_hosted_model_code_to_a_hosted_one_is_refused_at_propose_and_apply_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);

        string refusedProposal = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateModelName(row.Id, "openai-gpt-x")));
        ErrorOf(refusedProposal).Should().Contain(IdentityRefusal);

        // A token minted while billing was off is no way around it at apply time.
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(UpdateModelName(row.Id, "openai-gpt-x")));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);

        h.Db.ResetSaveCount();
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(UpdateModelName(row.Id, "openai-gpt-x", token)));
        ErrorOf(applied).Should().Contain(IdentityRefusal);
        h.Db.SaveCount.Should().Be(0);
        StoredModelName(h.Db, row.Id).Should().Be("local");
    }

    [Fact]
    public async Task Changing_a_zero_priced_self_hosted_capability_to_another_capability_is_refused_at_propose_and_apply_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db);

        string refusedProposal = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(ChangeCapability(row.Id, "Embedding")));
        ErrorOf(refusedProposal).Should().Contain(IdentityRefusal);

        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(ChangeCapability(row.Id, "Embedding")));
        string token = JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString()!;
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);

        h.Db.ResetSaveCount();
        string applied = Text(await Tool(h, "ApplyUpdateInferenceProvider").InvokeAsync(ChangeCapability(row.Id, "Embedding", token)));
        ErrorOf(applied).Should().Contain(IdentityRefusal);
        h.Db.SaveCount.Should().Be(0);
        StoredCapability(h.Db, row.Id).Should().Be(InferenceProviderCapability.Chat);
    }

    [Fact]
    public async Task Renaming_a_zero_priced_self_hosted_model_code_to_another_self_hosted_seed_entry_is_proposed()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedSelfHostedZero(h.Db, InferenceProviderCapability.Transcription, modelName: "Systran/faster-whisper-small",
            units: [UsageKind.AsrAudioSeconds]);

        string proposed = Text(await Tool(h, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateModelName(row.Id, "Systran/faster-whisper-large-v3")));

        ErrorOf(proposed).Should().BeNull();
        JsonDocument.Parse(proposed).RootElement.GetProperty("confirmationToken").GetString().Should().NotBeNullOrEmpty();
    }
}
