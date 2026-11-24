using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A fully resolved embedding provider configuration, ready to build (or reuse) an
/// <c>IEmbeddingGenerator</c> from. Built fresh on every resolve by
/// <see cref="IInferenceProviderResolver"/> — never persisted itself. Mirrors
/// <see cref="ResolvedInferenceProvider"/> exactly, kept as a separate type because a chat
/// resolution and a embedding resolution are never interchangeable (see
/// <see cref="InferenceProviderCapability"/>).
/// </summary>
/// <param name="ProviderId">
/// The MODEL CAPABILITY id (docs/design/model-catalog.md §5.1): the old provider row id for a migrated
/// row, so fingerprints, usage events and step configs keep their values. Null for the config fallback.
/// </param>
/// <param name="AccountId">The provider account (<c>inference_providers</c>) serving the capability.</param>
/// <param name="ModelId">The model (<c>inference_models</c>) the capability belongs to.</param>
public sealed record ResolvedEmbeddingProvider(
    Guid? ProviderId,
    string Name,
    InferenceProviderKind Kind,
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
    /// Stable cache key over everything that affects embedding-client construction
    /// (Kind|Endpoint|ModelName|SHA256(ApiKey)|TimeoutSeconds, itself hashed).
    /// The raw API key is never included in, or recoverable from, this value.
    /// </summary>
    public string CacheKey { get; } = ComputeCacheKey(Kind, Endpoint, ModelName, ApiKey, TimeoutSeconds);

    private static string ComputeCacheKey(
        InferenceProviderKind kind,
        string endpoint,
        string modelName,
        string apiKey,
        int timeoutSeconds)
    {
        string hashedApiKey = Sha256Hex(apiKey ?? string.Empty);
        string material = string.Join('|', kind, endpoint, modelName, hashedApiKey, timeoutSeconds);
        return Sha256Hex(material);
    }

    /// <summary>
    /// Suppresses the compiler-generated record <c>ToString()</c>, which would print every
    /// positional member — including <c>ApiKey</c> in plaintext. Nothing logs a whole provider
    /// today, but this type is one `LogError("... {Provider}", provider)` away from leaking a
    /// credential into the log stream.
    /// </summary>
    public override string ToString() => $"{Name} ({Kind}/{ModelName})";

    private static string Sha256Hex(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}
