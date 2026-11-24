using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A key travels in an HTTP header (ASCII only). On staging a fal.ai key pasted from a shortened
/// display ended in "…", was stored, and every test then failed with "Request headers must contain
/// only ASCII characters". It is now refused when it is saved.
/// </summary>
public class InferenceProviderApiKeyValidationTests
{
    [Theory]
    [InlineData("4f1c2a9e-1b2c-4d5e-8f90-0a1b2c3d4e5f:3a7b9c249…")]
    [InlineData("sk-abc123...")]
    public void A_shortened_key_is_refused_with_a_plain_message(string key) =>
        InferenceProvidersController.ApiKeyProblem(key).Should().Contain("shortened");

    [Theory]
    [InlineData("sk-abc 123")]
    [InlineData("sk-abc\n123")]
    [InlineData("sk-abç123")]
    public void A_key_that_cannot_be_a_header_value_is_refused(string key) =>
        InferenceProvidersController.ApiKeyProblem(key).Should().NotBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("env:")]
    [InlineData("  sk-ant-api03-AbC_d-123  ")]
    [InlineData("4f1c2a9e-1b2c-4d5e-8f90-0a1b2c3d4e5f:3a7b9c2491d0e8f7")]
    public void Real_keys_omissions_and_clears_are_accepted(string? key) =>
        InferenceProvidersController.ApiKeyProblem(key).Should().BeNull();
}
