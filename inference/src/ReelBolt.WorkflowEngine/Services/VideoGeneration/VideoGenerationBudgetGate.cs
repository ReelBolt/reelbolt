using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.VideoGeneration;

/// <summary>Bound from the <c>VideoGeneration</c> configuration section (Engine only).</summary>
public sealed class VideoGenerationOptions
{
    public const string SectionName = "VideoGeneration";

    /// <summary>
    /// OPTIONAL operator per-project daily cap on <c>VideoGenerate</c> spend, in CREDITS. Unset by default: no per-project
    /// limit ships any more (the daily limit is the user's own setting, docs/design/video-spend-limit.md). Kept only so a
    /// self-host operator who set it keeps working; when set, a cap ≤ 0 denies every credit reservation.
    /// </summary>
    public decimal? DailyBudgetCredits { get; set; }

    /// <summary>
    /// The operator's runaway-spend SAFETY CEILING on <c>VideoGenerate</c> spend across all tenants and projects per UTC day,
    /// in CREDITS (the shipped default is the credit equivalent of $5000, far above any normal day, never presented as a
    /// limit to users); a ceiling ≤ 0 denies every credit reservation.
    /// </summary>
    public decimal GlobalDailyBudgetCredits { get; set; } = VideoBudgetAccounting.DefaultGlobalDailyCredits;

    public int MaxConcurrentPerProvider { get; set; } = 2;

    /// <summary>Per-project daily GPU-minutes cap for self-hosted (ComfyUI) generation; default 120, ≤ 0 denies every GPU reservation.</summary>
    public decimal DailyGpuMinutes { get; set; } = VideoBudgetAccounting.DefaultProjectDailyGpuMinutes;

    /// <summary>Daily GPU-minutes cap across all projects for self-hosted generation; default 240, ≤ 0 denies every GPU reservation.</summary>
    public decimal GlobalDailyGpuMinutes { get; set; } = VideoBudgetAccounting.DefaultGlobalDailyGpuMinutes;

    /// <summary>
    /// How many times one take (same execution and request hash) may be abandoned and re-bought by a new
    /// attempt (Resume/Retry) when an earlier attempt left it Reserved with no remote job id. Default 1: one
    /// re-buy. Beyond it the take ends <c>RESERVATION_ORPHANED</c> with no submit. ≤ 0 never abandons a take the
    /// attempt asks for (it stays existing and ends <c>RESERVATION_ORPHANED</c>). The limit does not apply to an earlier
    /// attempt's orphan of a request hash the attempt no longer asks for (a Retry from an earlier step or a re-planned
    /// ShotDirector changed the prompt): that row is never re-bought, so it is abandoned whatever this value, and like
    /// every Abandoned row it keeps counting toward the step and daily caps.
    /// </summary>
    public int MaxAbandonsPerTake { get; set; } = 1;
}

/// <summary>
/// The one atomic reserve-or-deny for video generation spend. See <see cref="IVideoGenerationBudgetGate"/>
/// for the ordered contract; the unit (<see cref="VideoGenerationCostUnit"/>) selects WHICH caps
/// and WHICH ledger rows apply, and nothing else changes between the two — the same lock, the same
/// transaction, the same advisory lock, the same row shape.
/// </summary>
public class VideoGenerationBudgetGate : IVideoGenerationBudgetGate
{
    private static readonly SemaphoreSlim ReservationLock = new(1, 1);

    private const long AdvisoryLockKey = 0x5246_5649_4447_454E;

    private readonly IDbContextFactory<WorkflowEngineDbContext> _dbContextFactory;
    private readonly VideoGenerationOptions _options;

    /// <summary>Per-org daily cap source (A12); null means no org tier (unit tests, self-host wiring).</summary>
    private readonly IOrganizationEntitlements? _entitlements;

    public VideoGenerationBudgetGate(
        IDbContextFactory<WorkflowEngineDbContext> dbContextFactory,
        IOptions<VideoGenerationOptions> options,
        IOrganizationEntitlements? entitlements = null)
    {
        _entitlements = entitlements;
        _dbContextFactory = dbContextFactory;
        _options = options.Value;
    }

    public async Task<VideoGenerationReservation> ReserveAsync(
        VideoGenerationReservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<VideoGenerationTakeRequest> requested =
            request.Takes ?? Array.Empty<VideoGenerationTakeRequest>();

        // The unit decides the vocabulary of every message and the caps below. GPU-seconds rows are
        // written with "gpuseconds", credit rows with "credits" — the column default, so rows written
        // before the credit rename (which have no unit of their own) read as credits, as the
        // ExternalGenerationJobsCostCredits migration converted them.
        bool gpu = request.Unit == VideoGenerationCostUnit.GpuSeconds;
        string unitTag = gpu ? ExternalGenerationJob.GpuSecondsUnit : ExternalGenerationJob.CreditsUnit;
        string capName = gpu ? "MaxGpuMinutes" : "MaxSpendCredits";
        string Money(decimal v) => gpu ? $"{v:0.##} GPU-seconds" : $"{v} credits";

        // (1) Malformed request: nothing to lock for, nothing written.
        //
        // (1b) Quote provenance is checked HERE, in the same pre-lock block, deliberately: the
        // oracle-pricing rule makes "the ledger records the vendor's exact figure" an invariant of the component
        // that writes money, not a convention of one caller. A take that arrives carrying a quote but
        // a DIFFERENT CostEstimate is refused with zero rows, so neither a re-multiplied per-second
        // rate nor a drifted total can ever be persisted for an oracle-priced model. The vendor's own
        // figure is in dollars; the ledger is in credits, so the expected value is the quote's total
        // converted by the ONE converter (<see cref="CreditCurrency.CreditsFromUsd"/>). It sits
        // BEFORE the ReservationLock and before any insert, so a refusal costs nothing and serializes
        // nothing. A take with a null Quote — every table-priced (MiniMax) take — is unaffected.
        var quotedMismatch = requested.FirstOrDefault(
            t => t.Quote is not null && t.CostEstimate != CreditCurrency.CreditsFromUsd(t.Quote.TotalUsd));

        bool audio = request.Scope == VideoGenerationBudgetScope.Audio;
        bool enhance = request.Scope == VideoGenerationBudgetScope.Enhance;
        bool image = request.Scope == VideoGenerationBudgetScope.Image;
        bool creditsOnly = audio || enhance || image;
        // Audio, enhance and image rows are invisible to the video daily sums by their clip id, so each scope accepts only
        // its own ids and a video request carries none of them.
        var outOfScope = requested.FirstOrDefault(t => audio
            ? !VideoBudgetAccounting.IsAudioClipId(t.ClipId)
            : enhance
                ? !VideoBudgetAccounting.IsEnhanceClipId(t.ClipId)
                : image
                    ? !VideoBudgetAccounting.IsImageClipId(t.ClipId)
                    : VideoBudgetAccounting.IsAudioClipId(t.ClipId) || VideoBudgetAccounting.IsEnhanceClipId(t.ClipId) || VideoBudgetAccounting.IsImageClipId(t.ClipId));

        var configError = requested.Count == 0
            ? "no takes requested"
            : requested.Any(t => t.CostEstimate <= 0m)
                ? "every take's CostEstimate must be positive"
                : request.StepCap <= 0m
                    ? $"{capName} must be positive"
                    : request.StepResultId == Guid.Empty
                        ? "StepResultId is required"
                        : quotedMismatch is not null
                            ? $"take '{quotedMismatch.ClipId}' carries the vendor's quote of "
                                + $"${quotedMismatch.Quote!.TotalUsd} ({Money(CreditCurrency.CreditsFromUsd(quotedMismatch.Quote!.TotalUsd))}) but a CostEstimate of "
                                + $"{Money(quotedMismatch.CostEstimate)}; a quoted take must be charged the vendor's own figure"
                            : outOfScope is not null
                                ? audio
                                    ? $"take '{outOfScope.ClipId}' is not an audio clip id; an audio reservation needs one"
                                    : enhance
                                        ? $"take '{outOfScope.ClipId}' is not an enhance clip id; an enhance reservation needs one"
                                        : image
                                            ? $"take '{outOfScope.ClipId}' is not an image clip id; an image reservation needs one"
                                            : $"take '{outOfScope.ClipId}' carries an audio, enhance or image clip id; it would escape the video caps"
                                : creditsOnly && gpu
                                    ? "an audio, enhance or image reservation is priced in credits"
                                    : null;
        if (configError is not null)
        {
            return Deny($"CONFIG_INVALID: {configError}", 0m, 0m, 0m);
        }

        await ReservationLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            await using var tx = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(ct)
                : null;

            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            }

            // (2) Crash-resume: rows this execution already reserved/submitted for the same request.
            var hashes = requested.Select(t => t.RequestHash).Distinct().ToList();
            var existingRows = await db.ExternalGenerationJobs
                .Where(j => j.ExecutionId == request.ExecutionId
                    && hashes.Contains(j.RequestHash)
                    && (j.Status == ExternalGenerationJobStatus.Reserved
                        || j.Status == ExternalGenerationJobStatus.Submitted))
                .OrderBy(j => j.CreatedAt)
                .ToListAsync(ct);

            // (2b) An earlier attempt's orphan is abandoned (a product decision): on a new
            // attempt of the run, a take that attempt left Reserved with no RemoteJobId is given up and bought
            // again under a new row, so the user's run completes. Tracked here, saved only with the new rows:
            // a denial below leaves it exactly as it was. The orphan may still have reached the vendor, so an
            // Abandoned row is NOT released from any cap: it keeps counting toward the step cap
            // and, for its UTC day, the daily caps at its reserved amount. Only the user is never billed for
            // it. And one take is abandoned at most MaxAbandonsPerTake times per execution; past that it stays
            // existing and the executor ends it RESERVATION_ORPHANED with no submit.
            //
            // The step's earlier rows are found by STEP, not by hash (Security review): the hash carries the
            // prompt, which a Retry from an earlier step or a re-planned ShotDirector changes. Every row an
            // earlier step result of this step order left Reserved with no RemoteJobId, or Abandoned, counts
            // toward the step cap whatever its hash. An earlier attempt's orphan of a hash this attempt no
            // longer asks for is abandoned too (it stops looking in flight to the sweeper) without using up
            // any requested take's MaxAbandonsPerTake.
            var requestedHashes = hashes.ToHashSet(StringComparer.Ordinal);
            var step = await EarlierStepRowsAsync(db, request, ct);
            var abandonsByHash = step.Rows
                .Where(j => j.Status == ExternalGenerationJobStatus.Abandoned)
                .GroupBy(j => j.RequestHash, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var priorAbandonedSameHash = await db.ExternalGenerationJobs
                .Where(j => j.ExecutionId == request.ExecutionId
                    && hashes.Contains(j.RequestHash)
                    && j.Status == ExternalGenerationJobStatus.Abandoned)
                .ToListAsync(ct);
            foreach (var group in priorAbandonedSameHash.Where(j => !step.Rows.Contains(j)).GroupBy(j => j.RequestHash, StringComparer.Ordinal))
            {
                abandonsByHash[group.Key] = abandonsByHash.GetValueOrDefault(group.Key) + group.Count();
            }

            var now = DateTime.UtcNow;
            var abandoned = new List<ExternalGenerationJob>();
            foreach (var row in step.Rows.Where(j => j.Status == ExternalGenerationJobStatus.Reserved
                && step.EarlierAttemptOwners.Contains(j.StepResultId)).OrderBy(j => j.CreatedAt))
            {
                if (requestedHashes.Contains(row.RequestHash))
                {
                    int used = abandonsByHash.GetValueOrDefault(row.RequestHash);
                    if (used >= _options.MaxAbandonsPerTake)
                    {
                        continue;
                    }

                    abandonsByHash[row.RequestHash] = used + 1;
                }

                row.Status = ExternalGenerationJobStatus.Abandoned;
                row.UpdatedAt = now;
                abandoned.Add(row);
            }

            existingRows.RemoveAll(abandoned.Contains);

            // What this step's earlier rows may already have cost at the vendor (every cycle, every hash), plus
            // any same-hash Abandoned row of the execution the step lookup could not place. A row returned as
            // existing below is the take itself and is counted once, through the request.
            var reused = existingRows.ToHashSet();
            var abandonedCharge = step.Rows.Concat(priorAbandonedSameHash).Distinct()
                .Where(j => j.CostUnit == unitTag && !reused.Contains(j))
                .Sum(j => Charge(j.CostEstimate, j.ActualCost));

            var existingByHash = new Dictionary<string, ExternalGenerationJob>(StringComparer.Ordinal);
            foreach (var row in existingRows)
            {
                existingByHash.TryAdd(row.RequestHash, row);
            }

            var newTakes = requested.Where(t => !existingByHash.ContainsKey(t.RequestHash)).ToList();
            var estimated = newTakes.Sum(t => t.CostEstimate);
            var stepTotal = requested.Sum(t => t.CostEstimate) + abandonedCharge;

            // Today's ledger, IN THIS UNIT ONLY. A row is charged max(estimate, actual), floored at
            // 0, so no row can ever credit the ledger. Cancelled and Failed rows are released; an Abandoned
            // row is not (it may have been paid at the vendor), nor is a row abandoned just above, which
            // the query still reads as Reserved. A USD row never counts toward a GPU cap and vice versa —
            // they are not the same quantity.
            var today = DateTime.UtcNow.Date;
            var ledger = await VideoBudgetAccounting.TodayRows(db.ExternalGenerationJobs, unitTag, today)
                .ToListAsync(ct);
            var globalSpent = ledger.Sum(r => Charge(r.CostEstimate, r.ActualCost));
            var projectSpent = ledger
                .Where(r => r.ProjectId == request.ProjectId)
                .Sum(r => Charge(r.CostEstimate, r.ActualCost));

            // The owner of the run (the member who started it): whose own daily limit applies. Null for a run with
            // no initiating user (a schedule), which has no user limit.
            Guid? userId = await db.WorkflowExecutions
                .AsNoTracking()
                .Where(e => e.Id == request.ExecutionId)
                .Select(e => e.InitiatedByUserId)
                .FirstOrDefaultAsync(ct);

            // Tenancy (A2): the paying organization, read from the project. Null when unknown.
            Guid? organizationId = await db.Projects
                .AsNoTracking()
                .Where(p => p.Id == request.ProjectId)
                .Select(p => (Guid?)p.OrganizationId)
                .FirstOrDefaultAsync(ct);

            var nowUtc = DateTime.UtcNow;
            var unit = gpu ? VideoGenerationCostUnit.GpuSeconds : VideoGenerationCostUnit.Credits;
            VideoGenerationReservation Refuse(BudgetCapKind cap, decimal capValue, decimal spent, decimal needs) =>
                DenyBudget(
                    new BudgetRefusal(
                        cap, unit, capValue, spent, needs,
                        cap == BudgetCapKind.StepLimit ? null : VideoBudgetMessages.NextResetUtc(nowUtc)),
                    nowUtc, estimated, projectSpent, globalSpent);

            // (3) step cap
            if (stepTotal > request.StepCap)
            {
                return Refuse(BudgetCapKind.StepLimit, request.StepCap, 0m, stepTotal);
            }

            // audio has no daily cap and never draws on the video ones; only the step cap above
            // (MaxSpendCredits) and the step's credit hold limit it.
            if (!creditsOnly)
            {
                VideoGenerationReservation? capped = await CheckDailyCapsAsync();
                if (capped is not null)
                {
                    return capped;
                }
            }

            async Task<VideoGenerationReservation?> CheckDailyCapsAsync()
            {
                // (4)/(5) daily caps, per unit (fail-closed on a non-positive cap). GPU caps are
                // configured in minutes and compared in seconds, the unit every row is written in.
                decimal? projectCapOrNone = gpu ? _options.DailyGpuMinutes * 60m : _options.DailyBudgetCredits;
                decimal globalCap = gpu ? _options.GlobalDailyGpuMinutes * 60m : _options.GlobalDailyBudgetCredits;

                if (projectCapOrNone is { } projectCap && (projectCap <= 0m || projectSpent + estimated > projectCap))
                {
                    return Refuse(BudgetCapKind.ProjectDaily, projectCap, projectSpent, estimated);
                }

                // (4a) the user's OWN daily limit (credits only: GPU-minutes stay the operator's). Read in this
                // transaction, under the advisory lock, from the user's setting; the spend is the owner's rows of the
                // day (their executions), so the check and the insert below cannot interleave with another reservation.
                if (!gpu && userId is Guid owner)
                {
                    decimal? userLimit = await db.UserVideoSpendLimits.AsNoTracking()
                        .Where(l => l.UserId == owner)
                        .Select(l => l.DailyLimitCredits)
                        .FirstOrDefaultAsync(ct);
                    if (userLimit is { } limit)
                    {
                        var userRows = await VideoBudgetAccounting
                            .TodayRows(
                                VideoBudgetAccounting.JobsOfUser(db.ExternalGenerationJobs, db.WorkflowExecutions, owner),
                                unitTag, today)
                            .ToListAsync(ct);
                        decimal userSpent = userRows.Sum(r => Charge(r.CostEstimate, r.ActualCost));
                        if (userSpent + estimated > limit)
                        {
                            return Refuse(BudgetCapKind.UserDaily, limit, userSpent, estimated);
                        }
                    }
                }

                // (4b) the plan decides WHO may generate (videoGenerationAllowed), never how much per day: no plan
                // caps video spend. A plan without video generation is reported distinctly so the UI can offer an
                // upgrade instead of "try tomorrow". USD only: a GPU-seconds reservation (self-hosted ComfyUI,
                // issue #93 phase 5) is capped by the GPU caps above and below instead.
                if (!gpu && _entitlements is not null && organizationId is Guid orgId
                    && !(await _entitlements.GetAsync(orgId, ct)).VideoGenAllowed)
                {
                    return Deny(
                        "PLAN_NOT_ENTITLED: the organization's plan does not include video generation",
                        estimated, projectSpent, globalSpent);
                }

                // (5) global daily cap (fail-closed on a non-positive cap)
                if (globalCap <= 0m || globalSpent + estimated > globalCap)
                {
                    return Refuse(BudgetCapKind.GlobalDaily, globalCap, globalSpent, estimated);
                }

                return null;
            }

            // (6) insert every new take, return all takes in request order.
            var takes = new List<ReservedTake>(requested.Count);
            foreach (var take in requested)
            {
                if (existingByHash.TryGetValue(take.RequestHash, out var existing))
                {
                    takes.Add(new ReservedTake(
                        existing.Id, take.ClipId, take.RequestHash, existing.CostEstimate, existing.Status, Existing: true));
                    continue;
                }

                var job = new ExternalGenerationJob
                {
                    Id = Guid.NewGuid(),
                    ExecutionId = request.ExecutionId,
                    StepResultId = request.StepResultId,
                    ProjectId = request.ProjectId,
                    OrganizationId = organizationId,
                    ClipId = take.ClipId,
                    RequestHash = take.RequestHash,
                    ProviderId = request.ProviderId,
                    RemoteJobId = null,
                    Status = ExternalGenerationJobStatus.Reserved,
                    CostEstimate = take.CostEstimate,
                    CostUnit = unitTag,
                    // A take may show its own subjects (a planned shot's); otherwise the request's, as always.
                    AvatarId = take.AvatarId ?? request.AvatarId,
                    SubjectIdsJson = ExternalGenerationJob.WriteSubjectIds(take.SubjectIds ?? request.SubjectIds),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.ExternalGenerationJobs.Add(job);
                takes.Add(new ReservedTake(
                    job.Id, take.ClipId, take.RequestHash, take.CostEstimate, job.Status, Existing: false));
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Status is a concurrency token: a row this reservation abandons was changed under it by another
                // attempt (a zombie recording its submit). Nothing is saved (the transaction rolls back) and the step
                // fails with a defined, platform-class code rather than UNEXPECTED_ERROR.
                var changed = string.Join(", ", ex.Entries.Select(e => e.Entity is ExternalGenerationJob j ? j.Id.ToString() : "?"));
                return Deny(
                    $"RESERVATION_ORPHANED: ledger row(s) {changed} of this step changed while this attempt reserved its takes; nothing was reserved.",
                    estimated, projectSpent, globalSpent);
            }

            if (tx is not null)
            {
                await tx.CommitAsync(ct);
            }

            return new VideoGenerationReservation(true, estimated, projectSpent, globalSpent, null, takes);
        }
        finally
        {
            ReservationLock.Release();
        }
    }

    /// <summary>The rows earlier step results of the requesting step left behind, and which of those owners were earlier attempts.</summary>
    private sealed record EarlierStepRows(List<ExternalGenerationJob> Rows, HashSet<Guid> EarlierAttemptOwners);

    /// <summary>
    /// Every row of this execution owned by a DIFFERENT step result of the same step order as the requesting one, still
    /// <c>Reserved</c> with no <c>RemoteJobId</c> or already <c>Abandoned</c>, whatever its hash; plus which of their owners
    /// are an EARLIER attempt: a result a Retry superseded, or one that failed on a run since resumed. A row of the
    /// requesting step result itself (the in-step retries of one attempt) is never included, so <c>RESERVATION_ORPHANED</c>
    /// still holds inside an attempt; nor is a row with a <c>RemoteJobId</c> (the vendor has it: it is polled). No
    /// manual/auto flag is needed: the recovery sweeper never auto-resumes a run whose ledger holds a Reserved/Submitted
    /// row, so only an attempt the user asked for reaches here with an orphan. Rows are tracked, so a caller may abandon them.
    /// </summary>
    private static async Task<EarlierStepRows> EarlierStepRowsAsync(
        WorkflowEngineDbContext db, VideoGenerationReservationRequest request, CancellationToken ct)
    {
        var current = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.Id == request.StepResultId && r.WorkflowExecutionId == request.ExecutionId)
            .Select(r => new { r.WorkflowStep.StepOrder })
            .FirstOrDefaultAsync(ct);
        if (current is null)
        {
            return new EarlierStepRows([], []);
        }

        int resumeCount = await db.WorkflowExecutions
            .AsNoTracking()
            .Where(e => e.Id == request.ExecutionId)
            .Select(e => e.ResumeCount)
            .FirstOrDefaultAsync(ct);

        var owners = await db.WorkflowStepResults
            .AsNoTracking()
            .Where(r => r.WorkflowExecutionId == request.ExecutionId
                && r.Id != request.StepResultId
                && r.WorkflowStep.StepOrder == current.StepOrder)
            .Select(r => new { r.Id, Earlier = r.SupersededAt != null || (r.Status == StepStatus.Failed && resumeCount > 0) })
            .ToListAsync(ct);
        var ownerIds = owners.Select(o => o.Id).ToList();
        if (ownerIds.Count == 0)
        {
            return new EarlierStepRows([], []);
        }

        var rows = await db.ExternalGenerationJobs
            .Where(j => j.ExecutionId == request.ExecutionId
                && ownerIds.Contains(j.StepResultId)
                && ((j.Status == ExternalGenerationJobStatus.Reserved && (j.RemoteJobId == null || j.RemoteJobId == ""))
                    || j.Status == ExternalGenerationJobStatus.Abandoned))
            .ToListAsync(ct);

        return new EarlierStepRows(rows, owners.Where(o => o.Earlier).Select(o => o.Id).ToHashSet());
    }

    private static decimal Charge(decimal costEstimate, decimal? actualCost) =>
        VideoBudgetAccounting.Charge(costEstimate, actualCost);

    private static VideoGenerationReservation DenyBudget(
        BudgetRefusal refusal, DateTime nowUtc, decimal estimated, decimal projectSpent, decimal globalSpent) =>
        new(false, estimated, projectSpent, globalSpent,
            $"{VideoBudgetMessages.Code}: {VideoBudgetMessages.Describe(refusal, nowUtc)}",
            Array.Empty<ReservedTake>(), refusal);

    private static VideoGenerationReservation Deny(
        string reason, decimal estimated, decimal projectSpent, decimal globalSpent) =>
        new(false, estimated, projectSpent, globalSpent, reason, Array.Empty<ReservedTake>());
}
