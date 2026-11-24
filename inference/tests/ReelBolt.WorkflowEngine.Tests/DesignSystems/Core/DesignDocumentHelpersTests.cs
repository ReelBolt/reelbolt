using System;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

/// <summary>
/// The read/write helpers over the dynamic design document: construction, token enumeration with
/// its dot paths, lookup and token writing (including the provenance extension).
/// </summary>
public class DesignDocumentHelpersTests
{
    private static JsonNode ColorValue() => JsonNode.Parse("""{ "hex": "#2563eb", "colorSpace": "srgb" }""")!;

    private static JsonNode DimensionValue() => JsonNode.Parse("""{ "value": 16, "unit": "px" }""")!;

    private static TokenProvenance Provenance(string method = "static") => new(method, null, null, null);

    [Fact]
    public void New_CarriesTheSchema_AndNameWhenGiven()
    {
        JsonObject unnamed = DesignDocument.New();
        unnamed["schema"]!.GetValue<string>().Should().Be("reelbolt.design-system/1");
        unnamed.ContainsKey("name").Should().BeFalse();

        JsonObject named = DesignDocument.New("Acme");
        named["name"]!.GetValue<string>().Should().Be("Acme");
    }

    [Fact]
    public void Tokens_EnumeratesOnlyValueBearingNodes_WithDotPaths()
    {
        JsonObject root = DesignDocument.New();
        DesignDocument.SetToken(root, "color.primary", "color", ColorValue(), Provenance(), 0.9);
        DesignDocument.SetToken(root, "color.brand.secondary", "color", ColorValue(), Provenance(), 0.8);
        DesignDocument.SetToken(root, "spacing.md", "dimension", DimensionValue(), Provenance(), 0.7);

        var tokens = DesignDocument.Tokens(root).ToList();

        tokens.Should().HaveCount(3);
        tokens.Select(token => token.Path)
            .Should().Equal("color.primary", "color.brand.secondary", "spacing.md");
        tokens.Select(token => token.Type).Should().Equal("color", "color", "dimension");
    }

    [Fact]
    public void FindToken_FindsNestedTokens_AndReturnsNullWhenMissing()
    {
        JsonObject root = DesignDocument.New();
        DesignDocument.SetToken(root, "color.brand.primary", "color", ColorValue(), Provenance(), 0.5);

        DesignDocument.FindToken(root, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(root, "color.brand").Should().NotBeNull();
        DesignDocument.FindToken(root, "color.missing").Should().BeNull();
        DesignDocument.FindToken(root, "color.brand.primary.deeper").Should().BeNull();
    }

    [Fact]
    public void SetToken_CreatesGroups_AndWritesProvenance()
    {
        var sourceId = Guid.NewGuid();
        JsonObject root = DesignDocument.New();
        DesignDocument.SetToken(
            root,
            "color.brand.primary",
            "color",
            ColorValue(),
            new TokenProvenance("llm", "figma", sourceId, "frame:3"),
            conf: 0.8,
            locked: true,
            original: "#ffffff");

        var token = (JsonObject)root["color"]!["brand"]!["primary"]!;
        token["$type"]!.GetValue<string>().Should().Be("color");
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#2563eb");

        var reelbolt = (JsonObject)token["$extensions"]!["com.reelbolt"]!;
        reelbolt["conf"]!.GetValue<double>().Should().Be(0.8);
        reelbolt["locked"]!.GetValue<bool>().Should().BeTrue();
        reelbolt["orig"]!.GetValue<string>().Should().Be("#ffffff");

        var provenance = (JsonObject)reelbolt["prov"]!;
        provenance["method"]!.GetValue<string>().Should().Be("llm");
        provenance["importer"]!.GetValue<string>().Should().Be("figma");
        provenance["sourceId"]!.GetValue<string>().Should().Be(sourceId.ToString("D"));
        provenance["loc"]!.GetValue<string>().Should().Be("frame:3");

        JsonObject withoutOriginal = DesignDocument.New();
        DesignDocument.SetToken(withoutOriginal, "spacing.md", "dimension", DimensionValue(), Provenance(), 0.5);
        var written = (JsonObject)withoutOriginal["spacing"]!["md"]!;
        var extension = (JsonObject)written["$extensions"]!["com.reelbolt"]!;
        extension.ContainsKey("orig").Should().BeFalse();
        extension["locked"]!.GetValue<bool>().Should().BeFalse();
    }
}
