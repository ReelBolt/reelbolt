using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The state of a thread's most recent assistant turn, as reported by the re-attach entry point
/// (<c>GET /api/v1/assistant/threads/{threadId}/turn</c>).
/// </summary>
/// <remarks>
/// <see cref="Idle"/> means nothing is running on the thread and there is no finished turn to re-attach
/// to. The other four are the states of a turn the hub still knows about: it is answering
/// (<see cref="Running"/>), it is waiting on a permission card (<see cref="AwaitingApproval"/>), or it
/// ended (<see cref="Completed"/> / <see cref="Failed"/>).
/// </remarks>
public enum AssistantTurnState
{
    Idle,
    Running,
    AwaitingApproval,
    Completed,
    Failed
}

/// <summary>The wire vocabulary for <see cref="AssistantTurnState"/> — the literals the web client switches on.</summary>
public static class AssistantTurnStateText
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static string Of(AssistantTurnState state) => state switch
    {
        AssistantTurnState.Running => Running,
        AssistantTurnState.AwaitingApproval => AwaitingApproval,
        AssistantTurnState.Completed => Completed,
        AssistantTurnState.Failed => Failed,
        _ => Idle
    };
}

/// <summary>
/// A turn the server is answering (or has just answered) independently of the SSE connection that asked
/// for it, so a client that goes away — a phone screen switching off, a navigation, a reload — does not
/// stop the work, and a client that comes back can re-attach and read the live turn or its finished reply.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The turn used to be written straight to the one HTTP response: when that
/// connection died the reply was lost. The shape is the one <c>CLAUDE.md</c> § Workflow Execution
/// Liveness records for workflow runs — the work is not owned by the transport. Here the SSE request is a
/// <b>subscriber</b> to a turn the process owns: <see cref="Emit"/> fans every frame out to the live
/// subscribers and keeps it in <see cref="Subscribe">the log</see>, so a re-attach replays the turn from
/// the start and then streams what is still arriving.
/// </para>
/// <para>
/// <b>Everything here is bounded.</b> "Survives a disconnect" must not mean "grows without limit": a
/// subscriber that stops reading is <b>dropped</b> (a bounded channel, never an unbounded buffer behind a
/// dead connection), the frame log is capped by count and by bytes (and a replay that lost frames says so
/// with a <see cref="ReplayTruncatedType"/> frame rather than silently showing less), and a turn that
/// outlives its lifetime is reclaimed even if its <c>finally</c> never ran — see
/// <see cref="AssistantTurnHub.Reclaim"/>.
/// </para>
/// <para>
/// <b>Process-local, on purpose.</b> Like <see cref="AssistantTurnRegistry"/> and
/// <see cref="AssistantApprovalBroker"/>, a restart forgets a live turn — and a turn whose process died
/// really is gone. Re-attach is therefore not durable across a deploy; the client falls back to the
/// thread's persisted messages.
/// </para>
/// <para>
/// <b>A disconnect never becomes an allow.</b> Nothing here settles a permission card. A card left pending
/// when the browser goes away stays pending (the gate no longer watches the connection): it expires only on its
/// own timeout, so a client that comes back re-attaches, sees the same card replayed with the same arguments
/// and may still answer it. The tool runs only on that explicit answer (or the user's own auto mode).
/// </para>
/// </remarks>
public sealed class AssistantLiveTurn
{
    /// <summary>The most frames a turn's replay log holds before the oldest non-preamble frames are dropped.</summary>
    public const int MaxLogFrames = 4_096;

    /// <summary>The most characters a turn's replay log holds before the oldest non-preamble frames are dropped.</summary>
    public const int MaxLogChars = 1_000_000;

    /// <summary>How many frames a subscriber may fall behind before it is dropped.</summary>
    public const int SubscriberCapacity = 2_048;

    /// <summary>The wire type of the frame a truncated replay starts with, so a client is told it is seeing a partial turn.</summary>
    public const string ReplayTruncatedType = "data-turnTruncated";

    private const string ReplayTruncatedFrame =
        "data: {\"type\":\"data-turnTruncated\",\"data\":{\"truncated\":true,\"note\":\"This is a partial replay of a long turn; the thread's stored history has the whole reply.\"}}\n\n";

    /// <summary>The frames a replay must always keep: the <c>start</c> and <c>text-start</c> a client keys the message on.</summary>
    private const int PreambleFrames = 2;

    private readonly object _gate = new();
    private readonly List<string> _log = [];
    private readonly List<Channel<string>> _subscribers = [];
    private int _logChars;
    private bool _truncated;
    private DateTime _lastAttendedAt = DateTime.UtcNow;

    internal AssistantLiveTurn(Guid turnId, Guid threadId, Guid userId, Guid? organizationId, TimeSpan maxLifetime)
    {
        TurnId = turnId;
        ThreadId = threadId;
        UserId = userId;
        OrganizationId = organizationId;
        MaxLifetime = maxLifetime;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>The id of this turn, stable across every re-attach.</summary>
    public Guid TurnId { get; }

    public Guid ThreadId { get; }

    /// <summary>The caller the turn belongs to. A re-attach from anybody else is reported as "not found".</summary>
    public Guid UserId { get; }

    public Guid? OrganizationId { get; }

    public DateTime StartedAt { get; }

    /// <summary><c>reply</c> for a turn a person started, <c>run_wake</c> for one the platform started when a watched run finished.</summary>
    public string Kind { get; set; } = "reply";

    /// <summary>The turn's hard wall clock, from <see cref="StartedAt"/> — past it the hub reclaims the turn.</summary>
    public TimeSpan MaxLifetime { get; }

    /// <summary>
    /// How long the turn has left before its wall clock. An attach bounds its read with this, so a
    /// subscriber whose turn never completes returns instead of hanging.
    /// </summary>
    public TimeSpan RemainingLifetime => MaxLifetime - (DateTime.UtcNow - StartedAt);

    /// <summary>True once the replay log has dropped frames; a re-attach's backlog then starts with a marker frame.</summary>
    public bool ReplayTruncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    /// <summary>The <c>start</c> frame's message id, once the stream preamble has been written.</summary>
    public Guid? MessageId { get; private set; }

    /// <summary>The open permission card's id while <see cref="State"/> is <see cref="AssistantTurnState.AwaitingApproval"/>.</summary>
    public Guid? PendingApprovalId { get; private set; }

    public AssistantTurnState State { get; private set; } = AssistantTurnState.Running;

    /// <summary>
    /// Records that somebody looked at the turn just now (the original connection went away, a card was answered).
    /// The detached ceiling counts from here.
    /// </summary>
    public void Touch()
    {
        lock (_gate)
        {
            _lastAttendedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// True when nobody is watching the turn and nothing is waiting on a person: no re-attached subscriber, no open
    /// permission card (a card has its own timeout), and the last attention was at least <paramref name="span"/> ago.
    /// This — not the bare time since a disconnect — is what lets a turn nobody wants any more release its slot,
    /// while a turn a phone re-attached to, or one held on a card, is left to finish.
    /// </summary>
    public bool IsUnattendedFor(TimeSpan span)
    {
        lock (_gate)
        {
            return _subscribers.Count == 0
                && State != AssistantTurnState.AwaitingApproval
                && DateTime.UtcNow - _lastAttendedAt >= span;
        }
    }

    /// <summary>How many re-attached subscribers are reading the turn right now (diagnostics and tests).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public void SetMessageId(Guid messageId)
    {
        lock (_gate)
        {
            MessageId ??= messageId;
        }
    }

    /// <summary>Records that the turn is now held behind a permission card.</summary>
    public void MarkAwaitingApproval(Guid approvalId)
    {
        lock (_gate)
        {
            PendingApprovalId = approvalId;
            State = AssistantTurnState.AwaitingApproval;
        }
    }

    /// <summary>Records that a permission card was settled (allowed, declined or expired) and the turn is moving on.</summary>
    public void MarkDecided()
    {
        lock (_gate)
        {
            PendingApprovalId = null;
            _lastAttendedAt = DateTime.UtcNow;
            if (State == AssistantTurnState.AwaitingApproval)
                State = AssistantTurnState.Running;
        }
    }

    /// <summary>
    /// Publishes one already-serialized SSE frame: it is appended to the log (so a later re-attach replays
    /// the turn) and fanned out to every live subscriber. A subscriber that has stopped reading is dropped
    /// rather than buffered behind.
    /// </summary>
    public void Emit(string frame) => FanOut(frame, log: true);

    /// <summary>
    /// Sends a heartbeat comment to the live subscribers without adding it to the log: a re-attach must
    /// replay the turn's real frames, never a ping that belonged to a dead connection.
    /// </summary>
    public void Ping() => FanOut(": ping\n\n", log: false);

    private void FanOut(string frame, bool log)
    {
        List<Channel<string>> subscribers;
        lock (_gate)
        {
            if (log)
                AppendLocked(frame);

            subscribers = [.. _subscribers];
        }

        foreach (Channel<string> channel in subscribers)
        {
            if (channel.Writer.TryWrite(frame))
                continue;

            // The subscriber is not draining (a slow or dead connection): complete and drop it instead of
            // growing a buffer behind it. Its reader then ends, so the attaching request returns.
            channel.Writer.TryComplete();
            Unsubscribe(channel);
        }
    }

    /// <summary>
    /// Appends a frame to the replay log under the lock, dropping the oldest non-preamble frames when the
    /// log would exceed its frame or byte budget. The preamble (<c>start</c>, <c>text-start</c>) is never
    /// dropped, so a client can always key the replayed message; the drop is recorded as
    /// <see cref="ReplayTruncated"/>.
    /// </summary>
    private void AppendLocked(string frame)
    {
        _log.Add(frame);
        _logChars += frame.Length;

        while ((_log.Count > MaxLogFrames || _logChars > MaxLogChars) && _log.Count > PreambleFrames)
        {
            _logChars -= _log[PreambleFrames].Length;
            _log.RemoveAt(PreambleFrames);
            _truncated = true;
        }
    }

    /// <summary>Marks the turn terminal and releases every subscriber's reader. Idempotent.</summary>
    public void Complete(AssistantTurnState state)
    {
        List<Channel<string>> subscribers;
        lock (_gate)
        {
            if (State is AssistantTurnState.Completed or AssistantTurnState.Failed)
                return;

            State = state;
            PendingApprovalId = null;
            subscribers = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (Channel<string> channel in subscribers)
            channel.Writer.TryComplete();
    }

    /// <summary>
    /// Subscribes to the turn: the returned handle carries the frames emitted so far (the replay) and a
    /// reader for everything still to come. A subscription taken after the turn ended gets the whole log
    /// and a reader that is already complete. A truncated replay starts with a marker frame.
    /// </summary>
    public AssistantTurnSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(SubscriberCapacity)
        {
            SingleReader = true,
            // Wait mode so a non-blocking TryWrite reports "full" instead of silently discarding the frame:
            // that report is what lets Emit DROP a subscriber that has stopped reading rather than leaving
            // it wedged at its capacity forever. (DropWrite/DropOldest return true from TryWrite and hide
            // the stall, so the subscriber would never be dropped and its reader would never end.)
            FullMode = BoundedChannelFullMode.Wait
        });

        IReadOnlyList<string> backlog;

        lock (_gate)
        {
            backlog = _truncated ? [ReplayTruncatedFrame, .. _log] : [.. _log];

            if (State is AssistantTurnState.Completed or AssistantTurnState.Failed)
                channel.Writer.TryComplete();
            else
                _subscribers.Add(channel);
        }

        return new AssistantTurnSubscription(this, channel, backlog);
    }

    internal void Unsubscribe(Channel<string> channel)
    {
        lock (_gate)
        {
            _subscribers.Remove(channel);
            _lastAttendedAt = DateTime.UtcNow;
        }
    }

    /// <summary>The frames the turn has already emitted, in order — the replay a re-attach starts with.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _log];
        }
    }
}

/// <summary>One subscriber to a live turn: the backlog to replay first, then the frames still arriving.</summary>
public sealed class AssistantTurnSubscription : IDisposable
{
    private readonly AssistantLiveTurn _owner;
    private readonly Channel<string> _channel;

    internal AssistantTurnSubscription(AssistantLiveTurn owner, Channel<string> channel, IReadOnlyList<string> backlog)
    {
        _owner = owner;
        _channel = channel;
        Backlog = backlog;
    }

    /// <summary>The frames emitted before this subscription was taken, in order (a marker frame first if truncated).</summary>
    public IReadOnlyList<string> Backlog { get; }

    /// <summary>Everything emitted after <see cref="Backlog"/>, until the turn ends or this subscriber is dropped.</summary>
    public ChannelReader<string> Reader => _channel.Reader;

    public void Dispose() => _owner.Unsubscribe(_channel);
}

/// <summary>
/// The process-wide set of live assistant turns, keyed by thread. A new turn on a thread replaces the
/// previous one (a thread has at most one turn in flight); a finished turn is kept for a retention window
/// so a client that re-attaches after the reply landed still gets it.
/// </summary>
/// <remarks>
/// <para>
/// Singleton, like the registry and the approval broker. It is <b>self-reclaiming</b> rather than relying
/// on every turn reaching its <c>finally</c>: <see cref="Reclaim"/> drops a turn past its own wall clock
/// even if it never completed, so a turn that leaked on an exception cannot wedge every later re-attach,
/// and it drops a finished turn once its retention window has passed. Capacity is a further bound on
/// memory; only terminal turns are evicted by it, so a running turn is never silently lost.
/// </para>
/// <para>
/// <b>Why <c>Begin</c> did not have to move inside the controller's <c>try/finally</c>.</b> The obvious
/// fix for a leaked turn is to register it inside the same block that completes it — but the controller's
/// <c>Begin</c> sits ~300 lines before that block (the credit check, the tool wiring and the session
/// context all run in between), and moving it would re-indent the whole action. Reclamation here is a
/// <b>stronger</b> guarantee than that: it runs on <em>every</em> hub access rather than on a timer or a
/// sweep, so it cannot be starved, and it also covers a turn whose thread died without unwinding at all.
/// A turn past its wall clock is therefore reclaimed whether or not anything ever called
/// <c>Complete</c> on it.
/// </para>
/// </remarks>
public sealed class AssistantTurnHub
{
    private readonly ConcurrentDictionary<Guid, AssistantLiveTurn> _turns = new();
    private readonly int _capacity;
    private readonly TimeSpan _completedRetention;

    public AssistantTurnHub(int capacity = 512, TimeSpan? completedRetention = null)
    {
        _capacity = capacity;
        _completedRetention = completedRetention ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// The process-wide hub a host gets when it registers nothing. It is the same object for the whole
    /// process — which is exactly what a live turn needs — and a host (or a test) that wants its own can
    /// still register <see cref="AssistantTurnHub"/> in DI and that instance wins.
    /// </summary>
    /// <remarks>
    /// A static rather than a required DI registration so this feature is self-contained: a turn outliving
    /// its connection is process state, and a host that never registered it must still be correct. Resolve
    /// it with <c>GetService&lt;AssistantTurnHub&gt;() ?? AssistantTurnHub.Shared</c>.
    /// </remarks>
    public static AssistantTurnHub Shared { get; } = new();

    /// <summary>
    /// Starts a turn on <paramref name="threadId"/>, replacing any earlier one for that thread.
    /// <paramref name="maxLifetime"/> is the turn's hard wall clock: past it the turn is reclaimed even if
    /// it never completed.
    /// </summary>
    public AssistantLiveTurn Begin(Guid threadId, Guid userId, Guid? organizationId, TimeSpan? maxLifetime = null)
    {
        Reclaim();

        var turn = new AssistantLiveTurn(
            Guid.NewGuid(),
            threadId,
            userId,
            organizationId,
            maxLifetime ?? TimeSpan.FromSeconds(AssistantChatLimits.DefaultMaxTurnSeconds));

        _turns[threadId] = turn;
        Trim();
        return turn;
    }

    /// <summary>The turn on <paramref name="threadId"/>, if this process still knows one.</summary>
    public AssistantLiveTurn? Find(Guid threadId)
    {
        Reclaim();
        return _turns.TryGetValue(threadId, out AssistantLiveTurn? turn) ? turn : null;
    }

    /// <summary>
    /// The turn on <paramref name="threadId"/> when it belongs to this caller and workspace, else null —
    /// a foreign or unknown turn is reported the same way, so its existence is never confirmed.
    /// </summary>
    public AssistantLiveTurn? FindFor(Guid threadId, Guid userId, Guid? organizationId)
    {
        Reclaim();

        return _turns.TryGetValue(threadId, out AssistantLiveTurn? turn)
            && turn.UserId == userId
            && turn.OrganizationId == organizationId
                ? turn
                : null;
    }

    /// <summary>
    /// The turns of this caller and workspace that are writing right now (running, or waiting on an approval card).
    /// Process-local like a re-attach: a turn running on another replica is not listed here.
    /// </summary>
    public IReadOnlyList<AssistantLiveTurn> ListFor(Guid userId, Guid? organizationId)
    {
        Reclaim();
        return _turns.Values
            .Where(t => t.UserId == userId && t.OrganizationId == organizationId
                && t.State is AssistantTurnState.Running or AssistantTurnState.AwaitingApproval)
            .OrderBy(t => t.StartedAt)
            .ToList();
    }

    /// <summary>How many turns the hub is holding (diagnostics and tests).</summary>
    public int Count
    {
        get
        {
            Reclaim();
            return _turns.Count;
        }
    }

    /// <summary>
    /// Drops turns that cannot still be live: a non-terminal turn past its own wall clock (its <c>finally</c>
    /// never ran — an exception before it, a lost process thread) and a terminal turn past the retention
    /// window. Runs on every hub access, so it needs no timer and no hosted service.
    /// </summary>
    private void Reclaim()
    {
        DateTime now = DateTime.UtcNow;

        foreach (KeyValuePair<Guid, AssistantLiveTurn> entry in _turns)
        {
            AssistantLiveTurn turn = entry.Value;
            TimeSpan age = now - turn.StartedAt;

            bool expired = turn.State is AssistantTurnState.Completed or AssistantTurnState.Failed
                ? age > _completedRetention
                : age > turn.MaxLifetime;

            if (expired)
                _turns.TryRemove(new KeyValuePair<Guid, AssistantLiveTurn>(entry.Key, turn));
        }
    }

    private void Trim()
    {
        if (_turns.Count <= _capacity)
            return;

        foreach (KeyValuePair<Guid, AssistantLiveTurn> entry in _turns.OrderBy(e => e.Value.StartedAt))
        {
            if (_turns.Count <= _capacity)
                break;

            if (entry.Value.State is AssistantTurnState.Completed or AssistantTurnState.Failed)
                _turns.TryRemove(entry.Key, out _);
        }
    }
}
