using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// T-008: the capability matrix is one static table, and these tests are its drift guard. Every
/// importer id appears exactly once, every row names every axis exactly once, and the support
/// levels stay truthful to what the readers actually do after this initiative.
/// </summary>
public sealed class DesignImportCapabilitiesTests
{
    private static readonly string[] ImporterIds =
    [
        "dtcg", "css-theme", "design-md", "tokens-studio", "shadcn-registry", "storybook-index",
        "style-dictionary", "tailwind-v3", "figma-variables", "brand-pdf", "penpot", "sketch",
        "claude-design",
    ];

    [Fact]
    public void Every_importer_has_one_row_per_axis()
    {
        DesignImportCapabilities.Matrix.Select(m => m.ImporterId).Should().BeEquivalentTo(ImporterIds);
        DesignImportCapabilities.Matrix.Select(m => m.ImporterId).Distinct().Should().HaveCount(ImporterIds.Length);

        foreach (DesignImporterCapabilities importer in DesignImportCapabilities.Matrix)
        {
            importer.Axes.Should().HaveCount(Enum.GetValues<DesignImportAxis>().Length);
            importer.Axes.Select(a => a.Axis).Should().BeEquivalentTo(Enum.GetValues<DesignImportAxis>());
            importer.Axes.Select(a => a.Axis).Distinct().Should().HaveCount(Enum.GetValues<DesignImportAxis>().Length);
        }
    }

    [Fact]
    public void Every_row_has_a_label_and_a_note_on_each_axis()
    {
        foreach (DesignImporterCapabilities importer in DesignImportCapabilities.Matrix)
        {
            importer.Label.Should().NotBeNullOrWhiteSpace();
            importer.Axes.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Note));
        }
    }

    [Fact]
    public void For_finds_every_importer_and_rejects_an_unknown_id()
    {
        ImporterIds.Should().OnlyContain(id => DesignImportCapabilities.For(id) != null);
        DesignImportCapabilities.For("no-such-importer").Should().BeNull();
    }

    [Fact]
    public void Claude_design_reads_the_new_axes_in_full_after_this_initiative()
    {
        DesignImporterCapabilities claude = DesignImportCapabilities.For("claude-design")!;

        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Assets && a.Support == DesignImportSupport.Full);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.PreviewCards && a.Support == DesignImportSupport.Full);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.ReferenceDocuments && a.Support == DesignImportSupport.Full);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Tokens && a.Support == DesignImportSupport.Full);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Modes && a.Support == DesignImportSupport.Full);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Components && a.Support == DesignImportSupport.Full);
    }

    [Fact]
    public void Claude_design_voice_note_names_only_the_read_the_code_performs()
    {
        DesignImporterCapabilities claude = DesignImportCapabilities.For("claude-design")!;
        DesignImportAxisCapability voice = claude.Axes.Single(a => a.Axis == DesignImportAxis.Voice);

        // The importer writes only voice.summary, from the readme's first paragraph; SKILL.md supplies
        // the name and description, never tone/do/don't. The note must not claim a read the code skips.
        voice.Note.Should().Contain("summary");
        voice.Note.Should().NotContain("tone");
        voice.Note.Should().NotContain("do or don");
    }

    [Fact]
    public void Claude_design_stays_partial_where_the_read_is_partial()
    {
        DesignImporterCapabilities claude = DesignImportCapabilities.For("claude-design")!;

        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Fonts && a.Support == DesignImportSupport.Partial);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Prose && a.Support == DesignImportSupport.Partial);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.Voice && a.Support == DesignImportSupport.Partial);
        claude.Axes.Should().Contain(a => a.Axis == DesignImportAxis.DerivedTokens && a.Support == DesignImportSupport.Partial);
    }

    [Fact]
    public void Storybook_index_reads_components_but_no_tokens()
    {
        DesignImporterCapabilities storybook = DesignImportCapabilities.For("storybook-index")!;

        storybook.Axes.Single(a => a.Axis == DesignImportAxis.Components).Support.Should().Be(DesignImportSupport.Full);
        storybook.Axes.Single(a => a.Axis == DesignImportAxis.Tokens).Support.Should().Be(DesignImportSupport.None);
        storybook.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Support.Should().Be(DesignImportSupport.None);
    }

    [Fact]
    public void Brand_pdf_reads_only_a_subset_of_the_token_axis()
    {
        DesignImporterCapabilities pdf = DesignImportCapabilities.For("brand-pdf")!;

        pdf.Axes.Single(a => a.Axis == DesignImportAxis.Tokens).Support.Should().Be(DesignImportSupport.Partial);
        pdf.Axes.Single(a => a.Axis == DesignImportAxis.Fonts).Support.Should().Be(DesignImportSupport.Partial);
        pdf.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Support.Should().Be(DesignImportSupport.None);
        pdf.Axes.Single(a => a.Axis == DesignImportAxis.ReferenceDocuments).Support.Should().Be(DesignImportSupport.None);
    }

    [Fact]
    public void Css_theme_reads_selector_driven_modes_and_derived_tokens()
    {
        DesignImporterCapabilities css = DesignImportCapabilities.For("css-theme")!;

        css.Axes.Single(a => a.Axis == DesignImportAxis.Modes).Support.Should().Be(DesignImportSupport.Partial);
        css.Axes.Single(a => a.Axis == DesignImportAxis.DerivedTokens).Support.Should().Be(DesignImportSupport.Full);
        css.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Support.Should().Be(DesignImportSupport.None);
    }

    [Fact]
    public void Only_claude_design_owns_the_asset_and_reference_axes()
    {
        foreach (DesignImporterCapabilities importer in DesignImportCapabilities.Matrix)
        {
            DesignImportSupport assets = importer.Axes.Single(a => a.Axis == DesignImportAxis.Assets).Support;
            DesignImportSupport references = importer.Axes.Single(a => a.Axis == DesignImportAxis.ReferenceDocuments).Support;
            DesignImportSupport previewCards = importer.Axes.Single(a => a.Axis == DesignImportAxis.PreviewCards).Support;

            if (importer.ImporterId == "claude-design")
                continue;

            assets.Should().Be(DesignImportSupport.None, $"{importer.ImporterId} does not import asset bytes");
            references.Should().Be(DesignImportSupport.None, $"{importer.ImporterId} does not import reference documents");
            previewCards.Should().Be(DesignImportSupport.None, $"{importer.ImporterId} does not import preview cards");
        }
    }
}
