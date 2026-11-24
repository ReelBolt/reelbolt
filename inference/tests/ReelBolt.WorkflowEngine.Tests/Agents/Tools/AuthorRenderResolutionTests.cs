using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// QA-P0-5: an Author step's render reports its <c>outputResolution</c> in the step output, in the
/// top-level shape the API's GetWorkflowExecution warning scan reads.
/// </summary>
public sealed class AuthorRenderResolutionTests
{
    private const string NotHonoured =
        "{\"success\":true,\"outputResolution\":{\"requested\":\"2160p\",\"width\":1920,\"height\":1080,\"honoured\":false,\"reason\":\"your plan renders up to 1080p\"}}";

    private static AgentToolCallTrace Render(string? result) =>
        new() { ToolName = "RenderVideoAndUploadToStorage", Result = result };

    [Fact]
    public void The_render_report_is_added_at_the_top_level_of_a_json_object_output()
    {
        string merged = AuthorRenderResolution.Merge("{\"summary\":\"done\"}", [Render(NotHonoured)]);

        JsonObject node = JsonNode.Parse(merged)!.AsObject();
        node["summary"]!.GetValue<string>().Should().Be("done");
        node["outputResolution"]!["honoured"]!.GetValue<bool>().Should().BeFalse();
        node["outputResolution"]!["height"]!.GetValue<int>().Should().Be(1080);
    }

    /// <summary>QA-P0-23 low: the fenced-object unwrap must not mine an object out of a BARE JSON array and
    /// silently drop the rest of it. A bare array is kept whole.</summary>
    [Fact]
    public void A_bare_json_array_is_wrapped_whole_not_collapsed_to_its_first_object()
    {
        string merged = AuthorRenderResolution.Merge("""[{"a":1},{"b":2}]""", [Render(NotHonoured)]);

        JsonObject node = JsonNode.Parse(merged)!.AsObject();
        node["message"]!.GetValue<string>().Should().Be("""[{"a":1},{"b":2}]""");
        node["outputResolution"]!.AsObject().Should().NotBeNull();
    }

    [Fact]
    public void A_text_output_is_wrapped_so_the_report_stays_machine_readable()
    {
        string merged = AuthorRenderResolution.Merge("Rendered the film.", [Render(NotHonoured)]);

        JsonObject node = JsonNode.Parse(merged)!.AsObject();
        node["message"]!.GetValue<string>().Should().Be("Rendered the film.");
        node["outputResolution"]!["requested"]!.GetValue<string>().Should().Be("2160p");
    }

    /// <summary>QA-P0-23: a fenced JSON object is the Author's real output; it keeps its own properties
    /// (with the report added) instead of being wrapped as <c>{message, outputResolution}</c>, which would
    /// hide it from a downstream reader that extracts the JSON object.</summary>
    [Fact]
    public void A_fenced_json_object_output_keeps_its_own_properties()
    {
        string fenced = "Here is the manifest:\n```json\n{\"summary\":\"done\"}\n```";

        string merged = AuthorRenderResolution.Merge(fenced, [Render(NotHonoured)]);

        JsonObject node = JsonNode.Parse(merged)!.AsObject();
        node.ContainsKey("message").Should().BeFalse();
        node["summary"]!.GetValue<string>().Should().Be("done");
        node["outputResolution"]!["honoured"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>The downstream reader that loses the object today: the loop-back manifest note reads the
    /// Author's step output with the same first-JSON-object extractor.</summary>
    [Fact]
    public void A_fenced_manifest_still_reaches_the_loop_back_note()
    {
        string fenced =
            "```json\n{\"compositions\":[{\"id\":\"c0\",\"componentName\":\"Promo\",\"durationInFrames\":300}],\"video\":{\"fps\":30,\"durationInFrames\":300}}\n```";

        string merged = AuthorRenderResolution.Merge(fenced, [Render(NotHonoured)]);

        ReviewLoopStepExecutor.BuildPreviousManifestNote(merged).Should().NotBeNull().And.Contain("c0");
    }

    [Fact]
    public void The_last_render_wins()
    {
        string first = NotHonoured;
        string last = first.Replace("1920", "3840").Replace("1080", "2160").Replace("false", "true");

        string merged = AuthorRenderResolution.Merge("ok", [Render(first), Render(last)]);

        JsonNode.Parse(merged)!["outputResolution"]!["honoured"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"success\":true}")]
    public void A_render_without_a_report_leaves_the_output_byte_identical(string? toolResult)
    {
        AuthorRenderResolution.Merge("Rendered the film.", [Render(toolResult)]).Should().Be("Rendered the film.");
        AuthorRenderResolution.Merge("Rendered the film.", []).Should().Be("Rendered the film.");
    }

    [Fact]
    public void Another_tools_result_is_not_a_render_report()
    {
        var other = new AgentToolCallTrace { ToolName = "ReadProjectFile", Result = NotHonoured };

        AuthorRenderResolution.Merge("x", [other]).Should().Be("x");
    }

    [Fact]
    public async Task An_author_step_output_carries_the_report_that_GetWorkflowExecution_scans()
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("Author");
        agent.Setup(a => a.AgentType).Returns(AgentType.AuthorAgent);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = "Rendered the film.", ToolCalls = [Render(NotHonoured)] });
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            CorrelationId = "t",
            PendingOutputStorageKey = "projects/p/outputFiles/x.mp4"
        });
        var executor = new AgentStepExecutor(
            registry.Object, accessor.Object, Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(), Mock.Of<IDecisionGate>(), NullLogger<AgentStepExecutor>.Instance);
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "a", AgentType = AgentType.AuthorAgent, IsBuiltIn = true }
        };

        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        });

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails);
        using JsonDocument doc = JsonDocument.Parse(result.Output);
        doc.RootElement.GetProperty("outputResolution").GetProperty("honoured").ValueKind.Should().Be(JsonValueKind.False);
    }
}
