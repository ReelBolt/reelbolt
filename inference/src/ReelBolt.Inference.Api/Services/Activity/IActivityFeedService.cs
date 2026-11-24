using ReelBolt.Inference.Api.Controllers.Dto;

namespace ReelBolt.Inference.Api.Services.Activity;

/// <summary>One read of the background jobs of the caller's active workspace (<c>GET /api/v1/activity</c>).</summary>
public interface IActivityFeedService
{
    Task<ActivityFeedDto> GetAsync(int limit, CancellationToken ct);
}
