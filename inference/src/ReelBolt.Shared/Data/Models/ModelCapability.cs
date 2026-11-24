namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What one <see cref="InferenceModel"/> is used for (docs/design/model-catalog.md §2). Its id is what
/// every "provider id" reference holds: a migrated row keeps the id of the <c>inference_providers</c>
/// row it came from, so step configs, agent/voice/override FKs, usage events and step-cache
/// fingerprints stay valid unchanged.
/// </summary>
public class ModelCapability
{
    public Guid Id { get; set; }

    public Guid ModelId { get; set; }

    /// <summary>Denormalised from the model (kept equal by the admin service). Null = platform-managed.</summary>
    public Guid? OrganizationId { get; set; }

    public InferenceProviderCapability Capability { get; set; } = InferenceProviderCapability.Chat;

    /// <summary>At most one default per (organization, capability), the platform scope included.</summary>
    public bool IsDefault { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ModelPriceSource PriceSource { get; set; } = ModelPriceSource.Table;

    public DateTime? LastTestAt { get; set; }

    public bool? LastTestOk { get; set; }

    public string? LastTestError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public InferenceModel Model { get; set; } = null!;

    public ICollection<ModelCapabilityPrice> Prices { get; set; } = new List<ModelCapabilityPrice>();

    public ICollection<AgentDefinition> AgentDefinitions { get; set; } = new List<AgentDefinition>();
}
