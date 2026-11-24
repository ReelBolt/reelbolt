using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// The durable "once per user, per event" marker for the alpha funnel (alpha-launch T-018).
/// </summary>
public interface IAlphaFunnelClaims
{
    /// <summary>
    /// Claims <paramref name="eventName"/> for <paramref name="userId"/>. True **only** for the call
    /// that newly claimed it; every later call is false. The caller counts the event only on true.
    /// </summary>
    Task<bool> ClaimAsync(Guid userId, string eventName, CancellationToken ct);
}

/// <summary>
/// The claim marker on PostgreSQL, on the DbContext's own connection, so every API replica shares it
/// and a restart resets nothing. The claim is a single atomic insert: the unique key does the
/// de-duplication, so two concurrent callers cannot both be told they claimed the event.
/// </summary>
public sealed class AlphaFunnelClaimStore(InferenceApiDbContext db) : IAlphaFunnelClaims
{
    /// <summary>
    /// The claim statement. <c>ON CONFLICT DO NOTHING</c> reports 0 rows to a caller whose row
    /// already exists, which is exactly "someone got here first".
    /// </summary>
    public const string ClaimSql =
        "INSERT INTO alpha_funnel_claims (user_id, event, claimed_at) VALUES ($1, $2, $3) "
        + "ON CONFLICT (user_id, event) DO NOTHING";

    public async Task<bool> ClaimAsync(Guid userId, string eventName, CancellationToken ct)
    {
        int rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO alpha_funnel_claims (user_id, event, claimed_at) VALUES ({userId}, {eventName}, {DateTimeOffset.UtcNow}) ON CONFLICT (user_id, event) DO NOTHING",
            ct);
        return rows > 0;
    }
}
