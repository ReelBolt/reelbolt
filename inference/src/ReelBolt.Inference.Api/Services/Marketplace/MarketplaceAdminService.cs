using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Platform-admin side of the marketplace: the review queue, reports, takedown and restore, and the append-only audit. Every action
/// begins with an admin check that re-reads <c>application_users.is_admin</c>; an org Owner is not enough. Every status change goes
/// through <see cref="RecipeStatusMachine"/> with the admin as actor and writes an audit row. No answer carries an email or a user id
/// of an author or a reporter.
/// </summary>
public sealed class MarketplaceAdminService(
    InferenceApiDbContext db,
    ICurrentUser user,
    MarketplacePublishService publish,
    IMarketplaceObjectStore objects,
    IMarketplacePreviewDispatcher dispatcher,
    IMarketplaceAuthorNotifier notifier,
    IReviewQueueQuery reviewQueue,
    ILogger<MarketplaceAdminService> logger)
{
    public const int MaxNoteChars = 500;
    private const int PageSize = 200;

    /// <summary>The closed list of reasons an admin may give for a reject or remove (what the author is told, as a category).</summary>
    public static readonly IReadOnlySet<string> ReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "content", "unsafe", "copyright", "low_quality", "other"
    };

    private static MarketplaceResult<T> Fail<T>(MarketplaceError e) => MarketplaceResult<T>.Fail(e);

    private static readonly MarketplaceError StaleError = new(409, "STALE_VERSION", "This recipe changed since it was loaded. Reload the queue.");

    private async Task<bool> IsAdminAsync(CancellationToken ct) =>
        user.IsPlatformAdmin && await db.ApplicationUsers.AsNoTracking().AnyAsync(u => u.Id == user.UserId && u.IsAdmin, ct);

    // ------------------------------------------------------------------ reads

    /// <summary>
    /// The versions waiting for a human, oldest first, paged by keyset (created_at, id). Every condition, the ordering and the page
    /// are in SQL: only the latest version of a pending recipe, or a version newer than the live one, can be waiting.
    /// </summary>
    public async Task<MarketplaceResult<AdminRecipeRows>> QueueAsync(string? state, CancellationToken ct) => await QueueAsync(state, null, ct);

    public async Task<MarketplaceResult<AdminRecipeRows>> QueueAsync(string? state, string? cursor, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminRecipeRows>(new MarketplaceError(403));
        if (!string.IsNullOrWhiteSpace(state) && !string.Equals(state.Trim(), "pending", StringComparison.OrdinalIgnoreCase))
            return Fail<AdminRecipeRows>(new MarketplaceError(400, "INVALID_FIELD", "state must be 'pending'."));
        if (!TryParseCursor(cursor, out DateTime afterAt, out Guid afterId))
            return Fail<AdminRecipeRows>(new MarketplaceError(400, "INVALID_FIELD", "Unknown cursor."));

        IQueryable<MarketplaceRecipeVersion> source = QueueQuery(db, reviewQueue, cursor is null ? null : afterAt, afterId);
        List<MarketplaceRecipeVersion> pending = await source.Take(PageSize + 1).ToListAsync(ct);
        string? next = null;
        if (pending.Count > PageSize)
        {
            pending.RemoveAt(PageSize);
            MarketplaceRecipeVersion last = pending[^1];
            next = $"{last.CreatedAt.Ticks}.{last.Id:N}";
        }

        Dictionary<Guid, string> orgs = await OrgNamesAsync(pending.Select(v => v.Recipe!.AuthorOrgId), ct);
        return MarketplaceResult<AdminRecipeRows>.Ok(new AdminRecipeRows([.. pending.Select(v => RowOf(v.Recipe!, v, orgs, withPrompts: true))], next));
    }

    /// <summary>The whole queue query, ordered and keyed, before the page is taken: conditions, ordering and cursor are all SQL.</summary>
    public static IQueryable<MarketplaceRecipeVersion> QueueQuery(InferenceApiDbContext db, IReviewQueueQuery reviewQueue, DateTime? afterAt, Guid afterId)
    {
        IQueryable<MarketplaceRecipeVersion> source = db.MarketplaceRecipeVersions.AsNoTracking().Include(v => v.Recipe)
            .Where(v => (v.Recipe!.Status == RecipeStatus.Pending || (v.Recipe.Status == RecipeStatus.Listed && v.Id != v.Recipe.CurrentVersionId))
                && v.N == db.MarketplaceRecipeVersions.Where(x => x.RecipeId == v.RecipeId).Max(x => x.N));
        source = reviewQueue.Pending(source);
        if (afterAt is { } at)
            source = source.Where(v => v.CreatedAt > at || (v.CreatedAt == at && v.Id.CompareTo(afterId) > 0));
        return source.OrderBy(v => v.CreatedAt).ThenBy(v => v.Id);
    }

    private static bool TryParseCursor(string? cursor, out DateTime at, out Guid id)
    {
        at = default;
        id = default;
        if (cursor is null)
            return true;
        string[] parts = cursor.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long ticks) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(parts[1], "N", out id))
            return false;
        at = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    public async Task<MarketplaceResult<AdminRecipeRows>> RecipesAsync(string? status, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminRecipeRows>(new MarketplaceError(403));

        IQueryable<MarketplaceRecipe> query = db.MarketplaceRecipes.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse(status.Trim(), true, out RecipeStatus parsed) || !Enum.IsDefined(parsed))
                return Fail<AdminRecipeRows>(new MarketplaceError(400, "INVALID_FIELD", "Unknown status."));
            query = query.Where(r => r.Status == parsed);
        }

        List<MarketplaceRecipe> recipes = await query.OrderByDescending(r => r.UpdatedAt).Take(PageSize).ToListAsync(ct);
        Guid[] ids = [.. recipes.Select(r => r.Id)];
        List<MarketplaceRecipeVersion> versions = await db.MarketplaceRecipeVersions.AsNoTracking().Where(v => ids.Contains(v.RecipeId)).ToListAsync(ct);
        Dictionary<Guid, string> orgs = await OrgNamesAsync(recipes.Select(r => r.AuthorOrgId), ct);

        List<AdminRecipeRow> rows = [];
        foreach (MarketplaceRecipe r in recipes)
        {
            List<MarketplaceRecipeVersion> mine = [.. versions.Where(v => v.RecipeId == r.Id).OrderBy(v => v.N)];
            MarketplaceRecipeVersion? shown = mine.FirstOrDefault(v => v.Id == r.CurrentVersionId) ?? mine.LastOrDefault();
            rows.Add(RowOf(r, shown, orgs, withPrompts: false));
        }

        return MarketplaceResult<AdminRecipeRows>.Ok(new AdminRecipeRows(rows));
    }

    public async Task<MarketplaceResult<AdminReportRows>> ReportsAsync(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminReportRows>(new MarketplaceError(403));

        List<MarketplaceReport> open = await db.MarketplaceReports.AsNoTracking().Where(r => r.State == ReportState.Open)
            .OrderByDescending(r => r.CreatedAt).Take(PageSize).ToListAsync(ct);
        Guid[] ids = [.. open.Select(r => r.RecipeId).Distinct()];
        Dictionary<Guid, string> names = await db.MarketplaceRecipes.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var counts = (await db.MarketplaceReports.AsNoTracking().Where(r => ids.Contains(r.RecipeId) && r.State == ReportState.Open)
            .Select(r => new { r.RecipeId, r.ReporterUserId }).ToListAsync(ct))
            .GroupBy(r => r.RecipeId).ToDictionary(g => g.Key, g => g.Select(x => x.ReporterUserId).Distinct().Count());

        Guid[] reporterIds = [.. open.Select(r => r.ReporterUserId).Distinct()];
        var accounts = await db.ApplicationUsers.AsNoTracking().Where(u => reporterIds.Contains(u.Id))
            .Select(u => new { u.Id, u.CreatedAt, Verified = u.EmailVerifiedAt != null }).ToListAsync(ct);
        DateTime now = DateTime.UtcNow;

        return MarketplaceResult<AdminReportRows>.Ok(new AdminReportRows([.. open.Select(r =>
        {
            var account = accounts.FirstOrDefault(a => a.Id == r.ReporterUserId);
            return new AdminReportRow(
                r.Id, r.RecipeId, names.GetValueOrDefault(r.RecipeId, ""), MarketplaceWire.Word(r.Reason.ToString()), r.Note,
                counts.GetValueOrDefault(r.RecipeId, 1), r.CreatedAt,
                account is null ? null : (int)(now - account.CreatedAt).TotalDays, account?.Verified ?? false);
        })]));
    }

    public async Task<MarketplaceResult<AdminRecipeDetail>> DetailAsync(Guid recipeId, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminRecipeDetail>(new MarketplaceError(403));

        MarketplaceRecipe? r = await db.MarketplaceRecipes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recipeId, ct);
        if (r is null)
            return Fail<AdminRecipeDetail>(new MarketplaceError(404));

        List<MarketplaceRecipeVersion> versions = await db.MarketplaceRecipeVersions.AsNoTracking().Where(v => v.RecipeId == recipeId).OrderBy(v => v.N).ToListAsync(ct);
        MarketplaceRecipeVersion? latest = versions.LastOrDefault();
        MarketplaceRecipeVersion? current = versions.FirstOrDefault(v => v.Id == r.CurrentVersionId);
        MarketplaceRecipeVersion? shown = latest ?? current;
        Dictionary<Guid, string> orgs = await OrgNamesAsync([r.AuthorOrgId], ct);
        int open = (await db.MarketplaceReports.AsNoTracking().Where(x => x.RecipeId == recipeId && x.State == ReportState.Open)
            .Select(x => x.ReporterUserId).ToListAsync(ct)).Distinct().Count();

        JsonObject? meta = shown is null ? null : ParseObject(shown.CheckJson)["meta"] as JsonObject;
        return MarketplaceResult<AdminRecipeDetail>.Ok(new AdminRecipeDetail(
            r.Id,
            r.Name,
            r.Description,
            r.AuthorLabel ?? "",
            orgs.GetValueOrDefault(r.AuthorOrgId),
            MarketplaceWire.Status(r.Status),
            r.HiddenByReports,
            r.RemovedReason,
            r.Category,
            r.Language,
            r.CostClass,
            current?.N,
            r.CopyOrgs,
            open,
            [.. versions.Select(v =>
            {
                JsonObject c = ParseObject(v.CheckJson);
                return new AdminVersionDto(v.Id, v.N, MarketplacePublishService.VersionDigest(v), MarketplaceWire.Preview(v.PreviewStatus),
                    c["decision"]?.GetValue<string>(), c["reason"]?.GetValue<string>(), v.CreatedAt, v.ConsentAt);
            })],
            shown is null ? [] : PromptsOf(shown),
            shown?.CheckJson,
            r.ListedAt,
            r.CreatedAt));
    }

    /// <summary>
    /// The final preview or poster key of ONE version, for the reviewer. Platform admin only; any failure (not an admin, unknown or
    /// unfinished version, a key that is not exactly this version's own, a quarantined key) is a bare 404.
    /// </summary>
    public async Task<MarketplaceResult<AdminMediaRef>> MediaAsync(Guid versionId, bool poster, CancellationToken ct)
    {
        MarketplaceResult<AdminMediaRef> none = Fail<AdminMediaRef>(new MarketplaceError(404));
        if (!await IsAdminAsync(ct))
            return none;

        MarketplaceRecipeVersion? v = await db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        string? key = poster ? v?.PosterKey : v?.PreviewKey;
        if (v is null || v.PreviewStatus != PreviewStatus.Ready || string.IsNullOrEmpty(key)
            || ReelBolt.Shared.Storage.MarketplacePreviewKeys.IsQuarantine(key) || !ReelBolt.Shared.Storage.MarketplacePreviewKeys.IsVersionObject(v.RecipeId, v.Id, key))
            return none;

        return MarketplaceResult<AdminMediaRef>.Ok(new AdminMediaRef(v.RecipeId, v.Id, key, poster ? null : v.PreviewSha256));
    }

    public async Task<MarketplaceResult<AdminCopies>> CopiesAsync(Guid recipeId, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminCopies>(new MarketplaceError(403));
        if (!await db.MarketplaceRecipes.AsNoTracking().AnyAsync(r => r.Id == recipeId, ct))
            return Fail<AdminCopies>(new MarketplaceError(404));

        List<MarketplaceCopy> copies = await db.MarketplaceCopies.AsNoTracking().Where(c => c.RecipeId == recipeId).OrderBy(c => c.FirstCopiedAt).Take(1000).ToListAsync(ct);
        return MarketplaceResult<AdminCopies>.Ok(new AdminCopies([.. copies.Select(c => new AdminCopyDto(c.OrgId, c.VersionId, c.FirstCopiedAt))]));
    }

    public async Task<MarketplaceResult<AdminAuditRows>> AuditAsync(Guid? recipeId, int? limit, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminAuditRows>(new MarketplaceError(403));

        IQueryable<MarketplaceAuditEntry> query = db.MarketplaceAudit.AsNoTracking();
        if (recipeId is { } id)
            query = query.Where(a => a.RecipeId == id);
        List<MarketplaceAuditEntry> rows = await query.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);

        return MarketplaceResult<AdminAuditRows>.Ok(new AdminAuditRows([.. rows.Select(a =>
        {
            JsonObject d = ParseObject(a.DetailJson);
            // Only another admin's id is shown; a user (reporter, author) never is.
            return new AdminAuditRow(a.Id, a.RecipeId, a.VersionId, a.Action, a.ActorKind, a.ActorKind == "admin" ? a.ActorUserId : null,
                d["reason"]?.ToString(), d["note"]?.ToString(), a.CreatedAt);
        })]));
    }

    // ------------------------------------------------------------------ decisions

    public async Task<MarketplaceResult<AdminActionResponse>> ApproveAsync(Guid recipeId, AdminApproveRequest request, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminActionResponse>(new MarketplaceError(403));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));

        DateTime now = DateTime.UtcNow;
        if (request.VersionId is not { } versionId)
        {
            // Without a version, approve only clears the report flag of a recipe that is live.
            if (recipe.Status == RecipeStatus.Listed && recipe.HiddenByReports && request.ClearHidden == true)
                return await ClearHiddenAsync(recipe, "admin_cleared", now, ct);
            return Fail<AdminActionResponse>(new MarketplaceError(400, "VERSION_REQUIRED", "Send the versionId (and digest) of the version you reviewed."));
        }

        if (string.IsNullOrWhiteSpace(request.Digest))
            return Fail<AdminActionResponse>(new MarketplaceError(400, "DIGEST_REQUIRED", "Send the digest of the version you reviewed."));

        MarketplaceResult<PublishedRecipeResponse> decided = await publish.ApproveVersionAsync(recipeId, versionId, user.UserId, request.Digest, ct);
        if (decided.Error is { } error)
            return Fail<AdminActionResponse>(error);

        if (request.ClearHidden == true && recipe.HiddenByReports)
            return await ClearHiddenAsync(recipe, "admin_cleared", now, ct);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    public async Task<MarketplaceResult<AdminActionResponse>> RejectAsync(Guid recipeId, AdminRejectRequest request, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminActionResponse>(new MarketplaceError(403));
        if (ValidateReason(request.Reason, request.Note) is { } bad)
            return Fail<AdminActionResponse>(bad);
        if (request.VersionId is not { } versionId)
            return Fail<AdminActionResponse>(new MarketplaceError(400, "VERSION_REQUIRED", "Send the versionId (and digest) of the version you reviewed."));

        if (string.IsNullOrWhiteSpace(request.Digest))
            return Fail<AdminActionResponse>(new MarketplaceError(400, "DIGEST_REQUIRED", "Send the digest of the version you reviewed."));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));

        // A repeat of a reject that already happened on this version has no side effect (no second audit row, no second mail).
        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.RecipeId == recipeId, ct);
        bool repeat = version is not null && ParseObject(version.CheckJson) is { } c && c["decision"]?.GetValue<string>() == "rejected" && c["reason"]?.GetValue<string>() == "admin_rejected";

        MarketplaceResult<PublishedRecipeResponse> decided = await publish.RejectVersionAsync(recipeId, versionId, user.UserId, request.Digest, ct);
        if (decided.Error is { } error)
            return Fail<AdminActionResponse>(error);
        if (repeat)
            return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));

        DateTime now = DateTime.UtcNow;
        string reason = request.Reason!.Trim().ToLowerInvariant();
        AddAudit(recipeId, versionId, "rejected", reason, NoteOf(request.Note), now);
        // Rejecting a pending update of a recipe that is still live says nothing about the reports on the live version.
        if (recipe.Status != RecipeStatus.Listed)
            await MarkReportsActionedAsync(recipeId, now, ct);
        await db.SaveChangesAsync(ct);
        await notifier.NotifyAsync(recipeId, reason, ct);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    public async Task<MarketplaceResult<AdminActionResponse>> RemoveAsync(Guid recipeId, AdminRemoveRequest request, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminActionResponse>(new MarketplaceError(403));
        if (ValidateReason(request.Reason, request.Note) is { } bad)
            return Fail<AdminActionResponse>(bad);

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));

        string reason = request.Reason!.Trim().ToLowerInvariant();
        bool already = recipe.Status == RecipeStatus.Removed;
        if (!already)
        {
            DateTime now = DateTime.UtcNow;
            if (!RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Removed, RecipeActor.Admin, now))
                return Fail<AdminActionResponse>(new MarketplaceError(409, "INVALID_STATE", "This recipe cannot be removed in its current state."));

            recipe.RemovedReason = reason;
            recipe.Revision++;
            AddAudit(recipeId, recipe.CurrentVersionId, "removed", reason, NoteOf(request.Note), now);
            await MarkReportsActionedAsync(recipeId, now, ct);
            if (await TrySaveAsync(ct) is { } stale)
                return Fail<AdminActionResponse>(stale);
        }

        // Re-running a remove also re-runs the object deletion, so a failed delete can be retried.
        await DeleteObjectsAsync(recipeId, ct);
        if (!already)
            await notifier.NotifyAsync(recipeId, reason, ct);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    public async Task<MarketplaceResult<AdminActionResponse>> RestoreAsync(Guid recipeId, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminActionResponse>(new MarketplaceError(403));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.Include(r => r.Versions).FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));
        if (recipe.Status != RecipeStatus.Removed)
            return Fail<AdminActionResponse>(new MarketplaceError(409, "INVALID_STATE", "Only a removed recipe can be restored."));

        // A safety removal, or an author, submitter or organization under an active hold, is never restored by this route.
        if (recipe.RemovedReason == MarketplaceSafetyService.Reason
            || await MarketplaceSafetyService.HasActiveHoldAsync(db, recipe, recipe.Versions.Select(v => v.SubmittedByUserId), ct))
        {
            return Fail<AdminActionResponse>(new MarketplaceError(409, "RESTORE_BLOCKED_SAFETY", "This recipe cannot be restored."));
        }

        DateTime now = DateTime.UtcNow;
        MarketplaceRecipeVersion? current = recipe.Versions.FirstOrDefault(v => v.Id == recipe.CurrentVersionId);
        bool objectsExist = current is { PreviewKey.Length: > 0, PosterKey.Length: > 0 }
            && await objects.ExistsAsync(current.PreviewKey, ct) && await objects.ExistsAsync(current.PosterKey, ct);

        // A restore never lists at once: it goes back to review, and an admin approves it afterwards.
        RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Pending, RecipeActor.Admin, now);
        MarketplaceRecipeVersion? redispatch = null;
        if (objectsExist)
        {
            JsonObject kept = ParseObject(current!.CheckJson);
            kept["decision"] = "pending";
            kept["reason"] = "restored";
            kept.Remove("superseded");
            current.CheckJson = kept.ToJsonString();
            AddAudit(recipeId, current.Id, "restored", "objects_present", null, now);
        }
        else
        {
            // The preview objects were deleted by the takedown (or were never there): a new preview is made first.
            redispatch = current ?? recipe.Versions.OrderByDescending(v => v.N).FirstOrDefault();
            if (redispatch is null)
                return Fail<AdminActionResponse>(new MarketplaceError(409, "INVALID_STATE", "This recipe has no version to restore."));

            redispatch.PreviewStatus = PreviewStatus.Preparing;
            redispatch.PreviewKey = null;
            redispatch.PosterKey = null;
            redispatch.PreviewSha256 = null;
            redispatch.PreviewSeconds = null;
            JsonObject check = ParseObject(redispatch.CheckJson);
            check.Remove("decision");
            check.Remove("reason");
            check.Remove("superseded");
            redispatch.CheckJson = check.ToJsonString();
            AddAudit(recipeId, redispatch.Id, "restored", "preview_regenerating", null, now);
        }

        recipe.RemovedReason = null;
        recipe.Revision++;
        if (await TrySaveAsync(ct) is { } stale)
            return Fail<AdminActionResponse>(stale);

        if (redispatch is not null)
            await dispatcher.DispatchAsync(recipeId, redispatch.Id, ct);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    public async Task<MarketplaceResult<AdminActionResponse>> DismissReportAsync(Guid reportId, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct))
            return Fail<AdminActionResponse>(new MarketplaceError(403));

        MarketplaceReport? report = await db.MarketplaceReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));
        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == report.RecipeId, ct);
        if (recipe is null)
            return Fail<AdminActionResponse>(new MarketplaceError(404));
        if (report.State != ReportState.Open)
            return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));

        // A dismissal closes every open report of the recipe and brings it back; only NEW distinct reporters can hide it again, and a
        // dismissed (recipe, reporter) pair never counts toward the threshold.
        DateTime now = DateTime.UtcNow;
        foreach (MarketplaceReport open in await db.MarketplaceReports.Where(r => r.RecipeId == recipe.Id && r.State == ReportState.Open).ToListAsync(ct))
        {
            open.State = ReportState.Dismissed;
            open.HandledBy = user.UserId;
            open.HandledAt = now;
        }

        AddAudit(recipe.Id, recipe.CurrentVersionId, "report_dismissed", MarketplaceWire.Word(report.Reason.ToString()), null, now);
        if (recipe.HiddenByReports)
        {
            recipe.HiddenByReports = false;
            recipe.UpdatedAt = now;
            recipe.Revision++;
            AddAudit(recipe.Id, recipe.CurrentVersionId, "hidden_cleared", "reports_dismissed", null, now);
        }

        if (await TrySaveAsync(ct) is { } stale)
            return Fail<AdminActionResponse>(stale);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<MarketplaceResult<AdminActionResponse>> ClearHiddenAsync(MarketplaceRecipe recipe, string reason, DateTime now, CancellationToken ct)
    {
        recipe.HiddenByReports = false;
        recipe.UpdatedAt = now;
        recipe.Revision++;
        AddAudit(recipe.Id, recipe.CurrentVersionId, "hidden_cleared", reason, null, now);
        foreach (MarketplaceReport report in await db.MarketplaceReports.Where(r => r.RecipeId == recipe.Id && r.State == ReportState.Open).ToListAsync(ct))
        {
            report.State = ReportState.Dismissed;
            report.HandledBy = user.UserId;
            report.HandledAt = now;
        }

        if (await TrySaveAsync(ct) is { } stale)
            return Fail<AdminActionResponse>(stale);
        return MarketplaceResult<AdminActionResponse>.Ok(Action(recipe));
    }

    private async Task MarkReportsActionedAsync(Guid recipeId, DateTime now, CancellationToken ct)
    {
        foreach (MarketplaceReport report in await db.MarketplaceReports.Where(r => r.RecipeId == recipeId && r.State == ReportState.Open).ToListAsync(ct))
        {
            report.State = ReportState.Actioned;
            report.HandledBy = user.UserId;
            report.HandledAt = now;
        }
    }

    private async Task<MarketplaceError?> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return StaleError;
        }
    }

    private async Task DeleteObjectsAsync(Guid recipeId, CancellationToken ct)
    {
        try
        {
            int left = await objects.DeleteRecipeObjectsAsync(recipeId, await RecipeObjectProtection.LoadAsync(db, recipeId, ct), ct);
            if (left > 0)
                logger.LogWarning("{Count} object(s) of removed marketplace recipe {RecipeId} could not be deleted; the sweeper retries", left, recipeId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The recipe is already removed; running remove again (or the sweeper) retries the deletion.
            logger.LogError(ex, "Deleting the objects of removed marketplace recipe {RecipeId} failed", recipeId);
        }
    }

    private void AddAudit(Guid? recipeId, Guid? versionId, string action, string reason, string? note, DateTime now)
    {
        JsonObject detail = new() { ["reason"] = reason };
        if (note is not null)
            detail["note"] = note;
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            VersionId = versionId,
            Action = action,
            ActorKind = "admin",
            ActorUserId = user.UserId,
            DetailJson = detail.ToJsonString(),
            CreatedAt = now
        });
    }

    private static MarketplaceError? ValidateReason(string? reason, string? note)
    {
        if (string.IsNullOrWhiteSpace(reason) || !ReasonCodes.Contains(reason.Trim().ToLowerInvariant()))
            return new MarketplaceError(400, "INVALID_FIELD", $"The reason must be one of: {string.Join(", ", ReasonCodes.Order())}.");

        if (!string.IsNullOrWhiteSpace(note) && MarketplaceText.Sanitize(note).Length > MaxNoteChars)
            return new MarketplaceError(400, "INVALID_FIELD", $"The note can be at most {MaxNoteChars} characters.");
        return null;
    }

    private static string? NoteOf(string? note)
    {
        string clean = MarketplaceText.Sanitize(note);
        return clean.Length == 0 ? null : clean;
    }

    private static AdminActionResponse Action(MarketplaceRecipe recipe) => new(recipe.Id, MarketplaceWire.Status(recipe.Status), recipe.HiddenByReports);

    private async Task<Dictionary<Guid, string>> OrgNamesAsync(IEnumerable<Guid> orgIds, CancellationToken ct)
    {
        Guid[] ids = [.. orgIds.Distinct()];
        return await db.Organizations.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Name, ct);
    }

    private static AdminRecipeRow RowOf(MarketplaceRecipe recipe, MarketplaceRecipeVersion? version, IReadOnlyDictionary<Guid, string> orgs, bool withPrompts)
    {
        JsonObject check = ParseObject(version?.CheckJson);
        JsonObject? meta = check["meta"] as JsonObject;
        // A pending version's own text lives in its check; the recipe row still shows the live version until approval.
        bool pendingVersion = version is not null && version.Id != recipe.CurrentVersionId && meta is not null;
        string name = pendingVersion ? meta!["name"]?.GetValue<string>() ?? recipe.Name : recipe.Name;
        string? description = pendingVersion ? meta!["description"]?.GetValue<string>() ?? recipe.Description : recipe.Description;
        string label = pendingVersion ? meta!["authorLabel"]?.GetValue<string>() ?? recipe.AuthorLabel ?? "" : recipe.AuthorLabel ?? "";

        List<string> lint = [];
        if (check["lintCodes"] is JsonArray codes)
            lint.AddRange(codes.Select(c => c?.ToString() ?? "").Where(c => c.Length > 0));
        else if (check["reason"]?.GetValue<string>() is { Length: > 0 } reason)
            lint.Add(reason);

        return new AdminRecipeRow(
            recipe.Id,
            version?.Id,
            version is null ? null : MarketplacePublishService.VersionDigest(version),
            name,
            description,
            label,
            orgs.GetValueOrDefault(recipe.AuthorOrgId),
            version?.N ?? 0,
            MarketplaceWire.Status(recipe.Status),
            recipe.HiddenByReports,
            version is null ? null : new AdminChecksDto(check["text"]?.GetValue<string>(), MarketplaceWire.Preview(version.PreviewStatus), lint),
            withPrompts && version is not null ? PromptsOf(version) : null,
            version is null ? null : MarketplaceWire.Preview(version.PreviewStatus),
            version?.PreviewSha256);
    }

    private static IReadOnlyList<AdminPromptDto> PromptsOf(MarketplaceRecipeVersion version)
    {
        try
        {
            return [.. (PortableRecipeJson.TryParse(version.SnapshotJson)?.TextFields() ?? [])
                .Where(t => t.StepOrder is not null).Select(t => new AdminPromptDto(t.StepOrder!.Value, t.Field, t.Text))];
        }
        catch (NullReferenceException)
        {
            // A snapshot that is not a full recipe has no prompts to show.
            return [];
        }
    }

    private static JsonObject ParseObject(string? json)
    {
        try
        {
            return JsonNode.Parse(json ?? "{}") as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
