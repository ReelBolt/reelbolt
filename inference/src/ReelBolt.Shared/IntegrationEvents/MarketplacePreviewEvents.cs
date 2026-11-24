namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// Published by the Inference API once a marketplace version row is committed: asks the engine to cut the version's preview
/// (first 30 s, at most 720p) and poster from one video. The source key is resolved by the server from the author's own
/// project, never taken from a request; the event stays on the internal broker.
/// </summary>
public record MarketplacePreviewRequested(
    Guid VersionId,
    Guid RecipeId,
    string SourceStorageKey,
    Guid OrganizationId,
    DateTime RequestedAt,
    string RunNonce);

/// <summary>
/// Published by the engine when a preview job is over. Carries only the platform keys and what the API needs to verify the
/// bytes (SHA-256, size), and a stable failure code on failure; never a path, a tool message or a project identifier.
/// </summary>
public record MarketplacePreviewFinished(
    Guid VersionId,
    Guid RecipeId,
    bool Succeeded,
    string? FailureCode = null,
    string? PreviewKey = null,
    string? PosterKey = null,
    string? PreviewSha256 = null,
    double? Seconds = null,
    long? SizeBytes = null,
    string? RunNonce = null);

/// <summary>Failure codes of <see cref="MarketplacePreviewFinished"/>.</summary>
public static class MarketplacePreviewFailureCodes
{
    public const string Disabled = "disabled";
    public const string SourceInvalid = "source_invalid";
    public const string SourceMissing = "source_missing";
    public const string SourceTooLarge = "source_too_large";
    public const string SourceTooLong = "source_too_long";
    public const string NotVideo = "not_video";
    public const string EncodeFailed = "encode_failed";
    public const string UploadFailed = "upload_failed";
    public const string Superseded = "superseded";
    public const string Internal = "internal";
}
