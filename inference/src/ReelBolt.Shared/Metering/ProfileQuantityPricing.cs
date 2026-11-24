using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>What one profile quantity costs, and whether it runs on the organization's own provider (so is not charged).</summary>
public readonly record struct ProfileQuote(long CreditsMillis, bool OwnProvider);

/// <summary>
/// Prices one <see cref="ProfileQuantity"/> of a cold-start usage profile. Synchronous by design: the
/// pure cores (the Inference API's displayed range and <see cref="RunCreditHolds"/>) call it without
/// I/O; the live paths resolve providers and prices up front (<see cref="ProfilePriceTable"/>) and
/// hand the core a table of finished quotes. Tests price through <see cref="ModelPriceService"/> over a snapshot.
/// </summary>
public interface IProfileQuantityPricer
{
    /// <summary>
    /// Prices <paramref name="quantity"/>. For <see cref="ProfilePriceTarget.Chat"/>,
    /// <paramref name="chatAgentType"/>/<paramref name="chatAgentDefinitionId"/> name the agent whose
    /// chat provider is used (exactly the resolver's <c>ResolveAsync</c> arguments); other targets ignore them.
    /// </summary>
    ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId);

    /// <summary>
    /// As <see cref="Price"/>, but a chat quantity is priced at the agent's ORDINARY model, ignoring an app's chosen model
    /// (<see cref="AppAgentModelAmbient"/>): the model the agent's past usage was most likely metered on. Only
    /// <see cref="AppModelHoldScaling"/> asks for it, and only for an agent an app chose a model for, so every other run asks
    /// for nothing new. The default (a pricer that knows no choice) is <see cref="Price"/>.
    /// </summary>
    ProfileQuote PriceBaseline(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        => Price(quantity, chatAgentType, chatAgentDefinitionId);

    /// <summary>
    /// The markup this pricer prices at (<c>pricing_settings.markup</c> when built from the live price service, T-032):
    /// the cold-start default holds (<see cref="StepCreditDefaults.MillisFor(StepType, decimal)"/>) use it too.
    /// </summary>
    decimal Markup => PricingDefaults.DefaultMarkup;

    /// <summary>
    /// True when <paramref name="key"/> is priced from the compiled-in seed constants
    /// (<see cref="StepUsageProfiles.SeedFallbackMillis"/>) rather than from a real model capability price.
    /// A caller that must not act on a guess (the profile-build cap) refuses such a figure.
    /// </summary>
    bool IsSeedFallback(ProfilePriceKey key) => true;
}

/// <summary>
/// The seed-constant pricer: <see cref="StepUsageProfiles.SeedFallbackMillis"/> for every quantity, at
/// <paramref name="markup"/> (the default markup for <see cref="Instance"/>).
/// The fallback when a provider cannot be resolved or no price row prices a quantity, and the
/// pricer of callers with no provider or price context.
/// </summary>
public sealed class SeedProfilePricer(decimal markup = PricingDefaults.DefaultMarkup) : IProfileQuantityPricer
{
    public static SeedProfilePricer Instance { get; } = new();

    public decimal Markup => markup;

    public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        => new(StepUsageProfiles.SeedFallbackMillis(quantity.Kind, quantity.Quantity, markup), OwnProvider: false);
}

/// <summary>
/// The key one quote is stored under: chat quantities per agent, vision quantities per explicit provider
/// (<paramref name="ProviderId"/>; null is the default vision provider), every other target once.
/// </summary>
public readonly record struct ProfilePriceKey(
    UsageKind Kind, ProfilePriceTarget Target, decimal Quantity, AgentType ChatAgentType, Guid? ChatAgentDefinitionId, Guid? ProviderId = null,
    bool Baseline = false)
{
    /// <summary>
    /// The key of <paramref name="quantity"/>. <paramref name="baseline"/> marks a chat quantity priced at the agent's ordinary
    /// model (<see cref="IProfileQuantityPricer.PriceBaseline"/>); other targets ignore it. A speech quantity keeps its explicit
    /// provider (an app's chosen speech model, docs/design/app-inputs.md 28); null, as before, prices the default.
    /// </summary>
    public static ProfilePriceKey Of(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId, bool baseline = false)
        => quantity.Target switch
        {
            ProfilePriceTarget.Chat => new(quantity.Kind, quantity.Target, quantity.Quantity, chatAgentType, chatAgentDefinitionId, Baseline: baseline),
            ProfilePriceTarget.Vision or ProfilePriceTarget.SpeechSynthesis
                => new(quantity.Kind, quantity.Target, quantity.Quantity, AgentType.Custom, null, quantity.ProviderId),
            _ => new(quantity.Kind, quantity.Target, quantity.Quantity, AgentType.Custom, null)
        };
}

/// <summary>Records every quantity a core asks for, so the live path can price exactly that set up front.</summary>
public sealed class RecordingProfilePricer : IProfileQuantityPricer
{
    public HashSet<ProfilePriceKey> Requests { get; } = [];

    public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
    {
        Requests.Add(ProfilePriceKey.Of(quantity, chatAgentType, chatAgentDefinitionId));
        return SeedProfilePricer.Instance.Price(quantity, chatAgentType, chatAgentDefinitionId);
    }

    public ProfileQuote PriceBaseline(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
    {
        Requests.Add(ProfilePriceKey.Of(quantity, chatAgentType, chatAgentDefinitionId, baseline: true));
        return SeedProfilePricer.Instance.Price(quantity, chatAgentType, chatAgentDefinitionId);
    }
}

/// <summary>
/// Answers from a table of quotes priced up front; anything missing falls back to the seed constants at
/// <paramref name="markup"/> (the markup the quotes were priced at).
/// </summary>
public sealed class TableProfilePricer(
    IReadOnlyDictionary<ProfilePriceKey, ProfileQuote> quotes, decimal markup = PricingDefaults.DefaultMarkup) : IProfileQuantityPricer
{
    private readonly SeedProfilePricer _fallback = new(markup);

    public decimal Markup => markup;

    public bool IsSeedFallback(ProfilePriceKey key) => !quotes.ContainsKey(key);

    public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        => quotes.TryGetValue(ProfilePriceKey.Of(quantity, chatAgentType, chatAgentDefinitionId), out ProfileQuote quote)
            ? quote
            : _fallback.Price(quantity, chatAgentType, chatAgentDefinitionId);

    public ProfileQuote PriceBaseline(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        => quotes.TryGetValue(ProfilePriceKey.Of(quantity, chatAgentType, chatAgentDefinitionId, baseline: true), out ProfileQuote quote)
            ? quote
            : _fallback.Price(quantity, chatAgentType, chatAgentDefinitionId);
}

/// <summary>
/// Prices, up front, a set of profile quantities: the chat provider is resolved once per (agent type,
/// agent), compute once, and speech, transcription and video generation once each, then each quantity
/// goes through <see cref="IModelPriceService.PriceAsync"/> — the meter's own lookup, so the model
/// capability prices, the markup and the multipliers all flow through. A provider that cannot be resolved or
/// a quantity that cannot be priced falls back to the seed constants
/// (<see cref="StepUsageProfiles.SeedFallbackMillis"/>) for that key alone, logged, never thrown.
///
/// <para>
/// ONE implementation for the Inference API's estimate and the WorkflowEngine's hold, so the two
/// cannot price a cold step differently. Own-provider is decided by
/// <see cref="MeteringChatClient.BillingModeOf"/>, the same rule the meter uses at the call.
/// </para>
/// </summary>
public static class ProfilePriceTable
{
    /// <summary>Builds the table pricer for <paramref name="keys"/>. Never throws except on cancellation.</summary>
    public static async Task<IProfileQuantityPricer> BuildAsync(
        IReadOnlyCollection<ProfilePriceKey> keys,
        IInferenceProviderResolver? providers,
        IModelPriceService? prices,
        ILogger logger,
        CancellationToken ct)
    {
        if (prices is null)
        {
            if (keys.Count > 0)
            {
                logger.LogWarning("No model price service is available; cold-start profiles are priced at the seed constants");
            }

            return SeedProfilePricer.Instance;
        }

        // The live markup even with nothing to quote: the run's cold-start default holds are priced at it (T-032).
        decimal markup = await CurrentMarkupAsync(prices, ct).ConfigureAwait(false);
        if (keys.Count == 0)
        {
            return new SeedProfilePricer(markup);
        }

        Dictionary<ProfilePriceKey, ProfileQuote> quotes = [];
        try
        {
            Dictionary<(ProfilePriceTarget, AgentType, Guid?, Guid?, bool), PricedProvider?> resolved = [];
            bool warned = false;
            foreach (ProfilePriceKey key in keys)
            {
                (ProfilePriceTarget, AgentType, Guid?, Guid?, bool) target = (key.Target, key.ChatAgentType, key.ChatAgentDefinitionId, key.ProviderId, key.Baseline);
                if (!resolved.TryGetValue(target, out PricedProvider? provider))
                {
                    provider = await ResolveTargetAsync(providers, key, logger, ct).ConfigureAwait(false);
                    resolved[target] = provider;
                }

                if (provider is not { } p)
                {
                    continue; // the table pricer falls back to the seed constants for this key
                }

                try
                {
                    PriceQuote quote = await prices.PriceAsync(p.Request(key.Kind, key.Quantity), ct).ConfigureAwait(false);
                    quotes[key] = new ProfileQuote(quote.CreditsMillis, p.Mode != UsageBillingMode.Platform);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (!warned)
                    {
                        logger.LogWarning(ex,
                            "Pricing {Kind} on {ProviderKind}/{Model} for a cold-start profile failed; using the seed constants",
                            key.Kind, p.Kind, p.ModelCode);
                        warned = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pricing the cold-start profiles failed; using the seed constants");
        }

        return new TableProfilePricer(quotes, markup);
    }

    /// <summary>
    /// The markup a real quote is priced at now (<c>pricing_settings.markup</c>, with the live credit value folded in),
    /// read back from the price service's own USD arm: what one USD of platform cost is charged, over the at-cost rate.
    /// The default markup when the service cannot say.
    /// </summary>
    private static async Task<decimal> CurrentMarkupAsync(IModelPriceService prices, CancellationToken ct)
    {
        const decimal probeUsd = 1_000m;
        try
        {
            PriceQuote quote = await prices.PriceQuotedUsdAsync(probeUsd, UsageKind.LlmInputTokens, UsageBillingMode.Platform, ct: ct)
                .ConfigureAwait(false);
            return quote.CreditsMillis > 0 ? quote.CreditsMillis / (probeUsd * PricingDefaults.CreditsMillisPerUsd) : PricingDefaults.DefaultMarkup;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PricingDefaults.DefaultMarkup;
        }
    }

    /// <summary>The model capability, model, billing mode and price variant one price target resolves to.</summary>
    private readonly record struct PricedProvider(
        Guid? CapabilityId,
        Guid? AccountId,
        InferenceProviderKind Kind,
        InferenceProviderCapability Capability,
        string ModelCode,
        UsageBillingMode Mode,
        string? Variant = null)
    {
        /// <summary>The same request the meter would build for <paramref name="quantity"/> units of <paramref name="unit"/>.</summary>
        public PriceRequest Request(UsageKind unit, decimal quantity)
            => new(CapabilityId, AccountId, Kind, Capability, ModelCode, unit, Variant, Mode, quantity);

        public static PricedProvider Of(
            Guid? capabilityId, Guid? accountId, InferenceProviderKind kind, InferenceProviderCapability capability,
            string modelCode, Guid? organizationId)
            => new(capabilityId, accountId, kind, capability, modelCode, MeteringChatClient.BillingModeOf(organizationId));
    }

    /// <summary>Resolves one price target, or null (the seed constants then price it). Never throws except on cancellation.</summary>
    private static async Task<PricedProvider?> ResolveTargetAsync(
        IInferenceProviderResolver? providers, ProfilePriceKey key, ILogger logger, CancellationToken ct)
    {
        if (key.Target == ProfilePriceTarget.Compute)
        {
            // Compute is priced at the 1080p REFERENCE tier on purpose. The run's chosen resolution is
            // applied on top by OutputResolution.PixelFactor (ResolutionHoldScaling on the hold, the
            // estimator's display scaling on the range), and the seeded tier prices are exactly that factor
            // apart (720p = 0.444x, uhd = 4x the 1080p price; pinned by OutputResolutionEngineTests).
            // Pricing at the run's own tier here as well would count the resolution twice (16x at 4K).
            // The tier is the price VARIANT on the system compute capability, exactly as the meter records it.
            return new PricedProvider(
                WellKnownModelCatalog.ComputeCapabilityId, WellKnownModelCatalog.ComputeProviderId, ComputeMetering.ProviderKind,
                InferenceProviderCapability.Compute, WellKnownModelCatalog.ComputeModelCode, UsageBillingMode.Platform,
                ComputeMetering.Tier1080);
        }

        if (providers is null)
        {
            return null;
        }

        try
        {
            switch (key.Target)
            {
                case ProfilePriceTarget.Chat:
                    // An app's model input chose this agent's model for the run (docs/design/app-inputs.md 8.5): price that
                    // model, resolved through the same fail-closed by-id path the run uses; one that no longer resolves
                    // falls back to the agent's ordinary chain, exactly as the run would. A baseline key (AppModelHoldScaling)
                    // asks for the ordinary chain on purpose.
                    if (!key.Baseline
                        && AppAgentModelAmbient.For(key.ChatAgentDefinitionId) is { } chosenId
                        && await providers.ResolveChatAsync(chosenId, ct).ConfigureAwait(false) is { } chosen)
                    {
                        return PricedProvider.Of(chosen.ProviderId, chosen.AccountId, chosen.Kind, chosen.Capability, chosen.ModelName, chosen.OrganizationId);
                    }

                    ResolvedInferenceProvider chat = await providers.ResolveAsync(key.ChatAgentType, key.ChatAgentDefinitionId, ct).ConfigureAwait(false);
                    return PricedProvider.Of(chat.ProviderId, chat.AccountId, chat.Kind, chat.Capability, chat.ModelName, chat.OrganizationId);
                case ProfilePriceTarget.SpeechSynthesis:
                    // An app's chosen speech model (docs/design/app-inputs.md 28) names its capability; null is the default, as before.
                    return await providers.ResolveSpeechSynthesisAsync(key.ProviderId, ct).ConfigureAwait(false) is { } tts
                        ? PricedProvider.Of(tts.ProviderId, tts.AccountId, tts.Kind, InferenceProviderCapability.SpeechSynthesis, tts.ModelName, tts.OrganizationId)
                        : null;
                case ProfilePriceTarget.Transcription:
                    return await providers.ResolveTranscriptionAsync(null, ct).ConfigureAwait(false) is { } asr
                        ? PricedProvider.Of(asr.ProviderId, asr.AccountId, asr.Kind, InferenceProviderCapability.Transcription, asr.ModelName, asr.OrganizationId)
                        : null;
                case ProfilePriceTarget.Vision:
                    return await providers.ResolveVisionAsync(key.ProviderId, ct).ConfigureAwait(false) is { } vision
                        ? PricedProvider.Of(vision.ProviderId, vision.AccountId, vision.Kind, InferenceProviderCapability.Vision, vision.ModelName, vision.OrganizationId)
                        : null;
                case ProfilePriceTarget.WebSearch:
                    return await providers.ResolveWebSearchAsync(null, ct).ConfigureAwait(false) is { } search
                        ? PricedProvider.Of(search.ProviderId, search.AccountId, search.Kind, InferenceProviderCapability.WebSearch, search.ModelName, search.OrganizationId)
                        : null;
                case ProfilePriceTarget.VideoGeneration:
                    return await providers.ResolveVideoGenerationAsync(null, ct).ConfigureAwait(false) is { } video
                        ? PricedProvider.Of(video.ProviderId, video.AccountId, video.Kind, InferenceProviderCapability.VideoGeneration, video.ModelName, video.OrganizationId)
                        : null;
                default:
                    return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Resolving the {Target} provider for a cold-start profile failed; using the seed constants", key.Target);
            return null;
        }
    }
}
