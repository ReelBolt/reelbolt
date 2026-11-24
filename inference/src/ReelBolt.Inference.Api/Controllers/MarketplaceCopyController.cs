using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketplace;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Copy of a community recipe into a project (docs/design/recipe-marketplace.md). The route sits on the action, not the class, and
/// the project is authorized once inside <see cref="MarketplaceCopyService"/> (Edit). Bare 404 while <c>Marketplace:Enabled</c> is false.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplaceCopyController(MarketplaceCopyService service) : ControllerBase
{
    [HttpPost("projects/{projectId:guid}/marketplace/{id:guid}/copy")]
    [ProducesResponseType<CopyResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<CopyResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Copy(Guid projectId, Guid id, [FromBody] CopyRecipeRequest? request, CancellationToken ct)
    {
        MarketplaceResult<CopyOutcome> result = await service.CopyAsync(projectId, id, request, ct);
        if (result.Error is { } error)
        {
            return error.Code is null
                ? StatusCode(error.Status)
                : StatusCode(error.Status, new { code = error.Code, message = error.Message });
        }

        CopyOutcome outcome = result.Value!;
        return StatusCode(outcome.AlreadyCopied ? StatusCodes.Status200OK : StatusCodes.Status201Created, outcome.Body);
    }
}
