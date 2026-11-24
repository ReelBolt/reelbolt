using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// T-001: the allowlist SVG sanitizer. The corpus under <c>TestData/design-import/svg-hostile</c>
/// is synthetic, untrusted data authored for this test; it is read as bytes and never executed,
/// rendered or fetched. Each memory of <see cref="CorpusFiles"/> is one fixture and the outcome it
/// must produce.
/// </summary>
public sealed class SvgSanitizerTests
{
    private static readonly (string File, bool Ok)[] CorpusFiles =
    [
        ("script.svg", true),
        ("foreign-object.svg", true),
        ("event-attribute.svg", true),
        ("external-href.svg", true),
        ("use-external.svg", true),
        ("style-element.svg", true),
        ("css-url.svg", true),
        ("entity-expansion.svg", false),
        ("doctype.svg", false),
        ("malformed.svg", false),
        ("deep-nesting.svg", false),
        ("data-uri-image.svg", true),
        ("image-element.svg", true),
        ("smil-set.svg", true),
        ("processing-instruction.svg", true),
        ("namespace-confusion.svg", true),
        ("c2pa-metadata.svg", true),
        ("plain-logo.svg", true),
        ("attributes.svg", true),
    ];

    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string SvgNamespaceDeclaration = $"xmlns=\"{SvgNamespace}\"";

    private static readonly string[] ForbiddenInOutput =
    [
        "*script*", "*foreignObject*", "*javascript:*", "*onload=*", "*data:*",
        "*<!ENTITY*", "*<?xml-stylesheet*", "*xlink:*", "*<image*",
    ];

    public static TheoryData<string, bool> Corpus
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach ((string file, bool ok) in CorpusFiles)
                data.Add(file, ok);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void A_hostile_construct_is_never_in_the_output(string file, bool expectedOk)
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture(file)));

        result.Ok.Should().Be(expectedOk, result.Message ?? string.Empty);
        if (!result.Ok)
        {
            result.Sanitized.Should().BeNull();
            return;
        }

        result.Sanitized.Should().NotBeNull();
        foreach (string pattern in ForbiddenInOutput)
            result.Sanitized.Should().NotMatch(pattern);

        // The only URL the rebuilt document may carry is its single SVG namespace declaration.
        CountOccurrences(result.Sanitized!, SvgNamespaceDeclaration).Should().Be(1);
        WithoutSvgNamespace(result.Sanitized!).Should().NotContain("http");

        XDocument document = XDocument.Parse(result.Sanitized!);
        document.Root!.Name.LocalName.Should().Be("svg");
        document.Root.Name.NamespaceName.Should().Be(SvgNamespace);

        foreach (XElement element in document.Root.DescendantsAndSelf())
        {
            XAttribute? href = element.Attribute("href");
            if (href is not null)
                href.Value.Should().StartWith("#");
        }
    }

    [Fact]
    public void Every_corpus_fixture_has_a_case_and_every_case_a_fixture()
    {
        string[] onDisk = Directory.GetFiles(CorpusDirectory, "*.svg")
            .Select(System.IO.Path.GetFileName)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] covered = CorpusFiles
            .Select(entry => entry.File)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        onDisk.Should().Equal(covered);
    }

    [Fact]
    public void A_benign_logo_with_c2pa_metadata_keeps_its_visual_elements()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture("c2pa-metadata.svg")));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().Contain("<path").And.NotContain("metadata");
    }

    [Fact]
    public void A_benign_logo_survives_without_dropping_anything()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture("plain-logo.svg")));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.ElementsDropped.Should().Be(0);
        result.AttributesDropped.Should().Be(0);
        result.Sanitized.Should().Contain("<rect").And.Contain("<text").And.Contain("url(#brand-gradient)");
    }

    [Fact]
    public void A_benign_logo_keeps_the_svg_namespace()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture("plain-logo.svg")));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().Contain(SvgNamespaceDeclaration);
        CountOccurrences(result.Sanitized!, SvgNamespaceDeclaration).Should().Be(1);
        WithoutSvgNamespace(result.Sanitized!).Should().NotContain("http");
    }

    [Fact]
    public void An_event_attribute_is_dropped_and_counted()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture("attributes.svg")));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.AttributesDropped.Should().BeGreaterThanOrEqualTo(2);
        result.Sanitized.Should().NotContain("onmouseover").And.NotContain("javascript:");
    }

    [Fact]
    public void Deep_nesting_is_refused_within_the_deadline()
        => HostileInput.RunWithin(TimeSpan.FromSeconds(2),
                () => SvgSanitizer.Sanitize(File.ReadAllBytes(Fixture("deep-nesting.svg"))))
            .Ok.Should().BeFalse();

    [Fact]
    public void An_oversized_svg_is_refused_before_parsing()
        => SvgSanitizer.Sanitize(new byte[SvgSanitizer.MaxInputBytes + 1]).Code
            .Should().Be(SvgSanitizer.TooLargeCode);

    [Fact]
    public void A_non_svg_root_is_refused()
        => SvgSanitizer.Sanitize(Utf8("<html><body/></html>")).Code.Should().Be(SvgSanitizer.InvalidCode);

    [Fact]
    public void Malformed_xml_never_throws()
        => SvgSanitizer.Sanitize(Utf8("<svg><path d='M0 0'")).Ok.Should().BeFalse();

    [Fact]
    public void An_empty_input_is_empty()
        => SvgSanitizer.Sanitize(Array.Empty<byte>()).Code.Should().Be(SvgSanitizer.EmptyCode);

    [Fact]
    public void An_svg_with_no_drawable_element_is_empty()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(
            Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><linearGradient id=\"g\"/></defs></svg>"));

        result.Ok.Should().BeFalse();
        result.Code.Should().Be(SvgSanitizer.EmptyCode);
    }

    [Fact]
    public void A_use_pointing_at_a_missing_local_id_is_dropped()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(
            Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"#missing\"/><path d=\"M0 0\"/></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().NotContain("<use");
        result.ElementsDropped.Should().Be(1);
    }

    [Fact]
    public void A_use_pointing_at_a_local_id_is_kept()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(Utf8(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><defs><path id=\"mark\" d=\"M0 0\"/></defs>" +
            "<use href=\"#mark\"/></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().Contain("<use href=\"#mark\"").And.Contain("<path id=\"mark\"");
    }

    [Fact]
    public void Text_is_escaped_never_emitted_as_markup()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(Utf8(
            "<svg xmlns=\"http://www.w3.org/2000/svg\">" +
            "<text>&lt;script&gt;alert(1)&lt;/script&gt;</text></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().Contain("&lt;script&gt;").And.NotContain("<script>");
    }

    [Fact]
    public void An_attribute_value_beyond_the_cap_is_dropped()
    {
        string oversized = new('0', 4097);
        SvgSanitizeResult result = SvgSanitizer.Sanitize(Utf8(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0\" id=\"{oversized}\"/> " +
            "<rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.AttributesDropped.Should().BeGreaterThanOrEqualTo(1);
        result.Sanitized.Should().NotContain(oversized);
    }

    [Fact]
    public void Bidi_and_control_characters_are_removed_from_attribute_values()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(
            Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><path id=\"a‮b\" d=\"M0 0‮\"/></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.Sanitized.Should().NotContain("‮").And.Contain("id=\"ab\"");
    }

    [Fact]
    public void A_disallowed_element_takes_its_whole_subtree_with_it()
    {
        SvgSanitizeResult result = SvgSanitizer.Sanitize(Utf8(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><g><script>alert(1)</script>" +
            "<path d=\"M0 0\"/></g><rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/></svg>"));

        result.Ok.Should().BeTrue(result.Message ?? string.Empty);
        result.ElementsDropped.Should().Be(1);
        result.Sanitized.Should().NotContain("script");
    }

    private static string CorpusDirectory =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "design-import", "svg-hostile");

    private static string Fixture(string name) => System.IO.Path.Combine(CorpusDirectory, name);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string WithoutSvgNamespace(string text) =>
        text.Replace(SvgNamespaceDeclaration, string.Empty, StringComparison.Ordinal);
}
