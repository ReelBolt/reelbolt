using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class DesignImportRegistryTests
{
    [Fact]
    public void All_is_ordered_dtcg_css_theme_design_md()
    {
        DesignImportRegistry.All.Select(importer => importer.Id)
            .Should().Equal("dtcg", "css-theme", "design-md", "tokens-studio", "shadcn-registry", "storybook-index", "style-dictionary", "tailwind-v3");
    }

    [Fact]
    public void ImportAll_mixed_set_yields_one_valid_document()
    {
        ImportFile css = File("app/globals.css", ":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }");
        ImportFile dtcg = File("tokens.json", "{ \"color\": { \"background\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#0b0b0f\", \"colorSpace\": \"srgb\" } } } }");
        ImportFile md = File("DESIGN.md", "---\nname: Acme\ntypography:\n  heading:\n    fontFamily: Inter\n---\n## Overview\nA clean brand for developers.\n");

        DesignImportResult result = DesignImportRegistry.ImportAll([css, dtcg, md]);

        result.Design.Should().NotBeNull();
        DesignDocumentValidator.Validate(result.Design!, new DesignValidationContext(_ => true, new HashSet<string>()))
            .IsValid.Should().BeTrue();

        DesignDocument.FindToken(result.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.background").Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "typography.heading").Should().NotBeNull();

        JsonObject voice = result.Design!["voice"]!.AsObject();
        voice["summary"]!.GetValue<string>().Should().Contain("clean brand");

        foreach (DesignTokenRef token in DesignDocument.Tokens(result.Design!))
        {
            JsonObject prov = token.Token["$extensions"]!.AsObject()["com.reelbolt"]!.AsObject()["prov"]!.AsObject();
            prov["sourceId"].Should().BeNull();
        }

        result.Sources.Should().HaveCount(3);
        ImportFile[] all = [css, dtcg, md];
        foreach (ImportSourceRef source in result.Sources)
        {
            ImportFile file = all.Single(candidate => candidate.Path == source.Path);
            source.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)));
            source.Importer.Should().BeOneOf("css-theme", "dtcg", "design-md");
        }
    }

    [Fact]
    public void ImportAll_recomputes_sha256_not_trusting_ImportFile_Sha256()
    {
        ImportFile file = new(
            "tokens.json",
            Encoding.UTF8.GetBytes("{ \"color\": { \"primary\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#6d28d9\", \"colorSpace\": \"srgb\" } } } }"),
            "deadbeef",
            null);

        DesignImportResult result = DesignImportRegistry.ImportAll([file]);

        result.Sources.Should().ContainSingle();
        result.Sources[0].Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)));
        result.Sources[0].Sha256.Should().NotBe("deadbeef");
    }

    [Fact]
    public void ImportAll_redacts_secret()
    {
        ImportFile file = File(
            "tokens.json",
            "{ \"color\": { \"primary\": { \"$type\": \"color\", \"$value\": \"{sk-" + "abcdefghijklmnopqrstuvwxyz123}\" } } }");

        DesignImportResult result = DesignImportRegistry.ImportAll([file]);

        result.Design.Should().NotBeNull();
        DesignDocument.FindToken(result.Design!, "color.primary")!["$value"]!.GetValue<string>()
            .Should().Contain("[redacted]");
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "SECRET_REDACTED");
    }

    [Fact]
    public void ImportAll_empty_returns_empty_result()
    {
        DesignImportResult result = DesignImportRegistry.ImportAll(Array.Empty<ImportFile>());

        result.Design.Should().BeNull();
        result.ProductMap.Should().BeNull();
        result.Sources.Should().BeEmpty();
        result.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void ImportAll_nothing_recognized_emits_diagnostic()
    {
        ImportFile file = new("x.bin", new byte[] { 0x00, 0x01, 0x02, 0xFF }, string.Empty, null);

        DesignImportResult result = DesignImportRegistry.ImportAll([file]);

        result.Design.Should().BeNull();
        result.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "IMPORT_NOTHING_RECOGNIZED");
    }

    [Fact]
    public void ImportAll_redacts_secret_shaped_json_keys_and_diagnostic_paths()
    {
        string dtcg = "{ \"color\": { \"sk-" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaa\": { \"$type\": \"color\", \"$value\": \"#ffffff\" }, \"ok\": { \"$type\": \"color\", \"$value\": \"#000000\" } } }";

        DesignImportResult result = DesignImportRegistry.ImportAll([File("tokens.json", dtcg)]);

        result.Design!.ToJsonString().Should().NotContain("sk-aaaa");
        result.Diagnostics.Should().OnlyContain(d => !d.Path.Contains("sk-aaaa") && !d.Message.Contains("sk-aaaa"));
        DesignDocument.FindToken(result.Design!, "color.ok").Should().NotBeNull();
        result.Diagnostics.Should().Contain(d => d.Code == "SECRET_REDACTED");
    }

    [Fact]
    public void ImportAll_redacts_the_path_of_a_diagnostic_an_importer_emits()
    {
        var importer = new LeakyImporter();

        DesignImportResult result = DesignImportRegistry.ImportAll([File("a.txt", "x")], [importer]);

        result.Diagnostics.Should().Contain(d => d.Code == "LEAK");
        result.Diagnostics.Should().OnlyContain(d => !d.Path.Contains("ghp_") && !d.Message.Contains("ghp_"));
    }

    [Fact]
    public void ImportAll_redacts_a_secret_straddling_the_message_and_path_clamp_boundary()
    {
        const string Secret = "ghp_" + "abcdefghijklmnopqrstuvwxyz1234567890";
        string message = new string('m', 500) + Secret;
        string path = new string('p', 245) + Secret;
        var importer = new LeakyImporter(path, message);

        DesignImportResult result = DesignImportRegistry.ImportAll([File("a.txt", "x")], [importer]);

        ImportDiagnostic leak = result.Diagnostics.Single(d => d.Code == "LEAK");
        leak.Message.Should().NotContain("ghp_");
        leak.Path.Should().NotContain("ghp_");
        leak.Message.Length.Should().BeLessThanOrEqualTo(512);
        leak.Path.Length.Should().BeLessThanOrEqualTo(256);
    }

    private sealed class LeakyImporter : IDesignImporter
    {
        private readonly string _path;
        private readonly string _message;

        public LeakyImporter()
            : this("a/ghp_" + "abcdefghijklmnopqrstuvwxyz1234/b", "m ghp_" + "abcdefghijklmnopqrstuvwxyz1234")
        {
        }

        public LeakyImporter(string path, string message)
        {
            _path = path;
            _message = message;
        }

        public string Id => "leaky";

        public bool CanRead(string path, ReadOnlySpan<byte> head) => true;

        public DesignImportResult Import(IReadOnlyList<ImportFile> files) =>
            new(
                null,
                null,
                [],
                [new ImportDiagnostic(ImportDiagnosticSeverity.Warn, "LEAK", _path, _message)]);
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);
}
