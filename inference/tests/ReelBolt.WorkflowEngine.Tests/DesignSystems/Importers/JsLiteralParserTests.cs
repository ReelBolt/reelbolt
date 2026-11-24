using System;
using System.Linq;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class JsLiteralParserTests
{
    [Fact]
    public void Object_array_trailing_comma_and_comment_parse()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse("{ a: 1, b: [1, 2, ], c: \"x\", // c\n d: 'y' }");

        Assert.Empty(result.Issues);
        Assert.Equal("{\"a\":1,\"b\":[1,2],\"c\":\"x\",\"d\":\"y\"}", result.Root?.ToJsonString());
    }

    [Theory]
    [InlineData("module.exports = {a:1}")]
    [InlineData("export default {a:1}")]
    [InlineData("export default defineConfig({a:1})")]
    [InlineData("module.exports = defineConfig({a:1})")]
    [InlineData("{a:1} satisfies Config")]
    [InlineData("/** @type {import('tailwindcss').Config} */ export default {a:1}")]
    public void Root_forms_are_unwrapped(string text)
    {
        JsLiteralParseResult result = JsLiteralParser.Parse(text);

        Assert.Equal("{\"a\":1}", result.Root?.ToJsonString());
    }

    [Fact]
    public void Computed_key_is_dynamic()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse("{ [foo]: 1 }");

        Assert.Contains(result.Issues, issue => issue.Code == "TAILWIND_DYNAMIC_VALUE");
        Assert.Equal("{}", result.Root?.ToJsonString());
    }

    [Fact]
    public void Spread_is_dynamic_but_siblings_survive()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse("{ ...foo, a: 1 }");

        Assert.Contains(result.Issues, issue => issue.Code == "TAILWIND_DYNAMIC_VALUE");
        Assert.Equal("{\"a\":1}", result.Root?.ToJsonString());
    }

    [Theory]
    [InlineData("{ a: () => 1 }")]
    [InlineData("{ a: require('x') }")]
    [InlineData("{ a: `x${y}` }")]
    [InlineData("{ a: someVar }")]
    public void Dynamic_values_are_omitted(string text)
    {
        JsLiteralParseResult result = JsLiteralParser.Parse(text);

        Assert.Contains(result.Issues, issue => issue.Code == "TAILWIND_DYNAMIC_VALUE");
        Assert.Equal("{}", result.Root?.ToJsonString());
    }

    [Fact]
    public void Template_without_interpolation_is_a_string()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse("`plain`");

        Assert.Empty(result.Issues);
        Assert.Equal("\"plain\"", result.Root?.ToJsonString());
    }

    [Fact]
    public void Deep_nesting_is_bounded_and_reports_too_deep_once()
    {
        string text = "module.exports = " + new string('{', 5000) + new string('}', 5000);

        JsLiteralParseResult result = JsLiteralParser.Parse(text);

        Assert.Equal(1, result.Issues.Count(issue => issue.Code == "TOO_DEEP"));
    }

    [Fact]
    public void Oversized_source_is_refused()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse(new string('a', JsLiteralParser.MaxBytes + 1));

        Assert.Null(result.Root);
        JsLiteralIssue issue = Assert.Single(result.Issues);
        Assert.Equal("PARSE_FAILED", issue.Code);
    }

    [Fact]
    public void Empty_source_produces_no_issues()
    {
        JsLiteralParseResult result = JsLiteralParser.Parse("   \n\t ");

        Assert.Null(result.Root);
        Assert.Empty(result.Issues);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("{a:[)]}")]
    [InlineData("[;]")]
    [InlineData("{a:[/re/g, 1]}")]
    [InlineData("{a:[@, 1]}")]
    public void Array_with_an_unparseable_element_terminates(string text)
    {
        JsLiteralParseResult result = HostileInput.RunWithin(Limit, () => JsLiteralParser.Parse(text));

        Assert.Contains(result.Issues, issue => issue.Code == "PARSE_FAILED");
    }

    [Fact]
    public void Array_of_120k_elements_hitting_the_token_cap_terminates()
    {
        string text = "{a:[" + string.Concat(Enumerable.Repeat("1,", 120_000)) + "]}";

        JsLiteralParseResult result = HostileInput.RunWithin(Limit, () => JsLiteralParser.Parse(text));

        Assert.NotNull(result);
    }

    [Fact]
    public void Array_after_too_deep_nesting_terminates()
    {
        string text = "{" + string.Concat(Enumerable.Repeat("a:{", 40)) + "x:[1,2,3]" + new string('}', 41);

        JsLiteralParseResult result = HostileInput.RunWithin(Limit, () => JsLiteralParser.Parse(text));

        Assert.Contains(result.Issues, issue => issue.Code == "TOO_DEEP");
    }

    [Fact]
    public void Deep_array_nesting_with_trailing_stray_character_terminates()
    {
        string text = string.Concat(Enumerable.Repeat("[", 60)) + ")" + string.Concat(Enumerable.Repeat("]", 60));

        JsLiteralParseResult result = HostileInput.RunWithin(Limit, () => JsLiteralParser.Parse(text));

        Assert.NotNull(result);
    }
}
