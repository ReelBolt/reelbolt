namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The version of one legal document this deployment currently serves (F-7). Table
/// <c>legal_documents</c>, owned (migrated) by the Inference API; the Go API reads it by SQL and
/// never migrates it, the same arrangement <c>organizations</c> and <c>platform_settings</c> use.
///
/// <para>
/// <b>Why this table exists.</b> Before it, the accepted version of a legal document was whatever
/// string the caller put in the request body — signup and <c>/auth/accept-terms</c> only checked that
/// it was non-empty and at most 32 characters — so a row in <c>legal_acceptances</c> was not evidence
/// of anything: a caller could record a version that was never served, or a future one. The version a
/// deployment publishes is now a fact the server owns, and an acceptance is pinned to
/// <see cref="Version"/> rather than to the caller's assertion.
/// </para>
///
/// <para>
/// <b>One row per document, never deleted.</b> <see cref="EffectiveAt"/> and <see cref="UpdatedAt"/>
/// are what make the record auditable: for any <c>legal_acceptances</c> row, the version it names can
/// be dated against the interval this table says it was current, and <see cref="UpdatedBy"/> names
/// who published it. A version bump is an UPDATE of the one row, so the previous version is the
/// <c>legal_acceptances</c> rows that still name it — the acceptances are the history, this table is
/// only the current state.
/// </para>
///
/// <para>
/// A missing row is NOT a default. Every reader treats "no row for this document" as "the server
/// cannot state what it is serving", and the Go API refuses to record an acceptance rather than
/// inventing a version — the same fail-closed direction F11's switch store takes.
/// </para>
/// </summary>
public class LegalDocumentVersion
{
    /// <summary>Which document this row is about. The primary key; stored by name.</summary>
    public LegalDocument Document { get; set; }

    /// <summary>Current version label of the document (e.g. an ISO date). At most 50 characters.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// When <see cref="Version"/> became the current one. On a publication this is the moment the
    /// operator published it, which is the instant an acceptance of the PREVIOUS version stopped
    /// being current — the lower bound of its validity interval.
    /// </summary>
    public DateTime EffectiveAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this row was last written (the seed, or a publication).</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who last wrote it: <c>config</c> for the first-boot seed, otherwise the platform admin's
    /// email. Never a secret.
    /// </summary>
    public string? UpdatedBy { get; set; }
}
