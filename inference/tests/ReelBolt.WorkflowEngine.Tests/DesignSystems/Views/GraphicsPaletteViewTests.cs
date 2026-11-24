using System;
using System.Collections.Generic;
using System.Linq;
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
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Views;

/// <summary>
/// P3-06 golden coverage for the <see cref="DesignView.GraphicsPalette"/> renderer: the colours, the
/// sandbox font note and the logo's original file name (never its bytes), plus the guarantee that a
/// brand-colour change moves a GraphicsPalette role's cache key while an unrelated token does not.
/// </summary>
public class GraphicsPaletteViewTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);
    private static readonly Guid LogoId = Guid.Parse("0f9a6a80-1e2b-4c3d-8f10-9a7b6c5d4e3f");

    // ---------------------------------------------------------------------------------------------------
    // Golden text
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Graphics_palette_matches_golden_text()
    {
        JsonObject doc = PaletteFixture();
        var names = new Dictionary<Guid, string> { [LogoId] = "acme-logo.svg" };

        string? block = DesignSystemPromptView.RenderDesign(doc, DesignView.GraphicsPalette, 3, 2400, names);

        string expected = string.Join(
            "\n",
            "[DESIGN SYSTEM v3]",
            DesignSystemPromptView.DesignHeader,
            "Colors: primary #6D28D9 (high), secondary #0EA5E9 (medium), accent #F59E0B, text #111827",
            "Fonts: heading \"Inter\" 700; body \"Inter\" (installed in the sandbox)",
            "Logo: available as a project file named \"acme-logo.svg\"",
            "[END DESIGN SYSTEM]");

        block.Should().Be(expected);
    }

    [Fact]
    public void Logo_line_is_omitted_when_the_name_cannot_be_resolved()
    {
        JsonObject doc = PaletteFixture();

        string? withoutContext = DesignSystemPromptView.RenderDesign(doc, DesignView.GraphicsPalette, 1, 2400);
        withoutContext.Should().NotBeNull();
        withoutContext!.Should().NotContain("Logo:");

        var wrongId = new Dictionary<Guid, string> { [Guid.NewGuid()] = "other.svg" };
        string? unresolved = DesignSystemPromptView.RenderDesign(doc, DesignView.GraphicsPalette, 1, 2400, wrongId);
        unresolved.Should().NotBeNull();
        unresolved!.Should().NotContain("Logo:");

        JsonObject noAsset = DesignDocument.New("Acme");
        DesignDocument.SetToken(noAsset, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 1.0);
        string? missing = DesignSystemPromptView.RenderDesign(
            noAsset, DesignView.GraphicsPalette, 1, 2400, new Dictionary<Guid, string> { [LogoId] = "acme-logo.svg" });
        missing.Should().NotBeNull();
        missing!.Should().NotContain("Logo:");
    }

    [Fact]
    public void Palette_colours_are_capped_at_twelve()
    {
        JsonObject doc = DesignDocument.New("Acme");
        for (int i = 0; i < 30; i++)
            DesignDocument.SetToken(doc, $"color.extra.c{i:D2}", "color", new JsonObject { ["hex"] = "#112233" }, Prov, 1.0);

        string? block = DesignSystemPromptView.RenderDesign(doc, DesignView.GraphicsPalette, 1, 1_000_000);

        block.Should().NotBeNull();
        string colors = block!.Split('\n').Single(line => line.StartsWith("Colors:", StringComparison.Ordinal));
        colors.Split(", ").Length.Should().Be(12);
    }

    // ---------------------------------------------------------------------------------------------------
    // Cache key
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_brand_colour_change_moves_a_graphics_palette_roles_key()
    {
        DesignSystemRunState? before = await LoadStateAsync(primary: "#6d28d9", spacing: 4);
        DesignSystemRunState? after = await LoadStateAsync(primary: "#7c3aed", spacing: 4);

        string? plannerBefore = before!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsPlanner);
        string? plannerAfter = after!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsPlanner);

        plannerBefore.Should().NotBeNull();
        plannerBefore.Should().Contain("d=").And.Contain("p=-");
        plannerAfter.Should().NotBe(plannerBefore);
    }

    [Fact]
    public async Task A_token_the_palette_view_excludes_does_not_move_the_key()
    {
        DesignSystemRunState? before = await LoadStateAsync(primary: "#6d28d9", spacing: 4);
        DesignSystemRunState? after = await LoadStateAsync(primary: "#6d28d9", spacing: 12);

        string? plannerBefore = before!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsPlanner);
        string? plannerAfter = after!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsPlanner);
        string? directorBefore = before.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsDirector);
        string? directorAfter = after!.CacheKeyComponent(StepTypeEnum.Agent, AgentType.MotionGraphicsDirector);

        plannerAfter.Should().Be(plannerBefore);
        directorAfter.Should().Be(directorBefore);
    }

    // ---------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------

    private static JsonObject PaletteFixture()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", new JsonObject { ["hex"] = "#0ea5e9" }, Prov, 0.7);
        DesignDocument.SetToken(doc, "color.brand.accent", "color", new JsonObject { ["hex"] = "#f59e0b" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "color.text", "color", new JsonObject { ["hex"] = "#111827" }, Prov, 1.0);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        DesignDocument.SetToken(doc, "typography.weight.heading", "fontWeight", JsonValue.Create(700), Prov, 1.0);
        DesignDocument.SetToken(doc, "typography.family.body", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        doc["assets"] = new JsonObject
        {
            ["logo"] = new JsonObject { ["fileRef"] = "project-file:" + LogoId.ToString("D") },
        };
        return doc;
    }

    private static async Task<DesignSystemRunState?> LoadStateAsync(string primary, int spacing)
    {
        using WorkflowEngineDbContext db = NewDb();
        DesignSystemPin pin = await SeedAsync(db, primary, spacing);
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

    private static async Task<DesignSystemPin> SeedAsync(WorkflowEngineDbContext db, string primary, int spacing)
    {
        JsonObject design = DesignDocument.New("Acme");
        DesignDocument.SetToken(design, "color.brand.primary", "color", new JsonObject { ["hex"] = primary }, Prov, 0.95);
        DesignDocument.SetToken(design, "spacing.md", "dimension", new JsonObject { ["value"] = spacing, ["unit"] = "px" }, Prov, 1.0);
        string designJson = CanonicalJson.Serialize(design);
        string designDigest = CanonicalJson.Digest(design);

        Guid designId = Guid.NewGuid();
        db.DesignSystemVersions.Add(Version(designId, 1, designJson, designDigest));
        await db.SaveChangesAsync();

        return new DesignSystemPin(1, new DesignSystemPinMember(designId, 1, designDigest), null);
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
}
