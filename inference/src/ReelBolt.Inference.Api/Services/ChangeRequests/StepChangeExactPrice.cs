using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>The exact price of the re-made part of a step, in millicredits, with a short plain note.</summary>
public sealed record StepChangeExactPrice(long Millis, string Note);

/// <summary>
/// The exact price of just the items a step change re-makes (video clips, music takes, narration lines), from prices
/// we hold: the same provider row, model and price row the executor settles with. Never calls a vendor; anything that
/// cannot be priced exactly (unknown price, oracle-priced vendor, unreadable output, a whole-step redo) is null.
/// </summary>
public static class StepChangeExactPriceCalculator
{
    public static async Task<StepChangeExactPrice?> TryComputeAsync(
        WorkflowStep step, ChangeRequestPayload payload, string? outputJson,
        IInferenceProviderResolver providers, IModelPriceService prices, CancellationToken ct)
    {
        StepChangeScope.StepChangeSelection? selection = StepChangeScope.SelectRedone(step.StepType, payload, outputJson);
        if (selection is null)
            return null;
        try
        {
            long? millis = step.StepType switch
            {
                StepType.VideoGenerate => await VideoAsync(step.VideoGenerateConfigJson, selection, providers, prices, ct),
                StepType.AudioGenerate => await AudioAsync(step.AudioGenerateConfigJson, selection, providers, prices, ct),
                StepType.Voiceover => await VoiceoverAsync(step.VoiceoverConfigJson, selection, providers, prices, ct),
                _ => null
            };
            if (millis is not { } m || m <= 0)
                return null;
            string what = step.StepType switch
            {
                StepType.VideoGenerate => "the re-made clips",
                StepType.AudioGenerate => "the re-made takes",
                _ => "the re-made lines"
            };
            return new StepChangeExactPrice(m, $"Priced from {what} at the current rate.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // UnpricedUsageException, an unreadable config, a provider row that will not decrypt: not exactly computable.
            return null;
        }
    }

    private static async Task<long?> VideoAsync(
        string? configJson, StepChangeScope.StepChangeSelection selection,
        IInferenceProviderResolver providers, IModelPriceService prices, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        VideoGenerateStepConfig? config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(configJson, VideoGenerateStepConfig.JsonOptions);
        if (config is null)
            return null;
        ResolvedVideoGenerationProvider? provider = await LipSyncStepResolution.ResolveAsync(providers, config, ct);
        // An oracle-priced vendor needs a live quote: not exact from the price book.
        if (provider is null || provider.Kind == InferenceProviderKind.Higgsfield)
            return null;
        LipSyncStepResolution.Apply(config, provider);
        PriceQuote quote = await prices.PriceAsync(new PriceRequest(
            provider.ProviderId, provider.AccountId, provider.Kind, LipSyncStepResolution.CapabilityFor(config),
            string.IsNullOrWhiteSpace(config.Model) ? provider.ModelName : config.Model,
            UsageKind.VideoGenSeconds, string.IsNullOrWhiteSpace(config.Resolution) ? null : config.Resolution,
            MeteringChatClient.BillingModeOf(provider.OrganizationId),
            (decimal)selection.Redone.Sum(r => r.Weight)), ct);
        return quote.CreditsMillis;
    }

    private static async Task<long?> AudioAsync(
        string? configJson, StepChangeScope.StepChangeSelection selection,
        IInferenceProviderResolver providers, IModelPriceService prices, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        AudioGenerateStepConfig? config = JsonSerializer.Deserialize<AudioGenerateStepConfig>(configJson, AudioGenerateStepConfig.JsonOptions);
        if (config is null || config.Validate().Count > 0)
            return null;
        ResolvedAudioGenerationProvider? provider = await AudioGenerateCreditPricing.ResolveProviderAsync(providers, config, ct);
        if (provider is null)
            return null;
        string? model = config.ResolveModel(provider.Kind, provider.ModelName);
        AudioEndpoint? endpoint = AudioModelCatalog.Instance.Find(provider.Kind, model);
        if (endpoint is null || endpoint.Kind != config.Kind)
            return null;
        decimal units = selection.Redone.Sum(r => endpoint.Capabilities.BilledUnits(r.Weight));
        PriceQuote quote = await prices.PriceAsync(new PriceRequest(
            provider.ProviderId, provider.AccountId, provider.Kind, AudioModelCatalog.CapabilityOf(config.Kind), endpoint.Id,
            endpoint.Unit, null, MeteringChatClient.BillingModeOf(provider.OrganizationId), units), ct);
        return quote.CreditsMillis;
    }

    private static async Task<long?> VoiceoverAsync(
        string? configJson, StepChangeScope.StepChangeSelection selection,
        IInferenceProviderResolver providers, IModelPriceService prices, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;
        VoiceoverStepConfig? config = VoiceoverStepConfig.Parse(configJson);
        if (config is null)
            return null;
        ResolvedSpeechSynthesisProvider? provider = await providers.ResolveSpeechSynthesisAsync(config.ProviderId, ct);
        if (provider is null)
            return null;
        long bytes = 0;
        foreach (StepChangeScope.StepChangeRedoneItem r in selection.Redone)
        {
            string? text = r.Item.Text;
            if (r.Override is not null && r.Override.TryGetValue("text", out JsonElement t)
                && t.ValueKind == JsonValueKind.String && t.GetString() is { } edited
                && edited.Length > (text?.Length ?? 0))
                text = edited;
            if (string.IsNullOrWhiteSpace(text))
                return null; // a line priced by seconds, not text: not exact
            bytes += Encoding.UTF8.GetByteCount(text);
        }
        PriceQuote quote = await prices.PriceAsync(new PriceRequest(
            provider.ProviderId, provider.AccountId, provider.Kind, InferenceProviderCapability.SpeechSynthesis,
            string.IsNullOrWhiteSpace(config.Model) ? provider.ModelName : config.Model,
            UsageKind.TtsInputBytes, null, MeteringChatClient.BillingModeOf(provider.OrganizationId), bytes), ct);
        return quote.CreditsMillis;
    }
}
