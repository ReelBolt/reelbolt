using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Telemetry;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Anonymous sink for browser CSP and COEP violation reports (docs/csp-reporting.md). It ALWAYS answers
/// 204 with an empty body: malformed, oversized, disabled, rate-limited and failed reports included, so a
/// browser never retries and a probe learns nothing. It never echoes input, never logs a report, and keeps
/// only the allowlisted fields of <see cref="CspViolationKey"/> in a daily aggregate.
/// </summary>
[ApiController]
[Route("api/v1/telemetry")]
public class TelemetryCspReportController(
    IOptions<AlphaTelemetryOptions> options,
    CspReportRateLimiter limiter,
    ICspReportStore store,
    TimeProvider time) : ControllerBase
{
    /// <summary>Records the violation reports in the body, if any. Always 204.</summary>
    [HttpPost("csp-report")]
    [AllowAnonymous]
    public async Task<IActionResult> Report(CancellationToken ct)
    {
        AlphaTelemetryOptions o = options.Value;
        if (!o.CspReportEnabled)
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "disabled"));
            return NoContent();
        }

        if (!CspReportSanitizer.IsAcceptedContentType(Request.ContentType))
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "rejected_type"));
            return NoContent();
        }

        if (Request.ContentLength is > CspReportSanitizer.MaxBodyBytes)
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "malformed"));
            return NoContent();
        }

        // Read at most the cap (+1 byte to see an overrun); a longer body is dropped unread.
        byte[] buffer = new byte[CspReportSanitizer.MaxBodyBytes + 1];
        int read = 0;
        try
        {
            while (read < buffer.Length)
            {
                int n = await Request.Body.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "malformed"));
            return NoContent();
        }

        if (read > CspReportSanitizer.MaxBodyBytes)
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "malformed"));
            return NoContent();
        }

        IReadOnlyList<CspViolationKey> keys = CspReportSanitizer.Parse(buffer.AsSpan(0, read), Request.Headers.UserAgent.ToString());
        if (keys.Count == 0)
        {
            CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "malformed"));
            return NoContent();
        }

        DateOnly day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        foreach (CspViolationKey key in keys)
        {
            if (!limiter.TryAcquire())
            {
                CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "rate_limited"));
                continue;
            }

            try
            {
                await store.RecordAsync(key, day, o.CspReportMaxKeysPerDay, ct);
                CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "stored"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                CspReportDiagnostics.Reports.Add(1, new KeyValuePair<string, object?>("outcome", "error"));
            }
        }

        return NoContent();
    }
}
