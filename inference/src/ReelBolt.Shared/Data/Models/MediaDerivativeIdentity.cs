using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// The one canonical identity of a media derivative: a lowercase hex SHA-256 over the
/// length-prefixed join of (<c>sourceStorageKey</c>, <c>sourceEtag</c>, <c>kind</c> slug,
/// <c>variant</c>, <c>paramsHash</c>, <c>builderVersion</c>) — the six components behind the single
/// <see cref="MediaDerivative.CacheKey"/> column, which the <c>media_derivatives</c> table carries a
/// partial unique index over (<c>ux_media_derivatives_cache_key</c>, <c>WHERE deleted_at IS NULL</c>).
///
/// <para>
/// One function, no state and no I/O, so every producer and every consumer derives the same string
/// from the same six values rather than each re-implementing the concatenation.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a mirror, and byte-equality is the contract.</b> The consumer side of the wave is
/// <c>MediaDerivativeCacheKey.ComputeCacheKey</c>
/// (<c>WorkflowEngine/Services/Video/MediaDerivativeCacheKey.cs</c>, media-department-owned and
/// read-only here). Whatever that function returns for a source and a
/// <c>MediaDerivativeSpec</c> is, verbatim, the value this function must return for the same inputs
/// — the DB <c>cache_key</c> and the consumer's computed key are one and the same value, and a
/// single-character divergence would silently produce a cache that never hits rather than an error.
/// The two therefore share three conventions, kept identical on purpose and not merely similar:
/// </para>
/// <list type="number">
/// <item><description>
/// the <b>component order</b> above, which is <c>ComputeCacheKey</c>'s order exactly
/// (<c>sourceStorageKey</c>, <c>sourceEtag</c>, slug, variant, parameter hash, builder version);
/// </description></item>
/// <item><description>
/// <b>length-prefixing</b>, each component emitted as <c>&lt;length&gt;:&lt;value&gt;|</c>, so the
/// join is injective. The components are storage keys and hashes and a storage key may itself
/// contain the separator, so a bare <c>|</c> join could not tell <c>key="a", etag="b|c"</c> from
/// <c>key="a|b", etag="c"</c> — two genuinely different sources that would then share one derivative
/// object and one row. A value containing the separator changes its own length, so it cannot trade
/// characters with its neighbour to reproduce another component set's material. This is the same
/// discipline <c>DecisionQuestionMemoizer</c> uses (uppercase there; lowercase here);
/// </description></item>
/// <item><description>
/// <b>lowercase hex</b>, because these values are used as object-store path segments and
/// S3-compatible stores are case-sensitive, so the case is pinned once here rather than at every
/// call site.
/// </description></item>
/// </list>
/// <para>
/// The parameters arrive already in their final string form on the consumer side
/// (<c>spec.Slug.ToString()</c>, <c>spec.Variant.ToString(InvariantCulture)</c>), and the kind and
/// variant are converted here the same way — <see cref="MediaDerivativeKind"/>'s member names are
/// the spec's slugs, which <c>MediaDerivativeIdentityTests</c> asserts name for name so a rename on
/// either side fails loudly instead of silently sharing no cache at all.
/// </para>
/// <para>
/// <b>The parameter hash is an input, not something recomputed here.</b> <c>ComputeParamsHash</c>
/// hashes the canonical parameter JSON plus the builder version; this function only folds its result
/// into the wider identity, so there is exactly one implementation of that inner hash.
/// </para>
/// </remarks>
public static class MediaDerivativeIdentity
{
    /// <summary>Length in hex characters of the value returned by <see cref="Compute"/>.</summary>
    public const int HexLength = 64;

    /// <summary>
    /// Computes the canonical identity of a derivative of one source under one parameter set.
    /// Pure: same six inputs, same output, always.
    /// </summary>
    /// <param name="sourceStorageKey">The source object's storage key (the identity half that
    /// changes when a file is relinked or replaced).</param>
    /// <param name="sourceEtag">The source object's ETag at production time (the content-identity
    /// half).</param>
    /// <param name="kind">Which kind of derivative — the component that keeps two different kinds of
    /// one source apart when their parameter hashes happen to coincide.</param>
    /// <param name="variant">The variant index within that kind (a filmstrip density, a waveform
    /// level of detail); 0 for a kind that has exactly one.</param>
    /// <param name="paramsHash">Hash over the derivative parameter set that produced the object —
    /// the value stored in <see cref="MediaDerivative.ParamsHash"/>.</param>
    /// <param name="builderVersion">Version of the argv builder that produced the bytes — the value
    /// stored in <see cref="MediaDerivative.BuilderVersion"/>.</param>
    public static string Compute(
        string sourceStorageKey,
        string sourceEtag,
        MediaDerivativeKind kind,
        int variant,
        string paramsHash,
        int builderVersion)
    {
        ArgumentNullException.ThrowIfNull(sourceStorageKey);
        ArgumentNullException.ThrowIfNull(sourceEtag);
        ArgumentNullException.ThrowIfNull(paramsHash);

        var material = new StringBuilder();
        AppendComponent(material, sourceStorageKey);
        AppendComponent(material, sourceEtag);
        AppendComponent(material, kind.ToString());
        AppendComponent(material, variant.ToString(CultureInfo.InvariantCulture));
        AppendComponent(material, paramsHash);
        AppendComponent(material, builderVersion.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
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
}
