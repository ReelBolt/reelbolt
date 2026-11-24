using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>The user's answer to a permission card.</summary>
/// <param name="Allow">True to let the action run.</param>
/// <param name="AlwaysAllow">True when the user also asked to switch to auto mode (only meaningful with <paramref name="Allow"/>).</param>
/// <param name="Expired">True when no person answered: the request ran out of time, or the browser went away.</param>
public sealed record ApprovalAnswer(bool Allow, bool AlwaysAllow = false, bool Expired = false);

public enum ApprovalAnswerResult
{
    Recorded,

    /// <summary>No such open request for this caller: unknown id, another user's, or already finished.</summary>
    NotFound,

    AlreadyDecided
}

/// <summary>
/// The rendezvous between a running assistant turn (which is waiting on a permission card) and the HTTP
/// request that carries the user's click. Process-local, like <see cref="AssistantTurnRegistry"/>: the turn is
/// running in this process, so a request is exactly as alive as the process is.
/// </summary>
/// <remarks>
/// The request id is a random GUID <em>and</em> the open request remembers who it belongs to: an answer from
/// another user — or another workspace of the same user — is reported as "not found", never "forbidden", so
/// the existence of a request is not confirmed to anybody else.
/// </remarks>
public sealed class AssistantApprovalBroker
{
    private readonly ConcurrentDictionary<Guid, Entry> _open = new();

    /// <summary>How many requests are waiting for an answer (diagnostics and tests).</summary>
    public int OpenCount => _open.Count;

    /// <summary>Opens a request owned by <paramref name="userId"/> in <paramref name="organizationId"/>.</summary>
    public ApprovalTicket Open(Guid id, Guid userId, Guid? organizationId)
    {
        var entry = new Entry(userId, organizationId, new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_open.TryAdd(id, entry))
            throw new InvalidOperationException("An approval request with this id is already open.");
        return new ApprovalTicket(this, id, entry.Completion);
    }

    /// <summary>Records <paramref name="userId"/>'s answer to request <paramref name="id"/>.</summary>
    public ApprovalAnswerResult Answer(Guid id, Guid userId, Guid? organizationId, bool allow, bool alwaysAllow)
    {
        if (!_open.TryGetValue(id, out Entry? entry) || entry.UserId != userId || entry.OrganizationId != organizationId)
            return ApprovalAnswerResult.NotFound;

        return entry.Completion.TrySetResult(new ApprovalAnswer(allow, allow && alwaysAllow))
            ? ApprovalAnswerResult.Recorded
            : ApprovalAnswerResult.AlreadyDecided;
    }

    internal void Close(Guid id) => _open.TryRemove(id, out _);

    private sealed record Entry(Guid UserId, Guid? OrganizationId, TaskCompletionSource<ApprovalAnswer> Completion);
}

/// <summary>An open permission request: await <see cref="Answer"/>; dispose it when the wait is over.</summary>
public sealed class ApprovalTicket : IDisposable
{
    private readonly AssistantApprovalBroker _broker;
    private readonly TaskCompletionSource<ApprovalAnswer> _completion;

    internal ApprovalTicket(AssistantApprovalBroker broker, Guid id, TaskCompletionSource<ApprovalAnswer> completion)
    {
        _broker = broker;
        _completion = completion;
        Id = id;
    }

    public Guid Id { get; }

    public Task<ApprovalAnswer> Answer => _completion.Task;

    /// <summary>
    /// Settles the request as unanswered — nobody can click any more (the browser disconnected) or the time
    /// ran out. A no-op when somebody already answered.
    /// </summary>
    public void Expire() => _completion.TrySetResult(new ApprovalAnswer(Allow: false, Expired: true));

    public void Dispose() => _broker.Close(Id);
}
