using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The one rule for which provider and model a <c>VideoGenerate</c> step runs on. A step with avatar audio is a talking
/// head (lip-sync): it resolves through the <c>LipSync</c> capability (explicit LipSync row, else the default LipSync
/// row, else none) and NEVER falls back to <c>VideoGeneration</c>. Its model is the step's model when the step names a
/// provider, else the resolved LipSync capability's own model. Every other step resolves exactly as it always did.
/// </summary>
public static class LipSyncStepResolution
{
    /// <summary>Plain words for the run when no LipSync capability can be resolved.</summary>
    public const string NoModelMessage = "No lip-sync model is set up. Ask a workspace admin to add one.";

    /// <summary>True when the step is a talking head: it carries avatar audio.</summary>
    public static bool IsLipSync(VideoGenerateStepConfig config) => config.HasAvatarAudio;

    /// <summary>The capability the step's provider row is registered under, and its usage is recorded against.</summary>
    public static InferenceProviderCapability CapabilityFor(VideoGenerateStepConfig config) =>
        IsLipSync(config) ? InferenceProviderCapability.LipSync : InferenceProviderCapability.VideoGeneration;

    /// <summary>Resolves the step's provider through the capability that serves it; null when none resolves.</summary>
    public static Task<ResolvedVideoGenerationProvider?> ResolveAsync(
        IInferenceProviderResolver providers, VideoGenerateStepConfig config, CancellationToken ct) =>
        IsLipSync(config)
            ? providers.ResolveLipSyncAsync(config.ProviderId, ct)
            : providers.ResolveVideoGenerationAsync(config.ProviderId, ct);

    /// <summary>
    /// True only when the resolved row IS the one the step names. A <c>providerId</c> that did not resolve to itself (it names
    /// a row that is not an enabled LipSync row, or one saved before this capability existed) fell through to the default
    /// LipSync row, exactly as every other capability's resolver treats a wrong-capability id; the step then counts as having
    /// no provider of its own.
    /// </summary>
    public static bool UsesBoundRow(VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider) =>
        config.ProviderId is { } id && provider.ProviderId == id;

    /// <summary>
    /// The model the step really runs: the step's own model when its own LipSync row is bound, else (a lip-sync step with no
    /// bound row) the LipSync capability's own model; the template's model string is only a hint then.
    /// </summary>
    public static string EffectiveModel(VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider) =>
        IsLipSync(config) && !UsesBoundRow(config, provider) && !string.IsNullOrWhiteSpace(provider.ModelName)
            ? provider.ModelName
            : config.Model;

    /// <summary>
    /// Applies the rule to a LOCAL copy of the step config, the one call every consumer (executor, hold, exact price) makes
    /// right after resolving: sets <see cref="VideoGenerateStepConfig.Model"/> to the effective model and, for a lip-sync
    /// step with no bound row, moves the settings the model does not offer (resolution, ratio, duration) to what it does, since
    /// they are only hints then. A bound row's own settings are never touched: a mismatch there is the author's, and refused.
    /// </summary>
    public static void Apply(VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider)
    {
        config.Model = EffectiveModel(config, provider);
        if (!IsLipSync(config) || UsesBoundRow(config, provider) || provider.Kind != InferenceProviderKind.FalAi)
            return;
        // The same pure catalogue read FalAiVideoGenerationClient.Describe answers from: no client, no network.
        if (FalAiVideoGenerationClient.Catalog.Find(config.Model?.Trim()) is not { } endpoint)
            return;
        VideoGenerationCapabilities capabilities = new(
            endpoint.PricePerSecondByResolution, endpoint.MinDurationSeconds, endpoint.MaxDurationSeconds, endpoint.AllowedRatios);

        IReadOnlyList<string> resolutions = [.. capabilities.PricePerSecondByResolution.Keys];
        if (resolutions.Count > 0 && (config.Resolution is null || !resolutions.Contains(config.Resolution, StringComparer.Ordinal)))
            config.Resolution = Preferred(resolutions);
        if (capabilities.AllowedRatios.Count > 0 && (config.Ratio is null || !capabilities.AllowedRatios.Contains(config.Ratio, StringComparer.Ordinal)))
            config.Ratio = Preferred(capabilities.AllowedRatios);
        config.DurationSeconds = Math.Clamp(config.DurationSeconds, capabilities.MinDurationSeconds, Math.Max(capabilities.MinDurationSeconds, capabilities.MaxDurationSeconds));
    }

    private static string Preferred(IReadOnlyList<string> offered) =>
        offered.FirstOrDefault(o => o == FalVideoCatalog.NativeToken) ?? offered[0];

    /// <summary>
    /// Whether a lip-sync step's settings are accepted by the model it would run: false only when the model is known and does
    /// not offer the step's resolution or ratio AFTER <see cref="Apply"/> (so only a bound row can fail). Mutates <paramref name="config"/>.
    /// </summary>
    public static bool SettingsAccepted(VideoGenerateStepConfig config, ResolvedVideoGenerationProvider provider, VideoGenerationCapabilities capabilities)
    {
        Apply(config, provider); // the caller passes its own deserialized copy
        bool resolution = config.Resolution is not null && capabilities.PricePerSecondByResolution.Keys.Contains(config.Resolution, StringComparer.Ordinal);
        bool ratio = config.Ratio is not null && capabilities.AllowedRatios.Contains(config.Ratio, StringComparer.Ordinal);
        return resolution && ratio;
    }
}
