using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A fully resolved image provider configuration, ready to build (or reuse) an
/// <see cref="IImageClient"/> from. Built fresh on every resolve by
/// <see cref="IInferenceProviderResolver"/> — never persisted itself.
/// </summary>
/// <remarks>
/// <b>One resolved type for two capabilities, deliberately.</b> Every other capability has its own
/// resolved record because each resolves through different machinery and its results are never
/// interchangeable. <see cref="ImageGeneration"/> and <see cref="ImageEdit"/> resolve to the SAME
/// client (<see cref="IImageClient"/>), so a second record would be a byte-identical copy carrying a
/// different name — and, worse, the two would silently diverge the first time one gained a field.
/// <see cref="Capability"/> is what keeps them distinguishable: it is the capability the row was
/// resolved FOR, it is included in <see cref="CacheKey"/>, and it is what a caller checks before
/// assuming it may call <c>EditAsync</c>.
/// </remarks>
/// <param name="Capability">
/// The capability this resolution was made for. Never <c>Chat</c>, <c>Vision</c> or anything else
/// outside the image family — the resolver filters on it.
/// </param>
/// <param name="IsPlatformManaged">
/// True for a platform row, false for the organization's own. Carried because metering bills a BYO
/// row at a zero multiplier and a platform row at the platform rate, so this is a billing input, not
/// a display flag.
/// </param>
/// <param name="ProviderId">
/// The MODEL CAPABILITY id (docs/design/model-catalog.md §5.1): the old provider row id for a migrated
/// row, so fingerprints, usage events and step configs keep their values. Null for the config fallback.
/// </param>
/// <param name="AccountId">The provider account (<c>inference_providers</c>) serving the capability.</param>
/// <param name="ModelId">The model (<c>inference_models</c>) the capability belongs to.</param>
public sealed record ResolvedImageProvider(
    Guid? ProviderId,
    string Name,
    InferenceProviderKind Kind,
    InferenceProviderCapability Capability,
    string Endpoint,
    string ModelName,
    string ApiKey,
    int TimeoutSeconds,
    Guid? OrganizationId = null,
    bool IsPlatformManaged = true,
    Guid? AccountId = null,
    Guid? ModelId = null)
{
    /// <summary>
    /// Stable cache key over everything that affects image-client construction
    /// (Kind|Capability|Endpoint|ModelName|SHA256(ApiKey)|TimeoutSeconds, itself hashed).
    /// <see cref="Capability"/> is included even though the client is the same for both, so that two
    /// rows which are identical apart from their capability cannot share one cached instance and
    /// then be reported under the wrong capability.
    /// The raw API key is never included in, or recoverable from, this value.
    /// </summary>
    public string CacheKey { get; } = ComputeCacheKey(Kind, Capability, Endpoint, ModelName, ApiKey, TimeoutSeconds);

    private static string ComputeCacheKey(
        InferenceProviderKind kind,
        InferenceProviderCapability capability,
        string endpoint,
        string modelName,
        string apiKey,
        int timeoutSeconds)
    {
        string hashedApiKey = Sha256Hex(apiKey ?? string.Empty);
        string material = string.Join('|', kind, capability, endpoint, modelName, hashedApiKey, timeoutSeconds);
        return Sha256Hex(material);
    }

    /// <summary>
    /// Suppresses the compiler-generated record <c>ToString()</c>, which would print every
    /// positional member — including <c>ApiKey</c> in plaintext.
    /// </summary>
    public override string ToString() => $"{Name} ({Kind}/{Capability}/{ModelName})";

    private static string Sha256Hex(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}
