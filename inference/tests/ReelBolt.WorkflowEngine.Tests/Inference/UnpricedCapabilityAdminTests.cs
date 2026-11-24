#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// The fail-closed pricing rule on the admin REST surface: with billing on (the resolver fails closed on unpriced models), a
/// PLATFORM capability can never be left enabled without an effective price — unless it is oracle-priced and the
/// oracle answers — a new one the price book cannot price is created switched off, and the step-editor pickers never
/// offer one. Organization (BYO) capabilities are never affected, and with billing off nothing changes.
/// </summary>
public sealed class UnpricedCapabilityAdminTests
{
    private const string PublicEndpoint = "https://93.184.216.34/v1";
    private const string LocalEndpoint = "http://whisper:8000/v1";

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = ciphertext?.StartsWith("enc:", StringComparison.Ordinal) == true ? ciphertext[4..] : string.Empty;
            return ciphertext is not null;
        }
    }

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Mock<IInferenceProviderResolver> Resolver { get; init; }
        public Mock<IVideoGenerationClientFactory> Videos { get; } = new();

        public InferenceProvidersController Controller() => new(
            Db, new Admin(),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, Videos.Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, new PlainSecrets(),
            NullLogger<InferenceProvidersController>.Instance,
            providerResolver: Resolver.Object);

        public InferenceProviderAdminService Service() => new(
            Db, new PlainSecrets(),
            new InferenceProviderTester(Db, new PlainSecrets(), new Mock<IChatClientFactory>().Object,
                new Mock<ITranscriptionClientFactory>().Object, new Mock<IEmbeddingClientFactory>().Object,
                new Mock<IDecisionClientFactory>().Object, Videos.Object, new Mock<ISpeechSynthesisClientFactory>().Object,
                NullLogger<InferenceProviderTester>.Instance),
            Resolver.Object);
    }

    private static Harness Create(bool billingOn = true)
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(billingOn);
        return new Harness { Db = db, Resolver = resolver };
    }

    private static ModelCapabilityView Seed(Harness h, string name, bool enabled, Guid? org = null,
        InferenceProviderKind kind = InferenceProviderKind.OpenAICompatible,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat,
        ModelPriceSource source = ModelPriceSource.Table, (DateTime From, DateTime? To)? price = null,
        UsageKind[]? pricedUnits = null, string? endpoint = null, string? modelCode = null)
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
            Endpoint = endpoint ?? PublicEndpoint,
            ModelName = modelCode ?? "model-" + name,
            ApiKeyEncrypted = "enc:key",
            IsEnabled = enabled,
            PriceSource = source
        };
        h.Db.AddFlat(row);
        if (price is { } p)
        {
            // Priced means every unit the capability meters is priced (the resolver's rule), unless a test asks for fewer.
            UsageKind[] units = pricedUnits
                                ?? (ReelBolt.Shared.Metering.ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? all)
                                    ? all
                                    : [UsageKind.LlmInputTokens]);
            foreach (UsageKind unit in units)
            {
                h.Db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = row.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.000001m,
                    EffectiveFrom = p.From,
                    EffectiveTo = p.To
                });
            }
        }

        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
        return row;
    }

    private bool StoredEnabled(Harness h, Guid id) =>
        h.Db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Single(c => c.Id == id).IsEnabled;

    private static JsonElement BodyOf(IActionResult? result)
    {
        BadRequestObjectResult bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        return JsonSerializer.SerializeToElement(bad.Value);
    }

    // ------------------------------------------------------------------ update (enable)

    [Fact]
    public async Task Enabling_an_unpriced_platform_capability_is_a_400_with_message_and_errors_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "unpriced", enabled: false);

        ActionResult<CapabilityResponse> result = await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true));

        JsonElement body = BodyOf(result.Result);
        body.GetProperty("message").GetString().Should().Contain("price");
        body.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
        StoredEnabled(h, row.Id).Should().BeFalse("a refusal writes nothing");
        h.Resolver.Verify(r => r.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task An_enabled_unpriced_capability_cannot_be_left_enabled_by_another_change()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "legacy", enabled: true, price: (DateTime.UtcNow.AddDays(-10), DateTime.UtcNow.AddDays(-1)));

        ActionResult<CapabilityResponse> result = await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsDefault: true));

        BodyOf(result.Result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        h.Db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking().Single(c => c.Id == row.Id).IsDefault.Should().BeFalse();

        // Switching it OFF is always allowed.
        (await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: false))).Body().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Enabling_a_capability_with_a_price_in_force_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "priced", enabled: false, price: (DateTime.UtcNow.AddDays(-1), null));

        (await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true))).Body().IsEnabled.Should().BeTrue();
        StoredEnabled(h, row.Id).Should().BeTrue();
    }

    /// <summary>
    /// T-026 (finding 7): the enable gate uses the resolver's "fully priced" rule. A chat capability with a price for
    /// its input tokens only would still be refused by the resolver, so it cannot be switched on either.
    /// </summary>
    [Fact]
    public async Task Enabling_a_partly_priced_capability_is_a_400_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "partly", enabled: false, price: (DateTime.UtcNow.AddDays(-1), null),
            pricedUnits: [UsageKind.LlmInputTokens]);

        ActionResult<CapabilityResponse> result = await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true));

        BodyOf(result.Result).GetProperty("message").GetString().Should().Contain("price");
        StoredEnabled(h, row.Id).Should().BeFalse("a refusal writes nothing");
    }

    [Fact]
    public async Task An_oracle_priced_capability_is_enabled_only_while_its_oracle_answers()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "higgs", enabled: false, kind: InferenceProviderKind.Higgsfield,
            capability: InferenceProviderCapability.VideoGeneration, source: ModelPriceSource.Oracle);

        Mock<IVideoGenerationClient> client = new();
        Mock<IVideoGenerationPriceOracle> oracle = client.As<IVideoGenerationPriceOracle>();
        oracle.Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("the oracle could not price it"));
        h.Videos.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);

        ActionResult<CapabilityResponse> refused = await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true));
        BodyOf(refused.Result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        StoredEnabled(h, row.Id).Should().BeFalse();

        oracle.Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VideoGenerationPriceQuery q, CancellationToken _) =>
                new VideoGenerationPriceQuote(q.Model, q.Resolution, q.Ratio, q.DurationSeconds, 0.1m, 0.1m * q.DurationSeconds, null,
                    VideoGenerationPriceSource.VendorEstimate));

        (await h.Controller().UpdateCapability(h.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true))).Body().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Organization_capabilities_and_billing_off_are_not_affected()
    {
        Harness on = Create();
        Guid org = Guid.NewGuid();
        ModelCapabilityView byo = Seed(on, "byo", enabled: false, org: org);
        AdminOutcome<CapabilityResponse> byoOutcome = await on.Service().UpdateCapabilityAsync(
            ProviderScope.Org(org), byo.ProviderId, byo.ModelId, byo.Id, new UpdateCapabilityRequest(IsEnabled: true), CancellationToken.None);
        byoOutcome.Status.Should().Be(AdminStatus.Ok);
        StoredEnabled(on, byo.Id).Should().BeTrue();

        Harness off = Create(billingOn: false);
        ModelCapabilityView row = Seed(off, "self-hosted", enabled: false);
        (await off.Controller().UpdateCapability(off.Db, row.Id, new UpdateCapabilityRequest(IsEnabled: true))).Body().IsEnabled.Should().BeTrue();
    }

    // ------------------------------------------------------------------ T-030: enabling a model or a provider

    private static void SetParents(Harness h, ModelCapabilityView row, bool modelEnabled, bool providerEnabled)
    {
        InferenceModel model = h.Db.InferenceModels.IgnoreQueryFilters().Single(m => m.Id == row.ModelId);
        InferenceProvider provider = h.Db.InferenceProviders.IgnoreQueryFilters().Single(p => p.Id == row.ProviderId);
        model.IsEnabled = modelEnabled;
        provider.IsEnabled = providerEnabled;
        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
    }

    private static bool ModelEnabled(Harness h, Guid modelId) =>
        h.Db.InferenceModels.IgnoreQueryFilters().AsNoTracking().Single(m => m.Id == modelId).IsEnabled;

    private static bool ProviderEnabled(Harness h, Guid providerId) =>
        h.Db.InferenceProviders.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == providerId).IsEnabled;

    [Fact]
    public async Task Enabling_a_model_whose_enabled_capability_is_unpriced_is_a_400_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "unpriced", enabled: true);
        SetParents(h, row, modelEnabled: false, providerEnabled: true);

        ActionResult<ModelResponse> result = await h.Controller().UpdateModel(
            row.ProviderId, row.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None);

        JsonElement body = BodyOf(result.Result);
        body.GetProperty("message").GetString().Should().Contain("unpriced").And.Contain("price");
        body.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
        ModelEnabled(h, row.ModelId).Should().BeFalse("a refusal writes nothing");
    }

    [Fact]
    public async Task Enabling_a_model_is_allowed_when_its_unpriced_capability_is_off_or_its_provider_is_off()
    {
        Harness h = Create();
        ModelCapabilityView off = Seed(h, "off", enabled: false);
        SetParents(h, off, modelEnabled: false, providerEnabled: true);
        (await h.Controller().UpdateModel(off.ProviderId, off.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue();

        ModelCapabilityView underOffProvider = Seed(h, "under-off-provider", enabled: true);
        SetParents(h, underOffProvider, modelEnabled: false, providerEnabled: false);
        (await h.Controller().UpdateModel(underOffProvider.ProviderId, underOffProvider.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue("nothing becomes usable while the provider account is off");
    }

    [Fact]
    public async Task Enabling_a_provider_whose_enabled_model_has_an_enabled_unpriced_capability_is_a_400_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "unpriced", enabled: true);
        SetParents(h, row, modelEnabled: true, providerEnabled: false);

        ActionResult<ProviderResponse> result = await h.Controller().Update(
            row.ProviderId, new UpdateProviderRequest(IsEnabled: true), CancellationToken.None);

        BodyOf(result.Result).GetProperty("message").GetString().Should().Contain("unpriced");
        ProviderEnabled(h, row.ProviderId).Should().BeFalse();

        // With the model off, the same account switches on: nothing unpriced becomes usable.
        SetParents(h, row, modelEnabled: false, providerEnabled: false);
        (await h.Controller().Update(row.ProviderId, new UpdateProviderRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enabling_a_model_whose_oracle_priced_capability_has_an_answering_oracle_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "higgs", enabled: true, kind: InferenceProviderKind.Higgsfield,
            capability: InferenceProviderCapability.VideoGeneration, source: ModelPriceSource.Oracle);
        SetParents(h, row, modelEnabled: false, providerEnabled: true);
        Mock<IVideoGenerationClient> client = new();
        Mock<IVideoGenerationPriceOracle> oracle = client.As<IVideoGenerationPriceOracle>();
        oracle.Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("no quote"));
        h.Videos.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>())).Returns(client.Object);

        ActionResult<ModelResponse> refused = await h.Controller().UpdateModel(
            row.ProviderId, row.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None);
        BodyOf(refused.Result).GetProperty("message").GetString().Should().Contain("price quote");

        oracle.Setup(o => o.QuoteAsync(It.IsAny<VideoGenerationPriceQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VideoGenerationPriceQuery q, CancellationToken _) =>
                new VideoGenerationPriceQuote(q.Model, q.Resolution, q.Ratio, q.DurationSeconds, 0.1m, 0.1m * q.DurationSeconds, null,
                    VideoGenerationPriceSource.VendorEstimate));
        (await h.Controller().UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Enabling_a_model_or_provider_with_billing_off_or_in_an_organization_is_not_affected()
    {
        Harness off = Create(billingOn: false);
        ModelCapabilityView row = Seed(off, "self-hosted", enabled: true);
        SetParents(off, row, modelEnabled: false, providerEnabled: false);
        (await off.Controller().UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue();
        (await off.Controller().Update(row.ProviderId, new UpdateProviderRequest(IsEnabled: true), CancellationToken.None))
            .Body().IsEnabled.Should().BeTrue();

        Harness on = Create();
        Guid org = Guid.NewGuid();
        ModelCapabilityView byo = Seed(on, "byo", enabled: true, org: org);
        SetParents(on, byo, modelEnabled: false, providerEnabled: true);
        (await on.Service().UpdateModelAsync(ProviderScope.Org(org), byo.ProviderId, byo.ModelId, new UpdateModelRequest(IsEnabled: true), CancellationToken.None))
            .Status.Should().Be(AdminStatus.Ok);
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task A_new_capability_the_price_book_cannot_price_is_created_disabled()
    {
        Harness h = Create();
        ModelCapabilityView sibling = Seed(h, "sibling", enabled: true, price: (DateTime.UtcNow.AddDays(-1), null));

        CapabilityResponse created = (await h.Controller().AddCapabilityBeside(h.Db, sibling.Id, "Vision")).Body();

        created.IsEnabled.Should().BeFalse("nothing priced it, so it must not be offered");
        created.Prices.Should().BeEmpty();
        StoredEnabled(h, created.Id).Should().BeFalse();
    }

    [Fact]
    public async Task A_new_provider_keeps_its_book_priced_capabilities_on_and_switches_the_unpriced_ones_off()
    {
        Harness h = Create();

        ProviderResponse body = (await h.Controller().Create(new CreateProviderRequest("Anthropic", "Anthropic", PublicEndpoint,
            "sk-ant-1234", null, true, false,
            [
                new CreateModelRequest("claude-opus-5-5", "Claude Opus", true, [new CreateCapabilityRequest("Chat", true)]),
                new CreateModelRequest("claude-unknown-x", "Claude X", true, [new CreateCapabilityRequest("Chat")])
            ]), CancellationToken.None)).Body();

        body.Models.Single(m => m.DisplayName == "Claude Opus").Capabilities.Single().IsEnabled.Should().BeTrue();
        body.Models.Single(m => m.DisplayName == "Claude X").Capabilities.Single().IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task With_billing_off_a_new_unpriced_capability_stays_as_requested()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView sibling = Seed(h, "sibling", enabled: true);

        (await h.Controller().AddCapabilityBeside(h.Db, sibling.Id, "Vision")).Body().IsEnabled.Should().BeTrue();
    }

    // ------------------------------------------------------------------ pickers

    [Fact]
    public async Task The_step_editor_picker_omits_unpriced_platform_capabilities_while_billing_is_on()
    {
        Harness h = Create();
        Guid org = Guid.NewGuid();
        ModelCapabilityView priced = Seed(h, "priced", enabled: true, price: (DateTime.UtcNow.AddDays(-1), null));
        ModelCapabilityView unpriced = Seed(h, "unpriced", enabled: true);
        ModelCapabilityView oracle = Seed(h, "oracle", enabled: true, kind: InferenceProviderKind.Higgsfield,
            capability: InferenceProviderCapability.VideoGeneration, source: ModelPriceSource.Oracle);
        ModelCapabilityView byo = Seed(h, "byo", enabled: true, org: org);

        List<SelectableProviderResponse> on = await h.Service().ListSelectableAsync(org, null, CancellationToken.None);
        on.Select(r => r.Id).Should().BeEquivalentTo([priced.Id, oracle.Id, byo.Id]);

        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        List<SelectableProviderResponse> off = await h.Service().ListSelectableAsync(org, null, CancellationToken.None);
        off.Select(r => r.Id).Should().Contain(unpriced.Id, "self-host keeps unpriced rows");
    }

    /// <summary>
    /// T-040 (item 5): the picker offers a table-priced platform capability only when it is fully priced (a price in force
    /// for every unit it meters, the resolver's rule), not with any one price; BYO and oracle-priced rows are unaffected.
    /// </summary>
    [Fact]
    public async Task The_step_editor_picker_omits_a_partly_priced_platform_capability_while_billing_is_on()
    {
        Harness h = Create();
        Guid org = Guid.NewGuid();
        ModelCapabilityView full = Seed(h, "full", enabled: true, price: (DateTime.UtcNow.AddDays(-1), null));
        ModelCapabilityView partial = Seed(h, "partial", enabled: true, price: (DateTime.UtcNow.AddDays(-1), null),
            pricedUnits: [UsageKind.LlmInputTokens]);
        ModelCapabilityView oracle = Seed(h, "oracle", enabled: true, kind: InferenceProviderKind.Higgsfield,
            capability: InferenceProviderCapability.VideoGeneration, source: ModelPriceSource.Oracle);
        ModelCapabilityView byo = Seed(h, "byo", enabled: true, org: org, price: (DateTime.UtcNow.AddDays(-1), null),
            pricedUnits: [UsageKind.LlmInputTokens]);

        List<SelectableProviderResponse> on = await h.Service().ListSelectableAsync(org, null, CancellationToken.None);
        on.Select(r => r.Id).Should().BeEquivalentTo([full.Id, oracle.Id, byo.Id], "the resolver would refuse the partly priced row");

        List<SelectableProviderResponse> chat = await h.Service().ListSelectableAsync(org, InferenceProviderCapability.Chat, CancellationToken.None);
        chat.Select(r => r.Id).Should().BeEquivalentTo([full.Id, byo.Id]);

        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(false);
        (await h.Service().ListSelectableAsync(org, null, CancellationToken.None)).Select(r => r.Id).Should().Contain(partial.Id);
    }

    // ------------------------------------------------------------------ T-037: a zero-priced self-hosted default never points at a hosted endpoint

    private static Task<AdminOutcome<ProviderResponse>> EditEndpointAsync(Harness h, Guid providerId, string endpoint) =>
        h.Service().UpdateProviderAsync(ProviderScope.Platform, providerId, new UpdateProviderRequest(Endpoint: endpoint, ApiKey: "sk-new-key-1234"), CancellationToken.None); // a new host needs a re-entered key

    /// <summary>A seed-owned zero row in force on the capability: what the seeder writes for a self-hosted default.</summary>
    private static void AddSeedZeroRow(Harness h, Guid capabilityId)
    {
        foreach (UsageKind unit in new[] { UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens })
        {
            h.Db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = capabilityId,
                Unit = unit,
                CostUsdPerUnit = 0m,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1),
                Notes = "seed: model-catalog 2026.10.17.1",
            });
        }

        h.Db.SaveChanges();
        h.Db.ChangeTracker.Clear();
    }

    private static string StoredEndpoint(Harness h, Guid providerId) =>
        h.Db.InferenceProviders.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == providerId).Endpoint;

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_a_409_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ProviderResponse> refused = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        refused.Status.Should().Be(AdminStatus.Conflict);
        JsonSerializer.SerializeToElement(refused.Body).GetProperty("error").GetString().Should().Contain(
            "is priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.");
        StoredEndpoint(h, row.ProviderId).Should().Be(LocalEndpoint, "a refusal writes nothing");
    }

    /// <summary>SEC-19: an endpoint whose host cannot be normalised is hosted, so the gate refuses it with the 409 and never throws.</summary>
    [Theory]
    [InlineData("http://api﹒openai﹒com/v1")]
    [InlineData("http://⒈example.com/v1")]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_an_unnormalisable_host_is_a_409_and_writes_nothing(string endpoint)
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ProviderResponse> refused = await EditEndpointAsync(h, row.ProviderId, endpoint);

        refused.Status.Should().Be(AdminStatus.Conflict);
        JsonSerializer.SerializeToElement(refused.Body).GetProperty("error").GetString().Should().Contain(
            "is priced at zero as a self-hosted default; add a price for it before pointing it at a hosted endpoint.");
        StoredEndpoint(h, row.ProviderId).Should().Be(LocalEndpoint, "a refusal writes nothing");
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_another_local_endpoint_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, "http://embeddings:80/v1");

        result.Status.Should().Be(AdminStatus.Ok);
        StoredEndpoint(h, row.ProviderId).Should().Be("http://embeddings:80/v1");
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_allowed_once_an_admin_price_replaces_the_zero()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint);
        AddSeedZeroRow(h, row.Id);
        foreach (UsageKind unit in new[] { UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens })
        {
            AdminOutcome<PriceResponse> price = await h.Service().CreatePriceAsync(row.ProviderId, row.ModelId, row.Id,
                new CreatePriceRequest(Unit: unit.ToString(), CostUsdPerUnit: 0.000002m), CancellationToken.None);
            price.Status.Should().Be(AdminStatus.Ok);
        }

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        result.Status.Should().Be(AdminStatus.Ok);
        StoredEndpoint(h, row.ProviderId).Should().Be(PublicEndpoint);
    }

    [Fact]
    public async Task Pointing_a_zero_priced_self_hosted_default_at_a_hosted_endpoint_is_allowed_with_billing_off()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        result.Status.Should().Be(AdminStatus.Ok);
    }

    [Fact]
    public async Task Pointing_a_provider_with_no_self_hosted_zero_at_a_hosted_endpoint_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "hosted", enabled: true, endpoint: LocalEndpoint);

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        result.Status.Should().Be(AdminStatus.Ok);
    }

    // ------------------------------------------------------------------ T-038 SEC-18: a model-code edit is judged by the same gate

    private const string SeedSelfHostedTranscription = "Systran/faster-whisper-small";
    private const string SeedSelfHostedTranscriptionLarge = "Systran/faster-whisper-large-v3";

    private static Task<AdminOutcome<ModelResponse>> EditModelCodeAsync(Harness h, ModelCapabilityView row, string code) =>
        h.Service().UpdateModelAsync(ProviderScope.Platform, row.ProviderId, row.ModelId, new UpdateModelRequest(ModelCode: code), CancellationToken.None);

    private static string StoredModelCode(Harness h, Guid modelId) =>
        h.Db.InferenceModels.IgnoreQueryFilters().AsNoTracking().Single(m => m.Id == modelId).ModelCode;

    [Fact]
    public async Task A_video_model_that_already_names_a_talking_head_can_be_renamed_with_its_code_unchanged_but_not_switched_to_one()
    {
        const string talking = "minimax/h3-max/lip-sync/image-to-video";
        Harness h = Create();
        ModelCapabilityView stale = Seed(h, "stale", enabled: true, kind: InferenceProviderKind.FalAi,
            capability: InferenceProviderCapability.VideoGeneration, modelCode: talking);

        AdminOutcome<ModelResponse> same = await h.Service().UpdateModelAsync(
            ProviderScope.Platform, stale.ProviderId, stale.ModelId, new UpdateModelRequest(ModelCode: talking, DisplayName: "Renamed"), CancellationToken.None);
        same.Status.Should().Be(AdminStatus.Ok);

        ModelCapabilityView plain = Seed(h, "plain", enabled: true, kind: InferenceProviderKind.FalAi,
            capability: InferenceProviderCapability.VideoGeneration, modelCode: "fal-ai/kling-video/v3/turbo/pro/image-to-video");
        (await EditModelCodeAsync(h, plain, talking)).Status.Should().Be(AdminStatus.BadRequest);
    }

    [Fact]
    public async Task Renaming_a_zero_priced_self_hosted_model_code_to_a_hosted_one_is_a_409_and_writes_nothing()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint,
            capability: InferenceProviderCapability.Transcription, modelCode: SeedSelfHostedTranscription);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ModelResponse> refused = await EditModelCodeAsync(h, row, "openai/whisper-1");

        refused.Status.Should().Be(AdminStatus.Conflict);
        JsonSerializer.SerializeToElement(refused.Body).GetProperty("error").GetString().Should().Contain(
            "This model is priced at zero as a self-hosted default; add a price for it before changing its model.");
        StoredModelCode(h, row.ModelId).Should().Be(SeedSelfHostedTranscription, "a refusal writes nothing");
    }

    [Fact]
    public async Task Renaming_a_zero_priced_self_hosted_model_code_to_another_self_hosted_seed_entry_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint,
            capability: InferenceProviderCapability.Transcription, modelCode: SeedSelfHostedTranscription);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ModelResponse> result = await EditModelCodeAsync(h, row, SeedSelfHostedTranscriptionLarge);

        result.Status.Should().Be(AdminStatus.Ok);
        StoredModelCode(h, row.ModelId).Should().Be(SeedSelfHostedTranscriptionLarge);
    }

    [Fact]
    public async Task Renaming_a_zero_priced_self_hosted_model_code_to_a_hosted_one_is_allowed_with_billing_off()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = Seed(h, "selfhosted", enabled: true, endpoint: LocalEndpoint,
            capability: InferenceProviderCapability.Transcription, modelCode: SeedSelfHostedTranscription);
        AddSeedZeroRow(h, row.Id);

        AdminOutcome<ModelResponse> result = await EditModelCodeAsync(h, row, "openai/whisper-1");

        result.Status.Should().Be(AdminStatus.Ok);
    }

    [Fact]
    public async Task Renaming_a_model_with_no_self_hosted_zero_to_a_hosted_code_is_allowed()
    {
        Harness h = Create();
        ModelCapabilityView row = Seed(h, "hosted", enabled: true, endpoint: LocalEndpoint,
            capability: InferenceProviderCapability.Transcription, modelCode: SeedSelfHostedTranscription);

        AdminOutcome<ModelResponse> result = await EditModelCodeAsync(h, row, "openai/whisper-1");

        result.Status.Should().Be(AdminStatus.Ok);
    }

    // ------------------------------------------------------------------ agent-web-search: the api.openai.com rule holds on an endpoint update

    [Fact]
    public async Task Pointing_an_OpenAICompatible_WebSearch_account_at_another_host_is_a_400_and_writes_nothing()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = Seed(h, "ws", enabled: true, capability: InferenceProviderCapability.WebSearch, endpoint: "https://api.openai.com/v1");

        AdminOutcome<ProviderResponse> refused = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        refused.Status.Should().Be(AdminStatus.BadRequest);
        StoredEndpoint(h, row.ProviderId).Should().Be("https://api.openai.com/v1", "a refusal writes nothing");
    }

    [Fact]
    public async Task Pointing_an_OpenAICompatible_WebSearch_account_at_api_openai_com_is_allowed()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = Seed(h, "ws", enabled: true, capability: InferenceProviderCapability.WebSearch, endpoint: "https://api.openai.com/v1");

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, "https://api.openai.com/v1/");

        result.Status.Should().Be(AdminStatus.Ok);
    }

    [Fact]
    public async Task Pointing_an_OpenAICompatible_chat_only_account_at_another_host_is_still_allowed()
    {
        Harness h = Create(billingOn: false);
        ModelCapabilityView row = Seed(h, "chat", enabled: true, endpoint: "https://api.openai.com/v1");

        AdminOutcome<ProviderResponse> result = await EditEndpointAsync(h, row.ProviderId, PublicEndpoint);

        result.Status.Should().Be(AdminStatus.Ok);
    }
}
