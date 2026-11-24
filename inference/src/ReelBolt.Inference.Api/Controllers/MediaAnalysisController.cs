using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// R09.1: analysis on demand per media file. Silence, shots and motion are deterministic and free; the transcript is
/// metered (an up-front estimate, <c>402 INSUFFICIENT_CREDITS</c> when the balance cannot cover it). Results are cached by
/// FILE DIGEST (same bytes, same organization, any project) by the engine's analysis consumer; this controller owns the
/// request rows, the estimate and the reads. Never triggered from a GET. No response carries a storage key.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media")]
[Authorize]
public class MediaAnalysisController : ControllerBase
{
    // One template per action (no regex constraint): a token that is not file:<guid> or step_result:<guid> is a 404
    // from FindSourceAsync, the same answer a foreign token gets.
    private const string Ref = "{mediaRef}";

    /// <summary>A queued or running kind older than this is reported failed: the engine never answered.</summary>
    internal static readonly TimeSpan StaleAfter = MediaAnalysisAccess.StaleAfter;

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IPublishEndpoint _publish;
    private readonly ICurrentUser _currentUser;
    private readonly IInferenceProviderResolver _providers;
    private readonly ILogger<MediaAnalysisController> _logger;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IWorkflowCostEstimator? _costEstimator;

    public MediaAnalysisController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IPublishEndpoint publish,
        ICurrentUser currentUser,
        IInferenceProviderResolver providers,
        ILogger<MediaAnalysisController> logger,
        INonWorkflowChargeService? charges = null,
        IWorkflowCostEstimator? costEstimator = null)
    {
        _db = db;
        _projectAccess = projectAccess;
        _publish = publish;
        _currentUser = currentUser;
        _providers = providers;
        _logger = logger;
        _charges = charges;
        _costEstimator = costEstimator;
    }

    private bool BillingOn => _costEstimator is { BillingEnabled: true } && _charges is not null;

    /// <summary>Start analysis of the named kinds. Free kinds just queue; a transcript is checked against the balance first.</summary>
    [HttpPost(Ref + "/analysis")]
    public async Task<IActionResult> Start(Guid projectId, string mediaRef, [FromBody] MediaAnalysisRequestBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (!TryReadBody(body, out IReadOnlyList<string> kinds, out string language, out IActionResult? bad)) return bad!;

        if (await MediaAnalysisAccess.IsHeldAsync(_db, projectId, mediaRef, ct)) return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();
        MediaAnalysisAccess.SourceLookup lookup = await MediaAnalysisAccess.FindSourceAsync(_db, projectId, mediaRef, ct);
        if (lookup.Source is not { } source) return NotFound();
        if (!source.Analysable) return BadRequest(new { message = MediaAnalysisAccess.NotAnalysableMessage });
        if (access.Project?.OrganizationId is not { } organizationId) return NotFound();

        bool wantsTranscript = kinds.Contains(MediaAnalysisKinds.Transcript);
        if (wantsTranscript && EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        DateTime now = DateTime.UtcNow;
        List<MediaAnalysisRequest> existing = await MediaAnalysisAccess.LoadRowsAsync(_db, projectId, mediaRef, ct);
        Dictionary<string, MediaAnalysisKindState> states = new();
        List<MediaAnalysisRequest> fresh = [];
        Guid requestId = Guid.NewGuid();
        foreach (string kind in kinds)
        {
            string lang = LanguageFor(kind, language);
            MediaAnalysisRequest? current = existing.Where(r => r.Kind == kind && r.Language == lang)
                .OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            if (current is not null && await MediaAnalysisAccess.IsReusableAsync(_db, current, organizationId, now, ct))
            {
                states[kind] = await MediaAnalysisAccess.ToStateAsync(_db, current, now, ct);
                continue;
            }

            MediaAnalysisRequest row = MediaAnalysisAccess.NewRequest(
                requestId, projectId, organizationId, mediaRef, kind, lang,
                _currentUser.UserId == Guid.Empty ? null : _currentUser.UserId, now);
            fresh.Add(row);
            states[kind] = new MediaAnalysisKindState(MediaAnalysisStates.Queued, null, requestId, now, null);
        }

        ChargeHold? hold = null;
        double? heldSeconds = null;
        MediaAnalysisRequest? transcriptRow = fresh.FirstOrDefault(r => r.Kind == MediaAnalysisKinds.Transcript);
        if (transcriptRow is not null && BillingOn)
        {
            TranscriptPrice? price;
            try
            {
                price = await PriceTranscriptAsync(projectId, source, organizationId, ct);
            }
            catch (ModelUnpricedException unpriced)
            {
                return ModelUnpricedResults.Conflict(unpriced);
            }

            if (price is null)
                return Conflict(new { code = "TRANSCRIPT_UNAVAILABLE", message = TranscriptUnavailable(source, null) });
            if (price.RequiredMillis > 0 && price.BalanceMillis < price.RequiredMillis)
            {
                return StatusCode(StatusCodes.Status402PaymentRequired, new
                {
                    code = "INSUFFICIENT_CREDITS",
                    message = CreditShortfallMessage.Build(price.RequiredMillis, null, price.BalanceMillis),
                    missing = CreditShortfallMessage.MissingCredits(price.RequiredMillis, price.BalanceMillis)
                });
            }

            if (price.RequiredMillis > 0)
            {
                hold = await _charges!.BeginHoldAsync(
                    organizationId, UsageSource.Editor, price.RequiredMillis, transcriptRow.Id,
                    new CreditLedgerContext(projectId, Note: "transcript"), ct);
                if (!hold.Allowed)
                {
                    return StatusCode(StatusCodes.Status402PaymentRequired, new
                    {
                        code = "INSUFFICIENT_CREDITS",
                        message = CreditShortfallMessage.Build(price.RequiredMillis, null, hold.BalanceMillis),
                        missing = CreditShortfallMessage.MissingCredits(price.RequiredMillis, hold.BalanceMillis)
                    });
                }

                transcriptRow.HoldReservationId = hold.ReservationId;
                transcriptRow.HoldMillis = price.RequiredMillis;
                heldSeconds = price.Seconds;
            }
        }

        if (fresh.Count > 0)
        {
            // In-flight guard: a concurrent POST for the same project/file/kind/language may have queued since the first look.
            List<MediaAnalysisRequest> raced = await MediaAnalysisAccess.LoadRowsAsync(_db, projectId, mediaRef, ct);
            if (transcriptRow is not null && raced.FirstOrDefault(r => r.Kind == MediaAnalysisKinds.Transcript && r.Language == transcriptRow.Language
                    && r.State is MediaAnalysisStates.Queued or MediaAnalysisStates.Running && now - r.UpdatedAt < StaleAfter) is { } inFlight)
            {
                if (hold is not null) await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, null, _logger);
                states[MediaAnalysisKinds.Transcript] = await MediaAnalysisAccess.ToStateAsync(_db, inFlight, now, ct);
                fresh.Remove(transcriptRow);
                hold = null;
                heldSeconds = null;
            }
        }

        if (fresh.Count > 0 && !await MediaAnalysisAccess.QueueAsync(
                _db, _publish, projectId, organizationId, source.FileId, source.StorageKey,
                language.Length == 0 ? null : language, fresh,
                _currentUser.UserId == Guid.Empty ? null : _currentUser.UserId, heldSeconds, states, _logger, ct))
        {
            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, null, _logger);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = MediaAnalysisAccess.QueueFailedMessage });
        }

        return Accepted(new MediaAnalysisStartedResponse(
            requestId, mediaRef, kinds.ToDictionary(k => k, k => states[k])));
    }

    /// <summary>What a transcript of this file would cost, before it is asked for. Nothing is persisted.</summary>
    [HttpPost(Ref + "/analysis/estimate")]
    public async Task<IActionResult> Estimate(Guid projectId, string mediaRef, [FromBody] MediaAnalysisRequestBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (!TryReadBody(body, out IReadOnlyList<string> kinds, out string language, out IActionResult? bad)) return bad!;

        if (await MediaAnalysisAccess.IsHeldAsync(_db, projectId, mediaRef, ct)) return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();
        MediaAnalysisAccess.SourceLookup lookup = await MediaAnalysisAccess.FindSourceAsync(_db, projectId, mediaRef, ct);
        if (lookup.Source is not { } source) return NotFound();
        if (!source.Analysable) return BadRequest(new { message = MediaAnalysisAccess.NotAnalysableMessage });

        if (!BillingOn) return Ok(new MediaAnalysisEstimateResponse(false, null, null, null, true, false, null));
        if (!kinds.Contains(MediaAnalysisKinds.Transcript))
            return Ok(new MediaAnalysisEstimateResponse(true, 0, 0, null, true, false, null));
        if (access.Project?.OrganizationId is not { } organizationId) return NotFound();

        string lang = LanguageFor(MediaAnalysisKinds.Transcript, language);
        MediaAnalysisRequest? ready = (await MediaAnalysisAccess.LoadRowsAsync(_db, projectId, mediaRef, ct))
            .Where(r => r.Kind == MediaAnalysisKinds.Transcript && r.Language == lang && r.State == MediaAnalysisStates.Ready)
            .OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
        if (ready is not null && await MediaAnalysisAccess.ResultExistsAsync(_db, ready, organizationId, ct))
            return Ok(new MediaAnalysisEstimateResponse(true, 0, 0, null, true, true, null));

        TranscriptPrice? price;
        try
        {
            price = await PriceTranscriptAsync(projectId, source, organizationId, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }

        if (price is null)
            return Ok(new MediaAnalysisEstimateResponse(true, null, null, null, false, false, TranscriptUnavailable(source, null)));

        return Ok(new MediaAnalysisEstimateResponse(
            true, price.RequiredMillis / 1000.0, Math.Round(price.Seconds, 1), price.BalanceMillis / 1000.0,
            price.BalanceMillis >= price.RequiredMillis, false, null));
    }

    /// <summary>Per-kind state and a small summary for every kind; never the data.</summary>
    [HttpGet(Ref + "/analysis")]
    public async Task<IActionResult> GetStates(Guid projectId, string mediaRef, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (await MediaAnalysisAccess.IsHeldAsync(_db, projectId, mediaRef, ct)) return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();
        if ((await MediaAnalysisAccess.FindSourceAsync(_db, projectId, mediaRef, ct)).Source is null) return NotFound();

        DateTime now = DateTime.UtcNow;
        List<MediaAnalysisRequest> rows = await MediaAnalysisAccess.LoadRowsAsync(_db, projectId, mediaRef, ct);
        Dictionary<string, MediaAnalysisKindState> kinds = new();
        foreach (string kind in MediaAnalysisKinds.All)
        {
            MediaAnalysisRequest? row = rows.Where(r => r.Kind == kind).OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            kinds[kind] = row is null
                ? new MediaAnalysisKindState("none", null, null, null, null)
                : await MediaAnalysisAccess.ToStateAsync(_db, row, now, ct);
        }

        return Ok(new MediaAnalysisStatesResponse(mediaRef, kinds));
    }

    /// <summary>One kind's data, for the editor's panels. 409 while it is not ready.</summary>
    [HttpGet(Ref + "/analysis/{kind}")]
    public async Task<IActionResult> GetData(Guid projectId, string mediaRef, string kind, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (!MediaAnalysisKinds.All.Contains(kind)) return NotFound();
        if (await MediaAnalysisAccess.IsHeldAsync(_db, projectId, mediaRef, ct)) return ReelBolt.Inference.Api.Services.Outputs.OutputGate.Unavailable();
        if ((await MediaAnalysisAccess.FindSourceAsync(_db, projectId, mediaRef, ct)).Source is null) return NotFound();

        DateTime now = DateTime.UtcNow;
        MediaAnalysisRequest? row = (await MediaAnalysisAccess.LoadRowsAsync(_db, projectId, mediaRef, ct))
            .Where(r => r.Kind == kind).OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
        MediaAnalysisKindState state = row is null ? new("none", null, null, null, null) : await MediaAnalysisAccess.ToStateAsync(_db, row, now, ct);
        if (state.State != MediaAnalysisStates.Ready || row?.ResultId is null)
            return Conflict(new { state = state.State, reason = state.Reason });

        string? json = await MediaAnalysisAccess.ReadResultJsonAsync(_db, row, ct);
        return json is null ? Conflict(new { state = "none", reason = (string?)null }) : Content(json, "application/json");
    }

    // ---------------------------------------------------------------------------------------------

    private static bool TryReadBody(
        MediaAnalysisRequestBody? body, out IReadOnlyList<string> kinds, out string language, out IActionResult? bad)
    {
        kinds = [];
        language = string.Empty;
        bad = null;
        if (body?.Kinds is not { Count: > 0 })
        {
            bad = new BadRequestObjectResult(new { message = "Name at least one kind: silence, shots, motion, events, transcript or beats." });
            return false;
        }

        List<string> parsed = [];
        foreach (string raw in body.Kinds)
        {
            string kind = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (!MediaAnalysisKinds.All.Contains(kind))
            {
                bad = new BadRequestObjectResult(new { message = $"'{raw}' is not an analysis kind. Use silence, shots, motion, events, transcript or beats." });
                return false;
            }

            if (!parsed.Contains(kind)) parsed.Add(kind);
        }

        string lang = (body.Language ?? string.Empty).Trim().ToLowerInvariant();
        if (lang.Length > 16 || lang.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            bad = new BadRequestObjectResult(new { message = "The language must be a short code such as 'en'." });
            return false;
        }

        kinds = parsed;
        language = lang;
        return true;
    }

    private static string LanguageFor(string kind, string language) => kind == MediaAnalysisKinds.Transcript ? language : string.Empty;

    private static string TranscriptUnavailable(MediaAnalysisAccess.Source source, string? _) =>
        source.DurationSec is null
            ? "We haven't measured this file's length yet, so we can't say what a transcript costs. Open it in the editor once and try again."
            : "Transcription isn't set up for this workspace yet.";

    /// <summary>The transcript's price: the file's length through the catalogue's speech-to-text price, and the lock-free balance.</summary>
    private Task<TranscriptPrice?> PriceTranscriptAsync(Guid projectId, MediaAnalysisAccess.Source source, Guid organizationId, CancellationToken ct) =>
        TranscriptPriceEstimator.PriceAsync(_providers, _charges!, source.DurationSec, organizationId, ct);
}
