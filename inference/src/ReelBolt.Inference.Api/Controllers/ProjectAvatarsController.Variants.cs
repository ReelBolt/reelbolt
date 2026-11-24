using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

// Avatar builds (docs/design/avatar-objects.md "Avatar variants", "Pulling an object out of a video"): a variant of
// an object avatar made by the picture model, and an object pulled out of a video. The routes only do what differs
// by caller (the switch, project access, and whether THIS caller may allow sending media for one call); every other
// check, the price, the hold and the job live in IAvatarBuildService, which the assistant and MCP tools call too.
public partial class ProjectAvatarsController
{
    /// <summary>What making the variant would cost. Writes nothing (Edit).</summary>
    [HttpPost("{avatarId:guid}/variants/estimate")]
    public async Task<ActionResult<AvatarVariantEstimateResponse>> EstimateVariant(
        Guid projectId, Guid avatarId, [FromBody] CreateAvatarVariantRequest? request, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        AvatarBuildOutcome<VariantEstimate> outcome = await _builds.EstimateVariantAsync(
            access.Project!, Actor(), avatarId, ToBuildRequest(request ?? new CreateAvatarVariantRequest()), ct);
        if (outcome.Refusal is { } refusal) return RefusalResult(refusal);

        VariantEstimate estimate = outcome.Value!;
        return Ok(new AvatarVariantEstimateResponse(
            estimate.BillingEnabled, estimate.Credits, estimate.CreditsHigh, estimate.BalanceCredits, estimate.Affordable,
            estimate.Pictures, estimate.Videos, estimate.UnavailableReason));
    }

    /// <summary>
    /// Asks the picture model for a variant of an object avatar: <c>202</c> with the job to follow under
    /// <c>.../avatars/builds/{jobId}</c>. Nothing is written on a refusal (Edit, verified email, the plan).
    /// </summary>
    [HttpPost("{avatarId:guid}/variants")]
    public async Task<ActionResult<AvatarBuildStartedResponse>> CreateVariant(
        Guid projectId, Guid avatarId, [FromBody] CreateAvatarVariantRequest request, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool mediaAllowed = await MediaAllowedForThisCallAsync(project, MediaEgressSurface.AvatarVariant, request.AllowMediaEgress, ct);
        AvatarBuildOutcome<AvatarBuildStarted> outcome = await _builds.StartVariantAsync(
            project, Actor(), avatarId, ToBuildRequest(request), mediaAllowed, ct);
        if (outcome.Refusal is { } refusal) return RefusalResult(refusal);

        return Accepted(new AvatarBuildStartedResponse(outcome.Value!.JobId, outcome.Value.State));
    }

    /// <summary>
    /// Pulls an object out of a video into a new object avatar (docs/design/avatar-objects.md "Pulling an object out of
    /// a video"): <c>202</c> with the job to follow under <c>.../avatars/builds/{jobId}</c>. With a segmentation model
    /// and the media permission the object is looked for in each frame; otherwise whole frames are used (Edit).
    /// </summary>
    [HttpPost("extract-object")]
    public async Task<ActionResult<AvatarBuildStartedResponse>> ExtractObject(
        Guid projectId, [FromBody] ExtractObjectRequest request, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool mediaAllowed = await MediaAllowedForThisCallAsync(project, MediaEgressSurface.ObjectExtraction, request.AllowMediaEgress, ct);
        AvatarBuildOutcome<AvatarBuildStarted> outcome = await _builds.StartExtractionAsync(
            project, Actor(),
            new ObjectExtractionRequest(
                request.MediaRef, request.ObjectDescription, request.Name, request.Description, request.AtSeconds,
                request.ConsentStatement, request.ConsentLocale),
            mediaAllowed, ct);
        if (outcome.Refusal is { } refusal) return RefusalResult(refusal);

        return Accepted(new AvatarBuildStartedResponse(outcome.Value!.JobId, outcome.Value.State));
    }

    /// <summary>The project's avatar builds, newest first (View).</summary>
    [HttpGet("builds")]
    public async Task<ActionResult<List<AvatarBuildResponse>>> ListBuilds(Guid projectId, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        IReadOnlyList<AvatarBuildStatus> builds = await _builds.ListBuildsAsync(projectId, ct);
        return Ok(builds.Select(ToBuildResponse).ToList());
    }

    /// <summary>One avatar build of the project (View); another project's job is <c>404</c>.</summary>
    [HttpGet("builds/{jobId:guid}")]
    public async Task<ActionResult<AvatarBuildResponse>> GetBuild(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        AvatarBuildStatus? build = await _builds.GetBuildAsync(projectId, jobId, ct);
        return build is null ? NotFound() : Ok(ToBuildResponse(build));
    }

    /// <summary>
    /// Cancels a build that has not ended (Edit). Idempotent: an ended build is answered as it is. What was already
    /// made is charged; the rest of what was held is given back.
    /// </summary>
    [HttpPost("builds/{jobId:guid}/cancel")]
    public async Task<ActionResult<AvatarBuildResponse>> CancelBuild(Guid projectId, Guid jobId, CancellationToken ct)
    {
        if (!_options.Objects.Enabled || _builds is null) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        AvatarBuildStatus? build = await _builds.CancelBuildAsync(projectId, jobId, ct);
        return build is null ? NotFound() : Ok(ToBuildResponse(build));
    }

    // ------------------------------------------------------------------ helpers

    private AvatarBuildActor Actor() => new(_currentUser.UserId, _currentUser.EmailVerified);

    /// <summary>
    /// Whether THIS call may send the avatar's media out although the project's consent is not recorded: the body says
    /// so (<c>allowMediaEgress: true</c>) AND the one rule lets this caller allow it on this route
    /// (<see cref="IMediaEgressCallPermission"/>: a person signed in to the app, or an API key with <c>media.egress</c>).
    /// The project's recorded consent is read by the service itself.
    /// </summary>
    private async Task<bool> MediaAllowedForThisCallAsync(Project project, MediaEgressSurface surface, bool? allowMediaEgress, CancellationToken ct) =>
        allowMediaEgress == true
        && project.SubjectMediaConsentAt is null
        && await _mediaEgressPermission.MayAllowAsync(
            MediaEgressCall.FromRequest(surface, _currentUser.UserId, project.Id, HttpContext), ct);

    private static VariantBuildRequest ToBuildRequest(CreateAvatarVariantRequest request) => new(
        request.Instruction,
        request.VariantLabel,
        request.VariantDescription,
        request.Name,
        request.Description,
        request.ReferenceIds,
        request.IncludeVideos,
        request.ConsentStatement,
        request.ConsentLocale);

    private static AvatarBuildResponse ToBuildResponse(AvatarBuildStatus build) => new(
        build.JobId,
        build.Kind,
        build.State,
        build.Stage,
        new AvatarBuildProgressResponse(build.Done, build.Total),
        build.Name,
        build.AvatarId,
        build.FailureCode,
        build.Message,
        build.Credits,
        build.CreatedAt,
        build.UpdatedAt);

    /// <summary>A typed refusal as an HTTP answer; a 404 stays bare (it must not be told apart from a missing route).</summary>
    private ActionResult RefusalResult(AvatarRefusal refusal) =>
        refusal.StatusCode == StatusCodes.Status404NotFound ? NotFound() : refusal.ToResult();
}
