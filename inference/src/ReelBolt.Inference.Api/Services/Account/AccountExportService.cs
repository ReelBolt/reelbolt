using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Account;

/// <summary>Why an export request was or was not accepted (C9a).</summary>
public enum AccountExportRequestStatus
{
    /// <summary>Queued; the archive is built in the background and its link mailed.</summary>
    Accepted,

    /// <summary>An archive for this caller is already being built.</summary>
    AlreadyRunning,

    /// <summary>The caller already asked within the throttle window (C0: one per 24 hours).</summary>
    TooSoon,

    /// <summary>The feature is switched off (<c>Account:Export:Enabled</c>); the endpoint answers 404.</summary>
    Disabled
}

/// <param name="Status">The outcome.</param>
/// <param name="RetryAfter">How long until another request is accepted (only for <see cref="AccountExportRequestStatus.TooSoon"/>).</param>
public sealed record AccountExportRequestResult(AccountExportRequestStatus Status, TimeSpan? RetryAfter = null);

/// <summary>What one built export contains, for tests and logs.</summary>
public sealed record AccountExportBuildResult(
    Guid ExportId,
    string StorageKey,
    int Sections,
    int Rows,
    int FilesPacked,
    int FilesSkipped,
    long PackedBytes,
    bool Mailed);

/// <summary>
/// C9a: the engine behind <c>POST /api/v1/account/export</c>. It builds a zip of the caller's data
/// from the C0 data map (<see cref="AccountExportSections"/>), stores it outside every project
/// prefix and mails a 7-day link. One export per <see cref="AccountExportOptions.ThrottleHours"/> hours.
/// <para>
/// <b>Why the object store carries the state.</b> The archive, its life, and the "you asked
/// recently" marker all live in the object store rather than in a new table: the throttle survives a
/// restart and works with several API replicas, the sweeper's worklist IS the set of objects it must
/// delete, and the feature needs no EF migration. The marker is
/// <c>{prefix}/{userId}/request.json</c>, whose <c>LastModified</c> is the throttle clock.
/// </para>
/// <para>
/// <b>Failure model.</b> The request path only writes the marker and enqueues; a build failure is
/// logged and leaves no archive, so the caller can ask again after the next throttle window. Mail is
/// best-effort — the archive stays downloadable through <c>GET /api/v1/account/export</c>, which
/// exists for exactly that reason (and for a self-host with no SMTP).
/// </para>
/// </summary>
public sealed class AccountExportService : BackgroundService
{
    /// <summary>A marker object (never a zip) recording when the user last asked.</summary>
    public const string MarkerFileName = "request.json";

    /// <summary>SigV4 presigned URLs cannot outlive seven days; C0 asks for exactly seven.</summary>
    public static readonly TimeSpan MaxLinkTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AccountOptions _options;
    private readonly string _bucket;
    private readonly ILogger<AccountExportService> _logger;

    private readonly Channel<AccountExportJob> _queue = Channel.CreateUnbounded<AccountExportJob>();
    private readonly HashSet<Guid> _inFlight = [];
    private readonly Lock _inFlightLock = new();

    public AccountExportService(
        IServiceScopeFactory scopeFactory, IOptions<AccountOptions> options, IConfiguration configuration, ILogger<AccountExportService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _logger = logger;
    }

    private AccountExportOptions Options => _options.Export;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled)
            return;

        // Two duties on one loop: drain the queue, and sweep expired archives between drains. The
        // sweep runs on its interval rather than per build, so a burst of exports pays for it once.
        DateTime nextSweep = DateTime.UtcNow.AddMinutes(Math.Max(1, Options.SweepIntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_queue.Reader.TryRead(out AccountExportJob? job))
                {
                    try
                    {
                        await BuildAsync(job.UserId, job.Email, job.DisplayName, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Building the data export of user {UserId} failed; the user can ask again after the throttle window", job.UserId);
                    }
                    finally
                    {
                        lock (_inFlightLock)
                            _inFlight.Remove(job.UserId);
                    }
                }

                if (DateTime.UtcNow >= nextSweep)
                {
                    try
                    {
                        int deleted = await SweepExpiredAsync(stoppingToken);
                        if (deleted > 0)
                            _logger.LogInformation("Deleted {Count} expired account export(s)", deleted);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Sweeping expired account exports failed; it is retried on the next interval");
                    }

                    nextSweep = DateTime.UtcNow.AddMinutes(Math.Max(1, Options.SweepIntervalMinutes));
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The request path. Returns <see cref="AccountExportRequestStatus.TooSoon"/> with a
    /// <c>Retry-After</c> when the caller already asked inside the throttle window; otherwise writes
    /// the marker and queues the build.
    /// </summary>
    public async Task<AccountExportRequestResult> RequestExportAsync(
        Guid userId, string email, string displayName, CancellationToken ct)
    {
        if (!Options.Enabled)
            return new AccountExportRequestResult(AccountExportRequestStatus.Disabled);

        lock (_inFlightLock)
        {
            if (_inFlight.Contains(userId))
                return new AccountExportRequestResult(AccountExportRequestStatus.AlreadyRunning);
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();

        DateTime? lastRequest = await ReadMarkerAsync(s3, userId, ct);
        TimeSpan throttle = TimeSpan.FromHours(Math.Max(1, Options.ThrottleHours));
        if (lastRequest is { } at && DateTime.UtcNow - at < throttle)
            return new AccountExportRequestResult(AccountExportRequestStatus.TooSoon, at + throttle - DateTime.UtcNow);

        await WriteMarkerAsync(s3, userId, ct);

        lock (_inFlightLock)
        {
            if (!_inFlight.Add(userId))
                return new AccountExportRequestResult(AccountExportRequestStatus.AlreadyRunning);
        }

        if (!_queue.Writer.TryWrite(new AccountExportJob(userId, email, displayName)))
        {
            lock (_inFlightLock)
                _inFlight.Remove(userId);
            return new AccountExportRequestResult(AccountExportRequestStatus.TooSoon, throttle);
        }

        return new AccountExportRequestResult(AccountExportRequestStatus.Accepted);
    }

    /// <summary>The object key of the newest archive this user has, or <c>null</c> when they have none.</summary>
    public async Task<string?> FindLatestAsync(Guid userId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = UserPrefix(userId) + "/"
        }, ct);

        return (page.S3Objects ?? [])
            .Where(o => o.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.LastModified)
            .Select(o => o.Key)
            .FirstOrDefault();
    }

    /// <summary>
    /// What the dashboard's "Download your data" card needs to say without guessing: whether a copy is
    /// being prepared, when the newest copy was made and until when it is kept, and when the next
    /// request will be accepted.
    /// </summary>
    /// <remarks>
    /// "Preparing" is read from the store as well as from this process, so it holds across API
    /// replicas: a throttle marker newer than the newest archive means a build was asked for and has not
    /// landed yet. A build that failed leaves that state behind, so it is bounded by
    /// <see cref="PreparingWindow"/> rather than lasting until the throttle window ends.
    /// </remarks>
    public async Task<AccountExportStatus> GetStatusAsync(Guid userId, CancellationToken ct)
    {
        if (!Options.Enabled)
            return new AccountExportStatus(Enabled: false, Preparing: false, null, null, null, null);

        bool inFlight;
        lock (_inFlightLock)
            inFlight = _inFlight.Contains(userId);

        using IServiceScope scope = _scopeFactory.CreateScope();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();

        DateTime? requestedAt = await ReadMarkerAsync(s3, userId, ct);
        S3Object? latest = await FindLatestObjectAsync(s3, userId, ct);
        DateTime? latestAt = latest?.LastModified?.ToUniversalTime();
        DateTime now = DateTime.UtcNow;

        bool pendingInStore = requestedAt is { } asked
            && (latestAt is null || asked > latestAt.Value)
            && now - asked < PreparingWindow;

        TimeSpan throttle = TimeSpan.FromHours(Math.Max(1, Options.ThrottleHours));
        DateTime? nextRequestAt = requestedAt is { } at && now - at < throttle ? at + throttle : null;

        return new AccountExportStatus(
            Enabled: true,
            Preparing: inFlight || pendingInStore,
            LatestCreatedAt: latestAt,
            LatestBytes: latest?.Size,
            LatestExpiresAt: latestAt?.AddDays(Math.Clamp(Options.LinkTtlDays, 1, 365)),
            NextRequestAt: nextRequestAt);
    }

    /// <summary>How long a request with no archive yet still reads as "being prepared".</summary>
    public static readonly TimeSpan PreparingWindow = TimeSpan.FromHours(2);

    private async Task<S3Object?> FindLatestObjectAsync(IAmazonS3 s3, Guid userId, CancellationToken ct)
    {
        ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = UserPrefix(userId) + "/"
        }, ct);

        return (page.S3Objects ?? [])
            .Where(o => o.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.LastModified)
            .FirstOrDefault();
    }

    /// <summary>
    /// A fresh, short-lived download link for the newest archive the caller owns. This is what makes
    /// the feature usable without mail (self-host), and it re-issues a link after the mailed one has
    /// expired while the archive is still inside its <see cref="AccountExportOptions.LinkTtlDays"/>.
    /// </summary>
    public async Task<string?> CreateDownloadLinkAsync(Guid userId, TimeSpan ttl, CancellationToken ct)
    {
        string? key = await FindLatestAsync(userId, ct);
        if (key is null)
            return null;

        using IServiceScope scope = _scopeFactory.CreateScope();
        IAmazonS3 presign = scope.ServiceProvider.GetRequiredKeyedService<IAmazonS3>(S3ClientFactory.PresignServiceKey);
        return await MintLinkAsync(presign, key, ttl <= MaxLinkTtl ? ttl : MaxLinkTtl, ct);
    }

    /// <summary>
    /// Builds, stores and mails one user's archive. Exposed for tests: the background loop only puts
    /// the queue in front of it.
    /// </summary>
    public async Task<AccountExportBuildResult> BuildAsync(
        Guid userId, string email, string displayName, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        IAmazonS3 presign = scope.ServiceProvider.GetRequiredKeyedService<IAmazonS3>(S3ClientFactory.PresignServiceKey);
        var mail = scope.ServiceProvider.GetRequiredService<IAccountMailClient>();
        ITenantContext? tenant = scope.ServiceProvider.GetService<ITenantContext>();

        Guid exportId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        // One system scope: the export spans every workspace the user belongs to, so the tenant
        // filters must not hide rows. Every query below is scoped by explicit ids instead.
        AccountExportContent content;
        using (tenant.System())
            content = await CollectAsync(db, s3, userId, email, exportId, now, ct);

        string storageKey = $"{UserPrefix(userId)}/{exportId:N}.zip";
        using var archive = new MemoryStream();
        using (var write = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (AccountExportDocument document in content.Documents)
                WriteEntry(write, document.FileName, document.Json);

            foreach (AccountExportFile file in content.Files)
                WriteBinaryEntry(write, file.EntryPath, file.Bytes);

            WriteEntry(write, AccountExportSections.ManifestFileName, BuildManifest(userId, email, exportId, now, content));
        }

        long bytes = archive.Length;
        archive.Position = 0;
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = storageKey,
            InputStream = archive,
            ContentType = "application/zip",
            AutoCloseStream = false
        }, ct);

        TimeSpan ttl = TimeSpan.FromDays(Math.Clamp(Options.LinkTtlDays, 1, 7));
        string? link = await MintLinkAsync(presign, storageKey, ttl, ct);

        bool mailed = false;
        if (link is not null)
        {
            mailed = await mail.SendAsync(new AccountMail(
                email,
                "export_ready",
                new AccountMailData
                {
                    Name = displayName,
                    Link = link,
                    ExpiresIn = ttl.TotalDays >= 1 ? $"{ttl.TotalDays:0} days" : $"{ttl.TotalHours:0} hours"
                }), ct);
        }

        if (!mailed)
        {
            _logger.LogInformation(
                "The export {ExportId} of user {UserId} is ready but was not mailed; it stays available through the account export endpoint",
                exportId, userId);
        }

        return new AccountExportBuildResult(
            exportId, storageKey, content.Documents.Count, content.Rows,
            content.Files.Count, content.SkippedFiles.Count, bytes, mailed);
    }

    /// <summary>
    /// Deletes archives past <see cref="AccountExportOptions.LinkTtlDays"/>. The marker objects stay:
    /// they are tiny, one per user, and they are the throttle clock.
    /// </summary>
    public async Task<int> SweepExpiredAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        IAmazonS3 s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        string prefix = Options.Prefix.TrimEnd('/') + "/";
        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(Options.LinkTtlDays, 1, 365));

        List<string> expired = [];
        string? token = null;
        int seen = 0;
        do
        {
            ListObjectsV2Response page = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);

            foreach (S3Object o in page.S3Objects ?? [])
            {
                seen++;
                if (o.Key.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && o.LastModified < cutoff)
                    expired.Add(o.Key);
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null && seen < Math.Max(1, Options.SweepMaxObjects));

        if (expired.Count == 0)
            return 0;

        int deleted = 0;
        foreach (string[] batch in expired.Chunk(1000))
        {
            DeleteObjectsRequest request = new() { BucketName = _bucket, Quiet = true };
            foreach (string key in batch)
                request.AddKey(key);
            DeleteObjectsResponse response = await s3.DeleteObjectsAsync(request, ct);
            deleted += batch.Length - (response.DeleteErrors?.Count ?? 0);
        }

        return deleted;
    }

    // ---------------------------------------------------------------------------------------------
    // Data collection
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads every C0 section for one user. Each query is scoped by ids taken from that user's own
    /// rows (or their Personal workspace), never by "the current org", so the archive is the same
    /// whatever the caller's active workspace happens to be.
    /// </summary>
    private async Task<AccountExportContent> CollectAsync(
        InferenceApiDbContext db, IAmazonS3 s3, Guid userId, string email, Guid exportId, DateTime now, CancellationToken ct)
    {
        List<AccountExportDocument> documents = [];
        int rows = 0;

        void Add(string table, object payload, int count)
        {
            AccountExportSection section = AccountExportSections.For(table)
                ?? throw new InvalidOperationException($"'{table}' is not an account export section.");
            documents.Add(new AccountExportDocument(
                $"{AccountExportSections.DataFolder}/{section.FileName}", JsonSerializer.Serialize(payload, Json)));
            rows += count;
        }

        ApplicationUser user = await db.ApplicationUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException($"User {userId} does not exist.");

        // A service account (hidden user behind an API key) is never an export subject. The export is keyed by the
        // person's own user id and lists only that person's own membership rows, never an org's member list, and it
        // covers no org-level API key data, so nothing else needs excluding here.
        if (user.IsServiceAccount)
            throw new InvalidOperationException($"User {userId} is a service account, not a person.");

        List<Organization> memberOrgs = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.Organization)
            .ToListAsync(ct);
        Guid[] memberOrgIds = memberOrgs.Select(o => o.Id).ToArray();
        Guid[] personalOrgIds = memberOrgs.Where(o => o.Kind == OrganizationKind.Personal).Select(o => o.Id).ToArray();

        // The export covers the PERSONAL workspace's content. A Team workspace's projects belong to
        // the team and to its other members, so they are not exported (see the type's doc comment).
        List<Project> projects = await db.Projects.AsNoTracking().IgnoreQueryFilters()
            .Where(p => personalOrgIds.Contains(p.OrganizationId))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        Guid[] projectIds = projects.Select(p => p.Id).ToArray();

        List<WorkflowExecution> executions = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId))
            .OrderBy(e => e.StartedAt)
            .ToListAsync(ct);
        Guid[] executionIds = executions.Select(e => e.Id).ToArray();

        List<EditTimelineEntity> timelines = await db.EditTimelines.AsNoTracking().IgnoreQueryFilters()
            .Where(t => projectIds.Contains(t.ProjectId))
            .ToListAsync(ct);
        Guid[] timelineIds = timelines.Select(t => t.Id).ToArray();

        // 1. The account itself (never the password hash).
        Add("application_users", new[]
        {
            new
            {
                id = user.Id,
                email = user.Email,
                displayName = user.DisplayName,
                createdAt = user.CreatedAt,
                emailVerifiedAt = user.EmailVerifiedAt,
                authProvider = user.AuthProvider,
                mustChangePassword = user.MustChangePassword,
                deletionRequestedAt = user.DeletionRequestedAt,
                signupIp = user.SignupIp
            }
        }, 1);

        List<LegalAcceptance> acceptances = await db.LegalAcceptances.AsNoTracking()
            .Where(a => a.UserId == userId).OrderBy(a => a.AcceptedAt).ToListAsync(ct);
        Add("legal_acceptances",
            acceptances.Select(a => new { a.Id, document = a.Document.ToString(), a.Version, a.AcceptedAt, a.Ip }),
            acceptances.Count);

        List<OrganizationMembership> memberships = await db.OrganizationMemberships.AsNoTracking()
            .Where(m => m.UserId == userId).ToListAsync(ct);
        Add("organization_memberships",
            memberships.Select(m => new { organizationId = m.OrganizationId, m.UserId, role = m.Role.ToString(), m.CreatedAt }),
            memberships.Count);

        string lowered = email.ToLowerInvariant();
        List<OrganizationInvite> invites = await db.OrganizationInvites.AsNoTracking()
            .Where(i => i.InvitedByUserId == userId || i.Email.ToLower() == lowered)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(ct);
        Add("organization_invites", invites.Select(i => new
        {
            i.Id,
            organizationId = i.OrganizationId,
            i.Email,
            role = i.Role.ToString(),
            invitedByUserId = i.InvitedByUserId,
            i.CreatedAt,
            i.ExpiresAt,
            i.AcceptedAt,
            i.RevokedAt
        }), invites.Count);

        Add("organizations",
            memberOrgs.Select(o => new { o.Id, o.Name, kind = o.Kind.ToString(), o.CreatedAt, o.DeletedAt }),
            memberOrgs.Count);

        Add("projects",
            projects.Select(p => new { p.Id, p.Name, p.Description, organizationId = p.OrganizationId, status = p.Status.ToString(), p.CreatedAt, p.UpdatedAt }),
            projects.Count);

        List<ProjectFile> files = await db.ProjectFiles.AsNoTracking().IgnoreQueryFilters()
            .Where(f => projectIds.Contains(f.ProjectId) && f.CsamStatus != CsamScanStatus.Quarantined)
            .OrderBy(f => f.UploadedAt).ToListAsync(ct);

        // An output the platform is not serving, and the files that carry its bytes, are left out of the bundle.
        var outputHolds = await ReelBolt.Shared.Storage.ProjectOutputHold.ReadManyAsync(db, projectIds, ct);
        List<Guid> markedProjects = outputHolds.Where(h => h.Value.IsActive).Select(h => h.Key).ToList();
        HashSet<Guid> unavailableResultIds = [];
        HashSet<(Guid, string)> unavailableKeys = [];
        HashSet<Guid> unavailableExecutionIds = [];
        if (markedProjects.Count > 0)
        {
            var owners = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => markedProjects.Contains(r.WorkflowExecution.ProjectId))
                .Select(r => new { r.Id, r.WorkflowExecutionId, r.WorkflowExecution.ProjectId, r.OutputStorageKey, r.ArtifactStorageKey, r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
                .ToListAsync(ct);
            foreach (var o in owners.Where(o => outputHolds[o.ProjectId].HoldsOutput(o.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(o.FirstStartedAt, o.StartedAt))))
            {
                unavailableResultIds.Add(o.Id);
                unavailableExecutionIds.Add(o.WorkflowExecutionId);
                if (o.OutputStorageKey is not null) unavailableKeys.Add((o.ProjectId, o.OutputStorageKey));
                if (o.ArtifactStorageKey is not null) unavailableKeys.Add((o.ProjectId, o.ArtifactStorageKey));
            }
            files = files.Where(f => !(outputHolds[f.ProjectId].IsActive
                && ReelBolt.Shared.Storage.ProjectOutputHold.IsPlatformMade(f.ProjectId, f.StorageKey, f.OutputProducedAt)
                && (Outputs.OutputGate.HoldsFileStamp(outputHolds[f.ProjectId], ReelBolt.Shared.Storage.ProjectOutputHold.FileStamp(f.OutputProducedAt, f.UploadedAt)) || unavailableKeys.Contains((f.ProjectId, f.StorageKey))))).ToList();
        }

        Add("project_files", files.Select(f => new
        {
            f.Id,
            projectId = f.ProjectId,
            f.OriginalFileName,
            f.OriginalPath,
            f.DirectoryPath,
            f.Category,
            f.MimeType,
            f.SizeBytes,
            f.StorageKey,
            f.StorageBucket,
            f.StorageFileName,
            f.AgentSummary,
            summaryStatus = f.SummaryStatus.ToString(),
            indexingStatus = f.IndexingStatus.ToString(),
            f.UploadedAt
        }), files.Count);

        // A derivative in a marked project is kept only while its source file is one of the files kept above.
        HashSet<Guid> keptFileIds = files.Select(f => f.Id).ToHashSet();
        bool DerivativeUnavailable(MediaDerivative d) =>
            outputHolds.TryGetValue(d.ProjectId, out var hold) && hold.IsActive
            && (hold.Unreadable
                || (d.SourceStepResultId is { } resultId && unavailableResultIds.Contains(resultId))
                || (d.SourceProjectFileId is { } fileId && !keptFileIds.Contains(fileId))
                || (d.SourceStorageKey is not null && unavailableKeys.Contains((d.ProjectId, d.SourceStorageKey))));

        List<MediaDerivative> derivatives = await db.MediaDerivatives.AsNoTracking().IgnoreQueryFilters()
            .Where(d => projectIds.Contains(d.ProjectId)).ToListAsync(ct);
        Add("media_derivatives", derivatives.Select(d => new
        {
            d.Id,
            projectId = d.ProjectId,
            sourceProjectFileId = d.SourceProjectFileId,
            kind = d.Kind.ToString(),
            StorageKey = DerivativeUnavailable(d) ? null : d.StorageKey,
            d.MimeType,
            d.SizeBytes,
            d.Width,
            d.Height,
            d.DurationSec,
            d.CreatedAt,
            d.DeletedAt
        }), derivatives.Count);

        List<WorkflowDefinition> definitions = await db.WorkflowDefinitions.AsNoTracking()
            .Where(w => projectIds.Contains(w.ProjectId)).ToListAsync(ct);
        Guid[] definitionIds = definitions.Select(d => d.Id).ToArray();
        Add("workflow_definitions",
            definitions.Select(w => new { w.Id, projectId = w.ProjectId, w.Name, w.RequiresUserInput, w.CreatedAt, w.UpdatedAt }),
            definitions.Count);

        List<WorkflowStep> steps = await db.WorkflowSteps.AsNoTracking()
            .Where(s => definitionIds.Contains(s.WorkflowDefinitionId))
            .OrderBy(s => s.StepOrder)
            .ToListAsync(ct);
        Add("workflow_steps", steps.Select(s => new
        {
            s.Id,
            workflowDefinitionId = s.WorkflowDefinitionId,
            s.StepOrder,
            s.Label,
            stepType = s.StepType.ToString(),
            s.AgentDefinitionId,
            s.ConditionExpression,
            s.InputMappingJson,
            s.ExtractConfigJson,
            s.VideoAnalyzeConfigJson,
            s.VideoCompileConfigJson,
            s.VideoGenerateConfigJson,
            s.EditRoomConfigJson,
            s.GraphicsRoomConfigJson,
            s.ColorGradeRoomConfigJson,
            s.VoiceoverConfigJson,
            s.ThumbnailGenerateConfigJson,
            s.ConditionalConfigJson,
            s.ReviewLoopConfigJson,
            s.DesignSystemConfigJson,
            s.ContinueCompositionJson,
            s.RegisterOutputAsProjectFile,
            s.ExtractFrameConfigJson,
            s.AudioGenerateConfigJson,
            s.PinInsertConfigJson,
            s.AudioCleanupConfigJson,
            s.VideoEnhanceConfigJson,
            s.ImageGenerateConfigJson
        }), steps.Count);

        bool RunUnavailable(WorkflowExecution e) =>
            outputHolds.TryGetValue(e.ProjectId, out var hold) && hold.IsActive
            && (hold.Unreadable
                || unavailableExecutionIds.Contains(e.Id)
                || hold.HoldsOutput(ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(e.FirstStartedAt, e.StartedAt), ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(e.FirstStartedAt, e.StartedAt)));

        Add("workflow_executions", executions.Select(e => new
        {
            e.Id,
            workflowDefinitionId = e.WorkflowDefinitionId,
            projectId = e.ProjectId,
            organizationId = e.OrganizationId,
            status = e.Status.ToString(),
            e.StartedAt,
            e.CompletedAt,
            e.InitiatedByUserId,
            e.UserRequest,
            e.StyleConfigJson,
            e.RunBriefJson,
            ResultJson = RunUnavailable(e) ? null : e.ResultJson,
            e.ErrorMessage
        }), executions.Count);

        List<WorkflowStepResult> results = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => executionIds.Contains(r.WorkflowExecutionId))
            .OrderBy(r => r.ExecutedAt)
            .ToListAsync(ct);
        if (unavailableResultIds.Count > 0) results = results.Where(r => !unavailableResultIds.Contains(r.Id)).ToList();
        Add("workflow_step_results", results.Select(r => new
        {
            r.Id,
            workflowExecutionId = r.WorkflowExecutionId,
            workflowStepId = r.WorkflowStepId,
            status = r.Status.ToString(),
            r.Output,
            r.OutputJson,
            r.InputJson,
            r.TokensUsed,
            r.InputTokens,
            r.OutputTokens,
            r.DurationMs,
            r.ExecutedAt,
            r.CompletedAt,
            r.OutputStorageKey,
            r.ArtifactStorageKey,
            r.TimelineStorageKey,
            r.FailureClass,
            r.FailureCode
        }), results.Count);

        List<ReviewScore> scores = await db.ReviewScores.AsNoTracking()
            .Where(s => executionIds.Contains(s.WorkflowExecutionId)).ToListAsync(ct);
        Add("review_scores",
            scores.Select(s => new { s.Id, workflowExecutionId = s.WorkflowExecutionId, s.IterationNumber, s.Score, s.Comments, s.CreatedAt }),
            scores.Count);

        List<ExternalGenerationJob> jobs = await db.ExternalGenerationJobs.AsNoTracking()
            .Where(j => projectIds.Contains(j.ProjectId)).ToListAsync(ct);
        Dictionary<Guid, WorkflowExecution> executionsById = executions.ToDictionary(e => e.Id);
        // A job whose run is not served keeps no storage key in the archive; a job with no run to judge it by is treated alike.
        bool JobUnavailable(ExternalGenerationJob j) =>
            outputHolds.TryGetValue(j.ProjectId, out var hold) && hold.IsActive
            && (!executionsById.TryGetValue(j.ExecutionId, out var run) || RunUnavailable(run));

        Add("external_generation_jobs", jobs.Select(j => new
        {
            j.Id,
            executionId = j.ExecutionId,
            stepResultId = j.StepResultId,
            projectId = j.ProjectId,
            organizationId = j.OrganizationId,
            clipId = j.ClipId,
            status = j.Status.ToString(),
            remoteJobId = j.RemoteJobId,
            costEstimate = j.CostEstimate,
            actualCost = j.ActualCost,
            storageKey = JobUnavailable(j) ? null : j.StorageKey,
            j.CreatedAt
        }), jobs.Count);

        List<AssistantThread> threads = await db.AssistantThreads.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.UserId == userId).OrderBy(t => t.CreatedAt).ToListAsync(ct);
        Add("assistant_threads", threads.Select(t => new
        {
            t.Id,
            userId = t.UserId,
            organizationId = t.OrganizationId,
            projectId = t.ProjectId,
            t.Title,
            t.CreatedAt,
            t.UpdatedAt,
            t.ArchivedAt
        }), threads.Count);

        Guid[] threadIds = threads.Select(t => t.Id).ToArray();
        List<AssistantMessage> messages = await db.AssistantMessages.AsNoTracking()
            .Where(m => threadIds.Contains(m.ThreadId)).OrderBy(m => m.Sequence).ToListAsync(ct);
        Add("assistant_messages",
            messages.Select(m => new { m.Id, threadId = m.ThreadId, m.Role, m.Content, m.Sequence, m.MetadataJson, m.CreatedAt }),
            messages.Count);

        List<AgentDefinition> agents = await db.AgentDefinitions.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.OwnerId == userId).ToListAsync(ct);
        Add("agent_definitions", agents.Select(a => new
        {
            a.Id,
            a.Name,
            a.Description,
            a.SystemPrompt,
            agentType = a.AgentType.ToString(),
            organizationId = a.OrganizationId,
            a.ConfigJson,
            a.OutputSchemaJson,
            a.AssignedSkillsJson,
            a.InferenceProviderId,
            a.CreatedAt
        }), agents.Count);

        List<EditingStyle> styles = await db.EditingStyles.AsNoTracking()
            .Where(s => s.OwnerId == userId).ToListAsync(ct);
        Add("editing_styles",
            styles.Select(s => new { s.Id, s.OwnerId, s.ProjectId, s.Name, s.Description, s.ConfigJson, s.CreatedAt, s.UpdatedAt }),
            styles.Count);

        List<ProjectVoice> voices = await db.ProjectVoices.AsNoTracking().IgnoreQueryFilters()
            .Where(v => projectIds.Contains(v.ProjectId)).ToListAsync(ct);
        Add("project_voices", voices.Select(v => new
        {
            v.Id,
            projectId = v.ProjectId,
            v.Name,
            v.Description,
            source = v.Source.ToString(),
            v.RemoteVoiceId,
            v.ReferenceProjectFileIdsJson,
            v.ConsentAttestedByUserId,
            v.ConsentAttestedAt,
            v.ConsentStatement,
            v.ConsentSubjectName,
            v.ConsentRevokedAt,
            v.ConsentRevokedByUserId,
            v.RetentionUntil,
            v.CreatedByUserId,
            v.CreatedAt
        }), voices.Count);

        // Avatars (docs/design/avatars.md): the consent record and the references (files are in files/), vendor-copy
        // outcomes inside the same document; the audit events of these projects and of every action the user took.
        List<ProjectAvatar> avatars = await db.ProjectAvatars.AsNoTracking()
            .Where(a => projectIds.Contains(a.ProjectId)).ToListAsync(ct);
        Guid[] avatarIds = avatars.Select(a => a.Id).ToArray();
        List<ProjectAvatarReference> avatarReferences = await db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => avatarIds.Contains(r.AvatarId)).ToListAsync(ct);
        List<ProjectAvatarVendorAsset> avatarAssets = await db.ProjectAvatarVendorAssets.AsNoTracking()
            .Where(v => avatarIds.Contains(v.AvatarId)).ToListAsync(ct);
        Add("project_avatars", avatars.Select(a => new
        {
            a.Id,
            projectId = a.ProjectId,
            // Person or Object (docs/design/avatar-objects.md): for an object the consent fields below hold the
            // member's own object statement, and the keep-until date is the "never" value.
            subjectKind = a.SubjectKind.ToString(),
            a.Name,
            a.Description,
            a.ProjectVoiceId,
            a.ConsentAttestedByUserId,
            a.ConsentAttestedAt,
            a.ConsentStatement,
            a.ConsentStatementVersion,
            a.ConsentLocale,
            a.ConsentSubjectName,
            consentBasis = a.ConsentBasis.ToString(),
            a.ConsentSubjectAdult,
            a.ConsentDocumentFileId,
            a.ConsentRevokedAt,
            a.ConsentRevokedByUserId,
            a.RetentionUntil,
            disclosureLabel = a.DisclosureLabel.ToString(),
            a.CreatedByUserId,
            a.CreatedAt,
            a.UpdatedAt,
            // Set when the avatar is a linked import of a workspace avatar library item (below).
            a.LibraryItemId,
            references = avatarReferences.Where(r => r.AvatarId == a.Id).OrderBy(r => r.Ordinal).Select(r => new
            {
                r.Id,
                r.ProjectFileId,
                kind = r.Kind.ToString(),
                role = r.Role.ToString(),
                r.Ordinal,
                r.ContentSha256,
                r.Width,
                r.Height,
                r.DurationMs,
                r.AttachedAt
            }),
            vendorCopies = avatarAssets.Where(v => v.AvatarId == a.Id).Select(v => new
            {
                v.Id,
                v.UploadedAt,
                v.VendorExpiresAt,
                v.DeletedAt,
                deleteOutcome = v.DeleteOutcome?.ToString()
            })
        }), avatars.Count);

        // The workspace avatar library of the personal workspace(s) (docs/design/avatar-objects.md): each item with
        // its consent record and the facts of its files. No media and no storage key: the library's own copies are not
        // packed (an import's files are project files and are in the files/ folder).
        List<AvatarLibraryItem> libraryItems = await db.AvatarLibraryItems.AsNoTracking().IgnoreQueryFilters()
            .Where(i => personalOrgIds.Contains(i.OrganizationId)).ToListAsync(ct);
        Guid[] libraryItemIds = libraryItems.Select(i => i.Id).ToArray();
        List<AvatarLibraryReference> libraryReferences = await db.AvatarLibraryReferences.AsNoTracking()
            .Where(r => libraryItemIds.Contains(r.LibraryItemId)).ToListAsync(ct);
        Add("avatar_library_items", libraryItems.Select(i => new
        {
            i.Id,
            i.OrganizationId,
            subjectKind = i.SubjectKind.ToString(),
            i.Name,
            i.Description,
            i.ConsentAttestedByUserId,
            i.ConsentAttestedAt,
            i.ConsentStatement,
            i.ConsentStatementVersion,
            i.ConsentLocale,
            i.ConsentSubjectName,
            consentBasis = i.ConsentBasis.ToString(),
            i.ConsentSubjectAdult,
            hasConsentDocument = i.ConsentDocumentStorageKey != null,
            i.ConsentRevokedAt,
            i.ConsentRevokedByUserId,
            i.RetentionUntil,
            disclosureLabel = i.DisclosureLabel.ToString(),
            i.CreatedByUserId,
            i.CreatedAt,
            i.UpdatedAt,
            references = libraryReferences.Where(r => r.LibraryItemId == i.Id).OrderBy(r => r.Ordinal).Select(r => new
            {
                r.Id,
                kind = r.Kind.ToString(),
                role = r.Role.ToString(),
                r.Ordinal,
                r.ContentSha256,
                r.MimeType,
                r.FileName,
                r.SizeBytes,
                r.Width,
                r.Height,
                r.DurationMs,
                r.AttachedAt
            })
        }), libraryItems.Count);

        // A library item's own events carry its id as avatar id and may name no project, so they are matched by id.
        List<ProjectAvatarEvent> avatarEvents = await db.ProjectAvatarEvents.AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId) || e.ActorUserId == userId || libraryItemIds.Contains(e.AvatarId))
            .OrderBy(e => e.OccurredAt).ToListAsync(ct);
        Add("project_avatar_events", avatarEvents.Select(e => new
        {
            e.Id,
            e.AvatarId,
            projectId = e.ProjectId,
            e.OrganizationId,
            kind = e.Kind.ToString(),
            e.ActorUserId,
            e.OccurredAt,
            e.ConsentStatementVersion,
            e.OutcomesJson
        }), avatarEvents.Count);

        List<ProjectPronunciationEntry> pronunciations = await db.ProjectPronunciationEntries.AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId)).ToListAsync(ct);
        Add("project_pronunciation_entries", pronunciations.Select(e => new
        {
            e.Id,
            projectId = e.ProjectId,
            e.Term,
            e.Spelling,
            e.CaseSensitive,
            e.CreatedAt,
            e.UpdatedAt
        }), pronunciations.Count);

        Add("edit_timelines", timelines.Select(t => new
        {
            t.Id,
            projectId = t.ProjectId,
            t.Name,
            t.DocumentJson,
            t.Revision,
            t.SeedStepResultId,
            t.CreatedAt,
            t.UpdatedAt,
            t.DeletedAt
        }), timelines.Count);

        List<EditTimelineOp> ops = await db.EditTimelineOps.AsNoTracking()
            .Where(o => timelineIds.Contains(o.TimelineId)).OrderBy(o => o.Revision).ToListAsync(ct);
        Add("edit_timeline_ops",
            ops.Select(o => new { o.Id, timelineId = o.TimelineId, o.Revision, o.OpsJson, o.CreatedAt }),
            ops.Count);

        List<EditTimelineSnapshot> snapshots = await db.EditTimelineSnapshots.AsNoTracking()
            .Where(s => timelineIds.Contains(s.TimelineId)).OrderBy(s => s.Revision).ToListAsync(ct);
        Add("edit_timeline_snapshots",
            snapshots.Select(s => new { s.Id, timelineId = s.TimelineId, s.Revision, s.DocumentJson, s.CreatedAt }),
            snapshots.Count);

        // Metadata only: the stored API key is a credential, and C0 says "Metadata only, never the key".
        // One entry per model capability, flattened with its model and provider account (the shape the
        // export had when a provider row was one capability; ids are the capability ids).
        List<ModelCapabilityView> providers = await db.ModelCapabilities.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.OrganizationId != null && memberOrgIds.Contains(c.OrganizationId.Value))
            .Select(ModelCapabilityView.Projection)
            .ToListAsync(ct);
        Add("inference_providers", providers.Select(p => new
        {
            p.Id,
            organizationId = p.OrganizationId,
            p.Name,
            kind = p.Kind.ToString(),
            capability = p.Capability.ToString(),
            p.Endpoint,
            p.ModelName,
            p.IsDefault,
            p.IsEnabled,
            p.ApiKeyLastFour,
            p.CreatedAt
        }), providers.Count);

        List<UsageEvent> usage = await db.UsageEvents.AsNoTracking()
            .Where(u => u.UserId == userId || (u.ProjectId != null && projectIds.Contains(u.ProjectId.Value)))
            .OrderBy(u => u.OccurredAt)
            .ToListAsync(ct);
        Add("usage_events", usage.Select(u => new
        {
            u.Id,
            organizationId = u.OrganizationId,
            projectId = u.ProjectId,
            executionId = u.ExecutionId,
            stepResultId = u.StepResultId,
            userId = u.UserId,
            source = u.Source.ToString(),
            kind = u.Kind.ToString(),
            u.Quantity,
            u.ModelName,
            billingMode = u.BillingMode.ToString(),
            u.CreditsMillis,
            u.CostUsd,
            priceId = u.PriceId,
            u.Variant,
            u.Unpriced,
            u.OccurredAt
        }), usage.Count);

        List<CreditAccount> accounts = await db.CreditAccounts.AsNoTracking()
            .Where(a => memberOrgIds.Contains(a.OrganizationId)).ToListAsync(ct);
        Add("credit_accounts",
            accounts.Select(a => new { a.Id, organizationId = a.OrganizationId, a.BalanceMillis, a.ReservedMillis, a.UpdatedAt }),
            accounts.Count);

        Guid[] accountIds = accounts.Select(a => a.Id).ToArray();
        List<CreditLedgerEntry> ledger = await db.CreditLedgerEntries.AsNoTracking()
            .Where(e => accountIds.Contains(e.AccountId)).OrderBy(e => e.CreatedAt).ToListAsync(ct);
        Add("credit_ledger_entries", ledger.Select(e => new
        {
            e.Id,
            accountId = e.AccountId,
            kind = e.Kind.ToString(),
            e.BalanceDeltaMillis,
            e.ReservedDeltaMillis,
            e.BalanceAfterMillis,
            e.Note,
            e.ExecutionId,
            e.CreatedAt
        }), ledger.Count);

        List<Subscription> subscriptions = await db.Subscriptions.AsNoTracking()
            .Where(s => memberOrgIds.Contains(s.OrganizationId)).ToListAsync(ct);
        Add("subscriptions", subscriptions.Select(s => new
        {
            s.Id,
            organizationId = s.OrganizationId,
            planId = s.PlanId,
            s.Provider,
            s.ProviderSubscriptionId,
            status = s.Status.ToString(),
            s.PeriodStart,
            s.PeriodEnd,
            s.Seats,
            s.CancelAtPeriodEnd,
            s.CreatedAt
        }), subscriptions.Count);

        List<BillingCustomer> customers = await db.BillingCustomers.AsNoTracking()
            .Where(c => memberOrgIds.Contains(c.OrganizationId)).ToListAsync(ct);
        Add("billing_customers",
            customers.Select(c => new { c.Id, organizationId = c.OrganizationId, c.Provider, c.ProviderCustomerId, c.Email, c.CreatedAt }),
            customers.Count);

        // The raw provider payload is deliberately NOT exported: it repeats the provider's own record
        // and can carry payment detail that is theirs to hand over (C0 links to their portal). What we
        // did with each event is ours, and that is what this section is.
        List<BillingWebhookEvent> webhooks = await db.BillingWebhookEvents.AsNoTracking()
            .OrderBy(e => e.ReceivedAt).ToListAsync(ct);
        Add("billing_webhook_events",
            webhooks.Select(e => new { e.Id, e.Provider, e.EventId, e.EventType, e.ReceivedAt, e.ProcessedAt, e.Attempts, e.Error }),
            webhooks.Count);

        // saas-promotions: the person's marketing consent and its full event history (GDPR 15/20).
        // Consent is per email, so the row is found by the account id or, once the account has proven
        // it owns its address, by that address: an unverified account never gets the consent (and IP
        // evidence) of whoever subscribed with the address it typed (S-02).
        string? consentEmail = user.EmailVerifiedAt is null ? null : MarketingEmail.Normalize(user.Email);
        MarketingConsent? consent = await db.MarketingConsents.AsNoTracking()
            .Where(c => c.UserId == userId || (consentEmail != null && c.Email == consentEmail))
            .OrderByDescending(c => c.UserId == userId)
            .FirstOrDefaultAsync(ct);
        List<MarketingConsentEvent> consentEvents = consent is null
            ? []
            : await db.MarketingConsentEvents.AsNoTracking()
                .Where(e => e.ConsentId == consent.Id)
                .OrderBy(e => e.OccurredAt)
                .ToListAsync(ct);
        Add("marketing_consents", new
        {
            consent = consent is null ? null : new
            {
                email = consent.Email,
                status = MarketingConsentService.StatusName(consent.Status),
                wordingVersion = consent.WordingVersion,
                source = MarketingConsentService.SourceName(consent.Source),
                consent.RequestedAt,
                consent.ConsentedAt,
                consent.ConfirmedAt,
                consent.WithdrawnAt,
                ip = consent.Ip,
                userAgent = consent.UserAgent,
                consent.UpdatedAt,
                firstName = consent.FirstName,
                lastName = consent.LastName,
                nameSource = consent.NameSource?.ToString()
            },
            events = consentEvents.Select(e => new
            {
                action = JsonNamingPolicy.CamelCase.ConvertName(e.Action.ToString()),
                source = MarketingConsentService.SourceName(e.Source),
                wordingVersion = e.WordingVersion,
                ip = e.Ip,
                userAgent = e.UserAgent,
                e.OccurredAt
            })
        }, (consent is null ? 0 : 1) + consentEvents.Count);

        // T-019: the user's own bug reports and feedback — the only user prose the platform stores.
        // Exported verbatim (GDPR 15/20): the message and the context they chose to attach.
        List<FeedbackReport> feedbackReports = await db.FeedbackReports.AsNoTracking()
            .Where(r => r.UserId == userId).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        Add("feedback_reports", feedbackReports.Select(r => new
        {
            r.Id,
            userId = r.UserId,
            r.Kind,
            r.Message,
            contextJson = r.ContextJson,
            r.CreatedAt
        }), feedbackReports.Count);

        // New-user funnel: the welcome answers. The persona and the material are the person's own words
        // about themselves (closed tokens, no free text); suggestedPersona is the audience page they
        // signed up from. One row per user at most; the document is empty for a person who was never asked.
        UserOnboardingProfile? onboardingProfile = await db.UserOnboardingProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        Add("user_onboarding_profiles", new
        {
            profile = onboardingProfile is null ? null : new
            {
                persona = onboardingProfile.Persona,
                material = onboardingProfile.Material,
                suggestedPersona = onboardingProfile.SuggestedPersona,
                pickedPlan = onboardingProfile.PickedPlan,
                onboardingProfile.AnsweredAt,
                onboardingProfile.SkippedAt,
                onboardingProfile.CreatedAt,
                onboardingProfile.UpdatedAt
            }
        }, onboardingProfile is null ? 0 : 1);

        // The first touch recorded on the account: closed-shape tokens only (no IP, no user agent, no
        // free text). visitorId is the random id of the browser the consented cookie was set in.
        ReelBolt.Inference.Api.Services.Telemetry.UserAttribution? attribution = await db.UserAttributions.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId, ct);
        Add("user_attribution", new
        {
            attribution = attribution is null ? null : new
            {
                sourceLabel = attribution.SourceLabel,
                utmSource = attribution.UtmSource,
                utmMedium = attribution.UtmMedium,
                utmCampaign = attribution.UtmCampaign,
                utmContent = attribution.UtmContent,
                utmTerm = attribution.UtmTerm,
                referrerHost = attribution.ReferrerHost,
                landingPath = attribution.LandingPath,
                visitorId = attribution.VisitorId,
                attribution.FirstTouchAt,
                capture = attribution.Capture,
                attribution.CreatedAt
            }
        }, attribution is null ? 0 : 1);

        // --- The user's original files, up to the packing cap. ---
        long budget = Math.Max(0, Options.MaxPackedBytes);
        long packed = 0;
        List<AccountExportFile> packedFiles = [];
        List<AccountExportSkippedFile> skipped = [];
        // A generated output is packed only once it has a verdict that lets it go (allowed, or wordless);
        // pending, blocked and unjudged ones are left out and counted in the manifest.
        Dictionary<(Guid, string), string> cleared = (await db.OutputModerationVerdicts.AsNoTracking()
                .Where(v => projectIds.Contains(v.ProjectId)
                            && (v.State == "Allow" || v.State == "Review"
                                || (v.State == "Unavailable" && v.ReasonCode == "nothing_to_classify")))
                .Select(v => new { v.ProjectId, v.StorageKey, v.SourceVersion })
                .ToListAsync(ct))
            .ToDictionary(v => (v.ProjectId, v.StorageKey), v => v.SourceVersion);
        foreach (ProjectFile file in files)
        {
            string entryPath = $"{AccountExportSections.FilesFolder}/{file.ProjectId:N}/{file.OriginalPath ?? file.OriginalFileName}";
            bool isOutput = ReelBolt.Shared.Storage.ProjectOutputKeys.IsAddressableOutput(file.ProjectId, file.StorageKey);
            if (isOutput && !cleared.ContainsKey((file.ProjectId, file.StorageKey)))
            {
                skipped.Add(new AccountExportSkippedFile(file.OriginalFileName, file.SizeBytes, file.ProjectId, AccountExportSkippedFile.NotYetCleared));
                continue;
            }

            if (budget - packed < file.SizeBytes)
            {
                skipped.Add(new AccountExportSkippedFile(file.OriginalFileName, file.SizeBytes, file.ProjectId, "over the size cap"));
                continue;
            }

            try
            {
                using GetObjectResponse response = await s3.GetObjectAsync(new GetObjectRequest
                {
                    BucketName = _bucket,
                    Key = file.StorageKey
                }, ct);
                if (isOutput)
                {
                    // Only the bytes that were checked: a rewritten object is held back too.
                    string etag = response.ETag?.Trim().Trim('"') ?? string.Empty;
                    string version = string.IsNullOrEmpty(etag) ? $"len:{response.ContentLength}" : $"{etag}:{response.ContentLength}";
                    if (!string.Equals(cleared[(file.ProjectId, file.StorageKey)], version, StringComparison.Ordinal))
                    {
                        skipped.Add(new AccountExportSkippedFile(file.OriginalFileName, file.SizeBytes, file.ProjectId, AccountExportSkippedFile.NotYetCleared));
                        continue;
                    }
                }

                using var buffer = new MemoryStream();
                await response.ResponseStream.CopyToAsync(buffer, ct);
                packedFiles.Add(new AccountExportFile(entryPath, buffer.ToArray()));
                packed += buffer.Length;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading {StorageKey} for the export of user {UserId} failed", file.StorageKey, userId);
                skipped.Add(new AccountExportSkippedFile(file.OriginalFileName, file.SizeBytes, file.ProjectId, "could not be read from storage"));
            }
        }

        return new AccountExportContent(documents, rows, packedFiles, skipped);
    }

    private static string BuildManifest(Guid userId, string email, Guid exportId, DateTime now, AccountExportContent content)
    {
        object manifest = new
        {
            exportId,
            generatedAt = now,
            userId,
            email,
            software = "ReelBolt",
            formatVersion = 1,
            note = "This archive holds the data ReelBolt keeps about your account and your personal workspace, as "
                 + "listed in docs/design/saas-accounts.md. Content inside a team workspace is not included: it "
                 + "belongs to the team.",
            sections = AccountExportSections.All.Select(s => new
            {
                table = s.Table,
                file = $"{AccountExportSections.DataFolder}/{s.FileName}",
                description = s.Description
            }),
            files = new
            {
                packed = content.Files.Count,
                packedBytes = content.Files.Sum(f => (long)f.Bytes.Length),
                notYetCleared = content.SkippedFiles.Count(f => f.Reason == AccountExportSkippedFile.NotYetCleared),
                notYetClearedNote = "Generated files that are still being checked, or were not cleared, are left out of this archive. Ask again later for the first group.",
                skipped = content.SkippedFiles.Select(f => new { f.Name, f.Bytes, f.ProjectId, f.Reason })
            },
            notExported = AccountExportSections.DeliberatelyExcluded.Select(kv => new { table = kv.Key, reason = kv.Value })
        };

        return JsonSerializer.Serialize(manifest, Json);
    }

    private static void WriteEntry(ZipArchive archive, string name, string json)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write(json);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    // ---------------------------------------------------------------------------------------------
    // Object-store plumbing
    // ---------------------------------------------------------------------------------------------

    /// <summary>The per-user export prefix, outside every project prefix.</summary>
    public string UserPrefix(Guid userId) => $"{Options.Prefix.TrimEnd('/')}/{userId:N}";

    /// <summary>The throttle marker's key.</summary>
    public string MarkerKey(Guid userId) => $"{UserPrefix(userId)}/{MarkerFileName}";

    private async Task<DateTime?> ReadMarkerAsync(IAmazonS3 s3, Guid userId, CancellationToken ct)
    {
        try
        {
            GetObjectMetadataResponse metadata = await s3.GetObjectMetadataAsync(_bucket, MarkerKey(userId), ct);
            return metadata.LastModified;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex)
        {
            // A store that cannot answer means "no recent request on record": a store hiccup must never
            // become a permanent refusal.
            _logger.LogWarning(ex, "Reading the export marker of user {UserId} failed", userId);
            return null;
        }
    }

    private async Task WriteMarkerAsync(IAmazonS3 s3, Guid userId, CancellationToken ct)
    {
        using var empty = new MemoryStream();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = MarkerKey(userId),
            InputStream = empty,
            ContentType = "application/json"
        }, ct);
    }

    private async Task<string?> MintLinkAsync(IAmazonS3 presign, string key, TimeSpan ttl, CancellationToken ct)
    {
        try
        {
            return await presign.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(ttl)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Minting a download link for {Key} failed", key);
            return null;
        }
    }
}

/// <summary>One queued export request.</summary>
public sealed record AccountExportJob(Guid UserId, string Email, string DisplayName);

/// <summary>
/// <c>GET /api/v1/account/export/status</c>. Every time is UTC; a null means "there is none"
/// (no archive, or no throttle in force).
/// </summary>
public sealed record AccountExportStatus(
    bool Enabled,
    bool Preparing,
    DateTime? LatestCreatedAt,
    long? LatestBytes,
    DateTime? LatestExpiresAt,
    DateTime? NextRequestAt);

/// <summary>A rendered C0 section, ready to be written into the archive.</summary>
public sealed record AccountExportDocument(string FileName, string Json);

/// <summary>A file copied into the archive.</summary>
public sealed record AccountExportFile(string EntryPath, byte[] Bytes);

/// <summary>A file the manifest names instead of packing.</summary>
public sealed record AccountExportSkippedFile(string Name, long Bytes, Guid ProjectId, string Reason)
{
    /// <summary>Neutral wording for a generated file that is not packed because it has no clearing check yet.</summary>
    public const string NotYetCleared = "not yet cleared for export";
}

/// <summary>Everything one export contains.</summary>
public sealed record AccountExportContent(
    IReadOnlyList<AccountExportDocument> Documents,
    int Rows,
    IReadOnlyList<AccountExportFile> Files,
    IReadOnlyList<AccountExportSkippedFile> SkippedFiles);
