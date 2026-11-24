namespace ReelBolt.Shared.Data.Models;

/// <summary>How an <see cref="ApiKeySpend"/> row ended: open holds count at what they hold, closed rows at what they charged.</summary>
public enum ApiKeySpendStatus
{
    Open,
    Settled,
    Released
}

/// <summary>
/// One credit hold made for work an API key started (<c>api_key_spend</c>). The ledger writes it inside the
/// same locked transaction as the reservation, so the per-key caps are decided atomically with the hold:
/// <see cref="HeldMillis"/> counts against the key's windows while <see cref="Status"/> is Open, and
/// <see cref="ChargedMillis"/> counts once Settled (a Release charges 0). API-owned; reached only through
/// the key, so it carries no tenant filter.
/// </summary>
public class ApiKeySpend
{
    public Guid Id { get; set; }

    public Guid ApiKeyId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>The reservation's <c>credit_ledger_entries.id</c> (<c>Kind = Reserve</c>); unique.</summary>
    public Guid ReservationEntryId { get; set; }

    /// <summary>The credits held by the reservation.</summary>
    public long HeldMillis { get; set; }

    /// <summary>What was actually charged when the row settled; null while open (and for a release).</summary>
    public long? ChargedMillis { get; set; }

    public ApiKeySpendStatus Status { get; set; } = ApiKeySpendStatus.Open;

    /// <summary>When the hold was made. The window a spend counts in is the one this instant falls in.</summary>
    public DateTime CreatedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}
