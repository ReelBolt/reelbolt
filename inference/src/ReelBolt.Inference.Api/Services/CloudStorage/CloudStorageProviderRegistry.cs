using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.CloudStorage;

/// <summary>
/// The registered <see cref="ICloudStorageProvider"/> implementations, by kind
/// (docs/design/cloud-drive-sync.md "Core services"). The one place the generic code asks "which
/// provider handles this kind" and "which providers may be offered at all".
/// </summary>
public interface ICloudStorageProviderRegistry
{
    /// <summary>The provider of <paramref name="kind"/>, or null when none is registered.</summary>
    ICloudStorageProvider? Get(CloudStorageProviderKind kind);

    /// <summary>The registered providers whose client id and secret are configured, in <see cref="CloudStorageProviders.All"/> order.</summary>
    IReadOnlyList<ICloudStorageProvider> Configured { get; }
}

/// <inheritdoc />
public sealed class CloudStorageProviderRegistry : ICloudStorageProviderRegistry
{
    private readonly Dictionary<CloudStorageProviderKind, ICloudStorageProvider> _byKind;

    public CloudStorageProviderRegistry(IEnumerable<ICloudStorageProvider> providers)
    {
        _byKind = [];
        foreach (ICloudStorageProvider provider in providers)
        {
            // The first registration of a kind wins; a duplicate is a wiring mistake, not a choice.
            _byKind.TryAdd(provider.Kind, provider);
        }
    }

    public ICloudStorageProvider? Get(CloudStorageProviderKind kind) =>
        _byKind.TryGetValue(kind, out ICloudStorageProvider? provider) ? provider : null;

    public IReadOnlyList<ICloudStorageProvider> Configured =>
        CloudStorageProviders.All
            .Select(Get)
            .Where(p => p is { IsConfigured: true })
            .Select(p => p!)
            .ToList();
}
