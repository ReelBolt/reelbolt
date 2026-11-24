#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// The validation every chosen path goes through before it is stored (docs/github-app.md "Choosing
/// which files are synced"): plain repository-relative paths only, canonical, bounded.
/// </summary>
public class GitHubIncludePathsTests
{
    private static List<string> Normalize(params string?[] raw)
    {
        GitHubIncludePaths.TryNormalize(raw, out List<string> normalized, out string? reason).Should().BeTrue(reason);
        return normalized;
    }

    [Fact]
    public void Paths_are_trimmed_deduplicated_sorted_and_covered_paths_dropped()
    {
        Normalize(" /src/video/ ", "src/video/scene.tsx", "README.md", "README.md", "src/videos/x.ts")
            .Should().Equal("README.md", "src/video", "src/videos/x.ts");
    }

    [Fact]
    public void A_sibling_with_a_shared_prefix_is_not_covered()
    {
        Normalize("src", "src-old/a.ts").Should().Equal("src", "src-old/a.ts");
    }

    [Fact]
    public void Choosing_the_root_means_the_whole_repository()
    {
        Normalize("video", "/").Should().BeEmpty();
        Normalize().Should().BeEmpty();
        GitHubIncludePaths.TryNormalize(null, out List<string> none, out _).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    public void Traversal_empty_segments_backslashes_and_control_characters_are_refused(string bad)
    {
        GitHubIncludePaths.TryNormalize([bad], out _, out string? reason).Should().BeFalse();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_null_entry_is_refused()
    {
        GitHubIncludePaths.TryNormalize(["a", null], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Too_many_paths_or_too_long_a_path_is_refused()
    {
        IEnumerable<string> many = Enumerable.Range(0, GitHubIncludePaths.MaxPaths + 1).Select(i => $"f{i}.txt");
        GitHubIncludePaths.TryNormalize(many, out _, out _).Should().BeFalse();

        IEnumerable<string> exactly = Enumerable.Range(0, GitHubIncludePaths.MaxPaths).Select(i => $"f{i}.txt");
        GitHubIncludePaths.TryNormalize(exactly, out _, out _).Should().BeTrue();

        GitHubIncludePaths.TryNormalize([new string('a', GitHubIncludePaths.MaxPathLength + 1)], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Matching_is_by_whole_segment()
    {
        List<string> include = ["src/video", "README.md"];

        GitHubIncludePaths.Matches(include, "src/video").Should().BeTrue();
        GitHubIncludePaths.Matches(include, "src/video/a/b.tsx").Should().BeTrue();
        GitHubIncludePaths.Matches(include, "README.md").Should().BeTrue();
        GitHubIncludePaths.Matches(include, "src/videos/a.tsx").Should().BeFalse();
        GitHubIncludePaths.Matches(include, "README.md.bak").Should().BeFalse();
        GitHubIncludePaths.Matches([], "anything").Should().BeTrue();
    }

    [Fact]
    public void The_stored_form_round_trips_and_an_empty_selection_stores_null()
    {
        GitHubIncludePaths.Serialize([]).Should().BeNull();
        string? json = GitHubIncludePaths.Serialize(["a", "b/c"]);
        GitHubIncludePaths.Parse(json).Should().Equal("a", "b/c");
        GitHubIncludePaths.Parse(null).Should().BeEmpty();
    }

    [Fact]
    public void A_tampered_stored_value_is_refused_rather_than_widened()
    {
        Action act = () => GitHubIncludePaths.Parse("[\"../etc\"]");
        act.Should().Throw<InvalidOperationException>();
    }
}
