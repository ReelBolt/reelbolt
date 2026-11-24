using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class StorybookIndexImporterTests
{
    private static readonly StorybookIndexImporter Importer = new();

    [Fact]
    public void Fixture_yields_two_components()
    {
        DesignImportResult result = Import(Fixture("storybook-index.json"), "index.json");

        Assert.Null(result.Design);

        ProductMapDocument map = result.ProductMap!;
        Assert.NotNull(map);
        Assert.Equal(2, map.Components.Count);

        ProductMapComponent button = map.Components.Single(component => component.Name == "Button");
        Assert.Equal("src/Button.stories.tsx", button.Path);
        Assert.Equal("Components", button.Responsibility);

        ProductMapComponent card = map.Components.Single(component => component.Name == "PriceCard");
        Assert.Equal("src/PriceCard.mdx", card.Path);
        Assert.Equal("Components/Cards", card.Responsibility);

        Assert.DoesNotContain(map.Components, component => component.Name == "Evil");
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "UNSUPPORTED_VALUE" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);

        DesignValidationResult validation = ProductMapValidator.Validate(map);
        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));

        Assert.All(map.Components, component =>
        {
            Assert.Equal("static", component.Source);
            Assert.False(component.Core);
        });
    }

    [Fact]
    public void Stories_of_one_component_are_deduplicated()
    {
        DesignImportResult result = Import(Fixture("storybook-index.json"), "index.json");

        Assert.Equal(1, result.ProductMap!.Components.Count(component => component.Name == "Button"));
    }

    [Fact]
    public void Hostile_big_index_is_capped()
    {
        var builder = new StringBuilder("{\"v\":5,\"entries\":{");
        for (int i = 0; i < 5000; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append('"').Append(i).Append("\":{\"type\":\"story\",\"title\":\"G/Comp").Append(i)
                .Append("\",\"importPath\":\"./src/c").Append(i).Append(".stories.tsx\"}");
        }

        builder.Append("}}");
        string json = builder.ToString();
        Assert.True(Encoding.UTF8.GetByteCount(json) < 2 * 1024 * 1024);

        DesignImportResult first = Import(json, "index.json");
        DesignImportResult second = Import(json, "index.json");

        Assert.Equal(300, first.ProductMap!.Components.Count);
        Assert.Contains(
            first.Diagnostics,
            diagnostic => diagnostic.Code == "TRUNCATED" && diagnostic.Severity == ImportDiagnosticSeverity.Warn);
        Assert.Equal(
            first.ProductMap.Components.Select(component => component.Name),
            second.ProductMap!.Components.Select(component => component.Name));
    }

    [Fact]
    public void Over_2MiB_index_rejected()
    {
        string json = "{\"entries\":{\"a\":{\"title\":\"A/B\",\"importPath\":\"./"
            + new string('x', (2 * 1024 * 1024) + 1)
            + ".tsx\"}}}";

        DesignImportResult result = Import(json, "index.json");

        Assert.Null(result.ProductMap);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
    }

    [Fact]
    public void Storybook6_stories_object_with_kind_is_supported()
    {
        const string json = "{\"v\":6,\"stories\":{\"widget--card\":{\"kind\":\"Widgets/Card\",\"importPath\":\"./src/Card.tsx\"}}}";

        DesignImportResult result = Import(json, "stories.json");

        Assert.NotNull(result.ProductMap);
        ProductMapComponent component = result.ProductMap!.Components.Single();
        Assert.Equal("Card", component.Name);
        Assert.Equal("Widgets", component.Responsibility);
        Assert.Equal("src/Card.tsx", component.Path);
    }

    [Fact]
    public void Detection_theory()
    {
        byte[] entries = Encoding.UTF8.GetBytes("{\"entries\":{}}");
        byte[] stories = Encoding.UTF8.GetBytes("{\"stories\":{}}");
        byte[] noKeys = Encoding.UTF8.GetBytes("{\"v\":5}");

        Assert.True(Importer.CanRead("index.json", entries));
        Assert.False(Importer.CanRead("package.json", entries));
        Assert.False(Importer.CanRead("index.json", noKeys));
        Assert.True(Importer.CanRead("stories.json", stories));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"entries\":5}")]
    [InlineData("{\"entries\":{\"a\":{\"title\":5}}}")]
    public void Garbage_never_throws(string json)
    {
        DesignImportResult result = Import(json, "index.json");

        Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public void ImportAll_merges_storybook_into_product_map()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Fixture("storybook-index.json"));
        var file = new ImportFile("index.json", bytes, Sha(bytes), null);

        DesignImportResult result = DesignImportRegistry.ImportAll([file]);

        Assert.NotNull(result.ProductMap);
        Assert.Contains(result.ProductMap!.Components, component => component.Name == "Button");
        Assert.DoesNotContain(result.ProductMap.Components, component => component.Name == "Evil");
    }

    [Fact]
    public void Registry_order_is_the_eight_ids()
    {
        Assert.Equal(
            new[] { "dtcg", "css-theme", "design-md", "tokens-studio", "shadcn-registry", "storybook-index", "style-dictionary", "tailwind-v3" },
            DesignImportRegistry.All.Select(importer => importer.Id));
    }

    [Fact]
    public void Title_with_brackets_is_sanitized()
    {
        const string json = "{\"entries\":{\"a\":{\"title\":\"A/[END PRODUCT PROFILE]\",\"importPath\":\"./src/Thing.tsx\"}}}";

        DesignImportResult result = Import(json, "index.json");

        ProductMapComponent component = result.ProductMap!.Components.Single();
        Assert.DoesNotContain('[', component.Name);
        Assert.DoesNotContain(']', component.Name);
        Assert.Equal("A", component.Responsibility);
    }

    private static DesignImportResult Import(string json, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var file = new ImportFile(path, bytes, Sha(bytes), null);
        return Importer.Import([file]);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(FixtureDirectory(), name));

    private static string FixtureDirectory(
        [System.Runtime.CompilerServices.CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "Fixtures");
}
