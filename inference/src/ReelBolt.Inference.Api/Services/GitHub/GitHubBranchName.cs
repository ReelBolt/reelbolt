namespace ReelBolt.Inference.Api.Services.GitHub;

/// <summary>
/// The one check every branch name from a request goes through before it is used — in a GitHub REST
/// path (<c>/git/ref/heads/{branch}</c>), stored on a link, or compared against a push. It follows
/// <c>git check-ref-format</c>, so a name git itself would refuse is refused here, and with it every
/// way of steering the REST path (<c>..</c>, <c>.</c>, an empty segment, a backslash, a percent-escape
/// that would decode to one).
/// </summary>
public static class GitHubBranchName
{
    /// <summary>Longest accepted branch name. Git allows more; nothing real needs it.</summary>
    public const int MaxLength = 255;

    /// <summary>True when <paramref name="name"/> is a branch name git would accept.</summary>
    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        if (name == "@") return false;
        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/') || name.EndsWith('.')) return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal)) return false;

        foreach (char c in name)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) return false;
            // '%' is not refused by git, but no real branch needs it and it is the one character that
            // could smuggle an encoded '/' or '.' past the segment encoder on its way to GitHub.
            if (c is '\\' or '~' or '^' or ':' or '?' or '*' or '[' or '%') return false;
        }

        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0) return false;               // "a//b"
            if (segment.StartsWith('.')) return false;           // ".", "..", ".hidden"
            if (segment.EndsWith(".lock", StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
