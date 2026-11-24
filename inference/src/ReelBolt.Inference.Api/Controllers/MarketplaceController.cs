using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketplace;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Every marketplace route is a bare 404 while <c>Marketplace:Enabled</c> is false, even for a body that would fail model binding:
/// a resource filter runs before binding. <c>[Authorize]</c> is enforced by the authorization middleware before any filter, so an
/// unauthenticated caller still gets 401 (documented), never a body.
/// </summary>
public sealed class MarketplaceEnabledFilter(IOptionsSnapshot<MarketplaceOptions> options) : IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (!options.Value.Enabled)
            context.Result = new NotFoundResult();
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}

/// <summary>
/// Owner side of the recipe marketplace (docs/design/recipe-marketplace.md): consent text, publish preview, publish, new version,
/// unpublish and the caller organization's own recipes. No author, project, workflow, provider, file, voice, step-result or
/// storage identifier appears in any response; owner actions answer 404 for another organization's recipe.
/// </summary>
[ApiController]
[Route("api/v1/marketplace")]
[Authorize]
[TypeFilter(typeof(MarketplaceEnabledFilter))]
public sealed class MarketplaceController(MarketplacePublishService service) : ControllerBase
{
    [HttpGet("consent")]
    [ProducesResponseType<ConsentResponse>(StatusCodes.Status200OK)]
    public ActionResult<ConsentResponse> Consent() => service.GetConsent();

    [HttpPost("publish-preview")]
    [ProducesResponseType<PublishPreviewResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> PublishPreview([FromBody] PublishPreviewRequest request, CancellationToken ct) =>
        Map(await service.PreviewAsync(request.ProjectId, request.WorkflowId, ct));

    [HttpPost("recipes")]
    [ProducesResponseType<PublishedRecipeResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] PublishRecipeRequest request, CancellationToken ct) =>
        Map(await service.CreateAsync(request, ct), StatusCodes.Status201Created);

    [HttpPut("recipes/{id:guid}")]
    [ProducesResponseType<PublishedRecipeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid id, [FromBody] PublishRecipeRequest request, CancellationToken ct) =>
        Map(await service.UpdateAsync(id, request, ct));

    [HttpPost("recipes/{id:guid}/unpublish")]
    [ProducesResponseType<PublishedRecipeResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Unpublish(Guid id, CancellationToken ct) =>
        Map(await service.UnpublishAsync(id, ct));

    [HttpGet("mine")]
    [ProducesResponseType<MineResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Map(await service.MineAsync(ct));

    private IActionResult Map<T>(MarketplaceResult<T> result, int successStatus = StatusCodes.Status200OK)
    {
        if (result.Error is not { } error)
            return StatusCode(successStatus, result.Value);
        if (error.Code is null)
            return StatusCode(error.Status);
        object body = error.Refusals is null
            ? new { code = error.Code, message = error.Message }
            : new { code = error.Code, message = error.Message, refusals = error.Refusals };
        return StatusCode(error.Status, body);
    }
}
