using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Auth;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Streams the large, non-playable JSON artifact (full video-analysis document or a compile
/// step's edit-decision-list audit trail) referenced by a workflow step result's
/// <see cref="WorkflowStepResult.ArtifactStorageKey"/>. Mirrors <see cref="OutputsController"/>'s
/// authorization/validation guards exactly, but validates against the artifact key prefix
/// (<c>projects/{projectId}/agentFiles/video-analysis</c>, per the WorkflowEngine's
/// ProjectFileWorkspace.UploadArtifactAsync key construction and
/// <see cref="WorkflowStepResult.ArtifactStorageKey"/>'s own doc comment) rather than the
/// outputFiles prefix, so this endpoint can never be coerced into serving an unrelated object —
/// including another project's rendered mp4 under outputFiles.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/step-results/{stepResultId:guid}")]
[Authorize]
public class StepResultArtifactsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;

    public StepResultArtifactsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IAmazonS3 s3Client,
        IConfiguration configuration)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _s3Client = s3Client;
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
    }

    [HttpGet("artifact")]
    public async Task<IActionResult> GetArtifact(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowStepResult? stepResult = await _db.WorkflowStepResults
            .Include(r => r.WorkflowExecution)
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId, ct);

        if (stepResult == null) return NotFound();
        if (await ReelBolt.Inference.Api.Services.Outputs.OutputGate.IsResultHeldAsync(_db, stepResult.Id, ct))
            return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();
        if (stepResult.ArtifactStorageKey == null) return NotFound("This step result has no artifact.");

        // Validate storage key scope: must live under this project's video-analysis artifact
        // prefix — never the outputFiles prefix (a playable render/edit) and never another
        // project's objects, even though the WorkflowExecution join above already scopes the
        // step result to this project.
        // Trailing slash makes this a true path-segment prefix — without it, a key like
        // "projects/{id}/agentFiles/video-analysis-foreign/..." would also match (found by
        // Copilot review), letting this endpoint reach objects outside its intended scope.
        string expectedKeyPrefix = $"projects/{projectId}/agentFiles/video-analysis/";
        if (!stepResult.ArtifactStorageKey.StartsWith(expectedKeyPrefix, StringComparison.Ordinal))
            return Forbid();

        GetObjectResponse s3Response;
        try
        {
            s3Response = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = stepResult.ArtifactStorageKey
            }, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            return NotFound("Artifact not found in storage.");
        }

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, "artifact.json");
        return File(s3Response.ResponseStream, "application/json");
    }

    /// <summary>
    /// Streams the timeline artifact JSON file generated for this step result directly from S3/MinIO.
    /// Extracts timelineStorageKey from OutputJson, ArtifactStorageKey (if ending in -timeline.json),
    /// or derives it from the step order. Validates that the storage key stays strictly within
    /// the project's video-analysis artifact prefix and ends with -timeline.json.
    /// </summary>
    [HttpGet("timeline")]
    public async Task<IActionResult> GetTimeline(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowStepResult? stepResult = await _db.WorkflowStepResults
            .Include(r => r.WorkflowExecution)
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId, ct);

        if (stepResult == null) return NotFound();
        if (await ReelBolt.Inference.Api.Services.Outputs.OutputGate.IsResultHeldAsync(_db, stepResult.Id, ct))
            return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();

        string? timelineStorageKey = null;

        if (!string.IsNullOrWhiteSpace(stepResult.OutputJson))
        {
            try
            {
                var node = JsonNode.Parse(stepResult.OutputJson);
                timelineStorageKey = node?["timelineStorageKey"]?.GetValue<string>();
            }
            catch (JsonException) { }
        }

        if (string.IsNullOrEmpty(timelineStorageKey) &&
            stepResult.ArtifactStorageKey != null &&
            stepResult.ArtifactStorageKey.EndsWith("-timeline.json", StringComparison.Ordinal))
        {
            timelineStorageKey = stepResult.ArtifactStorageKey;
        }

        if (string.IsNullOrEmpty(timelineStorageKey) && stepResult.WorkflowStepId != Guid.Empty)
        {
            WorkflowStep? step = await _db.WorkflowSteps.FirstOrDefaultAsync(s => s.Id == stepResult.WorkflowStepId, ct);
            if (step != null && step.StepType == StepType.VideoCompile)
            {
                timelineStorageKey = $"projects/{projectId}/agentFiles/video-analysis/{stepResult.WorkflowExecutionId:D}/step-{step.StepOrder}-timeline.json";
            }
        }

        if (string.IsNullOrEmpty(timelineStorageKey))
        {
            return NotFound("No timeline generated for this step.");
        }

        // Validate storage key scope: must live under this project's video-analysis artifact prefix
        // and end with -timeline.json
        string expectedKeyPrefix = $"projects/{projectId}/agentFiles/video-analysis/";
        if (!timelineStorageKey.StartsWith(expectedKeyPrefix, StringComparison.Ordinal) ||
            !timelineStorageKey.EndsWith("-timeline.json", StringComparison.Ordinal))
        {
            return Forbid();
        }

        GetObjectResponse s3Response;
        try
        {
            s3Response = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = timelineStorageKey
            }, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            return NotFound("Timeline artifact not found in storage.");
        }

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, "artifact.json");
        return File(s3Response.ResponseStream, "application/json");
    }

    /// <summary>
    /// Streams a subtitle file (<c>srt</c> or <c>vtt</c>) a compile step wrote beside its render when its captions came
    /// from the transcript. The key is read from the step result's <c>subtitleStorageKeys</c> (never from the request),
    /// must lie under this project's video-analysis artifact prefix and end with the extension the format names, and is
    /// served as a download with the format's own content type. Same access check as the other artifact endpoints.
    /// </summary>
    [HttpGet("subtitles/{format}")]
    public async Task<IActionResult> GetSubtitles(Guid projectId, Guid stepResultId, string format, CancellationToken ct)
    {
        string? extension = format.ToLowerInvariant() switch
        {
            "srt" => "srt",
            "vtt" => "vtt",
            _ => null
        };
        if (extension is null) return NotFound("Subtitles are available as srt or vtt.");

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        WorkflowStepResult? stepResult = await _db.WorkflowStepResults
            .Include(r => r.WorkflowExecution)
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId, ct);
        if (stepResult == null) return NotFound();
        if (await ReelBolt.Inference.Api.Services.Outputs.OutputGate.IsResultHeldAsync(_db, stepResult.Id, ct))
            return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();

        // Only a compile step writes subtitle files: an agent step's output is model text and must not name a key.
        WorkflowStep? step = stepResult.WorkflowStepId == Guid.Empty
            ? null
            : await _db.WorkflowSteps.FirstOrDefaultAsync(s => s.Id == stepResult.WorkflowStepId, ct);
        if (step is null || step.StepType != StepType.VideoCompile) return NotFound("No subtitles were written for this step.");

        string? key = null;
        if (!string.IsNullOrWhiteSpace(stepResult.OutputJson))
        {
            try
            {
                key = JsonNode.Parse(stepResult.OutputJson)?["subtitleStorageKeys"]?[extension]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        }

        if (string.IsNullOrEmpty(key)) return NotFound("No subtitles were written for this step.");

        string expectedKeyPrefix = $"projects/{projectId}/agentFiles/video-analysis/";
        string expectedKey = $"{expectedKeyPrefix}{stepResult.WorkflowExecutionId:D}/step-{step.StepOrder}-subtitles.{extension}";
        // Rebuilt from the execution and step order, so a key with dot segments, backslashes or another project's
        // prefix can never be served, whatever the stored output says.
        if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
            return Forbid();

        GetObjectResponse s3Response;
        try
        {
            s3Response = await _s3Client.GetObjectAsync(new GetObjectRequest { BucketName = _bucketName, Key = expectedKey }, ct);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            return NotFound("Subtitle file not found in storage.");
        }

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(false, "subtitles." + extension);
        return File(s3Response.ResponseStream, extension == "srt" ? "application/x-subrip" : "text/vtt");
    }

    private static bool IsMissingObjectError(AmazonS3Exception ex)
    {
        return ex.StatusCode == HttpStatusCode.NotFound ||
               string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
               string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal);
    }
}
