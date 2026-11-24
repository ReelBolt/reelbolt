using System.Linq.Expressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The ONE video-generation budget accounting. The engine's <c>VideoGenerationBudgetGate</c> (which decides)
/// and the API's <c>VideoBudgetSnapshot</c> (which tells a tenant what is left) both use it, so they cannot
/// disagree: what a ledger row costs, which rows count today, the shipped default caps, and the tenant-safe
/// view of the binding cap.
/// </summary>
public static class VideoBudgetAccounting
{
    /// <summary>
    /// The operator's runaway-spend safety ceiling across ALL tenants per UTC day, in credits (the credit equivalent of
    /// $5000, a hundred times the old $50 figure). It is not a product limit: a person's daily limit is their own setting
    /// (<c>user_video_spend_limits</c>, docs/design/video-spend-limit.md), and no per-project figure is shipped.
    /// </summary>
    public static readonly decimal DefaultGlobalDailyCredits = CreditCurrency.CreditsFromUsd(5000m);
    public const decimal DefaultProjectDailyGpuMinutes = 120m;
    public const decimal DefaultGlobalDailyGpuMinutes = 240m;

    /// <summary>
    /// The jobs started by one user: those whose execution was initiated by <paramref name="userId"/>. The ledger rows carry no
    /// user of their own, so the person is read from the execution (<c>workflow_executions.initiated_by_user_id</c>).
    /// </summary>
    public static IQueryable<ExternalGenerationJob> JobsOfUser(
        IQueryable<ExternalGenerationJob> jobs, IQueryable<WorkflowExecution> executions, Guid userId) =>
        jobs.Where(j => executions.Any(e => e.Id == j.ExecutionId && e.InitiatedByUserId == userId));

    /// <summary>Clip-id prefix of an <c>AudioGenerate</c> music take's ledger row (<c>music-{k}</c>).</summary>
    public const string MusicClipPrefix = "music-";

    /// <summary>Clip-id prefix of an <c>AudioGenerate</c> sound-effect take's ledger row (<c>sfx-{k}</c>).</summary>
    public const string SoundEffectClipPrefix = "sfx-";

    /// <summary>
    /// The ledger clip id of audio take <paramref name="take"/>. AudioGenerate shares
    /// <c>external_generation_jobs</c> with VideoGenerate (whose clip ids are <c>v{n}</c> / <c>v{n}t{k}</c>), and this
    /// prefix is how the video daily caps leave audio rows out without a schema change.
    /// </summary>
    public static string AudioClipId(AudioGenerationKind kind, int take) =>
        $"{(kind == AudioGenerationKind.Music ? MusicClipPrefix : SoundEffectClipPrefix)}{take}";

    /// <summary>Clip-id prefix of an <c>ImageGenerate</c> image's ledger row (<c>img-{k}</c>).</summary>
    public const string ImageClipPrefix = "img-";

    /// <summary>The ledger clip id of ImageGenerate image <paramref name="k"/> (same trick as <see cref="AudioClipId"/>).</summary>
    public static string ImageClipId(int k) => $"{ImageClipPrefix}{k}";

    /// <summary>Whether a ledger row's clip id is an ImageGenerate image (see <see cref="ImageClipId"/>).</summary>
    public static bool IsImageClipId(string clipId) => clipId.StartsWith(ImageClipPrefix, StringComparison.Ordinal);

    /// <summary>Whether a ledger row's clip id is an AudioGenerate take (see <see cref="AudioClipId"/>).</summary>
    public static bool IsAudioClipId(string clipId) =>
        clipId.StartsWith(MusicClipPrefix, StringComparison.Ordinal) || clipId.StartsWith(SoundEffectClipPrefix, StringComparison.Ordinal);

    /// <summary>Clip-id prefix of a <c>VideoEnhance</c> hosted job's ledger row (<c>enhance-{op}</c>).</summary>
    public const string EnhanceClipPrefix = "enhance-";

    /// <summary>The ledger clip id of a hosted VideoEnhance job (<c>enhance-upscale</c>, <c>enhance-interpolate</c>).</summary>
    public static string EnhanceClipId(string op) => $"{EnhanceClipPrefix}{op.ToLowerInvariant()}";

    /// <summary>Whether a ledger row's clip id is a hosted VideoEnhance job (see <see cref="EnhanceClipId"/>).</summary>
    public static bool IsEnhanceClipId(string clipId) => clipId.StartsWith(EnhanceClipPrefix, StringComparison.Ordinal);

    /// <summary>The columns of a ledger row the accounting reads.</summary>
    public sealed record LedgerRow(Guid ProjectId, Guid? OrganizationId, decimal CostEstimate, decimal? ActualCost);

    /// <summary>A row is charged <c>max(estimate, actual)</c>, floored at 0, so no row can ever credit the ledger.</summary>
    public static decimal Charge(decimal costEstimate, decimal? actualCost) =>
        Math.Max(0m, Math.Max(costEstimate, actualCost ?? 0m));

    /// <summary>
    /// <see cref="Charge"/> as an expression a database can sum (S-01 / QA-P0-17: the snapshot aggregates in SQL
    /// instead of loading every tenant's rows). Conditionals only, so every provider translates it.
    /// </summary>
    public static readonly Expression<Func<ExternalGenerationJob, decimal>> ChargeOf = j =>
        Math.Round(
            (j.ActualCost ?? 0m) > j.CostEstimate
                ? ((j.ActualCost ?? 0m) > 0m ? (j.ActualCost ?? 0m) : 0m)
                : (j.CostEstimate > 0m ? j.CostEstimate : 0m),
            ChargeScale);

    /// <summary>
    /// Decimals each row's charge is rounded to inside <see cref="ChargeOf"/>. The ledger columns are unconstrained
    /// <c>numeric</c> and hold values like 53.333333333333333333333333333 (decimal division); Postgres sums those
    /// to more digits than a .NET decimal reads (OverflowException on the estimate endpoint). Six decimals keeps any
    /// realistic sum far inside 28 digits and moves a cap decision by at most 1e-6 per row.
    /// </summary>
    public const int ChargeScale = 6;

    /// <summary>
    /// The jobs that count against today's (UTC day) video caps, in ONE unit: Cancelled and Failed rows are
    /// released, a credits row never counts toward a GPU cap or vice versa, and an AudioGenerate row
    /// (<see cref="IsAudioClipId"/>), an ImageGenerate row (<see cref="IsImageClipId"/>) and a VideoEnhance row
    /// (<see cref="IsEnhanceClipId"/>) never count at all (Product decision: audio and image have no daily cap; only the enhance step's credits limit it).
    /// </summary>
    public static IQueryable<ExternalGenerationJob> TodayJobs(IQueryable<ExternalGenerationJob> jobs, string unitTag, DateTime todayUtc) =>
        jobs.Where(j => j.CreatedAt >= todayUtc
                && j.Status != ExternalGenerationJobStatus.Cancelled
                && j.Status != ExternalGenerationJobStatus.Failed
                && j.CostUnit == unitTag
                && !j.ClipId.StartsWith(MusicClipPrefix)
                && !j.ClipId.StartsWith(SoundEffectClipPrefix)
                && !j.ClipId.StartsWith(EnhanceClipPrefix)
                && !j.ClipId.StartsWith(ImageClipPrefix));

    /// <summary><see cref="TodayJobs"/>, projected to the columns the accounting reads.</summary>
    public static IQueryable<LedgerRow> TodayRows(IQueryable<ExternalGenerationJob> jobs, string unitTag, DateTime todayUtc) =>
        TodayJobs(jobs, unitTag, todayUtc)
            .Select(j => new LedgerRow(j.ProjectId, j.OrganizationId, j.CostEstimate, j.ActualCost));

    /// <summary>
    /// Whether the platform-wide cap has no room left (a cap of 0 or less has none). This boolean is the ONLY
    /// thing a tenant may learn of the platform cap, and only once it is reached: the gate then refuses every
    /// reservation anyway and says so (<c>VideoBudgetMessages</c>).
    /// </summary>
    public static bool PlatformReached(decimal globalCap, decimal globalUsed) => globalCap <= 0m || globalUsed >= globalCap;

    /// <summary>The next 00:00 UTC after <paramref name="todayUtc"/> (the daily caps' reset).</summary>
    public static DateTime NextReset(DateTime todayUtc) => DateTime.SpecifyKind(todayUtc.Date.AddDays(1), DateTimeKind.Utc);

    /// <summary>
    /// What a tenant may see of its video budget. Only the tenant's OWN caps compete for "binding": the project's
    /// and the person's own limit (no plan caps video spend). The platform cap never enters that choice and never yields a
    /// figure (S-01): while it has room the view is the tenant's own; once it is reached
    /// (<paramref name="platformReached"/>, see <see cref="PlatformReached"/>) the view is only
    /// <c>{unit, limitedBy: "platform", resetsAt}</c>, so no cap, used or remaining figure is derived from other
    /// tenants' spend. Caps and usage are in the ledger's unit (credits, or GPU-seconds for <paramref name="gpu"/>).
    /// </summary>
    public static IReadOnlyDictionary<string, object?> TenantView(
        bool gpu, decimal? projectCap, decimal projectUsed, bool platformReached, DateTime todayUtc,
        decimal? userCap = null, decimal userUsed = 0m, decimal? alertCredits = null)
    {
        DateTime resetsAt = NextReset(todayUtc);
        string unit = gpu ? "gpuMinutes" : "credits";
        if (platformReached)
        {
            return new Dictionary<string, object?>
            {
                ["unit"] = unit,
                ["limitedBy"] = "platform",
                ["resetsAt"] = resetsAt,
            };
        }

        var candidates = new List<(string LimitedBy, decimal Cap, decimal Used)>();
        if (projectCap is { } project)
        {
            candidates.Add(("project", project, projectUsed));
        }

        // The person's own daily limit (credits only): the user sets it, so it is the first thing the view names.
        if (!gpu && userCap is { } user)
        {
            candidates.Add(("user", Math.Max(0m, user), userUsed));
        }

        // No cap of the tenant's own: nothing but the credit balance and the step's own limit bounds the day.
        if (candidates.Count == 0)
        {
            return new Dictionary<string, object?>
            {
                ["unit"] = unit,
                ["dailyCapCredits"] = null,
                ["usedTodayCredits"] = userUsed,
                ["remainingTodayCredits"] = null,
                ["resetsAt"] = resetsAt,
                ["limitedBy"] = null,
                ["alertThresholdCredits"] = alertCredits,
                ["alertReached"] = alertCredits is { } a0 && userUsed >= a0,
            };
        }

        var binding = candidates.OrderBy(c => Math.Max(0m, c.Cap - c.Used)).First();
        decimal remaining = Math.Max(0m, binding.Cap - binding.Used);

        if (gpu)
        {
            return new Dictionary<string, object?>
            {
                ["unit"] = unit,
                ["dailyCapGpuMinutes"] = binding.Cap / 60m,
                ["usedTodayGpuMinutes"] = binding.Used / 60m,
                ["remainingTodayGpuMinutes"] = remaining / 60m,
                ["resetsAt"] = resetsAt,
                ["limitedBy"] = binding.LimitedBy,
            };
        }

        return new Dictionary<string, object?>
        {
            ["unit"] = unit,
            ["dailyCapCredits"] = binding.Cap,
            ["usedTodayCredits"] = binding.Used,
            ["remainingTodayCredits"] = remaining,
            ["resetsAt"] = resetsAt,
            ["limitedBy"] = binding.LimitedBy,
            ["userDailyLimitCredits"] = userCap,
            ["userUsedTodayCredits"] = userUsed,
            ["alertThresholdCredits"] = alertCredits,
            ["alertReached"] = alertCredits is { } a && userUsed >= a,
        };
    }
}
