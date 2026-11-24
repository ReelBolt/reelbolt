using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// Verifies the <c>X-Hub-Signature-256</c> header GitHub sends with every webhook delivery.
///
/// <para>
/// This is the only credential the webhook endpoint has, so the rules are absolute: no secret
/// configured means nothing is ever accepted (the caller answers <c>503</c> rather than running an
/// unverifiable endpoint), a missing or malformed header is a rejection, and the comparison is a
/// constant-time one over the raw bytes of the exact body that was signed.
/// </para>
///
/// <para>
/// <b>Why constant time matters here specifically.</b> An ordinary <c>==</c> over the hex digest
/// returns as soon as it finds a differing byte, so an attacker who can time the endpoint can recover
/// the correct HMAC one byte at a time and then forge any delivery they like — including a push that
/// overwrites a customer's project. <see cref="CryptographicOperations.FixedTimeEquals"/> removes that
/// signal; the digest length is fixed at 32 bytes, so nothing is learned from the length either.
/// </para>
/// </summary>
public interface IGitHubWebhookVerifier
{
    /// <summary>
    /// True only when a webhook secret is configured. False is not "verification disabled" — it is
    /// "this deployment cannot accept a webhook at all".
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// True when <paramref name="signatureHeader"/> is a well-formed <c>sha256=&lt;hex&gt;</c> HMAC of
    /// <paramref name="rawBody"/> under the configured secret. False for an absent header, a wrong
    /// scheme, malformed hex, any length mismatch, or a digest that simply does not match.
    /// </summary>
    bool TryVerify(string rawBody, string? signatureHeader);
}

/// <inheritdoc />
public sealed class GitHubWebhookVerifier : IGitHubWebhookVerifier
{
    private const string Prefix = "sha256=";

    private readonly GitHubOptions _options;

    public GitHubWebhookVerifier(IOptions<GitHubOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured => _options.HasWebhookSecret;

    public bool TryVerify(string rawBody, string? signatureHeader)
    {
        if (!IsConfigured) return false;
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        string header = signatureHeader.Trim();
        if (!header.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        string hex = header[Prefix.Length..];
        // 64 hex characters, always. Checked before decoding so a short string cannot reach the
        // comparison and so the early return reveals nothing beyond "this is not a digest".
        if (hex.Length != 64) return false;

        Span<byte> presented = stackalloc byte[32];
        if (!TryParseHex(hex, presented)) return false;

        byte[] secret = Encoding.UTF8.GetBytes(_options.WebhookSecret!);
        byte[] body = Encoding.UTF8.GetBytes(rawBody);

        Span<byte> expected = stackalloc byte[32];
        int written = HMACSHA256.HashData(secret, body, expected);
        if (written != expected.Length) return false;

        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    /// <summary>Lower-case or upper-case hex, nothing else. Rejects any non-hex character.</summary>
    private static bool TryParseHex(string hex, Span<byte> destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            int high = HexValue(hex[i * 2]);
            int low = HexValue(hex[(i * 2) + 1]);
            if (high < 0 || low < 0) return false;
            destination[i] = (byte)((high << 4) | low);
        }

        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
