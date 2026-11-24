#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>The assistant turn budget: the tool-loop iteration cap (80 by default, configurable), the 100-credit ceiling and the nesting of hold, minimum and ceiling.</summary>
public class AssistantTurnBudgetTests
{
    private sealed class ScriptedClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            // Keep asking for the tool for as long as it is offered.
            ChatMessage reply = options?.Tools is { Count: > 0 }
                ? new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), "Ping", new Dictionary<string, object?>())])
                : new ChatMessage(ChatRole.Assistant, "done");
            return Task.FromResult(new ChatResponse(reply));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class Provider(IChatClient client) : IAgentChatClientProvider
    {
        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) => new(client);
        public ValueTask<ResolvedInferenceProvider?> ResolveProviderAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) => new((ResolvedInferenceProvider?)null);
        public ValueTask<string?> ResolveReasoningEffortAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) => new((string?)null);
    }

    private static async Task<(int ToolCalls, int ModelCalls, IChatClient Client)> RunLoopAsync(int? iterations)
    {
        var inner = new ScriptedClient();
        AssistantTurnGuard guard = AssistantTurnGuard.ForCall(null, null, null, null, new ServiceCollection().BuildServiceProvider(), new AssistantToolContext(Guid.NewGuid()));
        IChatClient client = await guard.Wrap(new Provider(inner), iterations).GetAsync(AgentType.Assistant, null, default);
        int toolCalls = 0;
        AIFunction ping = AIFunctionFactory.Create(() => { toolCalls++; return "pong"; }, "Ping");
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [ping] });
        return (toolCalls, inner.Calls, client);
    }

    [Fact]
    public void The_iteration_cap_defaults_to_80_and_is_configurable_with_a_minimum_of_one()
    {
        AssistantChatLimits.MaxToolIterations(null).Should().Be(80);
        AssistantChatLimits.MaxToolIterations(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxToolIterations"] = "5" }).Build()).Should().Be(5);
        AssistantChatLimits.MaxToolIterations(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxToolIterations"] = "0" }).Build()).Should().Be(80);
    }

    [Fact]
    public async Task A_configured_cap_of_5_stops_the_tool_loop_at_5_tool_calls()
    {
        (int tools, int model, IChatClient client) = await RunLoopAsync(5);
        tools.Should().Be(5);
        model.Should().BeLessThanOrEqualTo(7);
        client.GetService<FunctionInvokingChatClient>()!.MaximumIterationsPerRequest.Should().Be(5);
    }

    [Fact]
    public async Task A_loop_of_81_scripted_tool_calls_stops_at_the_default_80()
    {
        (int tools, _, IChatClient client) = await RunLoopAsync(AssistantChatLimits.MaxToolIterations(null));
        tools.Should().Be(80);
        client.GetService<FunctionInvokingChatClient>()!.MaximumIterationsPerRequest.Should().Be(80);
    }

    [Fact]
    public async Task A_provider_client_that_already_carries_a_tool_loop_is_not_wrapped_in_a_second_one()
    {
        var inner = new ScriptedClient();
        IChatClient own = new FunctionInvokingChatClient(inner) { MaximumIterationsPerRequest = 7 };
        AssistantTurnGuard guard = AssistantTurnGuard.ForCall(null, null, null, null, new ServiceCollection().BuildServiceProvider(), new AssistantToolContext(Guid.NewGuid()));
        IChatClient client = await guard.Wrap(new Provider(own), 3).GetAsync(AgentType.Assistant, null, default);
        client.GetService<FunctionInvokingChatClient>()!.MaximumIterationsPerRequest.Should().Be(7, "the existing loop is left alone");
    }

    [Fact]
    public void The_turn_ceiling_defaults_to_100_credits_and_the_hold_stays_5()
    {
        var o = new NonWorkflowChargeOptions();
        o.NonWorkflow.AssistantTurnCeilingCredits.Should().Be(100);
        o.NonWorkflow.AssistantTurnHoldCredits.Should().Be(5);
        TurnBudgetValidation.Problem(o).Should().BeNull();
    }

    [Fact]
    public void A_hold_above_the_ceiling_or_a_minimum_above_the_hold_is_a_configuration_error()
    {
        var hold = new NonWorkflowChargeOptions();
        hold.NonWorkflow.AssistantTurnHoldCredits = 200;
        TurnBudgetValidation.Problem(hold).Should().Contain("AssistantTurnHoldCredits");

        var min = new NonWorkflowChargeOptions();
        min.NonWorkflow.AssistantMinTurnCredits = 9;
        TurnBudgetValidation.Problem(min).Should().Contain("AssistantMinTurnCredits");
    }

    [Fact]
    public void The_turn_wall_clock_must_stay_within_half_the_stale_hold_age()
    {
        NonWorkflowChargeService.TurnClockProblem(TimeSpan.FromSeconds(1800)).Should().BeNull();
        NonWorkflowChargeService.TurnClockProblem(TimeSpan.FromSeconds(1801)).Should().NotBeNull();
    }
}
