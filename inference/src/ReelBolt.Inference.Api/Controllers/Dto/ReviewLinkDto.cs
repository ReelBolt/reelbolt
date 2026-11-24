using System.Text.Json.Serialization;

namespace ReelBolt.Inference.Api.Controllers.Dto;

// Guest review links (Wave 4). Contract: docs/design/guest-review-links.md, docs/editor.md "Guest review links (Wave 4)".

/// <summary>Owner: <c>POST …/review-links</c> body. Every field optional.</summary>
public sealed record CreateReviewLinkRequest(string? Label, Guid? RenderExecutionId, int? ExpiresInDays, bool? AllowComments);

/// <summary>Owner: the <c>201</c> body. <c>Url</c> appears here and nowhere else, ever.</summary>
public sealed record CreateReviewLinkResponse(Guid Id, string Url, string? Label, DateTime ExpiresAt, DateTime RenderCreatedAt, bool Stale);

public sealed record ReviewLinkDecisionResponse(string Decision, string GuestName, string? Note, DateTime At);

/// <summary>Owner: one row of the list. Never carries the token, its hash or the URL.</summary>
public sealed record ReviewLinkSummaryResponse(
    Guid Id,
    string? Label,
    string Status,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    int ViewCount,
    DateTime? LastViewedAt,
    bool Stale,
    ReviewLinkDecisionResponse? Decision,
    int GuestComments,
    bool UnreadGuestActivity);

public sealed record ReviewLinkListResponse(IReadOnlyList<ReviewLinkSummaryResponse> Links);

/// <summary>Owner: <c>GET …/review-links/{linkId}</c>: the summary plus the decision history (newest first, at most 50).</summary>
public sealed record ReviewLinkDetailResponse(
    Guid Id,
    string? Label,
    string Status,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    int ViewCount,
    DateTime? LastViewedAt,
    bool Stale,
    bool AllowComments,
    DateTime RenderCreatedAt,
    ReviewLinkDecisionResponse? Decision,
    IReadOnlyList<ReviewLinkDecisionResponse> Decisions,
    int GuestComments,
    bool UnreadGuestActivity);

/// <summary>Public: one comment. No user ids, no emails (members appear by display name).</summary>
public sealed record PublicReviewCommentResponse(
    Guid Id,
    Guid? ParentId,
    string AuthorName,
    bool Guest,
    string Body,
    long Frame,
    long? DurationFrames,
    bool Resolved,
    DateTime CreatedAt);

public sealed record PublicReviewDecisionResponse(string Decision, string GuestName, DateTime At);

/// <summary>Public: <c>GET /api/v1/review-links/{token}</c>.</summary>
public sealed record PublicReviewResponse(
    string Title,
    DateTime ExpiresAt,
    bool AllowComments,
    DateTime RenderCreatedAt,
    bool Stale,
    double? DurationSec,
    IReadOnlyList<PublicReviewCommentResponse> Comments,
    PublicReviewDecisionResponse? Decision,
    PublicReviewRate? Rate = null);

/// <summary>The timeline's frame rate (frames per second = Num / Den), so guest note frames line up with the editor's.</summary>
public sealed record PublicReviewRate(int Num, int Den);

/// <summary>Public: <c>POST …/comments</c> body.</summary>
public sealed record PublicCreateReviewCommentRequest(string? Name, string? Body, long Frame, long? DurationFrames, Guid? ParentId);

/// <summary>Public: <c>POST …/decision</c> body. <c>Decision</c> is <c>approved</c> or <c>changesRequested</c>.</summary>
public sealed record PublicReviewDecisionRequest(string? Decision, string? Name, string? Note);

/// <summary>The plain body of every public error that is not the uniform empty 404.</summary>
public sealed record ReviewLinkMessage([property: JsonPropertyName("message")] string Message, [property: JsonPropertyName("code")] string? Code = null);
