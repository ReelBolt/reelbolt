namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// An organization-bound API key for the public API (<c>Authorization: Bearer rbk_&lt;id&gt;_&lt;secret&gt;</c>).
/// Only the SHA-256 of the full token is stored; the plaintext is returned once at creation. The key acts as its
/// service account at the service user's LIVE role, narrowed by <see cref="Scopes"/> and <see cref="ProjectIds"/>.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>The member who created the key. Audit only: the key acts as <see cref="ServiceUserId"/>, so it survives the creator leaving.</summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>The key's own hidden service-account user (<c>application_users.is_service_account</c>); unique.</summary>
    public Guid ServiceUserId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The 12-character public id inside the token (<c>rbk_&lt;KeyId&gt;_...</c>); unique.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the FULL token.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The last four characters of the secret, for recognising a key in a list.</summary>
    public string LastFour { get; set; } = string.Empty;

    /// <summary>Granted scopes (<c>ApiScopes</c>), stored as <c>text[]</c>.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>The projects the key may reach (<c>uuid[]</c>); null = every project the service user can reach.</summary>
    public Guid[]? ProjectIds { get; set; }

    /// <summary>Null = the key never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public Guid? RevokedByUserId { get; set; }

    /// <summary>Spend caps in credit millis (1000 = one credit); null = no cap for that window.</summary>
    public long? DailyCapMillis { get; set; }

    /// <summary>
    /// Set when the owner explicitly removed the daily cap (after confirming). Null with a null daily cap = a legacy
    /// key created before the default daily cap existed.
    /// </summary>
    public DateTime? DailyCapWaivedAt { get; set; }

    public long? WeeklyCapMillis { get; set; }

    public long? MonthlyCapMillis { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public DateTime? ExpiryWarned7dAt { get; set; }

    public DateTime? ExpiryWarned1dAt { get; set; }
}
