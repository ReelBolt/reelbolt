using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-17 coverage for <see cref="DesignSystemRunState"/>: a run with a usable pin loads its profile
/// and renders the role's blocks; a run without a pin (or with the ambient off) is null.
/// </summary>
public class DesignSystemRunStateTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

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

    [Fact]
    public async Task LoadAsync_loads_both_documents()
    {
        using WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db);
        using ServiceProvider sp = Provider(db);

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = DesignSystemPinJson.Serialize(pin) },
            sp,
            CancellationToken.None);

        state.Should().NotBeNull();
        state!.Profile.Design.Should().NotBeNull();
        state.Profile.ProductMap.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadAsync_returns_null_without_a_pin()
    {
        using WorkflowEngineDbContext db = NewDb();
        await SeedAsync(db);
        using ServiceProvider sp = Provider(db);

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = null },
            sp,
            CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_returns_null_when_disabled()
    {
        using WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db);
        using ServiceProvider sp = Provider(db, enabled: false);

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = DesignSystemPinJson.Serialize(pin) },
            sp,
            CancellationToken.None);

        state.Should().BeNull();
    }

    [Fact]
    public async Task BlocksFor_translator_gets_tokens_and_structure()
    {
        DesignSystemRunState state = await LoadStateAsync();

        (string? design, string? product) = state.BlocksFor(AgentType.RemotionComponentTranslator);

        design.Should().NotBeNull();
        design!.Should().StartWith("[DESIGN SYSTEM v1]");
        product.Should().NotBeNull();
        product!.Should().StartWith("[PRODUCT PROFILE v1]");
    }

    [Fact]
    public async Task BlocksFor_scriptwriter_gets_voice_and_overview()
    {
        DesignSystemRunState state = await LoadStateAsync();

        (string? design, string? product) = state.BlocksFor(AgentType.ScriptwriterAgent);

        design.Should().NotBeNull();
        product.Should().NotBeNull();
    }

    [Fact]
    public async Task BlocksFor_analyzer_returns_null_null()
    {
        DesignSystemRunState state = await LoadStateAsync();

        (string? design, string? product) = state.BlocksFor(AgentType.CodeStructureAnalyzer);

        design.Should().BeNull();
        product.Should().BeNull();
    }

    [Fact]
    public async Task The_logo_file_name_is_resolved_only_from_the_runs_own_project()
    {
        Guid mine = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid logoId = Guid.NewGuid();
        Guid foreignLogoId = Guid.NewGuid();

        ProjectFile Row(Guid id, Guid projectId, string name) => new()
        {
            Id = id,
            ProjectId = projectId,
            OriginalFileName = name,
            StorageKey = $"projects/{projectId}/{name}",
            MimeType = "image/svg+xml",
        };

        JsonObject Logo(Guid id)
        {
            JsonObject design = DesignDoc();
            design["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject { ["fileRef"] = "project-file:" + id.ToString("D") },
            };
            return design;
        }

        ProjectFile[] files = [Row(logoId, mine, "acme-logo.svg"), Row(foreignLogoId, other, "other-tenant-secret.svg")];

        LoadedPinFixture.Loaded own = await LoadedPinFixture.LoadAsync(projectId: mine, design: Logo(logoId), files: files);
        own.State!.RenderDesignView(DesignView.GraphicsPalette)!.Should().Contain("acme-logo.svg");

        LoadedPinFixture.Loaded foreign = await LoadedPinFixture.LoadAsync(projectId: mine, design: Logo(foreignLogoId), files: files);
        foreign.State.Should().NotBeNull();
        foreign.State!.RenderDesignView(DesignView.GraphicsPalette)!
            .Should().NotContain("other-tenant-secret.svg", "a file id from another project must not resolve to its name");
    }

    private static async Task<DesignSystemRunState> LoadStateAsync()
    {
        WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db);
        ServiceProvider sp = Provider(db);

        DesignSystemRunState? state = await DesignSystemRunState.LoadAsync(
            new WorkflowExecution { DesignSystemPinJson = DesignSystemPinJson.Serialize(pin) },
            sp,
            CancellationToken.None);

        state.Should().NotBeNull();
        return state!;
    }

    private static ServiceProvider Provider(WorkflowEngineDbContext db, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => db);
        services.AddScoped<PinnedDocumentLoader>();
        services.Configure<DesignSystemAmbientOptions>(options =>
        {
            options.Enabled = enabled;
            options.DesignMaxChars = 2400;
            options.ProductMaxChars = 3200;
        });
        return services.BuildServiceProvider();
    }

    private static async Task<DesignSystemPin> SeedAsync(WorkflowEngineDbContext db)
    {
        JsonObject design = DesignDoc();
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        string mapJson = CanonicalJson.Serialize(JsonNode.Parse(ProductJson));
        string mapDigest = CanonicalJson.Sha256Hex(mapJson);

        Guid designId = Guid.NewGuid();
        Guid productId = Guid.NewGuid();
        db.DesignSystemVersions.Add(Version(designId, 1, designJson, designDigest));
        db.DesignSystemVersions.Add(Version(productId, 1, mapJson, mapDigest));
        await db.SaveChangesAsync();

        return new DesignSystemPin(
            1,
            new DesignSystemPinMember(designId, 1, designDigest),
            new DesignSystemPinMember(productId, 1, mapDigest));
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

    private static JsonObject DesignDoc()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        return doc;
    }
}
