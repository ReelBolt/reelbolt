using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A fully resolved music or sound-effect provider, ready to build (or reuse) an <see cref="IAudioGenerationClient"/>
/// from. Built fresh on every resolve by <see cref="IInferenceProviderResolver"/>; never persisted. The same shape as
/// <see cref="ResolvedImageProvider"/>: <paramref name="Capability"/> is stamped on so a caller can tell a music
/// resolution from a sound-effect one.
/// </summary>
/// <param name="ProviderId">The model capability id (the old provider row id for a migrated row).</param>
/// <param name="AccountId">The provider account (<c>inference_providers</c>) serving the capability.</param>
/// <param name="ModelId">The model (<c>inference_models</c>) the capability belongs to.</param>
public sealed record ResolvedAudioGenerationProvider(
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
    /// (Kind|Endpoint|ModelName|SHA256(ApiKey)|TimeoutSeconds, itself hashed). The key is not recoverable from it.
    /// </summary>
    public string CacheKey { get; } = Sha256Hex(string.Join('|',
        Kind, Endpoint, ModelName, Sha256Hex(ApiKey ?? string.Empty), TimeoutSeconds));

    /// <summary>Never prints the key (the generated record ToString would).</summary>
    public override string ToString() => $"{Name} ({Kind}/{Capability}/{ModelName})";

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
