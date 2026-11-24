using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// A fully resolved inference provider configuration, ready to build (or reuse) an
/// <see cref="Microsoft.Extensions.AI.IChatClient"/> from. Built fresh on every resolve by
/// <see cref="IInferenceProviderResolver"/> — never persisted itself.
/// </summary>
/// <param name="PersonalOAuthMode">
/// See <see cref="ReelBolt.Shared.Data.Models.InferenceProvider.PersonalOAuthMode"/>. Defaults to
/// <c>false</c> so every existing positional construction (tests included) keeps compiling
/// unchanged.
/// </param>
/// <param name="ProviderId">
/// The MODEL CAPABILITY id (docs/design/model-catalog.md §5.1): the old provider row id for a migrated
/// row, so fingerprints, usage events and step configs keep their values. Null for the config fallback.
/// </param>
/// <param name="AccountId">The provider account (<c>inference_providers</c>) serving the capability.</param>
/// <param name="ModelId">The model (<c>inference_models</c>) the capability belongs to.</param>
/// <param name="Capability">
/// The capability the resolved row serves: <c>Chat</c> for a chat resolution, <c>Vision</c> for a vision one. Metering
/// prices the call under it (docs/design/model-catalog.md §5.3). Not part of <see cref="CacheKey"/>: chat and vision
/// share the client.
/// </param>
public sealed record ResolvedInferenceProvider(
    Guid? ProviderId,
    string Name,
    InferenceProviderKind Kind,
    string Endpoint,
    string ModelName,
    string ApiKey,
    int TimeoutSeconds,
    bool PersonalOAuthMode = false,
    Guid? OrganizationId = null,
    bool IsPlatformManaged = true,
    Guid? AccountId = null,
    Guid? ModelId = null,
    InferenceProviderCapability Capability = InferenceProviderCapability.Chat)
{
    /// <summary>
    /// Stable cache key over everything that affects chat-client construction
    /// (Kind|Endpoint|ModelName|SHA256(ApiKey)|TimeoutSeconds|PersonalOAuthMode, itself hashed).
    /// PersonalOAuthMode participates because it changes the headers/system-prompt behaviour
    /// <c>ChatClientFactory.BuildAnthropic</c> bakes into the client at construction time — without
    /// it, toggling the flag on an otherwise-unchanged row would keep serving the stale cached
    /// client. For a LocalRunner row the owning organization is part of it too, and its Endpoint is the paired device,
    /// so repointing the row at another device builds a new client. The raw API key is never included in, or recoverable from, this value.
    /// </summary>
    public string CacheKey { get; } = ComputeCacheKey(Kind, Endpoint, ModelName, ApiKey, TimeoutSeconds, PersonalOAuthMode, OrganizationId);

    private static string ComputeCacheKey(
        InferenceProviderKind kind,
        string endpoint,
        string modelName,
        string apiKey,
        int timeoutSeconds,
        bool personalOAuthMode,
        Guid? organizationId)
    {
        string hashedApiKey = Sha256Hex(apiKey ?? string.Empty);
        string material = string.Join('|', kind, endpoint, modelName, hashedApiKey, timeoutSeconds, personalOAuthMode);

        // A LocalRunner client bakes the owning organization and the device into its relay address, so the
        // organization joins the key for that kind only (every other kind's key is unchanged).
        if (kind == InferenceProviderKind.LocalRunner)
        {
            material += "|" + (organizationId?.ToString("D") ?? string.Empty);
        }

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
