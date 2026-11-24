using System;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-17 coverage for <see cref="DesignSystemAmbient"/>: the run's design block then its product
/// block are appended after the style block, and a role with no views sees the prompt unchanged.
/// </summary>
public class DesignSystemAmbientTests
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
    public void Decorate_appends_design_then_product_after_style()
    {
        const string styled = "BASE\n\n[STYLE INSTRUCTIONS]x[END STYLE INSTRUCTIONS]";

        using (DesignSystemAmbient.Enter(State()))
        {
            string result = DesignSystemAmbient.Decorate(styled, AgentType.RemotionComponentTranslator);

            int style = result.IndexOf("[STYLE INSTRUCTIONS]", StringComparison.Ordinal);
            int design = result.IndexOf("[DESIGN SYSTEM", StringComparison.Ordinal);
            int product = result.IndexOf("[PRODUCT PROFILE", StringComparison.Ordinal);

            style.Should().BeGreaterThanOrEqualTo(0);
            style.Should().BeLessThan(design);
            design.Should().BeLessThan(product);
            result.Should().EndWith("[END PRODUCT PROFILE]");
        }
    }

    [Fact]
    public void Decorate_composes_with_RunStyleAmbient()
    {
        var style = new RunStyleSnapshot("Fast", null, null, new StyleConfig(Pacing: StylePacing.Fast));

        using (RunStyleAmbient.Enter(new RunStyleState(style, null, StyleNudgeService.Instance)))
        using (DesignSystemAmbient.Enter(State()))
        {
            string result = DesignSystemAmbient.Decorate(
                RunStyleAmbient.Decorate("base", AgentType.ScriptwriterAgent),
                AgentType.ScriptwriterAgent);

            int styleIndex = result.IndexOf("[STYLE INSTRUCTIONS]", StringComparison.Ordinal);
            int designIndex = result.IndexOf("[DESIGN SYSTEM", StringComparison.Ordinal);
            int productIndex = result.IndexOf("[PRODUCT PROFILE", StringComparison.Ordinal);

            styleIndex.Should().BeGreaterThanOrEqualTo(0);
            styleIndex.Should().BeLessThan(designIndex);
            designIndex.Should().BeLessThan(productIndex);
        }
    }

    [Fact]
    public void Decorate_returns_the_same_instance_for_an_analyzer()
    {
        const string prompt = "analyze it";

        using (DesignSystemAmbient.Enter(State()))
        {
            DesignSystemAmbient.Decorate(prompt, AgentType.CodeStructureAnalyzer).Should().BeSameAs(prompt);
        }
    }

    [Fact]
    public void Decorate_without_state_returns_unchanged()
    {
        const string prompt = "write it";

        DesignSystemAmbient.Decorate(prompt, AgentType.ScriptwriterAgent).Should().BeSameAs(prompt);

        string styled = RunStyleAmbient.Decorate(prompt, AgentType.ScriptwriterAgent);
        DesignSystemAmbient.Decorate(styled, AgentType.ScriptwriterAgent).Should().Be(styled);
    }

    private static DesignSystemRunState State()
    {
        var profile = new LoadedProfile(DesignDoc(), 1, ProductMapJson.Parse(ProductJson), 1);
        return new DesignSystemRunState(profile, new DesignSystemAmbientOptions());
    }

    private static JsonObject DesignDoc()
    {
        JsonObject doc = DesignDocument.New("Acme");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "typography.family.heading", "fontFamily", new JsonArray("Inter"), Prov, 1.0);
        return doc;
    }
}
