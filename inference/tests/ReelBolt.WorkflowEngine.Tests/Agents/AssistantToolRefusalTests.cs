using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Tests for <see cref="AssistantToolRefusal"/> — the convention the MCP bridge uses to tell a tool
/// body's own refusal (an authority/plan/rate-limit decline the model reads) from a successful result,
/// so a refusal is metered as one. The convention is structural JSON, not prose: a top-level
/// <c>error</c> string.
/// </summary>
public class AssistantToolRefusalTests
{
    [Theory]
    // The exact envelopes the assistant's tool surfaces emit (see Agents/Tools/*.cs).
    [InlineData("""{"error":"admin privileges required"}""")]
    [InlineData("""{"error":"no project in context"}""")]
    [InlineData("""{"error":"run limit reached — 10 of 10 paid runs in the last hour on this project; try again in 12 minutes","code":"RUN_RATE_LIMITED","bucket":"paid","used":10,"limit":10,"windowMinutes":60,"retryAfterSeconds":720}""")]
    [InlineData("""{"error":"workflow not found"}""")]
    [InlineData("""{"error":"INSUFFICIENT_CREDITS","code":"INSUFFICIENT_CREDITS"}""")]
    [InlineData("""{"error":"reworded entirely later","planGate":{"code":"PLAN_LIMIT"}}""")]
    public void A_refusal_envelope_is_recognised(string text)
    {
        AssistantToolRefusal.IsRefusal(text).Should().BeTrue();
    }

    [Theory]
    [InlineData("[]")]                                   // ListWorkflowExecutions / ListAgents on an empty page
    [InlineData("""{"id":"...","name":"Project"}""")]     // a payload
    [InlineData("""{"workflowId":"...","stepCount":3}""")] // ProposeWorkflow's success shape
    [InlineData("plain text file content, not JSON")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("""{"error":null}""")]                    // explicitly no error
    [InlineData("""{"error":""}""")]                      // empty string is not a refusal
    [InlineData("""{"errors":["a problem"]}""")]          // WorkflowValidationResult uses a LIST
    public void Anything_that_is_not_a_refusal_envelope_is_not_recognised(string text)
    {
        AssistantToolRefusal.IsRefusal(text).Should().BeFalse();
    }

    [Fact]
    public void A_failed_connection_test_result_is_not_mistaken_for_a_refusal()
    {
        // THE false-positive this convention has to survive: TestInferenceProvider returns a
        // TestInferenceProviderResponse whose Ok is false and whose Error is populated when the
        // provider's connection test fails — a SUCCESSFUL tool call reporting a failed test.
        // AIFunctionFactory serialises that DTO with its DECLARED property names (no camelCase
        // policy), so the key is `Error`, not `error`. Serialising the real DTO with the default
        // options is exactly what the factory does, which is what makes this a real guard rather
        // than a hand-written string: a future serializer-policy change that made them collide
        // fails here instead of silently mislabelling every failed connection test as a refusal.
        string realResultText = JsonSerializer.Serialize(
            new TestInferenceProviderResponse(Ok: false, LatencyMs: 7, Error: "connection refused", ResponsePreview: null));

        realResultText.Should().Contain("Error");
        AssistantToolRefusal.IsRefusal(realResultText).Should().BeFalse(
            "a failed provider connection test is a successful tool call reporting a failure");
    }
}
