using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Generate clip" in the editor's media pool (docs/design/avatar-objects.md "Generating a clip with avatars from the
/// editor (subject clips)"): a clip that shows up to three of the project's avatars and objects, made by one HIDDEN
/// <see cref="WorkflowDefinitionKind.SubjectClip"/> run whose takes become ordinary project video files. The checks, the
/// price, the run, the status and the stop live in <see cref="ISubjectClipService"/> (shared with the assistant); this
/// controller answers <c>404</c> while neither avatar switch is on, authorizes the project once per action, decides
/// whether THIS call may allow sending media for itself, and maps the typed answers to HTTP. No response carries a
/// storage key, a bucket or a URL.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/subject-clips")]
[Authorize]
public class SubjectClipsController : ControllerBase
{
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ISubjectClipService _clips;
    private readonly AvatarOptions _options;
    private readonly IMediaEgressCallPermission _egressPermission;

    public SubjectClipsController(
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        ISubjectClipService clips,
        IOptions<AvatarOptions> options,
        IMediaEgressCallPermission? egressPermission = null)
    {
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _clips = clips;
        _options = options.Value;
        _egressPermission = egressPermission ?? new MediaEgressCallPermission();
    }

    /// <summary>What the clip would cost and how the avatars reach the model. Writes nothing (Edit).</summary>
    [HttpPost("estimate")]
    public async Task<ActionResult<SubjectClipEstimate>> Estimate(Guid projectId, [FromBody] SubjectClipRequestBody? body, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        SubjectClipOutcome<SubjectClipEstimate> outcome = await _clips.EstimateAsync(
            project, Actor(), ToRequest(body), await MediaAllowedForThisCallAsync(project, body?.AllowMediaEgress, ct), ct);
        return outcome.Refusal is { } refusal ? RefusalResult(refusal) : Ok(outcome.Value);
    }

    /// <summary>Starts the clip: <c>202</c> with the hidden run to follow under <c>.../subject-clips/{executionId}</c> (Edit, verified email).</summary>
    [HttpPost]
    public async Task<ActionResult<SubjectClipStarted>> Start(Guid projectId, [FromBody] SubjectClipRequestBody? body, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        SubjectClipOutcome<SubjectClipStarted> outcome = await _clips.StartAsync(
            project, Actor(), ToRequest(body), await MediaAllowedForThisCallAsync(project, body?.AllowMediaEgress, ct), ct);
        return outcome.Refusal is { } refusal ? RefusalResult(refusal) : Accepted(outcome.Value);
    }

    /// <summary>One subject clip of the project (View); another project's run, another kind's and a missing one are the same <c>404</c>.</summary>
    [HttpGet("{executionId:guid}")]
    public async Task<ActionResult<SubjectClipStatus>> Get(Guid projectId, Guid executionId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        SubjectClipStatus? status = await _clips.GetAsync(projectId, executionId, ct);
        return status is null ? NotFound() : Ok(status);
    }

    /// <summary>Stops a clip that has not ended (Edit): <c>202</c> stopping, <c>409</c> when it ended first, <c>404</c> otherwise.</summary>
    [HttpPost("{executionId:guid}/cancel")]
    public async Task<ActionResult<SubjectClipStopped>> Cancel(Guid projectId, Guid executionId, CancellationToken ct)
    {
        if (!_options.AnyEnabled) return NotFound();
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        SubjectClipOutcome<SubjectClipStopped> outcome = await _clips.CancelAsync(projectId, executionId, _currentUser.UserId, ct);
        return outcome.Refusal is { } refusal ? RefusalResult(refusal) : Accepted(outcome.Value);
    }

    // ------------------------------------------------------------------ helpers

    private SubjectClipActor Actor() => new(_currentUser.UserId, _currentUser.EmailVerified);

    /// <summary>
    /// Whether THIS call may send the avatars' files out although the project's consent is not recorded: the body says so
    /// (<c>allowMediaEgress: true</c>) AND the one rule lets this caller allow it on this route (a person signed in to the
    /// app, or an API key with <c>media.egress</c>; see <see cref="IMediaEgressCallPermission"/>). The project's recorded
    /// consent is read by the service itself.
    /// </summary>
    private async Task<bool> MediaAllowedForThisCallAsync(Project project, bool? allowMediaEgress, CancellationToken ct) =>
        allowMediaEgress == true
        && project.SubjectMediaConsentAt is null
        && await _egressPermission.MayAllowAsync(
            MediaEgressCall.FromRequest(MediaEgressSurface.SubjectClip, _currentUser.UserId, project.Id, HttpContext), ct);

    private static SubjectClipRequest ToRequest(SubjectClipRequestBody? body) => new(
        body?.Subjects?.Select(s => s?.AvatarId ?? Guid.Empty).ToList(),
        body?.Prompt,
        body?.CapabilityId,
        body?.DurationSeconds,
        body?.Ratio,
        body?.Resolution,
        body?.Takes,
        body?.SpeechFileId,
        body?.SubjectMode);

    /// <summary>
    /// A typed refusal as an HTTP answer. A bare <c>404</c> stays bare (it must not be told apart from a missing route); an
    /// avatar that is not in the project keeps its <c>AVATAR_NOT_FOUND</c> body; a run-rate refusal carries <c>Retry-After</c>.
    /// </summary>
    private ActionResult RefusalResult(AvatarRefusal refusal)
    {
        if (refusal.StatusCode == StatusCodes.Status404NotFound && refusal.Code == "not_found") return NotFound();
        if (refusal.StatusCode == StatusCodes.Status429TooManyRequests
            && refusal.Extra?.TryGetValue("retryAfterSeconds", out object? wait) == true && wait is int seconds && seconds > 0)
        {
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        return refusal.ToResult();
    }
}
