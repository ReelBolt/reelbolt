using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>What a presented link token turned out to be.</summary>
public enum MarketingTokenStatus
{
    Valid,
    Invalid,
    Expired,
}

/// <summary>A verification result; <see cref="ConsentId"/> is meaningful only when <see cref="Status"/> is Valid.</summary>
public readonly record struct MarketingTokenResult(MarketingTokenStatus Status, Guid ConsentId)
{
    public static MarketingTokenResult Invalid => new(MarketingTokenStatus.Invalid, Guid.Empty);
}

/// <summary>
/// The signed confirm and unsubscribe links (plan "Signed tokens"). A token carries only the key id,
/// the consent row id and (confirm only) the expiry: never an email.
/// </summary>
public interface IMarketingLinkTokens
{
    /// <summary><c>u1.{kid}.{b64url(consentId)}.{b64url(mac)}</c>; does not expire.</summary>
    string CreateUnsubscribeToken(Guid consentId);

    /// <summary><c>c1.{kid}.{b64url(consentId)}.{expUnix}.{b64url(mac)}</c>.</summary>
    string CreateConfirmToken(Guid consentId, DateTimeOffset expiresAt);

    MarketingTokenResult VerifyUnsubscribe(string? token);

    /// <summary>Checks the MAC first, then the expiry: a tampered expiry is Invalid, not Expired.</summary>
    MarketingTokenResult VerifyConfirm(string? token, DateTimeOffset now);
}

/// <summary>
/// HMAC-SHA256 link tokens keyed by <c>platform_secrets</c> rows named
/// <c>marketing-link-token-{kid}</c> — never the assistant confirmation key.
///
/// <para><b>Rotation.</b> The key id travels in the token. <see cref="KnownKeyIds"/> lists every id
/// that still verifies (unsubscribe links never expire, so old ids stay); new tokens use the last
/// one. An id outside the list is Invalid without ever touching the secret store, so a forged id
/// cannot make the store generate a key.</para>
///
/// <para><b>MAC.</b> Over the length-prefixed framing (4-byte big-endian UTF-8 length, then the bytes)
/// of the purpose (<c>"unsubscribe"</c> or <c>"confirm"</c>), the consent id ("D" format) and, for
/// confirm, the decimal expiry. The purpose is inside the MAC, so relabelling a confirm token as an
/// unsubscribe one fails. Compared with <see cref="CryptographicOperations.FixedTimeEquals"/>.</para>
/// </summary>
public sealed class MarketingLinkTokens : IMarketingLinkTokens
{
    /// <summary>Every key id that verifies, oldest first; the last one signs.</summary>
    public static readonly IReadOnlyList<string> KnownKeyIds = ["k1"];

    private const string UnsubscribePrefix = "u1";
    private const string ConfirmPrefix = "c1";
    private const string UnsubscribePurpose = "unsubscribe";
    private const string ConfirmPurpose = "confirm";
    private const int MaxTokenLength = 256;

    private readonly IMarketingSecretStore _secrets;

    public MarketingLinkTokens(IMarketingSecretStore secrets) => _secrets = secrets;

    /// <summary>The <c>platform_secrets</c> name of a key id.</summary>
    public static string SecretNameFor(string kid) => "marketing-link-token-" + kid;

    private static string CurrentKid => KnownKeyIds[^1];

    public string CreateUnsubscribeToken(Guid consentId)
    {
        string kid = CurrentKid;
        byte[] mac = Mac(kid, UnsubscribePurpose, consentId, expiry: null);
        return string.Join('.', UnsubscribePrefix, kid, Base64Url(consentId.ToByteArray()), Base64Url(mac));
    }

    public string CreateConfirmToken(Guid consentId, DateTimeOffset expiresAt)
    {
        string kid = CurrentKid;
        string exp = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        byte[] mac = Mac(kid, ConfirmPurpose, consentId, exp);
        return string.Join('.', ConfirmPrefix, kid, Base64Url(consentId.ToByteArray()), exp, Base64Url(mac));
    }

    public MarketingTokenResult VerifyUnsubscribe(string? token)
    {
        string[]? parts = Split(token, 4);
        if (parts is null || parts[0] != UnsubscribePrefix)
        {
            return MarketingTokenResult.Invalid;
        }

        return Check(parts[1], UnsubscribePurpose, parts[2], expiry: null, parts[3], out Guid id)
            ? new MarketingTokenResult(MarketingTokenStatus.Valid, id)
            : MarketingTokenResult.Invalid;
    }

    public MarketingTokenResult VerifyConfirm(string? token, DateTimeOffset now)
    {
        string[]? parts = Split(token, 5);
        if (parts is null || parts[0] != ConfirmPrefix
            || parts[3].Length is 0 or > 12
            || !parts[3].All(char.IsAsciiDigit)
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long expUnix))
        {
            return MarketingTokenResult.Invalid;
        }

        if (!Check(parts[1], ConfirmPurpose, parts[2], parts[3], parts[4], out Guid id))
        {
            return MarketingTokenResult.Invalid;
        }

        return now.ToUnixTimeSeconds() > expUnix
            ? new MarketingTokenResult(MarketingTokenStatus.Expired, id)
            : new MarketingTokenResult(MarketingTokenStatus.Valid, id);
    }

    private static string[]? Split(string? token, int count)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        string[] parts = token.Split('.');
        return parts.Length == count ? parts : null;
    }

    private bool Check(string kid, string purpose, string encodedId, string? expiry, string encodedMac, out Guid id)
    {
        id = Guid.Empty;
        if (!KnownKeyIds.Contains(kid, StringComparer.Ordinal))
        {
            return false;
        }

        byte[]? idBytes = FromBase64Url(encodedId);
        byte[]? presented = FromBase64Url(encodedMac);
        if (idBytes is not { Length: 16 } || presented is null)
        {
            return false;
        }

        Guid candidate = new(idBytes);
        // Canonical encoding only: base64 leaves spare bits in the last character, so a second
        // spelling of the same bytes exists. It is not the token we minted.
        if (Base64Url(idBytes) != encodedId || Base64Url(presented) != encodedMac)
        {
            return false;
        }

        byte[] expected = Mac(kid, purpose, candidate, expiry);
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    private byte[] Mac(string kid, string purpose, Guid consentId, string? expiry)
    {
        using MemoryStream message = new();
        Frame(message, purpose);
        Frame(message, consentId.ToString("D"));
        if (expiry is not null)
        {
            Frame(message, expiry);
        }

        return HMACSHA256.HashData(_secrets.GetKey(SecretNameFor(kid)), message.ToArray());
    }

    private static void Frame(MemoryStream message, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        message.Write(length);
        message.Write(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string value)
    {
        if (value.Length == 0 || value.Length > 128)
        {
            return null;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => "!" };
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
