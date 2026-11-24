using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>
/// The one place a guest review link's token is generated, hashed and shape-checked
/// (<c>docs/design/guest-review-links.md</c>, decision 2).
/// </summary>
/// <remarks>
/// <para>
/// A token is 32 bytes from <see cref="RandomNumberGenerator"/>, base64url without padding: exactly
/// <see cref="TokenLength"/> (43) characters of <c>[A-Za-z0-9_-]</c>. The database keeps only
/// <see cref="Hash"/> (SHA-256 of the ASCII token text), and a link is found by an equality lookup on
/// that hash through its unique index: application code never compares a token or a hash itself, so
/// there is no comparison to time.
/// </para>
/// <para>
/// <see cref="IsWellFormed"/> runs BEFORE the database is touched, so a probe with the wrong length or
/// alphabet costs nothing and answers the same uniform 404 as any other failure.
/// </para>
/// </remarks>
public static class ReviewLinkTokens
{
    public const int TokenBytes = 32;

    /// <summary>Base64url of 32 bytes without padding.</summary>
    public const int TokenLength = 43;

    public const int HashBytes = 32;

    public static string Generate()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>SHA-256 of the token's ASCII text (32 bytes). Only call it with a well-formed token.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>True only for exactly 43 characters of <c>A-Z a-z 0-9 _ -</c>.</summary>
    public static bool IsWellFormed(string? token)
    {
        if (token is null || token.Length != TokenLength) return false;
        foreach (char c in token)
        {
            bool ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
            if (!ok) return false;
        }

        return true;
    }
}
