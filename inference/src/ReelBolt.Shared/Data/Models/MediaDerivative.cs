namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What a <see cref="MediaDerivative"/> was generated for.
/// Declared here rather than in <c>Enums.cs</c> because it belongs to this table alone.
///
/// <para>
/// <b><see cref="MediaDerivative.Kind"/> is persisted as a string</b> — both DbContexts map it with
/// <c>HasConversion&lt;string&gt;()</c> and the creating migration
/// (<c>AddEditTimelineTables</c>) declares the column as <c>text</c>. Appending a member here
/// therefore never shifts the meaning of a value already in the table (there is no ordinal to
/// shift) and never needs a schema migration. That is the whole reason this enum is safe to extend
/// in place; the same is deliberately NOT true of any enum stored as an integer.
/// </para>
/// </summary>
public enum MediaDerivativeKind
{
    /// <summary>A low-resolution editing proxy of the source media.</summary>
    Proxy,

    /// <summary>
    /// An extracted audio waveform <b>image</b>.
    ///
    /// <para>
    /// <b>Superseded by <see cref="WaveformPeaks"/></b>, and kept only because nothing ever wrote
    /// it: no writer, no migration and no fixture ever produced a row or an object of this kind, so
    /// there is no value in the table whose meaning this member holds up. Removing it would be a
    /// code-only change too — it is retained purely as a signpost, so a reader who finds the name
    /// in an older design note is told which kind replaced it instead of guessing. The peaks form
    /// exists because the editor draws its waveform at whatever zoom the user picks, which a
    /// bitmap baked at one level of detail cannot serve.
    /// </para>
    /// </summary>
    Waveform,

    /// <summary>
    /// The audio sample-peak series the editor draws a waveform from: one fixed 2-channel 48 kHz
    /// decode reduced to a <c>ch,min,max</c> triple per time bucket, at a chosen level of detail.
    /// </summary>
    WaveformPeaks,

    /// <summary>A representative thumbnail frame.</summary>
    Thumbnail,

    /// <summary>
    /// A filmstrip sprite sheet: a tiled grid of evenly spaced frames plus its JSON index, so the
    /// editor can scrub and show a visual timeline without decoding the source.
    /// </summary>
    Filmstrip,

    /// <summary>
    /// A keyframe index: where the source's keyframes are, so seek planning never has to decode
    /// forward from an unknown position.
    /// </summary>
    KeyframeIndex,

    /// <summary>
    /// Up to twelve full-size JPEG stills of a video (long side at most 1280 px): evenly spaced with the
    /// first and last frame, or at the times a request named. One row per set; the stills are indexed
    /// objects beside the first one, listed (index, time, leaf) in the row's JSON index, like filmstrip
    /// sheets. <b>On demand only</b> — never part of the default derivative set an upload asks for.
    /// </summary>
    Stills,

    /// <summary>
    /// A low-resolution STABILISED proxy of a video: the editing proxy's encode (H.264, same size and GOP,
    /// audio dropped) with the render's two-pass <c>vidstab</c> chain applied, so the viewer can play the
    /// stabilised picture. The whole source is stabilised (the render stabilises the clip's span, so the
    /// edges of a short clip may differ slightly: the viewer marks it approximate). <b>On demand only</b> —
    /// requested when a clip's stabilise turns on, never part of the default set. Cached by (source, recipe version).
    /// </summary>
    StabilizedProxy,
}

/// <summary>
/// Which kind of row a media reference points at — the closed discriminator shared by
/// <see cref="MediaDerivative.SourceKind"/> and <see cref="MediaBinItem.SourceKind"/>.
///
/// <para>
/// This is the single canonical declaration of the type; it is deliberately not duplicated
/// anywhere else (a second enum of the same name in another namespace would make every file that
/// imports both namespaces fail to compile on an ambiguous reference). The integration-event
/// contract and the media controllers reference this type rather than declaring their own.
/// </para>
/// </summary>
public enum MediaSourceKind
{
    /// <summary>
    /// The reference is a <c>ProjectFile.Id</c>: an uploaded file, or an output that was
    /// <b>registered</b> as a project file.
    /// </summary>
    ProjectFile,

    /// <summary>
    /// The reference is a <c>WorkflowStepResult.Id</c>: a render that exists only as a step result's
    /// <c>OutputStorageKey</c> and was never registered as a <c>ProjectFile</c> row.
    /// </summary>
    StepResult,
}

/// <summary>
/// A derived media artifact (proxy, filmstrip, waveform, thumbnail) generated from a project file or
/// a step-result render and stored in the object store (table <c>media_derivatives</c>,
/// Inference-API-owned). The binary lives in S3/MinIO; this row is the index that lets a client find
/// it, and its cache identity is <see cref="CacheKey"/> alone.
///
/// <para>
/// <b><see cref="CacheKey"/> replaces the earlier three-column identity
/// <c>(SourceStorageKey, SourceETag, ParamsHash)</c>.</b> That triple was not actually an identity:
/// the parameter hash covers a spec's knobs and its builder version, but neither the derivative
/// kind nor the variant, so two different kinds of one source that happened to share parameter JSON
/// produced the same triple — and the partial unique index over it then made the second derivative
/// unpersistable rather than merely redundant. The single hash below carries all six components, so
/// the index constrains exactly the thing that must be unique.
/// </para>
/// </summary>
public class MediaDerivative
{
    public Guid Id { get; set; }

    /// <summary>The owning project. Cascade-deleted with the project.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>
    /// The project file this derivative was generated from, when the source is one.
    ///
    /// <para>
    /// <b>Nullable, and that is the point.</b> A <b>registered</b> render output IS a
    /// <c>ProjectFile</c> row (it has an id, a storage key and a category), so it is referenced
    /// here like any uploaded file. An <b>unregistered</b> render is only a
    /// <c>WorkflowStepResult.OutputStorageKey</c> — there is no <c>ProjectFile</c> row to point at,
    /// so this column is null and <see cref="SourceKind"/>/<see cref="SourceStepResultId"/> carry
    /// the reference instead. A non-nullable FK here would make the render case unrepresentable.
    /// </para>
    /// </summary>
    public Guid? SourceProjectFileId { get; set; }

    /// <summary>
    /// Which of <see cref="SourceProjectFileId"/>/<see cref="SourceStepResultId"/> is the source.
    /// Needed because the two source shapes are different tables: without it a consumer would have
    /// to guess from which id happens to be non-null, and a future source kind would be
    /// indistinguishable from a bug. Stored as a string, like <see cref="Kind"/>.
    /// </summary>
    public MediaSourceKind SourceKind { get; set; }

    /// <summary>
    /// The <c>WorkflowStepResult.Id</c> of an unregistered render, when
    /// <see cref="SourceKind"/> is <see cref="MediaSourceKind.StepResult"/>.
    ///
    /// <para>
    /// <b>Deliberately NOT a foreign key</b>, the same rationale as
    /// <c>decision_observations.execution_id</c>: a step result is deletable (re-running a workflow
    /// replaces result rows) and a derivative indexed from it must not be cascaded away with it,
    /// nor may a FK hold a step result hostage. This is a denormalized pointer, resolved by an
    /// explicit lookup, never by EF navigation.
    /// </para>
    /// </summary>
    public Guid? SourceStepResultId { get; set; }

    /// <summary>
    /// The source object's storage key, snapshotted when the derivative was produced.
    ///
    /// <para>
    /// <b>A cache-key component, not the identity itself.</b> <see cref="SourceProjectFileId"/>
    /// cannot serve as one: a relinked or replaced file keeps its row id while its content — and
    /// therefore its key — changes, so keying on the id alone would let a stale derivative satisfy a
    /// request for different bytes. Bounded to 1000 to mirror the existing <see cref="StorageKey"/>
    /// column.
    /// </para>
    /// </summary>
    public string SourceStorageKey { get; set; } = string.Empty;

    /// <summary>
    /// The source object's ETag (or a content hash when the store reports none), snapshotted with
    /// <see cref="SourceStorageKey"/>.
    ///
    /// <para>
    /// <b>The other content-identity component.</b> The storage key alone is not content identity:
    /// re-uploading a different file under the same key is a normal operation, and without an etag
    /// the old derivative would silently be served for the new bytes. 200 characters fits a quoted
    /// S3 ETag (a 32-character hex MD5 plus quoting) many times over and still leaves room for a
    /// longer content hash; it was 256 before, and narrowing it is safe here because no row has ever
    /// been written to this table.
    /// </para>
    /// </summary>
    public string SourceETag { get; set; } = string.Empty;

    /// <summary>
    /// A hash over the parameter set that produced this derivative (the canonical spec JSON plus the
    /// builder version that interpreted it) — one <b>component</b> of <see cref="CacheKey"/>, not an
    /// identity of its own.
    ///
    /// <para>
    /// <b>Replaces "one derivative per kind".</b> A filmstrip has a density, a waveform has a level
    /// of detail, and a proxy has a resolution; several derivative sets of one source therefore must
    /// coexist rather than overwrite each other. Folding the builder version into the hash is what
    /// makes an argv-builder change invalidate the old objects automatically. 64 characters is
    /// exactly the length of a lowercase hex SHA-256 — the algorithm has no prefix by design, so a
    /// wider column would only permit a value this column can never legitimately hold.
    /// </para>
    /// </summary>
    public string ParamsHash { get; set; } = string.Empty;

    /// <summary>
    /// The exact spec that produced the stored object, as JSON (jsonb).
    ///
    /// <para>
    /// Kept alongside the hash, not instead of it: a hash proves two parameter sets differ but
    /// cannot say how, so without this column a cache miss or a corrupt derivative would be
    /// undiagnosable after the fact. jsonb rather than text so an operator can query into it.
    /// </para>
    /// </summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>
    /// The version of the argv builder that produced the stored object.
    ///
    /// <para>
    /// <b>Persisted as its own column rather than only inside <see cref="ParamsHash"/>.</b> It is a
    /// component of both the hash and <see cref="CacheKey"/>, so it is fully derivable from either —
    /// but a hash proves two parameter sets differ without saying how, and this is the one component
    /// an operator changing builder code actually needs to read: "which rows were produced by
    /// version 1" is a query, not a re-computation. It is deliberately <b>not derivable from
    /// <see cref="Kind"/></b>: each kind versions independently, so a filmstrip and a proxy can be on
    /// different versions at the same moment.
    /// </para>
    /// </summary>
    public int BuilderVersion { get; set; }

    /// <summary>
    /// The one canonical cache identity of this derivative, as a lowercase hex SHA-256.
    ///
    /// <para>
    /// <b>This is the whole identity, and the partial unique index is over this column alone.</b>
    /// The value is produced by <c>MediaDerivativeIdentity.Compute</c>, a length-prefixed hash over
    /// <see cref="SourceStorageKey"/>, <see cref="SourceETag"/>, the kind's slug, the variant, the
    /// parameter hash and the builder version, in that order — the producer computes it, the
    /// consumer recomputes it, and the two are compared as one value rather than column by column.
    /// The columns above are kept alongside it because a hash proves two rows differ but cannot say
    /// which component did, which would make a cache miss or a bad derivative undiagnosable after
    /// the fact.
    /// </para>
    /// <para>
    /// <b>The variant is a component of this value but deliberately not a column.</b> The variant is
    /// a position in a kind's own closed catalogue (a filmstrip density, a peaks level of detail)
    /// and is already reflected in <see cref="ParamsJson"/> for the kinds that have more than one,
    /// so a separate column would be a second source of truth that could disagree with the hash.
    /// The producer therefore supplies it to the identity and stores the resulting key; nothing
    /// downstream re-derives it.
    /// </para>
    /// <para>
    /// <b>64 characters</b> is exactly the length of a lowercase hex SHA-256 — the same algorithm and
    /// the same case as <c>MediaDerivativeCacheKey.ComputeCacheKey</c> in the WorkflowEngine, which
    /// is the function this value must equal byte for byte.
    /// </para>
    /// </summary>
    public string CacheKey { get; set; } = string.Empty;

    /// <summary>Which kind of derivative this is.</summary>
    public MediaDerivativeKind Kind { get; set; }

    /// <summary>S3/MinIO object key.</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>S3/MinIO bucket holding the object.</summary>
    public string StorageBucket { get; set; } = string.Empty;

    /// <summary>
    /// The storage key of the JSON <b>index sidecar</b> that describes this derivative, when it has
    /// one — a <b>COMPANION POINTER</b> to a sibling object, and deliberately
    /// <b>NOT a component of <see cref="CacheKey"/></b>: it names an index <i>about</i> a
    /// derivative, it is not the derivative's content identity. Two runs of the same spec that both
    /// need this pointer must still produce the same <see cref="CacheKey"/>, so folding it in would
    /// make a re-produced index look like a different derivative.
    ///
    /// <para>
    /// <b>The intended value: for a <see cref="MediaDerivativeKind.Filmstrip"/> SHEET row, the
    /// storage key of that filmstrip's index sidecar object</b> — the sibling
    /// <c>FilmstripIndex.IndexLeaf(paramsHash16)</c> artifact, i.e.
    /// <c>filmstrip-&lt;paramsHash16&gt;-index.json</c> (as produced by
    /// <c>FilmstripIndex.IndexLeaf</c>) <b>under the same prefix</b> as the sheet objects it
    /// accompanies. One density cannot be one row: a filmstrip is N sprite sheets plus one sidecar,
    /// so the sheet rows are the addressable units and they all point at the one sidecar that says
    /// which thumbnail index each sheet starts at and at what source time — that is what lets a
    /// client resolve a hover time to a sprite cell. <b>Null for every other kind</b> (a proxy,
    /// peaks series, thumbnail or keyframe index has no sidecar) <b>and for a filmstrip whose index
    /// object is not written yet</b>, so null must never be read as "no filmstrip header" — a client
    /// that needs the index treats a null pointer as "not available yet".
    /// </para>
    /// <para>
    /// <b>Never unique-indexed</b> (several rows of one density legitimately share the one sidecar
    /// object) and <b>never returned raw to a client</b>: the API resolves it to a content URL, so
    /// the bucket layout stays server-side. Bounded to 1000 to mirror the existing
    /// <see cref="StorageKey"/> column.
    /// </para>
    /// </summary>
    public string? IndexStorageKey { get; set; }

    /// <summary>MIME type of the stored object, when known.</summary>
    public string? MimeType { get; set; }

    /// <summary>Size of the stored object in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Duration in seconds, for time-based derivatives (proxy, waveform).</summary>
    public double? DurationSec { get; set; }

    /// <summary>Pixel width, for image/video derivatives.</summary>
    public int? Width { get; set; }

    /// <summary>Pixel height, for image/video derivatives.</summary>
    public int? Height { get; set; }

    /// <summary>
    /// The SOURCE's probe as the derivative run measured it (<see cref="MediaSourceProbe"/>, jsonb),
    /// or null on a row written before probes were stored and not yet backfilled. Every row of one
    /// source and content identity carries the same value; it describes the source, not this
    /// derivative (whose own measurements are <see cref="DurationSec"/>/<see cref="Width"/>/<see cref="Height"/>).
    /// </summary>
    public string? SourceProbeJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete marker; non-null rows are hidden by the query filter.</summary>
    public DateTime? DeletedAt { get; set; }
}
