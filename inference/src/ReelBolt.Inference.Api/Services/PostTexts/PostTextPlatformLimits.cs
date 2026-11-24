namespace ReelBolt.Inference.Api.Services.PostTexts;

/// <summary>What one platform's post looks like: the text fields it has and the hard limits we enforce in code.</summary>
/// <param name="Id">The stable id used on the wire and in the table (<c>tiktok</c>, <c>instagram_reels</c>, ...).</param>
/// <param name="Label">The platform's name for people.</param>
/// <param name="HasTitle">True when the platform has a title field apart from the text (YouTube Shorts).</param>
/// <param name="MaxTitleChars">The title limit; 0 when there is no title.</param>
/// <param name="MaxBodyChars">The limit of the caption or description. When <paramref name="HashtagsInBody"/> it covers the hashtags too.</param>
/// <param name="MaxHashtags">The most hashtags a post keeps.</param>
/// <param name="SuggestedHashtags">The count the writer is asked for (not a claim about reach).</param>
/// <param name="HashtagsInBody">True when hashtags are typed into the same text and count against its limit.</param>
public sealed record PostTextPlatform(
    string Id,
    string Label,
    bool HasTitle,
    int MaxTitleChars,
    int MaxBodyChars,
    int MaxHashtags,
    int SuggestedHashtags,
    bool HashtagsInBody);

/// <summary>
/// The versioned table of platform limits, one place on purpose so the publishing and quality-rating work can reuse it.
/// The numbers are what each platform documents for its post text at <see cref="LastChecked"/>; they drift, so a change
/// is a new <see cref="Version"/> and a new date, and the writer never claims anything about reach or timing.
/// </summary>
public static class PostTextPlatformLimits
{
    /// <summary>Bump when any number below changes.</summary>
    public const int Version = 1;

    /// <summary>The date these numbers were last compared with the platforms' own help pages.</summary>
    public const string LastChecked = "2026-11-10";

    public const string TikTok = "tiktok";
    public const string InstagramReels = "instagram_reels";
    public const string YouTubeShorts = "youtube_shorts";
    public const string LinkedIn = "linkedin";
    public const string X = "x";

    public static IReadOnlyList<PostTextPlatform> All { get; } =
    [
        new(TikTok, "TikTok", false, 0, 2200, 5, 4, true),
        new(InstagramReels, "Instagram Reels", false, 0, 2200, 30, 5, true),
        new(YouTubeShorts, "YouTube Shorts", true, 100, 5000, 15, 3, true),
        new(LinkedIn, "LinkedIn", false, 0, 3000, 5, 3, true),
        new(X, "X", false, 0, 280, 2, 1, true)
    ];

    /// <summary>The platform with this id (case-insensitive), or null.</summary>
    public static PostTextPlatform? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string key = id.Trim();
        foreach (PostTextPlatform p in All)
        {
            if (string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase)) return p;
        }

        return null;
    }
}
