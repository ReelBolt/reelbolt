namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>One reading of a post's numbers. A null count is "not available" on that platform, never 0.</summary>
public sealed record SocialMetricReadingResponse(DateTime CapturedAt, long? Views, long? Likes, long? Comments, long? Shares, long? Saves, string Source);

/// <summary>A post's readings, oldest first, and when the next one is due (null once the 30 days are over).</summary>
public sealed record SocialPostMetricsResponse(
    Guid PostId,
    string Platform,
    string Status,
    IReadOnlyList<SocialMetricReadingResponse> Readings,
    SocialMetricReadingResponse? Latest,
    DateTime? NextReadingAt);

/// <summary>Sums of the newest reading of every post. A total is null when no post's platform provides that number.</summary>
public sealed record SocialMetricTotalsResponse(long? Views, long? Likes, long? Comments, long? Shares, long? Saves);

public sealed record SocialPlatformMetricsResponse(string Platform, int Posts, SocialMetricTotalsResponse Totals);

public sealed record SocialPostMetricsItemResponse(
    Guid PostId,
    Guid ProjectId,
    string Platform,
    string? Title,
    DateTime? PublishedAt,
    string? RemotePostUrl,
    SocialMetricReadingResponse? Latest);

/// <summary>The active organization's performance over the last <see cref="Days"/> days of posting.</summary>
public sealed record SocialMetricsSummaryResponse(
    int Days,
    int PublishedPosts,
    SocialMetricTotalsResponse Totals,
    IReadOnlyList<SocialPlatformMetricsResponse> Platforms,
    IReadOnlyList<SocialPostMetricsItemResponse> Posts);
