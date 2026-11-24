using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The ONE place the hosted audio-cleanup (voice isolation) estimate and credit hold are sized, the twin of
/// <see cref="AudioGenerateCreditPricing"/>. The metered quantity is the source's audio SECONDS rounded UP to whole
/// MINUTES (<see cref="UsageKind.AudioCleanupMinutes"/>; a started minute is a unit), priced through the SAME
/// <see cref="IModelPriceService"/> lookup (capability, model, unit) that settlement uses, so the hold is
/// <c>price x minutes rounded up x the platform margin</c> and a job that delivers what it ordered settles to its hold.
/// <para>
/// <b>Fail closed.</b> A model with no catalogue entry, no price row, a request over the length cap, or any failure
/// answers <c>null</c>: "unofferable", never "free". No default price exists.
/// </para>
/// </summary>
public static class AudioCleanupCreditPricing
{
    /// <summary>Default of <c>Cleanup:Hosted:MaxSourceMinutes</c>: the longest source one hosted request may carry.</summary>
    public const int DefaultMaxSourceMinutes = 30;

    /// <summary>The configuration key that overrides <see cref="DefaultMaxSourceMinutes"/>.</summary>
    public const string MaxSourceMinutesKey = "Cleanup:Hosted:MaxSourceMinutes";

    /// <summary>
    /// Default of <c>Cleanup:Local:MaxSourceMinutes</c>: the longest source one LOCAL (free, offline) cleanup may carry.
    /// Larger than the hosted cap because no vendor request or credit hold is involved.
    /// </summary>
    public const int DefaultLocalMaxSourceMinutes = 60;

    /// <summary>The configuration key that overrides <see cref="DefaultLocalMaxSourceMinutes"/>.</summary>
    public const string LocalMaxSourceMinutesKey = "Cleanup:Local:MaxSourceMinutes";

    /// <summary>
    /// The cap actually applied: the deployment's own value (never above <paramref name="defaultMinutes"/>) and then the
    /// step's <c>MaxSourceMinutes</c>, which can only LOWER it. A non-positive deployment value falls back to the default.
    /// </summary>
    public static int EffectiveMaxMinutes(int? stepMaxSourceMinutes, int? deploymentMaxSourceMinutes, int defaultMinutes)
    {
        int server = deploymentMaxSourceMinutes is { } configured and > 0 ? configured : defaultMinutes;
        server = Math.Min(server, defaultMinutes);
        return stepMaxSourceMinutes is { } own ? Math.Min(own, server) : server;
    }

    /// <summary>Started minutes of <paramref name="sourceSeconds"/> (exact, never pre-rounded): 60 s is 1, 60.001 s is 2.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public static decimal BilledMinutes(double sourceSeconds)
    {
        if (!(sourceSeconds > 0) || double.IsInfinity(sourceSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeconds), sourceSeconds, "A billed duration must be positive.");
        }

        return Math.Ceiling((decimal)sourceSeconds / 60m);
    }

    /// <summary>True when <paramref name="sourceSeconds"/> is within the cap (<paramref name="maxSourceMinutes"/> minutes, inclusive).</summary>
    public static bool WithinCap(double sourceSeconds, int maxSourceMinutes = DefaultMaxSourceMinutes) =>
        sourceSeconds > 0 && !double.IsInfinity(sourceSeconds) && maxSourceMinutes > 0
        && (decimal)sourceSeconds <= maxSourceMinutes * 60m;

    /// <summary>The vendor-list USD of one isolation of <paramref name="sourceSeconds"/> at <paramref name="endpoint"/>'s price.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Over the cap or not positive.</exception>
    /// <exception cref="InvalidOperationException">The price yields a non-positive estimate (fail closed).</exception>
    public static decimal EstimateUsd(AudioIsolationEndpoint endpoint, double sourceSeconds, int maxSourceMinutes = DefaultMaxSourceMinutes)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!WithinCap(sourceSeconds, maxSourceMinutes))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSeconds), sourceSeconds,
                $"Hosted cleanup takes a source of up to {maxSourceMinutes} minutes.");
        }

        decimal estimate = BilledMinutes(sourceSeconds) * endpoint.UsdPerMinute;
        if (estimate <= 0m)
        {
            throw new InvalidOperationException(
                $"The price of '{endpoint.Id}' yields a non-positive estimate ({estimate}); the model cannot be offered.");
        }

        return estimate;
    }

    /// <summary>
    /// The credit hold of an AudioCleanup STEP from its config alone: <c>0</c> for a local target (free: nothing to hold),
    /// the price-book hold of the EFFECTIVE CAP (the step's own <c>MaxSourceMinutes</c>, never above <see cref="DefaultMaxSourceMinutes"/>) for a
    /// hosted one, and <c>null</c> when it cannot be priced (no provider, no catalogue entry, no price row, a config that
    /// does not parse): fail closed, the caller then falls back to the cold-start row and the executor refuses the run.
    /// </summary>
    public static async ValueTask<long?> TryStepHoldMillisAsync(
        string? audioCleanupConfigJson,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioCleanupConfigJson)) return null;

        AudioCleanupStepConfig? config;
        try
        {
            config = System.Text.Json.JsonSerializer.Deserialize<AudioCleanupStepConfig>(audioCleanupConfigJson, AudioCleanupStepConfig.JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (config is null || !Enum.IsDefined(config.Target)) return null;
        if (!config.IsHosted) return 0L;

        // The hold is the honest worst case: the effective cap (the step's own, never above the platform cap).
        int minutes = Math.Min(config.MaxSourceMinutes ?? DefaultMaxSourceMinutes, DefaultMaxSourceMinutes);
        return await TryHoldMillisAsync(minutes * 60d, config.ProviderId, providers, prices, DefaultMaxSourceMinutes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The millicredits to estimate for (and hold on) an isolation of <paramref name="sourceSeconds"/> on the provider
    /// <paramref name="explicitProviderId"/> (or the default AudioIsolation row), or <c>null</c> when it cannot be
    /// priced: no provider, a model with no catalogue entry or no price row, a source over the cap.
    /// </summary>
    public static async ValueTask<long?> TryHoldMillisAsync(
        double sourceSeconds,
        Guid? explicitProviderId,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        int maxSourceMinutes = DefaultMaxSourceMinutes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            if (!WithinCap(sourceSeconds, maxSourceMinutes)) return null;

            ResolvedAudioGenerationProvider? provider = await providers.ResolveAudioIsolationAsync(explicitProviderId, ct).ConfigureAwait(false);
            if (provider is null) return null;

            AudioIsolationEndpoint? endpoint = AudioIsolationModelCatalog.Instance.Find(provider.Kind, provider.ModelName);
            if (endpoint is null) return null;

            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                InferenceProviderCapability.AudioIsolation,
                endpoint.Id,
                endpoint.Unit,
                null,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                BilledMinutes(sourceSeconds)), ct).ConfigureAwait(false);

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // UnpricedUsageException, a row that will not decrypt, a database that is down: "cannot size this", never "free".
            return null;
        }
    }
}
