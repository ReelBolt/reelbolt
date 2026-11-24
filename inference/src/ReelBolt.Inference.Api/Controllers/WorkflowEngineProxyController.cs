using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Forwards the three admin reads of the WorkflowEngine. nginx sends <c>/api/v1/workflow-engine/*</c> here
/// (all but <c>health</c>), so the engine never has to validate a session token: this controller
/// authenticates the caller, requires platform admin (database truth, see <c>MembershipValidator</c>) and
/// calls the engine with the credential from <see cref="IEngineCallCredential"/>. An MCP token reaches only
/// the calibration read, and only with <c>assistant.admin</c> (<c>McpScopeRoutes</c>). The engine's status
/// code and body are returned unchanged.
/// </summary>
[ApiController]
[Authorize]
public sealed class WorkflowEngineProxyController(IHttpClientFactory clients, IEngineCallCredential credential) : ControllerBase
{
    public const string AdminStatusPath = "/api/v1/workflow-engine/admin/status";
    public const string SkillsStatusPath = "/api/v1/workflow-engine/skills/status";

    [HttpGet(AdminStatusPath)]
    [HttpGet(SkillsStatusPath)]
    [HttpGet(DecisionCalibrationClient.CalibrationPath)]
    public async Task<IActionResult> Forward(CancellationToken ct)
    {
        // Rollout window: the control host deploys before the engine host. Until the engine runs with the
        // engine key it rejects the token minted here (401), and while it restarts it is unreachable (502).
        // These are admin-only diagnostic reads, so that is accepted; EngineJwt:AcceptLegacyOnBehalfOf covers
        // the other direction (engine to API), which runs depend on.
        if (!AuthClaims.ReadPlatformAdmin(User))
            return Forbid();

        AuthenticationHeaderValue? authorization = await credential.ForCurrentCallerAsync(ct);
        if (authorization is null)
            return Forbid();

        HttpClient client = clients.CreateClient(DecisionCalibrationClient.HttpClientName);
        using HttpRequestMessage request = new(HttpMethod.Get, Request.Path.Value + Request.QueryString.Value);
        request.Headers.Authorization = authorization;
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(Response.Body, ct);
            return new EmptyResult();
        }
    }
}
