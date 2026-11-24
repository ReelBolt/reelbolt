using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class ProductMapJsonTests
{
    [Fact]
    public void Serialize_Parse_RoundTripsFullDocument()
    {
        ProductMapDocument doc = FullDocument();
        string canonical = ProductMapJson.Serialize(doc);

        ProductMapDocument? parsed = ProductMapJson.Parse(canonical);

        parsed.Should().NotBeNull();
        ProductMapJson.Serialize(parsed!).Should().Be(canonical);
    }

    [Fact]
    public void Serialize_IsByteIdentical_WhenKeysAreWrittenInAnotherOrder()
    {
        ProductMapDocument doc = FullDocument();
        JsonObject node = ProductMapJson.ToNode(doc);

        var reversed = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in node.Reverse())
            reversed[property.Key] = property.Value?.DeepClone();

        CanonicalJson.Serialize(reversed).Should().Be(ProductMapJson.Serialize(doc));
    }

    [Fact]
    public void Parse_Garbage_ReturnsNull() => ProductMapJson.Parse("this is not json").Should().BeNull();

    [Fact]
    public void Parse_Array_ReturnsNull() => ProductMapJson.Parse("[]").Should().BeNull();

    [Fact]
    public void Parse_EmptyObject_ReturnsNull() => ProductMapJson.Parse("{}").Should().BeNull();

    [Fact]
    public void Parse_NullCollections_BecomeEmpty()
    {
        const string json = """
            {"schema":"reelbolt.product-map/1","name":null,"framework":{"name":"next","version":"","language":"typescript","styling":null,"packageManager":"npm","projectType":""},"architecture":null,"routingStrategy":null,"directories":null,"entryPoints":null,"routes":null,"components":null,"dependencies":{"runtime":null,"dev":null,"ui":null},"patterns":null,"provenance":null}
            """;

        ProductMapDocument? doc = ProductMapJson.Parse(json);

        doc.Should().NotBeNull();
        doc!.Directories.Should().BeEmpty();
        doc.EntryPoints.Should().BeEmpty();
        doc.Routes.Should().BeEmpty();
        doc.Components.Should().BeEmpty();
        doc.Patterns.Should().BeEmpty();
        doc.Framework.Styling.Should().BeEmpty();
        doc.Dependencies.Runtime.Should().BeEmpty();
        doc.Dependencies.Dev.Should().BeEmpty();
        doc.Dependencies.Ui.Should().BeEmpty();
        doc.Provenance.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_OmitsNullOptionalMembers()
    {
        var doc = new ProductMapDocument(
            ProductMapDocument.SchemaId,
            null,
            new ProductMapFramework("next", "", "", [], "npm", ""),
            null,
            null,
            [],
            [],
            [new ProductMapRoute("/", "app/page.tsx", "page", null, null, false, null)],
            [],
            new ProductMapDependencies([], [], []),
            [],
            new Dictionary<string, ProductMapProvenance>());

        JsonObject node = ProductMapJson.ToNode(doc);

        node.ContainsKey("name").Should().BeFalse();
        node.ContainsKey("architecture").Should().BeFalse();
        node.ContainsKey("routingStrategy").Should().BeFalse();

        JsonObject route = node["routes"]!.AsArray()[0]!.AsObject();
        route.ContainsKey("method").Should().BeFalse();
        route.ContainsKey("title").Should().BeFalse();
        route.ContainsKey("source").Should().BeFalse();
    }

    [Fact]
    public void Preview_cards_round_trip_as_camel_case()
    {
        ProductMapDocument doc = FullDocument() with
        {
            PreviewCards =
            [
                new ProductMapPreviewCard("Core", "Components", "Button", "700x360",
                    "components/core/core.card.html", "Save", ["logo.svg"], true),
            ],
        };

        JsonObject node = ProductMapJson.ToNode(doc);
        JsonObject card = node["previewCards"]!.AsArray()[0]!.AsObject();

        card["runtimeRendered"]!.GetValue<bool>().Should().BeTrue();
        card["assets"]!.AsArray().Select(asset => asset!.GetValue<string>()).Should().Equal("logo.svg");

        ProductMapDocument? parsed = ProductMapJson.Parse(ProductMapJson.Serialize(doc));
        parsed!.PreviewCards.Should().BeEquivalentTo(doc.PreviewCards);
    }

    [Fact]
    public void Serialize_OmitsEmptyPreviewCards()
    {
        ProductMapJson.ToNode(FullDocument()).ContainsKey("previewCards").Should().BeFalse();
        ProductMapJson.ToNode(FullDocument() with { PreviewCards = [] }).ContainsKey("previewCards").Should().BeFalse();
    }

    private static ProductMapDocument FullDocument() => new(
        ProductMapDocument.SchemaId,
        "Acme",
        new ProductMapFramework("next", "14.2.5", "typescript", ["tailwind"], "pnpm", "marketing site"),
        "App Router marketing site",
        "file conventions",
        [new ProductMapDirectory("app", "routes")],
        [new ProductMapEntryPoint("app/layout.tsx", "root layout")],
        [
            new ProductMapRoute("/", "app/page.tsx", "page", null, "Home", false, "llm"),
            new ProductMapRoute("/api/hello", "app/api/hello/route.ts", "api", "GET", null, false, "llm"),
        ],
        [new ProductMapComponent("Button", "components/ui/Button.tsx", "primary button", ["label"], true, "llm")],
        new ProductMapDependencies(
            [new ProductMapDependency("next", "14.2.5")],
            [new ProductMapDependency("typescript", "")],
            ["tailwind"]),
        ["server components"],
        new Dictionary<string, ProductMapProvenance>
        {
            ["framework"] = new("llm", []),
            ["routes"] = new("llm", []),
        });
}
