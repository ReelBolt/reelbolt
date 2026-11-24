using ReelBolt.Shared.Storage;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Account;

/// <summary>What one account's purge did. <see cref="Purged"/> false means "deferred to the next sweep".</summary>
public sealed record AccountPurgeUserResult(
    Guid UserId,
    bool Purged,
    int Projects,
    int ObjectsDeleted,
    int CollectionsDeleted,
    int SubscriptionsCancelled,
    int SubscriptionsRetainedForVendor,
    IReadOnlyList<string> Failures);

/// <summary>What one sweep did, across every due account.</summary>
public sealed record AccountPurgeRunResult(
    int UsersConsidered,
    int UsersPurged,
    int UsersDeferred,
    int ProjectsPurged,
    int ObjectsDeleted,
    int CollectionsDeleted,
    int SubscriptionsCancelled,
    int SubscriptionsRetainedForVendor);

/// <summary>
/// C9b's purge: after the 30-day grace period an account that asked to be deleted loses its content
/// and its rows, following the order C0 fixes — cancel the subscription, delete vendor-held voice
/// models, delete objects, delete Qdrant collections, delete the database rows children-first, then
/// the user. Two things are deliberately <b>kept</b>: <c>usage_events</c> rows (anonymised: the
/// <c>user_id</c> is nulled, the org-scoped bookkeeping stays) and <c>trial_claims</c> (a SHA-256
/// only, so deleting an account does not re-claim the trial).
/// <para>
/// <b>Why the objects and not just the rows.</b> The sweep lists <c>projects/{id}/</c> in the object
/// store and deletes by prefix, so an object no row points at any more (an orphan from an earlier
/// failure) goes too, exactly as C0 requires.
/// </para>
/// <para>
/// <b>Resumable by construction.</b> The user row is the worklist: every step is idempotent, and the
/// row is deleted only when the content steps all succeeded, so a failure — a vendor outage, a store
/// hiccup — leaves the account due and the whole purge is retried on the next sweep rather than
/// skipped. That is C0's "records progress on the user row" without a progress column: what is
/// already gone simply is not there to delete again.
/// </para>
/// <para>
/// <b>And nothing in the database is touched until the whole purge can finish.</b> The content steps
/// (the vendor models, the objects, the vectors) all run before a single row is deleted, and every
/// row this sweep deletes — children first, then the memberships, the workspace and the user — goes
/// in <b>one</b> <c>SaveChangesAsync</c>, i.e. one transaction. Both halves of that matter: the
/// memberships this purge removes are also the only handle it has on the workspace, so committing
/// them ahead of the rest would leave a retry that resolves no workspace, no project and no object,
/// deletes the user row anyway and reports success over stranded content (C11 F-2). A failure at any
/// point therefore leaves the account <i>exactly</i> as it was, still due. The one deliberate
/// exception is phase A's subscription cancellation, which is its own write and is meant to survive
/// (see <b>Billing</b>).
/// </para>
/// <para>
/// <b>Billing.</b> Cancelling the subscription happens as soon as the deletion is <i>requested</i>
/// (the same sweep sees <c>DeletionRequestedAt</c>), not 30 days later, so nobody pays for a month of
/// a workspace they have left. The provider-side half is B9b's (see
/// <see cref="AccountBillingCanceller"/>).
/// </para>
/// </summary>
public sealed class AccountPurgeService : BackgroundService
{
    /// <summary>Objects per S3 multi-object delete (the hard API limit).</summary>
    public const int MaxDeleteBatchSize = 1000;

    /// <summary>The organization name a purged workspace is left with: the user's own name must not outlive them.</summary>
    public const string AnonymisedOrganizationName = "Deleted workspace";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AccountOptions _options;
    private readonly bool _cloudTenancy;
    private readonly string _bucket;
    private readonly ILogger<AccountPurgeService> _logger;

    public AccountPurgeService(
        IServiceScopeFactory scopeFactory, IOptions<AccountOptions> options, IConfiguration configuration, ILogger<AccountPurgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _cloudTenancy = string.Equals(configuration["Tenancy:Mode"], "Cloud", StringComparison.OrdinalIgnoreCase);
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    private AccountPurgeOptions Options => _options.Purge;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
            return;

        // Let the API finish migrating/seeding before the first sweep.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                AccountPurgeRunResult run = await RunOnceAsync(DateTime.UtcNow, stoppingToken);
                if (run.UsersPurged > 0 || run.UsersDeferred > 0)
                {
                    _logger.LogInformation(
                        "Account purge swept {Considered} due account(s): {Purged} purged, {Deferred} deferred, "
                        + "{Projects} project(s), {Objects} object(s), {Collections} vector collection(s), "
                        + "{Cancelled} subscription(s) cancelled, {Retained} needing the billing provider",
                        run.UsersConsidered, run.UsersPurged, run.UsersDeferred, run.ProjectsPurged,
                        run.ObjectsDeleted, run.CollectionsDeleted, run.SubscriptionsCancelled, run.SubscriptionsRetainedForVendor);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Account purge sweep failed; it is retried on the next interval");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, Options.IntervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One sweep, exposed for tests.</summary>
    public async Task<AccountPurgeRunResult> RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!Options.Enabled)
            return new AccountPurgeRunResult(0, 0, 0, 0, 0, 0, 0, 0);

        List<Guid> due = await DueUsersAsync(nowUtc, ct);
        int purged = 0;
        int deferred = 0;
        int projects = 0;
        int objects = 0;
        int collections = 0;
        int cancelled = 0;
        int retained = 0;

        foreach (Guid userId in due)
        {
            try
            {
                AccountPurgeUserResult result = await PurgeUserAsync(userId, nowUtc, ct);
                projects += result.Projects;
                objects += result.ObjectsDeleted;
                collections += result.CollectionsDeleted;
                cancelled += result.SubscriptionsCancelled;
                retained += result.SubscriptionsRetainedForVendor;
                if (result.Purged)
                    purged++;
                else
                    deferred++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One account's failure must not stop the sweep: the row is still due, so the next
                // sweep retries the whole purge for it.
                deferred++;
                _logger.LogError(ex, "Purging account {UserId} failed; it stays due and is retried on the next sweep", userId);
            }
        }

        return new AccountPurgeRunResult(due.Count, purged, deferred, projects, objects, collections, cancelled, retained);
    }

    /// <summary>
    /// Accounts this sweep must act on: those whose 30-day grace has elapsed, and — when the cloud
    /// setting is on — those that never verified their address. A deletion request whose grace is
    /// still running is included as well, because the subscription is cancelled the moment the
    /// request is seen (see the type's doc comment).
    /// </summary>
    private async Task<List<Guid>> DueUsersAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

        using IDisposable system = tenant.System();
        List<Guid> requested = await db.ApplicationUsers.AsNoTracking()
            .Where(u => u.DeletionRequestedAt != null && !u.IsServiceAccount)
            .OrderBy(u => u.DeletionRequestedAt)
            .Select(u => u.Id)
            .Take(Math.Max(1, Options.MaxPerSweep))
            .ToListAsync(ct);

        if (!Options.PurgeUnverified || !_cloudTenancy)
            return requested;

        DateTime unverifiedCutoff = nowUtc.AddDays(-Math.Max(1, Options.UnverifiedDays));
        List<Guid> unverified = await db.ApplicationUsers.AsNoTracking()
            .Where(u => u.EmailVerifiedAt == null && u.DeletionRequestedAt == null && !u.IsServiceAccount && u.CreatedAt <= unverifiedCutoff)
            .OrderBy(u => u.CreatedAt)
            .Select(u => u.Id)
            .Take(Math.Max(1, Options.MaxPerSweep))
            .ToListAsync(ct);

        requested.AddRange(unverified);
        return requested;
    }

    /// <summary>
    /// The two phases for one account. Phase A (the billing stop) runs on every sweep that sees the
    /// account; phase B (objects, vectors, rows, the user) only once the grace has elapsed.
    /// </summary>
    public async Task<AccountPurgeUserResult> PurgeUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        var vectors = scope.ServiceProvider.GetRequiredService<IVectorIndexService>();
        var billing = scope.ServiceProvider.GetRequiredService<IAccountBillingCanceller>();
        var providers = scope.ServiceProvider.GetRequiredService<IInferenceProviderResolver>();
        var speech = scope.ServiceProvider.GetRequiredService<ISpeechSynthesisClientFactory>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();
        IMarketingSuppressionHasher? suppression = scope.ServiceProvider.GetService<IMarketingSuppressionHasher>();
        ISecretProtector? protector = scope.ServiceProvider.GetService<ISecretProtector>();
        ReelBolt.Inference.Api.Services.Avatars.IAvatarVendorAssetDeleter? avatarVendor =
            scope.ServiceProvider.GetService<ReelBolt.Inference.Api.Services.Avatars.IAvatarVendorAssetDeleter>();

        using IDisposable system = tenant.System();

        ApplicationUser? user = await db.ApplicationUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return new AccountPurgeUserResult(userId, true, 0, 0, 0, 0, 0, []);

        // A service account (the hidden user behind an API key) is never a purge subject: it goes with its
        // workspace (DeleteRowsAsync), never on its own. Deferred, never deleted, from here.
        if (user.IsServiceAccount)
            return new AccountPurgeUserResult(userId, false, 0, 0, 0, 0, 0, []);

        List<Organization> orgs = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Organization)
            .ToListAsync(ct);

        // Which workspaces go with the account: the user's own Personal workspace, and a Team
        // workspace they are the only member of. A Team workspace with other people in it stays.
        List<Organization> purgeOrgs = [];
        foreach (Organization org in orgs)
        {
            bool soleMember = await db.OrganizationMemberships.AsNoTracking()
                .CountAsync(m => m.OrganizationId == org.Id && !m.User.IsServiceAccount, ct) <= 1;
            if (org.Kind == OrganizationKind.Personal || soleMember)
                purgeOrgs.Add(org);
        }

        Guid[] purgeOrgIds = purgeOrgs.Select(o => o.Id).ToArray();
        List<Project> projects = await db.Projects.AsNoTracking().IgnoreQueryFilters()
            .Where(p => purgeOrgIds.Contains(p.OrganizationId))
            .ToListAsync(ct);
        Guid[] projectIds = projects.Select(p => p.Id).ToArray();

        // --- Phase A: the billing stop. ---
        int cancelled = 0;
        int retained = 0;
        foreach (Organization org in purgeOrgs)
        {
            AccountBillingCancellationResult result = await billing.CancelForOrganizationAsync(org.Id, ct);
            cancelled += result.Cancelled;
            retained += result.RetainedForVendor;
        }

        List<WorkflowExecution> executions = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId))
            .Select(e => new WorkflowExecution { Id = e.Id })
            .ToListAsync(ct);
        Guid[] executionIds = executions.Select(e => e.Id).ToArray();

        // --- Phase B only once the grace has elapsed. ---
        bool graceElapsed = user.DeletionRequestedAt is { } requested
            && requested <= nowUtc.AddDays(-Math.Max(0, Options.GraceDays));
        if (!graceElapsed && user.DeletionRequestedAt is not null)
            return new AccountPurgeUserResult(userId, false, 0, 0, 0, cancelled, retained, []);

        List<string> failures = [];

        // CSAM quarantine is a legal hold: no account or project purge may delete the evidence
        // object, the file row or its audit. The purge stays due until counsel closes the matter
        // (docs/csam.md), which is safer than destroying suspected illegal material.
        bool quarantinedEvidence = await db.ProjectFiles.IgnoreQueryFilters()
            .AnyAsync(f => projectIds.Contains(f.ProjectId) && f.CsamStatus == CsamScanStatus.Quarantined, ct);
        // The project's output marker defers the purge the same way, with or without a quarantined row; an
        // unreadable marker counts as present.
        var holdStates = (await ProjectOutputHold.ReadManyAsync(db, projectIds, ct)).Values.ToList();
        bool outputsKept = holdStates.Any(v => v.IsActive);
        if (quarantinedEvidence || outputsKept)
        {
            // Say only what is known: an unreadable marker is not evidence that anything is present.
            string reason = quarantinedEvidence
                ? "legal hold: quarantined CSAM evidence is present; resolve it with counsel before purging (docs/csam.md)"
                : holdStates.Any(v => v.Marker is not null)
                    ? "legal hold: a project output marker is set; resolve it with counsel before purging (docs/csam.md)"
                    : "purge deferred: a project output marker could not be read; retry once it can be (docs/csam.md)";
            return Deferred(userId, projectIds.Length, 0, 0, cancelled, retained, [reason]);
        }

        // 1. Vendor-held voice models first: deleting the row would lose the only handle on them.
        List<ProjectVoice> voices = await db.ProjectVoices.AsNoTracking().IgnoreQueryFilters()
            .Where(v => projectIds.Contains(v.ProjectId) && v.Source == ProjectVoiceSource.Cloned)
            .ToListAsync(ct);
        foreach (ProjectVoice voice in voices)
        {
            (bool deleted, string? note) = await ProjectVoicesController.DeleteRemoteModelAsync(voice, providers, speech, _logger, ct);
            if (!deleted && note is not null and not "provider_row_deleted" and not "library_voice_not_owned")
                failures.Add($"voice {voice.Id}: vendor model not deleted ({note})");
        }

        // 1b. Vendor-held avatar copies (docs/design/avatars.md), best effort like the voice models: deleting the rows
        // would lose the only handle on them. Phase 1 writes none; the check stays so phase 2 cannot forget it.
        List<ProjectAvatarVendorAsset> avatarAssets = await db.ProjectAvatarVendorAssets.AsNoTracking()
            .Where(v => v.DeletedAt == null && db.ProjectAvatars.IgnoreQueryFilters()
                .Any(a => a.Id == v.AvatarId && projectIds.Contains(a.ProjectId)))
            .ToListAsync(ct);
        foreach (ProjectAvatarVendorAsset asset in avatarAssets)
        {
            AvatarVendorAssetDeleteOutcome outcome = AvatarVendorAssetDeleteOutcome.NotSupported;
            try
            {
                if (avatarVendor is not null) outcome = await avatarVendor.DeleteAsync(asset, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Vendor deletion of avatar asset {AssetId} failed during the account purge", asset.Id);
                outcome = AvatarVendorAssetDeleteOutcome.VendorError;
            }

            // A refusal or an error is retried by deferring the purge, as for voice models; NotSupported (no deleter can
            // ever remove it) is logged for the operator and does not hold the account forever.
            if (outcome is AvatarVendorAssetDeleteOutcome.VendorRefused or AvatarVendorAssetDeleteOutcome.VendorError)
                failures.Add($"avatar asset {asset.Id}: vendor copy not deleted ({outcome})");
            else if (outcome == AvatarVendorAssetDeleteOutcome.NotSupported)
                _logger.LogWarning("Avatar asset {AssetId} ({RemoteAssetId}) cannot be deleted at its vendor; follow up by hand", asset.Id, asset.RemoteAssetId);
        }

        // 2. Every object under each project's prefix — orphans included.
        int objectsDeleted = 0;
        foreach (Guid projectId in projectIds)
        {
            try
            {
                (int deleted, int notDeleted) = await DeleteProjectObjectsAsync(s3, projectId, ct);
                objectsDeleted += deleted;
                // A batch the store answered with per-key errors is not a deleted prefix. Counting it
                // as a success would delete the rows that are the only map back to those objects and
                // report the account purged with the content still there — the same shape as F-2,
                // through the other door.
                if (notDeleted > 0)
                    failures.Add($"project {projectId}: {notDeleted} object(s) not deleted");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting the object-store prefix of project {ProjectId} failed", projectId);
                failures.Add($"project {projectId}: object prefix not deleted");
            }
        }

        // 2b. The workspace avatar library of each purged workspace (docs/design/avatar-objects.md): its objects live
        // under the ORGANIZATION prefix, outside every project prefix, so they are listed and deleted here.
        foreach (Organization org in purgeOrgs)
        {
            try
            {
                (int deleted, int notDeleted) = await DeleteAvatarLibraryObjectsAsync(s3, org.Id, ct);
                objectsDeleted += deleted;
                if (notDeleted > 0)
                    failures.Add($"workspace {org.Id}: {notDeleted} avatar library object(s) not deleted");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting the avatar library prefix of workspace {OrganizationId} failed", org.Id);
                failures.Add($"workspace {org.Id}: avatar library prefix not deleted");
            }
        }

        // 3. The project's vector collections (the embedded text of its files).
        int collectionsDeleted = 0;
        foreach (Guid projectId in projectIds)
        {
            try
            {
                collectionsDeleted += await vectors.DeleteProjectCollectionsAsync(projectId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting the vector collections of project {ProjectId} failed", projectId);
                failures.Add($"project {projectId}: vector collections not deleted");
            }
        }

        // 3. Marketplace recipes authored by the user or by a workspace that goes with them: their preview objects
        // live under the platform prefix marketplace/{recipeId}/, listed rather than trusted to the rows.
        Guid[] marketplaceRecipeIds = await MarketplaceRecipeIdsAsync(db, user.Id, purgeOrgs, ct);
        foreach (Guid recipeId in marketplaceRecipeIds)
        {
            try
            {
                // A version holding CSAM evidence outside quarantine/ is never purged: the purge of such an account waits for legal review.
                List<string?> checks = await db.MarketplaceRecipeVersions.AsNoTracking()
                    .Where(v => v.RecipeId == recipeId).Select(v => v.CheckJson).ToListAsync(ct);
                if (checks.Any(MarketplaceEvidenceGuard.IsHeld))
                {
                    _logger.LogError("The purge of marketplace recipe {RecipeId} is held: it carries CSAM evidence awaiting legal review", recipeId);
                    failures.Add($"marketplace recipe {recipeId}: evidence held for legal review");
                    continue;
                }

                (int deleted, int notDeleted) = await DeleteRecipeObjectsAsync(s3, recipeId, ct);
                objectsDeleted += deleted;
                if (notDeleted > 0)
                    failures.Add($"marketplace recipe {recipeId}: {notDeleted} object(s) not deleted");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deleting the object-store prefix of marketplace recipe {RecipeId} failed", recipeId);
                failures.Add($"marketplace recipe {recipeId}: object prefix not deleted");
            }
        }

        // The distinct exits. Nothing in the database is touched while the vendor or object-store half
        // has failed: the account stays due, and the next sweep retries the whole purge. Half-deleting
        // the rows of an account whose blobs survived would lose the only map back to those objects.
        if (failures.Count > 0)
            return Deferred(userId, projectIds.Length, objectsDeleted, collectionsDeleted, cancelled, retained, failures);

        // 4. The rows, children before parents.
        try
        {
            await DeleteRowsAsync(db, user, projectIds, executionIds, purgeOrgs, suppression, protector, nowUtc, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Deleting the rows of account {UserId} failed", userId);
            failures.Add("rows not deleted");
        }

        if (failures.Count > 0)
            return Deferred(userId, projectIds.Length, objectsDeleted, collectionsDeleted, cancelled, retained, failures);

        _logger.LogInformation(
            "Purged account {UserId}: {Projects} project(s), {Objects} object(s), {Collections} vector collection(s)",
            userId, projectIds.Length, objectsDeleted, collectionsDeleted);

        return new AccountPurgeUserResult(userId, true, projectIds.Length, objectsDeleted, collectionsDeleted, cancelled, retained, failures);
    }

    /// <summary>One deferred account: nothing was lost, the sweep will try again.</summary>
    private AccountPurgeUserResult Deferred(
        Guid userId, int projects, int objectsDeleted, int collectionsDeleted,
        int cancelled, int retained, List<string> failures)
    {
        _logger.LogWarning(
            "The purge of account {UserId} is deferred to the next sweep: {Failures}",
            userId, string.Join("; ", failures));
        return new AccountPurgeUserResult(userId, false, projects, objectsDeleted, collectionsDeleted, cancelled, retained, failures);
    }

    /// <summary>
    /// Deletes every object under <c>projects/{projectId}/</c>, listing the prefix rather than
    /// trusting the rows, and in batches of <see cref="MaxDeleteBatchSize"/>. Returns how many
    /// objects the store confirmed deleted and how many it refused; a non-zero second value defers
    /// the account, because the rows are the only map back to a surviving object.
    /// </summary>
    private Task<(int Deleted, int NotDeleted)> DeleteProjectObjectsAsync(IAmazonS3 s3, Guid projectId, CancellationToken ct) =>
        DeletePrefixObjectsAsync(s3, $"projects/{projectId}/", ct);

    /// <summary>
    /// Deletes every object under the platform prefix <c>marketplace/{recipeId}/</c> of a recipe the purge is
    /// removing (its preview and poster). Same listing-based, batched deletion as a project prefix.
    /// </summary>
    internal Task<(int Deleted, int NotDeleted)> DeleteRecipeObjectsAsync(IAmazonS3 s3, Guid recipeId, CancellationToken ct) =>
        DeletePrefixObjectsAsync(s3, $"marketplace/{recipeId}/", ct);

    /// <summary>
    /// Deletes every object of a purged workspace's avatar library
    /// (<c>organizations/{organizationId}/avatar-library/</c>, <see cref="AvatarLibraryStorage.OrganizationPrefix"/>):
    /// the library's own copies of reference media and signed releases. Same listing-based, batched deletion as a
    /// project prefix.
    /// </summary>
    internal Task<(int Deleted, int NotDeleted)> DeleteAvatarLibraryObjectsAsync(IAmazonS3 s3, Guid organizationId, CancellationToken ct) =>
        DeletePrefixObjectsAsync(s3, AvatarLibraryStorage.OrganizationPrefix(organizationId), ct);

    private async Task<(int Deleted, int NotDeleted)> DeletePrefixObjectsAsync(IAmazonS3 s3, string prefix, CancellationToken ct)
    {
        // Project versioning: the project prefix also covers projects/{p}/history/ (the protected copies), so an
        // account purge needs no extra step for them; only quarantine/ is excluded below.
        List<string> keys = [];
        string? token = null;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);

            // Belt and braces: quarantine/ is evidence and is never deleted by a project-prefix purge,
            // even if the legal-hold check above is ever bypassed.
            keys.AddRange((page.S3Objects ?? [])
                .Select(o => o.Key)
                .Where(k => !ReelBolt.Shared.Storage.MarketplacePreviewKeys.IsQuarantine(k)));
            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        if (keys.Count == 0)
            return (0, 0);

        int batchSize = Math.Clamp(Options.DeleteBatchSize, 1, MaxDeleteBatchSize);
        int deleted = 0;
        int notDeleted = 0;
        foreach (string[] batch in keys.Chunk(batchSize))
        {
            DeleteObjectsRequest request = new() { BucketName = _bucket, Quiet = true };
            foreach (string key in batch)
                request.AddKey(key);
            DeleteObjectsResponse response = await s3.DeleteObjectsAsync(request, ct);
            int failed = response.DeleteErrors?.Count ?? 0;
            deleted += batch.Length - failed;
            notDeleted += failed;
            if (failed > 0)
                _logger.LogWarning("Deleting {Count} object(s) under {Prefix} reported {Errors} error(s)", batch.Length, prefix, failed);
        }

        return (deleted, notDeleted);
    }

    /// <summary>
    /// The database half, in the order C0 fixes. Tables with no foreign key of their own
    /// (<c>workflow_step_cache_entries</c>, <c>decision_observations</c>,
    /// <c>external_generation_jobs</c>) are deleted by the project and execution ids they carry.
    /// </summary>
    private static async Task<Guid[]> MarketplaceRecipeIdsAsync(
        InferenceApiDbContext db, Guid userId, List<Organization> purgeOrgs, CancellationToken ct)
    {
        Guid[] orgIds = purgeOrgs.Select(o => o.Id).ToArray();
        return await db.MarketplaceRecipes.AsNoTracking()
            .Where(r => r.AuthorUserId == userId || orgIds.Contains(r.AuthorOrgId))
            .Select(r => r.Id)
            .ToArrayAsync(ct);
    }

    /// <summary>
    /// Marketplace rows of the purge: the user's ratings and reports (a rating also leaves its recipe's counters),
    /// then every recipe authored by the user or a purged workspace with its versions, copies, ratings and reports.
    /// Audit rows stay without the actor. Cascades are staged explicitly so the InMemory provider matches Postgres.
    /// </summary>
    private static async Task StageMarketplaceDeletionAsync(
        InferenceApiDbContext db, Guid userId, List<Organization> purgeOrgs, CancellationToken ct)
    {
        Guid[] recipeIds = await MarketplaceRecipeIdsAsync(db, userId, purgeOrgs, ct);

        // The user's ratings on recipes that stay: take their stars out of the denormalised counters.
        List<MarketplaceRating> ratings = await db.MarketplaceRatings.Where(r => r.UserId == userId).ToListAsync(ct);
        Guid[] ratedRecipeIds = ratings.Select(r => r.RecipeId).Where(id => !recipeIds.Contains(id)).Distinct().ToArray();
        List<MarketplaceRecipe> rated = await db.MarketplaceRecipes.Where(r => ratedRecipeIds.Contains(r.Id)).ToListAsync(ct);
        foreach (MarketplaceRecipe recipe in rated)
        {
            foreach (MarketplaceRating rating in ratings.Where(r => r.RecipeId == recipe.Id))
            {
                recipe.RatingSum = Math.Max(0, recipe.RatingSum - rating.Stars);
                recipe.RatingCount = Math.Max(0, recipe.RatingCount - 1);
            }
        }
        db.MarketplaceRatings.RemoveRange(ratings);
        db.MarketplaceReports.RemoveRange(await db.MarketplaceReports.Where(r => r.ReporterUserId == userId).ToListAsync(ct));

        foreach (MarketplaceAuditEntry entry in await db.MarketplaceAudit.Where(a => a.ActorUserId == userId).ToListAsync(ct))
            entry.ActorUserId = null;

        // Copies the purged workspaces made of recipes that stay: delete the rows by their organization and take the
        // organizations out of the recipes' distinct-copier counters.
        Guid[] purgeOrgIds = purgeOrgs.Select(o => o.Id).ToArray();
        List<MarketplaceCopy> ownCopies = await db.MarketplaceCopies.Where(c => purgeOrgIds.Contains(c.OrgId)).ToListAsync(ct);
        if (ownCopies.Count > 0)
        {
            Guid[] copiedStaying = ownCopies.Select(c => c.RecipeId).Where(id => !recipeIds.Contains(id)).Distinct().ToArray();
            foreach (MarketplaceRecipe recipe in await db.MarketplaceRecipes.Where(r => copiedStaying.Contains(r.Id)).ToListAsync(ct))
            {
                int orgs = ownCopies.Where(c => c.RecipeId == recipe.Id).Select(c => c.OrgId).Distinct().Count();
                recipe.CopyOrgs = Math.Max(0, recipe.CopyOrgs - orgs);
            }

            db.MarketplaceCopies.RemoveRange(ownCopies);
        }

        if (recipeIds.Length == 0)
            return;
        db.MarketplaceRatings.RemoveRange(await db.MarketplaceRatings.Where(r => recipeIds.Contains(r.RecipeId)).ToListAsync(ct));
        db.MarketplaceReports.RemoveRange(await db.MarketplaceReports.Where(r => recipeIds.Contains(r.RecipeId)).ToListAsync(ct));
        db.MarketplaceCopies.RemoveRange(await db.MarketplaceCopies.Where(c => recipeIds.Contains(c.RecipeId)).ToListAsync(ct));
        db.MarketplaceRecipeVersions.RemoveRange(await db.MarketplaceRecipeVersions.Where(v => recipeIds.Contains(v.RecipeId)).ToListAsync(ct));
        db.MarketplaceRecipes.RemoveRange(await db.MarketplaceRecipes.Where(r => recipeIds.Contains(r.Id)).ToListAsync(ct));
    }

    private async Task DeleteRowsAsync(
        InferenceApiDbContext db,
        ApplicationUser user,
        Guid[] projectIds,
        Guid[] executionIds,
        List<Organization> purgeOrgs,
        IMarketingSuppressionHasher? suppression,
        ISecretProtector? protector,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // One transaction for the whole deletion (see the SaveChangesAsync at the end), opened here so
        // the two set-based deletes below run inside it too and roll back with everything else.
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);

        // Two kinds of row are deleted set-based FIRST, before anything is read for the batch:
        // - Every ExecutionSnapshot (kind 5) of the purged projects, run snapshots and legacy archives
        //   alike. Their steps reference the account's custom agents (workflow_steps → agent_definitions
        //   is ON DELETE RESTRICT), and the database also deletes a run's snapshot WITH the run, so
        //   neither an EF delete nor leaving them to the project cascade can be ordered before the
        //   agents. Their steps and those steps' results go with them by cascade (SQFU-S2).
        // - The external-generation ledger. ExternalGenerationJob.Status is a concurrency token, so an
        //   EF delete is WHERE status = the status read; an engine write in between made that delete
        //   affect 0 rows and rolled the whole purge back on every retry (SQFU LOW 4). A set-based
        //   delete takes the row whatever its status (the engine's write then waits on our lock).
        if (db.Database.IsRelational())
        {
            await db.WorkflowDefinitions
                .Where(w => projectIds.Contains(w.ProjectId) && w.Kind == WorkflowDefinitionKind.ExecutionSnapshot)
                .ExecuteDeleteAsync(ct);
            await db.ExternalGenerationJobs
                .Where(j => projectIds.Contains(j.ProjectId))
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            // A provider without set-based deletes or cascades (the InMemory test provider): the same
            // rows through the batch. No concurrent writer exists there.
            Guid[] snapshotIds = (await db.WorkflowDefinitions
                .Where(w => projectIds.Contains(w.ProjectId) && w.Kind == WorkflowDefinitionKind.ExecutionSnapshot)
                .Select(w => w.Id).ToListAsync(ct)).ToArray();
            db.WorkflowSteps.RemoveRange(await db.WorkflowSteps
                .Where(s => snapshotIds.Contains(s.WorkflowDefinitionId)).ToListAsync(ct));
            db.WorkflowDefinitions.RemoveRange(await db.WorkflowDefinitions
                .Where(w => snapshotIds.Contains(w.Id)).ToListAsync(ct));
            db.ExternalGenerationJobs.RemoveRange(await db.ExternalGenerationJobs
                .Where(j => projectIds.Contains(j.ProjectId)).ToListAsync(ct));
        }

        // Engine-owned content of the purged projects.
        db.WorkflowStepCacheEntries.RemoveRange(await db.WorkflowStepCacheEntries
            .Where(e => projectIds.Contains(e.ProjectId)).ToListAsync(ct));
        db.DecisionObservations.RemoveRange(await db.DecisionObservations
            .Where(o => (o.ProjectId != null && projectIds.Contains(o.ProjectId.Value)) || executionIds.Contains(o.ExecutionId))
            .ToListAsync(ct));
        db.WorkflowStepResults.RemoveRange(await db.WorkflowStepResults
            .Where(r => executionIds.Contains(r.WorkflowExecutionId)).ToListAsync(ct));
        db.ReviewScores.RemoveRange(await db.ReviewScores
            .Where(s => executionIds.Contains(s.WorkflowExecutionId)).ToListAsync(ct));
        db.WorkflowExecutions.RemoveRange(await db.WorkflowExecutions
            .Where(e => projectIds.Contains(e.ProjectId)).ToListAsync(ct));

        // Every kind-5 row is already gone (above); the filter keeps this batch from reading one again.
        Guid[] definitionIds = (await db.WorkflowDefinitions
            .Where(w => projectIds.Contains(w.ProjectId) && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot)
            .Select(w => w.Id).ToListAsync(ct)).ToArray();
        db.WorkflowSteps.RemoveRange(await db.WorkflowSteps
            .Where(s => definitionIds.Contains(s.WorkflowDefinitionId)).ToListAsync(ct));
        db.WorkflowDefinitions.RemoveRange(await db.WorkflowDefinitions
            .Where(w => definitionIds.Contains(w.Id)).ToListAsync(ct));

        Guid[] timelineIds = (await db.EditTimelines.IgnoreQueryFilters()
            .Where(t => projectIds.Contains(t.ProjectId)).Select(t => t.Id).ToListAsync(ct)).ToArray();
        // Guest review links (Wave 4): the links of the purged projects' timelines and every link this user created
        // (in a shared workspace too: a share is delegated authority and dies with who granted it), with their
        // decisions. The purged projects' guest comments go with the comments themselves.
        Guid[] reviewLinkIds = (await db.EditReviewLinks.IgnoreQueryFilters()
            .Where(l => projectIds.Contains(l.ProjectId) || l.CreatedByUserId == user.Id)
            .Select(l => l.Id).ToListAsync(ct)).ToArray();
        db.EditReviewDecisions.RemoveRange(await db.EditReviewDecisions
            .Where(d => reviewLinkIds.Contains(d.LinkId)).ToListAsync(ct));
        db.EditTimelineComments.RemoveRange(await db.EditTimelineComments.IgnoreQueryFilters()
            .Where(c => projectIds.Contains(c.ProjectId) && c.GuestLinkId != null).ToListAsync(ct));
        db.EditReviewLinks.RemoveRange(await db.EditReviewLinks.IgnoreQueryFilters()
            .Where(l => reviewLinkIds.Contains(l.Id)).ToListAsync(ct));
        db.EditTimelineOps.RemoveRange(await db.EditTimelineOps
            .Where(o => timelineIds.Contains(o.TimelineId)).ToListAsync(ct));
        db.EditTimelineSnapshots.RemoveRange(await db.EditTimelineSnapshots
            .Where(s => timelineIds.Contains(s.TimelineId)).ToListAsync(ct));
        // Soft-deleted rows are deleted for real here, not left as tombstones (C0).
        db.EditTimelines.RemoveRange(await db.EditTimelines.IgnoreQueryFilters()
            .Where(t => projectIds.Contains(t.ProjectId)).ToListAsync(ct));
        db.MediaDerivatives.RemoveRange(await db.MediaDerivatives.IgnoreQueryFilters()
            .Where(d => projectIds.Contains(d.ProjectId)).ToListAsync(ct));
        db.PostTexts.RemoveRange(await db.PostTexts
            .Where(t => projectIds.Contains(t.ProjectId)).ToListAsync(ct));
        db.ProjectPronunciationEntries.RemoveRange(await db.ProjectPronunciationEntries
            .Where(e => projectIds.Contains(e.ProjectId)).ToListAsync(ct));
        db.ProjectVoices.RemoveRange(await db.ProjectVoices.IgnoreQueryFilters()
            .Where(v => projectIds.Contains(v.ProjectId)).ToListAsync(ct));
        // Avatars before the files: a reference holds its file with ON DELETE NO ACTION, so the avatar rows (with their
        // references and vendor-asset rows) go first in this same transaction. Their audit events stay (no FK).
        Guid[] avatarIds = (await db.ProjectAvatars.IgnoreQueryFilters()
            .Where(a => projectIds.Contains(a.ProjectId)).Select(a => a.Id).ToListAsync(ct)).ToArray();
        db.ProjectAvatarVendorAssets.RemoveRange(await db.ProjectAvatarVendorAssets
            .Where(v => avatarIds.Contains(v.AvatarId)).ToListAsync(ct));
        db.ProjectAvatarReferences.RemoveRange(await db.ProjectAvatarReferences
            .Where(r => avatarIds.Contains(r.AvatarId)).ToListAsync(ct));
        db.ProjectAvatars.RemoveRange(await db.ProjectAvatars.IgnoreQueryFilters()
            .Where(a => avatarIds.Contains(a.Id)).ToListAsync(ct));
        // The workspace avatar library of every purged workspace, after its imports (the avatars above): an import
        // holds its item with ON DELETE NO ACTION. The items' objects went with the organization prefix.
        Guid[] libraryOrgIds = purgeOrgs.Select(o => o.Id).ToArray();
        Guid[] libraryItemIds = (await db.AvatarLibraryItems.IgnoreQueryFilters()
            .Where(i => libraryOrgIds.Contains(i.OrganizationId)).Select(i => i.Id).ToListAsync(ct)).ToArray();
        db.AvatarLibraryReferences.RemoveRange(await db.AvatarLibraryReferences
            .Where(r => libraryItemIds.Contains(r.LibraryItemId)).ToListAsync(ct));
        db.AvatarLibraryItems.RemoveRange(await db.AvatarLibraryItems.IgnoreQueryFilters()
            .Where(i => libraryItemIds.Contains(i.Id)).ToListAsync(ct));
        db.ProjectFiles.RemoveRange(await db.ProjectFiles.IgnoreQueryFilters()
            .Where(f => projectIds.Contains(f.ProjectId)).ToListAsync(ct));
        db.StepChangeRequests.RemoveRange(await db.StepChangeRequests
            .Where(r => projectIds.Contains(r.ProjectId)).ToListAsync(ct));
        db.OutputModerationVerdicts.RemoveRange(await db.OutputModerationVerdicts
            .Where(v => projectIds.Contains(v.ProjectId)).ToListAsync(ct));
        db.ObjectEffectRequests.RemoveRange(await db.ObjectEffectRequests
            .Where(r => projectIds.Contains(r.ProjectId)).ToListAsync(ct));
        db.ProjectArchiveUnpacks.RemoveRange(await db.ProjectArchiveUnpacks
            .Where(r => projectIds.Contains(r.ProjectId)).ToListAsync(ct));
        db.Projects.RemoveRange(await db.Projects.IgnoreQueryFilters()
            .Where(p => projectIds.Contains(p.Id))
            .ToListAsync(ct));

        // The user's own rows.
        db.AssistantRunWatches.RemoveRange(await db.AssistantRunWatches.Where(w => w.UserId == user.Id).ToListAsync(ct));
        db.AssistantMessages.RemoveRange(await db.AssistantMessages
            .Where(m => db.AssistantThreads.Where(t => t.UserId == user.Id).Select(t => t.Id).Contains(m.ThreadId))
            .ToListAsync(ct));
        // Workflows that outlive the account (a shared project's) keep no pointer to the deleted threads.
        foreach (WorkflowDefinition created in await db.WorkflowDefinitions.IgnoreQueryFilters()
            .Where(w => w.CreatedInThreadId != null
                && db.AssistantThreads.IgnoreQueryFilters().Any(t => t.UserId == user.Id && t.Id == w.CreatedInThreadId))
            .ToListAsync(ct))
            created.CreatedInThreadId = null;
        db.AssistantThreads.RemoveRange(await db.AssistantThreads.IgnoreQueryFilters()
            .Where(t => t.UserId == user.Id).ToListAsync(ct));
        db.EditingStyles.RemoveRange(await db.EditingStyles
            .Where(s => s.OwnerId == user.Id).ToListAsync(ct));
        db.UserTokens.RemoveRange(await db.UserTokens
            .Where(t => t.UserId == user.Id).ToListAsync(ct));
        db.RunRateEvents.RemoveRange(await db.RunRateEvents
            .Where(e => e.UserId == user.Id).ToListAsync(ct));
        await StageMarketplaceDeletionAsync(db, user.Id, purgeOrgs, ct);
        // T-019: user prose goes with the account, not retained and not anonymised.
        db.FeedbackReports.RemoveRange(await db.FeedbackReports
            .Where(r => r.UserId == user.Id).ToListAsync(ct));
        db.LegalAcceptances.RemoveRange(await db.LegalAcceptances
            .Where(a => a.UserId == user.Id).ToListAsync(ct));

        // Custom agents: their steps are gone above, so the RESTRICT from workflow_steps cannot fire.
        db.AgentDefinitions.RemoveRange(await db.AgentDefinitions.IgnoreQueryFilters()
            .Where(a => a.OwnerId == user.Id).ToListAsync(ct));

        // Invites to the user's address go; the ones they sent stay with invited_by cleared (the FK
        // does that when the user row goes).
        string lowered = user.Email.ToLowerInvariant();
        db.OrganizationInvites.RemoveRange(await db.OrganizationInvites
            .Where(i => i.Email.ToLower() == lowered).ToListAsync(ct));

        // Anonymise rather than delete: usage rows are the bookkeeping C0 retains, keyed by org.
        List<UsageEvent> usage = await db.UsageEvents
            .Where(u => u.UserId == user.Id).ToListAsync(ct);
        foreach (UsageEvent usageEvent in usage)
            usageEvent.UserId = null;

        // Avatar audit events are kept for 3 years (AV-7) as the accountability record, but without the person:
        // the same pseudonymisation as usage rows. The rows of the purged projects stay too (no FK).
        foreach (ProjectAvatarEvent avatarEvent in await db.ProjectAvatarEvents
            .Where(e => e.ActorUserId == user.Id).ToListAsync(ct))
            avatarEvent.ActorUserId = null;

        // The billing record keeps the invoice trail but not the address.
        List<BillingCustomer> customers = await db.BillingCustomers
            .Where(c => purgeOrgs.Select(o => o.Id).Contains(c.OrganizationId)).ToListAsync(ct);
        foreach (BillingCustomer customer in customers)
            customer.Email = null;

        // Memberships of the user, then the workspace itself, soft-deleted and de-identified: its
        // name was derived from the user's own name.
        db.OrganizationMemberships.RemoveRange(await db.OrganizationMemberships
            .Where(m => m.UserId == user.Id).ToListAsync(ct));

        // The workspaces themselves: soft-deleted (A4's own semantics, so the retained bookkeeping
        // rows keyed by organization stay intact) and renamed, because a Personal workspace's name is
        // derived from the user's own name and must not outlive them.
        Guid[] purgeOrgIds = purgeOrgs.Select(o => o.Id).ToArray();
        List<Organization> trackedOrgs = await db.Organizations
            .Where(o => purgeOrgIds.Contains(o.Id)).ToListAsync(ct);
        foreach (Organization org in trackedOrgs)
        {
            org.DeletedAt = nowUtc;
            org.Name = AnonymisedOrganizationName;
            org.UpdatedAt = nowUtc;
        }

        // Webhook endpoints of a purged workspace go with it (their deliveries cascade in the database); the org row
        // is only soft-deleted above, so nothing else would remove them.
        db.WebhookEndpoints.RemoveRange(await db.WebhookEndpoints.IgnoreQueryFilters()
            .Where(w => purgeOrgIds.Contains(w.OrganizationId)).ToListAsync(ct));

        // Service accounts (one hidden user per API key) live and die with their workspace: the keys, the
        // memberships, the usage attribution and the user rows of every purged workspace go in this same batch.
        // They never counted as members above, so a workspace whose only people are the account being purged
        // is purged with all its keys.
        Guid[] serviceOrgIds = purgeOrgs.Select(o => o.Id).ToArray();
        Guid[] serviceUserIds = await db.OrganizationMemberships.IgnoreQueryFilters()
            .Where(m => serviceOrgIds.Contains(m.OrganizationId) && m.User.IsServiceAccount)
            .Select(m => m.UserId).Distinct().ToArrayAsync(ct);
        if (serviceUserIds.Length > 0)
        {
            db.ApiKeys.RemoveRange(await db.ApiKeys.IgnoreQueryFilters()
                .Where(k => serviceOrgIds.Contains(k.OrganizationId) || serviceUserIds.Contains(k.ServiceUserId)).ToListAsync(ct));
            db.OrganizationMemberships.RemoveRange(await db.OrganizationMemberships.IgnoreQueryFilters()
                .Where(m => serviceUserIds.Contains(m.UserId)).ToListAsync(ct));
            foreach (UsageEvent serviceUsage in await db.UsageEvents
                .Where(u => u.UserId != null && serviceUserIds.Contains(u.UserId.Value)).ToListAsync(ct))
                serviceUsage.UserId = null;
            foreach (Guid serviceUserId in serviceUserIds)
                db.ApplicationUsers.Remove(new ApplicationUser { Id = serviceUserId });
        }

        // Marketing consent (saas-promotions): the rows and events go; an address that had opted in
        // or withdrawn leaves only a keyed-HMAC suppression entry, so it is never mailed again.
        await MarketingConsentErasure.StageAsync(db, user, suppression, nowUtc, ct, protector);

        // The last step, and the one that makes the sweep resumable: until this succeeds the account
        // is still due, so a failure anywhere above retries the whole purge instead of skipping it.
        db.ApplicationUsers.Remove(new ApplicationUser { Id = user.Id });

        // ONE SaveChanges — one transaction — for every row above, and that is load-bearing rather
        // than tidy. The memberships removed in this batch are the only handle the sweep has on the
        // workspace: the worklist is the user row, but purgeOrgs, projectIds, the object prefixes and
        // the vector collections are all resolved through organization_memberships (see
        // PurgeUserAsync). Committing the memberships in an *earlier* SaveChanges leaves a retry that
        // resolves no workspace, no project and no object, deletes the user row anyway and returns
        // Purged = true — the content is then stranded with nothing left to retry it, while the sweep
        // logs a success (C11 F-2, demonstrated by execution). Every command of a single
        // SaveChangesAsync runs inside one transaction, so a failure anywhere in this batch rolls the
        // whole deletion back: the next sweep finds the account exactly as it was and purges it for
        // real. Do not split this call again.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
