using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// The write-time rules that are specific to <see cref="InferenceProviderKind.LocalRunner"/>: a model on the user's own
/// machine, relayed through a paired desktop runner. The capability allowlist (Chat, Vision and Embedding only) lives with the
/// kind/capability matrix in <see cref="Controllers.InferenceProvidersController.IsUnsupportedCombination"/>; this
/// class owns scope (organization-owned only), the key (none), and the endpoint (the paired device id).
/// </summary>
internal static class LocalRunnerProviderRules
{
    public const string PlatformScopeMessage =
        "LocalRunner providers belong to a workspace: a platform-wide provider cannot use a model on someone's own computer.";

    public const string ApiKeyMessage =
        "A LocalRunner provider takes no API key: the model runs on your own computer and is reached through the desktop app.";

    public const string DeviceMessage =
        "Endpoint must be the id of a desktop app (runner) that is paired to this workspace and has not been removed.";

    /// <summary>Why the kind cannot be used in <paramref name="scope"/>, or null.</summary>
    public static string? ScopeProblem(ProviderScope scope) => scope.IsOrg ? null : PlatformScopeMessage;

    /// <summary>A key of any kind is refused (blank is fine).</summary>
    public static string? ApiKeyProblem(string? apiKey) => string.IsNullOrWhiteSpace(apiKey) ? null : ApiKeyMessage;

    /// <summary>
    /// Why <paramref name="endpoint"/> cannot name a device of <paramref name="organizationId"/>, or null when it can:
    /// it must be a GUID naming a non-revoked Desktop device of THAT organization. The organization is always the
    /// row's own scope, never anything the request supplies; a device of another organization reads exactly like a
    /// missing one.
    /// </summary>
    public static async Task<string?> DeviceProblemAsync(
        InferenceApiDbContext db, Guid organizationId, string? endpoint, CancellationToken ct)
    {
        if (!Guid.TryParse(endpoint?.Trim(), out Guid deviceId)) return DeviceMessage;

        bool usable = await db.RunnerDevices.AsNoTracking().AnyAsync(
            d => d.Id == deviceId
                 && d.OrganizationId == organizationId
                 && d.Kind == RunnerKind.Desktop
                 && d.RevokedAt == null,
            ct);
        return usable ? null : DeviceMessage;
    }

    /// <summary>The canonical stored form of a validated device id.</summary>
    public static string Normalize(string endpoint) => Guid.Parse(endpoint.Trim()).ToString("D");
}
