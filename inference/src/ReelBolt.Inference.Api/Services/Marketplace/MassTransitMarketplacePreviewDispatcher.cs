using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Publishes <see cref="MarketplacePreviewRequested"/> for a committed version. The source key is read from the version's own
/// server-resolved <c>preview_source_json</c>; it must be a project key. Nothing is published when the source is not usable (the
/// sweeper then fails the version).
/// </summary>
public sealed class MassTransitMarketplacePreviewDispatcher(
    InferenceApiDbContext db,
    IPublishEndpoint publisher,
    ILogger<MassTransitMarketplacePreviewDispatcher> logger) : IMarketplacePreviewDispatcher
{
    public async Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct)
    {
        var row = await db.MarketplaceRecipeVersions
            .Where(v => v.Id == versionId && v.RecipeId == recipeId)
            .Select(v => new { Version = v, v.Recipe!.AuthorOrgId })
            .FirstOrDefaultAsync(ct);
        string? key = row is null ? null : ReadKey(row.Version.PreviewSourceJson);
        if (row is null || !MarketplacePreviewKeys.IsProjectSourceKey(key))
        {
            logger.LogWarning("Marketplace version {VersionId} has no usable preview source; no job was requested", versionId);
            return;
        }

        // A fresh nonce per dispatch, stored before publishing: only the run holding the current nonce can finish the version.
        string nonce = MarketplacePreviewKeys.NewNonce();
        row.Version.PreviewRunNonce = nonce;
        await db.SaveChangesAsync(ct);
        await publisher.Publish(new MarketplacePreviewRequested(versionId, recipeId, key!, row.AuthorOrgId, DateTime.UtcNow, nonce), ct);
    }

    internal static string? ReadKey(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("key", out JsonElement key) && key.ValueKind == JsonValueKind.String ? key.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
