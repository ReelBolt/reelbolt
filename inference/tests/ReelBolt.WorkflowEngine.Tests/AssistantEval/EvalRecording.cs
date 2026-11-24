using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>One tool call the model made (arguments as JSON) and what the tool answered.</summary>
internal sealed record EvalToolCall(string Name, string ArgsJson, string? Result);

/// <summary>Everything one sample produced: the calls, the final reply and the token counts.</summary>
internal sealed class EvalTranscript
{
    private readonly object _gate = new();
    private readonly List<EvalToolCall> _calls = [];
    private readonly Dictionary<string, int> _callIndexById = new();

    public IReadOnlyList<EvalToolCall> Calls { get { lock (_gate) return _calls.ToList(); } }
    public int ModelRequests { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public string Reply { get; set; } = "";
    public string? Error { get; set; }

    internal void AddCall(string callId, string name, string argsJson)
    {
        lock (_gate)
        {
            _callIndexById[callId] = _calls.Count;
            _calls.Add(new EvalToolCall(name, argsJson, null));
        }
    }

    internal void SetResult(string callId, string result)
    {
        lock (_gate)
        {
            if (_callIndexById.TryGetValue(callId, out int i) && _calls[i].Result is null)
                _calls[i] = _calls[i] with { Result = result.Length > 6000 ? result[..6000] : result };
        }
    }

    internal void AddUsage(UsageDetails? usage)
    {
        lock (_gate)
        {
            ModelRequests++;
            InputTokens += usage?.InputTokenCount ?? 0;
            OutputTokens += usage?.OutputTokenCount ?? 0;
        }
    }
}

/// <summary>
/// Sits BELOW the agent's function-invoking layer, so it sees exactly what the model asked for (each
/// tool call with its arguments), what the real tools answered, and the provider-reported usage of
/// every model request, without changing a single request or response.
/// </summary>
internal sealed class RecordingChatClient(IChatClient inner, EvalTranscript transcript, EvalBudget? budget = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<ChatMessage> list = messages.ToList();
        foreach (FunctionResultContent r in list.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            transcript.SetResult(r.CallId, r.Result?.ToString() ?? "");

        EvalBudget.Reservation? reservation = null;
        if (budget is not null)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.MaxOutputTokens ??= EvalBudget.MaxOutputTokens;
            reservation = budget.TryReserve(EvalBudget.EstimateInput(list, options), options.MaxOutputTokens.Value)
                ?? throw new EvalBudgetExhaustedException(budget.Reason ?? "budget exhausted");
        }

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(list, options, cancellationToken);
        }
        catch
        {
            // Failed or timed-out request: the reservation stays as the worst-case estimate.
            transcript.AddUsage(null);
            throw;
        }

        if (reservation is not null)
            budget!.Settle(reservation, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
        transcript.AddUsage(response.Usage);
        foreach (FunctionCallContent c in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            transcript.AddCall(c.CallId, c.Name, JsonSerializer.Serialize(c.Arguments ?? new Dictionary<string, object?>()));
        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The assistant agent runs non-streaming; the eval recorder only wraps that path.");
}

/// <summary>An <see cref="IAgentChatClientProvider"/> that hands every agent one fixed client.</summary>
internal sealed class FixedChatClientProvider(IChatClient client) : IAgentChatClientProvider
{
    public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
        ValueTask.FromResult(client);
}

/// <summary>
/// A scripted model for the harness's own tests: replays queued responses (a tool call, then text),
/// so the pipeline is exercised end to end without any provider.
/// </summary>
internal sealed class ScriptedChatClient(Func<int, IReadOnlyList<ChatMessage>, ChatResponse> script) : IChatClient
{
    private int _n;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse r = script(Interlocked.Increment(ref _n) - 1, messages.ToList());
        r.Usage ??= new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20, TotalTokenCount = 120 };
        return Task.FromResult(r);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        throw new NotSupportedException();
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
