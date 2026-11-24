using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>
/// The base of every <c>/api/public/v1/**</c> controller. It fixes the route prefix, the <c>public-v1</c>
/// OpenAPI document, and the shared gate (<see cref="PublicApiGuardFilter"/>: flag off 404, key-only 401,
/// the per-key rate limit, <c>X-Request-Id</c> and the audit row). Resource controllers only write actions.
/// </summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(GroupName = PublicApiControllerBase.DocumentName)]
[PublicApiAudience]
[ServiceFilter(typeof(PublicApiGuardFilter))]
[Route("api/public/v1")]
public abstract class PublicApiControllerBase : ControllerBase
{
    public const string DocumentName = "public-v1";
    public const string RoutePrefix = "api/public/v1";

    /// <summary>
    /// Builds <c>{error:{code,message,...extras}}</c> at <paramref name="status"/>. A 429 goes through the one
    /// rate-limited helper, so it always carries <c>Retry-After</c>, <c>RateLimit-Reset</c> and
    /// <c>error.resetsAt</c>, whichever controller produced it.
    /// </summary>
    protected ObjectResult PublicError(int status, string code, string message, object? extras = null)
    {
        ObjectResult result = PublicApiEnvelope.Result(status, code, message, extras);
        return status == StatusCodes.Status429TooManyRequests
            ? PublicApiEnvelope.RateLimited(Response, result)
            : result;
    }

    /// <summary>
    /// The one adapter from an existing service refusal (RunStartGate, PlanGateResults, a quota gate …) to
    /// the public envelope; never re-implement the refusal. A 429 also gets the shared rate-limit treatment
    /// here, so every 429 the surface emits carries the three fields (Q-4), whether the refusal came from a
    /// filter or a service.
    /// </summary>
    protected ObjectResult PublicRefusal(IActionResult result) =>
        PublicApiEnvelope.RateLimited(Response, PublicApiResults.From(result));
}
