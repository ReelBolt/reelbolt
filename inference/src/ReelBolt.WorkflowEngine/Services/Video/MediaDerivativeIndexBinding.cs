namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// What examining a produced derivative for a filmstrip index sidecar found.
/// </summary>
public enum MediaDerivativeIndexBindingStatus
{
    /// <summary>
    /// A filmstrip whose <c>application/json</c> artifact is exactly the leaf
    /// <see cref="FilmstripIndex.IndexLeaf"/> predicts. <see cref="MediaDerivativeIndexResolution.Reference"/>
    /// is set.
    /// </summary>
    Bound,

    /// <summary>
    /// Nothing to point at, which is the normal case: the derivative is not a filmstrip, or it is a
    /// filmstrip that carries no JSON artifact. <c>null</c> is the correct column value for both, and
    /// must not be read as "no filmstrip" — a client that needs the index treats it as "not available
    /// yet".
    /// </summary>
    NotApplicable,

    /// <summary>
    /// <b>A genuine finding, not a variant of "nothing to point at".</b> The result IS a filmstrip and
    /// DOES carry a JSON artifact, but that artifact's leaf is not the one
    /// <see cref="FilmstripIndex.IndexLeaf"/> predicts for this spec's parameter hash. Persisting a
    /// pointer to it would name an object the builder never wrote as the index — the silent-wrong
    /// failure this type exists to prevent — so no pointer is produced and the caller is expected to
    /// report the mismatch rather than choose one of the two leaves.
    /// </summary>
    LeafMismatch
}

/// <summary>
/// Where a filmstrip's index document lives: the directory its sibling sheets share, the leaf name
/// the builder wrote, and the object key the two compose.
/// </summary>
/// <param name="StoragePrefix">
/// <c>projects/{projectId}/agentFiles/media-derivatives/{sourceHash}</c> — the same prefix the sheet
/// objects live under, taken from the produced result rather than recomputed, so a row can never
/// point at an index in a directory its own sheets are not in.
/// </param>
/// <param name="IndexLeaf">Relative leaf, e.g. <c>filmstrip-&lt;paramsHash16&gt;-index.json</c>.</param>
/// <param name="IndexStorageKey">
/// <paramref name="StoragePrefix"/> joined with <paramref name="IndexLeaf"/> — the value the row's
/// <c>index_storage_key</c> holds. The caller is expected to confirm it against the key the object
/// store actually returned for that upload; this is the prediction, not the receipt.
/// </param>
public sealed record MediaDerivativeIndexReference(
    string StoragePrefix,
    string IndexLeaf,
    string IndexStorageKey);

/// <summary>The outcome of <see cref="MediaDerivativeIndexBinding.Resolve"/>.</summary>
/// <param name="Status">Which of the three cases this is.</param>
/// <param name="Reference">Set only when <paramref name="Status"/> is <see cref="MediaDerivativeIndexBindingStatus.Bound"/>.</param>
/// <param name="ActualIndexLeaf">
/// The JSON artifact's real leaf, set only on a <see cref="MediaDerivativeIndexBindingStatus.LeafMismatch"/>
/// so the caller can log which leaf was found instead of only that one was wrong.
/// </param>
public sealed record MediaDerivativeIndexResolution(
    MediaDerivativeIndexBindingStatus Status,
    MediaDerivativeIndexReference? Reference,
    string? ActualIndexLeaf)
{
    /// <summary>True when there is a pointer to persist.</summary>
    public bool IsBound => Status == MediaDerivativeIndexBindingStatus.Bound;

    internal static MediaDerivativeIndexResolution Bound(MediaDerivativeIndexReference reference) =>
        new(MediaDerivativeIndexBindingStatus.Bound, reference, ActualIndexLeaf: reference.IndexLeaf);

    internal static MediaDerivativeIndexResolution NotApplicable() =>
        new(MediaDerivativeIndexBindingStatus.NotApplicable, Reference: null, ActualIndexLeaf: null);

    internal static MediaDerivativeIndexResolution Mismatch(string actualLeaf) =>
        new(MediaDerivativeIndexBindingStatus.LeafMismatch, Reference: null, ActualIndexLeaf: actualLeaf);
}

/// <summary>
/// Binds a produced filmstrip derivative to the JSON index sidecar that describes it, so the sheet
/// row can carry an <c>index_storage_key</c> a client resolves into a content URL instead of
/// re-deriving a parameter hash of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure and I/O-free</b>, exactly like <see cref="FilmstripIndex"/>: it reads a produced result and
/// computes names. Uploading already happened by then (the dispatcher uploads every artifact), and the
/// binding deliberately does not re-derive the object key from the source — it takes
/// <see cref="MediaDerivativeResult.StoragePrefix"/>, the value the producer computed for this very
/// run, so the index can never be described as living somewhere its sheets do not.
/// </para>
/// <para>
/// <b>Why the caller must pass the spec.</b> The predicted leaf carries the <b>short parameter hash</b>
/// (<c>MediaDerivativeSpec.ParamsJson</c> + <c>BuilderVersion</c>), and a
/// <see cref="MediaDerivativeResult"/> does not carry the builder version. Recovering that hash by
/// parsing it back out of the artifact's own leaf would make the prediction agree with the artifact by
/// construction — the assertion below would be a tautology and could never catch the divergence it
/// exists for. The spec is the input that keeps it a genuine check.
/// </para>
/// <para>
/// <b>This is a server-side document and stays server-side.</b> A client fetches it through the key
/// the row owns; nothing in it, and nothing in this naming, comes from a client (plan §3.7 /
/// Global Constraint #12). In particular, "the client can derive <c>FilmstripIndex.IndexLeaf</c>
/// itself" is explicitly not the design: it would move the parameter-hash derivation onto the client
/// and break the moment a builder version changes.
/// </para>
/// </remarks>
public static class MediaDerivativeIndexBinding
{
    /// <summary>
    /// The content type the produced sidecar carries. Matched ordinally, case-insensitively — a
    /// producer that spells it <c>Application/JSON</c> still means the same object.
    /// </summary>
    public const string IndexContentType = "application/json";

    /// <summary>
    /// Resolves <paramref name="result"/> to its index sidecar, or explains why it has none. Never
    /// throws for a malformed or foreign result: every reason not to bind is a status, because a
    /// derivative that cannot be indexed is still a derivative worth writing.
    /// </summary>
    /// <param name="spec">The spec this result was produced from — the input the leaf prediction needs.</param>
    /// <param name="result">The produced derivative.</param>
    public static MediaDerivativeIndexResolution Resolve(MediaDerivativeSpec spec, MediaDerivativeResult result)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(result);

        // The slug check comes FIRST, and deliberately not "does it have a JSON artifact": a waveform
        // peaks derivative carries an application/json sidecar of its own (the byte-layout header), so
        // deciding on content type alone would bind a peaks row to an index document that describes no
        // filmstrip.
        if (result.Slug is not (MediaDerivativeKindSlug.Filmstrip or MediaDerivativeKindSlug.Stills))
            return MediaDerivativeIndexResolution.NotApplicable();

        MediaDerivativeArtifact? sidecar = result.Artifacts.FirstOrDefault(
            artifact => string.Equals(artifact.ContentType, IndexContentType, StringComparison.OrdinalIgnoreCase));

        if (sidecar is null)
            return MediaDerivativeIndexResolution.NotApplicable();

        string predictedLeaf = PredictIndexLeaf(spec);

        if (!string.Equals(sidecar.FileName, predictedLeaf, StringComparison.Ordinal))
        {
            // A finding. No pointer is produced: the row keeps a null index_storage_key, which the
            // contract defines as "not available yet" — a value a client already handles — rather
            // than a pointer to an object the builder never wrote.
            return MediaDerivativeIndexResolution.Mismatch(sidecar.FileName);
        }

        string storagePrefix = result.StoragePrefix;
        if (string.IsNullOrWhiteSpace(storagePrefix))
            return MediaDerivativeIndexResolution.NotApplicable();

        return MediaDerivativeIndexResolution.Bound(new MediaDerivativeIndexReference(
            storagePrefix,
            sidecar.FileName,
            $"{storagePrefix.TrimEnd('/')}/{sidecar.FileName}"));
    }

    /// <summary>
    /// The leaf the filmstrip builder writes for this spec's parameter hash — the prediction
    /// <see cref="Resolve"/> checks the produced artifact against. Exposed so a test (or an operator
    /// reading a log) can see what was expected without re-deriving the hash by hand.
    /// </summary>
    /// <remarks>
    /// The truncation to 16 hex characters is the producer's own convention, mirrored from
    /// <c>MediaDerivativeService.ProduceFilmstripAsync</c>: the sheet and index leaves both carry the
    /// short hash so two densities of one source land as sibling objects rather than overwriting each
    /// other. It is a name, never an identity — the identity is the full <c>cache_key</c>.
    /// </remarks>
    public static string PredictIndexLeaf(MediaDerivativeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string paramsHash = MediaDerivativeCacheKey.ComputeParamsHash(spec);
        return spec.Slug == MediaDerivativeKindSlug.Stills
            ? StillsArgvBuilder.IndexLeaf(paramsHash[..16])
            : FilmstripIndex.IndexLeaf(paramsHash[..16]);
    }
}
