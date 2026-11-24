using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class DesignTextTests
{
    [Fact]
    public void Sanitize_DropsMarkupAndCollapsesWhitespace()
    {
        DesignText.Sanitize(@"  Hello [World] ""q"" `x` <b>{c}#\ ", 100)
            .Should().Be("Hello World q x bc");
    }

    [Theory]
    [InlineData("a\u200Bb", "ab")]
    [InlineData("a\u202Eb", "ab")]
    [InlineData("a\u0000b\tc\n\nd", "a b c d")]
    public void Sanitize_RemovesFormatAndControlCharacters(string input, string expected)
    {
        DesignText.Sanitize(input, 100).Should().Be(expected);
    }

    [Theory]
    [InlineData("a\U000E0041\U000E0042b", "ab")]
    [InlineData("a\U000E0001b", "ab")]
    [InlineData("a\U000E007Fb", "ab")]
    [InlineData("a\uFE0Fb", "ab")]
    [InlineData("a\U000E0100b", "ab")]
    [InlineData("a\uFF3Bb\uFF3Dc", "abc")]
    [InlineData("a\u3010b\u3011c", "abc")]
    [InlineData("ok \U0001F600 fine", "ok \U0001F600 fine")]
    public void Sanitize_DropsSurrogateEncodedHiddenCharactersAndLookalikeBrackets(string input, string expected)
    {
        DesignText.Sanitize(input, 100).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_ReturnsNullForTextMadeOnlyOfTagCharacters()
    {
        DesignText.Sanitize("\U000E0041\U000E0042\U000E0043", 100).Should().BeNull();
    }

    [Fact]
    public void Sanitize_TruncatesToMaxLength()
    {
        DesignText.Sanitize("abcdefghij", 5).Should().Be("abcde");
        DesignText.Sanitize("abc def", 4).Should().Be("abc");
        DesignText.Sanitize("ab" + "\U0001F600", 3).Should().Be("ab");
    }

    [Fact]
    public void Sanitize_ReturnsNullWhenNothingIsLeft()
    {
        DesignText.Sanitize("[]", 10).Should().BeNull();
        DesignText.Sanitize("   ", 10).Should().BeNull();
        DesignText.Sanitize(null, 10).Should().BeNull();
        DesignText.Sanitize("x", 0).Should().BeNull();
    }

    [Theory]
    [InlineData("Inter")]
    [InlineData("Open Sans")]
    [InlineData("Playfair Display")]
    [InlineData("IBM Plex Sans (Var)")]
    public void IsSafeFontFamily_AcceptsConservativeNames(string name)
    {
        DesignText.IsSafeFontFamily(name).Should().BeTrue();
    }

    [Fact]
    public void IsSafeFontFamily_RejectsEverythingElse()
    {
        DesignText.IsSafeFontFamily("").Should().BeFalse();
        DesignText.IsSafeFontFamily(new string('a', 81)).Should().BeFalse();
        DesignText.IsSafeFontFamily("Inter;}").Should().BeFalse();
        DesignText.IsSafeFontFamily("a\"b").Should().BeFalse();
        DesignText.IsSafeFontFamily("x<y").Should().BeFalse();
        DesignText.IsSafeFontFamily("Ünder").Should().BeFalse();
    }
}
