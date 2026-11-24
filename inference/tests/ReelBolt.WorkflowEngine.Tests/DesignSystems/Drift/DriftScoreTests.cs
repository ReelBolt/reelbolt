using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>P3-01: the uncalibrated SPEC 13.3 weight table and both drift scores.</summary>
public sealed class DriftScoreTests
{
    [Theory]
    [InlineData("color.brand.primary", 1.0)]
    [InlineData("color.brand.secondary", 1.0)]
    [InlineData("color.background", 1.0)]
    [InlineData("color.text", 1.0)]
    [InlineData("typography.family.primary", 1.0)]
    [InlineData("typography.family.secondary", 1.0)]
    [InlineData("color.extra", 0.5)]
    [InlineData("typography.size.sm", 0.5)]
    [InlineData("spacing.4", 0.25)]
    [InlineData("radius.md", 0.25)]
    public void Design_weight_table(string path, double expected) =>
        DriftScore.DesignWeight(path).Should().Be(expected);

    [Fact]
    public void Design_score_hand_computed()
    {
        JsonObject baseDoc = Doc(("color.brand.primary", "#6d28d9"));
        JsonObject after = Doc(("color.brand.primary", "#7c3aed"));

        DesignDiff diff = DesignDocumentDiffer.Diff(baseDoc, after);

        // W_changed = 1.0, W_base = 1.0, denominator max(4, 1) = 4.
        DriftScore.Design(diff, baseDoc).Should().Be(0.25);
    }

    [Fact]
    public void Design_score_counts_added_and_removed_paths()
    {
        JsonObject baseDoc = Doc(("color.brand.primary", "#6d28d9"), ("spacing.4", "#0"));
        JsonObject after = Doc(("color.brand.primary", "#6d28d9"), ("color.brand.accent", "#f59e0b"));

        DesignDiff diff = DesignDocumentDiffer.Diff(baseDoc, after);

        // removed spacing.4 = 0.25, added color.brand.accent = 0.5, W_base = 1.25 -> denominator 4.
        DriftScore.Design(diff, baseDoc).Should().Be(0.1875);
    }

    [Fact]
    public void Design_voice_change_adds_a_half_unit()
    {
        JsonObject baseDoc = Doc(("color.brand.primary", "#6d28d9"));
        baseDoc["voice"] = new JsonObject { ["tone"] = "calm" };
        JsonObject after = Doc(("color.brand.primary", "#6d28d9"));
        after["voice"] = new JsonObject { ["tone"] = "bold" };

        DesignDiff diff = DesignDocumentDiffer.Diff(baseDoc, after);

        DriftScore.Design(diff, baseDoc).Should().Be(0.125);
    }

    [Fact]
    public void Product_three_new_routes_clears_the_threshold()
    {
        ProductMapDocument baseMap = Map();
        ProductMapDocument after = Map(routes:
        [
            Route("/pricing"),
            Route("/about"),
            Route("/blog"),
        ]);

        ProductMapDiff diff = ProductMapDiffer.Diff(baseMap, after);

        diff.Routes.Added.Should().Equal("/pricing", "/about", "/blog");
        DriftScore.Product(diff, baseMap).Should().BeGreaterThanOrEqualTo(0.05);
    }

    [Fact]
    public void Product_one_new_component_stays_below_the_threshold()
    {
        ProductMapDocument baseMap = Map();
        ProductMapDocument after = Map(components:
        [
            new ProductMapComponent("PriceCard", "components/PriceCard.tsx", "Prices", [], false, "static"),
        ]);

        ProductMapDiff diff = ProductMapDiffer.Diff(baseMap, after);

        diff.Components.Added.Should().Equal("PriceCard");
        DriftScore.Product(diff, baseMap).Should().BeLessThan(0.05);
    }

    [Fact]
    public void Product_dependency_change_is_a_changed_entry()
    {
        ProductMapDocument baseMap = Map(runtime: [new ProductMapDependency("react", "^18")]);
        ProductMapDocument after = Map(runtime:
        [
            new ProductMapDependency("react", "^19"),
            new ProductMapDependency("framer-motion", "^11"),
        ]);

        ProductMapDiff diff = ProductMapDiffer.Diff(baseMap, after);

        diff.Dependencies.Added.Should().Equal("framer-motion");
        diff.Dependencies.Changed.Should().ContainSingle();
        diff.Dependencies.Changed[0].Before!.GetValue<string>().Should().Be("^18");
        diff.Dependencies.Changed[0].After!.GetValue<string>().Should().Be("^19");
    }

    private static ProductMapRoute Route(string path) =>
        new(path, $"app{path}/page.tsx", "page", null, null, false, "static");

    private static ProductMapDocument Map(
        IReadOnlyList<ProductMapRoute>? routes = null,
        IReadOnlyList<ProductMapComponent>? components = null,
        IReadOnlyList<ProductMapDependency>? runtime = null) =>
        new(
            ProductMapDocument.SchemaId,
            null,
            new ProductMapFramework("next", "15", "typescript", [], "npm", "app"),
            null,
            null,
            [],
            [],
            routes ?? [],
            components ?? [],
            new ProductMapDependencies(runtime ?? [], [], []),
            [],
            new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal));

    private static JsonObject Doc(params (string Path, string Hex)[] tokens)
    {
        JsonObject root = DesignDocument.New();
        foreach ((string path, string hex) in tokens)
        {
            DesignDocument.SetToken(
                root,
                path,
                "color",
                new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
                new TokenProvenance("static", "css-theme", null, null),
                1.0);
        }

        return root;
    }
}
