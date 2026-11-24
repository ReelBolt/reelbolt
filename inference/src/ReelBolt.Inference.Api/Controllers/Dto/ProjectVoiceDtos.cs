namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// Registers a provider-library voice (no person in this project is its subject, so no consent
/// is captured) — see docs/voiceover.md "Voices and consent".
/// </summary>
public sealed record CreateLibraryVoiceRequest(
    string Name,
    string RemoteVoiceId,
    Guid? InferenceProviderId = null,
    string? Description = null);

/// <summary>
/// Clones a voice from this project's own recordings. The consent fields are REQUIRED and are
/// recorded verbatim with the attesting user's id and the time: <paramref name="ConsentStatement"/>
/// must equal <c>ProjectVoicesController.RequiredConsentStatement</c> exactly, so the API never
/// accepts a weaker wording, and <paramref name="ConsentSubjectName"/> names the recorded person.
/// <paramref name="RetentionDays"/> bounds how long the clone (and its vendor model) may exist.
/// </summary>
public sealed record CloneVoiceRequest(
    string Name,
    IReadOnlyList<Guid> ReferenceProjectFileIds,
    string ConsentStatement,
    string ConsentSubjectName,
    Guid? InferenceProviderId = null,
    string? Description = null,
    int RetentionDays = ProjectVoiceLimits.DefaultRetentionDays);

public static class ProjectVoiceLimits
{
    public const int DefaultRetentionDays = 365;
    public const int MaxRetentionDays = 730;
    public const int MaxReferenceFiles = 20;
    /// <summary>Total reference audio sent to the vendor per clone — 50 MiB, well under any vendor cap and enough for several minutes of WAV.</summary>
    public const long MaxReferenceBytes = 50L * 1024 * 1024;
}

public sealed record ProjectVoiceResponse(
    Guid Id,
    Guid ProjectId,
    Guid? InferenceProviderId,
    string? InferenceProviderName,
    string Name,
    string? Description,
    string Source,
    string RemoteVoiceId,
    IReadOnlyList<Guid> ReferenceProjectFileIds,
    bool Selectable,
    string? UnselectableReason,
    Guid? ConsentAttestedByUserId,
    DateTime? ConsentAttestedAt,
    string? ConsentStatement,
    string? ConsentSubjectName,
    DateTime? ConsentRevokedAt,
    DateTime? RetentionUntil,
    DateTime CreatedAt);

/// <summary>What deletion actually removed — reported, never assumed.</summary>
public sealed record DeleteVoiceResponse(
    Guid Id,
    bool RowDeleted,
    bool RemoteModelDeleted,
    string? RemoteDeletionNote,
    int ReferenceFilesDeleted);

/// <summary>One voice from the speech provider's public library (docs/voiceover.md "Browsing the Fish voice library").</summary>
public sealed record VoiceLibraryItemResponse(
    string VoiceId,
    string Name,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Tags,
    bool PreviewAvailable);

/// <summary>A page of library voices. <c>Total</c> is the vendor's total match count for the search.</summary>
public sealed record VoiceLibraryResponse(
    IReadOnlyList<VoiceLibraryItemResponse> Items,
    int Total,
    int Page,
    int PageSize);
