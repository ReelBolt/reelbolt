using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Coverage for the bounded cost-routing descriptor. The load-bearing assertions are the two
/// negative ones: the descriptor can never exceed its hard cap, and raw upstream (prior-step) text
/// can never appear in it — the feature's whole reason for existing is to ask a cheap decision model
/// a cheap question, and raw prior-step output is exactly the unbounded blob that would turn it into
/// an expensive one (and a leak of project content into a decision provider).
/// </summary>
public class CostRoutingDescriptorTests
{
    private const string Marker = "TOPSECRET-UPSTREAM-OUTPUT";

    [Fact]
    public void Descriptor_is_within_the_hard_character_cap_for_a_pathological_input()
    {
        string hugeInput = string.Concat(Enumerable.Repeat(Marker, 20000));

        string descriptor = CostRoutingDescriptor.Build(
            stepOrder: 42,
            agentName: new string('a', 5000),
            agentType: AgentType.AuthorAgent,
            stepType: StepType.Agent,
            toolCount: 12,
            outputSchemaName: new string('s', 5000),
            priorStepCount: 9,
            iterationCount: 3,
            resolvedAgentInput: hugeInput);

        descriptor.Length.Should().BeLessThanOrEqualTo(CostRoutingDescriptor.MaxDescriptorChars);
    }

    [Fact]
    public void Descriptor_never_contains_raw_prior_step_text()
    {
        string upstream = $"{Marker} — a 4MB prior step output would go here";

        string descriptor = CostRoutingDescriptor.Build(
            stepOrder: 2,
            agentName: "Author",
            agentType: AgentType.AuthorAgent,
            stepType: StepType.Agent,
            toolCount: 8,
            outputSchemaName: "AuthorOutput",
            priorStepCount: 1,
            iterationCount: 0,
            resolvedAgentInput: string.Concat(Enumerable.Repeat(upstream, 200)));

        descriptor.Should().NotContain(Marker);
        descriptor.Should().NotContain("prior step output would go here");
    }

    [Fact]
    public void Descriptor_reports_the_input_size_and_a_hash_instead_of_its_content()
    {
        string input = new string('x', 1234);

        string descriptor = CostRoutingDescriptor.Build(
            stepOrder: 1,
            agentName: "Author",
            agentType: AgentType.AuthorAgent,
            stepType: StepType.Agent,
            toolCount: 8,
            outputSchemaName: "AuthorOutput",
            priorStepCount: 0,
            iterationCount: 0,
            resolvedAgentInput: input);

        descriptor.Should().Contain("\"inputChars\":1234");
        descriptor.Should().Contain("\"inputSha256\":\"");

        // Same size, different content ⇒ a different hash. That is what keeps the descriptor useful
        // (a re-run against a changed context is visible) without carrying the context.
        string other = CostRoutingDescriptor.Build(
            stepOrder: 1,
            agentName: "Author",
            agentType: AgentType.AuthorAgent,
            stepType: StepType.Agent,
            toolCount: 8,
            outputSchemaName: "AuthorOutput",
            priorStepCount: 0,
            iterationCount: 0,
            resolvedAgentInput: new string('y', 1234));

        other.Should().NotBe(descriptor);
    }

    [Fact]
    public void Descriptor_carries_the_step_scalars_the_decision_is_made_from()
    {
        string descriptor = CostRoutingDescriptor.Build(
            stepOrder: 7,
            agentName: "MotionGraphicsPlanner",
            agentType: AgentType.MotionGraphicsPlanner,
            stepType: StepType.Agent,
            toolCount: 21,
            outputSchemaName: "MotionGraphicsPlanOutput",
            priorStepCount: 4,
            iterationCount: 2,
            resolvedAgentInput: "prompt");

        descriptor.Should().Contain("\"stepOrder\":7");
        descriptor.Should().Contain("\"agentName\":\"MotionGraphicsPlanner\"");
        descriptor.Should().Contain("\"agentType\":\"MotionGraphicsPlanner\"");
        descriptor.Should().Contain("\"stepType\":\"Agent\"");
        descriptor.Should().Contain("\"toolCount\":21");
        descriptor.Should().Contain("\"outputSchema\":\"MotionGraphicsPlanOutput\"");
        descriptor.Should().Contain("\"priorStepCount\":4");
        descriptor.Should().Contain("\"iterationCount\":2");
    }

    [Fact]
    public void Descriptor_handles_a_null_input_and_a_null_schema()
    {
        string descriptor = CostRoutingDescriptor.Build(
            stepOrder: 1,
            agentName: "Author",
            agentType: AgentType.AuthorAgent,
            stepType: StepType.Agent,
            toolCount: 0,
            outputSchemaName: null,
            priorStepCount: 0,
            iterationCount: 0,
            resolvedAgentInput: null);

        descriptor.Should().Contain("\"inputChars\":0");
        descriptor.Should().Contain("\"outputSchema\":\"\"");
        descriptor.Length.Should().BeLessThanOrEqualTo(CostRoutingDescriptor.MaxDescriptorChars);
    }
}
