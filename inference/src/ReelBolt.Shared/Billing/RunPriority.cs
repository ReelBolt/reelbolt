using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Shared.Billing;

/// <summary>
/// The plan's queue-priority class (B8c): the ONE value that decides how early a run is dequeued,
/// shared by every queue a run passes through.
///
/// <para><b>Where the number comes from.</b> It is the plan catalog's
/// <c>entitlements.priority</c> field — <c>0</c> on Trial and Creator (standard), <c>1</c> on Pro
/// and Team ("priority rendering"), <c>2</c> on Enterprise (dedicated). It reaches here through
/// <see cref="OrganizationEntitlementSet.Priority"/>, which
/// <see cref="PlanEntitlements.ToSet"/> fills straight from that JSON key, so the plan catalog is
/// the only place the value is authored and there is deliberately no second notion of "important
/// customer" anywhere in the codebase.</para>
///
/// <para><b>Where the number goes.</b> Two queues order on it, and both use this exact value —
/// no remapping, no second scale:
/// <list type="number">
/// <item>the RabbitMQ <c>workflow-execution</c> queue, as the AMQP message priority,
/// honoured because the engine declares the queue with <c>x-max-priority</c>
/// (<c>WorkflowEngine:QueuePriority:MaxPriority</c>) — see
/// <see cref="ToAmqp"/> and the receive-endpoint registration;</item>
/// <item>the runner gateway's <c>runner_jobs.priority</c> column, which D5b's claim orders by
/// (<c>priority DESC, created_at ASC, id</c>). The engine publishes the value onto
/// <c>WorkflowExecutionContext.RunPriority</c> so the compute fabric writes it
/// there verbatim.</item>
/// </list>
/// </para>
///
/// <para><b>Why the clamping is here and not at the queues.</b> AMQP priority is a single byte and
/// RabbitMQ treats a message priority above the queue's <c>x-max-priority</c> as the maximum, so
/// the value below is clamped into <c>[0, 255]</c>: no catalog edit — including a fat-fingered one —
/// can produce a value the publish path cannot carry. A catalog value above the declared queue
/// maximum is not an error, it simply saturates.</para>
///
/// <para><b>Self-host and billing-disabled are unchanged.</b>
/// <see cref="OrganizationEntitlementSet.Unlimited"/> leaves <c>Priority</c> at
/// <see cref="Standard"/>, so a self-host deployment publishes exactly what it published before
/// this feature existed.</para>
/// </summary>
public static class RunPriority
{
    /// <summary>The default class: admitted after every higher class, in creation order. This is the catalog's <c>trial</c>/<c>creator</c> value.</summary>
    public const int Standard = 0;

    /// <summary>
    /// The highest value the scale can carry. AMQP message priority is a single byte, so this is a
    /// property of the transport, not a policy: see the type summary for why the clamp is applied
    /// here rather than trusted to the catalog.
    /// </summary>
    public const int Max = byte.MaxValue;

    /// <summary>
    /// The <c>x-max-priority</c> the engine declares its execution queue with when the operator has
    /// not chosen one. RabbitMQ allocates per-priority internal queues, and its own guidance is to
    /// keep this small; <c>10</c> leaves four times the headroom the shipped catalog uses (max
    /// <c>2</c>) without paying for a 255-wide range on every queue.
    /// </summary>
    public const byte DefaultQueueMax = 10;

    /// <summary>
    /// The queue-priority class an organization's plan entitles it to. Never null, never throws: a
    /// missing or malformed value is <see cref="Standard"/>.
    /// </summary>
    public static int Of(OrganizationEntitlementSet entitlements)
        => Math.Clamp(entitlements.Priority, Standard, Max);

    /// <summary>
    /// <see cref="Of"/> for an organization, read through the single entitlement contract
    /// (<see cref="IOrganizationEntitlements"/>) so the value is always the one the current plan
    /// grants — a downgrade takes effect on the next run, with no separate priority state to keep in
    /// sync.
    /// </summary>
    public static async ValueTask<int> OfAsync(
        IOrganizationEntitlements entitlements, Guid organizationId, CancellationToken cancellationToken = default)
        => Of(await entitlements.GetAsync(organizationId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The AMQP message priority for a queue-priority class: the same number, as the single byte the
    /// protocol carries.
    /// </summary>
    public static byte ToAmqp(int runPriority) => (byte)Math.Clamp(runPriority, Standard, Max);

    /// <summary>
    /// The priority to stamp on a published run, or <c>null</c> when the message must carry no
    /// priority at all. The three publish sites all decide "stamp or not" through this one function
    /// rather than each repeating the comparison, so the wire shape of a
    /// <see cref="Standard"/> run — Trial, Creator, and every self-host deployment — is exactly what
    /// it was before B8c: no priority property, plain publish, no transport feature to depend on.
    /// </summary>
    public static byte? AmqpPriorityToStamp(int runPriority)
        => runPriority > Standard ? ToAmqp(runPriority) : null;
}
