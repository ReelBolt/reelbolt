using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// The one policy for CSAM hash matching at ingest. Fail OPEN is the product decision: no response,
/// timeout, 5xx, malformed body, transport error or missing configuration marks the file
/// <see cref="CsamScanStatus.NotScanned"/>, writes one <c>not-scanned</c> audit row and lets the
/// upload/run proceed. A match quarantines; it never fails the upload.
/// </summary>
public interface ICsmaHashMatcher
{
    Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct);
}

/// <summary>The verdict the matcher persisted, for a caller that wants to answer without re-reading the row.</summary>
public sealed record CsamScanOutcome(
    CsamScanStatus Status,
    string? Classification,
    string? MatchType,
    bool Quarantined,
    string? ErrorCode);
