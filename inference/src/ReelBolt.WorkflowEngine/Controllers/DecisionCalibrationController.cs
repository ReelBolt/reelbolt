using ReelBolt.Shared.Auth;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.WorkflowEngine.Services.Decision;

namespace ReelBolt.WorkflowEngine.Controllers;

/// <summary>
/// Read-only calibration report over <c>decision_observations</c>.
///
/// <para>
/// <b>Why this route.</b> The table and the aggregation are WorkflowEngine-owned, so this cannot
/// live on the Inference API. It deliberately does <b>not</b> live under <c>/api/v1/admin/*</c>
/// either: nginx routes that prefix to the <b>Go API</b>, while <c>/api/v1/workflow-engine/</c>
/// reaches this service through the Inference API's <c>WorkflowEngineProxyController</c>, which
/// authenticates the caller, requires platform admin and calls this route with its own credential.
/// </para>
///
/// <para>
/// <b>Why the in-action admin check.</b> <c>[Authorize]</c> alone would admit every authenticated
/// user, and the report spans every project's decision traffic. So the action repeats the house
/// claim check verbatim in shape from <c>AdminController</c> and returns <c>403</c> for anyone
/// whose <c>isAdmin</c> claim is absent, unparseable or false. No new policy, no new filter.
/// </para>
///
/// <para>
/// There is no write verb here: the calibration view is a read of what Shadow recorded, and
/// threshold persistence is a separate, reviewed surface (<c>docs/decision-models.md</c>).
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/workflow-engine/decision-calibration")]
[Authorize]
public class DecisionCalibrationController : ControllerBase
{
    /// <summary>
    /// Confidence threshold the report recomputes escalation at when the caller does not supply
    /// one. Phase 1's <c>DecisionGateConfig</c> default, deliberately shipped as a default rather
    /// than as a tuned number — the local install has no observations to tune against.
    /// </summary>
    public const double DefaultAcceptAt = 0.85;

    /// <summary>
    /// Top-two margin the report recomputes escalation at when the caller does not supply one.
    /// Phase 1's <c>DecisionGateConfig</c> default, same rationale as
    /// <see cref="DefaultAcceptAt"/>.
    /// </summary>
    public const double DefaultMinMargin = 0.25;

    private readonly IDecisionCalibrationService _calibrationService;

    public DecisionCalibrationController(IDecisionCalibrationService calibrationService)
    {
        _calibrationService = calibrationService;
    }

    /// <summary>
    /// Returns the calibration report computed at the supplied thresholds.
    /// </summary>
    /// <param name="acceptAt">
    /// Confidence threshold to recompute escalation at; must be within <c>[0, 1]</c>. Omitted means
    /// <see cref="DefaultAcceptAt"/>. Bound as <c>double?</c> so a supplied <c>0</c> is
    /// distinguishable from an absent parameter and is honoured rather than replaced by the
    /// default.
    /// </param>
    /// <param name="minMargin">
    /// Top-two margin to recompute escalation at; must be within <c>[0, 1]</c>. Omitted means
    /// <see cref="DefaultMinMargin"/>. Bound as <c>double?</c> for the same reason as
    /// <paramref name="acceptAt"/>.
    /// </param>
    /// <param name="providerId">
    /// Restrict the report to one inference provider; anything unknown simply matches nothing
    /// rather than erroring.
    /// </param>
    /// <param name="site">Restrict the report to one decision site; same unknown-value rule.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<ActionResult<DecisionCalibrationReport>> Get(
        [FromQuery] double? acceptAt,
        [FromQuery] double? minMargin,
        [FromQuery] Guid? providerId,
        [FromQuery] string? site,
        CancellationToken ct)
    {
        if (!AuthClaims.ReadPlatformAdmin(User))
        {
            return Forbid();
        }

        double resolvedAcceptAt = acceptAt ?? DefaultAcceptAt;
        double resolvedMinMargin = minMargin ?? DefaultMinMargin;

        // Range-checked, never clamped: a caller who asks for a threshold outside [0, 1] has made a
        // mistake the report cannot silently repair, and a clamped 200 would make the calibration
        // view plot numbers the caller never asked for. NaN/Infinity are rejected here too — the
        // range comparison alone would let them through.
        if (!IsUnitInterval(resolvedAcceptAt) || !IsUnitInterval(resolvedMinMargin))
        {
            return BadRequest(new
            {
                error = "acceptAt and minMargin must both be within [0, 1].",
                acceptAt = resolvedAcceptAt,
                minMargin = resolvedMinMargin
            });
        }

        DecisionCalibrationReport report = await _calibrationService.GetCalibrationAsync(
            resolvedAcceptAt, resolvedMinMargin, providerId, site, ct);

        return Ok(report);
    }

    private static bool IsUnitInterval(double value)
        => double.IsFinite(value) && value >= 0d && value <= 1d;
}
