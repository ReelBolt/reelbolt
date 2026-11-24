using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// Task 4: <see cref="MarkupText"/> reduces inert markup to plain text. Every assertion here is about
/// what must never leak (a script/style/head body) or what must survive (visible prose).
/// </summary>
public sealed class MarkupTextTests
{
    [Fact]
    public void An_unclosed_script_never_leaks_its_body()
    {
        string text = MarkupText.ToText("<p>visible</p><script>alert('pwned'); steal();");

        text.Should().Contain("visible");
        text.Should().NotContain("alert");
        text.Should().NotContain("steal");
    }

    [Fact]
    public void Encoded_script_tags_stay_text()
    {
        string text = MarkupText.ToText("&lt;script&gt;not-a-body&lt;/script&gt;");

        text.Should().Be("<script>not-a-body</script>");
    }

    [Fact]
    public void Head_and_style_bodies_are_dropped()
    {
        string text = MarkupText.ToText(
            "<html><head><title>t</title><meta name=\"x\" content=\"y\"></head>"
            + "<style>.a{color:red}</style><body>Body copy</body></html>");

        text.Should().Be("Body copy");
        text.Should().NotContain("color:red");
        text.Should().NotContain("<title>");
    }

    [Fact]
    public void Control_and_bidi_characters_are_removed()
    {
        string text = MarkupText.ToText("a\u0000b\u0007c‮d⁦e");

        text.Should().Be("abcde");
    }

    [Fact]
    public void Whitespace_is_collapsed_and_the_output_is_capped()
    {
        MarkupText.ToText("  one \n\t two   three  ").Should().Be("one two three");

        string capped = MarkupText.ToText(new string('a', 5000), maxChars: 100);
        capped.Should().HaveLength(100);
    }

    [Fact]
    public void Comments_do_not_leak_their_body()
    {
        string text = MarkupText.ToText("<p>before</p><!-- secret comment --><p>after</p>");

        text.Should().Be("beforeafter");
        text.Should().NotContain("secret");
    }
}
