using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>Whose providers an operation acts on: the platform's (null org) or one organization's.</summary>
public readonly record struct ProviderScope(Guid? OrganizationId)
{
    public bool IsOrg => OrganizationId.HasValue;
    public static ProviderScope Platform => new(null);
    public static ProviderScope Org(Guid organizationId) => new(organizationId);
}

public enum AdminStatus { Ok, Created, BadRequest, NotFound, Conflict, PaymentRequired }

/// <summary>Transport-neutral result; controllers map it to the exact <c>ActionResult</c> type they always returned.</summary>
public sealed record AdminOutcome<T>(AdminStatus Status, T? Value = default, object? Body = null)
{
    public static AdminOutcome<T> Ok(T value) => new(AdminStatus.Ok, value);
    public static AdminOutcome<T> Created(T value) => new(AdminStatus.Created, value);
    public static AdminOutcome<T> BadRequest(string error) => new(AdminStatus.BadRequest, default, new { error });
    public static AdminOutcome<T> NotFound(string? error = null) => new(AdminStatus.NotFound, default, error is null ? null : new { error });
    public static AdminOutcome<T> Conflict(string error) => new(AdminStatus.Conflict, default, new { error });

    /// <summary>
    /// a platform capability cannot be (left) enabled without a price. 400 <c>{message, errors}</c> (plus the
    /// <c>error</c> every other admin 400 carries, so existing clients show it).
    /// </summary>
    public static AdminOutcome<T> Unpriced(string message) => new(AdminStatus.BadRequest, default, new
    {
        error = message,
        message,
        errors = new Dictionary<string, string[]> { ["isEnabled"] = [message] }
    });
    public static AdminOutcome<T> PaymentRequired() => new(AdminStatus.PaymentRequired, default, new
    {
        error = InferenceProviderAdminService.ByoNotInPlanCode,
        message = "Your plan does not include bringing your own inference provider. Upgrade to add one."
    });
}

/// <summary>
/// The model catalog's admin logic (docs/design/model-catalog.md §6) behind both <c>/api/v1/inference-providers</c>
/// (platform admins, rows with a null organization) and <c>/api/v1/org/inference-providers</c> (org Owner/Admin, that
/// org's rows): provider ACCOUNTS (kind, endpoint, key, timeout) → their MODELS (model code, display name) → each model's
/// CAPABILITIES (default, enabled, last test) → on the platform, each capability's PRICES. Authority (who may call)
/// stays in the controllers; this class owns what a row may look like and keeps the resolver cache honest: every write
/// ends in <see cref="IInferenceProviderResolver.Invalidate"/>.
/// <para>
/// <b>Ownership chain.</b> Every id is loaded with an explicit organization predicate (the scope's org on every level)
/// AND its parent match (model under that provider, capability under that model, price under that capability); any
/// mismatch is a 404. The EF tenant filter is bypassed and never relied on.
/// </para>
/// <para>
/// Rules that differ for ORG rows (decision D3, docs/design/tenancy.md): no <c>env:</c> ambient-credential sentinel, no
/// <c>PersonalOAuthMode</c>, and NO private-address exemption. Creating or editing a BYO row (and testing one) needs the
/// BYO entitlement (402 <c>byo_not_in_plan</c>); listing, reading and deleting stay open so a downgraded org can clean
/// up. BYO capabilities are never priced.
/// </para>
/// <para>
/// The system compute provider (<see cref="InferenceProvider.IsSystem"/>) is listed but read-only: its account, models
/// and capabilities answer 409 to every write; only its prices are editable.
/// </para>
/// </summary>
public sealed partial class InferenceProviderAdminService
{
    public const string ByoNotInPlanCode = "byo_not_in_plan";

    private static readonly string[] AmbientSentinels =
    [
        ChatClientFactory.AnthropicAmbientCredentialSentinel,
        ChatClientFactory.GeminiAmbientCredentialSentinel,
        ChatClientFactory.DeepSeekAmbientCredentialSentinel
    ];

    /// <summary>Stable 400 code: the endpoint's origin changed but the request did not say what to do with the stored key.</summary>
    public const string ApiKeyRequiredForEndpointChangeCode = "API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE";

    internal const string ApiKeyRequiredForEndpointChangeMessage =
        "The endpoint's host or the provider kind changed: re-enter the key or tick Remove the stored key. "
        + "A stored key is never sent to a new host (API: send apiKey, or \"\" to clear it).";

    /// <summary>Stable 400 code: the <c>env:</c> server-credential key was paired with a host that is not the kind's vendor.</summary>
    public const string AmbientKeyEndpointNotAllowedCode = "AMBIENT_KEY_ENDPOINT_NOT_ALLOWED";

    internal const string AmbientKeyEndpointNotAllowedMessage =
        "The 'env:' key uses the server's own credential, so it is only allowed with an empty endpoint or the provider's "
        + "official endpoint. Enter the key itself to use another endpoint.";

    /// <summary>
    /// The vendor host each kind's ambient (<c>env:</c>) credential belongs to. A kind not listed has no vendor host,
    /// so <c>env:</c> is only accepted there with an empty endpoint (where it is merely inert).
    /// </summary>
    private static readonly Dictionary<InferenceProviderKind, string> AmbientVendorHosts = new()
    {
        [InferenceProviderKind.Anthropic] = "api.anthropic.com",
        [InferenceProviderKind.Gemini] = "generativelanguage.googleapis.com",
        [InferenceProviderKind.DeepSeek] = "api.deepseek.com",
        [InferenceProviderKind.Higgsfield] = "api.higgsfield.ai",
        [InferenceProviderKind.ElevenLabs] = "api.elevenlabs.io",
    };

    private const string PublicEndpointMessage =
        "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed.";

    internal const string SystemProviderMessage =
        "The built-in compute service is managed by the platform: it cannot be changed or deleted, only priced.";

    /// <summary>
    /// The longest NEW provider name, model display name or model code the admin API and the assistant tools accept.
    /// The columns themselves are unbounded text, so a longer legacy value still migrates and still loads.
    /// </summary>
    internal const int MaxNameLength = 200;

    private readonly InferenceApiDbContext _db;
    private readonly ISecretProtector _secretProtector;
    private readonly InferenceProviderTester _tester;
    private readonly IInferenceProviderResolver? _resolver;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly PriceBook _priceBook;
    private readonly IModelPriceService? _prices;
    private readonly TimeProvider _time;

    public InferenceProviderAdminService(
        InferenceApiDbContext db,
        ISecretProtector secretProtector,
        InferenceProviderTester tester,
        IInferenceProviderResolver? resolver = null,
        IOrganizationEntitlements? entitlements = null,
        PriceBook? priceBook = null,
        IModelPriceService? prices = null,
        TimeProvider? time = null)
    {
        _db = db;
        _secretProtector = secretProtector;
        _tester = tester;
        _resolver = resolver;
        _entitlements = entitlements;
        _prices = prices;
        _time = time ?? TimeProvider.System;
        _priceBook = priceBook ?? new PriceBook(db, prices, _time);
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>True for the literal ambient-credential sentinel (case-insensitive, trimmed).</summary>
    public static bool IsAmbientSentinel(string? apiKey) =>
        apiKey is not null && AmbientSentinels.Any(s => string.Equals(apiKey.Trim(), s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Endpoint gate: platform rows keep the kind-aware exemption; org rows never get it.</summary>
    internal static bool IsEndpointDisallowed(ProviderScope scope, string endpoint, InferenceProviderKind kind) =>
        scope.IsOrg
            ? InferenceProvidersController.IsDisallowedEndpoint(endpoint)
            : InferenceProvidersController.IsDisallowedEndpoint(endpoint, kind);

    /// <summary>Higgsfield is priced by the vendor's quote; every other kind by its price table.</summary>
    internal static ModelPriceSource PriceSourceFor(InferenceProviderKind kind) =>
        kind == InferenceProviderKind.Higgsfield ? ModelPriceSource.Oracle : ModelPriceSource.Table;

    // ------------------------------------------------------------------ scoped loads (explicit org + parent match)

    private IQueryable<InferenceProvider> Providers(ProviderScope scope) => _db.InferenceProviders
        .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
        .Where(p => p.OrganizationId == scope.OrganizationId);

    private IQueryable<InferenceModel> Models(ProviderScope scope) => _db.InferenceModels
        .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
        .Where(m => m.OrganizationId == scope.OrganizationId);

    private IQueryable<ModelCapability> Capabilities(ProviderScope scope) => _db.ModelCapabilities
        .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
        .Where(c => c.OrganizationId == scope.OrganizationId);

    private Task<InferenceProvider?> FindProviderAsync(ProviderScope scope, Guid id, CancellationToken ct) =>
        Providers(scope).FirstOrDefaultAsync(p => p.Id == id, ct);

    private Task<InferenceModel?> FindModelAsync(ProviderScope scope, Guid id, Guid modelId, CancellationToken ct) =>
        Models(scope)
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId && m.ProviderId == id && m.Provider.OrganizationId == scope.OrganizationId, ct);

    private Task<ModelCapability?> FindCapabilityAsync(ProviderScope scope, Guid id, Guid modelId, Guid capId, CancellationToken ct) =>
        Capabilities(scope)
            .Include(c => c.Model)
            .ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(c => c.Id == capId && c.ModelId == modelId
                                      && c.Model.ProviderId == id
                                      && c.Model.OrganizationId == scope.OrganizationId
                                      && c.Model.Provider.OrganizationId == scope.OrganizationId, ct);

    // ------------------------------------------------------------------ helpers

    private void Invalidate(ProviderScope scope)
    {
        // A platform row is part of EVERY org's snapshot, so a platform write drops them all.
        if (scope.IsOrg) _resolver?.Invalidate(scope.OrganizationId);
        else _resolver?.InvalidateAll();
    }

    private async Task<bool> ByoDeniedAsync(ProviderScope scope, CancellationToken ct) =>
        scope.IsOrg && _entitlements is not null &&
        !await _entitlements.AllowsByoProvidersAsync(scope.OrganizationId!.Value, ct);

    private static string? OrgRowRejection(ProviderScope scope, bool personalOAuthMode, string? apiKey)
    {
        if (!scope.IsOrg) return null;
        if (personalOAuthMode)
            return "PersonalOAuthMode is not available for organization providers. Use a Console API key.";
        if (IsAmbientSentinel(apiKey))
            return "The 'env:' ambient-credential key is not available for organization providers; supply the key itself.";
        return null;
    }

    /// <summary>
    /// The rules of a LocalRunner write: organization-owned only, no key, and an endpoint that names a non-revoked
    /// desktop runner of the SAME organization. Null when the write may proceed.
    /// </summary>
    private async Task<string?> LocalRunnerProblemAsync(ProviderScope scope, string endpoint, string? apiKey, CancellationToken ct)
    {
        if (LocalRunnerProviderRules.ScopeProblem(scope) is { } scopeProblem) return scopeProblem;
        if (LocalRunnerProviderRules.ApiKeyProblem(apiKey) is { } keyProblem) return keyProblem;
        return await LocalRunnerProviderRules.DeviceProblemAsync(_db, scope.OrganizationId!.Value, endpoint, ct);
    }

    private Task<bool> ProviderNameTakenAsync(ProviderScope scope, string name, Guid? exceptId, CancellationToken ct) =>
        Providers(scope).AnyAsync(p => p.Name == name && (exceptId == null || p.Id != exceptId), ct);

    private Task<bool> ModelNameTakenAsync(ProviderScope scope, string name, Guid? exceptId, CancellationToken ct) =>
        Models(scope).AnyAsync(m => m.DisplayName == name && (exceptId == null || m.Id != exceptId), ct);

    private static string InvalidCapabilityMessage(string? value) =>
        $"Invalid capability '{value}'. Expected one of: {string.Join(", ", Enum.GetNames<InferenceProviderCapability>().Where(n => n != nameof(InferenceProviderCapability.Compute)).Select(n => $"'{n}'"))}.";

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };

    private static bool TryParseKnownKind(string? value, out InferenceProviderKind kind) =>
        InferenceProvidersController.TryParseKind(value, out kind) && Enum.IsDefined(kind)
        && !int.TryParse(value, out _);

    /// <summary>
    /// The rules one capability of a model must pass: a known capability, never <c>Compute</c> (served only by the
    /// system provider), a kind that can serve it and a model code that can serve it.
    /// </summary>
    private static string? CapabilityProblem(
        string? value, InferenceProviderKind kind, string modelCode, string? endpoint, out InferenceProviderCapability capability)
    {
        capability = InferenceProviderCapability.Chat;
        if (string.IsNullOrWhiteSpace(value))
            return "Capability is required.";
        if (!InferenceProvidersController.TryParseCapability(value, out capability) || !Enum.IsDefined(capability)
            || int.TryParse(value, out _))
            return InvalidCapabilityMessage(value);
        if (capability == InferenceProviderCapability.Compute)
            return "The Compute capability belongs to the platform's built-in compute service and cannot be added to a provider.";
        if (InferenceProvidersController.IsUnsupportedCombination(kind, capability, endpoint, out string unsupported))
            return unsupported;
        if (InferenceProvidersController.IsUnsupportedModel(kind, capability, modelCode, out string unsupportedModel))
            return unsupportedModel;
        return null;
    }

    private sealed record ValidModel(string ModelCode, string DisplayName, bool IsEnabled,
        List<(InferenceProviderCapability Capability, bool IsDefault, bool IsEnabled)> Capabilities);

    /// <summary>Validates one model (and its capabilities) for a provider of <paramref name="kind"/>.</summary>
    private static string? ModelProblem(
        CreateModelRequest? request, InferenceProviderKind kind, string? endpoint, out ValidModel? model)
    {
        model = null;
        if (request is null) return "A model is required.";
        string code = request.ModelCode?.Trim() ?? string.Empty;
        if (code.Length == 0) return "ModelCode is required.";
        if (code.Length > MaxNameLength) return $"ModelCode must be at most {MaxNameLength} characters.";
        string display = string.IsNullOrWhiteSpace(request.DisplayName) ? code : request.DisplayName.Trim();
        if (display.Length > MaxNameLength) return $"DisplayName must be at most {MaxNameLength} characters.";

        List<(InferenceProviderCapability, bool, bool)> capabilities = [];
        foreach (CreateCapabilityRequest? c in request.Capabilities ?? [])
        {
            if (c is null) return "Capability is required.";
            if (CapabilityProblem(c.Capability, kind, code, endpoint, out InferenceProviderCapability capability) is { } problem)
                return problem;
            if (capabilities.Any(x => x.Item1 == capability))
                return $"The model '{display}' lists the {capability} capability twice; a model serves each capability once.";
            capabilities.Add((capability, c.IsDefault, c.IsEnabled));
        }

        model = new ValidModel(code, display, request.IsEnabled, capabilities);
        return null;
    }

    private static InferenceModel NewModel(InferenceProvider provider, ValidModel valid, DateTime now)
    {
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            OrganizationId = provider.OrganizationId,
            ModelCode = valid.ModelCode,
            DisplayName = valid.DisplayName,
            IsEnabled = valid.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now,
            Provider = provider
        };
        foreach ((InferenceProviderCapability capability, bool isDefault, bool isEnabled) in valid.Capabilities)
        {
            model.Capabilities.Add(NewCapability(model, provider.Kind, capability, isDefault, isEnabled, now));
        }

        return model;
    }

    private static ModelCapability NewCapability(
        InferenceModel model, InferenceProviderKind kind, InferenceProviderCapability capability, bool isDefault, bool isEnabled, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            OrganizationId = model.OrganizationId,
            Capability = capability,
            IsDefault = isDefault,
            IsEnabled = isEnabled,
            PriceSource = PriceSourceFor(kind),
            CreatedAt = now,
            UpdatedAt = now,
            Model = model
        };

    /// <summary>
    /// Clears <see cref="ModelCapability.IsDefault"/> on every other capability of the same (org, capability). Loaded
    /// then SAVED here, before the caller flips its own row, inside the caller's transaction: the (org, capability)
    /// partial unique index is checked per statement, so the old default must be cleared first. Not ExecuteUpdate,
    /// which the InMemory provider the tests use does not support.
    /// </summary>
    private async Task ClearOtherDefaultsAsync(ProviderScope scope, InferenceProviderCapability capability, Guid? exceptId, CancellationToken ct)
    {
        List<ModelCapability> others = await Capabilities(scope)
            .Where(c => c.IsDefault && c.Capability == capability && (exceptId == null || c.Id != exceptId))
            .ToListAsync(ct);
        if (others.Count == 0) return;
        foreach (ModelCapability other in others) other.IsDefault = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One transaction: first clears (and saves) the other defaults of every capability in <paramref name="defaults"/>,
    /// THEN runs <paramref name="apply"/> (which adds the new rows or flips the default) and saves it. The order is the
    /// point: the new default must not reach the database in the same statement batch as the old one it replaces.
    /// </summary>
    private async Task<bool> SaveWithDefaultsAsync(
        ProviderScope scope, IEnumerable<(InferenceProviderCapability Capability, Guid Id)> defaults, Action apply, CancellationToken ct)
    {
        List<(InferenceProviderCapability Capability, Guid Id)> clear = [.. defaults];
        await using IDbContextTransaction tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach ((InferenceProviderCapability capability, Guid id) in clear)
            {
                await ClearOtherDefaultsAsync(scope, capability, id, ct);
            }

            apply();
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Pre-fills the prices of new PLATFORM capabilities from the price book (BYO is never priced). While billing is on,
    /// a new enabled capability that is still unpriced afterwards (the book had nothing, and it is not an
    /// oracle-priced one whose oracle answers) is switched OFF, so the response shows it disabled and it is never offered.
    /// </summary>
    private async Task AutoPriceAsync(ProviderScope scope, IEnumerable<Guid> capabilityIds, CancellationToken ct)
    {
        if (scope.IsOrg) return;
        List<Guid> ids = [.. capabilityIds];
        foreach (Guid id in ids)
        {
            await _priceBook.AutoPriceAsync(id, ct);
        }

        if (!UnpricedCapabilityGate.Applies(_resolver) || ids.Count == 0) return;

        List<ModelCapability> created = await Capabilities(scope)
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Where(c => ids.Contains(c.Id) && c.IsEnabled)
            .ToListAsync(ct);
        bool switchedOff = false;
        foreach (ModelCapability capability in created)
        {
            if (!await UnpricedCapabilityGate.BlocksAsync(_db, _tester, capability, Now, ct)) continue;
            capability.IsEnabled = false;
            capability.UpdatedAt = Now;
            switchedOff = true;
        }

        if (!switchedOff) return;
        await _db.SaveChangesAsync(ct);
        Invalidate(scope);
    }

    // ------------------------------------------------------------------ reads

    public async Task<List<ProviderResponse>> ListAsync(ProviderScope scope, CancellationToken ct) =>
        await LoadTreeAsync(scope, providerId: null, ct);

    public async Task<AdminOutcome<ProviderResponse>> GetAsync(ProviderScope scope, Guid id, CancellationToken ct)
    {
        List<ProviderResponse> tree = await LoadTreeAsync(scope, id, ct);
        return tree.Count == 0
            ? AdminOutcome<ProviderResponse>.NotFound()
            : AdminOutcome<ProviderResponse>.Ok(tree[0]);
    }

    /// <summary>The scope's providers (one, when <paramref name="providerId"/> is set) with their models, capabilities and, on the platform, prices.</summary>
    private async Task<List<ProviderResponse>> LoadTreeAsync(ProviderScope scope, Guid? providerId, CancellationToken ct)
    {
        List<InferenceProvider> providers = await Providers(scope).AsNoTracking()
            .Where(p => providerId == null || p.Id == providerId)
            .ToListAsync(ct);
        if (providers.Count == 0) return [];

        List<Guid> providerIds = [.. providers.Select(p => p.Id)];
        List<InferenceModel> models = await Models(scope).AsNoTracking()
            .Where(m => providerIds.Contains(m.ProviderId))
            .ToListAsync(ct);
        List<Guid> modelIds = [.. models.Select(m => m.Id)];
        List<ModelCapability> capabilities = await Capabilities(scope).AsNoTracking()
            .Where(c => modelIds.Contains(c.ModelId))
            .ToListAsync(ct);

        PriceContext? prices = scope.IsOrg ? null : await LoadPricesAsync(capabilities.Select(c => c.Id), ct);

        ILookup<Guid, ModelCapability> capsByModel = capabilities.ToLookup(c => c.ModelId);
        ILookup<Guid, InferenceModel> modelsByProvider = models.ToLookup(m => m.ProviderId);
        return providers
            .OrderBy(p => p.IsSystem)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => MapProvider(p, modelsByProvider[p.Id]
                .OrderBy(m => m.DisplayName, StringComparer.Ordinal)
                .Select(m => MapModel(m, capsByModel[m.Id], prices))))
            .ToList();
    }

    private async Task<ModelResponse> LoadModelResponseAsync(ProviderScope scope, Guid modelId, CancellationToken ct)
    {
        InferenceModel model = await Models(scope).AsNoTracking().SingleAsync(m => m.Id == modelId, ct);
        List<ModelCapability> capabilities = await Capabilities(scope).AsNoTracking().Where(c => c.ModelId == modelId).ToListAsync(ct);
        PriceContext? prices = scope.IsOrg ? null : await LoadPricesAsync(capabilities.Select(c => c.Id), ct);
        return MapModel(model, capabilities, prices);
    }

    private async Task<CapabilityResponse> LoadCapabilityResponseAsync(ProviderScope scope, Guid capId, CancellationToken ct)
    {
        ModelCapability capability = await Capabilities(scope).AsNoTracking().SingleAsync(c => c.Id == capId, ct);
        PriceContext? prices = scope.IsOrg ? null : await LoadPricesAsync([capId], ct);
        return MapCapability(capability, prices);
    }

    internal static ProviderResponse MapProvider(InferenceProvider p, IEnumerable<ModelResponse> models) => new(
        p.Id, p.OrganizationId, p.Name, p.Kind.ToString(), p.Endpoint,
        !string.IsNullOrEmpty(p.ApiKeyEncrypted), p.ApiKeyLastFour, p.IsEnabled, p.IsSystem, p.TimeoutSeconds,
        p.PersonalOAuthMode, p.CreatedAt, p.UpdatedAt, [.. models]);

    private static ModelResponse MapModel(InferenceModel m, IEnumerable<ModelCapability> capabilities, PriceContext? prices) => new(
        m.Id, m.ModelCode, m.DisplayName, m.IsEnabled,
        [.. capabilities.OrderBy(c => c.Capability).Select(c => MapCapability(c, prices))]);

    private static CapabilityResponse MapCapability(ModelCapability c, PriceContext? prices) => new(
        c.Id, c.Capability.ToString(), c.IsDefault, c.IsEnabled, c.PriceSource.ToString(),
        c.LastTestAt, c.LastTestOk, c.LastTestError,
        prices is null ? null : [.. prices.For(c.Id)]);

    /// <summary>
    /// Enabled model capabilities an org member may pick in a step editor: every platform one plus the org's own (the
    /// latter only while the plan still allows BYO — the resolver drops them otherwise, so offering them would be a dead
    /// choice). The system compute provider is never offered. While billing is on neither is a table-priced
    /// platform capability that is not fully priced (T-040: some metered unit has no price in force), exactly the rows the
    /// resolver treats as absent. Nothing secret or topological
    /// leaves here.
    /// </summary>
    public async Task<List<SelectableProviderResponse>> ListSelectableAsync(
        Guid organizationId, InferenceProviderCapability? capability, CancellationToken ct)
    {
        List<ModelCapabilityView> offered = await SelectableRowsAsync(_db, _entitlements, _resolver, organizationId, capability, Now, ct);
        IReadOnlyDictionary<Guid, decimal> costIndex = offered.Any(p => p.OrganizationId == null && p.Capability == InferenceProviderCapability.Chat)
            ? await ChatCostIndexAsync(ct)
            : new Dictionary<Guid, decimal>();
        return offered
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new SelectableProviderResponse(
                p.Id, p.Name, p.Capability.ToString(), p.ModelName, p.OrganizationId == null, p.Kind.ToString(), p.ProviderName,
                costIndex.TryGetValue(p.Id, out decimal index) ? index : null))
            .ToList();
    }

    /// <summary>
    /// The rows <see cref="ListSelectableAsync"/> offers, with nothing mapped: the one definition of "a model this organization
    /// may pick", shared with the app-input model rules (docs/design/app-inputs.md 8.3) so the page, the run gate and the step
    /// editor can never disagree about what is selectable.
    /// </summary>
    internal static async Task<List<ModelCapabilityView>> SelectableRowsAsync(
        InferenceApiDbContext db, IOrganizationEntitlements? entitlements, IInferenceProviderResolver? resolver,
        Guid organizationId, InferenceProviderCapability? capability, DateTime now, CancellationToken ct)
    {
        bool byo = entitlements is null || await entitlements.AllowsByoProvidersAsync(organizationId, ct);
        bool pricedOnly = UnpricedCapabilityGate.Applies(resolver);
        List<ModelCapabilityView> rows = await db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(c => c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled
                        && !c.Model.Provider.IsSystem
                        && c.Capability != InferenceProviderCapability.Compute
                        && (capability == null || c.Capability == capability)
                        && (c.OrganizationId == null || (byo && c.OrganizationId == organizationId)))
            .Select(ModelCapabilityView.Projection)
            .ToListAsync(ct);

        // T-040: the resolver's own rule (ModelCapabilityView.IsFullyPriced): a table-priced platform row is offered only
        // with a price in force for every unit it meters, not just any one. BYO and oracle-priced rows are never in it.
        IReadOnlySet<Guid> unpriced = pricedOnly
            ? await ModelCapabilityView.UnpricedPlatformIdsAsync(
                db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                    .Where(c => capability == null || c.Capability == capability), now, ct)
            : new HashSet<Guid>();
        return [.. rows.Where(p => !unpriced.Contains(p.Id))];
    }

    /// <summary>Tokens of the fixed mix every platform Chat model is priced over: 1M input and a quarter of that out.</summary>
    private const decimal CostMixInputTokens = 1_000_000m;

    private const decimal CostMixOutputTokens = 250_000m;

    /// <summary>
    /// The relative cost of every priced, enabled PLATFORM Chat model: its price (in credits, billing mode Platform) for
    /// <see cref="CostMixInputTokens"/> input plus <see cref="CostMixOutputTokens"/> output tokens, over the cheapest such
    /// price among ALL of them, so the cheapest is 1.0. Rounded to two decimals. A model the price book cannot price is
    /// absent (it has no index and cannot be the baseline); so is everything when there is no price service or the cheapest
    /// costs nothing. Ratios only: the money never leaves this method.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, decimal>> ChatCostIndexAsync(CancellationToken ct)
    {
        Dictionary<Guid, decimal> index = [];
        if (_prices is null) return index;

        var chats = await _db.ModelCapabilities
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .AsNoTracking()
            .Where(c => c.OrganizationId == null && c.Capability == InferenceProviderCapability.Chat
                        && c.IsEnabled && c.Model.IsEnabled && c.Model.Provider.IsEnabled && !c.Model.Provider.IsSystem)
            .Select(c => new { c.Id, c.Model.Provider.Kind, c.Model.ModelCode })
            .ToListAsync(ct);

        Dictionary<Guid, decimal> price = [];
        foreach (var chat in chats)
        {
            try
            {
                PriceQuote input = await _prices.PriceAsync(MixRequest(chat.Id, chat.Kind, chat.ModelCode, UsageKind.LlmInputTokens, CostMixInputTokens), ct);
                PriceQuote output = await _prices.PriceAsync(MixRequest(chat.Id, chat.Kind, chat.ModelCode, UsageKind.LlmOutputTokens, CostMixOutputTokens), ct);
                price[chat.Id] = input.CreditsMillis + (decimal)output.CreditsMillis;
            }
            catch (UnpricedUsageException)
            {
                // Not priced: no index, not a baseline.
            }
        }

        decimal cheapest = price.Count == 0 ? 0m : price.Values.Min();
        if (cheapest <= 0m) return index;
        foreach ((Guid id, decimal value) in price)
            index[id] = Math.Round(value / cheapest, 2, MidpointRounding.AwayFromZero);
        return index;
    }

    private PriceRequest MixRequest(Guid capabilityId, InferenceProviderKind kind, string modelCode, UsageKind unit, decimal quantity) =>
        new(capabilityId, null, kind, InferenceProviderCapability.Chat, modelCode, unit, null, UsageBillingMode.Platform, quantity, Now);

    // ------------------------------------------------------------------ providers

    public async Task<AdminOutcome<ProviderResponse>> CreateProviderAsync(
        ProviderScope scope, CreateProviderRequest request, CancellationToken ct)
    {
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<ProviderResponse>.PaymentRequired();

        string name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return AdminOutcome<ProviderResponse>.BadRequest("Name is required.");
        if (name.Length > MaxNameLength)
            return AdminOutcome<ProviderResponse>.BadRequest($"Name must be at most {MaxNameLength} characters.");
        if (string.IsNullOrWhiteSpace(request.Endpoint))
            return AdminOutcome<ProviderResponse>.BadRequest("Endpoint is required.");
        string endpoint = request.Endpoint.Trim();

        if (!TryParseKnownKind(request.Kind, out InferenceProviderKind kind))
            return AdminOutcome<ProviderResponse>.BadRequest(
                $"Invalid kind '{request.Kind}'. Expected one of {InferenceProvidersController.KnownKinds}.");

        if (kind == InferenceProviderKind.LocalRunner)
        {
            if (await LocalRunnerProblemAsync(scope, endpoint, request.ApiKey, ct) is { } localProblem)
                return AdminOutcome<ProviderResponse>.BadRequest(localProblem);
            endpoint = LocalRunnerProviderRules.Normalize(endpoint);
        }
        else if (IsEndpointDisallowed(scope, endpoint, kind))
            return AdminOutcome<ProviderResponse>.BadRequest(PublicEndpointMessage);

        if (request.TimeoutSeconds is <= 0)
            return AdminOutcome<ProviderResponse>.BadRequest("TimeoutSeconds must be a positive number when provided.");

        if (InferenceProvidersController.IsPersonalOAuthModeUnsupported(kind, request.PersonalOAuthMode, out string unsupportedOAuthMode))
            return AdminOutcome<ProviderResponse>.BadRequest(unsupportedOAuthMode);

        if (OrgRowRejection(scope, request.PersonalOAuthMode, request.ApiKey) is { } orgRejection)
            return AdminOutcome<ProviderResponse>.BadRequest(orgRejection);

        if (InferenceProvidersController.ApiKeyProblem(request.ApiKey) is { } keyProblem)
            return AdminOutcome<ProviderResponse>.BadRequest(keyProblem);

        if (AmbientKeyEndpointProblem(kind, endpoint, request.ApiKey) is { } ambient)
            return Refusal<ProviderResponse>(ambient);

        List<ValidModel> models = [];
        foreach (CreateModelRequest? m in request.Models ?? [])
        {
            if (ModelProblem(m, kind, endpoint, out ValidModel? valid) is { } problem)
                return AdminOutcome<ProviderResponse>.BadRequest(problem);
            if (models.Any(x => x.DisplayName == valid!.DisplayName))
                return AdminOutcome<ProviderResponse>.BadRequest($"Two models are named '{valid!.DisplayName}'; model names must differ.");
            models.Add(valid!);
        }

        if (models.SelectMany(m => m.Capabilities).Where(c => c.IsDefault).GroupBy(c => c.Capability).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return AdminOutcome<ProviderResponse>.BadRequest($"Only one model can be the default for {twice.Key}.");

        if (await ProviderNameTakenAsync(scope, name, null, ct))
            return AdminOutcome<ProviderResponse>.Conflict($"A provider named '{name}' already exists.");
        foreach (ValidModel m in models)
        {
            if (await ModelNameTakenAsync(scope, m.DisplayName, null, ct))
                return AdminOutcome<ProviderResponse>.Conflict($"A model named '{m.DisplayName}' already exists.");
        }

        DateTime now = Now;
        InferenceProvider provider = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = scope.OrganizationId,
            Name = name,
            Kind = kind,
            Endpoint = endpoint,
            IsEnabled = request.IsEnabled,
            TimeoutSeconds = request.TimeoutSeconds,
            PersonalOAuthMode = request.PersonalOAuthMode,
            CreatedAt = now,
            UpdatedAt = now
        };
        InferenceProvidersController.ApplyApiKey(provider, request.ApiKey, _secretProtector);
        List<InferenceModel> created = [.. models.Select(m => NewModel(provider, m, now))];
        List<ModelCapability> capabilities = [.. created.SelectMany(m => m.Capabilities)];

        if (!await SaveWithDefaultsAsync(scope, capabilities.Where(c => c.IsDefault).Select(c => (c.Capability, c.Id)), () =>
            {
                _db.InferenceProviders.Add(provider);
                _db.InferenceModels.AddRange(created);
                _db.ModelCapabilities.AddRange(capabilities);
            }, ct))
            return AdminOutcome<ProviderResponse>.Conflict($"A provider or model with one of those names already exists.");

        Invalidate(scope);
        await AutoPriceAsync(scope, capabilities.Select(c => c.Id), ct);
        return AdminOutcome<ProviderResponse>.Created((await LoadTreeAsync(scope, provider.Id, ct))[0]);
    }

    public async Task<AdminOutcome<ProviderResponse>> UpdateProviderAsync(
        ProviderScope scope, Guid id, UpdateProviderRequest request, CancellationToken ct)
    {
        InferenceProvider? provider = await FindProviderAsync(scope, id, ct);
        if (provider is null) return AdminOutcome<ProviderResponse>.NotFound();
        if (provider.IsSystem) return AdminOutcome<ProviderResponse>.Conflict(SystemProviderMessage);
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<ProviderResponse>.PaymentRequired();

        // Everything is judged before anything is written to the tracked entity, so a refused update leaves nothing
        // behind for a later SaveChanges on the same context.
        string? name = request.Name?.Trim();
        if (name is not null)
        {
            if (name.Length == 0) return AdminOutcome<ProviderResponse>.BadRequest("Name cannot be empty.");
            if (name.Length > MaxNameLength)
                return AdminOutcome<ProviderResponse>.BadRequest($"Name must be at most {MaxNameLength} characters.");
        }

        string? endpoint = request.Endpoint?.Trim();
        if (endpoint is not null)
        {
            if (endpoint.Length == 0) return AdminOutcome<ProviderResponse>.BadRequest("Endpoint cannot be empty.");
            if (provider.Kind == InferenceProviderKind.LocalRunner)
            {
                if (await LocalRunnerProblemAsync(scope, endpoint, request.ApiKey, ct) is { } localProblem)
                    return AdminOutcome<ProviderResponse>.BadRequest(localProblem);
                endpoint = LocalRunnerProviderRules.Normalize(endpoint);
            }
            else if (IsEndpointDisallowed(scope, endpoint, provider.Kind))
                return AdminOutcome<ProviderResponse>.BadRequest(PublicEndpointMessage);
        }

        // A WebSearch capability on an OpenAICompatible account is valid only at api.openai.com (as on create), so
        // pointing such an account elsewhere is refused. Judged before anything is written.
        if (endpoint is not null && provider.Kind == InferenceProviderKind.OpenAICompatible
            && await Capabilities(scope).AnyAsync(
                c => c.Model.ProviderId == provider.Id && c.Capability == InferenceProviderCapability.WebSearch, ct)
            && InferenceProvidersController.IsUnsupportedCombination(
                provider.Kind, InferenceProviderCapability.WebSearch, endpoint, out string unsupportedEndpoint))
            return AdminOutcome<ProviderResponse>.BadRequest(unsupportedEndpoint);

        if (provider.Kind == InferenceProviderKind.LocalRunner && endpoint is null
            && LocalRunnerProviderRules.ApiKeyProblem(request.ApiKey) is { } localKeyProblem)
            return AdminOutcome<ProviderResponse>.BadRequest(localKeyProblem);

        bool personalOAuthMode = request.PersonalOAuthMode ?? provider.PersonalOAuthMode;
        if (InferenceProvidersController.IsPersonalOAuthModeUnsupported(provider.Kind, personalOAuthMode, out string unsupportedOAuthMode))
            return AdminOutcome<ProviderResponse>.BadRequest(unsupportedOAuthMode);

        if (OrgRowRejection(scope, personalOAuthMode, request.ApiKey) is { } orgRejection)
            return AdminOutcome<ProviderResponse>.BadRequest(orgRejection);

        if (InferenceProvidersController.ApiKeyProblem(request.ApiKey) is { } keyProblem)
            return AdminOutcome<ProviderResponse>.BadRequest(keyProblem);

        // A stored key (including a platform `env:` reference, which resolves to a real server secret) never follows
        // the endpoint to another scheme/host/port: the request must carry a new key or clear it ("").
        if (StoredKeyFollowProblem(provider.Kind, provider.Endpoint, !string.IsNullOrEmpty(provider.ApiKeyEncrypted),
                null, endpoint, request.ApiKey) is { } follow)
            return Refusal<ProviderResponse>(follow);
        if (AmbientKeyEndpointProblem(provider.Kind, endpoint ?? provider.Endpoint, request.ApiKey) is { } ambient)
            return Refusal<ProviderResponse>(ambient);

        if (request.TimeoutSeconds is <= 0)
            return AdminOutcome<ProviderResponse>.BadRequest("TimeoutSeconds must be a positive number.");

        if (name is not null && await ProviderNameTakenAsync(scope, name, provider.Id, ct))
            return AdminOutcome<ProviderResponse>.Conflict($"A provider named '{name}' already exists.");

        // As UpdateCapability: switching the account on must not make an unpriced platform capability of
        // one of its enabled models usable. Judged before anything is written.
        if (request.IsEnabled == true && !scope.IsOrg && UnpricedCapabilityGate.Applies(_resolver)
            && await UnpricedCapabilityGate.FirstExposedAsync(_db, _tester, await Capabilities(scope)
                    .Include(c => c.Model).ThenInclude(m => m.Provider)
                    .Where(c => c.Model.ProviderId == provider.Id && c.Model.IsEnabled)
                    .ToListAsync(ct), Now, ct) is { } exposedByProvider)
            return AdminOutcome<ProviderResponse>.Unpriced(
                UnpricedCapabilityGate.ExposesRefusalMessage($"The provider account '{provider.Name}'", exposedByProvider));

        // T-037: a self-hosted default priced at zero must not be pointed at a hosted endpoint (judged before any write).
        if (await SelfHostedZeroPriceGate.RefusalAsync(_db, _resolver, provider.Id, provider.Name, provider.Kind, null,
                provider.Endpoint, endpoint, Now, ct) is { } zeroRefusal)
            return AdminOutcome<ProviderResponse>.Conflict(zeroRefusal);

        if (name is not null) provider.Name = name;
        if (endpoint is not null) provider.Endpoint = endpoint;
        provider.PersonalOAuthMode = personalOAuthMode;
        if (request.TimeoutSeconds.HasValue) provider.TimeoutSeconds = request.TimeoutSeconds;
        if (request.IsEnabled.HasValue) provider.IsEnabled = request.IsEnabled.Value;
        // null/absent = unchanged, "" = clear, anything else = replace.
        InferenceProvidersController.ApplyApiKey(provider, request.ApiKey, _secretProtector);
        provider.UpdatedAt = Now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return AdminOutcome<ProviderResponse>.Conflict($"A provider named '{name}' already exists.");
        }

        Invalidate(scope);
        return AdminOutcome<ProviderResponse>.Ok((await LoadTreeAsync(scope, provider.Id, ct))[0]);
    }

    public async Task<AdminOutcome<object>> DeleteProviderAsync(ProviderScope scope, Guid id, CancellationToken ct)
    {
        InferenceProvider? provider = await FindProviderAsync(scope, id, ct);
        if (provider is null) return AdminOutcome<object>.NotFound();
        if (provider.IsSystem) return AdminOutcome<object>.Conflict(SystemProviderMessage);

        List<InferenceModel> models = await Models(scope).Where(m => m.ProviderId == provider.Id).ToListAsync(ct);
        List<Guid> modelIds = [.. models.Select(m => m.Id)];
        List<ModelCapability> capabilities = await Capabilities(scope).Where(c => modelIds.Contains(c.ModelId)).ToListAsync(ct);
        if (DefaultConflict(capabilities, models) is { } conflict) return AdminOutcome<object>.Conflict(conflict);

        // Cascades down (models, capabilities, prices); agents, voices and overrides referencing a capability fall
        // back to the default (OnDelete(SetNull) on their FKs).
        await RemoveCapabilitiesAsync(capabilities, ct);
        _db.InferenceModels.RemoveRange(models);
        _db.InferenceProviders.Remove(provider);
        await _db.SaveChangesAsync(ct);
        AfterDelete(scope, capabilities.Count > 0);
        return new AdminOutcome<object>(AdminStatus.Ok);
    }

    /// <summary>A 409 message when any of <paramref name="capabilities"/> is a default, else null.</summary>
    private static string? DefaultConflict(IEnumerable<ModelCapability> capabilities, IEnumerable<InferenceModel> models)
    {
        ModelCapability? defaultCapability = capabilities.FirstOrDefault(c => c.IsDefault);
        if (defaultCapability is null) return null;
        string modelName = models.FirstOrDefault(m => m.Id == defaultCapability.ModelId)?.DisplayName ?? "a model";
        return $"'{modelName}' is the default for {defaultCapability.Capability}. Make another model the default first.";
    }

    private async Task RemoveCapabilitiesAsync(IReadOnlyCollection<ModelCapability> capabilities, CancellationToken ct)
    {
        if (capabilities.Count == 0) return;
        List<Guid> ids = [.. capabilities.Select(c => c.Id)];
        _db.ModelCapabilityPrices.RemoveRange(await _db.ModelCapabilityPrices.Where(p => ids.Contains(p.ModelCapabilityId)).ToListAsync(ct));
        _db.ModelCapabilities.RemoveRange(capabilities);
    }

    private void AfterDelete(ProviderScope scope, bool capabilitiesRemoved)
    {
        Invalidate(scope);
        if (capabilitiesRemoved && !scope.IsOrg) _prices?.Invalidate();
    }

    // ------------------------------------------------------------------ models

    public async Task<AdminOutcome<ModelResponse>> CreateModelAsync(
        ProviderScope scope, Guid id, CreateModelRequest request, CancellationToken ct)
    {
        InferenceProvider? provider = await FindProviderAsync(scope, id, ct);
        if (provider is null) return AdminOutcome<ModelResponse>.NotFound();
        if (provider.IsSystem) return AdminOutcome<ModelResponse>.Conflict(SystemProviderMessage);
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<ModelResponse>.PaymentRequired();

        if (ModelProblem(request, provider.Kind, provider.Endpoint, out ValidModel? valid) is { } problem)
            return AdminOutcome<ModelResponse>.BadRequest(problem);
        if (await ModelNameTakenAsync(scope, valid!.DisplayName, null, ct))
            return AdminOutcome<ModelResponse>.Conflict($"A model named '{valid.DisplayName}' already exists.");

        InferenceModel model = NewModel(provider, valid, Now);
        if (!await SaveWithDefaultsAsync(scope, model.Capabilities.Where(c => c.IsDefault).Select(c => (c.Capability, c.Id)), () =>
            {
                _db.InferenceModels.Add(model);
                _db.ModelCapabilities.AddRange(model.Capabilities);
            }, ct))
            return AdminOutcome<ModelResponse>.Conflict($"A model named '{valid.DisplayName}' already exists.");

        Invalidate(scope);
        await AutoPriceAsync(scope, model.Capabilities.Select(c => c.Id), ct);
        return AdminOutcome<ModelResponse>.Ok(await LoadModelResponseAsync(scope, model.Id, ct));
    }

    public async Task<AdminOutcome<ModelResponse>> UpdateModelAsync(
        ProviderScope scope, Guid id, Guid modelId, UpdateModelRequest request, CancellationToken ct)
    {
        InferenceModel? model = await FindModelAsync(scope, id, modelId, ct);
        if (model is null) return AdminOutcome<ModelResponse>.NotFound();
        if (model.Provider.IsSystem) return AdminOutcome<ModelResponse>.Conflict(SystemProviderMessage);
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<ModelResponse>.PaymentRequired();

        string? code = request.ModelCode?.Trim();
        if (code is not null)
        {
            if (code.Length == 0) return AdminOutcome<ModelResponse>.BadRequest("ModelCode cannot be empty.");
            if (code.Length > MaxNameLength)
                return AdminOutcome<ModelResponse>.BadRequest($"ModelCode must be at most {MaxNameLength} characters.");

            // A new model code must still serve every capability the model has.
            List<InferenceProviderCapability> served = await Capabilities(scope)
                .Where(c => c.ModelId == model.Id).Select(c => c.Capability).ToListAsync(ct);
            // Re-checked only when the code really changes: an existing row that already names a model the rules no longer
            // allow (a talking head under Video generation) can still be renamed or switched off with its code unchanged.
            foreach (InferenceProviderCapability capability in served.Where(_ => !string.Equals(code, model.ModelCode, StringComparison.Ordinal)))
            {
                if (InferenceProvidersController.IsUnsupportedModel(model.Provider.Kind, capability, code, out string unsupported))
                    return AdminOutcome<ModelResponse>.BadRequest(unsupported);
            }
        }

        string? display = request.DisplayName?.Trim();
        if (display is not null)
        {
            if (display.Length == 0) return AdminOutcome<ModelResponse>.BadRequest("DisplayName cannot be empty.");
            if (display.Length > MaxNameLength)
                return AdminOutcome<ModelResponse>.BadRequest($"DisplayName must be at most {MaxNameLength} characters.");
            if (await ModelNameTakenAsync(scope, display, model.Id, ct))
                return AdminOutcome<ModelResponse>.Conflict($"A model named '{display}' already exists.");
        }

        // SEC-18: a renamed model code is every capability's identity. Judged before anything is written, as the endpoint edit is.
        if (code is not null && code != model.ModelCode
            && await SelfHostedZeroPriceGate.IdentityRefusalAsync(_db, _resolver, model.Provider.Id, model.Provider.Name, model.Provider.Kind,
                [.. (await Capabilities(scope).Where(c => c.ModelId == model.Id).ToListAsync(ct))
                    .Select(c => (c.Id, code, c.Capability.ToString()))], Now, ct) is { } zeroRefusal)
            return AdminOutcome<ModelResponse>.Conflict(zeroRefusal);

        // As UpdateCapability: switching the model on under an enabled account must not make one of its
        // unpriced platform capabilities usable. Judged before anything is written.
        if (request.IsEnabled == true && model.Provider.IsEnabled && !scope.IsOrg && UnpricedCapabilityGate.Applies(_resolver)
            && await UnpricedCapabilityGate.FirstExposedAsync(_db, _tester, await Capabilities(scope)
                    .Include(c => c.Model).ThenInclude(m => m.Provider)
                    .Where(c => c.ModelId == model.Id)
                    .ToListAsync(ct), Now, ct) is { } exposedByModel)
            return AdminOutcome<ModelResponse>.Unpriced(
                UnpricedCapabilityGate.ExposesRefusalMessage($"The model '{model.DisplayName}'", exposedByModel));

        if (code is not null) model.ModelCode = code;
        if (display is not null) model.DisplayName = display;
        if (request.IsEnabled.HasValue) model.IsEnabled = request.IsEnabled.Value;
        model.UpdatedAt = Now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return AdminOutcome<ModelResponse>.Conflict($"A model named '{display}' already exists.");
        }

        Invalidate(scope);
        return AdminOutcome<ModelResponse>.Ok(await LoadModelResponseAsync(scope, model.Id, ct));
    }

    public async Task<AdminOutcome<object>> DeleteModelAsync(ProviderScope scope, Guid id, Guid modelId, CancellationToken ct)
    {
        InferenceModel? model = await FindModelAsync(scope, id, modelId, ct);
        if (model is null) return AdminOutcome<object>.NotFound();
        if (model.Provider.IsSystem) return AdminOutcome<object>.Conflict(SystemProviderMessage);

        List<ModelCapability> capabilities = await Capabilities(scope).Where(c => c.ModelId == model.Id).ToListAsync(ct);
        if (DefaultConflict(capabilities, [model]) is { } conflict) return AdminOutcome<object>.Conflict(conflict);

        await RemoveCapabilitiesAsync(capabilities, ct);
        _db.InferenceModels.Remove(model);
        await _db.SaveChangesAsync(ct);
        AfterDelete(scope, capabilities.Count > 0);
        return new AdminOutcome<object>(AdminStatus.Ok);
    }

    // ------------------------------------------------------------------ capabilities

    public async Task<AdminOutcome<CapabilityResponse>> CreateCapabilityAsync(
        ProviderScope scope, Guid id, Guid modelId, CreateCapabilityRequest request, CancellationToken ct)
    {
        InferenceModel? model = await FindModelAsync(scope, id, modelId, ct);
        if (model is null) return AdminOutcome<CapabilityResponse>.NotFound();
        if (model.Provider.IsSystem) return AdminOutcome<CapabilityResponse>.Conflict(SystemProviderMessage);
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<CapabilityResponse>.PaymentRequired();

        if (CapabilityProblem(request.Capability, model.Provider.Kind, model.ModelCode, model.Provider.Endpoint, out InferenceProviderCapability capability) is { } problem)
            return AdminOutcome<CapabilityResponse>.BadRequest(problem);

        // A model serves each capability at most once (unique (model_id, capability)).
        if (await Capabilities(scope).AnyAsync(c => c.ModelId == model.Id && c.Capability == capability, ct))
            return AdminOutcome<CapabilityResponse>.Conflict($"The model '{model.DisplayName}' already serves the {capability} capability.");

        ModelCapability entity = NewCapability(model, model.Provider.Kind, capability, request.IsDefault, request.IsEnabled, Now);
        if (!await SaveWithDefaultsAsync(scope, entity.IsDefault ? [(capability, entity.Id)] : [], () => _db.ModelCapabilities.Add(entity), ct))
            return AdminOutcome<CapabilityResponse>.Conflict($"The model '{model.DisplayName}' already serves the {capability} capability.");

        Invalidate(scope);
        await AutoPriceAsync(scope, [entity.Id], ct);
        return AdminOutcome<CapabilityResponse>.Ok(await LoadCapabilityResponseAsync(scope, entity.Id, ct));
    }

    public async Task<AdminOutcome<CapabilityResponse>> UpdateCapabilityAsync(
        ProviderScope scope, Guid id, Guid modelId, Guid capId, UpdateCapabilityRequest request, CancellationToken ct)
    {
        ModelCapability? capability = await FindCapabilityAsync(scope, id, modelId, capId, ct);
        if (capability is null) return AdminOutcome<CapabilityResponse>.NotFound();
        if (capability.Model.Provider.IsSystem) return AdminOutcome<CapabilityResponse>.Conflict(SystemProviderMessage);
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<CapabilityResponse>.PaymentRequired();

        // judged before anything is written, so a refusal leaves the tracked row untouched.
        if ((request.IsEnabled ?? capability.IsEnabled) && !scope.IsOrg && UnpricedCapabilityGate.Applies(_resolver)
            && await UnpricedCapabilityGate.BlocksAsync(_db, _tester, capability, Now, ct))
            return AdminOutcome<CapabilityResponse>.Unpriced(UnpricedCapabilityGate.RefusalMessage(capability));

        if (!await SaveWithDefaultsAsync(scope, request.IsDefault == true ? [(capability.Capability, capability.Id)] : [], () =>
            {
                if (request.IsEnabled.HasValue) capability.IsEnabled = request.IsEnabled.Value;
                if (request.IsDefault.HasValue) capability.IsDefault = request.IsDefault.Value;
                capability.UpdatedAt = Now;
            }, ct))
            return AdminOutcome<CapabilityResponse>.Conflict("Another change to the defaults happened at the same time. Reload and try again.");

        Invalidate(scope);
        return AdminOutcome<CapabilityResponse>.Ok(await LoadCapabilityResponseAsync(scope, capability.Id, ct));
    }

    public async Task<AdminOutcome<object>> DeleteCapabilityAsync(ProviderScope scope, Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        ModelCapability? capability = await FindCapabilityAsync(scope, id, modelId, capId, ct);
        if (capability is null) return AdminOutcome<object>.NotFound();
        if (capability.Model.Provider.IsSystem) return AdminOutcome<object>.Conflict(SystemProviderMessage);
        if (DefaultConflict([capability], [capability.Model]) is { } conflict) return AdminOutcome<object>.Conflict(conflict);

        await RemoveCapabilitiesAsync([capability], ct);
        await _db.SaveChangesAsync(ct);
        AfterDelete(scope, capabilitiesRemoved: true);
        return new AdminOutcome<object>(AdminStatus.Ok);
    }

    /// <summary>Tests a saved capability with its provider's stored key and persists <c>last_test_*</c> on the capability.</summary>
    public async Task<AdminOutcome<ProviderTestOutcome>> TestCapabilityAsync(
        ProviderScope scope, Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        ModelCapability? capability = await FindCapabilityAsync(scope, id, modelId, capId, ct);
        if (capability is null) return AdminOutcome<ProviderTestOutcome>.NotFound();
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<ProviderTestOutcome>.PaymentRequired();
        if (capability.Model.Provider.IsSystem || capability.Capability == InferenceProviderCapability.Compute)
            return AdminOutcome<ProviderTestOutcome>.Ok(ProviderTestOutcome.Rejected(
                "The built-in compute service runs inside the platform; there is no connection to test."));

        ProviderTestOutcome outcome = await _tester.TestSavedAsync(capability, ct);
        Invalidate(scope); // LastTest* are part of the cached row (health-aware selection reads them).
        return AdminOutcome<ProviderTestOutcome>.Ok(outcome);
    }

    // ------------------------------------------------------------------ image tools

    /// <summary>
    /// One click for the picture editor: under an existing fal.ai provider, adds a model for every picture job
    /// (generation, the instruction edit and the six per-job capabilities) that has no fal.ai capability yet in this
    /// scope, each with the catalogue's default endpoint for its job. The key stays on the one account (nothing is
    /// copied or decrypted). A capability becomes its job's default only when that job has none, so an admin's existing
    /// choices are never overridden. Returns the models it created, which is empty when there was nothing to add.
    /// </summary>
    /// <remarks>
    /// One model per job (rather than one model serving several) because each fal.ai endpoint serves exactly one editor
    /// job: the model code IS the endpoint id.
    /// </remarks>
    public async Task<AdminOutcome<List<ModelResponse>>> CreateImageToolsAsync(ProviderScope scope, Guid id, CancellationToken ct)
    {
        InferenceProvider? provider = await FindProviderAsync(scope, id, ct);
        if (provider is null) return AdminOutcome<List<ModelResponse>>.NotFound();
        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<List<ModelResponse>>.PaymentRequired();

        if (provider.Kind != InferenceProviderKind.FalAi)
            return AdminOutcome<List<ModelResponse>>.BadRequest(
                "Only a fal.ai provider can set up the picture tools: fal.ai is the one kind that serves all of them.");

        if (string.IsNullOrEmpty(provider.ApiKeyEncrypted))
            return AdminOutcome<List<ModelResponse>>.BadRequest(
                "This provider has no API key to use. Save a key on it first.");

        var existing = await Capabilities(scope)
            .Select(c => new { c.Capability, c.IsDefault, c.Model.Provider.Kind })
            .ToListAsync(ct);
        HashSet<string> names = (await Models(scope).Select(m => m.DisplayName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        DateTime now = Now;
        List<InferenceModel> created = [];

        foreach (InferenceProviderCapability capability in ImageJobCapabilities.All)
        {
            if (existing.Any(p => p.Kind == InferenceProviderKind.FalAi && p.Capability == capability)) continue;

            string name = UniqueName($"{provider.Name} - {ImageToolLabel(capability)}", names);
            names.Add(name);
            InferenceModel model = NewModel(provider, new ValidModel(
                ImageJobCapabilities.FalDefaultFor(capability)!.Id, name, true,
                [(capability, !existing.Any(p => p.IsDefault && p.Capability == capability), true)]), now);
            created.Add(model);
        }

        if (created.Count == 0) return AdminOutcome<List<ModelResponse>>.Ok([]);

        _db.InferenceModels.AddRange(created);
        _db.ModelCapabilities.AddRange(created.SelectMany(m => m.Capabilities));
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _db.ChangeTracker.Clear();
            return AdminOutcome<List<ModelResponse>>.Conflict(
                "Another change to the providers happened at the same time. Reload and try again.");
        }

        Invalidate(scope);
        await AutoPriceAsync(scope, created.SelectMany(m => m.Capabilities).Select(c => c.Id), ct);
        List<ModelResponse> responses = [];
        foreach (InferenceModel model in created)
        {
            responses.Add(await LoadModelResponseAsync(scope, model.Id, ct));
        }

        return AdminOutcome<List<ModelResponse>>.Ok(responses);
    }

    /// <summary>The plain name a picture-tool model gets in the one-click setup.</summary>
    internal static string ImageToolLabel(InferenceProviderCapability capability) => capability switch
    {
        InferenceProviderCapability.ImageGeneration => "Create pictures",
        InferenceProviderCapability.ImageEdit => "Edit from a description",
        InferenceProviderCapability.ImageErase => "Remove objects",
        InferenceProviderCapability.ImageFill => "Fill an area",
        InferenceProviderCapability.ImageExpand => "Expand",
        InferenceProviderCapability.ImageBackgroundReplace => "Replace background",
        InferenceProviderCapability.ImageRelight => "Relight",
        InferenceProviderCapability.ImageUpscale => "Upscale",
        _ => capability.ToString()
    };

    internal static string UniqueName(string wanted, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(wanted)) return wanted;
        for (int n = 2; ; n++)
        {
            string candidate = $"{wanted} ({n})";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    // ------------------------------------------------------------------ unsaved test

    /// <summary>
    /// An unsaved connection test. With <see cref="TestProviderRequest.ProviderId"/> (a provider of this scope) and no
    /// key in the request, the saved key is reused — and with it the saved kind, endpoint, timeout and OAuth mode, so a
    /// stored key is never sent to an endpoint the caller just typed.
    /// </summary>
    public async Task<AdminOutcome<TestInferenceProviderResponse>> TestUnsavedAsync(
        ProviderScope scope, TestProviderRequest request, CancellationToken ct)
    {
        string? kindStr = request.Kind;
        string endpoint = request.Endpoint ?? string.Empty;
        string modelCode = request.ModelCode ?? string.Empty;
        string apiKey = request.ApiKey ?? string.Empty;
        int timeoutSeconds = InferenceProviderTester.DefaultTimeoutSeconds;
        bool? personalOAuthMode = request.PersonalOAuthMode;

        if (request.ProviderId.HasValue)
        {
            InferenceProvider? saved = await Providers(scope).AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == request.ProviderId.Value && !p.IsSystem, ct);
            if (saved == null) return AdminOutcome<TestInferenceProviderResponse>.NotFound("Referenced provider not found.");

            timeoutSeconds = saved.TimeoutSeconds ?? timeoutSeconds;
            if (request.ApiKey == null)
            {
                // Reusing the saved key against a different origin is refused outright (it used to be silently
                // replaced by the saved endpoint, which hid the mistake from the admin).
                if (!string.IsNullOrWhiteSpace(request.Endpoint)
                    && StoredKeyFollowProblem(saved.Kind, saved.Endpoint, !string.IsNullOrEmpty(saved.ApiKeyEncrypted),
                        null, request.Endpoint, null) is { } follow)
                    return Refusal<TestInferenceProviderResponse>(follow);

                kindStr = saved.Kind.ToString();
                endpoint = saved.Endpoint;
                personalOAuthMode = saved.PersonalOAuthMode;

                if (!string.IsNullOrEmpty(saved.ApiKeyEncrypted))
                {
                    if (!_secretProtector.TryUnprotect(saved.ApiKeyEncrypted, out string storedKey))
                        return AdminOutcome<TestInferenceProviderResponse>.Ok(
                            new TestInferenceProviderResponse(false, 0, "stored key could not be decrypted", null));
                    apiKey = storedKey;
                }
            }
            else
            {
                kindStr ??= saved.Kind.ToString();
                if (request.Endpoint == null) endpoint = saved.Endpoint;
                personalOAuthMode ??= saved.PersonalOAuthMode;
            }
        }

        if (await ByoDeniedAsync(scope, ct)) return AdminOutcome<TestInferenceProviderResponse>.PaymentRequired();

        if (string.IsNullOrWhiteSpace(kindStr) || !TryParseKnownKind(kindStr, out InferenceProviderKind kind))
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(
                $"Invalid or missing kind '{kindStr}'. Expected one of {InferenceProvidersController.KnownKinds}.");

        // Placed before any client is constructed, so a rejected endpoint never reaches a factory.
        if (kind == InferenceProviderKind.LocalRunner)
        {
            if (await LocalRunnerProblemAsync(scope, endpoint, request.ApiKey, ct) is { } localProblem)
                return AdminOutcome<TestInferenceProviderResponse>.BadRequest(localProblem);
            endpoint = LocalRunnerProviderRules.Normalize(endpoint);
        }
        else if (IsEndpointDisallowed(scope, endpoint, kind))
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(PublicEndpointMessage);

        // Chat when omitted, as the test always did. The model-code rule is not applied: a test is how an admin finds
        // out whether a model answers at all.
        InferenceProviderCapability capability = InferenceProviderCapability.Chat;
        if (!string.IsNullOrWhiteSpace(request.Capability)
            && (!InferenceProvidersController.TryParseCapability(request.Capability, out capability) || !Enum.IsDefined(capability)
                || capability == InferenceProviderCapability.Compute))
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(InvalidCapabilityMessage(request.Capability));

        if (InferenceProvidersController.IsUnsupportedCombination(kind, capability, endpoint, out string unsupportedCombination))
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(unsupportedCombination);

        bool resolvedPersonalOAuthMode = personalOAuthMode ?? false;
        if (InferenceProvidersController.IsPersonalOAuthModeUnsupported(kind, resolvedPersonalOAuthMode, out string unsupportedOAuthMode))
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(unsupportedOAuthMode);

        // A supplied key is checked; a stored one was checked when it was written.
        if (OrgRowRejection(scope, resolvedPersonalOAuthMode, request.ApiKey) is { } orgRejection)
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(orgRejection);

        if (InferenceProvidersController.ApiKeyProblem(request.ApiKey) is { } keyProblem)
            return AdminOutcome<TestInferenceProviderResponse>.BadRequest(keyProblem);

        if (AmbientKeyEndpointProblem(kind, endpoint, request.ApiKey) is { } ambient)
            return Refusal<TestInferenceProviderResponse>(ambient);

        TestInferenceProviderResponse result = await _tester.RunAsync(
            null, "test", kind, capability, endpoint, modelCode, apiKey, timeoutSeconds, resolvedPersonalOAuthMode, ct,
            organizationId: scope.OrganizationId);
        return AdminOutcome<TestInferenceProviderResponse>.Ok(result);
    }

    private static AdminOutcome<T> Refusal<T>((string Code, string Message) r) =>
        new(AdminStatus.BadRequest, default, new { error = r.Message, code = r.Code });

    /// <summary>
    /// The one rule every writer of a provider account's endpoint or kind applies (admin API, org twin, assistant
    /// tools): a stored key, <c>env:</c> included, never follows the account to another scheme/host/port or another
    /// kind unless the same request re-enters the key or clears it (<paramref name="apiKey"/> not null). LocalRunner
    /// rows are exempt: their endpoint is a device id with no origin, and they carry no key.
    /// </summary>
    internal static (string Code, string Message)? StoredKeyFollowProblem(
        InferenceProviderKind currentKind, string? currentEndpoint, bool hasStoredKey,
        InferenceProviderKind? newKind, string? newEndpoint, string? apiKey)
    {
        if (apiKey is not null || !hasStoredKey) return null;
        if (currentKind == InferenceProviderKind.LocalRunner && (newKind ?? currentKind) == InferenceProviderKind.LocalRunner)
            return null;
        bool kindChanged = newKind is { } k && k != currentKind;
        bool originChanged = newEndpoint is not null && !SameOrigin(currentEndpoint, newEndpoint.Trim());
        return kindChanged || originChanged
            ? (ApiKeyRequiredForEndpointChangeCode, ApiKeyRequiredForEndpointChangeMessage)
            : null;
    }

    /// <summary>
    /// <c>env:</c> resolves to the server's own credential, so it is only accepted with an empty endpoint or an https
    /// endpoint on the kind's vendor host (default port).
    /// </summary>
    internal static (string Code, string Message)? AmbientKeyEndpointProblem(
        InferenceProviderKind kind, string? effectiveEndpoint, string? apiKey)
    {
        if (!IsAmbientSentinel(apiKey) || string.IsNullOrWhiteSpace(effectiveEndpoint)) return null;
        bool vendor = AmbientVendorHosts.TryGetValue(kind, out string? host)
            && Uri.TryCreate(effectiveEndpoint.Trim(), UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && string.Equals(uri.IdnHost, host, StringComparison.OrdinalIgnoreCase);
        return vendor ? null : (AmbientKeyEndpointNotAllowedCode, AmbientKeyEndpointNotAllowedMessage);
    }

    /// <summary>Same scheme, host and (effective) port. Anything unparseable counts as a different origin.</summary>
    internal static bool SameOrigin(string? stored, string candidate) =>
        Uri.TryCreate(stored?.Trim(), UriKind.Absolute, out Uri? a)
        && Uri.TryCreate(candidate, UriKind.Absolute, out Uri? b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    // ------------------------------------------------------------------ configuration seeding support

    /// <summary>
    /// The platform provider account a configuration entry belongs to: the same kind, the same endpoint and the same
    /// key, compared DECRYPTED (ciphertexts carry a random IV); no key matches only a keyless account, and a stored key
    /// that cannot be decrypted never matches. Null when there is none. Never returns or logs key material.
    /// </summary>
    public async Task<Guid?> FindPlatformAccountAsync(
        InferenceProviderKind kind, string endpoint, string? apiKey, CancellationToken ct)
    {
        string wantedEndpoint = endpoint.Trim();
        string? wantedKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        List<InferenceProvider> candidates = await Providers(ProviderScope.Platform).AsNoTracking()
            .Where(p => !p.IsSystem && p.Kind == kind)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);

        foreach (InferenceProvider candidate in candidates.Where(c => string.Equals(c.Endpoint.Trim(), wantedEndpoint, StringComparison.Ordinal)))
        {
            if (string.IsNullOrEmpty(candidate.ApiKeyEncrypted))
            {
                if (wantedKey is null) return candidate.Id;
                continue;
            }

            if (wantedKey is not null
                && _secretProtector.TryUnprotect(candidate.ApiKeyEncrypted, out string stored)
                && string.Equals(stored, wantedKey, StringComparison.Ordinal))
                return candidate.Id;
        }

        return null;
    }
}
