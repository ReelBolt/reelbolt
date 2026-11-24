using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// How much video-generation budget a project has left today: a read-only view over
/// <c>external_generation_jobs</c> that applies the SAME accounting as the engine's
/// <c>VideoGenerationBudgetGate</c> (a row is charged <c>max(estimate, actual)</c> floored at 0,
/// Cancelled and Failed rows are released, only rows of the same unit count, the day is the UTC day),
/// against the same caps (<c>VideoGeneration:*</c> configuration and the person's own limit; no plan caps video spend).
/// </summary>
/// <remarks>
/// <para>The accounting itself (what a row costs, which rows count, the default caps, the tenant-safe view) is
/// <see cref="VideoBudgetAccounting"/> in ReelBolt.Shared, the one implementation the gate also uses. This only
/// reads the ledger, as SQL sums (never every tenant's rows), and the configured caps.</para>
/// <para><b>Which configuration.</b> The engine is the only process that decides; this snapshot only informs. It
/// reads the engine's own section and keys (<c>VideoGeneration:DailyBudgetCredits</c>,
/// <c>:GlobalDailyBudgetCredits</c>, <c>:DailyGpuMinutes</c>, <c>:GlobalDailyGpuMinutes</c>, bound exactly as the
/// engine's <c>VideoGenerationOptions</c> binds them, pinned by a test) with the same shipped defaults, so an
/// unconfigured deployment cannot disagree. The API cannot read the engine's process configuration, so an operator who
/// overrides a cap sets the same <c>VideoGeneration__*</c> variable on both services. Should the two ever
/// differ, the snapshot can only be wrong in what it SAYS: the gate still refuses by its own figure, with its own
/// sentence. The platform cap reaches a tenant only as "reached" (S-01).</para>
/// </remarks>
public static class VideoBudgetSnapshot
{
    private const string Section = "VideoGeneration";

    /// <summary>
    /// The budget picture for a run's project. The binding cap is the tenant's own tightest (project or
    /// the person's own limit); a reached platform cap is reported only as <c>limitedBy: "platform"</c>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, object?>> ForRunAsync(
        InferenceApiDbContext db,
        Microsoft.Extensions.Configuration.IConfiguration? configuration,
        Project project,
        Guid executionId,
        Guid? callerUserId,
        CancellationToken ct)
    {
        // A ComfyUI run is budgeted in GPU-seconds, everything else in credits (the gate's two ledgers).
        bool gpu = await db.ExternalGenerationJobs.AsNoTracking()
            .AnyAsync(j => j.ExecutionId == executionId && j.CostUnit == ExternalGenerationJob.GpuSecondsUnit, ct);
        DateTime today = DateTime.UtcNow.Date;
        IQueryable<ExternalGenerationJob> todays = TodayJobs(db, gpu, today);

        decimal globalUsed = await todays.SumAsync(VideoBudgetAccounting.ChargeOf, ct);
        decimal projectUsed = await todays.Where(j => j.ProjectId == project.Id).SumAsync(VideoBudgetAccounting.ChargeOf, ct);

        // The person's own daily limit and alert level (credits only), and what their runs spent today.
        decimal? userCap = null;
        decimal? alert = null;
        decimal userUsed = 0m;
        Guid? userId = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId).Select(e => e.InitiatedByUserId).FirstOrDefaultAsync(ct);
        // A personal limit and its use are shown only to the person who set it: another member viewing this run
        // sees no user figures (and the user cap is not offered as the binding cap).
        if (!gpu && userId is Guid owner && callerUserId == owner)
        {
            var setting = await db.UserVideoSpendLimits.AsNoTracking()
                .Where(l => l.UserId == owner).Select(l => new { l.DailyLimitCredits, l.AlertThresholdCredits }).FirstOrDefaultAsync(ct);
            userCap = setting?.DailyLimitCredits;
            alert = setting?.AlertThresholdCredits;
            userUsed = await VideoBudgetAccounting.JobsOfUser(todays, db.WorkflowExecutions.AsNoTracking(), owner)
                .SumAsync(VideoBudgetAccounting.ChargeOf, ct);
        }

        return View(configuration, gpu, projectUsed, globalUsed, today, userCap, userUsed, alert);
    }

    /// <summary>
    /// The project and platform caps in the ledger's unit (credits, or GPU-seconds when <paramref name="gpu"/>), read
    /// from the engine's <c>VideoGeneration</c> keys with the engine's defaults.
    /// </summary>
    public static (decimal? ProjectCap, decimal GlobalCap) ReadCaps(Microsoft.Extensions.Configuration.IConfiguration? configuration, bool gpu) =>
        gpu
            ? (Read(configuration, "DailyGpuMinutes", VideoBudgetAccounting.DefaultProjectDailyGpuMinutes) * 60m,
               Read(configuration, "GlobalDailyGpuMinutes", VideoBudgetAccounting.DefaultGlobalDailyGpuMinutes) * 60m)
            : (Read(configuration, "DailyBudgetCredits"),
               Read(configuration, "GlobalDailyBudgetCredits", VideoBudgetAccounting.DefaultGlobalDailyCredits));

    private static IQueryable<ExternalGenerationJob> TodayJobs(InferenceApiDbContext db, bool gpu, DateTime today) =>
        VideoBudgetAccounting.TodayJobs(
            db.ExternalGenerationJobs.AsNoTracking(),
            gpu ? ExternalGenerationJob.GpuSecondsUnit : ExternalGenerationJob.CreditsUnit,
            today);

    /// <summary>The global sum is reduced to "reached" here and goes no further (S-01).</summary>
    private static IReadOnlyDictionary<string, object?> View(
        Microsoft.Extensions.Configuration.IConfiguration? configuration, bool gpu,
        decimal projectUsed, decimal globalUsed, DateTime today,
        decimal? userCap, decimal userUsed, decimal? alert)
    {
        (decimal? projectCap, decimal globalCap) = ReadCaps(configuration, gpu);
        bool platformReached = VideoBudgetAccounting.PlatformReached(globalCap, globalUsed);
        return VideoBudgetAccounting.TenantView(gpu, projectCap, projectUsed, platformReached, today, userCap, userUsed, alert);
    }

    /// <summary>
    /// The person's own limit for the estimate and settings surfaces: what they set, what their runs spent today and how
    /// much is left. All null when they set nothing. Credits only.
    /// </summary>
    public static async Task<UserVideoLimitView> ForUserAsync(InferenceApiDbContext db, Guid userId, CancellationToken ct)
    {
        var setting = await db.UserVideoSpendLimits.AsNoTracking()
            .Where(l => l.UserId == userId).Select(l => new { l.DailyLimitCredits, l.AlertThresholdCredits }).FirstOrDefaultAsync(ct);
        DateTime today = DateTime.UtcNow.Date;
        decimal used = await VideoBudgetAccounting
            .JobsOfUser(TodayJobs(db, gpu: false, today), db.WorkflowExecutions.AsNoTracking(), userId)
            .SumAsync(VideoBudgetAccounting.ChargeOf, ct);
        decimal? limit = setting?.DailyLimitCredits;
        decimal? alert = setting?.AlertThresholdCredits;
        return new UserVideoLimitView(
            limit, alert, used, limit is { } l ? Math.Max(0m, l - used) : null,
            alert is { } a && used >= a, limit is { } l2 && used >= l2, VideoBudgetAccounting.NextReset(today));
    }

    private static decimal? Read(Microsoft.Extensions.Configuration.IConfiguration? configuration, string key) =>
        configuration is null
            ? null
            : Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<decimal?>(configuration, $"{Section}:{key}");

    private static decimal Read(Microsoft.Extensions.Configuration.IConfiguration? configuration, string key, decimal fallback) =>
        configuration is null
            ? fallback
            : Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<decimal?>(configuration, $"{Section}:{key}") ?? fallback;
}

/// <summary>A person's own daily video limit as the app shows it (<c>GET/PUT /api/v1/video-spend-limit</c>, the estimate).</summary>
/// <param name="DailyLimitCredits">Their limit; null = no daily limit beyond the credit balance and each step's limit.</param>
/// <param name="AlertThresholdCredits">Their alert level; null = none.</param>
/// <param name="UsedTodayCredits">What their runs have spent on video today (UTC).</param>
/// <param name="RemainingTodayCredits">The room left under the limit; null when there is no limit.</param>
/// <param name="AlertReached">Whether today's use has reached the alert level.</param>
/// <param name="LimitReached">Whether today's use has reached the limit.</param>
/// <param name="ResetsAt">The next 00:00 UTC.</param>
public sealed record UserVideoLimitView(
    decimal? DailyLimitCredits,
    decimal? AlertThresholdCredits,
    decimal UsedTodayCredits,
    decimal? RemainingTodayCredits,
    bool AlertReached,
    bool LimitReached,
    DateTime ResetsAt);
