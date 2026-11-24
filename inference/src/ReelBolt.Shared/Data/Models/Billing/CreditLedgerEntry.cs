namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>Kind of a ledger entry. Append-only, stored as the member name.</summary>
public enum CreditLedgerEntryKind
{
    Grant,
    TopUp,
    PlanRefill,
    Reserve,
    Settle,
    Release,
    Refund,
    Adjust,
    Expire
}

/// <summary>
/// One immutable, double-entry line of the credit ledger (table <c>credit_ledger_entries</c>). Every
/// entry carries two deltas, one against the balance and one against the reserved hold:
/// Grant/TopUp/PlanRefill/Refund +balance; Reserve +reserved; Settle -balance and -reserved;
/// Release -reserved; Expire -balance; Adjust either sign. The account is the sum of its entries.
/// </summary>
public class CreditLedgerEntry
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid OrganizationId { get; set; }
    public CreditLedgerEntryKind Kind { get; set; }

    public long BalanceDeltaMillis { get; set; }
    public long ReservedDeltaMillis { get; set; }

    /// <summary>Account balance after this entry (an audit aid).</summary>
    public long BalanceAfterMillis { get; set; }

    /// <summary>The entry this one acts on: a Settle/Release points at its Reserve, a Refund at its Settle.</summary>
    public Guid? RefEntryId { get; set; }

    /// <summary>The grant a Grant/TopUp/PlanRefill entry created.</summary>
    public Guid? GrantId { get; set; }

    // Plain attribution columns (no foreign keys) so ledger rows outlive what they describe.
    public Guid? ProjectId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? StepResultId { get; set; }

    /// <summary>jsonb array <c>[{"grantId","millis"}]</c>: which grants a Settle drew from (used to route a Refund back).</summary>
    public string? AllocationsJson { get; set; }

    public string? Note { get; set; }

    /// <summary>Unique; a retried write is a no-op that returns the original entry.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
