using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The cache identity of a media derivative: a SHA-256 over the length-prefixed join of the
/// source identity (<c>source_storage_key</c>, <c>source_etag</c>) and the spec
/// (<c>slug</c>, <c>variant</c>, <c>params_hash</c>, <c>builder_version</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every component is length-prefixed, and the join is therefore injective.</b> This is the
/// same discipline <c>DecisionQuestionMemoizer</c> uses (<c>WorkflowEngine/Execution/Decision/</c>)
/// and it is mirrored deliberately rather than reinvented. The components are caller-supplied
/// strings that may themselves contain the separator, so a bare <c>|</c> join cannot tell
/// <c>key="a", etag="b|c"</c> from <c>key="a|b", etag="c"</c> — two different sources that would
/// silently share one derivative object and one cache row. Each component is emitted as
/// <c>&lt;length&gt;:&lt;value&gt;|</c>; the length is what makes that impossible, because a value
/// containing the separator changes its own length and so cannot trade characters with its
/// neighbour to reproduce another component set's material.
/// </para>
/// <para>
/// <b>Lowercase hex</b>, unlike <c>DecisionQuestionMemoizer</c>'s uppercase form: these keys are
/// path segments and storage-key fragments (<c>projects/{p}/agentFiles/media-derivatives/{hash}/…</c>),
/// and S3-compatible object stores are case-sensitive, so the case is pinned here rather than at
/// every call site.
/// </para>
/// </remarks>
public static class MediaDerivativeCacheKey
{
    /// <summary>Number of hex characters of <see cref="ComputeSourceHash"/> used as the directory segment.</summary>
    public const int SourceHashLength = 16;

    /// <summary>
    /// SHA-256 over <see cref="MediaDerivativeSpec.ParamsJson"/> and
    /// <see cref="MediaDerivativeSpec.BuilderVersion"/>, length-prefixed the same way as
    /// <see cref="ComputeCacheKey"/>. Exposed separately because the storage leaf name carries it:
    /// two parameter sets of one source land in sibling objects, never on the same key.
    /// </summary>
    public static string ComputeParamsHash(MediaDerivativeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var material = new StringBuilder();
        AppendComponent(material, spec.ParamsJson);
        AppendComponent(material, spec.BuilderVersion.ToString(CultureInfo.InvariantCulture));
        return Sha256HexLower(material.ToString());
    }

    /// <summary>
    /// The full cache identity of one derivative of one source.
    /// </summary>
    public static string ComputeCacheKey(string sourceStorageKey, string sourceEtag, MediaDerivativeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(sourceStorageKey);
        ArgumentNullException.ThrowIfNull(sourceEtag);
        ArgumentNullException.ThrowIfNull(spec);

        var material = new StringBuilder();
        AppendComponent(material, sourceStorageKey);
        AppendComponent(material, sourceEtag);
        AppendComponent(material, spec.Slug.ToString());
        AppendComponent(material, spec.Variant.ToString(CultureInfo.InvariantCulture));
        AppendComponent(material, ComputeParamsHash(spec));
        AppendComponent(material, spec.BuilderVersion.ToString(CultureInfo.InvariantCulture));

        return Sha256HexLower(material.ToString());
    }

    /// <summary>
    /// The source identity on its own — first <see cref="SourceHashLength"/> hex characters of a
    /// SHA-256 over the same length-prefixed join of <c>storage key + etag</c>. Groups every
    /// derivative of one source (all kinds, all variants) under one storage directory while
    /// staying short enough to read in a log line.
    /// </summary>
    public static string ComputeSourceHash(string sourceStorageKey, string sourceEtag)
    {
        ArgumentNullException.ThrowIfNull(sourceStorageKey);
        ArgumentNullException.ThrowIfNull(sourceEtag);

        var material = new StringBuilder();
        AppendComponent(material, sourceStorageKey);
        AppendComponent(material, sourceEtag);

        return Sha256HexLower(material.ToString())[..SourceHashLength];
    }

    /// <summary>
    /// The storage directory every derivative of this source belongs under:
    /// <c>projects/{projectId}/agentFiles/media-derivatives/{sourceHash}</c>. Returned as a prefix
    /// (no trailing slash) so the caller joins it with a plain relative leaf name — the leaf is
    /// what <c>IProjectFileWorkspace.UploadArtifactAsync</c> takes as <c>fileName</c>, and
    /// <c>ProjectFilePath.NormalizeRelativePath</c> rejects <c>..</c>, so a full key must never be
    /// passed there.
    /// </summary>
    public static string ComputeStoragePrefix(Guid projectId, string sourceStorageKey, string sourceEtag)
    {
        ArgumentNullException.ThrowIfNull(sourceStorageKey);
        ArgumentNullException.ThrowIfNull(sourceEtag);

        return $"projects/{projectId:D}/agentFiles/media-derivatives/{ComputeSourceHash(sourceStorageKey, sourceEtag)}";
    }

    /// <summary>
    /// Appends one component as <c>&lt;length&gt;:&lt;value&gt;|</c>. The length is what makes the
    /// join injective: a value containing the separator changes its own length, so it cannot trade
    /// characters with its neighbour and produce another component set's material.
    /// </summary>
    private static void AppendComponent(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
               .Append(':')
               .Append(value)
               .Append('|');

    private static string Sha256HexLower(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
