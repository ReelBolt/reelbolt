using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A fully resolved segmentation/depth provider configuration, ready to build (or reuse) an
/// <see cref="IImageAnalysisClient"/> from. Built fresh on every resolve by
/// <see cref="IInferenceProviderResolver"/> — never persisted itself.
/// </summary>
/// <remarks>
/// One resolved type for both capabilities, for the same reason <see cref="IImageClient"/> has one:
/// the CLIENT is the same, so a second record would be a byte-identical copy that silently diverges
/// the first time one gained a field. <see cref="Capability"/> is what keeps a segmentation
/// resolution distinguishable from a depth one, and it is part of <see cref="CacheKey"/>.
/// </remarks>
/// <param name="Capability">The capability this resolution was made for: one of
/// <see cref="InferenceProviderCapability.Segmentation"/>,
/// <see cref="InferenceProviderCapability.Depth"/> or
/// <see cref="InferenceProviderCapability.VideoSegmentation"/> — the resolver filters on it, so a row
/// of one capability can never be handed to another's call. (Video segmentation reuses this record
/// because the resolved shape is identical; <see cref="IVideoSegmentationClient"/> is still the only
/// client that consumes it.)</param>
/// <param name="IsPlatformManaged">True for a platform row, false for the organization's own. A
/// billing input, not a display flag: a BYO row meters at a zero multiplier.</param>
/// <param name="ProviderId">
/// The MODEL CAPABILITY id (docs/design/model-catalog.md §5.1): the old provider row id for a migrated
/// row, so fingerprints, usage events and step configs keep their values. Null for the config fallback.
/// </param>
/// <param name="AccountId">The provider account (<c>inference_providers</c>) serving the capability.</param>
/// <param name="ModelId">The model (<c>inference_models</c>) the capability belongs to.</param>
public sealed record ResolvedImageAnalysisProvider(
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
    /// Stable cache key over everything that affects client construction
    /// (Kind|Capability|Endpoint|ModelName|SHA256(ApiKey)|TimeoutSeconds, itself hashed).
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
