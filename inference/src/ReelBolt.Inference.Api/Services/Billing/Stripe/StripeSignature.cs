using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Billing.Stripe;

/// <summary>
/// <c>Stripe-Signature: t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=&lt;hex&gt;][,v0=&lt;hex&gt;]</c>. The signed payload is
/// <c>"{t}.{rawBody}"</c>, HMAC-SHA256 keyed with the endpoint secret, hex encoded. More than one <c>v1</c> is sent while a
/// secret is rotating; every other scheme (<c>v0</c>, test-mode dummies) is ignored. Messages never contain a secret or signature.
/// </summary>
public static class StripeSignature
{
    public const string HeaderName = "Stripe-Signature";

    /// <summary>Throws <see cref="WebhookVerificationException"/> unless some <c>v1</c> matches the payload under some secret.</summary>
    public static void Verify(string rawBody, string? header, IReadOnlyList<string> secrets, int toleranceSeconds, DateTimeOffset now)
    {
        if (secrets.Count == 0)
        {
            throw new WebhookVerificationException("No webhook secret configured.");
        }

        if (string.IsNullOrWhiteSpace(header))
        {
            throw new WebhookVerificationException("Missing Stripe-Signature header.");
        }

        string? t = null;
        List<string> v1 = [];
        foreach (string part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq];
            string value = part[(eq + 1)..];
            if (key == "t") t = value;
            else if (key == "v1") v1.Add(value);
        }

        if (t is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            throw new WebhookVerificationException("Malformed Stripe-Signature header.");
        }

        if (v1.Count == 0)
        {
            throw new WebhookVerificationException("Stripe-Signature header carries no v1 signature.");
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - seconds) > Math.Max(0, toleranceSeconds))
        {
            throw new WebhookVerificationException("Signature timestamp outside the tolerance window.");
        }

        byte[] payload = Encoding.UTF8.GetBytes($"{t}.{rawBody}");
        List<byte[]> given = [];
        foreach (string hex in v1)
        {
            try
            {
                given.Add(Convert.FromHexString(hex));
            }
            catch (FormatException)
            {
                // A malformed candidate cannot match; keep checking the others.
            }
        }

        bool match = false;
        foreach (string secret in secrets)
        {
            byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
            foreach (byte[] candidate in given)
            {
                // Compare against every candidate (no early exit) so timing does not reveal which one matched.
                match |= CryptographicOperations.FixedTimeEquals(expected, candidate);
            }
        }

        if (!match)
        {
            throw new WebhookVerificationException("Signature mismatch.");
        }
    }

    /// <summary>Builds a header value for a payload (tests and local tooling).</summary>
    public static string Sign(string rawBody, string secret, long timestamp)
    {
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{rawBody}"));
        return $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }
}
