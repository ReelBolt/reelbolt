using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Coverage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class DesignCoverageTests
{
    [Fact]
    public void CompleteDesign_IsNotRecommendedForFallback()
    {
        CoverageReport report = DesignCoverage.Compute(DesignWithPrimary(), null);

        report.Design.PrimaryColor.Should().BeTrue();
        report.Design.Typography.Should().BeTrue();
        report.Design.TokenCount.Should().BeGreaterThanOrEqualTo(8);
        report.Design.FallbackRecommended.Should().BeFalse();
    }

    [Fact]
    public void DesignWithoutPrimaryColor_RecommendsFallback()
    {
        CoverageReport report = DesignCoverage.Compute(DesignWithoutPrimary(), null);

        report.Design.PrimaryColor.Should().BeFalse();
        report.Design.FallbackRecommended.Should().BeTrue();
    }

    [Fact]
    public void NullInputs_ProduceTheEmptyReport()
    {
        CoverageReport report = DesignCoverage.Compute(null, null);

        report.Design.Color.Should().BeFalse();
        report.Design.Typography.Should().BeFalse();
        report.Design.Spacing.Should().BeFalse();
        report.Design.Radius.Should().BeFalse();
        report.Design.Shadow.Should().BeFalse();
        report.Design.Motion.Should().BeFalse();
        report.Design.Voice.Should().BeFalse();
        report.Design.Assets.Should().BeFalse();
        report.Design.TokenCount.Should().Be(0);
        report.Design.PrimaryColor.Should().BeFalse();
        report.Design.FallbackRecommended.Should().BeTrue();

        report.ProductMap.Framework.Should().BeFalse();
        report.ProductMap.Routes.Should().Be(0);
        report.ProductMap.Components.Should().Be(0);
        report.ProductMap.ResponsibilityFilledPct.Should().Be(0.0);
        report.ProductMap.FallbackRecommended.Should().BeTrue();
    }

    [Fact]
    public void ProductMapWithEmptyResponsibilities_RecommendsFallback()
    {
        CoverageReport report = DesignCoverage.Compute(null, MapWith(components: 38, routes: 12, responsible: false));

        report.ProductMap.Components.Should().Be(38);
        report.ProductMap.Routes.Should().Be(12);
        report.ProductMap.ResponsibilityFilledPct.Should().Be(0.0);
        report.ProductMap.FallbackRecommended.Should().BeTrue();
    }

    [Fact]
    public void ProductMapWithoutComponents_RecommendsFallback()
    {
        CoverageReport report = DesignCoverage.Compute(null, MapWith(components: 0, routes: 12, responsible: false));

        report.ProductMap.Components.Should().Be(0);
        report.ProductMap.FallbackRecommended.Should().BeTrue();
    }

    [Fact]
    public void ProductMapFullyFilled_DoesNotRecommendFallback()
    {
        CoverageReport report = DesignCoverage.Compute(null, MapWith(components: 38, routes: 12, responsible: true));

        report.ProductMap.ResponsibilityFilledPct.Should().Be(1.0);
        report.ProductMap.FallbackRecommended.Should().BeFalse();
    }

    [Fact]
    public void SerializedReport_UsesCamelCaseTopLevelKeys()
    {
        CoverageReport report = DesignCoverage.Compute(null, null);
        string json = JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("design", out _).Should().BeTrue();
        document.RootElement.TryGetProperty("productMap", out JsonElement productMap).Should().BeTrue();

        foreach (string key in new[]
                 {
                     "framework", "routes", "components", "responsibilityFilledPct", "fallbackRecommended",
                 })
        {
            productMap.TryGetProperty(key, out _).Should().BeTrue();
        }
    }

    private static JsonObject DesignWithPrimary() => new()
    {
        ["color"] = new JsonObject
        {
            ["primary"] = Token("color", new JsonObject { ["hex"] = "#6d28d9" }),
            ["text"] = Token("color", new JsonObject { ["hex"] = "#111111" }),
        },
        ["typography"] = new JsonObject
        {
            ["family"] = new JsonObject
            {
                ["primary"] = Token("fontFamily", new JsonArray("Inter")),
            },
            ["size"] = new JsonObject
            {
                ["sm"] = Token("dimension", Dimension(14)),
                ["base"] = Token("dimension", Dimension(16)),
                ["lg"] = Token("dimension", Dimension(18)),
                ["xl"] = Token("dimension", Dimension(20)),
                ["2xl"] = Token("dimension", Dimension(24)),
                ["3xl"] = Token("dimension", Dimension(30)),
            },
        },
        ["spacing"] = new JsonObject
        {
            ["sm"] = Token("dimension", Dimension(8)),
        },
    };

    private static JsonObject DesignWithoutPrimary()
    {
        JsonObject design = DesignWithPrimary();
        design["color"]!.AsObject().Remove("primary");
        return design;
    }

    private static JsonObject Dimension(double value) =>
        new() { ["value"] = value, ["unit"] = "px" };

    private static JsonObject Token(string type, JsonNode value) => new()
    {
        ["$type"] = type,
        ["$value"] = value,
    };

    private static ProductMapDocument MapWith(int components, int routes, bool responsible)
    {
        var routeList = new List<ProductMapRoute>();
        for (int i = 0; i < routes; i++)
            routeList.Add(new ProductMapRoute("/r" + i, "r" + i + ".tsx", "page", null, null, false, null));

        var componentList = new List<ProductMapComponent>();
        for (int i = 0; i < components; i++)
        {
            componentList.Add(new ProductMapComponent(
                "C" + i,
                "components/C" + i + ".tsx",
                responsible ? "does a thing" : "",
                [],
                false,
                null));
        }

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            null,
            new ProductMapFramework("next", "", "", [], "npm", ""),
            null,
            null,
            [],
            [],
            routeList,
            componentList,
            new ProductMapDependencies([], [], []),
            [],
            new Dictionary<string, ProductMapProvenance>());
    }
}
