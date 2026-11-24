using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Merge;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Merge;

public sealed class ProductMapMergerTests
{
    [Fact]
    public void Static_wins_over_llm()
    {
        ProductMapDocument staticMap = Doc("static", 2);
        ProductMapDocument llmMap = Doc("llm", 5);

        ProductMapDocument merged = ProductMapMerger.Merge(staticMap, llmMap);

        merged.Routes.Count.Should().Be(2);
        merged.Routes.Should().OnlyContain(route => route.Source == "llm");
    }

    [Fact]
    public void Llm_fills_missing_section()
    {
        ProductMapDocument staticMap = Doc("static", 0);
        ProductMapDocument llmMap = Doc("llm", 3);

        ProductMapDocument merged = ProductMapMerger.Merge(staticMap, llmMap);

        merged.Routes.Count.Should().Be(3);
        merged.Components.Should().BeEmpty();
        merged.Name.Should().Be("static");
    }

    [Fact]
    public void Null_handling()
    {
        ProductMapDocument llmMap = Doc("llm", 3);
        ProductMapDocument staticMap = Doc("static", 2);

        ProductMapMerger.Merge(null, llmMap).Should().BeSameAs(llmMap);
        ProductMapMerger.Merge(staticMap, null).Should().BeSameAs(staticMap);

        ProductMapDocument empty = ProductMapMerger.Merge(null, null);
        empty.Should().NotBeNull();
        empty.Schema.Should().Be("reelbolt.product-map/1");
        empty.Routes.Should().BeEmpty();
        empty.Components.Should().BeEmpty();
        empty.Directories.Should().BeEmpty();
        empty.EntryPoints.Should().BeEmpty();
        empty.Patterns.Should().BeEmpty();
        empty.Provenance.Should().BeEmpty();
        empty.Dependencies.Runtime.Should().BeEmpty();
        empty.Dependencies.Dev.Should().BeEmpty();
        empty.Dependencies.Ui.Should().BeEmpty();
    }

    [Fact]
    public void Preview_cards_come_from_the_static_map_only()
    {
        ProductMapPreviewCard[] staticCards =
            [new ProductMapPreviewCard("Core", "Components", "Button", "700x360", "core.card.html", "Save", ["logo.svg"], false)];
        ProductMapPreviewCard[] llmCards =
            [new ProductMapPreviewCard("Llm", null, null, null, "llm.html", "llm text", [], true)];

        ProductMapDocument merged = ProductMapMerger.Merge(
            Doc("static", 2) with { PreviewCards = staticCards },
            Doc("llm", 3) with { PreviewCards = llmCards });

        merged.PreviewCards.Should().Equal(staticCards);
    }

    [Fact]
    public void Llm_preview_cards_never_fill_a_static_map_without_them()
    {
        ProductMapDocument llmMap = Doc("llm", 3) with
        {
            PreviewCards = [new ProductMapPreviewCard("Llm", null, null, null, "llm.html", "llm text", [], true)],
        };

        ProductMapMerger.Merge(Doc("static", 2), llmMap).PreviewCards.Should().BeNull();
    }

    private static ProductMapDocument Doc(string? name, int routes)
    {
        var routeList = new List<ProductMapRoute>();
        for (int i = 0; i < routes; i++)
            routeList.Add(new ProductMapRoute("/r" + i, "r" + i + ".tsx", "page", null, null, false, "llm"));

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            name,
            new ProductMapFramework("next", "14", "typescript", [], "pnpm", "site"),
            null,
            null,
            [],
            [],
            routeList,
            [],
            new ProductMapDependencies([], [], []),
            [],
            new Dictionary<string, ProductMapProvenance>());
    }
}
