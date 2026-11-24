using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class StyleDictionaryImporterTests
{
    [Fact]
    public void Json_config_imports_only_files_matching_source()
    {
        ImportFile config = F("config.json", "{\"source\":[\"tokens/**/*.json\"],\"platforms\":{\"css\":{\"transformGroup\":\"css\"}}}");
        ImportFile baseToken = F("tokens/color/base.json", "{\"color\":{\"brand\":{\"primary\":{\"value\":\"#6d28d9\",\"type\":\"color\"}}}}");
        ImportFile hidden = F("other/hidden.json", "{\"color\":{\"hidden\":{\"$type\":\"color\",\"$value\":\"#ff0000\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, baseToken, hidden]);

        DesignDocument.FindToken(result.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.hidden").Should().BeNull();
        result.Sources.Select(source => source.Path).Should().Equal("config.json");
    }

    [Fact]
    public void Source_glob_is_relative_to_the_config_directory()
    {
        ImportFile config = F("design/config.json", "{\"source\":[\"tokens/*.json\"],\"platforms\":{}}");
        ImportFile inDesign = F("design/tokens/a.json", "{\"color\":{\"brand\":{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}");
        ImportFile atRoot = F("tokens/a.json", "{\"color\":{\"hidden\":{\"$type\":\"color\",\"$value\":\"#ff0000\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, inDesign, atRoot]);

        DesignDocument.FindToken(result.Design!, "color.brand").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.hidden").Should().BeNull();
    }

    [Fact]
    public void Js_config_is_never_evaluated()
    {
        const string evil = "const cp = require('child_process'); cp.execSync('touch ' + process.env.PWNED); module.exports = { source: ['tokens/**/*.json'], platforms: {} };";
        ImportFile code = F("evil.config.js", evil);
        ImportFile token = F("tokens/a.json", "{\"color\":{\"base\":{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}");

        var importer = new StyleDictionaryImporter();
        byte[] head = Encoding.UTF8.GetBytes(evil);
        importer.CanRead("evil.config.js", head).Should().BeTrue();

        DesignImportResult result = importer.Import([code], [code, token]);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "SD_CONFIG_CODE_NOT_EXECUTED");
        result.Diagnostics.Should().NotContain(diagnostic => diagnostic.Severity == ImportDiagnosticSeverity.Error);
        DesignDocument.FindToken(result.Design!, "color.base").Should().NotBeNull();

        string serialized = JsonSerializer.Serialize(result);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
    }

    [Fact]
    public void Legacy_value_dialect_is_imported()
    {
        ImportFile config = F("config.json", "{\"source\":[\"tokens/*.json\"],\"platforms\":{}}");
        ImportFile token = F("tokens/a.json", "{\"color\":{\"accent\":{\"value\":\"#ff5722\",\"type\":\"color\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, token]);

        DesignDocument.FindToken(result.Design!, "color.accent").Should().NotBeNull();
    }

    [Fact]
    public void Include_with_node_modules_is_ignored()
    {
        ImportFile config = F("config.json", "{\"source\":[\"tokens/**/*.json\"],\"include\":[\"node_modules/@acme/tokens/**/*.json\"],\"platforms\":{}}");
        ImportFile token = F("tokens/a.json", "{\"color\":{\"brand\":{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}");
        ImportFile included = F("node_modules/@acme/tokens/x.json", "{\"color\":{\"secret\":{\"$type\":\"color\",\"$value\":\"#000000\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, token, included]);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "SD_INCLUDE_IGNORED");
        DesignDocument.FindToken(result.Design!, "color.secret").Should().BeNull();
        DesignDocument.FindToken(result.Design!, "color.brand").Should().NotBeNull();
    }

    [Fact]
    public void Unsafe_globs_are_rejected_not_used()
    {
        ImportFile config = F("config.json", "{\"source\":[\"../../etc/*.json\",\"/abs/*.json\",\"tokens/**/*.json\"],\"platforms\":{}}");
        ImportFile token = F("tokens/a.json", "{\"color\":{\"brand\":{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, token]);

        result.Diagnostics.Count(diagnostic => diagnostic.Code == "SD_GLOB_REJECTED").Should().Be(2);
        DesignDocument.FindToken(result.Design!, "color.brand").Should().NotBeNull();
    }

    [Fact]
    public void Oversized_config_is_refused_before_parse()
    {
        string huge = "{\"source\":[\"" + new string('a', 300 * 1024) + "\"],\"platforms\":{}}";
        ImportFile config = F("config.json", huge);

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config]);

        result.Diagnostics.Should().Contain(
            diagnostic => diagnostic.Code == "IMPORT_TOO_LARGE" && diagnostic.Severity == ImportDiagnosticSeverity.Error);
        result.Design.Should().BeNull();
    }

    [Fact]
    public void Malformed_config_never_throws()
    {
        var importer = new StyleDictionaryImporter();
        ImportFile truncated = F("config.json", "{\"source\":[\"tokens/");
        ImportFile deep = F("config.json", new string('[', 100) + new string(']', 100));
        ImportFile number = F("config.json", "{\"source\":5,\"platforms\":{}}");

        foreach (ImportFile file in new[] { truncated, deep, number })
        {
            DesignImportResult result = importer.Import([file], [file]);
            result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "SD_CONFIG_INVALID");
        }
    }

    [Fact]
    public void Matches_are_capped_at_200()
    {
        ImportFile config = F("config.json", "{\"source\":[\"tokens/*.json\"],\"platforms\":{}}");
        var files = new List<ImportFile> { config };
        for (int i = 0; i < 300; i++)
            files.Add(F($"tokens/t{i}.json", $"{{\"color\":{{\"c{i}\":{{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}}}}"));

        DesignImportResult result = new StyleDictionaryImporter().Import([config], files);

        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "TRUNCATED");
        DesignDocument.Tokens(result.Design!).Count().Should().BeLessThanOrEqualTo(200);
    }

    [Theory]
    [InlineData("config.json", "{\"source\":[\"a\"],\"platforms\":{}}", true)]
    [InlineData("sd.config.js", "module.exports={platforms:{}}", true)]
    [InlineData("config.json", "{\"a\":1}", false)]
    [InlineData("tailwind.config.js", "module.exports={platforms:{}}", false)]
    [InlineData("webpack.config.js", "module.exports={}", false)]
    public void CanRead_theory(string path, string head, bool expected)
    {
        ImportFile probe = F(path, head);
        bool actual = new StyleDictionaryImporter().CanRead(path, probe.Content.Span);
        actual.Should().Be(expected);
    }

    [Fact]
    public void Output_passes_design_document_validator()
    {
        ImportFile config = F("config.json", "{\"source\":[\"tokens/**/*.json\"],\"platforms\":{}}");
        ImportFile color = F("tokens/a.json", "{\"color\":{\"brand\":{\"$type\":\"color\",\"$value\":\"#6d28d9\"}}}");
        ImportFile number = F("tokens/b.json", "{\"radius\":{\"card\":{\"value\":12,\"type\":\"number\"}}}");

        DesignImportResult result = new StyleDictionaryImporter().Import([config], [config, color, number]);

        DesignValidationResult validation = DesignDocumentValidator.Validate(
            result.Design!,
            new DesignValidationContext(_ => true, new HashSet<string>()));

        Assert.True(
            validation.IsValid,
            string.Join("; ", validation.Violations.Select(violation => $"{violation.Code} {violation.Path} {violation.Message}")));
    }

    private static ImportFile F(string path, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return new ImportFile(path, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
    }
}
