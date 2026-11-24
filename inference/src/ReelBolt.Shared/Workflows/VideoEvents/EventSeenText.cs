using System.Text;

namespace ReelBolt.Shared.Workflows.VideoEvents;

/// <summary>
/// The one gate a vision model's words about an event pass before an agent reads them (<c>seen</c> on an event).
/// The words describe a customer's video, so they are untrusted: what comes out is at most
/// <see cref="MaxWords"/> lower-case words of letters (hyphens and apostrophes inside a word), nothing else. No
/// digit survives, so the text can never carry a time, a frame, an offset or a level. An answer that addresses its
/// reader or names the machinery ("ignore", "instructions", "you", ...) is refused whole, not trimmed. Pure.
/// </summary>
public static class EventSeenText
{
    /// <summary>Most words kept.</summary>
    public const int MaxWords = 12;

    /// <summary>Most characters kept (cut at a word boundary).</summary>
    public const int MaxChars = 80;

    /// <summary>Most characters of the raw answer that are read at all.</summary>
    public const int MaxRawChars = 400;

    private const int MaxWordChars = 24;
    private const int MinLetters = 3;

    /// <summary>
    /// Words a description of a picture has no reason to hold; one of them refuses the whole answer. Common nouns a
    /// video often shows ("system" as in a solar system, "tool") are not here: refusing them lost honest answers. The
    /// list is a best-effort filter, not a boundary: every reader of the words is also told they are untrusted.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.Ordinal)
    {
        "ignore", "ignored", "ignoring", "disregard", "forget", "instruction", "instructions", "prompt", "prompts",
        "assistant", "override", "overrides", "you", "your", "yours", "must", "json"
    };

    /// <summary>Whole answers that mean "I cannot tell".</summary>
    private static readonly HashSet<string> Empty = new(StringComparer.Ordinal)
    {
        "unclear", "unknown", "nothing", "none", "n a", "not sure", "not clear", "cannot tell", "can't tell", "no change"
    };

    /// <summary>The cleaned description, or null when nothing usable is left.</summary>
    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string text = raw.Length > MaxRawChars ? raw[..MaxRawChars] : raw;
        try
        {
            text = text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Invalid Unicode (a lone surrogate): read it as it is; the letter filter below drops what is not a letter.
        }

        StringBuilder cleaned = new(text.Length);
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // A token with a digit in it ("12.4s", "3x", "frame90") goes whole: no part of a number survives.
            if (token.Any(char.IsDigit))
                continue;

            foreach (char c in token)
            {
                if (char.IsLetter(c))
                    cleaned.Append(char.ToLowerInvariant(c));
                else if (c is '-' or '\'' or '\u2019')
                    cleaned.Append(c == '\u2019' ? '\'' : c);
                else
                    cleaned.Append(' ');
            }

            cleaned.Append(' ');
        }

        List<string> words = [];
        int letters = 0;
        foreach (string token in cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = token.Trim('-', '\'');
            if (word.Length == 0 || word.Length > MaxWordChars)
                continue;
            if (Refused.Contains(word))
                return null;
            if (words.Count == MaxWords)
                break;
            words.Add(word);
            letters += word.Count(char.IsLetter);
        }

        if (letters < MinLetters)
            return null;

        StringBuilder result = new();
        foreach (string word in words)
        {
            if (result.Length + (result.Length > 0 ? 1 : 0) + word.Length > MaxChars)
                break;
            if (result.Length > 0)
                result.Append(' ');
            result.Append(word);
        }

        string seen = result.ToString();
        return seen.Length == 0 || Empty.Contains(seen) ? null : seen;
    }
}
