using System.Text;

namespace ReelBolt.Shared.DesignSystems.Brand;

/// <summary>
/// Classifies a logo file by name: whose logo it is (<see cref="Subject"/>), its shape
/// (<see cref="Form"/>) and a weak tone hint from a light/dark token. Never throws.
/// </summary>
public static class BrandLogoClassifier
{
    public const string SubjectProduct = "product";
    public const string SubjectPublisher = "publisher";
    public const string SubjectUnknown = "unknown";

    public const string FormMark = "mark";
    public const string FormWordmark = "wordmark";
    public const string FormLockup = "lockup";

    private static readonly HashSet<string> PublisherTokens = new(StringComparer.Ordinal)
    {
        "publisher", "company", "corporate", "corp", "parent", "vendor", "studio", "agency",
    };

    private static readonly HashSet<string> WordmarkTokens = new(StringComparer.Ordinal) { "wordmark", "wordtype", "lettering" };
    private static readonly HashSet<string> LockupTokens = new(StringComparer.Ordinal) { "lockup", "horizontal", "stacked", "full" };

    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "logo", "icon", "mark", "symbol", "app", "brand", "wordmark", "light", "dark", "vector", "mono", "small", "large",
        "rounded", "maskable", "apple", "touch", "favicon", "default", "primary", "x",
        "wordtype", "lettering", "lockup", "horizontal", "stacked", "full",
    };

    /// <summary>
    /// <c>NameToneHint</c> comes from a light/dark token. It is ambiguous (light-coloured ink or for a light
    /// background) and only a fallback when nothing can be measured.
    /// </summary>
    public static (string Subject, string Form, string? NameToneHint) Classify(
        string? fileName, string? productSlug, IReadOnlyList<string>? publisherNames)
    {
        try
        {
            string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            List<string> tokens = Tokenize(stem);
            string letters = LettersOnly(stem);

            string form = tokens.Any(WordmarkTokens.Contains) ? FormWordmark
                : tokens.Any(LockupTokens.Contains) ? FormLockup
                : FormMark;

            string? hint = tokens.Contains("light") ? "light" : tokens.Contains("dark") ? "dark" : null;
            if (tokens.Contains("light") && tokens.Contains("dark")) hint = null;

            string slug = LettersOnly(productSlug);
            bool named = slug.Length >= 2 && letters.Contains(slug, StringComparison.Ordinal);
            if (named) return (SubjectProduct, form, hint);

            bool publisher = tokens.Any(PublisherTokens.Contains);
            if (!publisher && publisherNames is not null)
            {
                foreach (string name in publisherNames)
                {
                    string n = LettersOnly(name);
                    if (n.Length < 2) continue;
                    // A company that shares the product's name: its logo is the product's, never "publisher".
                    if (slug.Length >= 2 && (n.Contains(slug, StringComparison.Ordinal) || slug.Contains(n, StringComparison.Ordinal)))
                        continue;
                    if (letters.Contains(n, StringComparison.Ordinal)) { publisher = true; break; }
                }
            }

            if (publisher) return (SubjectPublisher, form, hint);

            bool product = tokens.All(t => t.Length < 2 || GenericTokens.Contains(t));
            return (product ? SubjectProduct : SubjectUnknown, form, hint);
        }
        catch (Exception)
        {
            return (SubjectUnknown, FormMark, null);
        }
    }

    private static readonly HashSet<string> LogoLikeTokens = new(StringComparer.Ordinal)
    {
        "logo", "icon", "mark", "brand", "wordmark", "favicon",
    };

    /// <summary>True when a file name's tokens say it is a logo-like image (logo/icon/mark/brand/wordmark/favicon).</summary>
    public static bool IsLogoLikeName(string? fileName)
    {
        try
        {
            return Tokenize(Path.GetFileNameWithoutExtension(fileName ?? string.Empty)).Any(LogoLikeTokens.Contains);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string LettersOnly(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Lower-cased letter runs, split on non-letters and at lower-to-upper camelCase boundaries.</summary>
    private static List<string> Tokenize(string stem)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (char c in stem)
        {
            if (!char.IsLetter(c))
            {
                Flush();
            }
            else
            {
                if (char.IsUpper(c) && char.IsLower(prev)) Flush();
                sb.Append(char.ToLowerInvariant(c));
            }

            prev = c;
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (sb.Length > 0) tokens.Add(sb.ToString());
            sb.Clear();
        }
    }
}
