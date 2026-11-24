#nullable enable
using System.Text;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// The worker runs with ICU, where culture-sensitive string operations ignore characters such as U+034F (combining
/// grapheme joiner). Extension, prefix and reserved-name decisions on entry names must stay ordinal in both modes.
/// </summary>
public class OrdinalNameDecisionTests
{
    private static readonly byte[] Js = Encoding.UTF8.GetBytes("console.log(1)");

    [Fact]
    public void A_plain_js_extension_is_still_dropped_and_matching_ignores_ascii_case()
    {
        ZipFileClassifier.Classify("evil.JS", Js).DropReason.Should().Be("DROPPED_SCRIPT");
    }

    [Theory]
    [InlineData("tail͏wind.config.js")]
    [InlineData("͏tailwind.config.js")]
    public void Tailwind_config_detection_is_a_literal_ordinal_prefix(string path)
    {
        ZipFileClassifier.IsTailwindConfig(path).Should().BeFalse();
        ZipFileClassifier.IsTailwindConfig("a/Tailwind.Config.ts").Should().BeTrue();
    }

    [Theory]
    [InlineData("a.js ", "DROPPED_SCRIPT")]
    [InlineData("a.js\u00a0", "DROPPED_SCRIPT")]
    [InlineData("a.js\u3164", "DROPPED_SCRIPT")]
    [InlineData("a.js\u034f", "DROPPED_SCRIPT")]
    public void Invisible_trailing_characters_never_hide_a_script_extension(string path, string expected)
    {
        ZipFileClassifier.Classify(path, Js).DropReason.Should().Be(expected);
    }

    [Theory]
    [InlineData("a.html\u2800")]
    [InlineData("a.html\u3000")]
    public void Invisible_trailing_characters_never_hide_an_html_extension(string path)
    {
        ZipFileClassifier.Classify(path, Js).Kind.Should().Be("markup");
    }
}
