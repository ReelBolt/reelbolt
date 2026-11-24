using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// One CSAM scan attempt (Project Arachnid Shield), written for every outcome — clean, match and
/// fail-open. It deliberately carries no content: no bytes, no base64 media, no presigned URL and no
/// token. Only classification, hashes, provider version and error codes.
/// <para>
/// The table is API-owned and reached only through a project file (an admin-only query or a migration
/// file), so it carries no tenant query filter: the parent file is the tenant boundary, exactly like
/// the other child tables the tenancy inventory pins as unfiltered.
/// </para>
/// </summary>
public class CsamScanAudit
{
    public Guid Id { get; set; }

    /// <summary>The project at scan time. No FK: the evidence outlives a deleted project.</summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// The scanned file at scan time. Nullable with <c>ON DELETE SET NULL</c> so deleting the file
    /// (or its project) never cascades the evidence away.
    /// </summary>
    public Guid? ProjectFileId { get; set; }

    /// <summary>The file's display name at scan time, so the admin list still names it after deletion.</summary>
    public string? ProjectFileName { get; set; }

    /// <summary><c>clean</c>, <c>match</c>, <c>not-scanned</c> or <c>disabled</c> (the feature flag was off).</summary>
    public string Status { get; set; } = "not-scanned";

    public string? Classification { get; set; }
    public string? MatchType { get; set; }
    public int? NearMatchCount { get; set; }

    /// <summary>The base64 PDQ hash sent to the provider (32 bytes), when one was computed locally.</summary>
    public string? PdqHashBase64 { get; set; }

    /// <summary>The local PDQ quality metric.</summary>
    public int? PdqQuality { get; set; }

    /// <summary>SHA-256 of the scanned bytes, hex. Used for provider-side match evidence.</summary>
    public string? ContentSha256Hex { get; set; }

    /// <summary><c>pdq</c>, <c>media</c> or <c>none</c> (nothing left the host).</summary>
    public string? Source { get; set; }

    /// <summary>The provider's database/list version, when it answered one.</summary>
    public string? ProviderVersion { get; set; }

    public bool Quarantined { get; set; }
    public bool QuarantineSucceeded { get; set; }

    /// <summary><c>TIMEOUT</c>, <c>HTTP_&lt;status&gt;</c>, <c>TRANSPORT</c>, <c>INVALID_RESPONSE</c>, <c>CONFIG_MISSING</c>, …</summary>
    public string? ErrorCode { get; set; }

    public Guid? ActorUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ProjectFile? ProjectFile { get; set; }
}
