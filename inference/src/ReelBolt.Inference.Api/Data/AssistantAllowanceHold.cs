using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Data;

/// <summary>Where an <see cref="AssistantAllowanceHold"/> is in its life.</summary>
public enum AssistantAllowanceHoldStatus
{
    /// <summary>Taken before the turn; its <see cref="AssistantAllowanceHold.HeldMillis"/> counts as used.</summary>
    Open,

    /// <summary>Closed at what the turn metered; its <see cref="AssistantAllowanceHold.ConsumedMillis"/> counts as used.</summary>
    Settled,

    /// <summary>Dropped without consuming anything; counts as nothing.</summary>
    Released,

    /// <summary>
    /// Closed by the stale sweep (left open past <c>NonWorkflowChargeService.StaleTurnHoldAge</c>): counts as nothing
    /// until the turn settles late, when it is charged what it metered. Appended: stored by name, never renumbered.
    /// </summary>
    Swept
}

/// <summary>
/// One assistant chat turn or MCP tool call's claim on the monthly assistant allowance (decision 9;
/// docs/metering-and-billing.md "Charging policy"). The authoritative allowance counter: the allowance used in a
/// period is the sum, over the organization's holds created in it, of <see cref="HeldMillis"/> while open and
/// <see cref="ConsumedMillis"/> once settled. It is read and written only under the organization's ledger lock
/// (<c>pg_advisory_xact_lock(hashtext(org_id))</c>, the same one <c>CreditLedger</c> takes), so concurrent turns
/// cannot both see the same allowance left. The part of a turn's hold the allowance could not cover is a credit
/// reservation on the ledger (<see cref="CreditReservationId"/>). API-owned table <c>assistant_allowance_holds</c>.
/// </summary>
public class AssistantAllowanceHold
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary><c>Assistant</c> or <c>Mcp</c>.</summary>
    public UsageSource Source { get; set; }

    /// <summary><c>nwa-hold:{source}:{requestId:N}</c>: replaying a turn's request id returns its hold.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Allowance millicredits held while the turn runs.</summary>
    public long HeldMillis { get; set; }

    /// <summary>
    /// Once settled: the allowance millicredits the turn actually used. While open: what the turn had metered at its
    /// last in-turn cut-off check (<c>NonWorkflowChargeService.CheckTurnAsync</c>), so concurrent turns of the
    /// organization count what it spent beyond its hold; non-zero from the turn's first paid claim on (T-040: the hold is
    /// marked used, so the stale sweep charges it). 0 once released, or swept without a charge.
    /// </summary>
    public long ConsumedMillis { get; set; }

    /// <summary>The ledger reservation for the part of the hold beyond the allowance, if any.</summary>
    public Guid? CreditReservationId { get; set; }

    /// <summary>Millicredits that ledger reservation holds.</summary>
    public long CreditHeldMillis { get; set; }

    /// <summary>Credits charged on the ledger when the turn settled.</summary>
    public long ChargedMillis { get; set; }

    /// <summary>
    /// Metered millicredits that were neither covered by the allowance nor charged. Always 0 for a turn settled since
    /// settlement charges the metered actual (F-3); kept for rows settled before that.
    /// </summary>
    public long UnchargedMillis { get; set; }

    /// <summary>
    /// T-040: the metered actual of a turn whose settlement failed (<c>NonWorkflowChargeService.RecordUnsettledTurnAsync</c>),
    /// kept apart from the in-flight exposure in <see cref="ConsumedMillis"/>. Null while unknown: the stale sweep then
    /// charges a hold marked used (exposure recorded) at least at its hold, and charges exactly this when it is known.
    /// </summary>
    public long? UnsettledMeteredMillis { get; set; }

    public AssistantAllowanceHoldStatus Status { get; set; }

    /// <summary>When the hold was taken; it counts against the allowance period this falls in.</summary>
    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
