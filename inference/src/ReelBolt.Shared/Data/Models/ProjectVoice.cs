namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Where a project voice came from. <b>APPEND ONLY</b> — persisted by name.
/// </summary>
public enum ProjectVoiceSource
{
    /// <summary>A voice CLONED from a real person's recording (biometric data — consent, retention and deletion apply).</summary>
    Cloned,

    /// <summary>A provider-library voice referenced by its vendor id; no person in this project is its subject.</summary>
    Library
}

/// <summary>
/// A voice a project can narrate in (see docs/voiceover.md "Voices and consent"). Owned by the
/// Inference API (<c>project_voices</c>, <c>__EFMigrationsHistory_Api</c>); the WorkflowEngine maps
/// the table read-only so the Voiceover step can resolve a <c>ProjectVoiceId</c> through the same
/// consent guard the API applies.
///
/// <para>
/// <b>A cloned voice is biometric data.</b> The row therefore carries, as first-class columns rather
/// than free text: who attested the subject's consent and when (<see cref="ConsentAttestedByUserId"/>,
/// <see cref="ConsentAttestedAt"/>), the statement they attested to and the subject's name as they
/// gave it, when consent was revoked, and the retention deadline after which the voice (and its
/// vendor-side model) is deleted whether or not anyone asks. <see cref="IsSelectable"/> is the ONE
/// read-path rule every consumer applies: a cloned voice is usable only while consent is attested,
/// not revoked, and not past retention. A Library voice needs no consent and never expires.
/// </para>
/// <para>
/// <b>Project-scoped by construction.</b> The row belongs to exactly one project (cascade-deleted
/// with it) and a voice is never offered to, or resolvable from, another project.
/// </para>
/// </summary>
public class ProjectVoice
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// The SpeechSynthesis MODEL CAPABILITY the vendor voice id belongs to (name kept from when it named a
    /// provider row; null once that capability is deleted, and the voice is then unusable).
    /// </summary>
    public Guid? InferenceProviderId { get; set; }

    /// <summary>Display name in the project's voice library.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProjectVoiceSource Source { get; set; } = ProjectVoiceSource.Library;

    /// <summary>The vendor's own id for the voice — Fish Audio's model <c>_id</c>, an OpenAI-compatible server's voice name.</summary>
    public string RemoteVoiceId { get; set; } = string.Empty;

    /// <summary>JSON array of the <c>project_files</c> ids the clone was trained from — the reference recordings, which deletion removes.</summary>
    public string? ReferenceProjectFileIdsJson { get; set; }

    /// <summary>The user who attested that the recorded person consented to this clone.</summary>
    public Guid? ConsentAttestedByUserId { get; set; }

    public DateTime? ConsentAttestedAt { get; set; }

    /// <summary>The consent statement the attesting user agreed to, verbatim — what they are accountable for.</summary>
    public string? ConsentStatement { get; set; }

    /// <summary>The name of the person whose voice was recorded, as the attesting user gave it.</summary>
    public string? ConsentSubjectName { get; set; }

    public DateTime? ConsentRevokedAt { get; set; }

    public Guid? ConsentRevokedByUserId { get; set; }

    /// <summary>When the voice and its vendor model are deleted regardless of use. Null only for a Library voice.</summary>
    public DateTime? RetentionUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Guid CreatedByUserId { get; set; }

    /// <summary>Whether the voice may be selected or spoken RIGHT NOW — the single read-path rule.</summary>
    public bool IsSelectable(DateTime utcNow) =>
        Source == ProjectVoiceSource.Library ||
        (ConsentAttestedAt is not null &&
         ConsentRevokedAt is null &&
         (RetentionUntil is null || RetentionUntil > utcNow));

    /// <summary>Why <see cref="IsSelectable"/> is false, as a machine-readable reason; null when selectable.</summary>
    public string? UnselectableReason(DateTime utcNow)
    {
        if (IsSelectable(utcNow))
            return null;
        if (ConsentAttestedAt is null)
            return "consent_missing";
        if (ConsentRevokedAt is not null)
            return "consent_revoked";
        return "retention_expired";
    }
}
