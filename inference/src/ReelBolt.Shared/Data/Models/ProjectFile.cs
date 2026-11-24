namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A file uploaded to a project, stored in MinIO/S3.
/// </summary>
public class ProjectFile
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    // Path-aware metadata for user/agent uploads. When users select a folder, the
    // browser will send the path in the form field and we save it here so the UI can
    // reconstruct the original directory structure. The simple file name is kept in
    // OriginalFileName (basename).
    public string? OriginalPath { get; set; }
    public string? DirectoryPath { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;

    // Category determines where in S3 the object lives (userFiles/outputFiles/agentFiles).
    // It defaults to "userFiles" for files uploaded via the API, and agents write to
    // "agentFiles" by default; render outputs go to "outputFiles".
    public string Category { get; set; } = "userFiles";

    public string StorageKey { get; set; } = string.Empty;
    public string? StorageFileName { get; set; }
    public string StorageBucket { get; set; } = string.Empty;
    public string? StoragePrefix { get; set; }
    public string? StorageMetadataJson { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the stored bytes, written by <c>ProjectFileHashSweeper</c> (or by a
    /// writer that already knows it). Null until the bytes have been hashed. A project-history item
    /// uses it as its content identity only together with <see cref="ContentSha256ETag"/>.
    /// </summary>
    public string? ContentSha256 { get; set; }

    /// <summary>
    /// The store's ETag at the moment <see cref="ContentSha256"/> was computed. Both columns are
    /// nulled when the object is overwritten or moved, so a hash never vouches for different bytes.
    /// </summary>
    public string? ContentSha256ETag { get; set; }
    /// <summary>
    /// What wrote this row. Null for the two original writers (a user upload and an agent), which is
    /// also what every pre-existing row holds; <c>github</c> for a file materialized by a repository
    /// pull. A repository pull reconciles <b>only</b> its own rows, so a push can never delete or
    /// overwrite a file someone uploaded — see <c>docs/github-app.md</c>.
    /// </summary>
    public string? SourceKind { get; set; }

    /// <summary>
    /// Provider-side revision of a <see cref="SourceKind"/>-sourced row — for <c>github</c>, the git
    /// blob SHA the stored bytes were decoded from. Reconciliation compares it so a repeated pull of an
    /// unchanged file is a no-op rather than a re-upload.
    /// </summary>
    public string? SourceRevision { get; set; }

    /// <summary>
    /// For a file a computer-folder sync wrote (<c>SourceKind = "local_folder"</c>): the
    /// <c>project_local_folder_links</c> row that owns it. Null for every other file, and set back to
    /// null when the link is removed (the file stays, as an ordinary file). See
    /// docs/design/local-folder-sync-and-storage.md.
    /// </summary>
    public Guid? SourceLinkId { get; set; }

    public string? AgentSummary { get; set; }
    public SummaryStatus SummaryStatus { get; set; } = SummaryStatus.Pending;
    public FileIndexingStatus IndexingStatus { get; set; } = FileIndexingStatus.NotIndexed;
    public DateTime? IndexedAt { get; set; }
    public string? IndexingError { get; set; }

    /// <summary>
    /// Ingest moderation state. A blocked file is moved under the project's <c>quarantine/</c> prefix
    /// and is refused by every read path; <see cref="FileModerationStatus.Error"/> keeps the file usable.
    /// </summary>
    public FileModerationStatus ModerationStatus { get; set; } = FileModerationStatus.Unmoderated;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// For a row made from an engine output (a registered render, a generated take, an assistant save's copy): when that
    /// output was produced. Written once when the row is created and never by a move, rename or any other update, so it
    /// stays true when <see cref="UploadedAt"/>, <see cref="Category"/> and the storage key are rewritten. Null for every
    /// other row (an upload, an agent file). The engine hides a row whose value is at or before the project's quarantine
    /// marker plus margin. API-owned column.
    /// </summary>
    public DateTime? OutputProducedAt { get; set; }

    // --- CSAM hash matching (Project Arachnid Shield) -------------------------------------------
    // New rows are Pending so the post-add path scans them; the database default is NotScanned so
    // rows written before this control existed are never mistaken for unscanned work.

    /// <summary>Scan verdict; a quarantined row's bytes are under <c>projects/{projectId}/quarantine/</c>.</summary>
    public CsamScanStatus CsamStatus { get; set; } = CsamScanStatus.Pending;

    /// <summary>When the last scan attempt produced an outcome (including a fail-open one).</summary>
    public DateTime? CsamScannedAt { get; set; }

    /// <summary>
    /// When a sweeper claimed the row (<see cref="CsamScanStatus.Scanning"/>). A claim older than the
    /// stale window is reclaimable, so a crashed worker cannot strand a row.
    /// </summary>
    public DateTime? CsamScanClaimedAt { get; set; }

    /// <summary>Provider classification of the last scan (<c>csam</c>, <c>harmful-abusive-material</c>, …).</summary>
    public string? CsamClassification { get; set; }

    /// <summary>Provider match type of the last scan (<c>exact</c> or <c>near</c>).</summary>
    public string? CsamMatchType { get; set; }

    /// <summary>The quarantine storage key the bytes were moved to, when a match quarantined them.</summary>
    public string? CsamQuarantineKey { get; set; }

    /// <summary>When the bytes were quarantined.</summary>
    public DateTime? CsamQuarantinedAt { get; set; }

    public Project Project { get; set; } = null!;
}
