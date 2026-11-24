using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// T-108: the ONE place a <see cref="StepType.AudioGenerate"/> step's credit hold is sized, the audio twin of
/// <see cref="VideoGenerateCreditPricing"/>. The hold is <c>Takes x the billed units of one take</c> (started minutes of
/// music, started seconds of a sound effect, <see cref="AudioGenerationCapabilities.BilledUnits"/>) priced through the
/// SAME <see cref="IModelPriceService"/> lookup — capability, model, unit — that the executor's
/// <c>MusicGenMinutes</c>/<c>SoundEffectGenSeconds</c> settlement feeds, so a step that delivers what it ordered settles
/// to its own hold.
/// <para>
/// <b>Never throws, never invents a price.</b> Any failure — no config, no provider row, a model the catalogue does not
/// price for the kind, no price row — answers <c>null</c> and the caller falls back to <see cref="StepCreditDefaults"/>.
/// The ledger side stays fail-closed on its own: the executor refuses an unpriceable model with <c>CONFIG_INVALID</c>
/// before any row exists.
/// </para>
/// </summary>
public static class AudioGenerateCreditPricing
{
    /// <summary>The provider row the step resolves to: its own <c>ProviderId</c>, else the kind's default row.</summary>
    public static Task<ResolvedAudioGenerationProvider?> ResolveProviderAsync(
        IInferenceProviderResolver providers, AudioGenerateStepConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind == AudioGenerationKind.Music
            ? providers.ResolveMusicGenerationAsync(config.ProviderId, ct)
            : providers.ResolveSoundEffectGenerationAsync(config.ProviderId, ct);
    }

    /// <summary>
    /// The millicredits to hold for an AudioGenerate step whose config is <paramref name="configJson"/>, or <c>null</c>
    /// when it cannot be priced (the caller falls back to <see cref="StepCreditDefaults"/>). A BYO row prices to what
    /// the price service says for BYO — the same answer the settlement gives it.
    /// </summary>
    public static async ValueTask<long?> TryHoldMillisAsync(
        string? configJson,
        IInferenceProviderResolver providers,
        IModelPriceService prices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            AudioGenerateStepConfig? config;
            try
            {
                config = string.IsNullOrWhiteSpace(configJson)
                    ? null
                    : JsonSerializer.Deserialize<AudioGenerateStepConfig>(configJson, AudioGenerateStepConfig.JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (config is null || config.Validate().Count > 0)
            {
                return null;
            }

            ResolvedAudioGenerationProvider? provider = await ResolveProviderAsync(providers, config, ct).ConfigureAwait(false);
            if (provider is null)
            {
                return null;
            }

            string? model = config.ResolveModel(provider.Kind, provider.ModelName);
            AudioEndpoint? endpoint = AudioModelCatalog.Instance.Find(provider.Kind, model);
            if (endpoint is null || endpoint.Kind != config.Kind || config.ValidateFor(endpoint).Count > 0)
            {
                return null;
            }

            decimal quantity = config.Takes * endpoint.Capabilities.BilledUnits(config.PricedDurationSec(endpoint));
            PriceQuote quote = await prices.PriceAsync(new PriceRequest(
                provider.ProviderId,
                provider.AccountId,
                provider.Kind,
                AudioModelCatalog.CapabilityOf(config.Kind),
                endpoint.Id,
                endpoint.Unit,
                null,
                MeteringChatClient.BillingModeOf(provider.OrganizationId),
                quantity), ct).ConfigureAwait(false);

            return quote.CreditsMillis;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // UnpricedUsageException, a row that will not decrypt, a database that is down: "we cannot size this
            // hold", never "this step is free".
            return null;
        }
    }
}
