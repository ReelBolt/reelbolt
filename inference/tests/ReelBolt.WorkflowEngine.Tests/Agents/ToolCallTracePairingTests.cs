using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// QA-P0-25: the diagnostics trace pairs each tool result with the call it belongs to (its
/// <c>CallId</c>), not with whichever call was added last — so a render that shares a parallel
/// tool-call turn still carries its <c>outputResolution</c> report into the step output.
/// </summary>
public sealed class ToolCallTracePairingTests
{
    private const string RenderReport = "{\"success\":true,\"outputResolution\":{\"requested\":\"2160p\",\"honoured\":false}}";

    private static ChatResponse Turn(params AIContent[] contents) =>
        new(new ChatMessage(ChatRole.Assistant, contents));

    [Fact]
    public void A_result_is_paired_with_its_own_call_in_a_parallel_turn()
    {
        var render = new FunctionCallContent("call_render", "RenderVideoAndUploadToStorage");
        var other = new FunctionCallContent("call_other", "ReadProjectFile");
        var renderResult = new FunctionResultContent("call_render", RenderReport);
        var otherResult = new FunctionResultContent("call_other", "read ok");

        (IReadOnlyList<AgentToolCallTrace> toolCalls, _) =
            ReelBoltAgentBase.ExtractDiagnostics(new object(), Turn(render, other, renderResult, otherResult));

        toolCalls.Should().HaveCount(2);
        toolCalls[0].ToolName.Should().Be("RenderVideoAndUploadToStorage");
        toolCalls[0].Result.Should().Contain("outputResolution");
        toolCalls[1].ToolName.Should().Be("ReadProjectFile");
        toolCalls[1].Result.Should().Be("read ok");
    }

    [Fact]
    public void The_render_report_of_a_parallel_turn_still_reaches_the_author_resolution()
    {
        var render = new FunctionCallContent("call_render", "RenderVideoAndUploadToStorage");
        var other = new FunctionCallContent("call_other", "ReadProjectFile");
        var renderResult = new FunctionResultContent("call_render", RenderReport);
        var otherResult = new FunctionResultContent("call_other", "read ok");

        (IReadOnlyList<AgentToolCallTrace> toolCalls, _) =
            ReelBoltAgentBase.ExtractDiagnostics(new object(), Turn(render, other, renderResult, otherResult));

        string merged = AuthorRenderResolution.Merge("{\"summary\":\"done\"}", toolCalls);

        merged.Should().NotBe("{\"summary\":\"done\"}");
        AuthorRenderResolution.Merge("{\"summary\":\"done\"}", toolCalls)
            .Should().Contain("outputResolution");
    }

    [Fact]
    public void Without_a_CallId_a_result_still_pairs_with_the_last_call()
    {
        var calls = new FakeContainer(new FakeFunctionCallContent("RenderVideoAndUploadToStorage"), new FakeFunctionCallContent("ReadProjectFile"));
        var results = new FakeContainer(new FakeFunctionResultContent("no ids anywhere"));

        (IReadOnlyList<AgentToolCallTrace> toolCalls, _) =
            ReelBoltAgentBase.ExtractDiagnostics(calls, results);

        toolCalls.Should().HaveCount(2);
        toolCalls[0].Result.Should().BeNull();
        toolCalls[1].Result.Should().Be("no ids anywhere");
    }

    private sealed class FakeContainer(params object[] contents)
    {
        public IReadOnlyList<object> Contents { get; } = contents;
    }

    // The type names carry "FunctionCall"/"FunctionResult" exactly as the extractor matches, and neither
    // exposes a CallId — the pre-framework shape the last-call fallback still has to serve.
    private sealed class FakeFunctionCallContent(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class FakeFunctionResultContent(string result)
    {
        public string Result { get; } = result;
    }
}
