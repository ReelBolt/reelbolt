using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Implemented by a media client whose PRICE depends on more than the model id — a quality tier, a
/// resolution tier — so metering can record, and the rate card can price, the tier that actually ran.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> A rate card keys on <c>(provider kind, usage kind, model name)</c> and cannot
/// express a request parameter. Several fal endpoints price purely as a function of one enum parameter
/// (<c>ideogram/v4.5</c> is $0.03, $0.06 or $0.22 by <c>quality</c>; <c>fal-ai/object-removal/mask</c>
/// is $0.006 to $0.024 by <c>model</c>; <c>google/nano-banana-2.1</c> is priced by <c>resolution</c>).
/// Pricing all of those at one figure either over-charges the cheap tier several times over or
/// under-charges the dear one, and both are silent. The key returned here is
/// <c>{endpoint}#{tier value}</c>, which is an ordinary <c>model_name</c> as far as the ledger and the
/// rate-card specificity rule (<c>exact &gt; longest prefix &gt; *</c>) are concerned — so no schema
/// change and no new rate-card matching rule is needed.
/// </para>
/// <para>
/// A client that prices by model alone does not implement this, and the decorator falls back to the
/// request's model (or the provider row's model when the request names none).
/// </para>
/// </remarks>
public interface IBillingModelKeyed
{
    /// <summary>
    /// The model name to meter a call under. <paramref name="parameters"/> are the request's
    /// endpoint-specific parameters (validated by the call that already succeeded). Must be total: an
    /// unknown model returns <paramref name="model"/> unchanged.
    /// </summary>
    string BillingModelKey(string model, IReadOnlyDictionary<string, JsonElement>? parameters);

    /// <summary>
    /// True when this call to <paramref name="model"/> is priced by the size of the picture it returns alone, so the
    /// meter records the measured output as the <c>{n}MP</c> price variant the rate rows are keyed on. A call whose
    /// price also counts input megapixels the output does not show answers false and keeps the variant-less row.
    /// Must be total: an unknown model is <c>false</c>.
    /// </summary>
    /// <param name="model">The model the call is billed under.</param>
    /// <param name="editsASource">The call takes a source picture (edit, inpaint, transform), not only a prompt.</param>
    /// <param name="referenceImages">Extra input pictures beyond the source (reference images).</param>
    bool PricesByOutputMegapixels(string model, bool editsASource, int referenceImages) => false;

    /// <summary>
    /// The most pixels <paramref name="model"/>'s output can have, from its vendor's documentation, or <c>null</c> when
    /// none is documented. A size-priced picture whose size cannot be read is billed at this ceiling, and at the
    /// variant-less (largest) row when there is none.
    /// </summary>
    long? MaxOutputPixels(string model) => null;
}
