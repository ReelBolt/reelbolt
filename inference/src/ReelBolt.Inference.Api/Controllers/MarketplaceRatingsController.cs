using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketplace;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Ratings and the caller's relation to a recipe (docs/design/recipe-marketplace.md). Bare 404 while <c>Marketplace:Enabled</c> is
/// false; <c>[Authorize]</c> answers 401 before any filter for an anonymous caller.
/// </summary>
[ApiController]
[Route("api/v1/marketplace/recipes/{id:guid}")]
[Authorize]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplaceRatingsController(MarketplaceRatingService service) : ControllerBase
{
    [HttpPut("rating")]
    [ProducesResponseType<RatingResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rate(Guid id, [FromBody] RateRecipeRequest request, CancellationToken ct) =>
        Map(await service.RateAsync(id, request.Stars, ct));

    [HttpGet("me")]
    [ProducesResponseType<RecipeMeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(Guid id, CancellationToken ct) => Map(await service.MeAsync(id, ct));

    private IActionResult Map<T>(MarketplaceResult<T> result)
    {
        if (result.Error is not { } error)
            return Ok(result.Value);
        return error.Code is null
            ? StatusCode(error.Status)
            : StatusCode(error.Status, new { code = error.Code, message = error.Message });
    }
}
