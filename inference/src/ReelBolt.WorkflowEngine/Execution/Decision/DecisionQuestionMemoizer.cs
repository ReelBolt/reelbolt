using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Inference;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// A per-execution memo of decision answers, so a question that has already been answered inside
/// one execution is never sent to the model a second time. This is what stops a <c>ReviewLoop</c>
/// iteration (and every other repeat caller) from re-asking an unchanged question about an
/// unchanged item.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this memoizes — and what it deliberately does not.</b> This memoizer only suppresses
/// <i>duplicate outbound model calls inside one execution</i>. A memoized answer is the same
/// answer: the same question over the same state against the same offered set returns the value
/// the model already gave, so a caller that folds decision answers into a step's output produces
/// exactly what it would have produced by asking again. Consequently the fully resolved step input
/// is unchanged, and this type adds <b>no</b> new component to any step-cache key.
/// </para>
/// <para>
/// The cross-execution <c>workflow_step_cache_entries</c> key is a <b>different mechanism</b> and
/// is not what this memoizes: that cache lets a <i>later</i> execution reuse a prior step's output
/// and is invalidated by the resolved step input; this memo lives and dies inside a single
/// execution and never outlives it. A step whose output genuinely depends on decision answers
/// (e.g. an <c>AcceptAt</c> threshold) is covered by the existing key through the resolved step
/// input — never by a new key field.
/// </para>
/// <para>
/// The memoizer is a decorator <b>at the call site</b>, not an <see cref="IDecisionClient"/>
/// implementation: the caller owns provider resolution (and therefore the no-provider branch) and
/// passes the already-resolved client in.
/// </para>
/// </remarks>
public interface IDecisionQuestionMemoizer
{
    /// <summary>
    /// Reads the answer for <paramref name="question"/> from the memo. Returns false when this
    /// execution has not answered that exact question (same site, name, kind, offered options and
    /// state content) yet — a miss never mutates the memo.
    /// </summary>
    bool TryGet(Guid executionId, string site, DecisionQuestion question, DecisionState state, out DecisionAnswer answer);

    /// <summary>
    /// Records an answer for <paramref name="question"/> under <paramref name="executionId"/>.
    /// </summary>
    void Store(Guid executionId, string site, DecisionQuestion question, DecisionState state, DecisionAnswer answer);

    /// <summary>
    /// Answers cached hits from the memo and sends only the <b>misses</b> as a single batched
    /// <see cref="IDecisionClient.DecideAsync"/> call, storing every returned answer. Returns the
    /// union of cached and fresh answers, with the fresh call's <c>Model</c> and token counts, or
    /// the model name <c>"memo"</c> with zero tokens when every question was a hit. When every
    /// question is a hit, <paramref name="client"/> is not called at all.
    /// </summary>
    Task<DecisionResult> DecideAsync(
        Guid executionId, string site, DecisionState state,
        IReadOnlyList<DecisionQuestion> questions, IDecisionClient client, CancellationToken ct);

    /// <summary>
    /// Drops every memoized answer for one execution. Called by whoever finishes the execution —
    /// an entry that is never evicted must still not grow without limit (see
    /// <see cref="DecisionQuestionMemoizer.MaxExecutions"/>).
    /// </summary>
    void Evict(Guid executionId);
}

/// <summary>
/// Bounded, thread-safe implementation of <see cref="IDecisionQuestionMemoizer"/>.
/// </summary>
/// <remarks>
/// Registered as a <b>singleton</b>, and that is load-bearing rather than incidental: the memo is
/// keyed by execution id and the execution runner (<c>WorkflowExecutionRunner</c>) is a singleton
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. A scoped registration would be
/// resolved per message scope and would memoize across nothing useful.
/// </remarks>
public sealed class DecisionQuestionMemoizer : IDecisionQuestionMemoizer
{
    /// <summary>
    /// Hard cap on how many executions may be retained at once. When a new execution pushes the
    /// count past this bound, the oldest retained execution is dropped first. Together with
    /// <see cref="Evict"/> this is what keeps a leaked entry from growing without limit.
    /// </summary>
    public const int MaxExecutions = 64;

    /// <summary>
    /// The <see cref="DecisionResult.Model"/> reported when every question was served from the
    /// memo and no outbound call happened.
    /// </summary>
    private const string MemoModel = "memo";

    /// <summary>
    /// The outer map is concurrent; each inner <see cref="Dictionary{TKey, TValue}"/> is not, so it
    /// is locked for both reads and writes. Contention is nil in practice — one execution is
    /// memoized by one step-running thread — and locking the read as well is what makes a
    /// concurrent read during a write safe rather than merely unlikely to be observed.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Dictionary<string, DecisionAnswer>> _entries = new();

    /// <summary>
    /// Insertion order of retained executions, oldest first, so the cap can evict oldest-first.
    /// Ids are enqueued once per successfully added execution; an <see cref="Evict"/> cannot remove
    /// one from the middle of the queue, so a dequeued id may already be gone (its removal is then
    /// a no-op) — never the other way round.
    /// </summary>
    private readonly ConcurrentQueue<Guid> _insertionOrder = new();

    /// <summary>Serializes admission and eviction so the cap is enforced by one thread at a time.</summary>
    private readonly object _admissionGate = new();

    /// <summary>
    /// Number of executions currently retained. Exposed for the bound test; never used to drive
    /// behaviour.
    /// </summary>
    public int RetainedExecutionCount => _entries.Count;

    public bool TryGet(Guid executionId, string site, DecisionQuestion question, DecisionState state, out DecisionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(state);

        answer = null!;

        if (!_entries.TryGetValue(executionId, out Dictionary<string, DecisionAnswer>? entry))
            return false;

        lock (entry)
        {
            if (entry.TryGetValue(ComputeKey(site, question, state), out DecisionAnswer stored))
            {
                answer = stored;
                return true;
            }
        }

        return false;
    }

    public void Store(Guid executionId, string site, DecisionQuestion question, DecisionState state, DecisionAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(answer);

        Dictionary<string, DecisionAnswer> entry = GetOrCreateEntry(executionId);

        lock (entry)
            entry[ComputeKey(site, question, state)] = answer;
    }

    public async Task<DecisionResult> DecideAsync(
        Guid executionId,
        string site,
        DecisionState state,
        IReadOnlyList<DecisionQuestion> questions,
        IDecisionClient client,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(client);

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        List<DecisionQuestion> misses = [];

        foreach (DecisionQuestion question in questions)
        {
            if (TryGet(executionId, site, question, state, out DecisionAnswer cached))
                answers[question.Name] = cached;
            else
                misses.Add(question);
        }

        if (misses.Count == 0)
        {
            // Every question was already answered in this execution — no outbound call at all.
            return new DecisionResult(MemoModel, answers, 0, 0);
        }

        DecisionResult fresh = await client.DecideAsync(state, misses, ct);

        foreach (DecisionQuestion question in misses)
        {
            // A question the client did not answer is not memoized: a later miss must be able to
            // re-ask rather than being pinned to an absent answer.
            if (fresh.Answers.TryGetValue(question.Name, out DecisionAnswer answer))
            {
                Store(executionId, site, question, state, answer);
                answers[question.Name] = answer;
            }
        }

        return new DecisionResult(fresh.Model, answers, fresh.InputTokens, fresh.OutputTokens);
    }

    public void Evict(Guid executionId)
    {
        _entries.TryRemove(executionId, out _);
    }

    /// <summary>
    /// Computes the memo key: a SHA-256 over the length-prefixed components
    /// <c>site</c>, <c>name</c>, <c>kind</c>, the offered options (count, then each option) and
    /// <c>SHA256(state)</c> — uppercase hex, the same <c>Convert.ToHexString</c> form as
    /// <c>DecisionGate.HashOptions</c> and <c>ResolvedDecisionProvider.ComputeCacheKey</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every component is length-prefixed, and the join is therefore unambiguous.</b> The
    /// components are caller-supplied strings that may themselves contain the separator, so a bare
    /// <c>|</c> join cannot tell <c>site="a", name="b|c"</c> from <c>site="a|b", name="c"</c> — two
    /// different questions that would silently share one answer for as long as the execution lives.
    /// Each component is emitted as <c>&lt;length&gt;:&lt;value&gt;|</c>, and the option list carries
    /// its own count before the options, so no arrangement of separators inside a value can
    /// reproduce another arrangement's material.
    /// </para>
    /// <para>
    /// Options are joined in the caller's order and deliberately <b>not</b> sorted: the offered set
    /// is caller-ordered, so a reordered set is a different question, and a changed set already
    /// changes the key. The state content is hashed into the material so a one-character change
    /// re-asks. <paramref name="site"/> is part of the key so two call sites asking an otherwise
    /// identical question cannot collide within one execution.
    /// </para>
    /// </remarks>
    public static string ComputeKey(string site, DecisionQuestion question, DecisionState state)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(state);

        var material = new StringBuilder();

        AppendComponent(material, site);
        AppendComponent(material, question.Name);
        AppendComponent(material, question.Kind.ToString());
        AppendComponent(material, question.Options.Count.ToString(CultureInfo.InvariantCulture));

        foreach (string option in question.Options)
            AppendComponent(material, option);

        AppendComponent(material, Sha256Hex(state.Content));

        return Sha256Hex(material.ToString());
    }

    /// <summary>
    /// Appends one component as <c>&lt;length&gt;:&lt;value&gt;|</c>. The length is what makes the
    /// join injective: a value containing the separator changes its own length, so it cannot trade
    /// characters with its neighbour and produce another component set's material.
    /// </summary>
    private static void AppendComponent(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
               .Append(':')
               .Append(value)
               .Append('|');

    private Dictionary<string, DecisionAnswer> GetOrCreateEntry(Guid executionId)
    {
        if (_entries.TryGetValue(executionId, out Dictionary<string, DecisionAnswer>? existing))
            return existing;

        var created = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);

        if (_entries.TryAdd(executionId, created))
        {
            _insertionOrder.Enqueue(executionId);
            TrimToCap();
            return created;
        }

        // Another thread won the add; its instance is the one everyone must share.
        return _entries[executionId];
    }

    private void TrimToCap()
    {
        lock (_admissionGate)
        {
            // Oldest-first: drop the front of the insertion queue until the cap holds. The loop is
            // bounded by the queue — an already-evicted id simply finds nothing to remove.
            while (_entries.Count > MaxExecutions && _insertionOrder.TryDequeue(out Guid oldest))
                _entries.TryRemove(oldest, out _);
        }
    }

    private static string Sha256Hex(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}
