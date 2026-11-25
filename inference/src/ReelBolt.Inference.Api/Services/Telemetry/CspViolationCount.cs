namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// One aggregate counter of browser CSP/COEP violation reports (table <c>csp_violation_counts</c>,
/// docs/csp-reporting.md). The key is the whole row: every column is a closed token, a scheme+host, a
/// sanitized path or a UTC day, so a row identifies no person. There is deliberately no column for a full
/// URL, query, referrer, source file, script sample, line, policy, status code, IP, user or cookie.
/// Unfiltered by tenant: anonymous, platform-wide, no route addresses a row.
/// </summary>
public class CspViolationCount
{
    /// <summary>The UTC day the report arrived.</summary>
    public DateOnly Day { get; set; }

    /// <summary><c>csp</c> or <c>coep</c>.</summary>
    public string ReportType { get; set; } = string.Empty;

    /// <summary><c>report</c>, <c>enforce</c> or <c>unknown</c>.</summary>
    public string Disposition { get; set; } = string.Empty;

    /// <summary>A CSP directive name from the closed vocabulary, <c>coep</c>, <c>other</c> or <c>(overflow)</c>.</summary>
    public string EffectiveDirective { get; set; } = string.Empty;

    /// <summary>Scheme+host (and port) of the blocked resource, a keyword, <c>other</c> or <c>(overflow)</c>.</summary>
    public string BlockedOrigin { get; set; } = string.Empty;

    /// <summary>Sanitized document path, <c>(sensitive)</c>, <c>other</c> or <c>(overflow)</c>.</summary>
    public string DocumentPath { get; set; } = string.Empty;

    /// <summary><c>chrome</c>, <c>firefox</c>, <c>safari</c>, <c>edge</c> or <c>other</c>.</summary>
    public string UaFamily { get; set; } = string.Empty;

    /// <summary>Reports counted under this key.</summary>
    public long Count { get; set; }
}
