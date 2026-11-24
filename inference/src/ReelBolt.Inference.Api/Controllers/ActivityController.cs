using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Activity;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/activity</c>: the background jobs of the caller's active workspace across every job kind, so an activity
/// tray survives a reload. Read-only; titles are names, never ids or storage keys.
/// </summary>
[ApiController]
[Route("api/v1/activity")]
[Authorize]
public class ActivityController : ControllerBase
{
    private readonly IActivityFeedService _feed;

    public ActivityController(IActivityFeedService feed) => _feed = feed;

    /// <summary><c>limit</c> defaults to 50 and is clamped to 1..100.</summary>
    [HttpGet]
    public async Task<ActionResult<ActivityFeedDto>> Get([FromQuery] int? limit, CancellationToken ct) =>
        Ok(await _feed.GetAsync(ActivityFeedService.ClampLimit(limit), ct));
}
