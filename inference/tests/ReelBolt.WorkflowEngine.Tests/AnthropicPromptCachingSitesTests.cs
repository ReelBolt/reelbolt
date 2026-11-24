using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The three assembly sites that opt into Anthropic prompt caching, and the one that deliberately
/// does not go further than the newest turn. See the initiative's audit.md: the engine agent and the
/// room seat have a system prefix that is stable for the whole run; the assistant's is rebuilt every
/// turn and may only carry the newest-turn breakpoint. Every non-Anthropic kind keeps the exact
/// options shape it had before this property existed.
/// </summary>
public class AnthropicPromptCachingSitesTests
{
    [Fact]
    public void Engine_agent_options_opt_into_system_and_turn_caching()
    {
        ChatOptions options = ReelBoltAgentBase.BuildChatOptions(
            new ConfigurationBuilder().Build(), "AnyAgent", defaults: null);

        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.SystemAndTurn);
    }

    [Theory]
    [InlineData(InferenceProviderKind.Anthropic, AnthropicPromptCacheMode.TurnOnly)]
    [InlineData(InferenceProviderKind.AzureOpenAI, AnthropicPromptCacheMode.Off)]
    [InlineData(InferenceProviderKind.OpenAICompatible, AnthropicPromptCacheMode.Off)]
    [InlineData(InferenceProviderKind.Gemini, AnthropicPromptCacheMode.Off)]
    [InlineData(InferenceProviderKind.DeepSeek, AnthropicPromptCacheMode.Off)]
    public void Assistant_turn_options_opt_in_only_on_Anthropic(
        InferenceProviderKind kind, AnthropicPromptCacheMode expected)
    {
        ChatOptions? options = AssistantChatLimits.BuildTurnOptions(configuration: null, kind);

        options.Should().NotBeNull();
        AnthropicPromptCaching.ModeOf(options).Should().Be(expected);

        if (expected == AnthropicPromptCacheMode.Off)
        {
            options!.AdditionalProperties.Should().BeNull(
                "a non-Anthropic turn must carry no caching key at all");
        }
    }

    [Fact]
    public void Assistant_turn_options_keep_the_null_shape_for_a_provider_default_non_anthropic_turn()
    {
        // The pre-existing contract: on a kind that accepts no ceiling, an opted-out deployment gets
        // a null options object, byte-identical to before either key existed. The caching opt-in
        // must not turn that into an empty ChatOptions.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [AssistantChatLimits.ReasoningEffortKey] = AssistantChatLimits.ProviderDefaultReasoningEffort,
            })
            .Build();

        AssistantChatLimits.BuildTurnOptions(configuration, InferenceProviderKind.AzureOpenAI)
            .Should().BeNull();
    }
}
