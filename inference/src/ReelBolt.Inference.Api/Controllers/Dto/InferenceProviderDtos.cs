namespace ReelBolt.Inference.Api.Controllers.Dto;

// ---- the model catalog tree (docs/design/model-catalog.md §6) -------------------------------------------------
// Platform /api/v1/inference-providers and the org twin /api/v1/org/inference-providers share these shapes; the org
// endpoints always answer `prices: null`.

/// <summary>One provider ACCOUNT: kind, endpoint, key and timeout, shared by every model under it.</summary>
public record ProviderResponse(Guid Id, Guid? OrganizationId, string Name, string Kind, string Endpoint,
    bool HasApiKey, string? ApiKeyLastFour, bool IsEnabled, bool IsSystem, int? TimeoutSeconds, bool PersonalOAuthMode,
    DateTime CreatedAt, DateTime UpdatedAt, List<ModelResponse> Models);

/// <summary>One model under a provider: the vendor's model code and the name customers pick.</summary>
public record ModelResponse(Guid Id, string ModelCode, string DisplayName, bool IsEnabled, List<CapabilityResponse> Capabilities);

/// <summary>
/// One capability of a model: its id is what agents, overrides, voices and step configs store. <paramref name="Prices"/>
/// is the platform's price rows (newest first) and always null on the org endpoints.
/// </summary>
public record CapabilityResponse(Guid Id, string Capability, bool IsDefault, bool IsEnabled, string PriceSource,
    DateTime? LastTestAt, bool? LastTestOk, string? LastTestError, List<PriceResponse>? Prices);

/// <summary>
/// One price row of a capability. <paramref name="EffectiveCreditsMillisPerUnit"/> is
/// <c>override ?? cost × markup × 1000 ÷ creditUsdValue</c>; <paramref name="EffectiveMarginPercent"/> is what that
/// charges over the cost (null when the cost is zero).
/// </summary>
public record PriceResponse(Guid Id, string Unit, string? Variant, decimal CostUsdPerUnit,
    decimal? CreditsMillisPerUnitOverride, decimal EffectiveCreditsMillisPerUnit, decimal? EffectiveMarginPercent,
    DateTime EffectiveFrom, DateTime? EffectiveTo, string? Notes);

/// <summary><c>POST /</c>: a new provider account, optionally with its first models.</summary>
public record CreateProviderRequest(string? Name, string? Kind, string? Endpoint, string? ApiKey = null,
    int? TimeoutSeconds = null, bool IsEnabled = true, bool PersonalOAuthMode = false, List<CreateModelRequest>? Models = null);

/// <summary><c>POST /{id}/models</c> (and one entry of <see cref="CreateProviderRequest.Models"/>).</summary>
public record CreateModelRequest(string? ModelCode, string? DisplayName, bool IsEnabled = true,
    List<CreateCapabilityRequest>? Capabilities = null);

/// <summary><c>POST /{id}/models/{modelId}/capabilities</c> (and one entry of <see cref="CreateModelRequest.Capabilities"/>).</summary>
public record CreateCapabilityRequest(string? Capability, bool IsDefault = false, bool IsEnabled = true);

/// <summary>
/// <c>PUT /{id}</c>. Every member is optional (null = unchanged). <paramref name="ApiKey"/>: omitted keeps the stored
/// key, <c>""</c> clears it, anything else replaces it. The kind is fixed at creation.
/// </summary>
public record UpdateProviderRequest(string? Name = null, string? Endpoint = null, string? ApiKey = null,
    int? TimeoutSeconds = null, bool? IsEnabled = null, bool? PersonalOAuthMode = null);

/// <summary><c>PUT /{id}/models/{modelId}</c>. Null = unchanged.</summary>
public record UpdateModelRequest(string? ModelCode = null, string? DisplayName = null, bool? IsEnabled = null);

/// <summary><c>PUT /{id}/models/{modelId}/capabilities/{capId}</c>. <c>isDefault: true</c> clears the other defaults of the same (org, capability).</summary>
public record UpdateCapabilityRequest(bool? IsDefault = null, bool? IsEnabled = null);

/// <summary>
/// <c>POST /test</c>: an unsaved test. With <paramref name="ProviderId"/> and no <paramref name="ApiKey"/>, the saved
/// provider's key is reused, and with it that provider's kind, endpoint, timeout and OAuth mode (a stored key is never
/// sent anywhere else).
/// </summary>
public record TestProviderRequest(string? Kind = null, string? Endpoint = null, string? ApiKey = null,
    string? ModelCode = null, string? Capability = null, bool? PersonalOAuthMode = null, Guid? ProviderId = null);

/// <summary><c>POST …/capabilities/{capId}/prices</c> (platform only). <paramref name="Unit"/> is a <c>UsageKind</c> name.</summary>
public record CreatePriceRequest(string? Unit, string? Variant = null, decimal? CostUsdPerUnit = null,
    decimal? CreditsMillisPerUnitOverride = null, DateTime? EffectiveFrom = null, string? Notes = null);

/// <summary><c>GET /consolidation-report</c> (platform only): which providers share one account and would be merged.</summary>
public record ConsolidationReportResponse(List<ConsolidationGroupResponse> Groups);

public record ConsolidationGroupResponse(Guid SurvivorId, string SurvivorName, List<Guid> MergedProviderIds, int MergedModelCount);

public record TestInferenceProviderResponse(bool Ok, long LatencyMs, string? Error, string? ResponsePreview);

public record SetAgentInferenceProviderRequest(Guid? InferenceProviderId);

/// <summary>Null clears the stored value (the agent falls back to configuration, then its code default).</summary>
public record SetAgentReasoningEffortRequest(string? ReasoningEffort);

/// <summary>
/// <c>GET /api/v1/inference-providers/status?capability=…</c>: which provider a run would use for a
/// capability right now, and whether it is healthy — readable by ANY signed-in user, so it carries
/// no endpoint, model, key or error text, only the admin-chosen display name and plain-language facts.
/// </summary>
public record InferenceProviderStatusResponse(
    string Capability,
    string? ProviderName,
    bool? LastTestOk,
    DateTime? LastTestAt,
    bool Healthy,
    bool UsingFallback,
    string? Message);

/// <summary>
/// One row of <c>GET /api/v1/inference-providers/selectable</c>: one model capability a step editor may offer to ANY
/// org member (Billing excluded). <paramref name="Id"/> is the capability id, <paramref name="Name"/> the model's display
/// name, <paramref name="Model"/> its model code, <paramref name="ProviderName"/> the account's name. Deliberately
/// nothing else — no endpoint, key, health or error text — so a plain member never learns where an org's keys point.
/// <paramref name="CostIndex"/> is a relative cost (cheapest priced platform Chat model = 1.0, two decimals), a ratio of
/// platform prices over a fixed token mix; null for BYO, non-Chat and unpriced rows. Never a money amount.
/// </summary>
public record SelectableProviderResponse(Guid Id, string Name, string Capability, string Model, bool IsPlatformManaged,
    string Kind, string ProviderName, decimal? CostIndex = null);

/// <summary>Body of <c>PUT /api/v1/org/agent-overrides/{agentType}</c>.</summary>
public record SetOrgAgentOverrideRequest(Guid? InferenceProviderId);

public record OrgAgentOverrideResponse(string AgentType, Guid? InferenceProviderId, DateTime UpdatedAt);

/// <summary>
/// One row of <c>GET /api/v1/org/agent-models</c>: a built-in agent, the platform's recommended Chat model for it
/// (<paramref name="Why"/> only while that is the seeded model), the workspace's own choice, the model the agent really runs
/// on for this workspace, and <paramref name="Source"/> (<c>workspace</c> | <c>recommended</c> | <c>default</c>).
/// Ids are model capability ids the caller's workspace can use; names are model display names. No endpoint, key or price.
/// </summary>
public record OrgAgentModelResponse(
    string AgentType,
    Guid? RecommendedModelId,
    string? RecommendedModelName,
    string? Why,
    Guid? ChoiceModelId,
    Guid? EffectiveModelId,
    string? EffectiveModelName,
    string Source);
