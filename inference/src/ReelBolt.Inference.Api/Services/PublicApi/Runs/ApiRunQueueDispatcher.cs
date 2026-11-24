using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.PublicApi.Runs;

/// <summary>
/// Starts the run requests the hourly run limit held back. Every
/// <c>PublicApi:RunQueue:PollSeconds</c> it reconciles stale claims, claims a small batch of due rows (Postgres
/// <c>FOR UPDATE SKIP LOCKED</c>, oldest first, one row per key so a key's requests start in FIFO order) and, for
/// each, runs the WHOLE start sequence again (<see cref="PublicRunStartService"/>, the method the synchronous start
/// uses) in a fresh DI scope acting as the key's service user, with its authority re-read from the database.
/// </summary>
/// <remarks>
/// Crash safety: a row is claimed (<c>starting</c>) before the start, the execution carries
/// <see cref="ApiRunRequest.CorrelationFor"/> as its correlation id, and <c>started</c> + <c>execution_id</c> are written
/// inside the run-limit lock right after the execution is queued. A claim older than
/// <c>StaleStartingMinutes</c> is reconciled by looking for the execution with that correlation id: found, the row is
/// <c>started</c>; absent, the row goes back to <c>queued</c>. So a crash can never start a request twice.
/// </remarks>
public sealed class ApiRunQueueDispatcher(
    IServiceScopeFactory scopes,
    IOptions<PublicApiOptions> options,
    ILogger<ApiRunQueueDispatcher> logger,
    TimeProvider? time = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (options.Value.Enabled) await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Run queue dispatch failed: {Error}", ex.GetType().Name);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.RunQueue.PollSeconds)), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One dispatch pass (also the test seam). Returns how many requests it started.</summary>
    internal async Task<int> TickAsync(CancellationToken ct)
    {
        List<(Guid Id, DateTime Token)> claimed;
        using (IServiceScope scope = scopes.CreateScope())
        {
            using IDisposable system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            await ReconcileStaleAsync(scope.ServiceProvider, db, ct);
            claimed = await ClaimAsync(db, ct);
        }

        int started = 0;
        foreach ((Guid id, DateTime token) in claimed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // S-M2: refresh the fencing token before each row, so a slow batch does not look stale while it runs.
                DateTime? refreshing = await RefreshClaimAsync(id, token, ct);
                if (refreshing is not { } fencing) continue; // lost the claim: someone else owns it now
                if (await ProcessAsync(id, fencing, ct)) started++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row stays "starting": the reconcile either finds its execution or puts it back in the queue.
                logger.LogWarning("Run request {RequestId} could not be processed: {Error}", id, ex.GetType().Name);
            }
        }

        return started;
    }

    /// <summary>Claims due rows: the oldest of each key, none while an older request of that key is still waiting or in flight.</summary>
    private async Task<List<(Guid Id, DateTime Token)>> ClaimAsync(InferenceApiDbContext db, CancellationToken ct)
    {
        DateTime now = ClaimToken();
        int batch = Math.Max(1, options.Value.RunQueue.BatchSize);

        if (db.Database.IsNpgsql())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            List<ApiRunRequest> locked = await db.ApiRunRequests.FromSqlInterpolated($"""
                SELECT * FROM api_run_requests r
                WHERE r.status = 'queued' AND r.not_before <= {now}
                  AND NOT EXISTS (SELECT 1 FROM api_run_requests o
                                  WHERE o.api_key_id = r.api_key_id AND o.id <> r.id
                                    AND o.status IN ('queued', 'starting') AND o.created_at < r.created_at)
                ORDER BY r.created_at
                LIMIT {batch}
                FOR UPDATE SKIP LOCKED
                """).ToListAsync(ct);
            foreach (ApiRunRequest row in locked) MarkClaimed(row, now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return locked.Select(r => (r.Id, now)).ToList();
        }

        // Other providers (tests): same rule, no row lock (one dispatcher).
        List<ApiRunRequest> waiting = await db.ApiRunRequests
            .Where(r => r.Status == ApiRunRequest.Queued || r.Status == ApiRunRequest.Starting)
            .ToListAsync(ct);
        List<ApiRunRequest> due = waiting
            .Where(r => r.Status == ApiRunRequest.Queued && r.NotBefore <= now
                && !waiting.Any(o => o.ApiKeyId == r.ApiKeyId && o.Id != r.Id && o.CreatedAt < r.CreatedAt))
            .OrderBy(r => r.CreatedAt)
            .Take(batch)
            .ToList();
        foreach (ApiRunRequest row in due) MarkClaimed(row, now);
        await db.SaveChangesAsync(ct);
        return due.Select(r => (r.Id, now)).ToList();
    }

    /// <summary>
    /// The claim's fencing token. Truncated to whole microseconds, the precision a Postgres <c>timestamptz</c>
    /// round-trips: a token that changed on read would never compare equal to the stored value.
    /// </summary>
    private DateTime ClaimToken()
    {
        DateTime now = _time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Ticks - (now.Ticks % 10), DateTimeKind.Utc);
    }

    private static void MarkClaimed(ApiRunRequest row, DateTime now)
    {
        row.Status = ApiRunRequest.Starting;
        row.StartingAt = now;
        row.UpdatedAt = now;
    }

    /// <summary>
    /// S-M2: extends this dispatcher's claim before it starts the row. Returns the new token, or null when the row is
    /// no longer <c>starting</c> on <paramref name="token"/> (another dispatcher reconciled or claimed it).
    /// </summary>
    private async Task<DateTime?> RefreshClaimAsync(Guid requestId, DateTime token, CancellationToken ct)
    {
        DateTime now = ClaimToken();
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        if (db.Database.IsRelational())
        {
            int updated = await db.ApiRunRequests
                .Where(r => r.Id == requestId && r.Status == ApiRunRequest.Starting && r.StartingAt == token)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.StartingAt, now)
                    .SetProperty(r => r.UpdatedAt, now), ct);
            return updated == 0 ? null : now;
        }

        ApiRunRequest? row = await db.ApiRunRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null || row.Status != ApiRunRequest.Starting || row.StartingAt != token) return null;
        row.StartingAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return now;
    }

    /// <summary>A claim whose dispatcher died: <c>started</c> when its execution exists, else back in the queue.</summary>
    private async Task ReconcileStaleAsync(IServiceProvider sp, InferenceApiDbContext db, CancellationToken ct)
    {
        DateTime now = ClaimToken();
        DateTime cutoff = now.AddMinutes(-Math.Max(1, options.Value.RunQueue.StaleStartingMinutes));
        List<ApiRunRequest> stale = (await db.ApiRunRequests
                .Where(r => r.Status == ApiRunRequest.Starting)
                .ToListAsync(ct))
            .Where(r => (r.StartingAt ?? r.UpdatedAt) < cutoff)
            .ToList();

        foreach (ApiRunRequest row in stale)
        {
            string correlation = ApiRunRequest.CorrelationFor(row.Id);
            Guid? executionId = await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.ProjectId == row.ProjectId && e.CorrelationId == correlation)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);
            row.UpdatedAt = now;
            row.StartingAt = null;
            if (executionId is { } found)
            {
                row.Status = ApiRunRequest.Started;
                row.ExecutionId = found;
                row.EstimatedStartAt = null;
            }
            else
            {
                row.Status = ApiRunRequest.Queued;
                row.NotBefore = now;
            }

            await db.SaveChangesAsync(ct);
            if (executionId is { } done)
                await NotifyStartedAsync(sp, row, done, ct);
        }
    }

    internal async Task<bool> ProcessAsync(Guid requestId, DateTime fencingToken, CancellationToken ct)
    {
        ApiRunRequest request;
        ApiKey? key;
        (Guid UserId, string Email, bool EmailVerified)? serviceUser = null;
        bool membershipOk = false;
        using (IServiceScope lookup = scopes.CreateScope())
        {
            using IDisposable system = lookup.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
            InferenceApiDbContext db = lookup.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            request = await db.ApiRunRequests.AsNoTracking().FirstAsync(r => r.Id == requestId, ct);
            // S-M2: the fencing token. A row another dispatcher claimed (or reconciled) since our claim is not ours.
            if (request.Status != ApiRunRequest.Starting || request.StartingAt != fencingToken)
                return false;
            key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == request.ApiKeyId, ct);
            if (key is not null)
            {
                MembershipValidator.Membership membership = await lookup.ServiceProvider.GetRequiredService<MembershipValidator>()
                    .LookupAsync(db, key.ServiceUserId, key.OrganizationId, ct);
                var user = await db.ApplicationUsers.AsNoTracking()
                    .Where(u => u.Id == key.ServiceUserId)
                    .Select(u => new { u.Email, u.EmailVerifiedAt, u.IsServiceAccount })
                    .FirstOrDefaultAsync(ct);
                // M7/M8: a soft-deleted org (deleted_at set by the Go API) must stop its keys at the next
                // start, before AccountPurgeService removes the workspace and its service accounts.
                bool organizationActive = await db.Organizations.AsNoTracking()
                    .AnyAsync(o => o.Id == key.OrganizationId && o.DeletedAt == null, ct);
                membershipOk = organizationActive && user is { IsServiceAccount: true }
                    && membership.UserExists && membership.Role is OrganizationRole.Member;
                if (user is not null) serviceUser = (key.ServiceUserId, user.Email, user.EmailVerifiedAt is not null);
            }
        }

        DateTime now = _time.GetUtcNow().UtcDateTime;
        // The authority is re-read now, never trusted from when the request was queued.
        if (!options.Value.Enabled)
            return await RefuseAsync(request, ApiKeyAuthentication.Disabled,
                ApiKeyAuthentication.MessageFor(ApiKeyAuthentication.Disabled), ct);

        // Q-5: the same codes and words the synchronous path's authentication gives, so a refusal at start time
        // says exactly what a request with the same key would have said.
        string? keyFailure = key is null
            ? ApiKeyAuthentication.Inactive
            : key.RevokedAt is not null
                ? ApiKeyAuthentication.Revoked
                : key.ExpiresAt is { } expires && expires <= now
                    ? ApiKeyAuthentication.Expired
                    : !membershipOk || serviceUser is null ? ApiKeyAuthentication.Inactive : null;
        if (keyFailure is not null)
            return await RefuseAsync(request, keyFailure, ApiKeyAuthentication.MessageFor(keyFailure), ct);

        using IServiceScope worker = scopes.CreateScope();
        IServiceProvider sp = worker.ServiceProvider;

        // L21: a queued run of a read-only plan (no API access) is refused at start, with the
        // synchronous path's own code (PublicApiGuardFilter does the same check per request).
        if (sp.GetService<ReelBolt.Shared.Tenancy.IOrganizationEntitlements>() is { } entitlements
            && ReelBolt.Shared.Billing.PlanGate.CheckApiWrite(await entitlements.GetAsync(key!.OrganizationId, ct)) is { } planRefusal)
            return await RefuseAsync(request, planRefusal.Code, planRefusal.Message, ct, sp);

        ClaimsPrincipal principal = new(new ClaimsIdentity(
        [
            new Claim("sub", key!.ServiceUserId.ToString()),
            new Claim("email", serviceUser!.Value.Email),
            new Claim(AuthClaims.Organization, key.OrganizationId.ToString()),
            new Claim(AuthClaims.OrgRole, OrganizationRole.Member.ToString()),
            new Claim(AuthClaims.EmailVerified, serviceUser.Value.EmailVerified ? "true" : "false"),
            new Claim(AuthClaims.PlatformAdmin, "false"),
            new Claim(AuthClaims.IsAdminAlias, "false"),
            new Claim(AuthClaims.Scope, string.Join(' ', key.Scopes)),
            new Claim(AuthClaims.ApiKeyId, key.Id.ToString())
        ], ApiKeyAuthentication.Scheme));
        DefaultHttpContext http = new() { User = principal, RequestServices = sp, RequestAborted = ct };
        http.Items[HttpApiKeyContext.ItemKey] = new ApiKeyRequestInfo(
            key.Id, key.OrganizationId, key.ProjectIds?.ToArray(), key.Scopes.ToArray());
        sp.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        PublicStartRunRequest? body = JsonSerializer.Deserialize<PublicStartRunRequest>(request.RequestJson, PublicApiEnvelope.Json);
        InferenceApiDbContext workerDb = sp.GetRequiredService<InferenceApiDbContext>();
        PublicRunStartService start = sp.GetRequiredService<PublicRunStartService>();

        // S-M2: the claim and the start end together. This dispatcher only marks the request started if it still holds
        // the fencing token; 0 rows affected means another dispatcher owns it, so this one does nothing.
        bool claimWon = false;
        RunStartOutcome outcome = await start.StartAsync(
            request.ProjectId, request.WorkflowId, body, key.Id, ApiRunRequest.CorrelationFor(request.Id),
            async (executionId, token) =>
            {
                int updated;
                if (workerDb.Database.IsRelational())
                {
                    updated = await workerDb.ApiRunRequests
                        .Where(r => r.Id == request.Id && r.Status == ApiRunRequest.Starting && r.StartingAt == fencingToken)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(r => r.Status, ApiRunRequest.Started)
                            .SetProperty(r => r.ExecutionId, executionId)
                            .SetProperty(r => r.EstimatedStartAt, (DateTime?)null)
                            .SetProperty(r => r.UpdatedAt, _time.GetUtcNow().UtcDateTime), CancellationToken.None);
                }
                else
                {
                    ApiRunRequest row = await workerDb.ApiRunRequests.FirstAsync(r => r.Id == request.Id, CancellationToken.None);
                    updated = row.Status == ApiRunRequest.Starting && row.StartingAt == fencingToken ? 1 : 0;
                    if (updated > 0)
                    {
                        row.Status = ApiRunRequest.Started;
                        row.ExecutionId = executionId;
                        row.EstimatedStartAt = null;
                        row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
                        await workerDb.SaveChangesAsync(CancellationToken.None);
                    }
                }

                claimWon = updated > 0;
            }, CancellationToken.None);

        if (outcome.Started is { } startedRun)
        {
            // Only the dispatcher whose conditional update won announces; a loser stays silent.
            if (claimWon) await NotifyStartedAsync(sp, request, startedRun.Id, ct);
            return claimWon;
        }

        if (outcome.RateFull is { } full)
        {
            DateTime next = _time.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(1, full.RetryAfterSeconds));
            await UpdateAsync(request.Id, r =>
            {
                r.Status = ApiRunRequest.Queued;
                r.StartingAt = null;
                r.NotBefore = next;
                r.EstimatedStartAt = next;
            }, ct);
            return false;
        }

        if (outcome.LockBusy is { } contended)
        {
            // The run-limit lock was only busy: requeue a few seconds out; nothing is consumed and nothing is refused.
            DateTime soon = _time.GetUtcNow().UtcDateTime.AddSeconds(Math.Clamp(contended.RetryAfterSeconds, 1, 5));
            await UpdateAsync(request.Id, r =>
            {
                r.Status = ApiRunRequest.Queued;
                r.StartingAt = null;
                r.NotBefore = soon;
                r.EstimatedStartAt = soon;
            }, ct);
            return false;
        }

        (string code, string message) = Describe(outcome.Refusal!);
        await RefuseAsync(request, code, message, ct, sp);
        return false;
    }

    private async Task<bool> RefuseAsync(ApiRunRequest request, string code, string message, CancellationToken ct, IServiceProvider? sp = null)
    {
        if (message.Length > 2000) message = message[..2000];
        await UpdateAsync(request.Id, r =>
        {
            r.Status = ApiRunRequest.Refused;
            r.ErrorCode = code;
            r.ErrorMessage = message;
            r.StartingAt = null;
            r.EstimatedStartAt = null;
        }, ct);

        using IServiceScope scope = scopes.CreateScope();
        try
        {
            await (sp ?? scope.ServiceProvider).GetRequiredService<IApiRunRequestEvents>()
                .RefusedAsync(request.Id, request.ApiKeyId, request.OrganizationId, request.ProjectId, code, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Run request refused hook failed: {Error}", ex.GetType().Name);
        }

        return false;
    }

    private async Task NotifyStartedAsync(IServiceProvider sp, ApiRunRequest request, Guid executionId, CancellationToken ct)
    {
        try
        {
            await sp.GetRequiredService<IApiRunRequestEvents>()
                .StartedAsync(request.Id, request.ApiKeyId, request.OrganizationId, request.ProjectId, executionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Run request started hook failed: {Error}", ex.GetType().Name);
        }
    }

    private async Task UpdateAsync(Guid requestId, Action<ApiRunRequest> change, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetRequiredService<ITenantContext>().BeginSystemScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ApiRunRequest row = await db.ApiRunRequests.FirstAsync(r => r.Id == requestId, ct);
        change(row);
        row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The code and message the synchronous start answers with for this refusal (the public envelope's).</summary>
    private static (string Code, string Message) Describe(IActionResult refusal)
    {
        ObjectResult shaped = PublicApiResults.From(refusal);
        JsonNode? error = (shaped.Value as JsonObject)?["error"];
        string code = error?["code"]?.GetValue<string>() ?? PublicApiErrorCodes.ForStatus(shaped.StatusCode ?? 400);
        string message = error?["message"]?.GetValue<string>() ?? PublicApiErrorCodes.MessageForStatus(shaped.StatusCode ?? 400);
        return (code, message);
    }
}
