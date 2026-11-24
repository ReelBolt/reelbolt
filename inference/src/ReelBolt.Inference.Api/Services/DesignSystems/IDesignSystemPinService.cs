using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// Builds the per-run design-system pin (SPEC 9.1): the JSON snapshot of the project's currently
/// Approved design version and product-map version, read at queue time. There is no request field —
/// the pin is always "the current approved versions at request time".
/// </summary>
public interface IDesignSystemPinService
{
    /// <summary>
    /// The serialized <see cref="DesignSystemPin"/> for <paramref name="projectId"/>, or null when the
    /// project has no Approved version of either kind. Never throws for absent data.
    /// </summary>
    Task<string?> BuildPinJsonAsync(Guid projectId, CancellationToken ct);
}
