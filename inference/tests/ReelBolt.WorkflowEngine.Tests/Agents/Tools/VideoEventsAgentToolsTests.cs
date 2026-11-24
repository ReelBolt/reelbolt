using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>The agent twins of the read tools: only inside a run, six reads a step, and "not available" when the run has no analysis.</summary>
public class VideoEventsAgentToolsTests
{
    private static VideoEventsAgentTools Tools(WorkflowExecutionContext? context)
    {
        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(context);
        Mock<IServiceScopeFactory> scopes = new();
        scopes.Setup(s => s.CreateScope()).Throws(new InvalidOperationException("no database in this test"));
        return new VideoEventsAgentTools(accessor.Object, scopes.Object, Mock.Of<IProjectFileWorkspace>());
    }

    private static WorkflowExecutionContext Context() =>
        new() { ExecutionId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), CorrelationId = "t", StepResultId = Guid.NewGuid() };

    [Fact]
    public async Task Outside_a_run_the_tools_refuse()
    {
        Func<Task> act = () => Tools(null).ListVideoEvents();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_run_with_no_readable_analysis_answers_not_available_not_an_error()
    {
        JsonNode answer = JsonNode.Parse(await Tools(Context()).DescribeVideoWindow("e0", "e3"))!;
        answer["available"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_seventh_read_in_a_step_is_refused_and_another_step_starts_fresh()
    {
        VideoEventsAgentTools tools = Tools(Context());
        for (int i = 0; i < 6; i++)
            (await tools.ListVideoEvents()).Should().NotContain("read limit reached");
        JsonNode.Parse(await tools.ListVideoEvents())!["error"]!.GetValue<string>().Should().Be("read limit reached");
        JsonNode.Parse(await tools.DescribeVideoWindow("e0", "e1"))!["error"]!.GetValue<string>().Should().Be("read limit reached");

        (await Tools(Context()).ListVideoEvents()).Should().NotContain("read limit reached");
    }
}
