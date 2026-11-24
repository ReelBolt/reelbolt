using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

public class ReviewLinkNotifierNameTests
{
    [Theory]
    [InlineData("Ana Rossi", "Ana Rossi")]
    [InlineData("  Ana   \t Rossi  ", "Ana Rossi")]
    [InlineData("Click https://evil.example/pay now", "Click now")]
    [InlineData("pay at evil.com/x", "pay at")]
    [InlineData("www.evil.com", "A guest")]
    [InlineData("ana@evil.example", "A guest")]
    [InlineData("Ana <b>", "Ana b")]
    [InlineData("", "A guest")]
    public void Names_are_reduced_to_a_safe_display_form(string raw, string expected) =>
        ReviewLinkActivityNotifier.SafeDisplayName(raw).Should().Be(expected);

    [Fact]
    public void Names_are_capped_at_forty_characters() =>
        ReviewLinkActivityNotifier.SafeDisplayName(new string('n', 80)).Length.Should().Be(40);
}
