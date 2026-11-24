namespace ReelBolt.Shared.Data.Models;

/// <summary>What a <see cref="UserToken"/> authorises. Stored by name.</summary>
public enum UserTokenPurpose
{
    VerifyEmail,
    ResetPassword,
    EmailChange,
}

/// <summary>
/// A single-use emailed token (verify email, reset password, change email). Only the hex SHA-256 of
/// the raw token is stored; the raw value exists only in the email link (docs/design/saas-accounts.md).
/// </summary>
public class UserToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public UserTokenPurpose Purpose { get; set; }

    /// <summary>Hex SHA-256 of the raw token; unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    /// <summary>The address the token's email was sent to (max 320).</summary>
    public string? SentToEmail { get; set; }

    /// <summary>For an email change: the requested new address, applied on confirmation (max 320).</summary>
    public string? PendingEmail { get; set; }

    /// <summary>The user (e.g. an admin) who requested the token, if not the owner. No FK.</summary>
    public Guid? RequestedByUserId { get; set; }
}
