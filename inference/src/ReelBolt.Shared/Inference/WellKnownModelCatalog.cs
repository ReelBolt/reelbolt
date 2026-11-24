namespace ReelBolt.Shared.Inference;

/// <summary>
/// Fixed ids of the platform compute provider, its one model and its one <c>Compute</c> capability
/// (docs/design/model-catalog.md §4.2 step 1, §5.4). The catalog upgrader creates them; compute metering
/// prices against <see cref="ComputeCapabilityId"/>.
/// </summary>
public static class WellKnownModelCatalog
{
    public static readonly Guid ComputeProviderId = new("6f1c2a4e-0c3b-4d7e-9a51-1c0d5e7b2a01");

    public static readonly Guid ComputeModelId = new("6f1c2a4e-0c3b-4d7e-9a51-1c0d5e7b2a02");

    public static readonly Guid ComputeCapabilityId = new("6f1c2a4e-0c3b-4d7e-9a51-1c0d5e7b2a03");

    /// <summary>The compute provider's name.</summary>
    public const string ComputeProviderName = "Platform compute";

    /// <summary>The compute model's display name.</summary>
    public const string ComputeModelDisplayName = "Rendering and encoding";

    /// <summary>The compute model's code.</summary>
    public const string ComputeModelCode = "compute";
}
