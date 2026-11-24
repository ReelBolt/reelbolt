namespace ReelBolt.Inference.Api.Services.Media;

/// <summary>
/// How often, and how much at a time, the API repairs video sources that have no proxy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a sweeper and not an outbox.</b> The upload path publishes
/// <c>MediaDerivativesRequested</c> best-effort — a broker outage must not fail an upload whose bytes
/// are already committed — and before this service nothing re-derived that work, so a lost publish
/// left a video playing its original forever. An outbox would persist the same fact twice: the
/// <c>project_files</c> and <c>workflow_step_results</c> rows are already the durable record that the
/// work exists, and "this source has no proxy" is a query over them plus <c>media_derivatives</c>. A
/// sweeper therefore needs no new table, cannot drift from the truth it describes, repairs the whole
/// back catalogue (not only requests made after the deploy), and its repeats are harmless because the
/// engine cache-hits by <c>cache_key</c>.
/// </para>
/// <para>
/// <b>The cost is latency, and it is bounded by <see cref="IntervalMinutes"/>.</b> A lost publish is
/// repaired on the next tick of the API replica that happens to sweep it; when several replicas run,
/// two of them can select the same source — the duplicate is a no-op downstream, and the alternative
/// (a cross-replica lease) buys nothing at this scale.
/// </para>
/// </remarks>
public sealed class MediaDerivativeBackfillOptions
{
    /// <summary>Configuration section, bound by <c>AddInferenceApiServices</c>.</summary>
    public const string SectionName = "MediaDerivatives:Backfill";

    /// <summary>Whether the periodic sweep runs at all. <c>false</c> leaves the admin endpoints working.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minutes between sweeps. <c>0</c> disables the timer, exactly like <see cref="Enabled"/>.</summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Sources one sweep may request. The sweep is ordered oldest-source-first, so a backlog larger
    /// than this drains across ticks instead of stampeding the engine's derivative slots.
    /// </summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Grace period before the first sweep, so migrations, seeding and the first requests settle
    /// before a deploy starts enqueueing encoders.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 60;
}
