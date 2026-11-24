using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Audio cleanup on demand per media file: <c>POST .../media/{mediaRef}/cleanup</c> queues a hidden one-step
/// <see cref="WorkflowDefinitionKind.AudioCleanup"/> workflow (the <c>AudioCleanup</c> step), <c>.../cleanup/estimate</c>
/// prices it and <c>GET .../cleanup/{executionId}</c> polls it. Start and estimate are <see cref="AudioCleanupService"/>
/// (shared with the assistant tools); this controller maps its answer to HTTP. No response
/// carries a storage key, bucket or URL; every project or media failure is a 404.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media")]
[Authorize]
public class AudioCleanupController : ControllerBase
{
    private const string Ref = "{mediaRef}";

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly AudioCleanupService _cleanup;

    // The constructor keeps the route's own dependencies (the route tests build it by hand); the shared service is made from them.
    public AudioCleanupController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        ICurrentUser currentUser,
        IWorkflowExecutionService executions,
        IAssistantExecutionGuard guard,
        IInferenceProviderResolver providers,
        IModelPriceService? prices = null,
        IOrganizationEntitlements? entitlements = null,
        INonWorkflowChargeService? charges = null,
        IWorkflowCostEstimator? costEstimator = null,
        TimeProvider? clock = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _projectAccess = projectAccess;
        _cleanup = new AudioCleanupService(
            db, projectAccess, currentUser, executions, guard, providers, prices, entitlements, charges, costEstimator, clock, configuration);
    }

    /// <summary>Queue the cleanup. 202 with the execution to poll.</summary>
    [HttpPost(Ref + "/cleanup")]
    public async Task<IActionResult> Start(Guid projectId, string mediaRef, [FromBody] AudioCleanupRequestBody? body, CancellationToken ct)
    {
        // The route's own Edit check; the service takes it as the authority (it re-reads only when none is passed).
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        AudioCleanupResult<AudioCleanupStartedResponse> result = await _cleanup.StartAsync(projectId, mediaRef, body, ct, access);
        if (result.Failure is { } failure)
        {
            if (failure.RetryAfterSeconds is { } retry)
                Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return failure.Result;
        }

        return StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    /// <summary>What the cleanup would cost, before it is asked for. Nothing is persisted and no vendor is called.</summary>
    [HttpPost(Ref + "/cleanup/estimate")]
    public async Task<IActionResult> Estimate(Guid projectId, string mediaRef, [FromBody] AudioCleanupRequestBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        AudioCleanupResult<AudioCleanupEstimateResponse> result = await _cleanup.EstimateAsync(projectId, mediaRef, body, ct, access);
        return result.Failure is { } failure ? failure.Result : Ok(result.Value);
    }

    /// <summary>State and result of one cleanup run. The cleaned file is the registered project file (<c>outputFileId</c>).</summary>
    [HttpGet(Ref + "/cleanup/{executionId:guid}")]
    public async Task<IActionResult> Status(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        WorkflowExecution? execution = await _db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId
                && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.AudioCleanup, ct);
        if (execution is null) return NotFound();
        if (await ReelBolt.Inference.Api.Services.Outputs.OutputGate.IsExecutionHeldAsync(_db, execution, ct))
            return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();

        string? configJson = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == execution.WorkflowDefinitionId && s.StepType == StepType.AudioCleanup)
            .Select(s => s.AudioCleanupConfigJson).FirstOrDefaultAsync(ct);
        string? target = AudioCleanupService.TryParseConfig(configJson)?.Target.ToString();

        string state = execution.Status switch
        {
            ExecutionStatus.Queued => "queued",
            ExecutionStatus.Running => "running",
            ExecutionStatus.Passed => "ready",
            ExecutionStatus.Cancelled => "cancelled",
            _ => "failed"
        };
        if (state is "queued" or "running")
            return Ok(new AudioCleanupStatusResponse(execution.Id, state, target, null, null, null, null, null, null));

        string? output = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == execution.Id)
            .OrderByDescending(r => r.ExecutedAt)
            .Select(r => r.Output).FirstOrDefaultAsync(ct);
        JsonElement root = AudioCleanupService.ParseObject(output);

        if (state != "ready")
        {
            string? code = AudioCleanupService.StringOf(root, "errorCode");
            string message = AudioCleanupService.StringOf(root, "message") ?? execution.ErrorMessage ?? "The audio cleanup did not finish.";
            return Ok(new AudioCleanupStatusResponse(execution.Id, state, target, null, null, null, null, message, code));
        }

        bool applied = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("applied", out JsonElement a) && a.ValueKind != JsonValueKind.False;
        double? durationSec = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("durationSec", out JsonElement d) && d.TryGetDouble(out double dv) ? dv : null;
        Guid? fileId = Guid.TryParse(AudioCleanupService.StringOf(root, "outputFileId"), out Guid parsed) ? parsed : null;
        if (fileId is { } id && !await _db.ProjectFiles.AsNoTracking().AnyAsync(f => f.Id == id && f.ProjectId == projectId, ct))
            fileId = null;

        string? note = applied ? null : "Nothing to remove was found, so the audio is unchanged.";
        return Ok(new AudioCleanupStatusResponse(
            execution.Id, "ready", target, AudioCleanupService.StringOf(root, "method"), applied, durationSec, applied ? fileId : null, note, null));
    }
}
