using System;
using AwesomeAssertions;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Storage;

public class ProjectOutputKeysTests
{
    private static readonly Guid Project = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("99999999-2222-3333-4444-555555555555");

    [Theory]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/outputFiles/exec/final.mp4", true)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/agentFiles/generated/exec/step-2-v1.mp4", true)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/agentFiles/video-analysis/exec/a.json", false)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/userFiles/a.mp4", false)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/outputFiles/../userFiles/a.mp4", false)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/agentFiles/generated\\x.mp4", false)]
    [InlineData("projects/99999999-2222-3333-4444-555555555555/outputFiles/exec/final.mp4", false)]
    [InlineData("projects/11111111-2222-3333-4444-555555555555/outputFilesX/final.mp4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_projects_render_and_generated_prefixes_are_addressable(string? key, bool expected)
    {
        ProjectOutputKeys.IsAddressableOutput(Project, key).Should().Be(expected);
    }

    [Fact]
    public void Prefixes_carry_the_project_and_a_trailing_slash()
    {
        ProjectOutputKeys.OutputFilesPrefix(Other).Should().Be($"projects/{Other}/outputFiles/");
        ProjectOutputKeys.GeneratedPrefix(Other).Should().Be($"projects/{Other}/agentFiles/generated/");
    }
}
