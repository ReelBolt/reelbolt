using ReelBolt.Inference.Api.Services.SocialPublishing;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>Per-platform options of a post. A null value means the platform's default.</summary>
/// <param name="Privacy"><c>public</c>, <c>unlisted</c>, <c>private</c> (YouTube) or <c>connections</c> (LinkedIn).</param>
/// <param name="AiGenerated">Ask the platform to label the video as made with AI.</param>
public sealed record SocialPostOptionsDto(string? Privacy = null, bool? AiGenerated = null, bool? CommentsEnabled = null);

/// <summary>
/// Create a post. Exactly one of <see cref="PublishNow"/>, <see cref="ScheduledAt"/> (an instant with offset) or
/// <see cref="ScheduledLocal"/> (a wall-clock time in <see cref="TimeZone"/>, an IANA id) says when. Text may come from
/// <see cref="PostTextId"/> (a saved post text of this output and platform); explicit fields win. It is COPIED: later edits of
/// the saved text never change the post.
/// </summary>
public sealed record CreateSocialPostRequest(
    Guid ProjectId,
    string OutputKind,
    Guid OutputId,
    Guid AccountId,
    string? Title = null,
    string? Body = null,
    List<string>? Hashtags = null,
    Guid? PostTextId = null,
    SocialPostOptionsDto? Options = null,
    bool PublishNow = false,
    DateTimeOffset? ScheduledAt = null,
    DateTime? ScheduledLocal = null,
    string? TimeZone = null);

/// <summary>Change a scheduled post. A field left out stays as it was. Allowed only while the post is still <c>scheduled</c>.</summary>
public sealed record UpdateSocialPostRequest(
    string? Title = null,
    string? Body = null,
    List<string>? Hashtags = null,
    SocialPostOptionsDto? Options = null,
    DateTimeOffset? ScheduledAt = null,
    DateTime? ScheduledLocal = null,
    string? TimeZone = null);

/// <summary>Retry a failed post, or an <c>unknown</c> one once the person has checked the channel and confirms it is not there.</summary>
public sealed record RetrySocialPostRequest(bool ConfirmNotPosted = false);

public sealed record PreflightSocialPostRequest(
    Guid ProjectId,
    string OutputKind,
    Guid OutputId,
    Guid AccountId,
    string? Title = null,
    string? Body = null,
    List<string>? Hashtags = null,
    SocialPostOptionsDto? Options = null);

public sealed record SocialPreflightResponse(
    string Platform,
    int LimitsVersion,
    string LimitsLastChecked,
    bool Ok,
    IReadOnlyList<SocialPreflightIssue> Issues);

public sealed record SocialPostResponse(
    Guid Id,
    Guid ProjectId,
    string OutputKind,
    Guid OutputId,
    Guid AccountId,
    string Platform,
    string Format,
    string? Title,
    string? Body,
    IReadOnlyList<string> Hashtags,
    Guid? PostTextId,
    SocialPostOptionsDto Options,
    DateTime? ScheduledAt,
    string? ScheduledLocal,
    string? TimeZone,
    string Status,
    int Attempts,
    DateTime? NextAttemptAt,
    string? RemotePostUrl,
    string? FailureCode,
    string? FailureMessage,
    Guid CreatedByUserId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? PublishedAt,
    bool CanEdit,
    bool CanCancel,
    bool CanRetry);

public sealed record SocialPostListResponse(IReadOnlyList<SocialPostResponse> Items, int Total);
