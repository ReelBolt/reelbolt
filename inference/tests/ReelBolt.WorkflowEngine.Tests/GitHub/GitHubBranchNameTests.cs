#nullable enable
using System;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.GitHub;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.GitHub;

/// <summary>
/// A branch name from a request becomes part of a GitHub REST path, so it is held to
/// <c>git check-ref-format</c> first, and the path builder refuses a dot segment on its own as well.
/// </summary>
public class GitHubBranchNameTests
{
    [Theory]
    [InlineData("main")]
    [InlineData("release/1.2")]
    [InlineData("feature/video-intro_v2")]
    [InlineData("dependabot/npm_and_yarn/next-15.0.1")]
    [InlineData("v1.0")]
    public void Real_branch_names_are_accepted(string name) =>
        GitHubBranchName.IsValid(name).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../..")]
    [InlineData("a/../b")]
    [InlineData(".")]
    [InlineData("./a")]
    [InlineData("a/.hidden")]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a.")]
    [InlineData("a.lock")]
    [InlineData("a/b.lock/c")]
    [InlineData("-a")]
    [InlineData("@")]
    [InlineData("a@{1}")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a\\b")]
    [InlineData("a~1")]
    [InlineData("a^")]
    [InlineData("a:b")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a[b")]
    [InlineData("a%2e%2e")]
    [InlineData("a\u0000")]
    public void Names_git_would_refuse_or_that_could_steer_a_path_are_refused(string name) =>
        GitHubBranchName.IsValid(name).Should().BeFalse();

    [Fact]
    public void An_overlong_name_is_refused() =>
        GitHubBranchName.IsValid(new string('a', GitHubBranchName.MaxLength + 1)).Should().BeFalse();

    [Theory]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("a/../b", true)]
    [InlineData("a//b", true)]
    [InlineData("", false)]
    public void The_path_builder_never_emits_a_dot_or_empty_segment(string value, bool keepSlashes)
    {
        Action act = () => GitHubAppClient.Seg(value, keepSlashes);
        act.Should().Throw<GitHubApiException>();
    }

    [Fact]
    public void The_path_builder_keeps_branch_slashes_and_encodes_each_piece() =>
        GitHubAppClient.Seg("release/1 2", keepSlashes: true).Should().Be("release/1%202");
}
