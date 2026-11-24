using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>
/// Fills a built-in agent's recommended model (<c>agent_definitions.inference_provider_id</c>, resolver step 3) from
/// <see cref="AgentRecommendationSeed"/>, at boot. Only a NULL is filled, so a platform admin's choice is never
/// overwritten; the model must be an enabled, fully priced PLATFORM Chat capability (the rule the step-editor picker
/// and the resolver apply), otherwise the agent is left alone and keeps resolving to the default. Idempotent.
/// </summary>
public static class AgentRecommendedModelSeeder
{
    /// <summary>Configuration key: <c>false</c> switches the seeder off (default on).</summary>
    public const string EnabledKey = "AgentRecommendations:Enabled";

    public static async Task SeedAsync(InferenceApiDbContext db, IConfiguration configuration, ILogger logger,
        IInferenceProviderResolver? resolver = null, CancellationToken ct = default)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            logger.LogInformation("Agent recommended models: {Key} is false, skipped.", EnabledKey);
            return;
        }

        AgentRecommendationSeed seed = AgentRecommendationSeed.LoadDefault();
        IReadOnlyList<string> problems = seed.Validate();
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
                logger.LogError("Agent recommended models: invalid seed: {Problem}", problem);
            return;
        }

        await RunAsync(db, seed, DateTime.UtcNow, logger, resolver, ct);
    }

    /// <summary>Applies <paramref name="seed"/>; returns how many built-in agents were filled.</summary>
    public static async Task<int> RunAsync(InferenceApiDbContext db, AgentRecommendationSeed seed, DateTime now,
        ILogger logger, IInferenceProviderResolver? resolver = null, CancellationToken ct = default)
    {
        IQueryable<ModelCapability> capabilities = db.ModelCapabilities.IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName]);
        IReadOnlySet<Guid> unpriced = await ModelCapabilityView.UnpricedPlatformIdsAsync(capabilities, now, ct);

        int filled = 0;
        foreach (AgentRecommendation entry in seed.Entries)
        {
            AgentDefinition? definition = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.AgentType == entry.AgentType && a.IsBuiltIn, ct);
            if (definition is null || definition.InferenceProviderId is not null)
                continue;

            List<ModelCapabilityView> candidates = await ModelCapabilityView.EnabledFor(capabilities, null)
                .Where(c => c.Capability == InferenceProviderCapability.Chat && c.Kind == entry.Kind && c.ModelName == entry.ModelCode)
                .OrderByDescending(c => c.IsDefault).ThenBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .ToListAsync(ct);
            ModelCapabilityView? match = candidates.FirstOrDefault(c => !unpriced.Contains(c.Id));
            if (match is null)
            {
                logger.LogDebug("Agent recommended models: no enabled, priced platform Chat model {Kind}/{Model} for {Agent}; left alone.",
                    entry.Kind, entry.ModelCode, entry.AgentType);
                continue;
            }

            definition.InferenceProviderId = match.Id;
            filled++;
        }

        if (filled > 0)
        {
            await db.SaveChangesAsync(ct);
            // Every org's snapshot holds the built-in overrides, so a write is visible to all of them.
            resolver?.InvalidateAll();
            logger.LogInformation("Agent recommended models: set the recommended model on {Count} built-in agents.", filled);
        }

        return filled;
    }
}
