using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// What happens when the engine reports a preview. On failure the version is marked failed (the author sees "preview failed, try
/// another video"). On success, in this order and on the new object bytes: the SHA-256 the engine reported, the CSAM control, the
/// content check; then <see cref="MarketplacePublishService.FinalizeVersionAsync"/> decides the listing. Idempotent: a version that
/// is no longer preparing is left alone.
/// </summary>
public sealed class MarketplacePreviewFinalizer(
    InferenceApiDbContext db,
    MarketplacePublishService publish,
    IMarketplacePreviewObjects objects,
    IMarketplacePreviewCsamGate csam,
    IMarketplacePreviewModerator moderator,
    ILogger<MarketplacePreviewFinalizer> logger,
    MarketplaceSafetyService? safety = null)
{
    public async Task HandleAsync(MarketplacePreviewFinished message, CancellationToken ct)
    {
        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.Include(v => v.Recipe)
            .FirstOrDefaultAsync(v => v.Id == message.VersionId, ct);
        if (version?.Recipe is not { } recipe || version.RecipeId != message.RecipeId)
            return;

        // A result of a superseded run (or for a version that is no longer preparing) changes nothing; its staging objects go.
        if (version.PreviewStatus != PreviewStatus.Preparing
            || !MarketplacePreviewKeys.IsValidNonce(message.RunNonce)
            || !string.Equals(version.PreviewRunNonce, message.RunNonce, StringComparison.Ordinal))
        {
            // A failed version (evidence may be staged there) is never touched by a redelivered or late message.
            if (version.PreviewStatus != PreviewStatus.Failed && MarketplacePreviewKeys.IsValidNonce(message.RunNonce))
                await TryDeleteStagingAsync(recipe.Id, version.Id, message.RunNonce!, version.CheckJson, ct);
            return;
        }

        string nonce = message.RunNonce!;
        string stagedPreview = MarketplacePreviewKeys.StagingPreview(recipe.Id, version.Id, nonce);
        string stagedPoster = MarketplacePreviewKeys.StagingPoster(recipe.Id, version.Id, nonce);
        string previewKey = MarketplacePreviewKeys.Preview(recipe.Id, version.Id);
        string posterKey = MarketplacePreviewKeys.Poster(recipe.Id, version.Id);

        if (!message.Succeeded)
        {
            await FailAsync(version, recipe.Id, message.FailureCode ?? MarketplacePreviewFailureCodes.Internal, ct);
            return;
        }

        if (!string.Equals(message.PreviewKey, stagedPreview, StringComparison.Ordinal)
            || !string.Equals(message.PosterKey, stagedPoster, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(message.PreviewSha256) || message.Seconds is not > 0)
        {
            await FailAsync(version, recipe.Id, "invalid_result", ct);
            return;
        }

        if (!await ShaMatchesAsync(stagedPreview, message.PreviewSha256, ct))
        {
            await FailAsync(version, recipe.Id, "sha_mismatch", ct);
            return;
        }

        JsonObject check = ParseCheck(version.CheckJson);
        JsonObject preview = new();

        PreviewCsamResult scan = await csam.ScanAsync(stagedPreview, stagedPoster, ct);
        preview["csam"] = scan.ToString().ToLowerInvariant();
        if (scan == PreviewCsamResult.Match)
        {
            await QuarantineAsync(version, recipe, stagedPreview, stagedPoster, check, preview, ct);
            return;
        }

        if (scan == PreviewCsamResult.Unavailable)
        {
            await FailAsync(version, recipe.Id, "csam_scan_unavailable", ct);
            return;
        }

        PreviewModerationResult moderation = await moderator.CheckAsync(stagedPreview, recipe.AuthorOrgId, ct);
        preview["moderation"] = moderation.Outcome.ToString().ToLowerInvariant();
        if (moderation.Reason is not null)
            preview["moderationReason"] = moderation.Reason;

        PreviewVerdict verdict = moderation.Outcome switch
        {
            PreviewModerationOutcome.Allow => PreviewVerdict.Allow,
            PreviewModerationOutcome.Block => PreviewVerdict.Block,

            // Review, and Unavailable (off, no provider, could not judge): a human decides, never auto-listed.
            _ => PreviewVerdict.Review
        };

        // With the CSAM control off nothing scanned these bytes: never auto-list, a human decides.
        if (scan == PreviewCsamResult.NotConfigured && verdict == PreviewVerdict.Allow)
        {
            verdict = PreviewVerdict.Review;
            preview["unscanned"] = true;
        }

        if (verdict == PreviewVerdict.Block)
        {
            await TryDeleteStagingAsync(recipe.Id, version.Id, nonce, version.CheckJson, ct);
        }
        else
        {
            // Only now, after the scan and the content check of the staged bytes, are they copied to the final immutable key
            // by the API; the copy is re-hashed so what is recorded is what was copied.
            string? posterSha = await HashAsync(stagedPoster, ct);
            if (posterSha is null
                || !await CopyCheckedAsync(stagedPreview, previewKey, message.PreviewSha256, ct)
                || !await TryCopyAsync(stagedPoster, posterKey, ct)
                || !await ShaMatchesAsync(posterKey, posterSha, ct))
            {
                await TryDeleteAsync(previewKey, posterKey, ct);
                await FailAsync(version, recipe.Id, "copy_failed", ct);
                return;
            }

            await TryDeleteStagingAsync(recipe.Id, version.Id, nonce, version.CheckJson, ct);
            version.PreviewKey = previewKey;
            version.PosterKey = posterKey;
            version.PreviewSha256 = message.PreviewSha256;
            version.PosterSha256 = posterSha;
            version.PreviewSeconds = message.Seconds;
        }

        preview["verdict"] = verdict.ToString().ToLowerInvariant();
        check["preview"] = preview;
        version.CheckJson = check.ToJsonString();
        await db.SaveChangesAsync(ct);
        await publish.FinalizeVersionAsync(version.Id, verdict, ct);
    }

    private async Task<bool> ShaMatchesAsync(string key, string expected, CancellationToken ct) =>
        string.Equals(await HashAsync(key, ct), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task<string?> HashAsync(string key, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await objects.OpenReadAsync(key, ct);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A marketplace preview object could not be read back");
            return null;
        }
    }

    private async Task FailAsync(MarketplaceRecipeVersion version, Guid recipeId, string code, CancellationToken ct)
    {
        if (MarketplacePreviewKeys.IsValidNonce(version.PreviewRunNonce))
            await TryDeleteStagingAsync(recipeId, version.Id, version.PreviewRunNonce!, version.CheckJson, ct);

        JsonObject check = ParseCheck(version.CheckJson);
        check["previewFailure"] = code;
        version.CheckJson = check.ToJsonString();
        version.PreviewStatus = PreviewStatus.Failed;
        db.MarketplaceAudit.Add(Audit(recipeId, version.Id, "preview_failed", code));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A positive match. The staged preview is MOVED to the evidence prefix; if the move fails nothing is deleted (the staged object
    /// is the evidence, left in place and flagged). The version ends terminal Failed, the recipe is taken down at once (a listed one
    /// becomes Removed with reason safety), <c>HumanRejectedAt</c> is set so the author never auto-lists again, and the source project
    /// gets the CSAM output hold and the content quarantine marker. Quarantined keys are never deleted by any path.
    /// </summary>
    private async Task QuarantineAsync(
        MarketplaceRecipeVersion version, MarketplaceRecipe recipe, string previewKey, string posterKey,
        JsonObject check, JsonObject preview, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        logger.LogError("A marketplace preview matched the CSAM control: recipe {RecipeId} version {VersionId}", recipe.Id, version.Id);

        // STEP 1, its own save, BEFORE any copy: the evidence is recorded as staged (so no delete path touches it, whatever happens
        // next), the version fails, the recipe is taken down and the source project is held. Retried on a concurrency conflict.
        string[] staged = [previewKey, posterKey];
        check["preview"] = preview;
        check["previewFailure"] = "csam_match";
        check[MarketplaceEvidenceGuard.EvidenceKey] = MarketplaceEvidenceGuard.Staged;
        check[MarketplaceEvidenceGuard.KeysKey] = new JsonArray([.. staged.Select(k => (JsonNode?)JsonValue.Create(k))]);
        db.MarketplaceAudit.Add(Audit(recipe.Id, version.Id, MarketplaceSafetyService.CsamMatchAudit, "csam_match"));
        Guid? sourceProject = SourceProjectId(version.PreviewSourceJson);
        if (sourceProject is { } projectId)
        {
            await ProjectContentQuarantineMarker.StageCsamAsync(db, projectId, now, ct);
            await ProjectContentQuarantineMarker.StageAsync(db, projectId, now, ct);
        }

        await SaveWithRetryAsync(() =>
        {
            if (recipe.Status == RecipeStatus.Listed)
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Removed, RecipeActor.System, now);
            else
                RecipeStatusMachine.TryTransition(recipe, RecipeStatus.Rejected, RecipeActor.System, now);
            recipe.HumanRejectedAt ??= now;
            recipe.RemovedReason = "safety";
            recipe.UpdatedAt = now;
            version.CheckJson = check.ToJsonString();
            version.PreviewStatus = PreviewStatus.Failed;
        }, ct);

        // STEP 2: BOTH objects go to quarantine/ (the scan may have matched either). A source is deleted only after its quarantine
        // copy is verified present with the same hash; anything not verified stays where it is, already recorded as held evidence.
        List<string> unverified = [];
        if (!await QuarantineOneAsync(previewKey, MarketplacePreviewKeys.QuarantinedPreview(recipe.Id, version.Id), ct))
            unverified.Add(previewKey);
        if (!await QuarantineOneAsync(posterKey, MarketplacePreviewKeys.QuarantinedPoster(recipe.Id, version.Id), ct))
            unverified.Add(posterKey);

        // STEP 3: everything verified moves the marker to quarantined; otherwise it keeps naming exactly the unverified keys.
        if (unverified.Count == 0)
        {
            check[MarketplaceEvidenceGuard.EvidenceKey] = MarketplaceEvidenceGuard.Quarantined;
            check.Remove(MarketplaceEvidenceGuard.KeysKey);
        }
        else
        {
            check[MarketplaceEvidenceGuard.KeysKey] = new JsonArray([.. unverified.Select(k => (JsonNode?)JsonValue.Create(k))]);
        }

        await SaveWithRetryAsync(() => version.CheckJson = check.ToJsonString(), ct);

        // The submitter's other recipes in any workspace come down now rather than at the next sweep.
        if (safety is not null)
        {
            try
            {
                await safety.RunOnceAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The safety takedown after a CSAM match failed; the sweeper repeats it");
            }
        }
    }

    /// <summary>Applies <paramref name="mutate"/> and saves; on a concurrency conflict reloads the conflicting rows and applies it again (bounded).</summary>
    private async Task SaveWithRetryAsync(Action mutate, CancellationToken ct)
    {
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            mutate();
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < attempts)
            {
                logger.LogWarning(ex, "Saving the CSAM response lost a concurrent update (attempt {Attempt}); re-reading and retrying", attempt);
                foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in ex.Entries)
                {
                    if (entry.State == EntityState.Modified || entry.State == EntityState.Deleted)
                        await entry.ReloadAsync(ct);
                }

                await ReconcileAddedRowsAsync(ct);
            }
        }
    }

    /// <summary>
    /// A failed save leaves its inserts tracked as Added. PostgreSQL rolls the whole save back, so the rows are absent and must be
    /// inserted again; a provider that does not roll back (the in-memory one) may already hold them, and inserting twice would fail
    /// on the key. For each tracked insert: if the row already exists in the store keep that copy, otherwise insert it again.
    /// </summary>
    private async Task ReconcileAddedRowsAsync(CancellationToken ct)
    {
        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            Microsoft.EntityFrameworkCore.Metadata.IKey? key = entry.Metadata.FindPrimaryKey();
            if (key is null)
                continue;

            object?[] values = [.. key.Properties.Select(p => entry.Property(p.Name).CurrentValue)];
            if (values.Any(v => v is null || Equals(v, DefaultOf(v))))
                continue; // a store-generated key: nothing could have been written under it

            object entity = entry.Entity;
            entry.State = EntityState.Detached;
            if (await db.FindAsync(entity.GetType(), values!, ct) is null)
                db.Add(entity);
        }
    }

    private static object? DefaultOf(object? v) => v is { } x && x.GetType().IsValueType ? Activator.CreateInstance(x.GetType()) : null;

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> (3 tries), verifies the copy by hash, then deletes the source. False leaves the source in place.</summary>
    private async Task<bool> QuarantineOneAsync(string source, string destination, CancellationToken ct)
    {
        string? expected = await HashAsync(source, ct);
        if (expected is null)
        {
            logger.LogCritical("EVIDENCE: a CSAM-matched staged object could not be read; it was left in place");
            return false;
        }

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await objects.CopyAsync(source, destination, ct);
                if (await ShaMatchesAsync(destination, expected, ct))
                {
                    await objects.DeleteAsync([source], ct);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Copying CSAM evidence to quarantine failed (attempt {Attempt})", attempt);
            }
        }

        logger.LogCritical("EVIDENCE: copying a CSAM-matched object to quarantine could not be verified; the staged object was left in place and must be preserved");
        return false;
    }

    internal static Guid? SourceProjectId(string? previewSourceJson)
    {
        string? key = MassTransitMarketplacePreviewDispatcher.ReadKey(previewSourceJson);
        if (!MarketplacePreviewKeys.IsProjectSourceKey(key))
            return null;
        const string prefix = "projects/";
        int slash = key!.IndexOf('/', prefix.Length);
        return Guid.TryParse(key[prefix.Length..slash], out Guid id) ? id : null;
    }

    private async Task TryDeleteStagingAsync(Guid recipeId, Guid versionId, string nonce, string? checkJson, CancellationToken ct)
    {
        // Held evidence is never deleted (MarketplaceEvidenceGuard contract).
        if (MarketplaceEvidenceGuard.IsHeld(checkJson))
            return;
        try
        {
            await objects.DeletePrefixAsync(MarketplacePreviewKeys.StagingPrefix(recipeId, versionId, nonce), ct, MarketplaceEvidenceGuard.HeldKeys(checkJson));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleting the staged preview objects of version {VersionId} failed; the sweeper removes them", versionId);
        }
    }

    private async Task TryDeleteAsync(string previewKey, string posterKey, CancellationToken ct)
    {
        try
        {
            await objects.DeleteAsync([previewKey, posterKey], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Deleting a half-copied marketplace preview failed");
        }
    }

    private async Task<bool> TryCopyAsync(string from, string to, CancellationToken ct)
    {
        try
        {
            await objects.CopyAsync(from, to, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Copying a checked marketplace preview object failed");
            return false;
        }
    }

    private async Task<bool> CopyCheckedAsync(string from, string to, string expectedSha, CancellationToken ct) =>
        await TryCopyAsync(from, to, ct) && await ShaMatchesAsync(to, expectedSha, ct);

    private static MarketplaceAuditEntry Audit(Guid recipeId, Guid versionId, string action, string reason) => new()
    {
        Id = Guid.NewGuid(),
        RecipeId = recipeId,
        VersionId = versionId,
        Action = action,
        ActorKind = "system",
        DetailJson = new JsonObject { ["reason"] = reason }.ToJsonString(),
        CreatedAt = DateTime.UtcNow
    };

    internal static JsonObject ParseCheck(string? json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }
}
