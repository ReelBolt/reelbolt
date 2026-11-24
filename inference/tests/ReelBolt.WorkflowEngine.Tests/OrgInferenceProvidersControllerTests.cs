using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A9b: the organization-scoped provider API (<c>/api/v1/org/inference-providers</c>,
/// <c>/api/v1/org/agent-overrides</c>), the member-facing <c>selectable</c> list, and the platform
/// endpoint's continued behaviour over null-org rows. Covers the Chat, Decision and VideoGeneration
/// capability families because the org rules (no private-address exemption, no env: key) cut across
/// the per-kind rules those capabilities carry.
/// </summary>
public class OrgInferenceProvidersControllerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class FakeUser(Guid? orgId, OrganizationRole? role, bool platformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "t@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => orgId;
        public OrganizationRole? OrgRole => role;
        public bool IsPlatformAdmin => platformAdmin;
    }

    private sealed class Harness
    {
        public required InferenceApiDbContext Db { get; init; }
        public required Mock<IInferenceProviderResolver> Resolver { get; init; }
        public required Mock<IOrganizationEntitlements> Entitlements { get; init; }
        public required Mock<ISecretProtector> Secrets { get; init; }
        public required InferenceProviderAdminService Service { get; init; }

        public OrgInferenceProvidersController Org(Guid? orgId, OrganizationRole? role) => new(new FakeUser(orgId, role), Service);

        public OrgAgentOverridesController Overrides(Guid? orgId, OrganizationRole? role) =>
            new(Db, new FakeUser(orgId, role), Resolver.Object, Entitlements.Object);

        public InferenceProvidersController Platform(Guid? orgId, OrganizationRole? role, bool platformAdmin) => new(
            Db, new FakeUser(orgId, role, platformAdmin),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, Secrets.Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object,
            null, Resolver.Object, Entitlements.Object);

        public void SetByo(bool allowed) => Entitlements
            .Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { ByoAllowed = allowed }));
    }

    private static Harness CreateHarness(bool byoAllowed = true)
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        InferenceApiDbContext db = new(options);

        Mock<ISecretProtector> secrets = new();
        secrets.Setup(s => s.Protect(It.IsAny<string>())).Returns<string>(k => "enc:" + k);
        Mock<IInferenceProviderResolver> resolver = new();
        Mock<IOrganizationEntitlements> entitlements = new();

        InferenceProviderTester tester = new(
            db, secrets.Object, new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new EmbeddingClientFactory(), new Mock<IDecisionClientFactory>().Object,
            new Mock<IVideoGenerationClientFactory>().Object, new Mock<ISpeechSynthesisClientFactory>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProviderTester>>().Object);

        Harness h = new()
        {
            Db = db,
            Resolver = resolver,
            Entitlements = entitlements,
            Secrets = secrets,
            Service = new InferenceProviderAdminService(db, secrets.Object, tester, resolver.Object, entitlements.Object)
        };
        h.SetByo(byoAllowed);
        return h;
    }

    private static CreateProviderRequest ChatRequest(string name = "my-azure", string endpoint = "https://93.184.216.7",
        string kind = "OpenAICompatible", string? apiKey = "sk-test-1234", bool isDefault = false, string? capability = null) =>
        ProviderTree.OneModel(name, kind, endpoint, "model-x", apiKey, isDefault, true, null, capability);

    private static ModelCapabilityView Row(Guid? org, string name, InferenceProviderCapability capability = InferenceProviderCapability.Chat,
        InferenceProviderKind kind = InferenceProviderKind.OpenAICompatible, bool isDefault = false, bool enabled = true,
        string endpoint = "https://93.184.216.7") => new()
        {
            Id = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            OrganizationId = org,
            Name = name,
            Kind = kind,
            Capability = capability,
            Endpoint = endpoint,
            ModelName = "m",
            ApiKeyEncrypted = "enc:secret",
            ApiKeyLastFour = "cret",
            IsDefault = isDefault,
            IsEnabled = enabled
        };

    private static async Task<ModelCapabilityView> Seed(Harness h, ModelCapabilityView p)
    {
        h.Db.AddFlat(p);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return p;
    }

    private static UpdateProviderRequest Rename(string name) => new(Name: name);

    // ---- role matrix ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OrganizationRole.Owner, true)]
    [InlineData(OrganizationRole.Admin, true)]
    [InlineData(OrganizationRole.Member, false)]
    [InlineData(OrganizationRole.Billing, false)]
    public async Task Org_endpoints_allow_only_owner_and_admin_and_refuse_the_rest_with_403(OrganizationRole role, bool allowed)
    {
        Harness h = CreateHarness();
        ModelCapabilityView own = await Seed(h, Row(OrgA, "own"));
        OrgInferenceProvidersController c = h.Org(OrgA, role);

        var list = await c.List(default);
        var get = await c.Get(own.ProviderId, default);
        var create = await c.Create(ChatRequest(), default);
        var update = await c.Update(own.ProviderId, new UpdateProviderRequest(TimeoutSeconds: 45), default);
        var createModel = await c.CreateModel(own.ProviderId, new CreateModelRequest("m2", "own-2", true, []), default);
        var updateModel = await c.UpdateModel(own.ProviderId, own.ModelId, new UpdateModelRequest(ModelCode: "m3"), default);
        var createCap = await c.CreateCapability(own.ProviderId, own.ModelId, new CreateCapabilityRequest("Vision"), default);
        var updateCap = await c.UpdateCapability(own.ProviderId, own.ModelId, own.Id, new UpdateCapabilityRequest(IsEnabled: true), default);
        var testSaved = await c.TestCapability(own.ProviderId, own.ModelId, own.Id, default);
        var testUnsaved = await c.TestUnsaved(new TestProviderRequest("OpenAICompatible", "https://93.184.216.7", "k", "m", "Chat"), default);
        var imageTools = await c.CreateImageTools(own.ProviderId, default);
        var deleteCap = await c.DeleteCapability(own.ProviderId, own.ModelId, own.Id, default);
        var deleteModel = await c.DeleteModel(own.ProviderId, own.ModelId, default);
        var delete = await c.Delete(own.ProviderId, default);

        if (allowed)
        {
            list.Result.Should().BeOfType<OkObjectResult>();
            get.Result.Should().BeOfType<OkObjectResult>();
            create.Result.Should().BeOfType<CreatedAtActionResult>();
            update.Result.Should().BeOfType<OkObjectResult>();
            createModel.Result.Should().BeOfType<OkObjectResult>();
            updateModel.Result.Should().BeOfType<OkObjectResult>();
            createCap.Result.Should().BeOfType<OkObjectResult>();
            updateCap.Result.Should().BeOfType<OkObjectResult>();
            deleteCap.Should().BeOfType<NoContentResult>();
            deleteModel.Should().BeOfType<NoContentResult>();
            delete.Should().BeOfType<NoContentResult>();
        }
        else
        {
            list.Result.Should().BeOfType<ForbidResult>();
            get.Result.Should().BeOfType<ForbidResult>();
            create.Result.Should().BeOfType<ForbidResult>();
            update.Result.Should().BeOfType<ForbidResult>();
            createModel.Result.Should().BeOfType<ForbidResult>();
            updateModel.Result.Should().BeOfType<ForbidResult>();
            createCap.Result.Should().BeOfType<ForbidResult>();
            updateCap.Result.Should().BeOfType<ForbidResult>();
            testSaved.Result.Should().BeOfType<ForbidResult>();
            testUnsaved.Result.Should().BeOfType<ForbidResult>();
            imageTools.Result.Should().BeOfType<ForbidResult>();
            deleteCap.Should().BeOfType<ForbidResult>();
            deleteModel.Should().BeOfType<ForbidResult>();
            delete.Should().BeOfType<ForbidResult>();
            (await h.Db.FlatProviders().CountAsync()).Should().Be(1, "a refused caller changes nothing");
            (await h.Db.FlatProviders().SingleAsync()).ModelName.Should().Be("m");
            (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Org_endpoints_without_an_active_org_are_404()
    {
        Harness h = CreateHarness();
        OrgInferenceProvidersController c = h.Org(null, null);

        (await c.List(default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.Create(ChatRequest(), default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Platform_endpoints_still_require_platform_admin_even_for_an_org_owner()
    {
        Harness h = CreateHarness();
        ModelCapabilityView plat = await Seed(h, Row(null, "plat"));
        InferenceProvidersController c = h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: false);

        (await c.List(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Get(plat.ProviderId, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Create(ChatRequest(), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Update(plat.ProviderId, Rename("x"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Delete(plat.ProviderId, default)).Should().BeOfType<ForbidResult>();
        (await c.CreateModel(plat.ProviderId, new CreateModelRequest("m2", "x", true, []), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateModel(plat.ProviderId, plat.ModelId, new UpdateModelRequest(DisplayName: "x"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.DeleteModel(plat.ProviderId, plat.ModelId, default)).Should().BeOfType<ForbidResult>();
        (await c.CreateCapability(plat.ProviderId, plat.ModelId, new CreateCapabilityRequest("Vision"), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.UpdateCapability(plat.ProviderId, plat.ModelId, plat.Id, new UpdateCapabilityRequest(IsDefault: true), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.DeleteCapability(plat.ProviderId, plat.ModelId, plat.Id, default)).Should().BeOfType<ForbidResult>();
        (await c.TestCapability(plat.ProviderId, plat.ModelId, plat.Id, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.TestUnsaved(new TestProviderRequest(ProviderId: plat.ProviderId), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.CreateImageTools(plat.ProviderId, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.GetConsolidationReport(null!, default)).Result.Should().BeOfType<ForbidResult>();
        (await c.CreatePrice(plat.ProviderId, plat.ModelId, plat.Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m), default))
            .Result.Should().BeOfType<ForbidResult>();
        (await c.RetirePrice(plat.ProviderId, plat.ModelId, plat.Id, Guid.NewGuid(), null, default)).Result.Should().BeOfType<ForbidResult>();
        (await h.Db.FlatProviders().SingleAsync()).Name.Should().Be("plat");
    }

    // ---- create / ownership / invalidation ------------------------------------------------------------

    [Fact]
    public async Task Org_create_stamps_the_org_on_every_level_redacts_the_key_hides_prices_and_invalidates_only_that_org()
    {
        Harness h = CreateHarness();

        var result = await h.Org(OrgA, OrganizationRole.Admin).Create(ChatRequest(), default);

        ProviderResponse body = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject.Value.Should().BeOfType<ProviderResponse>().Subject;
        body.HasApiKey.Should().BeTrue();
        body.ApiKeyLastFour.Should().Be("1234");
        body.OrganizationId.Should().Be(OrgA);
        body.Flat().Prices.Should().BeNull("the org endpoints never return prices");
        (await h.Db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).OrganizationId.Should().Be(OrgA);
        (await h.Db.InferenceModels.IgnoreQueryFilters().SingleAsync()).OrganizationId.Should().Be(OrgA);
        (await h.Db.ModelCapabilities.IgnoreQueryFilters().SingleAsync()).OrganizationId.Should().Be(OrgA);
        (await h.Db.ModelCapabilityPrices.CountAsync()).Should().Be(0, "BYO capabilities are never priced");
        h.Resolver.Verify(r => r.Invalidate(OrgA), Times.Once);
        h.Resolver.Verify(r => r.InvalidateAll(), Times.Never);
    }

    [Fact]
    public async Task Org_models_and_capabilities_inherit_the_providers_organization()
    {
        Harness h = CreateHarness();
        ModelCapabilityView own = await Seed(h, Row(OrgA, "own"));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        ModelResponse model = (await c.CreateModel(own.ProviderId,
            new CreateModelRequest("m2", "own-2", true, [new CreateCapabilityRequest("Vision")]), default)).Body();
        CapabilityResponse capability = (await c.CreateCapability(own.ProviderId, own.ModelId,
            new CreateCapabilityRequest("Vision"), default)).Body();

        (await h.Db.InferenceModels.IgnoreQueryFilters().SingleAsync(m => m.Id == model.Id)).OrganizationId.Should().Be(OrgA);
        (await h.Db.ModelCapabilities.IgnoreQueryFilters().SingleAsync(m => m.Id == model.Capabilities[0].Id)).OrganizationId.Should().Be(OrgA);
        (await h.Db.ModelCapabilities.IgnoreQueryFilters().SingleAsync(m => m.Id == capability.Id)).OrganizationId.Should().Be(OrgA);
        model.Capabilities[0].Prices.Should().BeNull();
        capability.Prices.Should().BeNull();
    }

    [Fact]
    public async Task Org_list_returns_only_the_orgs_own_providers_not_platform_or_other_orgs()
    {
        Harness h = CreateHarness();
        await Seed(h, Row(null, "platform"));
        await Seed(h, Row(OrgA, "a-row"));
        await Seed(h, Row(OrgB, "b-row"));

        var result = await h.Org(OrgA, OrganizationRole.Owner).List(default);

        List<ProviderResponse> rows = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<ProviderResponse>>().Subject;
        rows.Select(r => r.Name).Should().Equal("a-row");
        rows.Single().Models.Single().Capabilities.Single().Prices.Should().BeNull();
    }

    [Fact]
    public async Task Two_orgs_may_reuse_the_same_provider_name()
    {
        Harness h = CreateHarness();
        await Seed(h, Row(OrgB, "shared-name"));

        var result = await h.Org(OrgA, OrganizationRole.Owner).Create(ChatRequest(name: "shared-name"), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var again = await h.Org(OrgA, OrganizationRole.Owner).Create(ChatRequest(name: "shared-name"), default);
        again.Result.Should().BeOfType<ConflictObjectResult>("within one org the name stays unique");
    }

    [Fact]
    public async Task Platform_write_invalidates_every_org_snapshot()
    {
        Harness h = CreateHarness();

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true).Create(ChatRequest(name: "plat"), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        (await h.Db.FlatProviders().SingleAsync()).OrganizationId.Should().BeNull();
        h.Resolver.Verify(r => r.InvalidateAll(), Times.Once);
    }

    [Fact]
    public async Task Update_and_delete_invalidate_the_org()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        (await c.UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(ModelCode: "m2"), default))
            .Result.Should().BeOfType<OkObjectResult>();
        (await c.Delete(row.ProviderId, default)).Should().BeOfType<NoContentResult>();

        h.Resolver.Verify(r => r.Invalidate(OrgA), Times.Exactly(2));
        (await h.Db.FlatProviders().CountAsync()).Should().Be(0, "deleting a provider cascades to its models and capabilities");
    }

    // ---- cross-org isolation (D5: 404) and the ownership chain -----------------------------------------

    [Fact]
    public async Task Org_cannot_reach_another_orgs_provider_or_a_platform_one_at_any_level()
    {
        Harness h = CreateHarness();
        ModelCapabilityView other = await Seed(h, Row(OrgB, "b-row"));
        ModelCapabilityView platform = await Seed(h, Row(null, "platform"));
        OrgInferenceProvidersController a = h.Org(OrgA, OrganizationRole.Owner);

        foreach (ModelCapabilityView row in new[] { other, platform })
        {
            (await a.Get(row.ProviderId, default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.Update(row.ProviderId, Rename("renamed"), default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.Delete(row.ProviderId, default)).Should().BeOfType<NotFoundResult>();
            (await a.CreateImageTools(row.ProviderId, default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.CreateModel(row.ProviderId, new CreateModelRequest("m", "renamed", true, []), default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(DisplayName: "renamed"), default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.DeleteModel(row.ProviderId, row.ModelId, default)).Should().BeOfType<NotFoundResult>();
            (await a.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Vision"), default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.UpdateCapability(row.ProviderId, row.ModelId, row.Id, new UpdateCapabilityRequest(IsDefault: true), default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.DeleteCapability(row.ProviderId, row.ModelId, row.Id, default)).Should().BeOfType<NotFoundResult>();
            (await a.TestCapability(row.ProviderId, row.ModelId, row.Id, default)).Result.Should().BeOfType<NotFoundResult>();
            (await a.TestUnsaved(new TestProviderRequest(ProviderId: row.ProviderId), default)).Result.Should().BeOfType<NotFoundObjectResult>();
        }

        (await h.Db.FlatProviders().CountAsync()).Should().Be(2);
        (await h.Db.FlatProviders().AllAsync(p => p.Name != "renamed" && !p.IsDefault)).Should().BeTrue();
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        h.Resolver.Verify(r => r.Invalidate(It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task A_model_or_capability_addressed_under_the_wrong_parent_is_404_even_inside_the_org()
    {
        Harness h = CreateHarness();
        ModelCapabilityView first = await Seed(h, Row(OrgA, "first"));
        ModelCapabilityView second = await Seed(h, Row(OrgA, "second"));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        // second's model under first's provider, and second's capability under first's model.
        (await c.UpdateModel(first.ProviderId, second.ModelId, new UpdateModelRequest(DisplayName: "moved"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.DeleteModel(first.ProviderId, second.ModelId, default)).Should().BeOfType<NotFoundResult>();
        (await c.CreateCapability(first.ProviderId, second.ModelId, new CreateCapabilityRequest("Vision"), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(first.ProviderId, first.ModelId, second.Id, new UpdateCapabilityRequest(IsEnabled: false), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(first.ProviderId, second.ModelId, second.Id, new UpdateCapabilityRequest(IsEnabled: false), default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.DeleteCapability(first.ProviderId, first.ModelId, second.Id, default)).Should().BeOfType<NotFoundResult>();
        (await c.TestCapability(first.ProviderId, first.ModelId, second.Id, default)).Result.Should().BeOfType<NotFoundResult>();

        (await h.Db.FlatProviders().CountAsync()).Should().Be(2);
        (await h.Db.FlatProviders().AllAsync(p => p.IsEnabled && p.Name != "moved")).Should().BeTrue();
    }

    [Fact]
    public async Task Platform_endpoints_do_not_see_org_rows()
    {
        Harness h = CreateHarness();
        ModelCapabilityView orgRow = await Seed(h, Row(OrgA, "a-row"));
        await Seed(h, Row(null, "platform"));
        InferenceProvidersController c = h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true);

        List<ProviderResponse> rows = (await c.List(default)).Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<ProviderResponse>>().Subject;
        rows.Select(r => r.Name).Should().Equal("platform");
        (await c.Get(orgRow.ProviderId, default)).Result.Should().BeOfType<NotFoundResult>();
        (await c.Delete(orgRow.ProviderId, default)).Should().BeOfType<NotFoundResult>();
        (await c.UpdateCapability(orgRow.ProviderId, orgRow.ModelId, orgRow.Id, new UpdateCapabilityRequest(IsDefault: true), default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await c.CreatePrice(orgRow.ProviderId, orgRow.ModelId, orgRow.Id, new CreatePriceRequest("LlmInputTokens", CostUsdPerUnit: 1m), default))
            .Result.Should().BeOfType<NotFoundResult>("an org capability is never priced, not even by a platform admin");
        (await h.Db.ModelCapabilityPrices.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_org_controller_has_no_price_or_consolidation_routes()
    {
        // Routing, not a runtime check: a route the controller does not declare answers 404.
        string[] templates = typeof(OrgInferenceProvidersController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false))
            .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
            .Select(a => a.Template ?? string.Empty)
            .ToArray();

        templates.Should().NotBeEmpty();
        templates.Should().NotContain(t => t.Contains("prices") || t.Contains("consolidation-report"));
        typeof(InferenceProvidersController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false))
            .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
            .Select(a => a.Template ?? string.Empty)
            .Should().Contain(t => t.EndsWith("/prices")).And.Contain("consolidation-report");
        await Task.CompletedTask;
    }

    // ---- env: sentinel, PersonalOAuthMode -----------------------------------------------------------------

    [Theory]
    [InlineData("env:")]
    [InlineData("ENV:")]
    [InlineData("  env: ")]
    public async Task Org_create_rejects_the_ambient_credential_sentinel(string key)
    {
        Harness h = CreateHarness();

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Create(ChatRequest(kind: "Anthropic", apiKey: key), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString().Should().Contain("env:");
        (await h.Db.FlatProviders().CountAsync()).Should().Be(0);
        h.Resolver.Verify(r => r.Invalidate(It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Platform_create_still_accepts_the_ambient_credential_sentinel()
    {
        Harness h = CreateHarness();

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Create(ChatRequest(kind: "Anthropic", endpoint: "https://api.anthropic.com", apiKey: "env:"), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Org_update_rejects_the_sentinel_and_leaves_the_row_untouched()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Update(row.ProviderId, new UpdateProviderRequest(ApiKey: "env:"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).ApiKeyEncrypted.Should().Be("enc:secret");
    }

    // ---- a stored key never follows the endpoint to a new host ----------------------------------------------

    [Fact]
    public async Task Org_update_moving_the_endpoint_to_another_host_without_a_key_is_refused_and_keeps_the_row()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: "https://93.184.217.67"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
        h.Db.ChangeTracker.Clear();
        ModelCapabilityView saved = await h.Db.FlatProviders().SingleAsync();
        saved.Endpoint.Should().Be("https://93.184.216.7");
        saved.ApiKeyEncrypted.Should().Be("enc:secret");
    }

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("http://api.anthropic.com")]
    [InlineData("https://api.anthropic.com:8443")]
    public async Task Platform_update_moving_the_endpoint_origin_without_a_key_is_refused(string endpoint)
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(null, "p", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: endpoint), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).Endpoint.Should().Be("https://api.anthropic.com");
    }

    [Fact]
    public async Task Platform_update_repointing_an_env_key_provider_is_refused_too()
    {
        Harness h = CreateHarness();
        ModelCapabilityView seeded = Row(null, "p", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com");
        seeded.ApiKeyEncrypted = "enc:env:";
        ModelCapabilityView row = await Seed(h, seeded);

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: "https://api.openai.com"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("https://93.184.216.7/v1", null)]           // same origin, other path: key kept
    [InlineData("HTTPS://93.184.216.7:443/", null)]         // same origin spelled differently
    [InlineData("https://93.184.217.67", "sk-new-5678")]      // new host with a new key
    [InlineData("https://93.184.217.67", "")]                 // new host, key explicitly cleared
    public async Task Org_update_endpoint_is_allowed_on_the_same_origin_or_with_a_key_decision(string endpoint, string? apiKey)
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: endpoint, ApiKey: apiKey), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).Endpoint.Should().Be(endpoint.Trim());
    }

    [Fact]
    public async Task Org_unsaved_test_reusing_a_saved_key_against_another_host_is_refused()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));

        var result = await h.Org(OrgA, OrganizationRole.Owner).TestUnsaved(
            new TestProviderRequest("OpenAICompatible", "https://93.184.217.67", null, "m") { ProviderId = row.ProviderId }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
    }

    [Fact]
    public async Task The_refusal_tells_the_admin_what_the_form_offers()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: "https://93.184.217.67"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("re-enter the key or tick Remove the stored key");
    }

    // ---- env: only reaches the kind's vendor host --------------------------------------------------------

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("http://api.anthropic.com")]
    [InlineData("https://api.anthropic.com:8443")]
    public async Task Platform_update_with_an_env_key_at_a_non_vendor_endpoint_is_refused(string endpoint)
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(null, "p", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: endpoint, ApiKey: "env:"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("AMBIENT_KEY_ENDPOINT_NOT_ALLOWED");
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).ApiKeyEncrypted.Should().Be("enc:secret");
    }

    [Fact]
    public async Task Platform_update_setting_env_at_the_vendor_endpoint_is_allowed()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(null, "p", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: "https://api.anthropic.com/v1", ApiKey: "env:"), default);

        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Platform_create_with_an_env_key_at_a_non_vendor_endpoint_is_refused()
    {
        Harness h = CreateHarness();

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Create(ChatRequest(kind: "Anthropic", endpoint: "https://api.openai.com", apiKey: "env:"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("AMBIENT_KEY_ENDPOINT_NOT_ALLOWED");
        (await h.Db.FlatProviders().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Platform_unsaved_test_with_an_env_key_at_a_non_vendor_endpoint_is_refused()
    {
        Harness h = CreateHarness();

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .TestUnsaved(new TestProviderRequest("Gemini", "https://api.openai.com", "env:", "m"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString()
            .Should().Contain("AMBIENT_KEY_ENDPOINT_NOT_ALLOWED");
    }

    [Fact]
    public async Task Org_rows_reject_PersonalOAuthMode_on_create_update_and_test()
    {
        Harness h = CreateHarness();
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));

        (await c.Create(ProviderTree.OneModel("oauth", "Anthropic", "https://api.anthropic.com", "m", "sk-ant-oat01-x", false, true, null, null, true), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Update(row.ProviderId, new UpdateProviderRequest(PersonalOAuthMode: true), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.TestUnsaved(new TestProviderRequest("Anthropic", "https://api.anthropic.com", "k", "m", null, true), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        h.Db.ChangeTracker.Clear(); // a refused update must not have persisted anything
        (await h.Db.FlatProviders().SingleAsync()).PersonalOAuthMode.Should().BeFalse();
    }

    // ---- private-address guard: no exemption for org rows -----------------------------------------------

    [Theory]
    [InlineData("OpenAICompatible", "Chat", "http://127.0.0.1:8000/v1")]
    [InlineData("OpenAICompatible", "Embedding", "http://192.168.1.10:8000/v1")]
    [InlineData("OpenJev", "Decision", "http://127.0.0.1:8791")]
    [InlineData("FishAudio", "SpeechSynthesis", "http://10.0.0.5:8080")]
    // The ASR half of the same kind keeps the identical exemption: the private-address guard is
    // kind-aware, not capability-aware (InferenceProvidersController.IsDisallowedEndpoint), so a
    // FishAudio row serving Transcription is exactly as self-hostable as one serving SpeechSynthesis.
    [InlineData("FishAudio", "Transcription", "http://10.0.0.5:8080")]
    [InlineData("FishAudio", "Transcription", "http://host.docker.internal:8080")]
    public async Task Org_create_rejects_private_endpoints_for_kinds_that_platform_rows_may_self_host(string kind, string capability, string endpoint)
    {
        Harness h = CreateHarness();

        var orgResult = await h.Org(OrgA, OrganizationRole.Owner)
            .Create(ChatRequest(kind: kind, endpoint: endpoint, capability: capability), default);
        var platformResult = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true)
            .Create(ChatRequest(name: "plat", kind: kind, endpoint: endpoint, capability: capability), default);

        orgResult.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value!.ToString().Should().Contain("public");
        platformResult.Result.Should().BeOfType<CreatedAtActionResult>("the self-hosted exemption is a platform-operator privilege");
        (await h.Db.FlatProviders().CountAsync(p => p.OrganizationId == OrgA)).Should().Be(0);
    }

    [Fact]
    public async Task Org_update_that_repoints_at_a_private_address_is_rejected()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a"));

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Update(row.ProviderId, new UpdateProviderRequest(Endpoint: "http://169.254.169.254/"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        h.Db.ChangeTracker.Clear();
        (await h.Db.FlatProviders().SingleAsync()).Endpoint.Should().Be("https://93.184.216.7");
    }

    [Fact]
    public async Task Org_unsaved_test_rejects_private_endpoints_before_any_client_is_built()
    {
        Harness h = CreateHarness();

        var result = await h.Org(OrgA, OrganizationRole.Owner).TestUnsaved(
            new TestProviderRequest("OpenJev", "http://127.0.0.1:8791", "k", "m", "Decision"), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Org_saved_test_rejects_a_row_left_at_a_private_address()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a", endpoint: "http://127.0.0.1:8000/v1"));

        var result = await h.Org(OrgA, OrganizationRole.Owner).TestCapability(row.ProviderId, row.ModelId, row.Id, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- per-kind capability rules still apply to org rows (spot checks, one per capability family) -------

    [Theory]
    [InlineData("Anthropic", "Transcription")]
    [InlineData("OpenJev", "Chat")]
    [InlineData("OpenAICompatible", "VideoGeneration")]
    [InlineData("MiniMax", "Chat")]
    [InlineData("Anthropic", "Decision")]
    public async Task Org_create_keeps_the_kind_capability_matrix(string kind, string capability)
    {
        Harness h = CreateHarness();

        var result = await h.Org(OrgA, OrganizationRole.Owner).Create(ChatRequest(kind: kind, capability: capability), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Org_add_capability_keeps_the_kind_capability_matrix_and_never_offers_Compute()
    {
        Harness h = CreateHarness();
        ModelCapabilityView row = await Seed(h, Row(OrgA, "a", kind: InferenceProviderKind.Anthropic, endpoint: "https://api.anthropic.com"));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Transcription"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Compute"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Nonsense"), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Chat"), default))
            .Result.Should().BeOfType<ConflictObjectResult>("the model already serves Chat");
        (await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Vision"), default))
            .Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Org_can_register_a_public_MiniMax_video_generation_provider()
    {
        Harness h = CreateHarness();

        var result = await h.Org(OrgA, OrganizationRole.Owner)
            .Create(ChatRequest(kind: "MiniMax", endpoint: "https://93.184.216.7", capability: "VideoGeneration"), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    // ---- plan gating --------------------------------------------------------------------------------------

    [Fact]
    public async Task Byo_not_in_plan_is_402_for_create_update_and_tests_but_list_get_and_delete_still_work()
    {
        Harness h = CreateHarness(byoAllowed: false);
        ModelCapabilityView row = await Seed(h, Row(OrgA, "legacy"));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        static void Is402<T>(ActionResult<T> r) =>
            r.Result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(402);

        var create = await c.Create(ChatRequest(), default);
        Is402(create);
        ((ObjectResult)create.Result!).Value!.ToString().Should().Contain("byo_not_in_plan");
        Is402(await c.Update(row.ProviderId, new UpdateProviderRequest(TimeoutSeconds: 10), default));
        Is402(await c.CreateModel(row.ProviderId, new CreateModelRequest("m2", "x", true, []), default));
        Is402(await c.UpdateModel(row.ProviderId, row.ModelId, new UpdateModelRequest(ModelCode: "m2"), default));
        Is402(await c.CreateCapability(row.ProviderId, row.ModelId, new CreateCapabilityRequest("Vision"), default));
        Is402(await c.UpdateCapability(row.ProviderId, row.ModelId, row.Id, new UpdateCapabilityRequest(IsDefault: true), default));
        Is402(await c.CreateImageTools(row.ProviderId, default));
        Is402(await c.TestCapability(row.ProviderId, row.ModelId, row.Id, default));
        Is402(await c.TestUnsaved(new TestProviderRequest("OpenAICompatible", "https://93.184.216.7", "k", "m"), default));

        (await c.List(default)).Result.Should().BeOfType<OkObjectResult>();
        (await c.Get(row.ProviderId, default)).Result.Should().BeOfType<OkObjectResult>();
        (await c.Delete(row.ProviderId, default)).Should().BeOfType<NoContentResult>();
        (await h.Db.FlatProviders().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Platform_endpoint_is_not_plan_gated()
    {
        Harness h = CreateHarness(byoAllowed: false);

        var result = await h.Platform(OrgA, OrganizationRole.Owner, platformAdmin: true).Create(ChatRequest(name: "plat"), default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    // ---- defaults are scoped to (org, capability) ---------------------------------------------------------

    [Fact]
    public async Task Setting_an_org_default_clears_only_that_orgs_default_for_that_capability()
    {
        Harness h = CreateHarness();
        ModelCapabilityView platformDefault = await Seed(h, Row(null, "plat", isDefault: true));
        ModelCapabilityView otherOrgDefault = await Seed(h, Row(OrgB, "b", isDefault: true));
        ModelCapabilityView oldDefault = await Seed(h, Row(OrgA, "old", isDefault: true));
        ModelCapabilityView otherCapability = await Seed(h, Row(OrgA, "vision", InferenceProviderCapability.Vision, isDefault: true));

        var result = await h.Org(OrgA, OrganizationRole.Owner).Create(ChatRequest(name: "new", isDefault: true), default);
        result.Result.Should().BeOfType<CreatedAtActionResult>();

        async Task<bool> IsDefault(Guid id) => (await h.Db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == id)).IsDefault;
        (await IsDefault(oldDefault.Id)).Should().BeFalse();
        (await IsDefault(platformDefault.Id)).Should().BeTrue();
        (await IsDefault(otherOrgDefault.Id)).Should().BeTrue();
        (await IsDefault(otherCapability.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Promoting_via_update_is_scoped_the_same_way()
    {
        Harness h = CreateHarness();
        ModelCapabilityView otherOrgDefault = await Seed(h, Row(OrgB, "b", isDefault: true));
        ModelCapabilityView oldDefault = await Seed(h, Row(OrgA, "old", isDefault: true));
        ModelCapabilityView candidate = await Seed(h, Row(OrgA, "cand"));

        var result = await h.Org(OrgA, OrganizationRole.Admin)
            .UpdateCapability(candidate.ProviderId, candidate.ModelId, candidate.Id, new UpdateCapabilityRequest(IsDefault: true), default);

        result.Result.Should().BeOfType<OkObjectResult>();
        result.Body().IsDefault.Should().BeTrue();
        (await h.Db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == oldDefault.Id)).IsDefault.Should().BeFalse();
        (await h.Db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == otherOrgDefault.Id)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_anything_that_holds_a_default_is_409_until_the_default_moves()
    {
        Harness h = CreateHarness();
        ModelCapabilityView def = await Seed(h, Row(OrgA, "def", isDefault: true));
        OrgInferenceProvidersController c = h.Org(OrgA, OrganizationRole.Owner);

        (await c.DeleteCapability(def.ProviderId, def.ModelId, def.Id, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.DeleteModel(def.ProviderId, def.ModelId, default)).Should().BeOfType<ConflictObjectResult>();
        (await c.Delete(def.ProviderId, default)).Should().BeOfType<ConflictObjectResult>();
        (await h.Db.FlatProviders().CountAsync()).Should().Be(1);

        (await c.UpdateCapability(def.ProviderId, def.ModelId, def.Id, new UpdateCapabilityRequest(IsDefault: false), default))
            .Result.Should().BeOfType<OkObjectResult>();
        (await c.DeleteCapability(def.ProviderId, def.ModelId, def.Id, default)).Should().BeOfType<NoContentResult>();
        (await h.Db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(1, "deleting a capability leaves its model");
    }

    // ---- selectable ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Selectable_lists_platform_rows_and_own_org_rows_only_and_exposes_nothing_secret()
    {
        Harness h = CreateHarness();
        await Seed(h, Row(null, "plat-chat"));
        await Seed(h, Row(null, "plat-disabled", enabled: false));
        await Seed(h, Row(OrgA, "a-chat"));
        await Seed(h, Row(OrgA, "a-vision", InferenceProviderCapability.Vision));
        await Seed(h, Row(OrgB, "b-chat"));

        var result = await h.Platform(OrgA, OrganizationRole.Member, platformAdmin: false).Selectable(null, default);

        List<SelectableProviderResponse> rows = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<SelectableProviderResponse>>().Subject;
        rows.Select(r => r.Name).Should().Equal("a-chat", "a-vision", "plat-chat");
        rows.Single(r => r.Name == "plat-chat").IsPlatformManaged.Should().BeTrue();
        rows.Single(r => r.Name == "a-chat").IsPlatformManaged.Should().BeFalse();
        rows.Single(r => r.Name == "a-chat").Kind.Should().Be("OpenAICompatible");
        rows.Single(r => r.Name == "a-chat").ProviderName.Should().Be("a-chat");
        typeof(SelectableProviderResponse).GetProperties().Select(p => p.Name)
            .Should().BeEquivalentTo("Id", "Name", "Capability", "Model", "IsPlatformManaged", "Kind", "ProviderName", "CostIndex");
    }

    [Fact]
    public async Task Selectable_never_offers_the_system_compute_provider()
    {
        Harness h = CreateHarness();
        ModelCapabilityView compute = Row(null, "Compute", InferenceProviderCapability.Compute);
        compute.IsSystem = true;
        await Seed(h, compute);
        await Seed(h, Row(null, "plat-chat"));

        var result = await h.Platform(OrgA, OrganizationRole.Member, platformAdmin: false).Selectable(null, default);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<SelectableProviderResponse>>()
            .Subject.Select(r => r.Name).Should().Equal("plat-chat");
    }

    [Fact]
    public async Task Selectable_filters_by_capability_and_rejects_an_unknown_one()
    {
        Harness h = CreateHarness();
        await Seed(h, Row(null, "plat-chat"));
        await Seed(h, Row(OrgA, "a-vision", InferenceProviderCapability.Vision));
        InferenceProvidersController c = h.Platform(OrgA, OrganizationRole.Member, platformAdmin: false);

        var vision = await c.Selectable("vision", default);
        vision.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<SelectableProviderResponse>>()
            .Subject.Select(r => r.Name).Should().Equal("a-vision");

        (await c.Selectable("nonsense", default)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Selectable_hides_org_rows_when_the_plan_does_not_allow_byo()
    {
        Harness h = CreateHarness(byoAllowed: false);
        await Seed(h, Row(null, "plat-chat"));
        await Seed(h, Row(OrgA, "a-chat"));

        var result = await h.Platform(OrgA, OrganizationRole.Member, platformAdmin: false).Selectable(null, default);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<SelectableProviderResponse>>()
            .Subject.Select(r => r.Name).Should().Equal("plat-chat");
    }

    [Fact]
    public async Task Selectable_is_open_to_every_role_but_billing_and_404_without_an_org()
    {
        Harness h = CreateHarness();

        foreach (OrganizationRole role in new[] { OrganizationRole.Owner, OrganizationRole.Admin, OrganizationRole.Member })
        {
            (await h.Platform(OrgA, role, false).Selectable(null, default)).Result.Should().BeOfType<OkObjectResult>();
        }

        (await h.Platform(OrgA, OrganizationRole.Billing, false).Selectable(null, default)).Result.Should().BeOfType<ForbidResult>();
        (await h.Platform(null, null, false).Selectable(null, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    // ---- agent overrides ----------------------------------------------------------------------------------

    [Fact]
    public async Task Override_set_and_clear_round_trip_and_invalidate_the_org()
    {
        Harness h = CreateHarness();
        ModelCapabilityView plat = await Seed(h, Row(null, "plat"));
        OrgAgentOverridesController c = h.Overrides(OrgA, OrganizationRole.Admin);

        var set = await c.Set("VideoStoryEditor", new SetOrgAgentOverrideRequest(plat.Id), default);
        set.Result.Should().BeOfType<OkObjectResult>();
        (await h.Db.OrganizationAgentProviderOverrides.SingleAsync()).Should().Match<OrganizationAgentProviderOverride>(
            o => o.OrganizationId == OrgA && o.AgentType == AgentType.VideoStoryEditor && o.InferenceProviderId == plat.Id);

        ModelCapabilityView own = await Seed(h, Row(OrgA, "own"));
        (await c.Set("videostoryeditor", new SetOrgAgentOverrideRequest(own.Id), default)).Result.Should().BeOfType<OkObjectResult>();
        (await h.Db.OrganizationAgentProviderOverrides.SingleAsync()).InferenceProviderId.Should().Be(own.Id, "a second PUT replaces the row");

        (await c.Clear("VideoStoryEditor", default)).Should().BeOfType<NoContentResult>();
        (await c.Clear("VideoStoryEditor", default)).Should().BeOfType<NoContentResult>("clearing is idempotent");
        (await h.Db.OrganizationAgentProviderOverrides.CountAsync()).Should().Be(0);
        h.Resolver.Verify(r => r.Invalidate(OrgA), Times.Exactly(4));
    }

    [Fact]
    public async Task Override_to_another_orgs_or_a_disabled_or_missing_provider_is_404_and_writes_nothing()
    {
        Harness h = CreateHarness();
        ModelCapabilityView other = await Seed(h, Row(OrgB, "b"));
        ModelCapabilityView disabled = await Seed(h, Row(null, "off", enabled: false));
        OrgAgentOverridesController c = h.Overrides(OrgA, OrganizationRole.Owner);

        foreach (Guid id in new[] { other.Id, disabled.Id, Guid.NewGuid() })
        {
            (await c.Set("ReviewAgent", new SetOrgAgentOverrideRequest(id), default)).Result.Should().BeOfType<NotFoundObjectResult>();
        }

        (await h.Db.OrganizationAgentProviderOverrides.CountAsync()).Should().Be(0);
        h.Resolver.Verify(r => r.Invalidate(It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task Override_to_an_unpriced_platform_model_is_the_same_404_while_billing_fails_closed()
    {
        Harness h = CreateHarness();
        h.Resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);
        ModelCapabilityView unpriced = await Seed(h, Row(null, "no-price"));
        OrgAgentOverridesController c = h.Overrides(OrgA, OrganizationRole.Owner);

        NotFoundObjectResult refused = (await c.Set("ReviewAgent", new SetOrgAgentOverrideRequest(unpriced.Id), default))
            .Result.Should().BeOfType<NotFoundObjectResult>().Subject;

        refused.Value.Should().BeEquivalentTo(new { error = "Referenced inference provider not found." });
        (await h.Db.OrganizationAgentProviderOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Override_rejects_non_chat_providers_custom_and_unknown_agent_types_and_a_missing_body_id()
    {
        Harness h = CreateHarness();
        ModelCapabilityView vision = await Seed(h, Row(null, "v", InferenceProviderCapability.Vision));
        ModelCapabilityView chat = await Seed(h, Row(null, "c"));
        OrgAgentOverridesController c = h.Overrides(OrgA, OrganizationRole.Owner);

        (await c.Set("ReviewAgent", new SetOrgAgentOverrideRequest(vision.Id), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Set("Custom", new SetOrgAgentOverrideRequest(chat.Id), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Set("NoSuchAgent", new SetOrgAgentOverrideRequest(chat.Id), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Set("3", new SetOrgAgentOverrideRequest(chat.Id), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await c.Set("ReviewAgent", new SetOrgAgentOverrideRequest(null), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await h.Db.OrganizationAgentProviderOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Override_to_an_own_org_row_is_refused_after_a_downgrade()
    {
        Harness h = CreateHarness(byoAllowed: false);
        ModelCapabilityView own = await Seed(h, Row(OrgA, "own"));

        var result = await h.Overrides(OrgA, OrganizationRole.Owner).Set("ReviewAgent", new SetOrgAgentOverrideRequest(own.Id), default);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Theory]
    [InlineData(OrganizationRole.Member)]
    [InlineData(OrganizationRole.Billing)]
    public async Task Override_endpoints_refuse_member_and_billing_with_403(OrganizationRole role)
    {
        Harness h = CreateHarness();
        ModelCapabilityView plat = await Seed(h, Row(null, "plat"));
        OrgAgentOverridesController c = h.Overrides(OrgA, role);

        (await c.List(default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Set("ReviewAgent", new SetOrgAgentOverrideRequest(plat.Id), default)).Result.Should().BeOfType<ForbidResult>();
        (await c.Clear("ReviewAgent", default)).Should().BeOfType<ForbidResult>();
        (await h.Db.OrganizationAgentProviderOverrides.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Override_list_is_scoped_to_the_active_org()
    {
        Harness h = CreateHarness();
        ModelCapabilityView plat = await Seed(h, Row(null, "plat"));
        h.Db.OrganizationAgentProviderOverrides.AddRange(
            new OrganizationAgentProviderOverride { OrganizationId = OrgA, AgentType = AgentType.ReviewAgent, InferenceProviderId = plat.Id },
            new OrganizationAgentProviderOverride { OrganizationId = OrgB, AgentType = AgentType.DirectorAgent, InferenceProviderId = plat.Id });
        await h.Db.SaveChangesAsync();

        var result = await h.Overrides(OrgA, OrganizationRole.Owner).List(default);

        result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeAssignableTo<List<OrgAgentOverrideResponse>>()
            .Subject.Select(o => o.AgentType).Should().Equal("ReviewAgent");
    }
}
