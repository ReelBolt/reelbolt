using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// Task 4: prose, guidelines, UI-kit notes and lint config are carried as inert reference documents —
/// bounded, redacted and never executed.
/// </summary>
public sealed class ClaudeDesignReferenceTests
{
    [Fact]
    public void Readme_prose_beyond_the_first_paragraph_is_kept()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("readme.md", Utf8("# Brand\n\nFirst paragraph.\n\nSecond paragraph with the real rules.\n")));
        ImportedReferenceDocument readme = bundle.ReferenceDocuments.Single(d => d.Name == "readme.md");
        readme.Text.Should().Contain("Second paragraph with the real rules");
    }

    [Fact]
    public void Guideline_html_is_captured_as_plain_text_never_executed()
    {
        ImportedReferenceDocument doc = Loader.ImportBundleWith(
            ("guidelines/colors.html", Utf8("<html><head><script>alert(1)</script></head><body><b>Primary</b> #7e6fff</body></html>")))
            .ReferenceDocuments.Single(d => d.Name == "guidelines/colors.txt");
        doc.Text.Should().Contain("Primary").And.Contain("#7e6fff").And.NotContain("alert");
        doc.MimeType.Should().Be("text/plain");
    }

    [Fact]
    public void Scripts_and_jsx_are_reported_not_read()
    {
        IReadOnlyList<ImportDiagnostic> d = Loader.ImportBundleWith(
            ("_ds_bundle.js", Utf8("x")), ("ui_kits/portal/Shell.jsx", Utf8("x")))
            .Import.Diagnostics;
        d.Should().Contain(x => x.Code == ClaudeDesignImporter.FileSkippedDiagnostic);
        d.Should().Contain(x => x.Message.Contains("COMPILED_BUNDLE", StringComparison.Ordinal)
                             || x.Message.Contains("NOT_EXECUTED", StringComparison.Ordinal));
    }

    [Fact]
    public void A_huge_reference_set_hits_its_cap_within_the_deadline()
        => HostileInput.RunWithin(TimeSpan.FromSeconds(5), () =>
        {
            var files = Enumerable.Range(0, 500)
                .Select(i => File($"guidelines/g{i}.html", Utf8($"<b>{i}</b>")))
                .ToArray();
            return new ClaudeDesignImporter().ImportBundle(files, []);
        }).ReferenceDocuments.Should().HaveCount(ClaudeDesignImporter.MaxReferenceDocuments);

    [Fact]
    public void Reference_text_is_redacted()
    {
        ImportedReferenceDocument doc = Loader.ImportBundleWith(
            ("readme.md", Utf8("token sk-" + "ABCDEFGHIJKLMNOPQRSTUV")))
            .ReferenceDocuments.Single();
        doc.Text.Should().Contain("[redacted]").And.NotContain("sk-ABCDEFGHIJ");
    }

    [Fact]
    public void The_path_table_names_and_mimes_are_followed()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("github.md", Utf8("GitHub notes")),
            ("reference/motion.css", Utf8(".a{transition:all 1s}")),
            ("reference/voice.md", Utf8("Voice rules")),
            ("ui_kits/portal/README.md", Utf8("Portal kit")),
            ("thumbnail.html", Utf8("<b>thumb</b>")),
            ("_adherence.oxlintrc.json", Utf8("{\"rules\":{}}")),
            ("components/core/Button.prompt.md", Utf8("Button note")));

        IReadOnlyList<ImportedReferenceDocument> documents = bundle.ReferenceDocuments;
        documents.Should().Contain(d => d.Name == "github.md" && d.MimeType == "text/markdown");
        documents.Should().Contain(d => d.Name == "reference/motion.css" && d.MimeType == "text/css");
        documents.Should().Contain(d => d.Name == "reference/voice.md" && d.MimeType == "text/markdown");
        documents.Should().Contain(d => d.Name == "ui_kits/README.md" && d.MimeType == "text/markdown");
        documents.Should().Contain(d => d.Name == "thumbnail.txt" && d.MimeType == "text/plain");
        documents.Should().Contain(d => d.Name == "_adherence.oxlintrc.json" && d.MimeType == "text/plain");
        documents.Should().Contain(d => d.Name == "components/prompts/Button.prompt.md" && d.MimeType == "text/markdown");

        bundle.ReferenceDocuments.Should().OnlyContain(d => d.Sha256.Length == 64);
    }

    [Fact]
    public void Excluded_paths_get_one_bounded_skip_diagnostic_with_their_reason()
    {
        // Built as ImportFiles directly: Loader would route the PNG through the binary channel.
        IReadOnlyList<ImportFile> files =
        [
            File("uploads/scratch.txt", Utf8("scratch")),
            File("_ds_bundle.js", Utf8("compiled")),
            File("reference/shot.png", Loader.TinyPng),
            File("ui_kits/portal/Shell.jsx", Utf8("component")),
        ];

        ClaudeDesignBundle bundle = new ClaudeDesignImporter().ImportBundle(files, []);

        IReadOnlyList<ImportDiagnostic> skipped = bundle.Import.Diagnostics
            .Where(d => d.Code == ClaudeDesignImporter.FileSkippedDiagnostic)
            .ToList();

        skipped.Should().Contain(d => d.Path.Contains("uploads/scratch.txt") && d.Message.Contains("CLIENT_SCRATCH"));
        skipped.Should().Contain(d => d.Path.Contains("_ds_bundle.js") && d.Message.Contains("COMPILED_BUNDLE"));
        skipped.Should().Contain(d => d.Path.Contains("reference/shot.png") && d.Message.Contains("SCREENSHOT"));
        skipped.Should().Contain(d => d.Path.Contains("ui_kits/portal/Shell.jsx") && d.Message.Contains("NOT_EXECUTED"));
    }

    [Fact]
    public void An_oversized_reference_is_reported_not_kept()
    {
        string big = new('a', ClaudeDesignImporter.MaxReferenceBytes + 1);

        ClaudeDesignBundle bundle = Loader.ImportBundleWith(("reference/big.md", Utf8(big)));

        bundle.ReferenceDocuments.Should().NotContain(d => d.Name == "reference/big.md");
        bundle.Import.Diagnostics.Should().Contain(d =>
            d.Code == ClaudeDesignImporter.ReferenceTooLargeDiagnostic);
    }

    [Fact]
    public void One_info_summary_reports_kept_and_skipped_counts()
    {
        ClaudeDesignBundle bundle = Loader.ImportBundleWith(
            ("readme.md", Utf8("prose")),
            ("_ds_bundle.js", Utf8("compiled")));

        bundle.Import.Diagnostics.Should().ContainSingle(d =>
            d.Code == ClaudeDesignImporter.ReferenceDiagnostic && d.Severity == ImportDiagnosticSeverity.Info);
    }

    private static ImportFile File(string path, byte[] content) =>
        new(path, content, Convert.ToHexStringLower(SHA256.HashData(content)), null);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
