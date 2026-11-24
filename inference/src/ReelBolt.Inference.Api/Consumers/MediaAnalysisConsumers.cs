using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>R09.1: the engine began a run; its queued kinds are now running.</summary>
public sealed class MediaAnalysisStartedConsumer(InferenceApiDbContext db) : IConsumer<MediaAnalysisStarted>
{
    public async Task Consume(ConsumeContext<MediaAnalysisStarted> context)
    {
        List<MediaAnalysisRequest> rows = await db.MediaAnalysisRequests
            .Where(r => r.RequestId == context.Message.RequestId && r.State == MediaAnalysisStates.Queued)
            .ToListAsync(context.CancellationToken);
        foreach (MediaAnalysisRequest row in rows)
        {
            row.State = MediaAnalysisStates.Running;
            row.UpdatedAt = DateTime.UtcNow;
        }

        if (rows.Count > 0) await db.SaveChangesAsync(context.CancellationToken);
    }
}

/// <summary>
/// R09.1: a run is over. Stores each new result in the digest cache (same bytes, same organization, any project), points the
/// request rows at it, and closes the transcript's credit hold: settled at what the recogniser metered for a result that was
/// actually produced, released for a cache hit or a failure (a failed transcript is never charged).
/// </summary>
public sealed class MediaAnalysisFinishedConsumer(
    InferenceApiDbContext db,
    ILogger<MediaAnalysisFinishedConsumer> logger,
    INonWorkflowChargeService? charges = null) : IConsumer<MediaAnalysisFinished>
{
    /// <summary>
    /// What a PRODUCED transcript settles at. Unpriced or unmetered usage, or a missing or zero metered amount, settles at
    /// the hold (the estimate), never at 0: a failed meter must not make paid work free.
    /// </summary>
    internal static long SettleAmount(MediaAnalysisKindOutcome outcome, long holdMillis)
    {
        long actual = outcome.ActualMillis ?? 0;
        return outcome.UnpricedUsage || actual <= 0 ? Math.Max(actual, holdMillis) : actual;
    }

    public async Task Consume(ConsumeContext<MediaAnalysisFinished> context)
    {
        try
        {
            await ProcessAsync(context.Message, null, context.CancellationToken);
        }
        catch (DbUpdateException ex) when (IsResultKeyViolation(ex))
        {
            // The unique (organization, digest, kind, language) index: a concurrent run stored the same result first.
            // That result is already cached, so treat this run as a cache hit (hold released, nothing settled).
            logger.LogInformation(ex, "Analysis result already cached by a concurrent run; releasing the hold");
            db.ChangeTracker.Clear();
            await ProcessAsync(context.Message, ex, context.CancellationToken);
        }
    }

    internal const string ResultKeyConstraint = "ux_media_analysis_results_key";

    /// <summary>True only for a real Postgres unique violation (23505) on the result-cache key; any other DB error must not look like a cache hit.</summary>
    internal static bool IsResultKeyViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg && pg.ConstraintName == ResultKeyConstraint;

    private async Task ProcessAsync(MediaAnalysisFinished message, DbUpdateException? collision, CancellationToken ct)
    {
        List<MediaAnalysisRequest> rows = await db.MediaAnalysisRequests
            .Where(r => r.RequestId == message.RequestId && r.ProjectId == message.ProjectId)
            .ToListAsync(ct);

        List<(ChargeHold Hold, long? Millis)> closes = [];
        foreach (MediaAnalysisRequest row in rows)
        {
            MediaAnalysisKindOutcome? outcome = message.Outcomes.FirstOrDefault(o => o.Kind == row.Kind);
            row.UpdatedAt = DateTime.UtcNow;
            row.Digest = message.Digest;
            bool stored = false;
            bool alreadyCached = false;
            if (outcome is { Ok: true, ResultJson: not null })
            {
                MediaAnalysisResult? result = null;
                if (message.Digest is { Length: > 0 } digest)
                {
                    result = await db.MediaAnalysisResults.FirstOrDefaultAsync(
                        r => r.OrganizationId == row.OrganizationId && r.Digest == digest && r.Kind == row.Kind && r.Language == row.Language, ct);
                    if (result is not null && collision is not null) alreadyCached = true;
                    if (result is null && collision is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(collision).Throw(); // nobody else's row: never settle at 0 on an unexplained error
                    // An events result that is not current (another detector version, or marked degraded) is replaced in
                    // place by the one this run computed, even when the new one is degraded too: the newest detection of
                    // this version is the one to show (the cache key has no version, so there is only ever one row).
                    if (result is not null && row.Kind == MediaAnalysisKinds.Events && !outcome.FromCache
                        && MediaAnalysisJson.EventsAreThisVersion(outcome.SummaryJson) && !MediaAnalysisJson.EventsAreCurrent(result.SummaryJson))
                    {
                        result.ResultJson = outcome.ResultJson;
                        result.SummaryJson = outcome.SummaryJson ?? "{}";
                        result.CreatedAt = DateTime.UtcNow;
                    }

                    if (result is null)
                    {
                        result = new MediaAnalysisResult
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = row.OrganizationId,
                            Digest = digest,
                            Kind = row.Kind,
                            Language = row.Language,
                            ResultJson = outcome.ResultJson,
                            SummaryJson = outcome.SummaryJson ?? "{}",
                            MeasuredSeconds = outcome.MeasuredSeconds,
                            CreatedAt = DateTime.UtcNow
                        };
                        db.MediaAnalysisResults.Add(result);
                    }
                }

                if (result is not null)
                {
                    row.State = MediaAnalysisStates.Ready;
                    row.ResultId = result.Id;
                    row.Reason = null;
                    stored = true;
                }
            }

            if (!stored)
            {
                row.State = MediaAnalysisStates.Failed;
                row.Reason = outcome?.Reason ?? "The analysis didn't finish. Try again in a moment.";
            }

            if (row.HoldReservationId is { } reservation && !row.HoldSettled)
            {
                row.HoldSettled = true;
                ChargeHold hold = new(true, reservation, row.HoldMillis, 0, null)
                {
                    OrganizationId = row.OrganizationId,
                    Source = UsageSource.Editor,
                    RequestId = row.Id
                };
                // Settle only a result this run produced; a cache hit or a failure releases the hold.
                closes.Add((hold, stored && !alreadyCached && outcome is { FromCache: false } ? SettleAmount(outcome, row.HoldMillis) : null));
            }
        }

        await db.SaveChangesAsync(ct);
        foreach ((ChargeHold hold, long? millis) in closes)
            await NonWorkflowChargeResults.CloseHoldAsync(charges, hold, millis, logger);
    }
}
