using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-18 coverage for <see cref="StepCacheKeyInputs.DesignSystem"/> and
/// <see cref="DesignSystemRunState.CacheKeyComponent"/>: a null component is byte-identical to the
/// pre-v6 key, a token only the consuming role's view renders moves only that role's key, and an
/// unusable pin contributes nothing at all.
/// </summary>
public class DesignSystemCacheKeyTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);
    private static readonly IStepCacheKeyBuilder Builder = new StepCacheKeyBuilder();

    private const string ProductJson =
        """
        {
          "schema": "reelbolt.product-map/1",
          "framework": { "name": "next", "version": "15.1", "language": "typescript", "styling": [], "packageManager": "pnpm", "projectType": "web app" },
          "routes": [{ "path": "/", "file": "app/page.tsx", "kind": "page", "requiresAuth": false }],
          "components": [{ "name": "Button", "path": "components/ui/Button.tsx", "responsibility": "Primary button.", "keyProps": ["variant"], "core": true }],
          "dependencies": { "runtime": [], "dev": [], "ui": [] }
        }
        """;

    // (a) A null component is not hashed at all, so the key is byte-identical to one computed with
    // the field absent (the conditional append mirrors RunStyle); a present component does move it.
    [Fact]
    public void A_null_component_leaves_the_key_shape_unchanged()
    {
        StepCacheKeyInputs withNull = BaseInputs() with { DesignSystem = null };
        StepCacheKeyInputs withoutField = BaseInputs();

        Builder.Build(withNull).Should().Be(Builder.Build(withoutField));

        StepCacheKeyInputs withComponent = BaseInputs() with { DesignSystem = "d=0123456789abcdef|p=-" };
        Builder.Build(withComponent).Should().NotBe(Builder.Build(withNull));
    }

    // (b) The translator renders the Tokens view (which includes the extra `color.brand.tertiary`
    // token) while the reviewer renders Conformance (which does not): changing only that token moves
    // the translator's key and leaves the reviewer's alone.
    [Fact]
    public async Task A_token_only_the_translator_sees_moves_its_key_and_not_the_reviewers()
    {
        DesignSystemRunState? baseline = await LoadStateAsync(includeTertiary: false);
        DesignSystemRunState? changed = await LoadStateAsync(includeTertiary: true);
        baseline.Should().NotBeNull();
        changed.Should().NotBeNull();

        string? translatorBefore = baseline!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.RemotionComponentTranslator);
        string? translatorAfter = changed!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.RemotionComponentTranslator);
        string? reviewerBefore = baseline.CacheKeyComponent(StepTypeEnum.Agent, AgentType.ReviewAgent);
        string? reviewerAfter = changed.CacheKeyComponent(StepTypeEnum.Agent, AgentType.ReviewAgent);

        translatorBefore.Should().NotBeNull();
        reviewerBefore.Should().NotBeNull();
        translatorAfter.Should().NotBe(translatorBefore);
        reviewerAfter.Should().Be(reviewerBefore);

        // ...and that difference is exactly what the real step cache key sees.
        Builder.Build(BaseInputs() with { DesignSystem = translatorBefore })
            .Should().NotBe(Builder.Build(BaseInputs() with { DesignSystem = translatorAfter }));
        Builder.Build(BaseInputs() with { DesignSystem = reviewerBefore })
            .Should().Be(Builder.Build(BaseInputs() with { DesignSystem = reviewerAfter }));
    }

    // (c) A pin whose documents no longer match their frozen digests loads no state, so the executor
    // contributes no component — the null case above, never a throw.
    [Fact]
    public async Task An_unusable_pin_yields_no_component()
    {
        DesignSystemRunState? state = await LoadStateAsync(includeTertiary: true, bogusDigest: true);

        state.Should().BeNull();
        state?.CacheKeyComponent(StepTypeEnum.Agent, AgentType.RemotionComponentTranslator).Should().BeNull();

        StepCacheKeyInputs inputs = BaseInputs() with
        {
            DesignSystem = state?.CacheKeyComponent(StepTypeEnum.Agent, AgentType.RemotionComponentTranslator)
        };

        Builder.Build(inputs).Should().Be(Builder.Build(BaseInputs()));
    }

    [Fact]
    public async Task A_role_with_no_view_has_no_component()
    {
        DesignSystemRunState? state = await LoadStateAsync(includeTertiary: true);
        state.Should().NotBeNull();

        state!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.CodeStructureAnalyzer).Should().BeNull();
    }

    // Phase 4: the grade hint gives the director a view, so a ColorGradeRoom gets the director's
    // component whatever agent type (if any) the step names.
    [Fact]
    public async Task A_color_grade_room_uses_the_director_view()
    {
        DesignSystemRunState? state = await LoadStateAsync(includeTertiary: true);
        state.Should().NotBeNull();

        state!.CacheKeyComponent(StepTypeEnum.ColorGradeRoom, AgentType.Colorist)
            .Should().Be(state.CacheKeyComponent(StepTypeEnum.Agent, AgentType.ColorGradeDirector));
    }

    private static StepCacheKeyInputs BaseInputs() => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = StepTypeEnum.Agent,
        AgentType = AgentType.RemotionComponentTranslator,
        AgentDefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        MaxIterations = 1,
        ProjectFileFingerprint = "abc123",
    };

    private static async Task<DesignSystemRunState?> LoadStateAsync(bool includeTertiary, bool bogusDigest = false)
    {
        using WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db, includeTertiary, bogusDigest);
        using ServiceProvider sp = Provider(db);

        return await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = DesignSystemPinJson.Serialize(pin) },
            sp,
            CancellationToken.None);
    }

    private static ServiceProvider Provider(WorkflowEngineDbContext db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddScoped<PinnedDocumentLoader>();
        services.Configure<DesignSystemAmbientOptions>(options =>
        {
            options.Enabled = true;
            options.DesignMaxChars = 2400;
            options.ProductMaxChars = 3200;
        });
        return services.BuildServiceProvider();
    }

    private static async Task<DesignSystemPin> SeedAsync(
        WorkflowEngineDbContext db, bool includeTertiary, bool bogusDigest)
    {
        JsonObject design = DesignDoc(includeTertiary);
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();
        db.DesignSystemVersions.Add(Version(designId, 1, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, 1, mapJson, mapDigest));
        await db.SaveChangesAsync();

        string pinnedDesign = bogusDigest ? new string('a', 64) : designDigest;
        string pinnedMap = bogusDigest ? new string('b', 64) : mapDigest;

        return new DesignSystemPin(
            1,
            new DesignSystemPinMember(designId, 1, pinnedDesign),
            new DesignSystemPinMember(productId, 1, pinnedMap));
    }

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DesignSystemVersion Version(Guid id, int number, string json, string digest) => new()
    {
        Id = id,
        DesignSystemId = Guid.NewGuid(),
        VersionNumber = number,
        State = DesignVersionState.Approved,
        DocumentJson = json,
        DocumentDigest = digest,
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
    };

    private static JsonObject DesignDoc(bool includeTertiary)
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);

        // Rendered by the Tokens view (includeExtras: true) but NOT by Conformance (includeExtras:
        // false): exactly the token whose change must move the translator's key only.
        if (includeTertiary)
            DesignDocument.SetToken(doc, "color.brand.tertiary", "color", new JsonObject { ["hex"] = "#0ea5e9" }, Prov, 0.9);

        return doc;
    }
}
