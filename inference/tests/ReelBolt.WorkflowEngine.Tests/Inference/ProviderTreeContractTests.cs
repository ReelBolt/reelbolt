#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The platform model catalog API (docs/design/model-catalog.md §6; replaces the interim flat contract): provider
/// accounts → models → capabilities → prices under <c>/api/v1/inference-providers</c>. The org twin, its authority and
/// its 402/404 rules are pinned in <c>OrgInferenceProvidersControllerTests</c>; the kind/capability matrix and the
/// endpoint gate in the per-kind controller suites.
/// </summary>
public class ProviderTreeContractTests
{
    private const string PublicEndpoint = "https://93.184.216.7/v1";

    /// <summary>Reversible, so key-equality and "the stored value is not the plaintext" are both observable.</summary>
    private sealed class ReversibleSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            if (ciphertext is null || !ciphertext.StartsWith("enc:", StringComparison.Ordinal)) return false;
            plaintext = ciphertext[4..];
            return true;
        }
    }

    private sealed class Admin(bool isAdmin = true) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private sealed class CountingPrices : IModelPriceService
    {
        public int Invalidations;
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) => default;
        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default) => default;
        public void Invalidate() => Invalidations++;
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required CountingPrices Prices { get; init; }
        public required Mock<IInferenceProviderResolver> Resolver { get; init; }

        public InferenceProvidersController Controller(bool isAdmin = true) => new(
            Db, new Admin(isAdmin),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, new ReversibleSecrets(),
            NullLogger<InferenceProvidersController>.Instance,
            providerResolver: Resolver.Object, prices: Prices);
    }

    private static Harness Create()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        return new Harness { Db = db, Prices = new CountingPrices(), Resolver = new Mock<IInferenceProviderResolver>() };
    }

    private static CreateModelRequest Model(string code, string name, params (string Capability, bool IsDefault)[] capabilities) =>
        new(code, name, true, [.. capabilities.Select(c => new CreateCapabilityRequest(c.Capability, c.IsDefault))]);

    private static CreateProviderRequest Account(string name, string kind = "OpenAICompatible", string? apiKey = "sk-live-1234",
        params CreateModelRequest[] models) => new(name, kind, PublicEndpoint, apiKey, null, true, false, [.. models]);

    private static async Task<ProviderResponse> CreateAsync(Harness h, CreateProviderRequest request) =>
        (await h.Controller().Create(request, CancellationToken.None)).Body();

    private static ModelCapabilityView SeedRow(Harness h, string name, Guid? org = null, bool isDefault = false,
        InferenceProviderKind kind = InferenceProviderKind.OpenAICompatible,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat, string model = "m")
    {
        ModelCapabilityView row = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            OrganizationId = org,
            Name = name,
            Kind = kind,
            Capability = capability,
            Endpoint = PublicEndpoint,
            ModelName = model,
            ApiKeyEncrypted = "enc:secret-key",
            ApiKeyLastFour = "-key",
            IsDefault = isDefault
        };
        h.Db.AddFlat(row);
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
        return row;
    }

    private static ModelCapabilityView SeedSystemCompute(Harness h)
    {
        ModelCapabilityView compute = new()
        {
            Id = WellKnownModelCatalog.ComputeCapabilityId,
            ProviderId = WellKnownModelCatalog.ComputeProviderId,
            ModelId = WellKnownModelCatalog.ComputeModelId,
            ProviderName = WellKnownModelCatalog.ComputeProviderName,
            Name = WellKnownModelCatalog.ComputeModelDisplayName,
            ModelName = WellKnownModelCatalog.ComputeModelCode,
            Capability = InferenceProviderCapability.Compute,
            Endpoint = "internal://compute",
            IsDefault = true,
            IsSystem = true
        };
        h.Db.AddFlat(compute);
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
        return compute;
    }

    // ------------------------------------------------------------------ authority

    [Fact]
    public async Task Every_platform_write_and_read_is_platform_admin_only_and_writes_nothing_otherwise()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "existing");
        InferenceProvidersController c = h.Controller(isAdmin: false);

        (await c.List(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Get(row.ProviderId, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Create(Account("new", models: [Model("m", "new", ("Chat", false))]), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Update(row.ProviderId, new UpdateProviderRequest(Name: "x"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Delete(row.ProviderId, default)).Should().BeOfType<ForbidResult>();
        (await c.CreateModel(row.ProviderId, Model("m2", "m2"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(DisplayName: "x"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.DeleteModel(row.ProviderId, row.ModelId, default)).Should().BeOfType<ForbidResult>();
        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Vision"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateCapability(row.ProviderId, row.ModelId, row.Id, new UpdateCapabilityRequest(IsDefault: true), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.DeleteCapability(row.ProviderId, row.ModelId, row.Id, default)).Should().BeOfType<ForbidResult>();
        (await c.TestCapability(row.ProviderId, row.ModelId, row.Id, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.TestUnsaved(new TestProviderRequest(ProviderId: row.ProviderId), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.CreateImageTools(row.ProviderId, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m), default))
            .Result.Should().BeOfType<ForbidResult>();
        (await c.RetirePrice(row.ProviderId, row.ModelId, row.Id, Guid.NewGuid(), null, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.GetConsolidationReport(null!, default)).Result.Should().BeOfType<ForbidResult>();

        (await h.Db.FlatProviders().SingleAsync()).Name.Should().Be("existing");
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await h.Db.ModelCapabilityPrices.CountAsync()).Should().Be(0);
    }

    // ------------------------------------------------------------------ create and read the tree

    [Fact]
    public async Task Create_with_models_makes_one_account_and_returns_the_whole_tree_with_prices()
    {
        Harness h = Create();

        ActionResult<ProviderResponse> result = await h.Controller().Create(
            Account("Anthropic", "Anthropic", "sk-ant-1234",
                Model("claude-opus-5-5", "Claude Opus", ("Chat", true), ("Vision", false)),
                Model("claude-unknown-x", "Claude X", ("Chat", false))),
            CancellationToken.None);

        ProviderResponse body = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<ProviderResponse>().Subject;
        body.OrganizationId.Should().BeNull();
        body.Kind.Should().Be("Anthropic");
        body.HasApiKey.Should().BeTrue();
        body.ApiKeyLastFour.Should().Be("1234");
        body.IsSystem.Should().BeFalse();
        body.Models.Select(m => m.DisplayName).Should().Equal("Claude Opus", "Claude X");
        ModelResponse opus = body.Models[0];
        opus.Capabilities.Select(c => (c.Capability, c.IsDefault)).Should().Equal(("Chat", true), ("Vision", false));
        opus.Capabilities.Should().OnlyContain(c => c.PriceSource == "Table" && c.Prices != null);
        opus.Capabilities[0].Prices!.Select(p => p.Unit).Should().Contain(["LlmInputTokens", "LlmOutputTokens"],
            "a new platform capability is pre-priced from the price book");
        body.Models[1].Capabilities.Single().Prices.Should().BeEmpty("a model the book does not price stays a coverage gap");

        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await h.Db.InferenceModels.IgnoreQueryFilters().AllAsync(m => m.OrganizationId == null && m.ProviderId == body.Id)).Should().BeTrue();
        (await h.Db.ModelCapabilities.IgnoreQueryFilters().CountAsync(c => c.OrganizationId == null)).Should().Be(3);
        (await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).ApiKeyEncrypted.Should().Be("enc:sk-ant-1234");
        h.Resolver.Verify(r => r.InvalidateAll(), Times.Once);
        h.Prices.Invalidations.Should().BeGreaterThan(0, "auto-pricing drops the price cache");
    }

    [Fact]
    public async Task A_Higgsfield_capability_is_priced_by_the_vendor_quote()
    {
        Harness h = Create();

        ProviderResponse body = await CreateAsync(h, new CreateProviderRequest("Higgsfield", "Higgsfield", "https://93.184.216.9",
            "id:secret", Models: [Model("kling-video/v3.0/std/text-to-video", "Kling", ("VideoGeneration", false))]));

        body.Flat().Prices.Should().NotBeNull();
        body.Models.Single().Capabilities.Single().PriceSource.Should().Be("Oracle");
    }

    [Fact]
    public async Task Get_and_list_show_the_system_compute_provider_read_only_with_its_prices()
    {
        Harness h = Create();
        SeedSystemCompute(h);
        SeedRow(h, "Chat");

        List<ProviderResponse> list = (await h.Controller().List(default)).Body();

        list.Select(p => (p.Name, p.IsSystem)).Should().Equal(("Chat", false), (WellKnownModelCatalog.ComputeProviderName, true));
        list[1].Models.Single().Capabilities.Single().Capability.Should().Be("Compute");
        (await h.Controller().Get(WellKnownModelCatalog.ComputeProviderId, default)).Result.Should().BeOfType<OkObjectResult>();
    }

    [Theory]
    [InlineData("Transcription", "Anthropic")]
    [InlineData("Compute", "OpenAICompatible")]
    [InlineData("Nonsense", "OpenAICompatible")]
    [InlineData("", "OpenAICompatible")]
    public async Task Create_refuses_a_capability_the_kind_cannot_serve_or_that_no_admin_may_add(string capability, string kind)
    {
        Harness h = Create();

        ActionResult<ProviderResponse> result = await h.Controller().Create(
            Account("p", kind, models: [Model("m", "m", (capability, false))]), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_refuses_a_model_listing_one_capability_twice_or_two_defaults_for_one_capability()
    {
        Harness h = Create();
        InferenceProvidersController c = h.Controller();

        (await c.Create(Account("p", models: [Model("m", "m", ("Chat", false), ("Chat", false))]), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Create(Account("p", models: [Model("a", "a", ("Chat", true)), Model("b", "b", ("Chat", true))]), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Create(Account("p", models: [Model("a", "same", ("Chat", false)), Model("b", "same", ("Vision", false))]), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Provider_and_model_names_are_unique_in_the_scope_with_a_plain_409()
    {
        Harness h = Create();
        ModelCapabilityView existing = SeedRow(h, "taken");
        InferenceProvidersController c = h.Controller();

        ActionResult<ProviderResponse> sameProvider = await c.Create(Account("taken", models: [Model("m", "other", ("Chat", false))]), default);
        sameProvider.Result.Should().BeOfType<ConflictObjectResult>().Subject.Value!.ToString().Should().Contain("taken");
        (await c.Create(Account("free", models: [Model("m", "taken", ("Chat", false))]), default))
            .Result.Should().BeOfType<ConflictObjectResult>();
        (await c.CreateModel(existing.ProviderId, Model("m2", "taken"), default)).Result.Should().BeOfType<ConflictObjectResult>();
        ModelResponse second = (await c.CreateModel(existing.ProviderId, Model("m2", "second"), default)).Body();
        (await c.UpdateModel(existing.ProviderId, second.Id, new UpdateModelRequest(DisplayName: "taken"), default))
            .Result.Should().BeOfType<ConflictObjectResult>();
        (await c.CreateCapability(existing.ProviderId, existing.ModelId, new CreateCapabilityRequest("Chat"), default))
            .Result.Should().BeOfType<ConflictObjectResult>("a model serves each capability once");
    }

    [Fact]
    public async Task New_names_and_model_codes_over_200_characters_are_a_400_although_the_columns_are_unbounded()
    {
        Harness h = Create();
        ModelCapabilityView existing = SeedRow(h, "existing");
        InferenceProvidersController c = h.Controller();
        string tooLong = new('x', 201);

        (await c.Create(Account(tooLong, models: [Model("m", "fine", ("Chat", false))]), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Create(Account("fine", models: [Model(tooLong, "fine", ("Chat", false))]), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Create(Account("fine", models: [Model("m", tooLong, ("Chat", false))]), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Update(existing.ProviderId, new UpdateProviderRequest(Name: tooLong), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateModel(existing.ProviderId, Model(tooLong, "fine"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.UpdateModel(existing.ProviderId, existing.ModelId, new UpdateModelRequest(ModelCode: tooLong), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.UpdateModel(existing.ProviderId, existing.ModelId, new UpdateModelRequest(DisplayName: tooLong), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).Name.Should().Be("existing");
    }

    // ------------------------------------------------------------------ models and capabilities

    [Fact]
    public async Task A_model_added_under_an_account_inherits_its_kind_rules_and_its_organization()
    {
        Harness h = Create();
        ModelCapabilityView anthropic = SeedRow(h, "Anthropic", kind: InferenceProviderKind.Anthropic);
        InferenceProvidersController c = h.Controller();

        (await c.CreateModel(anthropic.ProviderId, Model("claude-x", "x", ("Transcription", false)), default))
            .Result.Should().BeOfType<BadRequestObjectResult>("Anthropic has no speech-to-text API");
        ModelResponse added = (await c.CreateModel(anthropic.ProviderId, Model("claude-opus-5-5", "Opus", ("Chat", false)), default)).Body();

        InferenceModel stored = await h.Db.InferenceModels.IgnoreQueryFilters().SingleAsync(m => m.Id == added.Id);
        stored.ProviderId.Should().Be(anthropic.ProviderId);
        stored.OrganizationId.Should().BeNull();
        added.Capabilities.Single().Prices.Should().NotBeEmpty("platform capabilities are pre-priced");
    }

    [Fact]
    public async Task A_new_model_code_must_still_serve_every_capability_of_the_model()
    {
        Harness h = Create();
        ModelCapabilityView upscale = SeedRow(h, "fal upscale", kind: InferenceProviderKind.FalAi,
            capability: InferenceProviderCapability.ImageUpscale, model: "fal-ai/seedvr/upscale/image");

        ActionResult<ModelResponse> result = await h.Controller().UpdateModel(upscale.ProviderId, upscale.ModelId,
            new UpdateModelRequest(ModelCode: "fal-ai/image-apps-v2/relighting"), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).ModelName.Should().Be("fal-ai/seedvr/upscale/image");
    }

    [Fact]
    public async Task Promoting_a_default_clears_only_the_other_default_of_the_same_capability_in_the_platform()
    {
        Harness h = Create();
        ModelCapabilityView oldDefault = SeedRow(h, "old", isDefault: true);
        ModelCapabilityView visionDefault = SeedRow(h, "vision", isDefault: true, capability: InferenceProviderCapability.Vision);
        ModelCapabilityView orgDefault = SeedRow(h, "org", org: Guid.NewGuid(), isDefault: true);
        ModelCapabilityView candidate = SeedRow(h, "cand");

        CapabilityResponse promoted = (await h.Controller().UpdateCapability(candidate.ProviderId, candidate.ModelId, candidate.Id,
            new UpdateCapabilityRequest(IsDefault: true), CancellationToken.None)).Body();

        promoted.IsDefault.Should().BeTrue();
        async Task<bool> IsDefault(Guid id) => (await h.Db.FlatProviders().SingleAsync(p => p.Id == id)).IsDefault;
        (await IsDefault(oldDefault.Id)).Should().BeFalse();
        (await IsDefault(visionDefault.Id)).Should().BeTrue();
        (await IsDefault(orgDefault.Id)).Should().BeTrue();
        (await IsDefault(candidate.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Deletes_are_409_while_a_default_sits_underneath_and_cascade_down_otherwise()
    {
        Harness h = Create();
        InferenceProvidersController c = h.Controller();
        ProviderResponse account = await CreateAsync(h, Account("acct", models:
            [Model("a", "A", ("Chat", true), ("Vision", false)), Model("b", "B", ("Embedding", false))]));
        ModelResponse a = account.Models.Single(m => m.DisplayName == "A");
        ModelResponse b = account.Models.Single(m => m.DisplayName == "B");
        await c.CreatePrice(account.Id, a.Id, a.Capabilities[1].Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 0.000001m), default);

        (await c.Delete(account.Id, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.DeleteModel(account.Id, a.Id, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.DeleteCapability(account.Id, a.Id, a.Capabilities.Single(x => x.IsDefault).Id, default)).Should().BeOfType<ConflictObjectResult>();

        (await c.DeleteCapability(account.Id, a.Id, a.Capabilities.Single(x => !x.IsDefault).Id, default)).Should().BeOfType<NoContentResult>();
        (await h.Db.ModelCapabilityPrices.AnyAsync(p => p.ModelCapabilityId == a.Capabilities[1].Id)).Should().BeFalse("prices go with their capability");
        (await c.DeleteModel(account.Id, b.Id, default)).Should().BeOfType<NoContentResult>();
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(1);

        await c.UpdateCapability(account.Id, a.Id, a.Capabilities[0].Id, new UpdateCapabilityRequest(IsDefault: false), default);
        (await c.Delete(account.Id, default)).Should().BeOfType<NoContentResult>();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.ModelCapabilities.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await h.Db.ModelCapabilityPrices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_price_whose_notes_start_with_the_seed_marker_is_400_because_that_prefix_marks_catalog_seed_rows()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "a");
        InferenceProvidersController c = h.Controller();

        ActionResult<PriceResponse> seeded = await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m, Notes: "  seed: hand-made"), default);
        seeded.Result.Should().BeOfType<BadRequestObjectResult>()
            .Which.Value!.ToString().Should().Contain("Notes cannot start with 'seed:'");
        (await h.Db.ModelCapabilityPrices.AnyAsync()).Should().BeFalse();

        // T-110 (INFO): the marker is matched case-insensitively, after trimming.
        foreach (string forged in new[] { "SEED: shouting", " Seed: capitalised" })
        {
            (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
                    new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m, Notes: forged), default))
                .Result.Should().BeOfType<BadRequestObjectResult>(forged);
        }

        (await h.Db.ModelCapabilityPrices.AnyAsync()).Should().BeFalse();

        PriceResponse fine = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m, Notes: "Seeded by hand: not the marker"), default)).Body();
        fine.Notes.Should().Be("Seeded by hand: not the marker");
    }

    [Fact]
    public async Task The_system_provider_cannot_be_changed_or_deleted_at_any_level_but_its_capability_can_be_priced()
    {
        Harness h = Create();
        SeedSystemCompute(h);
        InferenceProvidersController c = h.Controller();
        Guid p = WellKnownModelCatalog.ComputeProviderId, m = WellKnownModelCatalog.ComputeModelId, cap = WellKnownModelCatalog.ComputeCapabilityId;

        (await c.Update(p, new UpdateProviderRequest(Name: "mine"), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.Delete(p, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.CreateModel(p, Model("x", "x", ("Chat", false)), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.UpdateModel(p, m, new UpdateModelRequest(DisplayName: "x"), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.DeleteModel(p, m, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.CreateCapability(p, m, new CreateCapabilityRequest("Chat"), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.UpdateCapability(p, m, cap, new UpdateCapabilityRequest(IsEnabled: false), default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await c.DeleteCapability(p, m, cap, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.TestCapability(p, m, cap, default)).Result.Should().BeOfType<BadRequestObjectResult>("there is no connection to test");

        PriceResponse price = (await c.CreatePrice(p, m, cap,
            new CreatePriceRequest("RenderOutputSeconds", "1080p", 0.00002m), default)).Body();
        price.Variant.Should().Be("1080p");
        h.Db.ChangeTracker.Clear();
        ModelCapabilityView stored = await h.Db.FlatProviders().SingleAsync();
        stored.Name.Should().Be(WellKnownModelCatalog.ComputeModelDisplayName);
        stored.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Mismatched_parents_and_org_rows_are_404_on_every_platform_route()
    {
        Harness h = Create();
        ModelCapabilityView a = SeedRow(h, "a");
        ModelCapabilityView b = SeedRow(h, "b");
        ModelCapabilityView org = SeedRow(h, "org", org: Guid.NewGuid());
        InferenceProvidersController c = h.Controller();
        PriceResponse bPrice = (await c.CreatePrice(b.ProviderId, b.ModelId, b.Id,
            new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 0.000001m), default)).Body();

        // b's model under a's provider; b's capability under a's model; b's price under a's capability.
        (await c.UpdateModel(a.ProviderId, b.ModelId, new UpdateModelRequest(DisplayName: "moved"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.DeleteModel(a.ProviderId, b.ModelId, default)).Should().BeOfType<NotFoundResult>();
        (await c.CreateCapability(a.ProviderId, b.ModelId, new CreateCapabilityRequest("Vision"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(a.ProviderId, a.ModelId, b.Id, new UpdateCapabilityRequest(IsEnabled: false), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(a.ProviderId, b.ModelId, b.Id, new UpdateCapabilityRequest(IsEnabled: false), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.CreatePrice(a.ProviderId, b.ModelId, b.Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m), default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await c.RetirePrice(a.ProviderId, b.ModelId, b.Id, bPrice.Id, null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.DeleteCapability(a.ProviderId, a.ModelId, b.Id, default)).Should().BeOfType<NotFoundResult>();
        (await c.TestCapability(a.ProviderId, a.ModelId, b.Id, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.CreatePrice(a.ProviderId, a.ModelId, b.Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m), default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await c.RetirePrice(a.ProviderId, a.ModelId, a.Id, bPrice.Id, null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.RetirePrice(b.ProviderId, b.ModelId, b.Id, Guid.NewGuid(), null, default)).Result.Should().BeOfType<NotFoundResult>();

        // An organization's own rows are not the platform's.
        (await c.Get(org.ProviderId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.Update(org.ProviderId, new UpdateProviderRequest(Name: "taken over"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.CreateModel(org.ProviderId, Model("x", "x"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(org.ProviderId, org.ModelId, org.Id, new UpdateCapabilityRequest(IsDefault: true), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.TestUnsaved(new TestProviderRequest(ProviderId: org.ProviderId), default)).Result.Should().BeOfType<NotFoundObjectResult>();

        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().CountAsync()).Should().Be(3);
        (await h.Db.FlatProviders().AllAsync(r => r.IsEnabled && !r.IsDefault && r.Name != "moved" && r.ProviderName != "taken over")).Should().BeTrue();
        (await h.Db.ModelCapabilityPrices.SingleAsync()).EffectiveTo.Should().BeNull();
    }

    // ------------------------------------------------------------------ provider account updates

    [Fact]
    public async Task Update_key_semantics_omit_keeps_empty_clears_value_replaces_and_the_key_is_never_returned()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "keys");
        InferenceProvidersController c = h.Controller();

        ProviderResponse kept = (await c.Update(row.ProviderId, new UpdateProviderRequest(TimeoutSeconds: 30), default)).Body();
        kept.HasApiKey.Should().BeTrue();
        kept.TimeoutSeconds.Should().Be(30);

        ProviderResponse replaced = (await c.Update(row.ProviderId, new UpdateProviderRequest(ApiKey: "  sk-new-9876 "), default)).Body();
        replaced.ApiKeyLastFour.Should().Be("9876");
        (await h.Db.InferenceProviders.IgnoreQueryFilters().AsNoTracking().SingleAsync()).ApiKeyEncrypted.Should().Be("enc:sk-new-9876");
        System.Text.Json.JsonSerializer.Serialize(replaced).Should().NotContain("sk-new").And.NotContain("enc:");

        ProviderResponse cleared = (await c.Update(row.ProviderId, new UpdateProviderRequest(ApiKey: ""), default)).Body();
        cleared.HasApiKey.Should().BeFalse();
        cleared.ApiKeyLastFour.Should().BeNull();

        (await c.Update(row.ProviderId, new UpdateProviderRequest(ApiKey: "sk-shortened…"), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Update(row.ProviderId, new UpdateProviderRequest(TimeoutSeconds: 0), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Update(row.ProviderId, new UpdateProviderRequest(Name: " "), default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PersonalOAuthMode_is_for_Anthropic_accounts_only()
    {
        Harness h = Create();
        ModelCapabilityView openAi = SeedRow(h, "oai");
        ModelCapabilityView anthropic = SeedRow(h, "ant", kind: InferenceProviderKind.Anthropic);
        InferenceProvidersController c = h.Controller();

        (await c.Update(openAi.ProviderId, new UpdateProviderRequest(PersonalOAuthMode: true), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Update(anthropic.ProviderId, new UpdateProviderRequest(PersonalOAuthMode: true), default)).Body().PersonalOAuthMode.Should().BeTrue();
        (await c.Create(new CreateProviderRequest("oauth", "OpenAICompatible", PublicEndpoint, PersonalOAuthMode: true), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ------------------------------------------------------------------ image tools

    [Fact]
    public async Task Image_tools_become_models_under_the_same_fal_account_named_after_it_and_never_steal_a_default()
    {
        Harness h = Create();
        ProviderResponse fal = await CreateAsync(h, new CreateProviderRequest("fal", "FalAi", "https://93.184.216.20", "fal-secret-1234",
            Models: [Model(ImageJobCapabilities.FalDefaultFor(InferenceProviderCapability.ImageEdit)!.Id, "fal - Edit from a description", ("ImageEdit", true))]));
        SeedRow(h, "bfl", isDefault: true, kind: InferenceProviderKind.BlackForestLabs,
            capability: InferenceProviderCapability.ImageGeneration, model: "flux-2-pro");
        InferenceProvidersController c = h.Controller();

        List<ModelResponse> created = (await c.CreateImageTools(fal.Id, default)).Body();

        created.Should().HaveCount(ImageJobCapabilities.All.Count - 1, "the account already serves the instruction edit");
        created.Select(m => m.DisplayName).Should().Contain("fal - Upscale").And.Contain("fal - Create pictures");
        created.Should().OnlyContain(m => m.Capabilities.Count == 1);
        created.Single(m => m.Capabilities[0].Capability == "ImageGeneration").Capabilities[0].IsDefault.Should().BeFalse("bfl stays the default");
        created.Single(m => m.Capabilities[0].Capability == "ImageErase").Capabilities[0].IsDefault.Should().BeTrue();
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => p.Kind == InferenceProviderKind.FalAi)).Should().Be(1, "no key is copied to a second account");
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync(m => m.ProviderId == fal.Id)).Should().Be(ImageJobCapabilities.All.Count);

        (await c.CreateImageTools(fal.Id, default)).Body().Should().BeEmpty("a second click has nothing left to add");
    }

    [Fact]
    public async Task Image_tools_need_a_fal_account_with_a_stored_key()
    {
        Harness h = Create();
        ModelCapabilityView openAi = SeedRow(h, "oai");
        ProviderResponse keyless = await CreateAsync(h, new CreateProviderRequest("fal keyless", "FalAi", "https://93.184.216.20",
            Models: [Model("fal-ai/sam-3-1/image", "sam", ("Segmentation", false))]));
        InferenceProvidersController c = h.Controller();

        (await c.CreateImageTools(openAi.ProviderId, default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateImageTools(keyless.Id, default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateImageTools(Guid.NewGuid(), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ------------------------------------------------------------------ unsaved test with a saved key

    [Fact]
    public async Task An_unsaved_test_that_reuses_a_saved_key_is_pinned_to_the_saved_kind_and_endpoint()
    {
        // The stored key may only ever be sent where it is stored for: with providerId and no key, the request's
        // endpoint is ignored. A private address there would otherwise be a key-exfiltration route.
        Harness h = Create();
        ModelCapabilityView saved = SeedRow(h, "saved", kind: InferenceProviderKind.DeepSeek);
        Mock<IChatClientFactory> chat = new();
        InferenceProvidersController c = new(h.Db, new Admin(), chat.Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, new ReversibleSecrets(), NullLogger<InferenceProvidersController>.Instance);

        // Another origin with no key is refused outright; no client is ever built for it.
        (await c.TestUnsaved(new TestProviderRequest(Kind: "OpenAICompatible", Endpoint: "https://93.184.217.66/steal",
            ModelCode: "m", Capability: "Chat", ProviderId: saved.ProviderId), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        chat.Verify(f => f.Get(It.IsAny<ResolvedInferenceProvider>()), Times.Never);

        // Without an endpoint (or on the same origin) the saved kind and endpoint are used, whatever the request says.
        await c.TestUnsaved(new TestProviderRequest(Kind: "OpenAICompatible", ModelCode: "m", Capability: "Chat",
            ProviderId: saved.ProviderId), default);
        chat.Verify(f => f.Get(It.Is<ResolvedInferenceProvider>(p =>
            p.Endpoint == PublicEndpoint && p.Kind == InferenceProviderKind.DeepSeek && p.ApiKey == "secret-key")), Times.Once);
    }

    // ------------------------------------------------------------------ prices

    [Fact]
    public async Task A_new_price_closes_the_open_one_of_the_same_unit_and_variant_and_refuses_the_same_instant()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "priced");
        InferenceProvidersController c = h.Controller();
        DateTime first = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), second = first.AddMonths(1);

        PriceResponse one = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", null, 0.000003m, EffectiveFrom: first), default)).Body();
        PriceResponse otherUnit = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmOutputTokens", null, 0.000015m, EffectiveFrom: first), default)).Body();
        PriceResponse variant = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", "batch", 0.0000015m, EffectiveFrom: first), default)).Body();
        PriceResponse two = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", null, 0.000004m, EffectiveFrom: second, Notes: "list price raised"), default)).Body();

        two.EffectiveTo.Should().BeNull();
        two.Notes.Should().Be("list price raised");
        Dictionary<Guid, ModelCapabilityPrice> rows = await h.Db.ModelCapabilityPrices.AsNoTracking().ToDictionaryAsync(p => p.Id);
        rows[one.Id].EffectiveTo.Should().Be(second);
        rows[otherUnit.Id].EffectiveTo.Should().BeNull("another unit is another series");
        rows[variant.Id].EffectiveTo.Should().BeNull("another variant is another series");

        (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
                new CreatePriceRequest("LlmInputTokens", null, 0.000005m, EffectiveFrom: second), default))
            .Result.Should().BeOfType<ConflictObjectResult>();
        h.Prices.Invalidations.Should().Be(4);

        List<ProviderResponse> tree = (await c.List(default)).Body();
        tree.Single().Flat().Prices!.Should().HaveCount(4);
    }

    [Theory]
    [InlineData("EmbeddingTokens", "0.1", null, null)]   // not a unit Chat meters
    [InlineData("NoSuchUnit", "0.1", null, null)]
    [InlineData("3", "0.1", null, null)]
    [InlineData("LlmInputTokens", "-0.1", null, null)]
    [InlineData("LlmInputTokens", "0.1", "-1", null)]
    [InlineData("LlmInputTokens", null, null, null)]
    [InlineData("LlmInputTokens", "0.1", null, "a-variant-that-is-longer-than-sixty-four-characters-and-so-cannot-be-stored")]
    public async Task Price_validation_refuses_bad_units_costs_overrides_and_variants(string unit, string? cost, string? overrideMillis, string? variant)
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "priced");

        ActionResult<PriceResponse> result = await h.Controller().CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest(unit, variant, cost is null ? null : decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture),
                overrideMillis is null ? null : decimal.Parse(overrideMillis, System.Globalization.CultureInfo.InvariantCulture)),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.ModelCapabilityPrices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Effective_credits_and_margin_follow_the_markup_and_the_credit_value_unless_overridden()
    {
        Harness h = Create();
        h.Db.PricingSettings.Add(new PricingSettings { Markup = 1.5m, CreditUsdValue = 0.006m });
        await h.Db.SaveChangesAsync();
        ModelCapabilityView row = SeedRow(h, "priced");
        InferenceProvidersController c = h.Controller();

        PriceResponse derived = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", null, 0.006m), default)).Body();
        PriceResponse overridden = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmOutputTokens", null, 0.006m, CreditsMillisPerUnitOverride: 2000m), default)).Body();
        PriceResponse free = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmCachedInputTokens", null, 0m), default)).Body();

        // 0.006 USD × 1.5 × 1000 ÷ 0.006 = 1500 millicredits; it charges 1.5 credits × 0.006 = 0.009 USD: a 50 % margin.
        derived.EffectiveCreditsMillisPerUnit.Should().Be(1500m);
        derived.EffectiveMarginPercent.Should().Be(50m);
        // An override wins: 2000 millicredits = 0.012 USD over a 0.006 cost is a 100 % margin.
        overridden.EffectiveCreditsMillisPerUnit.Should().Be(2000m);
        overridden.CreditsMillisPerUnitOverride.Should().Be(2000m);
        overridden.EffectiveMarginPercent.Should().Be(100m);
        free.EffectiveCreditsMillisPerUnit.Should().Be(0m);
        free.EffectiveMarginPercent.Should().BeNull("there is no margin on a zero cost");
    }

    [Fact]
    public async Task Retiring_a_price_sets_its_end_once_and_never_before_its_start()
    {
        Harness h = Create();
        ModelCapabilityView row = SeedRow(h, "priced");
        InferenceProvidersController c = h.Controller();
        DateTime start = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        PriceResponse price = (await c.CreatePrice(row.ProviderId, row.ModelId, row.Id,
            new CreatePriceRequest("LlmInputTokens", null, 0.000001m, EffectiveFrom: start), default)).Body();

        (await c.RetirePrice(row.ProviderId, row.ModelId, row.Id, price.Id, start.AddDays(-1), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        PriceResponse retired = (await c.RetirePrice(row.ProviderId, row.ModelId, row.Id, price.Id, start.AddDays(10), default)).Body();
        retired.EffectiveTo.Should().Be(start.AddDays(10));
        (await c.RetirePrice(row.ProviderId, row.ModelId, row.Id, price.Id, null, default)).Result.Should().BeOfType<ConflictObjectResult>();
        (await h.Db.ModelCapabilityPrices.SingleAsync()).EffectiveTo.Should().Be(start.AddDays(10));
    }

    // ------------------------------------------------------------------ consolidation report

    [Fact]
    public async Task The_consolidation_report_lists_platform_providers_that_share_one_account()
    {
        Harness h = Create();
        ProviderResponse first = await CreateAsync(h, Account("Anthropic", "Anthropic", "sk-ant-shared",
            Model("claude-opus-5-5", "Claude Chat", ("Chat", false))));
        ProviderResponse second = await CreateAsync(h, Account("Anthropic Vision", "Anthropic", "sk-ant-shared",
            Model("claude-opus-5-5-v", "Claude Vision", ("Vision", false))));
        await CreateAsync(h, Account("Other key", "Anthropic", "sk-ant-other", Model("claude-x", "Claude Other", ("Chat", false))));
        ModelCatalogUpgrader upgrader = new(h.Db, new ReversibleSecrets(), new ConfigurationBuilder().Build(),
            NullLogger<ModelCatalogUpgrader>.Instance);

        ConsolidationReportResponse report = (await h.Controller().GetConsolidationReport(upgrader, default)).Body();

        ConsolidationGroupResponse group = report.Groups.Should().ContainSingle().Subject;
        new[] { group.SurvivorId }.Concat(group.MergedProviderIds).Should().BeEquivalentTo([first.Id, second.Id]);
        group.MergedModelCount.Should().Be(1);
        (await h.Db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(3, "the report is read only");
    }
}
