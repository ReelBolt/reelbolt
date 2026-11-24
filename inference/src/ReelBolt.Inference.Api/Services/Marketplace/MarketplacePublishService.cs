using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The publish gate and owner side of the marketplace (docs/design/recipe-marketplace.md): preview, create, new version,
/// unpublish, mine, and the finalize step a preview job ends with. Nothing here returns an author, project, workflow, provider,
/// file, voice, step-result or storage identifier.
/// </summary>
public sealed partial class MarketplacePublishService(
    InferenceApiDbContext db,
    ICurrentUser user,
    IProjectAccessService projectAccess,
    IMarketplaceTextCheck textCheck,
    IMarketplacePreviewDispatcher dispatcher,
    ICreditBalanceReader credits,
    IMarketplaceCapLock capLock,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplacePublishService> logger,
    IMarketplaceAuthorNotifier? notifier = null)
{
    public static readonly IReadOnlyList<string> Languages = ["en", "it"];

    /// <summary>Plans whose organizations list at once.</summary>
    public static readonly IReadOnlySet<string> PaidPlans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "creator", "pro", "team", "enterprise"
    };

    public const string LicenceSummary =
        "Anyone can view, copy and adapt your recipe and see its preview. You keep ownership of what you made; you can unpublish "
        + "at any time and copies already made stay.";

    [GeneratedRegex(@"[^@\s]+@[^@\s]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LooksLikeEmail();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WordBoundary();

    private static readonly MarketplaceError ConcurrentUpdate = new(409, "CONCURRENT_UPDATE", "This recipe was changed at the same moment. Reload and try again.");

    private MarketplaceOptions Options => options.Value;

    public ConsentResponse GetConsent() => new(Options.ConsentVersion, MarketplaceConsentText.English, LicenceSummary);

    // ------------------------------------------------------------------ preview

    public async Task<MarketplaceResult<PublishPreviewResponse>> PreviewAsync(Guid projectId, Guid workflowId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (!access.IsFound)
            return MarketplaceResult<PublishPreviewResponse>.Fail(AccessError(access.Outcome));

        if (await SubmitterRecordAsync(user.UserId, ct) == SubmitterRecord.Hard)
            return MarketplaceResult<PublishPreviewResponse>.Fail(new MarketplaceError(404));

        Analysis? analysis = await AnalyzeAsync(projectId, workflowId, ct);
        if (analysis is null)
            return MarketplaceResult<PublishPreviewResponse>.Fail(new MarketplaceError(404));

        PortableRecipe? recipe = analysis.Recipe;
        return MarketplaceResult<PublishPreviewResponse>.Ok(new PublishPreviewResponse(
            analysis.Refusals.Count == 0,
            analysis.Refusals,
            [.. (recipe?.Removed ?? []).Select(r => new RemovedDto(r.StepOrder, r.Field, r.What))],
            [.. (recipe?.Slots ?? []).Select(s => new SlotDto(s.Kind, s.StepOrder, s.Label))],
            [.. (recipe?.Steps ?? []).Select(s => new StepDto(s.Order, Humanize(s.StepType.ToString()), s.Label ?? Humanize(s.StepType.ToString()), LimitsOf(s)))],
            [.. (recipe?.TextFields() ?? []).Where(t => t.StepOrder is not null).Select(t => new PromptDto(t.StepOrder!.Value, t.Field, t.Text))],
            analysis.CostClass));
    }

    // ------------------------------------------------------------------ create / update

    public Task<MarketplaceResult<PublishedRecipeResponse>> CreateAsync(PublishRecipeRequest request, CancellationToken ct) =>
        PublishAsync(null, request, ct);

    public Task<MarketplaceResult<PublishedRecipeResponse>> UpdateAsync(Guid recipeId, PublishRecipeRequest request, CancellationToken ct) =>
        PublishAsync(recipeId, request, ct);

    private async Task<MarketplaceResult<PublishedRecipeResponse>> PublishAsync(Guid? recipeId, PublishRecipeRequest request, CancellationToken ct)
    {
        static MarketplaceResult<PublishedRecipeResponse> Fail(MarketplaceError e) => MarketplaceResult<PublishedRecipeResponse>.Fail(e);

        if (!user.EmailVerified)
        {
            return Fail(new MarketplaceError(403, EmailVerificationGate.ErrorCode,
                "Verify your email address before publishing. Check your inbox for the verification link."));
        }

        if (user.OrganizationId is not { } orgId)
            return Fail(new MarketplaceError(404));

        MarketplaceRecipe? existing = null;
        if (recipeId is { } rid)
        {
            // An organization's recipe is replaced under organization-wide Edit, as unpublish is: Edit on one project
            // (a project-restricted API key) is not authority over every recipe of the organization.
            ProjectAccessOutcome role = projectAccess.AuthorizeOrganization(ProjectPermission.Edit);
            if (role != ProjectAccessOutcome.Found)
                return Fail(AccessError(role));

            existing = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == rid, ct);
            if (existing is null || existing.AuthorOrgId != orgId)
                return Fail(new MarketplaceError(404));
            if (existing.Status == RecipeStatus.Removed)
                return Fail(new MarketplaceError(409, "RECIPE_REMOVED", "This recipe was taken down and cannot be changed."));
        }

        if (!request.ConsentAccepted)
            return Fail(new MarketplaceError(400, "CONSENT_REQUIRED", "Accept the publishing terms to continue."));
        if (!string.Equals(request.ConsentVersion, Options.ConsentVersion, StringComparison.Ordinal))
            return Fail(new MarketplaceError(409, "CONSENT_VERSION_STALE", "The publishing terms changed. Read them again and accept to continue."));

        (Meta? meta, MarketplaceError? fieldError) = await ValidateFieldsAsync(request, ct);
        if (fieldError is not null)
            return Fail(fieldError);

        ProjectAccessResult access = await projectAccess.GetAsync(request.ProjectId, ProjectPermission.Edit, ct);
        if (!access.IsFound)
            return Fail(AccessError(access.Outcome));

        // A submitter with a held or quarantined project anywhere (any organization they belong to) publishes nothing; no hint why.
        if (await SubmitterRecordAsync(user.UserId, ct) == SubmitterRecord.Hard)
        {
            return Fail(new MarketplaceError(404, "PREVIEW_REQUIRED",
                "Choose a finished video from this project to use as the recipe's preview."));
        }

        Analysis? analysis = await AnalyzeAsync(request.ProjectId, request.WorkflowId, ct);
        if (analysis is null)
            return Fail(new MarketplaceError(404));
        if (analysis.Refusals.Count > 0)
            return Fail(Refused(analysis.Refusals));

        MarketplaceError? capError = await CheckCapsAsync(existing, orgId, ct);
        if (capError is not null)
            return Fail(capError);

        string? previewSource = await ResolvePreviewSourceAsync(request.OutputRef, request.ProjectId, ct);
        if (previewSource is null)
        {
            return Fail(new MarketplaceError(404, "PREVIEW_REQUIRED",
                "Choose a finished video from this project to use as the recipe's preview."));
        }

        PortableRecipe recipe = analysis.Recipe!;
        List<TextCheckField> texts =
        [
            new("name", meta!.Name),
            new("description", meta.Description),
            new("authorLabel", meta.AuthorLabel),
            .. recipe.TextFields().Select(t => new TextCheckField(t.StepOrder is { } o ? $"step{o}.{t.Field}" : t.Field, t.Text))
        ];
        // The same content by the same submitter while an earlier identical submission is still being prepared: no second pass at the
        // checks, and no moderation call spent on it.
        string submittedDigest = ContentDigest(analysis.Json!, recipe.Manifest is null ? "{}" : AppManifestJson.Serialize(recipe.Manifest));
        if (await db.MarketplaceRecipeVersions.AsNoTracking().AnyAsync(
                v => v.SubmittedByUserId == user.UserId && v.SnapshotDigest == submittedDigest && v.PreviewStatus == PreviewStatus.Preparing, ct))
        {
            return Fail(new MarketplaceError(409, "DUPLICATE_SUBMISSION", "This recipe is already being prepared. Wait for it to finish."));
        }

        int blockedToday = await db.MarketplaceAudit.AsNoTracking()
            .CountAsync(a => a.Action == "text_blocked" && a.ActorUserId == user.UserId && a.CreatedAt >= DateTime.UtcNow.Date, ct);
        if (blockedToday >= Options.MaxBlockedPublishAttemptsPerUserPerDay)
        {
            return Fail(new MarketplaceError(429, "PUBLISH_ATTEMPTS_LIMITED",
                "Too many publish attempts were refused today. Try again tomorrow."));
        }

        TextCheckResult check = await textCheck.CheckAsync(texts, orgId, request.ProjectId, user.UserId, ct);
        if (check.Verdict == TextVerdict.Block)
        {
            Audit(null, null, "text_blocked", "user", user.UserId, "text_blocked", DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            return Fail(Refused([new RefusalDto("TEXT_BLOCKED", null,
                "Some of the text in this recipe may violate the content policy. Change it and try again.")]));
        }

        // Counts and inserts of the caps run under a per-organization lock so parallel POSTs cannot all pass the count.
        await using IAsyncDisposable capGate = await capLock.AcquireAsync(db, orgId, ct);
        MarketplaceError? lockedCap = await CheckCapsAsync(existing, orgId, ct);
        if (lockedCap is not null)
            return Fail(lockedCap);

        DateTime now = DateTime.UtcNow;
        string[] stepTypes = [.. recipe.Steps.Select(s => s.StepType.ToString()).Distinct(StringComparer.Ordinal)];
        meta = meta with { CostClass = analysis.CostClass, StepCount = recipe.Steps.Count, StepTypes = stepTypes };

        MarketplaceRecipe recipeRow;
        int versionN;
        if (existing is null)
        {
            recipeRow = new MarketplaceRecipe
            {
                Id = Guid.NewGuid(),
                AuthorUserId = user.UserId,
                AuthorOrgId = orgId,
                Status = RecipeStatus.Draft,
                CreatedAt = now,
                UpdatedAt = now
            };
            ApplyMeta(recipeRow, meta);
            db.MarketplaceRecipes.Add(recipeRow);
            versionN = 1;
        }
        else
        {
            recipeRow = existing;
            versionN = (await db.MarketplaceRecipeVersions.Where(v => v.RecipeId == recipeRow.Id).MaxAsync(v => (int?)v.N, ct) ?? 0) + 1;
            // A new version replaces the one under review or still preparing, explicitly.
            foreach (MarketplaceRecipeVersion prior in await db.MarketplaceRecipeVersions.Where(v => v.RecipeId == recipeRow.Id).ToListAsync(ct))
            {
                JsonObject priorCheck = ParseCheck(prior.CheckJson);
                string? priorDecision = priorCheck["decision"]?.GetValue<string>();
                if ((priorDecision is null or "pending") && priorCheck["superseded"]?.GetValue<bool>() != true)
                {
                    priorCheck["superseded"] = true;
                    prior.CheckJson = priorCheck.ToJsonString();
                    Audit(recipeRow.Id, prior.Id, "version_superseded", "user", user.UserId, "new_version", now);
                }
            }

            if (recipeRow.Status is RecipeStatus.Rejected or RecipeStatus.Unpublished)
                RecipeStatusMachine.TryTransition(recipeRow, RecipeStatus.Draft, RecipeActor.Author, now);
            if (recipeRow.Status is RecipeStatus.Draft)
                ApplyMeta(recipeRow, meta);
            recipeRow.UpdatedAt = now;
        }

        recipeRow.ConsentVersion = request.ConsentVersion;
        recipeRow.ConsentAt = now;

        MarketplaceRecipeVersion version = new()
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeRow.Id,
            N = versionN,
            SubmittedByUserId = user.UserId,
            SnapshotDigest = ContentDigest(analysis.Json!, recipe.Manifest is null ? "{}" : AppManifestJson.Serialize(recipe.Manifest)),
            SnapshotJson = analysis.Json!,
            ManifestJson = recipe.Manifest is null ? "{}" : AppManifestJson.Serialize(recipe.Manifest),
            PreviewSourceJson = previewSource,
            PreviewStatus = PreviewStatus.Preparing,
            CheckJson = BuildCheckJson(check, meta),
            ConsentVersion = request.ConsentVersion,
            ConsentAt = now,
            CreatedAt = now
        };
        db.MarketplaceRecipeVersions.Add(version);
        Audit(recipeRow.Id, version.Id, existing is null ? "recipe_created" : "version_submitted", "user", user.UserId, "submitted", now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(ConcurrentUpdate);
        }

        try
        {
            await dispatcher.DispatchAsync(recipeRow.Id, version.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is committed; a version left preparing is retried by the preview layer, never listed.
            logger.LogWarning(ex, "Marketplace preview dispatch failed for version {VersionId}", version.Id);
        }

        return MarketplaceResult<PublishedRecipeResponse>.Ok(new PublishedRecipeResponse(
            recipeRow.Id, MarketplaceWire.Status(recipeRow.Status), version.N, MarketplaceWire.Preview(version.PreviewStatus)));
    }

    // ------------------------------------------------------------------ finalize

    /// <summary>
    /// Called by the preview layer when a version's preview is ready (or failed). Applies <see cref="ListingDecision"/>, writes the
    /// audit row, sets <c>ever_listed_at</c> and replaces the previous live version in one save. Idempotent: a version that is no
    /// longer preparing is left alone. The old live version stays live until this decides the new one lists.
    /// </summary>
    public async Task<ListingResult?> FinalizeVersionAsync(Guid versionId, PreviewVerdict previewVerdict, CancellationToken ct)
    {
        // Another writer (an admin removal, an author unpublish, a newer version, a purge) may change the recipe between the read and
        // the save. The recipe's Revision makes that save fail; it is retried once on fresh state, then the version is ignored.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await FinalizeOnceAsync(versionId, previewVerdict, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                logger.LogWarning(ex, "Marketplace finalize hit a concurrent update for version {VersionId} (attempt {Attempt})", versionId, attempt + 1);
                db.ChangeTracker.Clear();
            }
        }

        try
        {
            MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.FirstOrDefaultAsync(v => v.Id == versionId, ct);
            if (version is not null && version.PreviewStatus == PreviewStatus.Preparing)
            {
                version.PreviewStatus = previewVerdict == PreviewVerdict.Failed ? PreviewStatus.Failed : PreviewStatus.Ready;
                Audit(version.RecipeId, version.Id, "version_ignored", "system", null, "concurrent_update", DateTime.UtcNow);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (DbUpdateException ex)
        {
            // The recipe (and with it the version) may be gone, or changed again; nothing was applied either way.
            logger.LogWarning(ex, "Marketplace finalize could not record the ignored version {VersionId}", versionId);
            db.ChangeTracker.Clear();
        }

        return null;
    }

    private async Task<ListingResult?> FinalizeOnceAsync(Guid versionId, PreviewVerdict previewVerdict, CancellationToken ct)
    {
        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.Include(v => v.Recipe).FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version?.Recipe is not { } recipe || version.PreviewStatus != PreviewStatus.Preparing)
            return null;

        DateTime now = DateTime.UtcNow;
        version.PreviewStatus = previewVerdict == PreviewVerdict.Failed ? PreviewStatus.Failed : PreviewStatus.Ready;
        JsonObject check = ParseCheck(version.CheckJson);
        TextVerdict text = Enum.TryParse(check["text"]?.GetValue<string>(), true, out TextVerdict t) ? t : TextVerdict.Unavailable;

        bool actionable = recipe.Status is RecipeStatus.Draft or RecipeStatus.Pending or RecipeStatus.Listed;
        bool newerExists = await db.MarketplaceRecipeVersions.AnyAsync(v => v.RecipeId == recipe.Id && v.N > version.N, ct);
        // The submitter's trust, from recipes that are still standing: a takedown or rejection revokes it.
        Guid submitter = version.SubmittedByUserId == Guid.Empty ? recipe.AuthorUserId : version.SubmittedByUserId;
        bool trusted = await db.MarketplaceRecipes.AnyAsync(
            r => r.AuthorUserId == submitter && r.EverListedAt != null && r.HumanRejectedAt == null
                && r.Status != RecipeStatus.Removed && r.Status != RecipeStatus.Rejected, ct);

        // Author-level flag: anyone (or any organization) with a human-rejected or removed recipe always waits for a human.
        bool authorFlagged = await db.MarketplaceRecipes.AnyAsync(
            r => (r.AuthorUserId == submitter || r.AuthorOrgId == recipe.AuthorOrgId)
                && (r.HumanRejectedAt != null || r.HiddenByReports || r.Status == RecipeStatus.Removed), ct);
        // The same content, from anyone, that is still being processed, waited for a human, was rejected or superseded, or belongs to
        // a removed, hidden or human-rejected recipe: a human decides again, so the automatic checks cannot be re-rolled.
        string? digest = version.SnapshotDigest;
        bool digestMatch = false;
        if (digest is not null)
        {
            foreach (MarketplaceRecipeVersion earlier in await db.MarketplaceRecipeVersions.AsNoTracking().Include(v => v.Recipe)
                .Where(v => v.Id != version.Id && v.SnapshotDigest == digest).ToListAsync(ct))
            {
                JsonObject earlierCheck = ParseCheck(earlier.CheckJson);
                string? earlierDecision = earlierCheck["decision"]?.GetValue<string>();
                bool flaggedRecipe = earlier.Recipe is { } er && (er.HumanRejectedAt != null || er.HiddenByReports || er.Status == RecipeStatus.Removed);
                bool superseded = earlierCheck["superseded"]?.GetValue<bool>() == true;
                if (flaggedRecipe || superseded || earlierDecision is null or "pending" or "rejected")
                {
                    digestMatch = true;
                    // Recorded for the admin queue only; no owner or public DTO carries these.
                    check["matchedRecipeId"] = earlier.RecipeId.ToString();
                    check["matchedVersionId"] = earlier.Id.ToString();
                    break;
                }
            }
        }

        SubmitterRecord record = await SubmitterRecordAsync(submitter, ct);
        bool submitterHeld = record is SubmitterRecord.Soft or SubmitterRecord.Hard;
        bool paid = await IsPaidAsync(recipe.AuthorOrgId, ct);
        ListingResult decision = ListingDecision.Decide(text, previewVerdict, trusted, paid, recipe.HumanRejectedAt is not null || authorFlagged, digestMatch, submitterHeld, record == SubmitterRecord.OrgSoft);

        // A human owns a pending recipe: the system never lists it behind them (Pending to Listed is an admin move).
        if (decision.Outcome == ListingOutcome.Listed && recipe.Status == RecipeStatus.Pending)
            decision = new ListingResult(ListingOutcome.Pending, "awaiting_review");

        // The organization's live cap is re-checked at the moment a draft would become live, never only at submit.
        if (actionable && recipe.Status == RecipeStatus.Draft && decision.Outcome != ListingOutcome.Rejected)
        {
            int others = await CountLiveAsync(recipe.AuthorOrgId, recipe.Id, ct);
            if (others >= Options.MaxLivePerOrganization)
                decision = new ListingResult(ListingOutcome.Rejected, "live_limit");
        }

        Meta? meta = ReadMeta(check);
        string action;
        if (!actionable)
        {
            decision = new ListingResult(ListingOutcome.Rejected, "recipe_not_active");
            action = "version_ignored";
        }
        else if (newerExists)
        {
            // A newer version was submitted while this one was preparing: it never goes live or to review.
            decision = new ListingResult(ListingOutcome.Rejected, "superseded");
            check["superseded"] = true;
            action = "version_superseded";
        }
        else if (decision.Outcome == ListingOutcome.Rejected)
        {
            if (recipe.Status is RecipeStatus.Draft or RecipeStatus.Pending)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Rejected, RecipeActor.System, now);
            action = "version_rejected";
        }
        else if (decision.Outcome == ListingOutcome.Listed)
        {
            // Supersede the previous live version: the recipe points at the new one in the same save.
            if (meta is not null)
                ApplyMeta(recipe, meta);
            recipe.CurrentVersionId = version.Id;
            if (recipe.Status == RecipeStatus.Draft)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Listed, RecipeActor.System, now);
            recipe.EverListedAt ??= now;
            recipe.ListedAt ??= now;
            recipe.UpdatedAt = now;
            action = "version_listed";
        }
        else
        {
            if (recipe.Status == RecipeStatus.Draft)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Pending, RecipeActor.System, now);

            // The queue shows this version; CurrentVersionId and the recipe text change only when an admin approves it.
            action = "version_pending";
        }

        check["decision"] = decision.Outcome.ToString().ToLowerInvariant();
        check["reason"] = decision.Reason;
        version.CheckJson = check.ToJsonString();
        Audit(recipe.Id, version.Id, action, "system", null, decision.Reason, now);
        await db.SaveChangesAsync(ct);

        // The author hears (category only) when their version was refused by the automatic checks or its preview failed.
        if (notifier is not null && decision.Outcome == ListingOutcome.Rejected
            && decision.Reason switch { "preview_failed" or "preview_blocked" => "preview", "text_blocked" => "content", _ => null } is { } category)
            await notifier.NotifyAsync(recipe.Id, category, ct);

        return decision;
    }

    // ------------------------------------------------------------------ admin decisions on one version

    /// <summary>SHA-256 (hex) of the canonical snapshot and the manifest, the content identity of a version.</summary>
    public static string ContentDigest(string snapshotJson, string manifestJson) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshotJson + "\n" + manifestJson)));

    /// <summary>A hash of everything an admin judges: snapshot, manifest, the texts and the preview. Queue rows carry it.</summary>
    public static string VersionDigest(MarketplaceRecipeVersion version)
    {
        string meta = ParseCheck(version.CheckJson)["meta"]?.ToJsonString() ?? "";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\n', version.SnapshotJson, version.ManifestJson, meta, version.PreviewSha256 ?? "")));
        return Convert.ToHexStringLower(hash);
    }

    public Task<MarketplaceResult<PublishedRecipeResponse>> ApproveVersionAsync(
        Guid recipeId, Guid versionId, Guid actorUserId, string? expectedDigest, CancellationToken ct) =>
        DecideVersionAsync(recipeId, versionId, actorUserId, expectedDigest, approve: true, ct);

    public Task<MarketplaceResult<PublishedRecipeResponse>> RejectVersionAsync(
        Guid recipeId, Guid versionId, Guid actorUserId, string? expectedDigest, CancellationToken ct) =>
        DecideVersionAsync(recipeId, versionId, actorUserId, expectedDigest, approve: false, ct);

    private async Task<MarketplaceResult<PublishedRecipeResponse>> DecideVersionAsync(
        Guid recipeId, Guid versionId, Guid actorUserId, string? expectedDigest, bool approve, CancellationToken ct)
    {
        static MarketplaceResult<PublishedRecipeResponse> Fail(MarketplaceError e) => MarketplaceResult<PublishedRecipeResponse>.Fail(e);
        MarketplaceError stale = new(409, "STALE_VERSION", "This version changed or was replaced since it was queued. Reload the queue.");

        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.Include(v => v.Recipe)
            .FirstOrDefaultAsync(v => v.Id == versionId && v.RecipeId == recipeId, ct);
        if (version?.Recipe is not { } recipe)
            return Fail(new MarketplaceError(404));

        JsonObject check = ParseCheck(version.CheckJson);
        string? decided = check["decision"]?.GetValue<string>();
        string? reason = check["reason"]?.GetValue<string>();
        PublishedRecipeResponse Response() => new(recipe.Id, MarketplaceWire.Status(recipe.Status), version.N, MarketplaceWire.Preview(version.PreviewStatus));

        // Repeating a decision that was already made on exactly this version is a no-op.
        if (approve && recipe.CurrentVersionId == version.Id && recipe.Status == RecipeStatus.Listed && reason == "admin_approved")
            return MarketplaceResult<PublishedRecipeResponse>.Ok(Response());
        if (!approve && decided == "rejected" && reason == "admin_rejected")
            return MarketplaceResult<PublishedRecipeResponse>.Ok(Response());

        bool superseded = check["superseded"]?.GetValue<bool>() == true;
        bool newerExists = await db.MarketplaceRecipeVersions.AnyAsync(v => v.RecipeId == recipeId && v.N > version.N, ct);
        if (decided != "pending" || superseded || newerExists)
            return Fail(stale);
        if (expectedDigest is not null && !string.Equals(expectedDigest, VersionDigest(version), StringComparison.OrdinalIgnoreCase))
            return Fail(stale);
        if (recipe.Status is not (RecipeStatus.Pending or RecipeStatus.Listed))
            return Fail(new MarketplaceError(409, "INVALID_STATE", "This recipe is not waiting for a decision."));

        DateTime now = DateTime.UtcNow;
        if (approve)
        {
            if (ReadMeta(check) is { } meta)
                ApplyMeta(recipe, meta);
            recipe.CurrentVersionId = version.Id;
            if (recipe.Status == RecipeStatus.Pending)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Listed, RecipeActor.Admin, now);
            recipe.EverListedAt ??= now;
            recipe.ListedAt ??= now;
            recipe.UpdatedAt = now;
        }
        else
        {
            if (recipe.Status == RecipeStatus.Pending)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Rejected, RecipeActor.Admin, now);
            else
                recipe.HumanRejectedAt = now;
        }

        check["decision"] = approve ? "listed" : "rejected";
        check["reason"] = approve ? "admin_approved" : "admin_rejected";
        version.CheckJson = check.ToJsonString();
        Audit(recipe.Id, version.Id, approve ? "version_approved" : "version_rejected", "admin", actorUserId, approve ? "admin_approved" : "admin_rejected", now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail(ConcurrentUpdate);
        }

        return MarketplaceResult<PublishedRecipeResponse>.Ok(Response());
    }

    private Task<int> CountLiveAsync(Guid orgId, Guid? excludeRecipeId, CancellationToken ct) =>
        db.MarketplaceRecipes.AsNoTracking().CountAsync(r =>
            r.AuthorOrgId == orgId && r.Id != excludeRecipeId
            && (r.Status == RecipeStatus.Listed || r.Status == RecipeStatus.Pending
                || (r.Status == RecipeStatus.Draft && r.Versions.Any(v => v.PreviewStatus == PreviewStatus.Preparing))), ct);

    // ------------------------------------------------------------------ unpublish / mine

    public async Task<MarketplaceResult<PublishedRecipeResponse>> UnpublishAsync(Guid recipeId, CancellationToken ct)
    {
        ProjectAccessOutcome role = projectAccess.AuthorizeOrganization(ProjectPermission.Edit);
        if (role != ProjectAccessOutcome.Found)
            return MarketplaceResult<PublishedRecipeResponse>.Fail(AccessError(role));

        if (user.OrganizationId is not { } orgId)
            return MarketplaceResult<PublishedRecipeResponse>.Fail(new MarketplaceError(404));

        MarketplaceRecipe? recipe = await db.MarketplaceRecipes.FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (recipe is null || recipe.AuthorOrgId != orgId)
            return MarketplaceResult<PublishedRecipeResponse>.Fail(new MarketplaceError(404));

        DateTime now = DateTime.UtcNow;
        if (!RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Unpublished, RecipeActor.Author, now))
        {
            return MarketplaceResult<PublishedRecipeResponse>.Fail(
                new MarketplaceError(409, "INVALID_STATE", "This recipe cannot be unpublished in its current state."));
        }

        Audit(recipe.Id, recipe.CurrentVersionId, "unpublished", "user", user.UserId, "author_request", now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return MarketplaceResult<PublishedRecipeResponse>.Fail(ConcurrentUpdate);
        }

        MarketplaceRecipeVersion? current = recipe.CurrentVersionId is { } cv
            ? await db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == cv, ct)
            : null;
        return MarketplaceResult<PublishedRecipeResponse>.Ok(new PublishedRecipeResponse(
            recipe.Id, MarketplaceWire.Status(recipe.Status), current?.N ?? 0, MarketplaceWire.Preview(current?.PreviewStatus ?? PreviewStatus.Preparing)));
    }

    public async Task<MarketplaceResult<MineResponse>> MineAsync(CancellationToken ct)
    {
        ProjectAccessOutcome role = projectAccess.AuthorizeOrganization(ProjectPermission.View);
        if (role != ProjectAccessOutcome.Found)
            return MarketplaceResult<MineResponse>.Fail(AccessError(role));
        if (user.OrganizationId is not { } orgId)
            return MarketplaceResult<MineResponse>.Fail(new MarketplaceError(404));

        List<MarketplaceRecipe> recipes = await db.MarketplaceRecipes.AsNoTracking()
            .Where(r => r.AuthorOrgId == orgId).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        Guid[] ids = [.. recipes.Select(r => r.Id)];
        List<MarketplaceRecipeVersion> versions = await db.MarketplaceRecipeVersions.AsNoTracking()
            .Where(v => ids.Contains(v.RecipeId)).OrderBy(v => v.N).ToListAsync(ct);
        List<MarketplaceAuditEntry> audit = await db.MarketplaceAudit.AsNoTracking()
            .Where(a => a.RecipeId != null && ids.Contains(a.RecipeId.Value)).OrderByDescending(a => a.CreatedAt).ToListAsync(ct);

        List<MineRecipeDto> items = [];
        foreach (MarketplaceRecipe r in recipes)
        {
            List<MarketplaceRecipeVersion> mine = [.. versions.Where(v => v.RecipeId == r.Id)];
            MarketplaceRecipeVersion? current = mine.FirstOrDefault(v => v.Id == r.CurrentVersionId);
            MarketplaceRecipeVersion? latest = mine.LastOrDefault();
            // A reason is shown only where a decision against the author stands, and only as a public category.
            string? reason = r.Status is RecipeStatus.Rejected or RecipeStatus.Removed
                ? MarketplaceAuthorReasons.ToPublicCategory(audit.Where(a => a.RecipeId == r.Id).Select(a => ReasonOf(a.DetailJson)).FirstOrDefault(x => x is not null))
                : null;
            items.Add(new MineRecipeDto(
                r.Id, r.Name, r.Category, r.CostClass, MarketplaceWire.Status(r.Status), r.HiddenByReports, reason,
                current?.N, latest is null ? null : MarketplaceWire.Preview(latest.PreviewStatus),
                [.. mine.Select(v => new MineVersionDto(v.N, MarketplaceWire.Preview(v.PreviewStatus), v.CreatedAt))],
                r.CopyOrgs, r.RunCount,
                r.RatingCount >= Options.MinRatingsForAverage && r.RatingCount > 0 ? Math.Round((double)r.RatingSum / r.RatingCount, 2) : null,
                r.RatingCount, r.ListedAt, r.CreatedAt,
                current?.N ?? latest?.N ?? 0, r.CopyOrgs, r.RunCount, r.Description, r.Language, r.AuthorLabel ?? ""));
        }

        return MarketplaceResult<MineResponse>.Ok(new MineResponse(items));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Analysis(
        PortableRecipe? Recipe, string? Json, IReadOnlyList<RefusalDto> Refusals, string CostClass);

    private sealed record Meta(
        string Name, string Description, string Category, string Language, string AuthorLabel,
        string CostClass = "", int StepCount = 0, string[]? StepTypes = null);

    private async Task<Analysis?> AnalyzeAsync(Guid projectId, Guid workflowId, CancellationToken ct)
    {
        WorkflowDefinition? workflow = await db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == workflowId && w.ProjectId == projectId, ct);
        if (workflow is null)
            return null;

        List<Guid> agentIds = [.. workflow.Steps.Select(s => s.AgentDefinitionId)];
        foreach (WorkflowStep step in workflow.Steps.Where(s => !string.IsNullOrWhiteSpace(s.ParallelAgentIdsJson)))
        {
            try
            {
                agentIds.AddRange(JsonSerializer.Deserialize<List<Guid>>(step.ParallelAgentIdsJson!) ?? []);
            }
            catch (JsonException)
            {
                // The builder refuses a step whose agents it cannot resolve.
            }
        }

        Guid[] distinct = [.. agentIds.Distinct()];
        Dictionary<Guid, AgentDefinition> agents = await db.AgentDefinitions.AsNoTracking()
            .Where(a => distinct.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);

        IReadOnlyList<AppStepFacts> facts =
        [
            .. workflow.Steps.OrderBy(s => s.StepOrder).Select(s => AppStepFacts.FromStep(
                s, agents.TryGetValue(s.AgentDefinitionId, out AgentDefinition? a) && a.IsBuiltIn ? a.AgentType : null))
        ];
        string cost = AppCostClass.Derive(facts);

        PortableRecipeResult built = PortableRecipeBuilder.Build(workflow, [.. workflow.Steps], agents, AppManifestJson.TryParse(workflow.AppManifestJson));
        List<RefusalDto> refusals = [.. built.Refusals.Select(r => new RefusalDto(r.Code, r.StepOrder, r.Message))];
        if (built.Recipe is { } recipe && refusals.Count == 0)
        {
            foreach (string error in PortableRecipeValidator.Validate(recipe))
                refusals.Add(new RefusalDto("RECIPE_INVALID", null, error));
        }

        return new Analysis(refusals.Count == 0 ? built.Recipe : null, built.CanonicalJson, refusals, cost);
    }

    private async Task<(Meta?, MarketplaceError?)> ValidateFieldsAsync(PublishRecipeRequest request, CancellationToken ct)
    {
        static (Meta?, MarketplaceError?) Bad(string message) => (null, new MarketplaceError(400, "INVALID_FIELD", message));

        string name = OneLine(MarketplaceText.Sanitize(request.Name));
        if (name.Length is < 1 or > 80)
            return Bad("The name must be 1 to 80 characters of plain text.");
        string description = MarketplaceText.Sanitize(request.Description);
        if (description.Length is < 1 or > 1000)
            return Bad("The description must be 1 to 1000 characters of plain text.");
        string category = (request.Category ?? "").Trim().ToLowerInvariant();
        if (!AppManifestVocabulary.Categories.Contains(category))
            return Bad($"The category must be one of: {string.Join(", ", AppManifestVocabulary.Categories)}.");
        string language = (request.Language ?? "").Trim().ToLowerInvariant();
        if (!Languages.Contains(language))
            return Bad($"The language must be one of: {string.Join(", ", Languages)}.");

        string label;
        if (string.IsNullOrWhiteSpace(request.AuthorLabel))
        {
            string display = await db.ApplicationUsers.AsNoTracking().Where(u => u.Id == user.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";
            label = OneLine(MarketplaceText.Sanitize(display));
            if (label.Length == 0 || LooksLikeEmail().IsMatch(label))
                label = "ReelBolt member";
            if (label.Length > 60)
                label = label[..60].TrimEnd();
        }
        else
        {
            label = OneLine(MarketplaceText.Sanitize(request.AuthorLabel));
            if (label.Length is < 1 or > 60)
                return Bad("The author name must be 1 to 60 characters of plain text.");
            if (LooksLikeEmail().IsMatch(label) || LooksLikeEmail().IsMatch(request.AuthorLabel))
                return Bad("The author name cannot be an email address.");
        }

        foreach (string text in new[] { name, description, label })
        {
            if (SnapshotLeakGuard.Classify(text) is not null)
                return Bad("The name, description and author name cannot contain ids, links with keys, emails or secrets.");
        }

        return (new Meta(name, description, category, language, label), null);
    }

    private async Task<MarketplaceError?> CheckCapsAsync(MarketplaceRecipe? existing, Guid orgId, CancellationToken ct)
    {
        DateTime midnight = DateTime.UtcNow.Date;
        int today = await db.MarketplaceRecipeVersions.AsNoTracking()
            .CountAsync(v => v.SubmittedByUserId == user.UserId && v.CreatedAt >= midnight, ct);
        if (today >= Options.MaxPublishesPerUserPerDay)
        {
            return new MarketplaceError(429, "DAILY_PUBLISH_LIMIT",
                $"You can publish {Options.MaxPublishesPerUserPerDay} times a day. Try again tomorrow.");
        }

        bool alreadyCounted = existing is { Status: RecipeStatus.Listed or RecipeStatus.Pending or RecipeStatus.Draft };
        if (!alreadyCounted)
        {
            // Drafts whose preview is still being prepared count too: they are about to be live.
            if (await CountLiveAsync(orgId, null, ct) >= Options.MaxLivePerOrganization)
            {
                return new MarketplaceError(409, "LIVE_RECIPE_LIMIT",
                    $"An organization can have {Options.MaxLivePerOrganization} live recipes. Unpublish one first.");
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves <c>file:&lt;guid&gt;</c> or <c>step_result:&lt;guid&gt;</c> inside the project already authorized, to a storage
    /// reference kept only in <c>preview_source_json</c>. Null when malformed or not found there.
    /// </summary>
    private async Task<string?> ResolvePreviewSourceAsync(string? outputRef, Guid projectId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputRef))
            return null;

        // A project under a content quarantine or a CSAM output hold publishes nothing.
        var hold = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.ContentQuarantinedAt, p.CsamOutputHoldAt }).FirstOrDefaultAsync(ct);
        if (hold is null || hold.ContentQuarantinedAt is not null || hold.CsamOutputHoldAt is not null)
            return null;

        string token = outputRef.Trim();
        if (token.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(token["file:".Length..], out Guid fileId))
        {
            var file = await db.ProjectFiles.AsNoTracking()
                .Where(f => f.Id == fileId && f.ProjectId == projectId)
                .Select(f => new { f.StorageBucket, f.StorageKey, f.MimeType, f.CsamStatus, f.ModerationStatus })
                .FirstOrDefaultAsync(ct);
            if (file is null || string.IsNullOrEmpty(file.StorageKey) || !file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                return null;

            // Only a file whose scans are done and clean (or never scanned because the scanner is off) can be a public preview.
            if (file.CsamStatus is CsamScanStatus.Quarantined or CsamScanStatus.Pending or CsamScanStatus.Scanning
                || file.ModerationStatus is FileModerationStatus.Quarantined or FileModerationStatus.Scanning)
                return null;
            return new JsonObject { ["kind"] = "file", ["bucket"] = file.StorageBucket, ["key"] = file.StorageKey }.ToJsonString();
        }

        if (token.StartsWith("step_result:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(token["step_result:".Length..], out Guid resultId))
        {
            string? key = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == resultId && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null)
                .Select(r => r.OutputStorageKey).FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(key))
                return null;

            // The output must be a video, judged by its extension the way OutputsController does.
            string extension = Path.GetExtension(key).ToLowerInvariant();
            return extension is ".mp4" or ".webm" or ".mov" ? new JsonObject { ["kind"] = "step_result", ["key"] = key }.ToJsonString() : null;
        }

        return null;
    }

    /// <summary>
    /// The submitter's own record, read past the tenant filter. HARD: a project the submitter owns is under a CSAM output hold, or holds a
    /// CSAM-quarantined file (the publish is refused with no hint, and any version goes to a human). SOFT: a project the submitter owns
    /// has a plain content-moderation quarantine (never cleared, so it cannot block; it only routes the version to a human). A teammate's
    /// project does not count: a clean member of the organization is not held for someone else's record. The SOURCE project's own holds
    /// are checked separately when the preview source is resolved.
    /// </summary>
    private async Task<SubmitterRecord> SubmitterRecordAsync(Guid userId, CancellationToken ct)
    {
        bool hard = await db.Projects.IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.OwnerId == userId && p.CsamOutputHoldAt != null, ct)
            || await MarketplaceSafetyService.HasCsamMatchAsync(db, userId, ct)
            || await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking().AnyAsync(
                f => f.CsamStatus == CsamScanStatus.Quarantined && db.Projects.IgnoreQueryFilters().Any(p => p.Id == f.ProjectId && p.OwnerId == userId), ct);
        if (hard)
            return SubmitterRecord.Hard;
        if (await db.Projects.IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.OwnerId == userId && p.ContentQuarantinedAt != null, ct))
            return SubmitterRecord.Soft;

        // Any project the submitter can edit (Owner, Admin or Member of its organization; Billing cannot) with a CSAM hold, an
        // unresolved CSAM quarantine or a content quarantine: the version waits for a human. No refusal and no hint.
        Guid[] editableOrgs = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Role != OrganizationRole.Billing).Select(m => m.OrganizationId).ToArrayAsync(ct);
        bool orgHeld = editableOrgs.Length > 0 && (
            await db.Projects.IgnoreQueryFilters().AsNoTracking().AnyAsync(
                p => editableOrgs.Contains(p.OrganizationId) && (p.CsamOutputHoldAt != null || p.ContentQuarantinedAt != null), ct)
            || await db.ProjectFiles.IgnoreQueryFilters().AsNoTracking().AnyAsync(
                f => f.CsamStatus == CsamScanStatus.Quarantined
                    && db.Projects.IgnoreQueryFilters().Any(p => p.Id == f.ProjectId && editableOrgs.Contains(p.OrganizationId)), ct));
        return orgHeld ? SubmitterRecord.OrgSoft : SubmitterRecord.Clean;
    }

    private enum SubmitterRecord
    {
        Clean,
        Soft,
        OrgSoft,
        Hard
    }

    private async Task<bool> IsPaidAsync(Guid organizationId, CancellationToken ct)
    {
        try
        {
            // Billing disabled returns null, and so does any read failure: not paid.
            CreditBalanceSnapshot? snapshot = await credits.GetAsync(organizationId, ct);
            return snapshot?.Plan is { } plan && PaidPlans.Contains(plan);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Plan lookup failed for a marketplace listing decision; treating the organization as not paid");
            return false;
        }
    }

    private static string BuildCheckJson(TextCheckResult check, Meta meta) =>
        new JsonObject
        {
            ["text"] = check.Verdict.ToString().ToLowerInvariant(),
            ["fields"] = new JsonArray([.. check.Fields.Select(f => (JsonNode)new JsonObject
            {
                ["field"] = f.Field,
                ["verdict"] = f.Verdict.ToString().ToLowerInvariant()
            })]),
            ["meta"] = JsonSerializer.SerializeToNode(meta, PortableRecipeJson.Options)
        }.ToJsonString();

    private static JsonObject ParseCheck(string? json)
    {
        try
        {
            return JsonNode.Parse(json ?? "{}") as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Meta? ReadMeta(JsonObject check) =>
        check["meta"] is JsonObject m ? m.Deserialize<Meta>(PortableRecipeJson.Options) : null;

    private static void ApplyMeta(MarketplaceRecipe recipe, Meta meta)
    {
        recipe.Name = meta.Name;
        recipe.Description = meta.Description;
        recipe.Category = meta.Category;
        recipe.Language = meta.Language;
        recipe.AuthorLabel = meta.AuthorLabel;
        recipe.CostClass = meta.CostClass;
        recipe.StepCount = meta.StepCount;
        recipe.StepTypes = meta.StepTypes ?? [];
    }

    private static string? ReasonOf(string? detailJson)
    {
        try
        {
            return (JsonNode.Parse(detailJson ?? "{}") as JsonObject)?["reason"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Audit(Guid? recipeId, Guid? versionId, string action, string actorKind, Guid? actorUserId, string reason, DateTime now) =>
        db.MarketplaceAudit.Add(new MarketplaceAuditEntry
        {
            Id = Guid.NewGuid(),
            RecipeId = recipeId,
            VersionId = versionId,
            Action = action,
            ActorKind = actorKind,
            ActorUserId = actorUserId,
            DetailJson = new JsonObject { ["reason"] = reason }.ToJsonString(),
            CreatedAt = now
        });

    private static MarketplaceError AccessError(ProjectAccessOutcome outcome) =>
        new(outcome == ProjectAccessOutcome.Forbidden ? 403 : 404);

    private static MarketplaceError Refused(IReadOnlyList<RefusalDto> refusals) =>
        new(422, refusals[0].Code, refusals[0].Message, refusals);

    private static string OneLine(string text) => string.Join(' ', text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The bounds a copier should see on a step: how many rounds a review loop may run and any credit cap.</summary>
    internal static IReadOnlyList<string> LimitsOf(PortableStep step)
    {
        List<string> limits = [];
        if (step.StepType == StepType.ReviewLoop)
        {
            limits.Add($"Review loop, up to {step.MaxIterations} rounds");
            if (step.MinScore is { } pass)
                limits.Add($"Pass mark {pass} out of 10");
        }

        if (step.Config is JsonObject config)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in config)
            {
                if (string.Equals(kv.Key, "maxSpendCredits", StringComparison.OrdinalIgnoreCase)
                    && kv.Value is JsonValue v && v.TryGetValue(out double credits))
                    limits.Add($"Spend limited to {credits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} credits");
            }
        }

        return limits;
    }

    private static string Humanize(string pascal) => WordBoundary().Replace(pascal, " ");
}
