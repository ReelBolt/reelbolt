using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>
/// Plain-text sanitiser for everything an author writes into a public recipe (name, description, prompts, labels): control and
/// bidi/zero-width characters, markup, links and runs of whitespace go. Style of <c>AppManifestValidator</c> and
/// <c>OverlayTextSanitizer</c>. Never throws.
/// </summary>
public static partial class MarketplaceText
{
    /// <summary>The longest any one text field may be after sanitising.</summary>
    public const int MaxFieldChars = 4000;

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Markup();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"(?i)(\b[a-z][a-z0-9+.-]{1,15}://\S*|\bwww\.\S+|\b(?:mailto|javascript|data|vbscript):\S*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Link();

    [GeneratedRegex(@"[^\S\n]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineSpace();

    [GeneratedRegex(@" ?\n ?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NewlineSpace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlankRuns();

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            string s = Markup().Replace(text, " ");
            s = MarkdownLink().Replace(s, "$1");
            s = Link().Replace(s, " ");
            StringBuilder sb = new(s.Length);
            foreach (Rune r in s.EnumerateRunes())
            {
                if (r.Value is 0x0A or 0x2028 or 0x2029 or 0x85)
                    sb.Append('\n');
                else if (r.Value == '\r')
                    continue;
                else if (r.Value == '\t' || Rune.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator)
                    sb.Append(' ');
                else if (!IsDropped(r) && r.Value is not ('`' or '<' or '>'))
                    sb.Append(r.ToString());
            }

            string t = InlineSpace().Replace(sb.ToString(), " ");
            t = NewlineSpace().Replace(t, "\n");
            t = BlankRuns().Replace(t, "\n\n");
            return t.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    /// <summary>Control (other than newline and tab), format (bidi, zero-width, tag), private-use, unassigned and variation-selector characters.</summary>
    private static bool IsDropped(Rune r)
    {
        UnicodeCategory category = Rune.GetUnicodeCategory(r);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator)
            return true;
        return r.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or (>= 0x180B and <= 0x180F);
    }
}
