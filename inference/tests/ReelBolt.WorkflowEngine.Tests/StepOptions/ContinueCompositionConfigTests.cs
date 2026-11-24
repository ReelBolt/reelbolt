using System;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.StepOptions;

/// <summary>
/// The step-level "continue from an earlier run's composition" option: exactly one of
/// <c>executionId</c> / <c>versionId</c>, parsed from the step's <c>continueCompositionJson</c>.
/// </summary>
public class ContinueCompositionConfigTests
{
    [Fact]
    public void Execution_id_alone_is_valid()
    {
        new ContinueCompositionConfig(Guid.NewGuid(), null).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Version_id_alone_is_valid()
    {
        new ContinueCompositionConfig(null, Guid.NewGuid()).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Neither_id_is_refused()
    {
        new ContinueCompositionConfig(null, null).Validate().Should().ContainSingle()
            .Which.Should().Contain("exactly one");
    }

    [Fact]
    public void Both_ids_are_refused()
    {
        new ContinueCompositionConfig(Guid.NewGuid(), Guid.NewGuid()).Validate().Should().ContainSingle()
            .Which.Should().Contain("exactly one");
    }

    [Fact]
    public void Empty_guid_counts_as_unset()
    {
        new ContinueCompositionConfig(Guid.Empty, null).Validate().Should().NotBeEmpty();
    }

    [Fact]
    public void Parses_camel_case_json()
    {
        Guid id = Guid.NewGuid();
        ContinueCompositionConfig.TryParse($"{{\"versionId\":\"{id}\"}}", out ContinueCompositionConfig? config, out string? error)
            .Should().BeTrue(error);
        config!.VersionId.Should().Be(id);
        config.ExecutionId.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"executionId\":\"not-a-guid\"}")]
    public void Unusable_json_is_refused_with_a_reason(string json)
    {
        ContinueCompositionConfig.TryParse(json, out _, out string? error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
