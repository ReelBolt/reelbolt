using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

public sealed record PublicOutputView(
    Guid StepResultId, int? StepOrder, string Kind, string FileName, long SizeBytes, double? DurationSec, DateTime CreatedAt);

public sealed record PublicOutputLinkView(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// A run's outputs and short-lived download links over the public API. The list reuses the dashboard's output
/// rules (addressable keys only, held outputs omitted, a copied row is not a render); the link is minted only
/// after the same gates as <c>OutputsController.DownloadOutput</c>, publish moderation included. Never a storage key.
/// </summary>
public sealed class RunOutputsController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IProjectAccessService projectAccess,
    IOutputDescriber describer,
    IOutputLinkService links,
    IConfiguration configuration,
    PublishModeration? publishModeration = null) : PublicApiControllerBase
{
    [HttpGet("projects/{projectId:guid}/runs/{runId:guid}/outputs")]
    [RequireScope(ApiScopes.OutputsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> List(Guid projectId, Guid runId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        var execution = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == runId && e.ProjectId == projectId)
            .Select(e => new { e.Id, e.WorkflowDefinitionId })
            .FirstOrDefaultAsync(ct);
        if (execution is null) return PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Run not found.");

        var rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == runId && r.OutputStorageKey != null && r.ReusedFromStepResultId == null && r.SupersededAt == null)
            .OrderBy(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => new { r.Id, r.WorkflowStepId, StorageKey = r.OutputStorageKey!, r.OutputJson, Produced = r.CompletedAt ?? r.ExecutedAt })
            .ToListAsync(ct);
        rows = rows.Where(r => ProjectOutputKeys.IsAddressableOutput(projectId, r.StorageKey)).ToList();

        HashSet<Guid> held = await OutputGate.HeldResultIdsAsync(db, projectId, rows.Select(r => r.Id).ToList(), ct);
        List<Guid> stepIds = rows.Select(r => r.WorkflowStepId).Distinct().ToList();
        Dictionary<Guid, int> orders = await db.WorkflowSteps.AsNoTracking()
            .Where(s => stepIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s.StepOrder, ct);

        List<PublicOutputView> data = [];
        foreach (var row in rows.Where(r => !held.Contains(r.Id)))
        {
            OutputRender? render = await describer.FindRenderAsync(projectId, row.Id, ct);
            if (render is null) continue;
            data.Add(new PublicOutputView(
                row.Id, orders.TryGetValue(row.WorkflowStepId, out int order) ? order : null,
                KindOf(render.MimeType, render.FileName), render.FileName, render.SizeBytes,
                RenderMetadataReader.Read(row.OutputJson).DurationSec, row.Produced));
        }

        return Ok(data);
    }

    [HttpPost("projects/{projectId:guid}/outputs/{stepResultId:guid}/link")]
    // Accepted by Security (L16): a POST that mints a read link and changes no state, so outputs.read is the right scope.
    [RequireScope(ApiScopes.OutputsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Link(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        Project project = access.Project!;

        WorkflowStepResult? stepResult = await db.WorkflowStepResults.AsNoTracking()
            .Include(r => r.WorkflowExecution)
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId, ct);
        if (stepResult is null || stepResult.OutputStorageKey is null)
            return PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "This step result has no output.");

        // The download route's gates, in its order: a held output, an addressable key, then publish moderation.
        if (await OutputGate.IsResultHeldAsync(db, stepResult.Id, ct)) return PublicRefusal(OutputGate.Unavailable());
        if (!ProjectOutputKeys.IsAddressableOutput(projectId, stepResult.OutputStorageKey))
            return PublicError(StatusCodes.Status403Forbidden, PublicApiErrorCodes.Forbidden, PublicApiErrorCodes.MessageForStatus(403));

        if (publishModeration is not null)
        {
            string fileName = Path.GetFileName(stepResult.OutputStorageKey);
            PublishModerationOutcome outcome = await publishModeration.EvaluateObjectAsync(
                projectId, stepResult.OutputStorageKey, MimeOf(fileName),
                new ModerationAttribution(
                    project.OrganizationId, projectId, currentUser.UserId, stepResult.WorkflowExecutionId,
                    stepResult.Id, ModerationSurface.Publish, ModerationContentKind.Text),
                ct);
            if (!outcome.Allowed)
            {
                string code = outcome.Code ?? "CONTENT_MODERATION_UNAVAILABLE";
                if (code == PublishModerationOutcome.PendingCode)
                {
                    string retryAfter = OutputModerationTiming.PendingRetryAfterHeader(configuration);
                    Response.Headers.RetryAfter = retryAfter;
                    return PublicError(StatusCodes.Status202Accepted, code, outcome.Message,
                        new Dictionary<string, object?>
                        {
                            ["retryAfter"] = int.Parse(retryAfter, CultureInfo.InvariantCulture),
                            ["final"] = outcome.Final
                        });
                }

                return PublicError(
                    code == "CONTENT_BLOCKED" ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status409Conflict,
                    code, outcome.Message, new Dictionary<string, object?> { ["final"] = outcome.Final });
            }
        }

        OutputLink link = links.Create(new OutputLinkTarget(projectId, OutputLinkKind.StepResult, stepResult.Id, null), currentUser.UserId, null);
        return Ok(new PublicOutputLinkView(link.Url, link.ExpiresAt));
    }

    private static string KindOf(string mime, string fileName)
    {
        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "video";
        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "image";
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "audio";
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".mp4" or ".webm" or ".mov" or ".mkv" => "video",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => "image",
            ".mp3" or ".wav" or ".m4a" or ".ogg" or ".opus" or ".aac" => "audio",
            _ => "file"
        };
    }

    private static string MimeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".gif" => "image/gif",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
