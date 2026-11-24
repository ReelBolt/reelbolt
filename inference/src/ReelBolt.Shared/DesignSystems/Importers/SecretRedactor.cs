using System.Text.RegularExpressions;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Replaces recognizable credentials with <c>[redacted]</c> (SPEC §9 rule 9). Every pattern is a
/// linear-time <see cref="RegexOptions.NonBacktracking"/> expression with no lookaround or
/// backreference, so a hostile input can never make matching take super-linear time and the method
/// never throws.
/// </summary>
public static class SecretRedactor
{
    private const RegexOptions Options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    private static readonly Regex OpenAiKey = new(@"sk-[A-Za-z0-9_-]{20,}", Options);

    // ghp_ (personal), gho_ (OAuth), ghu_ (user-to-server), ghs_ (server-to-server), ghr_ (refresh).
    private static readonly Regex GitHubToken = new(@"gh[pousr]_[A-Za-z0-9]{30,}", Options);

    private static readonly Regex GitHubFineGrainedPat = new(@"github_pat_[A-Za-z0-9_]{50,}", Options);

    // AKIA (long-lived) and ASIA (STS temporary) access key ids.
    private static readonly Regex AwsAccessKeyId = new(@"(?:AKIA|ASIA)[0-9A-Z]{16}", Options);

    // A 40-character AWS-style secret, only when it follows a "secret"/"aws" context word and a separator.
    private static readonly Regex AwsSecretAccessKey = new(
        @"(?:secret|aws)[A-Za-z0-9_ .-]{0,40}[""']?\s*[:=]\s*[""']?[A-Za-z0-9/+=]{40,}",
        Options | RegexOptions.IgnoreCase);

    // The whole PEM block through its END line; the header-only form below catches truncated blocks.
    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", Options);

    private static readonly Regex PrivateKeyHeader = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", Options);

    private static readonly Regex SlackToken = new(@"xox[baprs]-[A-Za-z0-9-]{10,}", Options);

    private static readonly Regex JsonWebToken = new(
        @"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}(?:\.[A-Za-z0-9_-]*)?", Options);

    private static readonly Regex BearerToken = new(
        @"Bearer\s+[A-Za-z0-9._~+/=-]{20,}", Options | RegexOptions.IgnoreCase);

    // scheme://user:password@host - the credentials and the scheme go; the host is not a secret.
    private static readonly Regex UrlCredentials = new(
        @"[A-Za-z][A-Za-z0-9+.-]*://[^\s/:@]+:[^\s/@]+@", Options);

    private static readonly Regex StripeKey = new(@"[sr]k_(?:live|test)_[A-Za-z0-9]{10,}", Options);

    private static readonly Regex GoogleApiKey = new(@"AIza[0-9A-Za-z_-]{35}", Options);

    private static readonly Regex FigmaToken = new(@"figd_[A-Za-z0-9_-]{20,}", Options);

    private static readonly Regex SendGridKey = new(@"SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}", Options);

    private static readonly Regex[] Patterns =
    [
        PrivateKeyBlock,
        PrivateKeyHeader,
        BearerToken,
        JsonWebToken,
        UrlCredentials,
        OpenAiKey,
        StripeKey,
        GitHubToken,
        GitHubFineGrainedPat,
        AwsAccessKeyId,
        AwsSecretAccessKey,
        GoogleApiKey,
        FigmaToken,
        SendGridKey,
        SlackToken,
    ];

    /// <summary>
    /// The input with every secret replaced by <c>[redacted]</c>, and the total number of matches
    /// replaced. A null or empty input answers <c>("", 0)</c>.
    /// </summary>
    public static (string text, int count) Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return (string.Empty, 0);

        int count = 0;
        string result = input;

        foreach (Regex pattern in Patterns)
        {
            result = pattern.Replace(result, _ =>
            {
                count++;
                return "[redacted]";
            });
        }

        return (result, count);
    }
}
