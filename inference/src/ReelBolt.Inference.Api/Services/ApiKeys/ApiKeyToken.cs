using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.ApiKeys;

/// <summary>The token format <c>rbk_&lt;12-char Crockford base32 id&gt;_&lt;base64url of 32 random bytes&gt;</c>.</summary>
public static partial class ApiKeyToken
{
    public const string Prefix = "rbk_";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int IdLength = 12;
    private const int SecretBytes = 32;

    /// <summary>Anything longer than this is not a token; checked before the regex runs.</summary>
    public const int MaxLength = 64;

    [GeneratedRegex("^rbk_([0-9A-HJKMNP-TV-Z]{12})_([A-Za-z0-9_-]{43})$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public sealed record Generated(string Token, string KeyId, string LastFour);

    public static Generated Generate()
    {
        StringBuilder id = new(IdLength);
        for (int i = 0; i < IdLength; i++)
            id.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);

        byte[] secretRaw = RandomNumberGenerator.GetBytes(SecretBytes);
        string secret = Base64Url.EncodeToString(secretRaw);
        string keyId = id.ToString();
        return new Generated($"{Prefix}{keyId}_{secret}", keyId, secret[^4..]);
    }

    /// <summary>Strict parse: a token that is not exactly the format yields false.</summary>
    public static bool TryParse(string? token, out string keyId)
    {
        keyId = string.Empty;
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength) return false;
        Match m = TokenPattern().Match(token);
        if (!m.Success) return false;
        keyId = m.Groups[1].Value;
        return true;
    }

    /// <summary>Lowercase hex SHA-256 of the full token.</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True when the text looks like an API key (prefix only; routing hint, not validation).</summary>
    public static bool LooksLikeKey(string? token) =>
        token is not null && token.StartsWith(Prefix, StringComparison.Ordinal);
}
