using System;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class ImportGlobTests
{
    [Theory]
    [InlineData("tokens/**/*.json", "tokens/a.json", true)]
    [InlineData("tokens/**/*.json", "tokens/x/y/a.json", true)]
    [InlineData("tokens/**/*.json", "tokens/a.txt", false)]
    [InlineData("**/*.json", "a.json", true)]
    [InlineData("**/*.json", "x/a.json", true)]
    [InlineData("a?.json", "ab.json", true)]
    [InlineData("a?.json", "abc.json", false)]
    [InlineData("*.json", "x/a.json", false)]
    [InlineData("**", "a.json", true)]
    [InlineData("**", "x/y/a.json", true)]
    [InlineData("a/**/b.json", "a/b.json", true)]
    [InlineData("a/**/b.json", "a/x/b.json", true)]
    [InlineData("a/**/b.json", "a/x/y/b.json", true)]
    [InlineData("a/**/b.json", "a/x/y/c.json", false)]
    [InlineData("tokens/*.json", "tokens/a.json", true)]
    [InlineData("tokens/*.json", "tokens/x/a.json", false)]
    [InlineData("assets/**", "assets/a/b.css", true)]
    [InlineData("assets/**", "assets.css", false)]
    public void IsMatch_theory(string pattern, string path, bool expected)
    {
        ImportGlob.IsMatch(pattern, path).Should().Be(expected);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/x")]
    [InlineData("a\\b")]
    [InlineData("node_modules/**")]
    [InlineData("a{b,c}")]
    [InlineData("")]
    [InlineData("a\tb")]
    public void IsSafePattern_rejects_unsafe(string pattern)
    {
        ImportGlob.IsSafePattern(pattern).Should().BeFalse();
    }

    [Theory]
    [InlineData("tokens/**/*.json")]
    [InlineData("**/*.json")]
    [InlineData("a b/@c+d.json")]
    public void IsSafePattern_accepts_safe(string pattern)
    {
        ImportGlob.IsSafePattern(pattern).Should().BeTrue();
    }

    [Fact]
    public async System.Threading.Tasks.Task IsMatch_is_fast_on_a_long_path()
    {
        string path = new string('a', 1000);

        // A backtracking matcher would need effectively unbounded time here, so the bound only has to separate
        // "linear" from "never finishes": the match runs on a worker thread with a generous hard timeout instead of
        // a tight wall-clock assertion that flakes on a loaded CI box.
        System.Threading.Tasks.Task<bool> match = System.Threading.Tasks.Task.Run(() => ImportGlob.IsMatch("**/*a*a*a*a*b", path));

        bool matched = await match.WaitAsync(TimeSpan.FromSeconds(20));

        matched.Should().BeFalse("the glob must finish (a timeout throws), not backtrack catastrophically");
    }

    [Fact]
    public void IsMatch_rejects_paths_over_1024_characters()
    {
        string path = "tokens/" + new string('a', 1024) + ".json";

        ImportGlob.IsMatch("tokens/*.json", path).Should().BeFalse();
    }
}
