using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The caller's OWN daily video-generation spend limit and alert level (docs/design/video-spend-limit.md). Per USER, keyed by
/// the JWT's <c>sub</c>; nothing here reads or writes another person's setting. A null limit (the default, and the answer
/// when there is no row) means no daily limit beyond the credit balance and each step's <c>MaxSpendCredits</c>. The engine's
/// budget gate enforces the limit; the alert is shown in the app (no notification is sent).
/// </summary>
[ApiController]
[Route("api/v1/video-spend-limit")]
[Authorize]
public class VideoSpendLimitController(InferenceApiDbContext db, ICurrentUser currentUser) : ControllerBase
{
    public const int MaxBodyBytes = 1024;

    /// <summary>The largest limit or alert a person may set, in credits (far above any plan; a typo guard, not a product cap).</summary>
    public const decimal MaxCredits = 100_000_000m;

    [HttpGet]
    public async Task<ActionResult<UserVideoLimitView>> Get(CancellationToken ct)
        => Ok(await VideoBudgetSnapshot.ForUserAsync(db, currentUser.UserId, ct));

    [HttpPut]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<UserVideoLimitView>> Put([FromBody] SaveVideoSpendLimitRequest request, CancellationToken ct)
    {
        string? error = Validate(request);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        Guid userId = currentUser.UserId;
        UserVideoSpendLimit? row = await db.UserVideoSpendLimits.FirstOrDefaultAsync(l => l.UserId == userId, ct);
        DateTime now = DateTime.UtcNow;
        if (row is null)
        {
            row = new UserVideoSpendLimit { UserId = userId, CreatedAt = now };
            db.UserVideoSpendLimits.Add(row);
        }

        row.DailyLimitCredits = request.DailyLimitCredits is { } limit ? Math.Round(limit, 4) : null;
        row.AlertThresholdCredits = request.AlertThresholdCredits is { } alert ? Math.Round(alert, 4) : null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Ok(await VideoBudgetSnapshot.ForUserAsync(db, userId, ct));
    }

    /// <summary>The first reason the body is not acceptable, or null.</summary>
    public static string? Validate(SaveVideoSpendLimitRequest request)
    {
        if (request.DailyLimitCredits is < 0 or > MaxCredits)
        {
            return "invalid_daily_limit";
        }

        if (request.AlertThresholdCredits is < 0 or > MaxCredits)
        {
            return "invalid_alert_threshold";
        }

        // An alert above the limit could never fire before the limit refuses: that is a mistake, not a setting.
        if (request.DailyLimitCredits is { } limit && request.AlertThresholdCredits is { } alert && alert > limit)
        {
            return "alert_above_limit";
        }

        return null;
    }
}

/// <summary>Body of <c>PUT /api/v1/video-spend-limit</c>: both fields are replaced; null clears.</summary>
public sealed record SaveVideoSpendLimitRequest(decimal? DailyLimitCredits, decimal? AlertThresholdCredits);
