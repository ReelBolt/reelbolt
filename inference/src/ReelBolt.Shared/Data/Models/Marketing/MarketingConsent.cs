namespace ReelBolt.Shared.Data.Models.Marketing;

/// <summary>Current marketing-consent state of one email. Stored as the member name.</summary>
public enum MarketingConsentStatus
{
    Pending,
    OptedIn,
    Withdrawn,
}

/// <summary>Where a consent change came from. Stored as the member name.</summary>
public enum MarketingConsentSource
{
    Signup,
    Settings,
    Checkout,
    AcceptTerms,
    SiteContact,
    SiteFooter,
    Unsubscribe,

    /// <summary>Withdrawn by the person through Brevo's own unsubscribe link or webhook.</summary>
    BrevoUnsubscribe,

    /// <summary>Withdrawn because the person marked a campaign as spam (webhook).</summary>
    EmailComplaint,

    /// <summary>
    /// Withdrawn because Brevo blocklisted the address for a reason the API does not expose. Mail resumes only
    /// after a new confirmed opt-in. Append-only: stored by name.
    /// </summary>
    BrevoBlocklist,
}

/// <summary>Where a contact name came from. Stored as the member name. Append-only enum.</summary>
public enum MarketingNameSource
{
    /// <summary>Derived from the account display name.</summary>
    Account,

    /// <summary>Typed by the visitor into the subscribe form.</summary>
    Form,

    /// <summary>Set by a platform admin; always wins.</summary>
    Admin,
}

/// <summary>What a consent event records. Stored as the member name.</summary>
public enum MarketingConsentAction
{
    Requested,
    Granted,
    Confirmed,
    Withdrawn,
}

/// <summary>
/// Current marketing-consent state, one row per normalized (trimmed, lowercase) email (table
/// <c>marketing_consents</c>). Consent is per individual email, never per org. <see cref="Id"/> is
/// the id the signed confirm and unsubscribe links carry. Withdrawal keeps the row.
/// </summary>
public class MarketingConsent
{
    public Guid Id { get; set; }

    /// <summary>Normalized email (≤320), unique.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The account, when there is one (set null when the user is deleted).</summary>
    public Guid? UserId { get; set; }

    public MarketingConsentStatus Status { get; set; } = MarketingConsentStatus.Pending;

    /// <summary>The consent-wording version shown when this state was reached (≤64).</summary>
    public string WordingVersion { get; set; } = string.Empty;

    public MarketingConsentSource Source { get; set; }

    public DateTime? RequestedAt { get; set; }
    public DateTime? ConsentedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? WithdrawnAt { get; set; }

    /// <summary>Request evidence (≤64).</summary>
    public string? Ip { get; set; }

    /// <summary>Request evidence (≤256).</summary>
    public string? UserAgent { get; set; }

    public DateTime? LastConfirmationSentAt { get; set; }

    /// <summary>
    /// A Withdrawn row whose address asked to subscribe again: the row stays Withdrawn (its evidence
    /// untouched) until a confirm link issued after the withdrawal is clicked. Cleared on confirm, and
    /// by the sweeper once older than the pending retention.
    /// </summary>
    public DateTime? ResubscribeRequestedAt { get; set; }

    /// <summary>When this address was last imported into the managed Brevo list (null: not at Brevo).</summary>
    public DateTime? BrevoSyncedAt { get; set; }

    /// <summary>
    /// The address cannot be mailed (hard bounce, Brevo blocklist). Removes the row from the sendable set;
    /// a later confirmed opt-in clears it. Not a withdrawal: consent is untouched.
    /// </summary>
    public DateTime? UndeliverableAt { get; set; }

    /// <summary>A short stable code for <see cref="UndeliverableAt"/> (≤100).</summary>
    public string? UndeliverableReason { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Contact first name (≤100). Never written to events or logs.</summary>
    public string? FirstName { get; set; }

    /// <summary>Contact last name (≤100). Never written to events or logs.</summary>
    public string? LastName { get; set; }

    /// <summary>Where the name came from; null when never set.</summary>
    public MarketingNameSource? NameSource { get; set; }

    public DateTime? NameUpdatedAt { get; set; }

    /// <summary>When the contact was last pushed to Brevo successfully.</summary>
    public DateTime? BrevoPushedAt { get; set; }

    public int BrevoPushAttempts { get; set; }
    public DateTime? BrevoPushAttemptedAt { get; set; }

    /// <summary>A short stable code of the last failed push (≤100); null when none.</summary>
    public string? BrevoPushErrorCode { get; set; }
}

/// <summary>
/// Single-row (Id = 1) state of the Brevo contact sync worker (table <c>marketing_contact_sync_state</c>).
/// </summary>
public class MarketingContactSyncState
{
    public int Id { get; set; } = 1;
    public DateTime? LastRunAt { get; set; }
    public bool? LastRunOk { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? LastReconcileAt { get; set; }
    public DateTime? RunRequestedAt { get; set; }
}

/// <summary>
/// Append-only history of a <see cref="MarketingConsent"/> (table <c>marketing_consent_events</c>);
/// every state change writes exactly one row. Deleted with its consent row.
/// </summary>
public class MarketingConsentEvent
{
    public Guid Id { get; set; }
    public Guid ConsentId { get; set; }
    public string Email { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public MarketingConsentAction Action { get; set; }
    public MarketingConsentSource Source { get; set; }
    public string WordingVersion { get; set; } = string.Empty;
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An email that must never be exported again after its account was deleted (table
/// <c>marketing_suppressions</c>). Only the keyed HMAC-SHA256 of the normalized email is stored.
/// </summary>
public class MarketingSuppression
{
    public Guid Id { get; set; }

    /// <summary>Lowercase hex HMAC-SHA256 (≤64), unique.</summary>
    public string EmailHmac { get; set; } = string.Empty;

    public DateTime WithdrawnAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Audit row for one admin CSV export of the consent list (table <c>marketing_consent_exports</c>).</summary>
public class MarketingConsentExport
{
    public Guid Id { get; set; }
    public Guid ExportedByUserId { get; set; }
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    public int RowCount { get; set; }
}

/// <summary>
/// An address to delete from Brevo after its account was purged (table <c>marketing_brevo_erasures</c>). The
/// address is held protected (<c>ISecretProtector</c>) and the row is deleted once Brevo confirmed (204/404).
/// </summary>
public class MarketingBrevoErasure
{
    public Guid Id { get; set; }

    /// <summary>The normalized address, protected. Never the plain address.</summary>
    public string ProtectedEmail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
}
