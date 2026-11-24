using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public sealed class SimpleGlobTests
{
    [Theory]
    [InlineData("**/*.css", "app/globals.css", true)]
    [InlineData("**/*.css", "globals.css", true)]
    [InlineData("**/*.css", "app/globals.tsx", false)]
    [InlineData("**/tokens/**/*.json", "tokens/color.json", true)]
    [InlineData("**/tokens/**/*.json", "src/tokens/a/b.json", true)]
    [InlineData("**/tokens/**/*.json", "tokens/color.css", false)]
    [InlineData("app/*", "app/globals.css", true)]
    [InlineData("app/*", "app/sub/x.css", false)]
    [InlineData("a?.css", "ab.css", true)]
    [InlineData("a?.css", "a.css", false)]
    [InlineData("a?.css", "abc.css", false)]
    [InlineData("package.json", "package.json", true)]
    [InlineData("package.json", "src/package.json", false)]
    [InlineData("**/brand/**", "brand/logo.svg", true)]
    [InlineData("**/brand/**", "src/brand/design.md", true)]
    public void IsMatch_matches_the_documented_semantics(string pattern, string path, bool expected) =>
        SimpleGlob.IsMatch(pattern, path).Should().Be(expected);

    [Fact]
    public void Special_characters_are_literal()
    {
        SimpleGlob.IsMatch("a+b(c).css", "a+b(c).css").Should().BeTrue();
        SimpleGlob.IsMatch("a+b(c).css", "aab(c).css").Should().BeFalse();
    }

    [Fact]
    public void Empty_inputs_do_not_throw()
    {
        SimpleGlob.IsMatch(string.Empty, string.Empty).Should().BeTrue();
        SimpleGlob.IsMatch(string.Empty, "app/globals.css").Should().BeFalse();
        SimpleGlob.IsMatch("**/*.css", string.Empty).Should().BeFalse();
    }
}
