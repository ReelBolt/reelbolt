using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>
/// Standard Webhooks signing (https://www.standardwebhooks.com): the secret is <c>whsec_</c> + base64 of 32 random
/// bytes; the signature is <c>v1,</c> + base64(HMAC-SHA256(key = the base64-decoded secret, message =
/// <c>"{id}.{timestamp}.{body}"</c>)). During a rotation both secrets sign and the header carries both signatures
/// separated by a space, which the spec allows.
/// </summary>
public static class StandardWebhookSigner
{
    public const string SecretPrefix = "whsec_";
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    public static string GenerateSecret() => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>One <c>v1,&lt;base64&gt;</c> signature.</summary>
    public static string Sign(string secret, string messageId, long timestampSeconds, string body)
    {
        string encoded = secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret;
        byte[] key = Convert.FromBase64String(encoded);
        byte[] message = Encoding.UTF8.GetBytes($"{messageId}.{timestampSeconds}.{body}");
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, message));
    }

    /// <summary>The <c>webhook-signature</c> value: every secret's signature, space-separated.</summary>
    public static string SignAll(IEnumerable<string> secrets, string messageId, long timestampSeconds, string body) =>
        string.Join(' ', secrets.Select(s => Sign(s, messageId, timestampSeconds, body)));
}
