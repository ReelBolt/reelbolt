namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// The one stored publish verdict of one project output, bound to the bytes that were judged. A row
/// has no expiry: an output is judged once, when it is created, and judged again only when its
/// version (ETag + size) changes. It carries no content and no content hash; the audit table keeps
/// those. Owned by the Inference API (<c>output_moderation_verdicts</c>); the engine does not map it.
/// </summary>
public class OutputModerationVerdict
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; set; }

    public string StorageKey { get; set; } = string.Empty;

    /// <summary>The object's identity at judgement time: ETag plus size (<c>len:{size}</c> when the store reports no ETag).</summary>
    public string SourceVersion { get; set; } = string.Empty;

    /// <summary>Allow, Review, Block or Unavailable, stored by name.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The policy reason code; null for an allow and for a plain outage.</summary>
    public string? ReasonCode { get; set; }

    /// <summary>The plain, score-free sentence shown when the output is refused.</summary>
    public string? ReasonMessage { get; set; }

    public string ModelSnapshot { get; set; } = string.Empty;

    /// <summary>The first step result that asked for the output. A plain value, not a foreign key.</summary>
    public Guid? StepResultId { get; set; }

    /// <summary>When the step that made the output completed; start of the verdict-latency measure.</summary>
    public DateTime? OutputProducedAt { get; set; }

    /// <summary>How many times the judgement ended in an outage for this version.</summary>
    public int Attempts { get; set; }

    /// <summary>When the sweep may retry an outage; null once it has stopped trying (or for a decided verdict).</summary>
    public DateTime? NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One request to judge an output. A record, so an identical offer is deduplicated while it is queued or running.</summary>
public sealed record OutputModerationTask(Guid ProjectId, string StorageKey);
