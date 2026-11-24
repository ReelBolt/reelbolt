using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>One platform's text as the model wrote it or the user edited it, before or after validation.</summary>
public sealed record PostTextDraft(string Platform, string? Title, string Body, IReadOnlyList<string> Hashtags);

/// <summary>A checked draft: the cleaned text, the text as it would be pasted, and whether anything was cut.</summary>
public sealed record PostTextChecked(PostTextDraft Draft, string Text, int Characters, int Limit, bool Trimmed);

/// <summary>
/// The code-side checks every post text passes, whoever wrote it: links and ids are stripped (the model is never trusted to
/// leave them out), hashtags are normalised and capped, and the length limits of <see cref="PostTextPlatformLimits"/> are enforced.
/// </summary>
public static class PostTextValidator
{
    public const int MaxHashtagChars = 40;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex Links = new(
        @"\b(?:https?|ftp)://\S+|\bwww\.\S+|\b[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.(?:com|net|org|io|co|app|dev|ly|me|tv|it|ai|gl|be|link|page|site|xyz)\b(?:/\S*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Ids = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b|\b(?:outputFiles|userFiles|agentFiles|projects)/\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Spaces = new(@"[ \t]{2,}", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Blank = new(@"\n{3,}", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Removes links, file keys and ids, control characters and runs of blanks. Newlines between paragraphs stay.</summary>
    public static string StripUnsafe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }

        string s = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        s = Links.Replace(s, string.Empty);
        s = Ids.Replace(s, string.Empty);
        s = Spaces.Replace(s, " ");
        s = Blank.Replace(s, "\n\n");
        return string.Join('\n', s.Split('\n').Select(l => l.TrimEnd())).Trim();
    }

    /// <summary>One hashtag as typed, without <c>#</c>, letters digits and underscore only; null when nothing is left.</summary>
    public static string? NormalizeHashtag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        foreach (char c in raw.Trim().TrimStart('#'))
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length >= MaxHashtagChars) break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static string HashtagLine(IReadOnlyList<string> hashtags) =>
        string.Join(' ', hashtags.Select(h => "#" + h));

    /// <summary>The text as it is pasted into the platform: title (when the platform has one) is separate; body, then the hashtags.</summary>
    public static string Compose(PostTextDraft draft, PostTextPlatform platform)
    {
        if (draft.Hashtags.Count == 0) return draft.Body;
        string tags = HashtagLine(draft.Hashtags);
        return draft.Body.Length == 0 ? tags : draft.Body + "\n\n" + tags;
    }

    /// <summary>Cleans a draft to the platform's rules. Never throws; an empty body stays empty (the caller refuses it).</summary>
    public static PostTextChecked Check(PostTextDraft draft, PostTextPlatform platform)
    {
        bool trimmed = false;

        string? title = null;
        if (platform.HasTitle)
        {
            title = FlattenLine(StripUnsafe(draft.Title));
            if (title.Length > platform.MaxTitleChars)
            {
                title = CutAtWord(title, platform.MaxTitleChars);
                trimmed = true;
            }

            if (title.Length == 0) title = null;
        }

        var tags = new List<string>();
        foreach (string raw in draft.Hashtags ?? [])
        {
            // A link or id typed as a tag is dropped whole, not reduced to its letters.
            if (StripUnsafe(raw).Length != (raw ?? string.Empty).Trim().Length) continue;
            if (NormalizeHashtag(raw) is { } tag && !tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
        }

        if (tags.Count > platform.MaxHashtags)
        {
            tags = tags.Take(platform.MaxHashtags).ToList();
            trimmed = true;
        }

        string body = StripUnsafe(draft.Body);

        // Hashtags typed into the same text count against its limit; they never take more than half of it.
        if (platform.HashtagsInBody)
        {
            while (tags.Count > 0 && HashtagLine(tags).Length + 2 > platform.MaxBodyChars / 2)
            {
                tags.RemoveAt(tags.Count - 1);
                trimmed = true;
            }
        }

        int tagCost = platform.HashtagsInBody && tags.Count > 0 ? HashtagLine(tags).Length + 2 : 0;
        int budget = Math.Max(0, platform.MaxBodyChars - tagCost);
        if (body.Length > budget)
        {
            body = CutAtWord(body, budget);
            trimmed = true;
        }

        var cleaned = new PostTextDraft(platform.Id, title, body, tags);
        string text = Compose(cleaned, platform);
        return new PostTextChecked(cleaned, text, text.Length, platform.MaxBodyChars, trimmed);
    }

    private static string FlattenLine(string s) => Spaces.Replace(s.Replace('\n', ' '), " ").Trim();

    private static string CutAtWord(string s, int max)
    {
        if (max <= 0) return string.Empty;
        if (s.Length <= max) return s;
        int end = max;
        // Never split a surrogate pair.
        if (end > 0 && char.IsHighSurrogate(s[end - 1])) end--;
        string head = s[..end];
        int space = head.LastIndexOfAny([' ', '\n']);
        if (space > max / 2) head = head[..space];
        return head.TrimEnd(' ', '\n', ',', ';', ':', '-');
    }
}
