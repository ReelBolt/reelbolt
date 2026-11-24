using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.PublicApi.Dto;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary><c>GET /api/public/v1/templates</c>: the workflow templates the deployment offers (no project involved).</summary>
[Route("api/public/v1/templates")]
public sealed class PublicTemplatesController(WorkflowTemplateProvisioningService templates) : PublicResourceControllerBase
{
    [RequireScope(ApiScopes.WorkflowsRead)]
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> List(CancellationToken ct)
    {
        IReadOnlyList<WorkflowTemplateDefinition> available = await templates.ListRecipesAsync(ct);
        List<PublicTemplateView> data = [];
        foreach (WorkflowTemplateDefinition t in available)
        {
            string costClass = AppCostClass.Derive(AppStepFacts.FromTemplate(t));
            data.Add(new PublicTemplateView(t.Key, t.Name, t.Description, costClass, t.RequiresUserInput));
        }

        return Ok(new { data });
    }
}
