using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The two small endpoints behind the assistant's permission cards (docs/design/assistant-approvals.md):
/// answering a card, and reading/changing the caller's own approval mode.
/// </summary>
/// <remarks>
/// Both act on the CALLER only — the user id comes from the verified token, never from the path or the body,
/// so there is no way to answer someone else's card or change someone else's mode. An unknown, foreign or
/// finished request is <c>404</c> in every case; a <c>403</c> would confirm that it exists.
/// </remarks>
[ApiController]
[Route("api/v1/assistant")]
[Authorize]
public class AssistantApprovalsController : ControllerBase
{
    public const string AskMode = "ask";
    public const string AutoMode = "auto";

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly AssistantApprovalBroker _broker;
    private readonly double _autoLimit;

    public AssistantApprovalsController(InferenceApiDbContext db, ICurrentUser currentUser, AssistantApprovalBroker broker, IConfiguration? configuration = null)
    {
        _autoLimit = AssistantApprovalGate.ResolveAutoApproveLimit(configuration);
        _db = db;
        _currentUser = currentUser;
        _broker = broker;
    }

    /// <summary>Answers one permission card: <c>allow</c> or <c>deny</c>.</summary>
    [HttpPost("approvals/{id:guid}")]
    public IActionResult Answer(Guid id, [FromBody] AssistantApprovalAnswerRequest request)
    {
        string? decision = request.Decision?.Trim().ToLowerInvariant();
        if (decision is not ("allow" or "deny"))
            return BadRequest(new { error = "decision must be \"allow\" or \"deny\"." });

        return _broker.Answer(id, _currentUser.UserId, _currentUser.OrganizationId, decision == "allow", request.AlwaysAllow) switch
        {
            ApprovalAnswerResult.Recorded => NoContent(),
            ApprovalAnswerResult.AlreadyDecided => Conflict(new { error = "This request was already answered." }),
            _ => NotFound()
        };
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<AssistantPreferencesDto>> GetPreferences(CancellationToken ct)
    {
        bool auto = await _db.ApplicationUsers.AsNoTracking()
            .Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.AssistantAutoApprove)
            .FirstOrDefaultAsync(ct);

        return Ok(new AssistantPreferencesDto(auto ? AutoMode : AskMode, _autoLimit));
    }

    [HttpPut("preferences")]
    public async Task<ActionResult<AssistantPreferencesDto>> PutPreferences([FromBody] AssistantPreferencesDto request, CancellationToken ct)
    {
        string? mode = request.ApprovalMode?.Trim().ToLowerInvariant();
        if (mode is not (AskMode or AutoMode))
            return BadRequest(new { error = "approvalMode must be \"ask\" or \"auto\"." });

        // Loaded-then-saved rather than ExecuteUpdateAsync (which the InMemory test provider cannot run).
        ApplicationUser? user = await _db.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == _currentUser.UserId, ct);
        if (user is null)
            return NotFound();

        user.AssistantAutoApprove = mode == AutoMode;
        await _db.SaveChangesAsync(ct);

        // Read back from the row: what is returned is what is stored, not what was asked for.
        return await GetPreferences(ct);
    }
}
