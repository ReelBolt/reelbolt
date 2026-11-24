using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Export;

/// <summary>
/// Pins <c>import(export(doc))</c> for every format. Each test names the kinds its format can
/// express and the exact lossy list, so a change to either side of the round trip must change the
/// list deliberately.
/// </summary>
public sealed class RoundTripTests
{
    private static readonly DtcgImporter Dtcg = new();
    private static readonly CssThemeImporter Css = new();
    private static readonly DesignMdImporter DesignMd = new();

    private static readonly string[] DtcgExpressible = ["color", "fontFamily", "fontWeight", "cubicBezier", "number"];
    private static readonly string[] CssExpressible = ["color", "dimension", "fontFamily", "duration", "cubicBezier"];

    [Fact]
    public void Json_RoundTrip_Equals_The_Original()
    {
        JsonObject original = Original();

        Assert.Equal(CanonicalJson.Serialize(original), CanonicalJson.Serialize(DesignExporter.ToJson(original)));
    }

    [Fact]
    public void Json_RoundTrip_Keeps_Z_Transform_And_The_Empty_Shadow()
    {
        JsonObject original = WithDerivedTokens();

        JsonObject roundTripped = (JsonObject)JsonNode.Parse(DesignExporter.ToJson(original).ToJsonString())!;

        AssertDerivedTokens(roundTripped);
    }

    [Fact]
    public void Dtcg_RoundTrip_Keeps_Z_Transform_Keyword_And_The_Empty_Shadow()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DesignExporter.ToDtcg(WithDerivedTokens()).ToJsonString());
        DesignImportResult imported = Dtcg.Import([new ImportFile("tokens.json", bytes, string.Empty, null)]);

        imported.Design.Should().NotBeNull();
        AssertDerivedTokens(imported.Design!);
    }

    private static JsonObject WithDerivedTokens()
    {
        JsonObject document = Original();
        var provenance = new TokenProvenance("static", "test", null, null);
        DesignDocument.SetToken(document, "z.drawer", "number", JsonValue.Create(1100), provenance, 1.0);
        DesignDocument.SetToken(document, "typography.transform.button", "keyword", JsonValue.Create("uppercase"), provenance, 1.0);
        DesignDocument.SetToken(document, "shadow.0", "shadow", new JsonArray(), provenance, 1.0);
        return document;
    }

    private static void AssertDerivedTokens(JsonObject document)
    {
        JsonObject zIndex = DesignDocument.FindToken(document, "z.drawer")!;
        zIndex["$type"]!.GetValue<string>().Should().Be("number");
        zIndex["$value"]!.GetValue<double>().Should().Be(1100);

        JsonObject transform = DesignDocument.FindToken(document, "typography.transform.button")!;
        transform["$type"]!.GetValue<string>().Should().Be("keyword");
        transform["$value"]!.GetValue<string>().Should().Be("uppercase");

        JsonObject shadow = DesignDocument.FindToken(document, "shadow.0")!;
        shadow["$type"]!.GetValue<string>().Should().Be("shadow");
        shadow["$value"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public void Dtcg_RoundTrip_Recovers_The_Reader_Supported_Kinds()
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportDtcg(original);

        Dictionary<string, List<string>> originalValues = NonAliasValuesByType(original);
        Dictionary<string, List<string>> roundValues = NonAliasValuesByType(roundTripped);

        foreach (string type in DtcgExpressible)
            Assert.True(ContainsAll(roundValues, type, originalValues[type]), $"DTCG lost the {type} kind");

        Assert.Equal(
            "{color.primary}",
            DesignDocument.FindToken(roundTripped, "component.button.background")!["$value"]!.GetValue<string>());
    }

    [Fact]
    public void Dtcg_RoundTrip_Recovers_Every_Token_Kind_Now_That_The_Object_Dialect_Is_Read()
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportDtcg(original);

        Dictionary<string, List<string>> originalValues = NonAliasValuesByType(original);
        Dictionary<string, List<string>> roundValues = NonAliasValuesByType(roundTripped);

        // The documented lossy list used to be dimension, duration, shadow and typography.
        Assert.Empty(DesignExporter.DtcgLossyTokenTypes);
        foreach (string type in new[] { "dimension", "duration", "shadow", "typography" })
            Assert.True(ContainsAll(roundValues, type, originalValues[type]), $"DTCG lost the {type} kind");
    }

    [Fact]
    public void Dtcg_Export_Carries_Modes_Voice_Assets_And_Fonts_In_The_Root_Extension()
    {
        JsonObject original = Original();
        JsonObject extension = (JsonObject)DesignExporter.ToDtcg(original)["$extensions"]!["com.reelbolt"]!;

        foreach (string key in new[] { "modes", "voice", "assets", "fonts" })
            Assert.Equal(CanonicalJson.Serialize(original[key]), CanonicalJson.Serialize(extension[key]));
    }

    [Fact]
    public void Dtcg_Export_Carries_Font_Files_In_The_Root_Extension()
    {
        JsonObject original = Original();
        original["fonts"]!["files"] = new JsonObject
        {
            ["Inter"] = "project-file:0f9a6a80-1e2b-4c3d-8f10-9a7b6c5d4e3f",
        };

        JsonObject extension = (JsonObject)DesignExporter.ToDtcg(original)["$extensions"]!["com.reelbolt"]!;

        Assert.Equal(CanonicalJson.Serialize(original["fonts"]), CanonicalJson.Serialize(extension["fonts"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Css_RoundTrip_Recovers_Colour_Dimension_FontFamily_Duration_And_Easing(bool tailwind)
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportCss(original, tailwind);

        Dictionary<string, List<string>> originalValues = NonAliasValuesByType(original);
        Dictionary<string, List<string>> roundValues = NonAliasValuesByType(roundTripped);

        foreach (string type in CssExpressible)
            Assert.True(ContainsAll(roundValues, type, originalValues[type]), $"CSS lost the {type} kind (tailwind={tailwind})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Css_RoundTrip_Drops_Exactly_The_Documented_Kinds(bool tailwind)
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportCss(original, tailwind);

        Dictionary<string, List<string>> originalValues = NonAliasValuesByType(original);
        Dictionary<string, List<string>> roundValues = NonAliasValuesByType(roundTripped);

        Assert.Equal(new[] { "shadow", "typography" }, DesignExporter.CssLossyTokenTypes);
        foreach (string type in DesignExporter.CssLossyTokenTypes)
            Assert.False(ContainsAll(roundValues, type, originalValues[type]), $"CSS unexpectedly kept the {type} kind");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Css_RoundTrip_Recovers_Dark_Mode_Overrides(bool tailwind)
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportCss(original, tailwind);

        // Hyphenated keys such as `on-surface` cannot survive the CSS dialect's `-` separator
        // (the reader turns them into `on.surface`), so equality is by value set; the hyphen-free
        // overrides keep their exact path.
        Assert.Equal(DarkValues(original), DarkValues(roundTripped));

        foreach (string path in new[] { "color.surface", "color.background", "color.border" })
        {
            Assert.Equal(
                CanonicalJson.Serialize(original["modes"]!["dark"]![path]),
                CanonicalJson.Serialize(roundTripped["modes"]!["dark"]![path]));
        }
    }

    private static HashSet<string> DarkValues(JsonObject document)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in (JsonObject)document["modes"]!["dark"]!)
            values.Add(CanonicalJson.Serialize(entry.Value));

        return values;
    }

    [Fact]
    public void DesignMd_RoundTrip_Recovers_Colour_Typography_Radius_Spacing_And_Voice()
    {
        JsonObject original = Original();
        JsonObject roundTripped = ImportDesignMd(original);

        // Colours: DESIGN.md flattens nested keys, so equality is by value.
        Assert.Equal(
            NonAliasValuesByType(original)["color"].ToHashSet(StringComparer.Ordinal),
            NonAliasValuesByType(roundTripped)["color"].ToHashSet(StringComparer.Ordinal));

        // Typography composites keep their family/size/weight/line-height/letter-spacing.
        Assert.Equal(
            CanonicalJson.Serialize(DesignDocument.FindToken(original, "typography.heading.h1")!["$value"]),
            CanonicalJson.Serialize(DesignDocument.FindToken(roundTripped, "typography.heading-h1")!["$value"]));

        Assert.Equal(TextStyleValues(original, "fontFamily"), TextStyleValues(roundTripped, "fontFamily"));
        Assert.Equal(TextStyleValues(original, "fontSize"), TextStyleValues(roundTripped, "fontSize"));
        Assert.Equal(TextStyleValues(original, "fontWeight"), TextStyleValues(roundTripped, "fontWeight"));

        foreach (string group in new[] { "radius", "spacing" })
        {
            foreach (DesignTokenRef token in DesignDocument.Tokens(original))
            {
                if (!string.Equals(token.Type, "dimension", StringComparison.Ordinal)
                    || !token.Path.StartsWith(group + ".", StringComparison.Ordinal))
                {
                    continue;
                }

                JsonObject? recovered = DesignDocument.FindToken(roundTripped, token.Path);
                Assert.NotNull(recovered);
                Assert.Equal(CanonicalJson.Serialize(token.Value), CanonicalJson.Serialize(recovered!["$value"]));
            }
        }

        foreach (string key in new[] { "summary", "do", "dont", "tone" })
        {
            Assert.Equal(
                CanonicalJson.Serialize(original["voice"]![key]),
                CanonicalJson.Serialize(roundTripped["voice"]![key]));
        }
    }

    [Fact]
    public void DesignMd_RoundTrip_Drops_Exactly_The_Documented_Fields()
    {
        JsonObject roundTripped = ImportDesignMd(Original());

        Assert.Equal(
            new[] { "modes", "assets", "fonts", "shadow", "motion", "component" },
            DesignExporter.DesignMdLossyFields);

        Assert.Null(roundTripped["modes"]);
        Assert.Null(roundTripped["assets"]);
        Assert.Null(roundTripped["fonts"]);

        foreach (DesignTokenRef token in DesignDocument.Tokens(roundTripped))
        {
            Assert.DoesNotContain(token.Type, new[] { "shadow", "duration", "cubicBezier" });
            Assert.False(token.Path.StartsWith("component.", StringComparison.Ordinal));
        }
    }

    private static JsonObject ImportDtcg(JsonObject document)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DesignExporter.ToDtcg(document).ToJsonString());
        return Dtcg.Import([new ImportFile("tokens.json", bytes, string.Empty, null)]).Design!;
    }

    private static JsonObject ImportCss(JsonObject document, bool tailwind)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DesignExporter.ToCss(document, tailwind));
        return Css.Import([new ImportFile("tokens.css", bytes, string.Empty, null)]).Design!;
    }

    private static JsonObject ImportDesignMd(JsonObject document)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(DesignExporter.ToDesignMd(document));
        return DesignMd.Import([new ImportFile("DESIGN.md", bytes, string.Empty, null)]).Design!;
    }

    private static Dictionary<string, List<string>> NonAliasValuesByType(JsonObject document)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            if (IsAlias(token.Value))
                continue;

            if (!values.TryGetValue(token.Type, out List<string>? list))
                values[token.Type] = list = [];

            list.Add(CanonicalJson.Serialize(token.Value));
        }

        return values;
    }

    /// <summary>Every family / size / weight the document states, whether as a token or inside a typography composite.</summary>
    private static HashSet<string> TextStyleValues(JsonObject document, string key)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (DesignTokenRef token in DesignDocument.Tokens(document))
        {
            string? tokenType = key switch
            {
                "fontFamily" => "fontFamily",
                "fontSize" => "dimension",
                "fontWeight" => "fontWeight",
                _ => null,
            };

            if (tokenType is not null
                && string.Equals(token.Type, tokenType, StringComparison.Ordinal)
                && token.Path.StartsWith("typography." + SubPath(key) + ".", StringComparison.Ordinal))
            {
                values.Add(CanonicalJson.Serialize(token.Value));
            }

            if (string.Equals(token.Type, "typography", StringComparison.Ordinal)
                && token.Value is JsonObject typography
                && typography[key] is JsonNode sub)
            {
                values.Add(CanonicalJson.Serialize(sub));
            }
        }

        return values;
    }

    private static string SubPath(string key) => key switch
    {
        "fontFamily" => "family",
        "fontSize" => "size",
        "fontWeight" => "weight",
        _ => key,
    };

    private static bool ContainsAll(Dictionary<string, List<string>> haystack, string type, IEnumerable<string> values)
    {
        if (!haystack.TryGetValue(type, out List<string>? list))
            return false;

        var set = new HashSet<string>(list, StringComparer.Ordinal);
        return values.All(set.Contains);
    }

    private static bool IsAlias(JsonNode? value) =>
        value is JsonValue jsonValue
        && jsonValue.GetValueKind() == System.Text.Json.JsonValueKind.String
        && jsonValue.GetValue<string>() is string text
        && text.Length >= 3
        && text[0] == '{'
        && text[^1] == '}';

    private static JsonObject Original()
    {
        string path = Path.Combine(FixtureDirectory(), "design-valid.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");
}
