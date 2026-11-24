using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>A structured-output agent runs a real tool loop; only its final JSON reaches the step output.</summary>
public class AgentFinalAnswerTests
{
    private sealed class NarratingClient : IChatClient
    {
        private int _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            ChatMessage reply = _calls == 1
                ? new ChatMessage(ChatRole.Assistant,
                    [new TextContent("I'll check the project files for any sound-effect clips before deciding."), new FunctionCallContent("call1", "ListFiles")])
                : new ChatMessage(ChatRole.Assistant, "I have enough to decide.\n\n{\"cues\":[],\"planRationale\":\"none\"}");
            return Task.FromResult(new ChatResponse(reply));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class Plan
    {
        public List<string> Cues { get; set; } = [];
        public string PlanRationale { get; set; } = "";
    }

    private sealed class TestAgent(IAgentChatClientProvider chatClients, IEnumerable<AIFunction> tools)
        : ReelBoltAgentBase(chatClients, new ConfigurationBuilder().Build(), "FinalAnswerAgent", "test", AgentType.SoundDesigner, "BASE",
            Mock.Of<ISkillAgentToolsFactory>(), tools, outputSchemaType: typeof(Plan));

    [Fact]
    public async Task Text_before_a_tool_call_is_a_note_and_the_output_is_only_the_final_json()
    {
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(new NarratingClient()));
        AIFunction listFiles = AIFunctionFactory.Create(() => "[]", "ListFiles");

        AgentRunResult result = await new TestAgent(provider.Object, [listFiles]).RunAsync("go");

        result.Output.Should().Be("{\"cues\":[],\"planRationale\":\"none\"}");
        result.Output.Should().NotContain("I'll check").And.NotContain("I have enough");
        result.Reasoning.Should().Contain(r => r.Contains("I have enough"));
        result.ToolCalls.Should().Contain(t => t.ToolName == "ListFiles");
        result.Reasoning.Should().Contain(r => r.Contains("I'll check"));
    }
}
