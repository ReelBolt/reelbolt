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
using ReelBolt.Shared.Styles;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// The run's style reaches the model as the agent's own instructions — appended after the agent's
/// prompt — and only while a styled run is in scope; an unstyled run sends the prompt untouched.
/// </summary>
public class AgentStylePromptTests
{
    private const string BasePrompt = "You pick background music.";

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

    private sealed class TestAgent(IAgentChatClientProvider chatClients, IConfiguration configuration, ISkillAgentToolsFactory skills)
        : ReelBoltAgentBase(chatClients, configuration, "StyleTestAgent", "test", AgentType.MusicSupervisor, BasePrompt, skills);

    private static (TestAgent Agent, CapturingChatClient Client) Create()
    {
        var client = new CapturingChatClient();
        var provider = new Mock<IAgentChatClientProvider>();
        provider.Setup(p => p.GetAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IChatClient>(client));
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return (new TestAgent(provider.Object, configuration, Mock.Of<ISkillAgentToolsFactory>()), client);
    }

    [Fact]
    public async Task An_unstyled_run_sends_the_prompt_without_a_style_block()
    {
        (TestAgent agent, CapturingChatClient client) = Create();

        await agent.RunAsync("pick one");

        client.Seen.Should().ContainSingle().Which.Should().Contain(BasePrompt).And.NotContain(StyleNudgeService.BlockStart);
    }

    [Fact]
    public async Task A_styled_run_appends_the_agent_s_style_block_to_its_instructions()
    {
        (TestAgent agent, CapturingChatClient client) = Create();
        var style = new RunStyleSnapshot("Loud", null, null, new StyleConfig(Music: StyleMusicEnergy.High));

        using (RunStyleAmbient.Enter(new RunStyleState(style, null, StyleNudgeService.Instance)))
        {
            await agent.RunAsync("pick one");
        }

        string instructions = client.Seen.Should().ContainSingle().Subject;
        instructions.Should().Contain(BasePrompt + "\n\n" + StyleNudgeService.BlockStart);
        instructions.Should().Contain(StyleNudgeRegistry.Music[StyleMusicEnergy.High][StyleAgentRole.Music]);
        agent.SystemPrompt.Should().Be(BasePrompt, "the singleton agent's own prompt is never mutated");
    }
}
