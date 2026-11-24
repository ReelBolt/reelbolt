using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>
/// Coalesces guest activity into owner notifications: at most one comment notification per link per 10 minutes
/// (a count of the notes since the last one, naming the latest guest), and a decision always notifies unless the
/// link notified in the last minute. Writes <c>last_notified_at</c> on the link; sends through
/// <see cref="IUserActivityNotifier"/> to the link's creator only. Entirely best-effort.
/// </summary>
/// <remarks>
/// Coalescing state lives in the database, so it holds across replicas up to the race of two requests landing in
/// the same instant (worst case one extra mail). The details line carries only the guest's normalised name and
/// counts or the decision word, never note text.
/// </remarks>
public sealed class ReviewLinkActivityNotifier(
    InferenceApiDbContext db,
    IUserActivityNotifier notifier,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task NotifyCommentAsync(EditReviewLink link, string timelineName, string latestGuestName, CancellationToken ct)
    {
        try
        {
            DateTime now = _clock.GetUtcNow().UtcDateTime;
            if (link.LastNotifiedAt is { } last && now - last < ReviewLinkOptions.NotifyCommentInterval) return;

            DateTime since = link.LastNotifiedAt ?? link.CreatedAt;
            int count = await db.EditTimelineComments.AsNoTracking()
                .CountAsync(c => c.GuestLinkId == link.Id && c.CreatedAt > since, ct);
            if (count < 1) count = 1;

            string name = SafeDisplayName(latestGuestName);
            string details = count == 1
                ? $"1 new note from {name} (guest)"
                : $"{count} new notes, latest from {name} (guest)";
            await SendAsync(link, timelineName, details, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never reaches the guest; logs nothing about the link or the text.
            _ = ex;
        }
    }

    public async Task NotifyDecisionAsync(
        EditReviewLink link, string timelineName, EditReviewDecisionKind decision, string guestName, CancellationToken ct)
    {
        try
        {
            DateTime now = _clock.GetUtcNow().UtcDateTime;
            if (link.LastNotifiedAt is { } last && now - last < ReviewLinkOptions.NotifyDecisionFloor) return;

            string name = SafeDisplayName(guestName);
            string details = decision == EditReviewDecisionKind.Approved
                ? $"{name} (guest) approved"
                : $"{name} (guest) asked for changes";
            await SendAsync(link, timelineName, details, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    /// <summary>
    /// The guest-chosen name reduced to something that cannot be a link or an address in the owner's mail: URL-ish and
    /// '@' tokens removed, whitespace collapsed, at most 40 characters; "A guest" when nothing is left.
    /// </summary>
    public static string SafeDisplayName(string? raw)
    {
        string[] kept = (raw ?? "")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Contains('@') && !t.Contains("://") && !t.Contains("www.", StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(t, @"\w\.[a-z]{2,}(/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToArray();
        string joined = string.Concat(string.Join(' ', kept).Where(c => c is not ('<' or '>' or '"' or '`' or '\\')));
        joined = joined.Trim();
        if (joined.Length > 40) joined = joined[..40].TrimEnd();
        return joined.Length == 0 ? "A guest" : joined;
    }

    private async Task SendAsync(EditReviewLink link, string timelineName, string details, DateTime now, CancellationToken ct)
    {
        string subject = ReviewGuestText.Excerpt(string.IsNullOrWhiteSpace(link.Label) ? timelineName : $"{timelineName} ({link.Label})", 120);
        if (subject.Length == 0) subject = "Timeline";
        link.LastNotifiedAt = now;
        await db.SaveChangesAsync(ct);
        await notifier.GuestReviewActivityAsync(link.OrganizationId, link.ProjectId, link.CreatedByUserId, subject, details, ct);
    }
}
