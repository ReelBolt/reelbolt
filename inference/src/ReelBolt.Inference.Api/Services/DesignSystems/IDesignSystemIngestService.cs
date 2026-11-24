using ReelBolt.Inference.Api.Controllers.Dto;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>What one ingest run did (this task's own shape).</summary>
public enum DesignSystemIngestStatus
{
    Ok,
    NotFound,
    InvalidArtifactKey,
    ArtifactTooLarge,
    ArtifactNotFound,
    InvalidDocument,
}

/// <summary>
/// The outcome of one ingest: the Draft version ids it created (or already held for the execution)
/// and their summaries, or a message for the refusal codes.
/// </summary>
public sealed record DesignSystemIngestResult(
    DesignSystemIngestStatus Status,
    IReadOnlyList<Guid>? VersionIds = null,
    IReadOnlyList<DesignVersionSummaryResponse>? Versions = null,
    string? Message = null);

/// <summary>
/// SPEC 9 <c>POST /ingest</c>: reads a Compose-step artifact from storage under a validated key
/// prefix, re-validates both documents before any write, creates <c>StaticExtract</c>/<c>LlmExtract</c>
/// /<c>Merge</c> Drafts plus their sources, and is idempotent per <c>workflow_execution_id</c>.
/// </summary>
public interface IDesignSystemIngestService
{
    Task<DesignSystemIngestResult> IngestAsync(
        Guid projectId, Guid userId, Guid executionId, int stepOrder, CancellationToken ct);
}
