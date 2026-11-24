namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One vendor model served by an <see cref="InferenceProvider"/> account (docs/design/model-catalog.md §2):
/// the code sent to the vendor and the customer-facing name pickers show. Its <see cref="Capabilities"/>
/// are what steps, agents and voices reference.
/// </summary>
public class InferenceModel
{
    public Guid Id { get; set; }

    public Guid ProviderId { get; set; }

    /// <summary>Denormalised from the provider (kept equal by the admin service). Null = platform-managed.</summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Azure deployment name, or the vendor's model id.</summary>
    public string ModelCode { get; set; } = string.Empty;

    /// <summary>Picker label, unique per organization (NULL = the platform scope).</summary>
    public string DisplayName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public InferenceProvider Provider { get; set; } = null!;

    public ICollection<ModelCapability> Capabilities { get; set; } = new List<ModelCapability>();
}
