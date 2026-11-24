using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.Marketing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Controllers.Admin;

public sealed record CampaignRequest(string? Subject, string? Html);

public sealed record CampaignSendRequest(int ConfirmRecipientCount);

/// <summary>The outcome is read as an object so a non-string value (a number, an object) reaches the service and answers <c>outcome_invalid</c>, not a framework 400 without a code.</summary>
public sealed record CampaignResolveRequest(object? Outcome);

/// <summary>
/// Marketing campaigns, platform admin only (re-checked first in every action). 404 while
/// <c>Marketing:Campaigns:Enabled</c> is false. A send is only ever queued here; the worker runs it.
/// </summary>
[ApiController]
[Route("api/v1/marketing/admin/campaigns")]
[Authorize]
public class MarketingCampaignAdminController(MarketingCampaignService campaigns, ICurrentUser currentUser) : ControllerBase
{
    [HttpGet("")]
    public async Task<IActionResult> List([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        IReadOnlyList<MarketingCampaign> rows = await campaigns.ListAsync(limit, ct);
        return Ok(rows.Select(c => View(c, includeHtml: false)));
    }

    [HttpGet("settings")]
    public IActionResult Settings()
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        CampaignSettings s = campaigns.Settings();
        return Ok(new { footerOk = s.FooterOk, listSendEnabled = s.ListSendEnabled, testRecipientMasked = s.TestRecipientMasked, brevoConfigured = s.BrevoConfigured });
    }

    [HttpPost("")]
    public async Task<IActionResult> Create([FromBody] CampaignRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign row = await campaigns.CreateAsync(new CampaignContent(request.Subject, request.Html), currentUser.UserId, ct);
            return Ok(View(row, includeHtml: true));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        MarketingCampaign? row = await campaigns.GetAsync(id, ct);
        return row is null ? NotFound() : Ok(View(row, includeHtml: true));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CampaignRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign? row = await campaigns.UpdateAsync(id, new CampaignContent(request.Subject, request.Html), ct);
            return row is null ? NotFound() : Ok(View(row, includeHtml: true));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            return await campaigns.DeleteAsync(id, ct) ? NoContent() : NotFound();
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    [HttpPost("{id:guid}/dry-run")]
    public async Task<IActionResult> DryRun(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        CampaignDryRunResult? r = await campaigns.DryRunAsync(id, currentUser.UserId, ct);
        return r is null
            ? NotFound()
            : Ok(new { recipientCount = r.RecipientCount, suppressedCount = r.SuppressedCount, sampleMasked = r.SampleMasked, footerOk = r.FooterOk, errors = r.Errors, listSendEnabled = r.ListSendEnabled });
    }

    [HttpPost("{id:guid}/test-send")]
    public async Task<IActionResult> TestSend(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            return await campaigns.TestSendAsync(id, currentUser.UserId, ct) ? Ok(new { sent = true }) : NotFound();
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
        catch (BrevoCampaignException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "send_failed", code = e.Code });
        }
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, [FromBody] CampaignSendRequest request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign? row = await campaigns.RequestSendAsync(id, request.ConfirmRecipientCount, currentUser.UserId, ct);
            return row is null ? NotFound() : Accepted(View(row, includeHtml: false));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal, atSend: true);
        }
    }

    [HttpPost("{id:guid}/refresh-results")]
    public async Task<IActionResult> RefreshResults(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign? row = await campaigns.RefreshResultsAsync(id, ct);
            return row is null ? NotFound() : Ok(View(row, includeHtml: false));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
        catch (BrevoCampaignException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "refresh_failed", code = e.Code });
        }
    }

    /// <summary>The human way out of SendUnknown, after checking Brevo by hand. Calls Brevo nothing.</summary>
    [HttpPost("{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] CampaignResolveRequest? request, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign? row = await campaigns.ResolveAsync(id, OutcomeText(request?.Outcome), currentUser.UserId, ct);
            return row is null ? NotFound() : Ok(View(row, includeHtml: false));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    /// <summary>Reopens a Draft, Failed or Sent campaign as a new Draft with the same subject and HTML and no test record.</summary>
    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Forbid();
        if (!campaigns.Enabled) return NotFound();
        try
        {
            MarketingCampaign? row = await campaigns.DuplicateAsync(id, currentUser.UserId, ct);
            return row is null ? NotFound() : Ok(View(row, includeHtml: true));
        }
        catch (CampaignRefusal refusal)
        {
            return Refusal(refusal);
        }
    }

    private static string? OutcomeText(object? outcome) => outcome switch
    {
        string s => s,
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } e => e.GetString(),
        _ => null,
    };

    private ObjectResult Refusal(CampaignRefusal r, bool atSend = false) => StatusCode(r.Code switch
    {
        "list_send_disabled" => StatusCodes.Status403Forbidden,
        "not_draft" or "recipient_count_changed" or "campaign_active" or "unresolved_send" or "not_send_unknown" => StatusCodes.Status409Conflict,
        "rate_limited" => StatusCodes.Status429TooManyRequests,
        "subject_missing" or "subject_too_long" or "subject_invalid_characters" or "html_missing" or "html_too_large" or "html_too_deep" or "html_no_body" or "outcome_invalid" when !atSend => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status422UnprocessableEntity,
    }, new { error = r.Code });

    private object View(MarketingCampaign c, bool includeHtml)
    {
        // previewHtml is the exact HTML that would go to Brevo (footer included); null with previewError when it cannot render.
        string? previewHtml = null;
        string? previewError = null;
        if (includeHtml)
        {
            previewHtml = campaigns.TryBuildHtml(MarketingCampaignService.ContentOf(c), out previewError);
        }

        return new
        {
            previewHtml,
            previewError,
            id = c.Id,
            subject = c.Subject,
            html = includeHtml ? c.Html : null,
            status = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(c.Status.ToString()),
            createdBy = c.CreatedBy,
            createdAt = c.CreatedAt,
            updatedAt = c.UpdatedAt,
            testSentAt = c.TestSentAt,
            testCurrent = c.TestSentAt != null && c.TestContentHash == MarketingCampaignService.ContentHash(MarketingCampaignService.ContentOf(c)),
            confirmedRecipientCount = c.ConfirmedRecipientCount,
            finalRecipientCount = c.FinalRecipientCount,
            dispatchStartedAt = c.DispatchStartedAt,
            failureCode = c.FailureCode,
            stats = MarketingCampaignService.ParseStats(c.StatsJson),
            statsAt = c.StatsAt,
        };
    }
}
