using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>Why <see cref="ProviderHealthSelector.Select"/> picked the provider it picked.</summary>
public enum ProviderSelectionReason
{
    /// <summary>Nothing of this capability is configured and enabled.</summary>
    None,

    /// <summary>The step named this provider explicitly. Always honoured, healthy or not.</summary>
    Explicit,

    /// <summary>The capability's default row, which has not failed its last connection test.</summary>
    Default,

    /// <summary>
    /// The default row FAILED its last connection test, so another enabled row of the same
    /// capability whose last test passed was used instead.
    /// </summary>
    HealthyFallback,

    /// <summary>
    /// The default row failed its last connection test and no other row has passed one, so the
    /// default is used anyway (it may have recovered since it was tested).
    /// </summary>
    UnhealthyDefault,

    /// <summary>No default row exists, but an enabled row of this capability passed its last test.</summary>
    HealthyNonDefault
}

/// <summary>The outcome of <see cref="ProviderHealthSelector.Select"/>.</summary>
/// <param name="Provider">The row a run would use, or null when nothing resolves.</param>
/// <param name="Reason">Why that row.</param>
/// <param name="ConfiguredDefault">The capability's default row (null when none), so a caller can name the provider that was passed over.</param>
public sealed record ProviderSelection(
    ModelCapabilityView? Provider,
    ProviderSelectionReason Reason,
    ModelCapabilityView? ConfiguredDefault)
{
    /// <summary>True when the chosen row's last connection test failed (false when it passed or was never run).</summary>
    public bool ChosenProviderFailedLastTest => Provider?.LastTestOk == false;

    /// <summary>True when a healthier row was used instead of the configured default.</summary>
    public bool UsedFallback => Reason is ProviderSelectionReason.HealthyFallback or ProviderSelectionReason.HealthyNonDefault;
}

/// <summary>
/// Health-aware provider choice for the capabilities where switching to another configured row is
/// harmless — today <see cref="InferenceProviderCapability.SpeechSynthesis"/> (see
/// <see cref="IInferenceProviderResolver.ResolveSpeechSynthesisAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Precedence: an explicit, enabled, capability-matching <c>explicitProviderId</c> always wins → else
/// the default row when its <c>LastTestOk</c> is not <c>false</c> (a never-tested default is trusted)
/// → else the enabled row of the capability whose last test passed, most recently tested first → else
/// the default anyway → else <see cref="ProviderSelectionReason.None"/>.
/// </para>
/// <para>
/// This exists because a deployment's default speech provider had failed its connection test while
/// a second, working one sat unused: every narrated video was synthesized against the broken one
/// and nothing told the customer. It is deliberately NOT applied to chat (the legacy config fallback
/// and per-agent overrides already define that precedence), video generation (another row means
/// another vendor's bill) or embeddings (another model means another vector space).
/// </para>
/// </remarks>
public static class ProviderHealthSelector
{
    /// <param name="enabledProviders">The ENABLED rows (the caller filters; the resolver's snapshot already holds only enabled rows).</param>
    public static ProviderSelection Select(
        IEnumerable<ModelCapabilityView> enabledProviders,
        InferenceProviderCapability capability,
        Guid? explicitProviderId)
    {
        List<ModelCapabilityView> candidates = enabledProviders
            .Where(p => p.Capability == capability)
            .ToList();

        ModelCapabilityView? configuredDefault = candidates.FirstOrDefault(p => p.IsDefault);

        if (explicitProviderId is Guid id && candidates.FirstOrDefault(p => p.Id == id) is { } explicitProvider)
            return new ProviderSelection(explicitProvider, ProviderSelectionReason.Explicit, configuredDefault);

        if (configuredDefault is not null && configuredDefault.LastTestOk != false)
            return new ProviderSelection(configuredDefault, ProviderSelectionReason.Default, configuredDefault);

        ModelCapabilityView? healthy = candidates
            .Where(p => p.LastTestOk == true && (configuredDefault is null || p.Id != configuredDefault.Id))
            .OrderByDescending(p => p.LastTestAt ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (healthy is not null)
        {
            return new ProviderSelection(
                healthy,
                configuredDefault is null ? ProviderSelectionReason.HealthyNonDefault : ProviderSelectionReason.HealthyFallback,
                configuredDefault);
        }

        if (configuredDefault is not null)
            return new ProviderSelection(configuredDefault, ProviderSelectionReason.UnhealthyDefault, configuredDefault);

        return new ProviderSelection(null, ProviderSelectionReason.None, null);
    }
}
