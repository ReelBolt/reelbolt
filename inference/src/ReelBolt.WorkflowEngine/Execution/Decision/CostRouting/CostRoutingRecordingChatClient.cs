using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// Records what a routed agent run's model calls actually did, so the observation row's
/// <c>DownstreamOutcomeJson</c> can be filled in with a real outcome rather than an intention.
///
/// <para>
/// A <see cref="DelegatingChatClient"/> is the right seam for exactly one reason: the run's chat
/// client is the only object this feature hands the agent, so wrapping it costs the executor nothing
/// (the ticket permits exactly one statement there, and that statement is already spent on the
/// routing decision itself) while still giving the outcome a place to be observed from inside the
/// run. It observes — it never alters, retries, or short-circuits anything.
/// </para>
///
/// <para>
/// <b>Nothing here may throw on its own account.</b> Both overrides rethrow the inner call's
/// exception unchanged after recording it (a swallowed model error would turn a failing step into a
/// silently wrong one); the recording itself is pure field assignment on a per-run state object.
/// </para>
/// </summary>
internal sealed class CostRoutingRecordingChatClient : DelegatingChatClient
{
    /// <summary>
    /// Cap on a recorded error message. The column is a JSON blob, and an HTTP failure from a chat
    /// provider can carry a multi-KB body; the outcome only needs enough to tell "timed out" from
    /// "401". No credentials are ever read out of the message — it is the exception's own text.
    /// </summary>
    internal const int MaxErrorChars = 400;

    private readonly CostRoutingRunState _state;

    public CostRoutingRecordingChatClient(IChatClient innerClient, CostRoutingRunState state)
        : base(innerClient)
    {
        _state = state;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
            _state.RecordChatCallSucceeded(HasJsonObject(response));
            return response;
        }
        catch (Exception ex)
        {
            _state.RecordChatCallFailed(Describe(ex));
            throw;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => RecordStreamAsync(base.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);

    /// <summary>
    /// Streaming twin of the recording above. A stream is only known to have succeeded once it
    /// completes, and it carries no single response body to look for a JSON object in, so a
    /// completed stream records "succeeded, no JSON object observed" — deliberately not a guess that
    /// it produced structured output. ReelBolt's agents run non-streaming today; this exists so a
    /// future streaming path is observed rather than silently invisible.
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> RecordStreamAsync(
        IAsyncEnumerable<ChatResponseUpdate> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using (IAsyncEnumerator<ChatResponseUpdate> enumerator = source.GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                ChatResponseUpdate current;

                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;

                    current = enumerator.Current;
                }
                catch (Exception ex)
                {
                    _state.RecordChatCallFailed(Describe(ex));
                    throw;
                }

                yield return current;
            }
        }

        _state.RecordChatCallSucceeded(responseHadJsonObject: false);
    }

    /// <summary>
    /// Whether this response carries a parseable JSON object — the same predicate
    /// <c>AgentStepExecutor</c> fails a structured-output step on, so "the tier produced a usable
    /// answer" is measured here in the terms the step itself will judge it by.
    /// </summary>
    private static bool HasJsonObject(ChatResponse response) =>
        RobustJsonExtractor.ExtractLastJsonObject(response.Text ?? string.Empty) is not null;

    private static string Describe(Exception ex)
    {
        string message = $"{ex.GetType().Name}: {ex.Message}";
        return message.Length <= MaxErrorChars ? message : message[..MaxErrorChars];
    }
}
