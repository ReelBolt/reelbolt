using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>Email normalization for consent: trim + lowercase, one row per result.</summary>
public static class MarketingEmail
{
    /// <summary>The column width of <c>marketing_consents.email</c>.</summary>
    public const int MaxLength = 320;

    public static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Shared contract C1 (identical to the Go relay's <c>CanonicalMailbox</c>): the inbox an address
    /// delivers to, for LIMIT keys only; consent rows stay keyed by <see cref="Normalize"/>. Trim and
    /// lowercase; split at the last <c>@</c> (none: the normalized input); cut the local part at its
    /// first <c>+</c> unless that leaves it empty; for gmail.com and googlemail.com drop every
    /// <c>.</c> from the local part and use gmail.com.
    /// </summary>
    public static string CanonicalMailbox(string? raw)
    {
        string normalized = Normalize(raw);
        int at = normalized.LastIndexOf('@');
        if (at < 0)
        {
            return normalized;
        }

        string local = normalized[..at];
        string domain = normalized[(at + 1)..];
        int plus = local.IndexOf('+');
        if (plus > 0)
        {
            local = local[..plus];
        }

        if (domain is "gmail.com" or "googlemail.com")
        {
            local = local.Replace(".", string.Empty, StringComparison.Ordinal);
            domain = "gmail.com";
        }

        return $"{local}@{domain}";
    }

    /// <summary>The domain of a canonical mailbox (empty when it has no <c>@</c>).</summary>
    public static string DomainOf(string canonical)
    {
        int at = canonical.LastIndexOf('@');
        return at < 0 ? string.Empty : canonical[(at + 1)..];
    }

    /// <summary>A syntactically plausible single address (no display name, exactly one <c>@</c>).</summary>
    public static bool IsValid(string normalized)
    {
        if (normalized.Length is 0 or > MaxLength
            || normalized.Count(c => c == '@') != 1
            || normalized.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or ',' or ';' or '"'))
        {
            return false;
        }

        return MailAddress.TryCreate(normalized, out MailAddress? parsed)
            && string.Equals(parsed.Address, normalized, StringComparison.Ordinal)
            && parsed.Host.Contains('.');
    }
}

/// <summary>The keyed hash the suppression list stores instead of an erased address.</summary>
public interface IMarketingSuppressionHasher
{
    /// <summary>Lowercase hex HMAC-SHA256 (64 chars) of the normalized email.</summary>
    string Hash(string email);
}

/// <summary>
/// HMAC-SHA256 of the normalized email under the <c>marketing-suppression-pepper</c> platform secret.
/// Keyed (not a bare SHA-256) so the stored list cannot be reversed by hashing a dictionary of
/// addresses without the database's protected secret.
/// </summary>
public sealed class MarketingSuppressionHasher : IMarketingSuppressionHasher
{
    public const string SecretName = "marketing-suppression-pepper";

    private readonly IMarketingSecretStore _secrets;

    public MarketingSuppressionHasher(IMarketingSecretStore secrets) => _secrets = secrets;

    public string Hash(string email) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_secrets.GetKey(SecretName), Encoding.UTF8.GetBytes(MarketingEmail.Normalize(email))));
}
