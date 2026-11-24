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
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>The brief reaches a real agent through <c>ReelBoltAgentBase.CreateAgentAsync</c>, in the documented order.</summary>
public class RunBriefAgentWiringTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public List<string> Seen { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.Add(string.Join("\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text)) + "\n" + options?.Instructions);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
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

    private sealed class TestAgent(IAgentChatClientProvider chatClients, IConfiguration configuration, ISkillAgentToolsFactory skills, AgentType type)
        : ReelBoltAgentBase(chatClients, configuration, "BriefTestAgent", "test", type, "BASE PROMPT", skills);

    private static async Task<string> PromptOf(AgentType type)
    {
        var client = new CapturingChatClient();
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(client));
        var agent = new TestAgent(provider.Object, new ConfigurationBuilder().Build(), Mock.Of<ISkillAgentToolsFactory>(), type);
        await agent.RunAsync("go");
        return client.Seen.Single();
    }

    [Fact]
    public async Task An_italian_run_decorates_the_scriptwriter_before_the_style_and_leaves_an_analyzer_alone()
    {
        var execution = new WorkflowExecution
        {
            StyleConfigJson = StyleJson.Serialize(new RunStyleSnapshot("S", null, null, new StyleConfig(Pacing: StylePacing.Fast)))
        };
        using IDisposable brief = RunBriefAmbient.Enter(new RunBriefState(new StoredRunBrief(Language: "it", LengthSec: 60)));
        using IDisposable style = RunStyleAmbient.Enter(RunStyleState.From(execution, StyleNudgeService.Instance, briefSetsLanguage: true));

        string writer = await PromptOf(AgentType.ScriptwriterAgent);
        int prompt = writer.IndexOf("BASE PROMPT", StringComparison.Ordinal);
        int runBrief = writer.IndexOf("[RUN BRIEF]", StringComparison.Ordinal);
        int styleBlock = writer.IndexOf("[STYLE INSTRUCTIONS]", StringComparison.Ordinal);
        prompt.Should().BeGreaterThanOrEqualTo(0);
        runBrief.Should().BeGreaterThan(prompt);
        styleBlock.Should().BeGreaterThan(runBrief);
        writer.Should().Contain("Italian (it)");

        string analyzer = await PromptOf(AgentType.CodeStructureAnalyzer);
        analyzer.Should().NotContain("[RUN BRIEF]").And.NotContain("Italian");
    }
}
