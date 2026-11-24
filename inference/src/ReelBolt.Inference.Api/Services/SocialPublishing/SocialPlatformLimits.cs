using System.Globalization;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>What a platform accepts for a video post: file size, length and picture shape.</summary>
/// <param name="TextPlatformId">The id of the matching row of <see cref="PostTextPlatformLimits"/>, where the text limits live.</param>
/// <param name="MinAspect">Smallest accepted width / height.</param>
/// <param name="MaxAspect">Largest accepted width / height (1.0 = square; below 1 = vertical only).</param>
public sealed record SocialMediaLimits(
    SocialPlatformKind Platform,
    string TextPlatformId,
    string Format,
    long MaxVideoBytes,
    double MinDurationSeconds,
    double MaxDurationSeconds,
    double MinAspect,
    double MaxAspect);

/// <summary>
/// The ONE versioned table of what each publishing platform accepts. The text limits are not repeated: they come from
/// <see cref="PostTextPlatformLimits"/> (post-captions), so the caption writer and the publisher never disagree. The
/// media limits are what each platform documents at <see cref="LastChecked"/> and drift: a change is a new
/// <see cref="Version"/> and a new date. The preflight and the fire-time re-check read only this table.
/// </summary>
public static class SocialPlatformLimits
{
    /// <summary>Bump when any number below changes.</summary>
    public const int Version = 1;

    /// <summary>The date these media numbers were last compared with the platforms' own developer pages (verify before each release).</summary>
    public const string LastChecked = "2026-11-13";

    public static IReadOnlyList<SocialMediaLimits> All { get; } =
    [
        // YouTube Shorts: a normal upload, vertical or square, up to 3 minutes. Our own cap is below the API's file ceiling.
        new(SocialPlatformKind.YouTube, PostTextPlatformLimits.YouTubeShorts, "short", 8L * 1024 * 1024 * 1024, 1, 180, 0.4, 1.0),
        // LinkedIn member video: 3 seconds to 30 minutes, up to 5 GB, aspect between 1:2.4 and 2.4:1.
        new(SocialPlatformKind.LinkedIn, PostTextPlatformLimits.LinkedIn, "post", 5L * 1024 * 1024 * 1024, 3, 1800, 1 / 2.4, 2.4)
    ];

    public static SocialMediaLimits For(SocialPlatformKind kind) =>
        All.First(l => l.Platform == kind);

    public static PostTextPlatform Text(SocialPlatformKind kind) =>
        PostTextPlatformLimits.Find(For(kind).TextPlatformId)!;

    /// <summary>The text a post is sent with: the title is separate where the platform has one, hashtags are appended to the body.</summary>
    public static string ComposeBody(string? body, IReadOnlyList<string> hashtags)
    {
        string text = (body ?? string.Empty).TrimEnd();
        string tags = string.Join(' ', hashtags.Select(NormalizeHashtag).Where(t => t.Length > 1));
        if (tags.Length == 0) return text;
        return text.Length == 0 ? tags : text + "\n\n" + tags;
    }

    public static string NormalizeHashtag(string tag)
    {
        // Letters, digits and underscore only: nothing a platform's markup could read as a mention, link or escape.
        return PostTextValidator.NormalizeHashtag(tag) is { } t ? "#" + t : string.Empty;
    }
}

public sealed record SocialPreflightInput(
    SocialPlatformKind Platform,
    string? Title,
    string? Body,
    IReadOnlyList<string> Hashtags,
    long? SizeBytes,
    double? DurationSeconds,
    int? Width,
    int? Height,
    string? Privacy = null);

/// <param name="Severity"><c>error</c> blocks publishing; <c>warning</c> says a fact could not be checked.</param>
/// <param name="RerenderVertical">True when re-rendering the video as 9:16 would fix this.</param>
public sealed record SocialPreflightIssue(string Code, string Severity, string Field, string Message, bool RerenderVertical = false);

public sealed record SocialPreflightResult(int LimitsVersion, string LimitsLastChecked, IReadOnlyList<SocialPreflightIssue> Issues)
{
    public bool Ok => Issues.All(i => i.Severity != SocialPreflightSeverity.Error);
}

public static class SocialPreflightSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>Checks a post against <see cref="SocialPlatformLimits"/> before anything is uploaded: text length, file size, picture shape, length.</summary>
public static class SocialPreflight
{
    public static SocialPreflightResult Check(SocialPreflightInput input)
    {
        SocialMediaLimits media = SocialPlatformLimits.For(input.Platform);
        PostTextPlatform text = SocialPlatformLimits.Text(input.Platform);
        string name = SocialPlatforms.DisplayName(input.Platform);
        List<SocialPreflightIssue> issues = [];

        string title = input.Title?.Trim() ?? string.Empty;
        if (text.HasTitle)
        {
            if (title.Length == 0)
                issues.Add(new("title_required", SocialPreflightSeverity.Error, "title", $"{name} needs a title."));
            else if (title.Length > text.MaxTitleChars)
                issues.Add(new("title_too_long", SocialPreflightSeverity.Error, "title", $"The title is {title.Length} characters; {name} allows {text.MaxTitleChars}."));
        }

        if (input.Platform == SocialPlatformKind.LinkedIn && input.Privacy is "unlisted" or "private")
            issues.Add(new("privacy_unsupported", SocialPreflightSeverity.Error, "options",
                $"LinkedIn has no \"{input.Privacy}\" setting. Choose public or connections."));

        string body = SocialPlatformLimits.ComposeBody(input.Body, input.Hashtags);
        if (body.Length > text.MaxBodyChars)
            issues.Add(new("body_too_long", SocialPreflightSeverity.Error, "body", $"The text is {body.Length} characters; {name} allows {text.MaxBodyChars}."));
        if (!text.HasTitle && body.Length == 0)
            issues.Add(new("body_required", SocialPreflightSeverity.Error, "body", $"{name} needs some text."));
        if (input.Hashtags.Count > text.MaxHashtags)
            issues.Add(new("too_many_hashtags", SocialPreflightSeverity.Error, "hashtags", $"There are {input.Hashtags.Count} hashtags; {name} keeps {text.MaxHashtags}."));

        if (input.SizeBytes is not { } size)
            issues.Add(new("size_unknown", SocialPreflightSeverity.Warning, "video", "The file size could not be checked."));
        else if (size <= 0)
            issues.Add(new("file_empty", SocialPreflightSeverity.Error, "video", "The video file is empty."));
        else if (size > media.MaxVideoBytes)
            issues.Add(new("file_too_large", SocialPreflightSeverity.Error, "video",
                $"The video is {Mb(size)} MB; {name} takes up to {Mb(media.MaxVideoBytes)} MB."));

        if (input.DurationSeconds is not { } seconds)
            issues.Add(new("duration_unknown", SocialPreflightSeverity.Warning, "video", "The video length could not be checked."));
        else if (seconds > media.MaxDurationSeconds)
            issues.Add(new("video_too_long", SocialPreflightSeverity.Error, "video",
                $"The video is {Seconds(seconds)} s; {name} takes up to {Seconds(media.MaxDurationSeconds)} s."));
        else if (seconds < media.MinDurationSeconds)
            issues.Add(new("video_too_short", SocialPreflightSeverity.Error, "video",
                $"The video is {Seconds(seconds)} s; {name} needs at least {Seconds(media.MinDurationSeconds)} s."));

        if (input.Width is not { } w || input.Height is not { } h || w <= 0 || h <= 0)
            issues.Add(new("aspect_unknown", SocialPreflightSeverity.Warning, "video", "The picture shape could not be checked."));
        else
        {
            double aspect = (double)w / h;
            if (aspect > media.MaxAspect + 0.005 || aspect < media.MinAspect - 0.005)
                issues.Add(new("aspect_out_of_range", SocialPreflightSeverity.Error, "video",
                    $"The picture is {w}x{h}; {name} needs {AspectRange(media)}. Render it again as 9:16.", RerenderVertical: true));
        }

        return new SocialPreflightResult(SocialPlatformLimits.Version, SocialPlatformLimits.LastChecked, issues);
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Seconds(double s) => s.ToString("0.#", CultureInfo.InvariantCulture);

    private static string AspectRange(SocialMediaLimits m) =>
        m.MaxAspect <= 1.0 ? "a vertical or square picture" : "a picture between 1:2.4 and 2.4:1";
}
