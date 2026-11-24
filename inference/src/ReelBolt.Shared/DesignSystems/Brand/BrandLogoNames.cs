using System.Text.RegularExpressions;

namespace ReelBolt.Shared.DesignSystems.Brand;

/// <summary>Pulls company names out of design-system prose using generic patterns only.</summary>
public static class BrandLogoNames
{
    private const int MaxNames = 3;
    private const int MaxLength = 60;
    private const string Suffix = @"(?:Srl|S\.r\.l\.?|Inc\.?|Ltd\.?|GmbH|LLC|AG|SA|Corp\.?|Group)";
    private const string ForeignSuffix = @"(?:Srl|S\.r\.l\.?|SpA|S\.p\.A\.?|GmbH|Inc\.?|Ltd\.?|LLC|SA|AG|Corp\.?|Group)";
    private const string Word = @"[A-Z][\w&'\-]*";
    private const string NameBody = Word + @"(?:[ \t]+" + Word + @"){0,3}";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex[] Patterns =
    [
        new(@"(?:published[ \t]+by|\b[Bb]y|\b[Ff]rom)[ \t]+(" + NameBody + @")[ \t]+(" + Suffix + @")(?![\w])", RegexOptions.CultureInvariant, Timeout),
        new(@"(?:published[ \t]+by)[ \t]+(" + NameBody + @")", RegexOptions.CultureInvariant, Timeout),
        new(@"(?:[Pp]rodotto[ \t]+(?:da|di)|[Ss]viluppato[ \t]+da|[Uu]n[ \t]+prodotto[ \t]+di|[Rr]ealizzato[ \t]+da|[Pp]roducto[ \t]+de|[Dd]esarrollado[ \t]+por|[Pp]roduit[ \t]+par|[Dd][ée]velopp[ée][ \t]+par)[ \t]+(" + NameBody + @")[ \t]+(" + ForeignSuffix + @")(?![\w])", RegexOptions.CultureInvariant, Timeout),
        new(@"©[ \t]*(?:\d{4}[ \t,\-]*)?(" + NameBody + @")", RegexOptions.CultureInvariant, Timeout),
    ];

    private static readonly Regex TrailingSuffix = new(
        @"[ \t]+(?:Srl|S\.r\.l\.?|SpA|S\.p\.A\.?|Inc\.?|Ltd\.?|GmbH|LLC|AG|SA|Corp\.?)$", RegexOptions.CultureInvariant, Timeout);

    /// <summary>
    /// The product slug (lower-case letters and digits) of a design-system name such as
    /// <c>acme-hr-design</c> or <c>Acme-HR Design System</c>: the generic words design, system and brand
    /// are dropped and the rest is joined. Empty when nothing is left.
    /// </summary>
    public static string ProductSlugFrom(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (string token in name.Split([' ', '\t', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("design", StringComparison.OrdinalIgnoreCase)
                || token.Equals("system", StringComparison.OrdinalIgnoreCase)
                || token.Equals("brand", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (char c in token.ToLowerInvariant())
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<string> PublisherNamesFrom(string? prose)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(prose)) return result;

        try
        {
            string text = prose.Length > 20_000 ? prose[..20_000] : prose;
            foreach (Regex pattern in Patterns)
            {
                foreach (Match m in pattern.Matches(text))
                {
                    string name = m.Groups[1].Value;
                    if (m.Groups.Count > 2 && m.Groups[2].Value.Equals("Group", StringComparison.Ordinal))
                        name += " Group";

                    name = name.Trim().TrimEnd('.', ',', ';');
                    name = TrailingSuffix.Replace(name, string.Empty).Trim();
                    string? clean = DesignText.Sanitize(name, MaxLength);
                    if (clean is null || clean.Length < 2) continue;
                    if (result.Contains(clean, StringComparer.OrdinalIgnoreCase)) continue;
                    result.Add(clean);
                    if (result.Count >= MaxNames) return result;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return result;
    }
}
