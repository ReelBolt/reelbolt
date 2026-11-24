namespace ReelBolt.Shared.Data.Models.Billing;

/// <summary>
/// The cached projection of an organization's credit ledger (table <c>credit_accounts</c>), one row
/// per org. Only <c>CreditLedger</c> writes it, in the same transaction as the ledger entry and under
/// the org's advisory lock; the ledger is the source of truth and <c>BalanceMillis</c> always equals
/// the sum of the entries' balance deltas.
/// </summary>
public class CreditAccount
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Spendable credits including those held by open reservations. Negative only after an overdrawn settlement.</summary>
    public long BalanceMillis { get; set; }

    /// <summary>Credits held by open reservations. Available to reserve = <see cref="BalanceMillis"/> - this.</summary>
    public long ReservedMillis { get; set; }

    /// <summary>Optimistic concurrency token (bumped on every write) in addition to the advisory lock.</summary>
    public long RowVersion { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
