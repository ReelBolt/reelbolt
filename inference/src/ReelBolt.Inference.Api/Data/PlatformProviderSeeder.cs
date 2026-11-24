using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Data;

/// <summary>One entry of the <c>PlatformProviders</c> configuration array.</summary>
public sealed class PlatformProviderEntry
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? Capability { get; set; }
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    /// <summary>The key itself, or the literal <c>env:</c> sentinel for kinds that support ambient credentials.</summary>
    public string? ApiKey { get; set; }
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// Upserts the platform model catalog (<c>OrganizationId = null</c>, decision D3) from the <c>PlatformProviders</c>
/// configuration array at startup, so a cloud deployment can run hosted ASR/embeddings without the whisper/embeddings
/// containers (D19) and without an admin clicking through the UI. Each entry is one model capability:
/// <list type="bullet">
///   <item><description>its provider ACCOUNT is the platform provider with the same kind, endpoint and key (compared
///   decrypted; <see cref="InferenceProviderAdminService.FindPlatformAccountAsync"/>), so entries sharing one account land
///   under one provider. With no such account, the account that already holds the entry's model (same kind and endpoint)
///   is reused and, when the entry carries a key, re-keyed (logged, without key material) — but only while it serves
///   that one model: an account that also serves other models keeps its key, and the entry gets a new account.
///   Otherwise a new provider is created, named after the entry;</description></item>
///   <item><description>its MODEL is the account's model named after the entry (else the one with the entry's model
///   code), added when missing; its CAPABILITY is added when missing, and otherwise switched on/off and promoted to
///   default as the entry says.</description></item>
/// </list>
/// Every write goes through <see cref="InferenceProviderAdminService"/> in platform scope, i.e. exactly the validation
/// the admin API applies (kind/capability allowlists, endpoint guard), the same resolver invalidation and the same
/// price-book pre-fill. Never deletes, never replaces a stored key with an empty value, and an empty section (or entries
/// with a blank name, the unconfigured compose slots) is a no-op. Key material is never logged.
/// </summary>
public static class PlatformProviderSeeder
{
    public const string SectionKey = "PlatformProviders";

    public static async Task<int> SeedAsync(
        InferenceProviderAdminService admin, IConfiguration configuration, ILogger logger, CancellationToken ct = default)
    {
        List<PlatformProviderEntry> entries = configuration.GetSection(SectionKey).Get<List<PlatformProviderEntry>>() ?? [];
        entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Name)).ToList();
        if (entries.Count == 0) return 0;

        int applied = 0;
        foreach (PlatformProviderEntry entry in entries)
        {
            string name = entry.Name!.Trim();
            try
            {
                if (await UpsertAsync(admin, entry, name, logger, ct)) applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Platform provider '{Name}' could not be seeded.", name);
            }
        }
        return applied;
    }

    private static async Task<bool> UpsertAsync(
        InferenceProviderAdminService admin, PlatformProviderEntry e, string name, ILogger logger, CancellationToken ct)
    {
        string? apiKey = string.IsNullOrWhiteSpace(e.ApiKey) ? null : e.ApiKey.Trim();
        bool kindKnown = InferenceProvidersController.TryParseKind(e.Kind, out InferenceProviderKind kind);

        // the direct ElevenLabs route is dormant, so it is seeded nothing: no row, no key.
        if (kindKnown && kind == InferenceProviderKind.ElevenLabs)
        {
            logger.LogWarning("Platform provider '{Name}' skipped: the ElevenLabs kind is not offered; music and sound effects go through fal only.", name);
            return false;
        }

        // env: only means something to the kinds whose factories resolve ambient credentials; storing it
        // for any other kind would persist a literal key "env:" that fails on first use.
        if (InferenceProviderAdminService.IsAmbientSentinel(apiKey)
            && (!kindKnown || kind is not (InferenceProviderKind.Anthropic or InferenceProviderKind.Gemini or InferenceProviderKind.DeepSeek)))
        {
            logger.LogError("Platform provider '{Name}' rejected: the 'env:' key is not supported for kind '{Kind}'; supply the key.", name, e.Kind);
            return false;
        }

        string endpoint = e.Endpoint?.Trim() ?? string.Empty;
        string modelCode = e.Model?.Trim() ?? string.Empty;
        string capability = string.IsNullOrWhiteSpace(e.Capability) ? nameof(InferenceProviderCapability.Chat) : e.Capability.Trim();
        List<ProviderResponse> platform = await admin.ListAsync(ProviderScope.Platform, ct);

        Guid? accountId = kindKnown ? await admin.FindPlatformAccountAsync(kind, endpoint, apiKey, ct) : null;
        ProviderResponse? account = platform.FirstOrDefault(p => p.Id == accountId);
        if (account is null && kindKnown)
        {
            // No account holds this key (it was rotated, or the entry omits it): the account that already holds the
            // entry's model at the same kind and endpoint is reused, so the model is updated in place rather than
            // duplicated. With a key it is re-keyed only when it serves that one model: a key belongs to the whole
            // account, and one entry must never move the other models under it to a key configured for itself.
            ProviderResponse? holder = platform.FirstOrDefault(p => !p.IsSystem && p.Kind == kind.ToString()
                                                   && string.Equals(p.Endpoint.Trim(), endpoint, StringComparison.Ordinal)
                                                   && p.Models.Any(m => m.DisplayName == name));
            if (holder is not null && apiKey is not null && holder.Models.Count > 1)
            {
                logger.LogWarning(
                    "Platform provider '{Name}': its model sits under the account {AccountId} ('{AccountName}'), which also serves " +
                    "{Others} other model(s), so that account keeps its key; a new account with the configured key is created " +
                    "for this entry instead. Remove the old model from that account once nothing uses it.",
                    name, holder.Id, holder.Name, holder.Models.Count - 1);
            }
            else if (holder is not null)
            {
                if (apiKey is not null)
                {
                    if (!Report(await admin.UpdateProviderAsync(ProviderScope.Platform, holder.Id, new UpdateProviderRequest(ApiKey: apiKey), ct), name, "re-key", logger))
                        return false;
                    logger.LogWarning(
                        "Platform provider '{Name}': re-keyed the account {AccountId} ('{AccountName}') from configuration, because no " +
                        "account held the configured key (a rotation).", name, holder.Id, holder.Name);
                }

                account = holder;
            }
        }

        // Model display names are unique in the platform scope, so a model created beside one that keeps its old account
        // (above) takes the next free name.
        HashSet<string> modelNames = platform.SelectMany(p => p.Models).Select(m => m.DisplayName).ToHashSet(StringComparer.Ordinal);
        if (account is null)
        {
            HashSet<string> providerNames = platform.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            CreateProviderRequest create = new(
                InferenceProviderAdminService.UniqueName(name, providerNames), e.Kind, endpoint, apiKey, e.TimeoutSeconds,
                IsEnabled: true, PersonalOAuthMode: false,
                Models: [new CreateModelRequest(modelCode, InferenceProviderAdminService.UniqueName(name, modelNames), true,
                    [new CreateCapabilityRequest(capability, e.IsDefault, e.Enabled)])]);
            return Report(await admin.CreateProviderAsync(ProviderScope.Platform, create, ct), name, "create", logger);
        }

        if (e.TimeoutSeconds.HasValue && e.TimeoutSeconds != account.TimeoutSeconds
            && !Report(await admin.UpdateProviderAsync(ProviderScope.Platform, account.Id,
                new UpdateProviderRequest(TimeoutSeconds: e.TimeoutSeconds), ct), name, "update", logger))
            return false;

        ModelResponse? model = account.Models.FirstOrDefault(m => m.DisplayName == name)
                               ?? account.Models.FirstOrDefault(m => string.Equals(m.ModelCode, modelCode, StringComparison.Ordinal));
        if (model is null)
        {
            CreateModelRequest create = new(modelCode, InferenceProviderAdminService.UniqueName(name, modelNames), true,
                [new CreateCapabilityRequest(capability, e.IsDefault, e.Enabled)]);
            return Report(await admin.CreateModelAsync(ProviderScope.Platform, account.Id, create, ct), name, "create", logger);
        }

        if (modelCode.Length > 0 && !string.Equals(model.ModelCode, modelCode, StringComparison.Ordinal)
            && !Report(await admin.UpdateModelAsync(ProviderScope.Platform, account.Id, model.Id,
                new UpdateModelRequest(ModelCode: modelCode), ct), name, "update", logger))
            return false;

        CapabilityResponse? existing = model.Capabilities.FirstOrDefault(c => string.Equals(c.Capability, capability, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Report(await admin.CreateCapabilityAsync(ProviderScope.Platform, account.Id, model.Id,
                new CreateCapabilityRequest(capability, e.IsDefault, e.Enabled), ct), name, "create", logger);
        }

        if ((e.IsDefault && !existing.IsDefault) || existing.IsEnabled != e.Enabled)
        {
            return Report(await admin.UpdateCapabilityAsync(ProviderScope.Platform, account.Id, model.Id, existing.Id,
                new UpdateCapabilityRequest(e.IsDefault ? true : null, e.Enabled), ct), name, "update", logger);
        }

        logger.LogInformation("Platform provider '{Name}' already matches configuration.", name);
        return true;
    }

    private static bool Report<T>(AdminOutcome<T> result, string name, string verb, ILogger logger)
    {
        if (result.Status is AdminStatus.Ok or AdminStatus.Created)
        {
            logger.LogInformation("Platform provider '{Name}' {Verb} applied from configuration.", name, verb);
            return true;
        }
        logger.LogError("Platform provider '{Name}' rejected on {Verb} ({Status}): {Body}", name, verb, result.Status, result.Body);
        return false;
    }
}
