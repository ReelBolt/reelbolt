using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Feedback;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The in-product bug / feedback channel — the alpha's primary signal channel and, by product decision,
/// the only place user prose is stored.
///
/// <para><b>Why caller-scoped and not project-scoped.</b> A report is about the product, not about a
/// project resource: the person is the subject and the sink is a row an operator reads. A project or
/// an execution is optional <i>context</i> the user chooses to attach, not the route's authority, so
/// hanging the endpoint off <c>/projects/{projectId}/feedback</c> would make the route's tenancy the
/// question when the real question is what the user attached. The context is therefore validated
/// field by field below: a project the caller cannot see, or an execution they cannot see, is a
/// <b>404</b> — never a <b>403</b>, which would confirm it exists.</para>
/// </summary>
[ApiController]
[Route("api/v1/feedback")]
[Authorize]
public class FeedbackController : ControllerBase
{
    private readonly FeedbackService _feedback;
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;

    public FeedbackController(
        InferenceApiDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
        // Built here rather than registered in DI: the composition root is not this ticket's file.
        _feedback = new FeedbackService(db, projectAccess);
    }

    /// <summary>
    /// Stores one report. The body's <c>context</c> is exactly what the user attached; nothing else is
    /// collected. Returns <c>201</c> with the stored id.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(128 * 1024)]
    public async Task<IActionResult> Submit([FromBody] FeedbackRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "invalid_body", message = "A JSON body is required." });
        }

        FeedbackSubmitResult result = await _feedback.SubmitAsync(
            _currentUser.UserId, ActiveOrganization.Require(_currentUser), request, ct);

        return result.Outcome switch
        {
            FeedbackOutcome.Created => StatusCode(
                StatusCodes.Status201Created,
                new FeedbackCreatedResponse(result.ReportId!.Value, result.CreatedAt!.Value)),
            FeedbackOutcome.InvalidKind => BadRequest(
                new { error = "invalid_kind", message = "kind must be 'bug' or 'feedback'." }),
            FeedbackOutcome.InvalidMessage => BadRequest(new
            {
                error = "invalid_message",
                message = $"message is required and at most {FeedbackService.MaxMessageLength} characters."
            }),
            FeedbackOutcome.InvalidContext => BadRequest(new { error = "invalid_context", message = result.Message }),
            FeedbackOutcome.ProjectNotFound or FeedbackOutcome.ExecutionNotFound => NotFound(),
            FeedbackOutcome.RateLimited => StatusCode(
                StatusCodes.Status429TooManyRequests, new { error = "rate_limited", message = result.Message }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// The operator's read of the sink: the newest reports, platform admins only. Without this the
    /// "sink an operator reads" would be a database row and nothing else.
    /// </summary>
    [HttpGet("admin")]
    public async Task<IActionResult> ListForOperators(
        [FromQuery] int? limit, CancellationToken ct)
    {
        if (!_currentUser.IsPlatformAdmin)
        {
            return Forbid();
        }

        int take = Math.Clamp(limit ?? 100, 1, 500);
        List<FeedbackReportDto> reports = await _db.FeedbackReports.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new FeedbackReportDto(r.Id, r.UserId, r.Kind, r.Message, r.ContextJson, r.CreatedAt))
            .ToListAsync(ct);

        return Ok(reports);
    }
}
