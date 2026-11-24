using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.ImportWorker;

/// <summary>
/// The single bearer check. The scheme is compared case-insensitively, the secret itself is never
/// compared directly: both sides are SHA-256 hashed and the digests are compared in constant time, so
/// neither the token's length nor a prefix match leaks through timing.
/// </summary>
public static class AuthGate
{
    private const string Scheme = "Bearer ";

    public static bool IsAuthorized(string? header, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return false;

        if (string.IsNullOrEmpty(header) || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        string presented = header[Scheme.Length..].Trim();
        byte[] presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        byte[] expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedToken));
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
