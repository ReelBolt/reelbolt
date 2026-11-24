using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.Metering;

/// <summary>What one picture of an ImageGenerate step costs on the resolved row.</summary>
/// <param name="Model">The concrete model the request is built against.</param>
/// <param name="BillingKey">The price-book model key (a tier-priced endpoint carries its tier).</param>
/// <param name="CreditsMillis">Millicredits charged to the tenant per picture (0 on an organization's own row).</param>
/// <param name="CostUsd">The vendor's cost per picture in USD, platform-rate (what the ledger and the step cap are measured in).</param>
/// <param name="Variant">The megapixel variant the price was read at, or null.</param>
public readonly record struct ImageUnitPrice(string Model, string BillingKey, long CreditsMillis, decimal CostUsd, string? Variant);

/// <summary>
/// The one place an ImageGenerate step's credits are sized: provider, model and per-picture price. The engine's hold
/// (<see cref="TryHoldMillisAsync"/>), the executor's ledger rows and the step estimate all read it, so they agree
/// by construction. Pictures are priced from the price book (<see cref="UsageKind.ImageGenImages"/> for Generate,
/// <see cref="UsageKind.ImageEditImages"/> for Edit) times the number of pictures.
/// </summary>
public static class ImageGenerateCreditPricing
{
    /// <summary>The capability the step's mode is resolved under.</summary>
    public static InferenceProviderCapability CapabilityOf(ImageGenerateMode mode) =>
        mode == ImageGenerateMode.Edit ? InferenceProviderCapability.ImageEdit : InferenceProviderCapability.ImageGeneration;

    /// <summary>The usage kind a picture of the step's mode meters under.</summary>
    public static UsageKind UnitOf(ImageGenerateMode mode) =>
        mode == ImageGenerateMode.Edit ? UsageKind.ImageEditImages : UsageKind.ImageGenImages;

    /// <summary>The explicit row, else the capability's default row, else null.</summary>
    public static Task<ResolvedImageProvider?> ResolveProviderAsync(
        IInferenceProviderResolver providers, ImageGenerateStepConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(config);
        return config.Mode == ImageGenerateMode.Edit
            ? providers.ResolveImageEditAsync(config.ProviderId, ct)
            : providers.ResolveImageGenerationAsync(config.ProviderId, ct);
    }

    /// <summary>
    /// The concrete model: a fal.ai row's own model when it is a catalogued endpoint for the job, else the catalogue's
    /// default for the job; any other kind uses the row's model.
    /// </summary>
    public static string ResolveModel(ImageGenerateMode mode, ResolvedImageProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Kind != InferenceProviderKind.FalAi)
            return provider.ModelName?.Trim() ?? string.Empty;

        InferenceProviderCapability capability = CapabilityOf(mode);
        FalImageJob job = ImageJobCapabilities.JobFor(capability)!.Value;
        string model = ImageJobCapabilities.FalModelFor(job, provider.ModelName);
        return model.Length > 0 ? model : ImageJobCapabilities.FalDefaultFor(capability)?.Id ?? string.Empty;
    }

    /// <summary>The key the price book carries the model under (a tier-priced fal endpoint is <c>{id}#{tier}</c>).</summary>
    public static string BillingKey(InferenceProviderKind kind, string model, string? plannedTier = null)
    {
        if (kind == InferenceProviderKind.FalAi
            && FalImageCatalog.Instance.Find(model) is { BillingTierField: { } tierField } endpoint
            && endpoint.FieldNamed(tierField) is { } field)
        {
            return model + "#" + (plannedTier ?? field.VendorDefault);
        }

        return model;
    }

    /// <summary>
    /// The pixels a size-priced model is held at: the requested size, else the model's documented maximum; null when
    /// the model is not size-priced or documents no maximum (the price book's variant-less, largest row then applies).
    /// </summary>
    public static long? ExpectedPixels(ImageGenerateStepConfig config, InferenceProviderKind kind, string model)
    {
        if (!OutputSizePricing.PricesByOutputMegapixels(kind, model, config.Mode == ImageGenerateMode.Edit, 0))
            return null;
        if (config.Width is > 0 && config.Height is > 0)
            return OutputSizePricing.GridPixels(config.Width.Value, config.Height.Value);
        return OutputSizePricing.MaxOutputPixels(kind, model);
    }

    /// <summary>
    /// The price of ONE picture. The tenant credits follow the row's billing mode; the vendor cost is always read at the
    /// platform rate so a ledger row is never zero.
    /// </summary>
    /// <exception cref="UnpricedUsageException">The price book has no platform price for the model.</exception>
    public static async Task<ImageUnitPrice> PriceOneAsync(
        IModelPriceService prices, ImageGenerateStepConfig config, ResolvedImageProvider provider, string model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(provider);

        // The tier the step will actually send (ImageOutputPlanner), so hold, estimate, ledger and usage agree.
        string key = BillingKey(provider.Kind, model, ImageOutputPlanner.PlanFor(config, provider.Kind, model).Tier);
        long? pixels = ExpectedPixels(config, provider.Kind, model);
        // A tier-priced endpoint's key is "code#tier": the code is the model, the tier is the price variant (the same split
        // the editor's metering decorator makes). Sending the whole key as the model code matched no capability and
        // fell through to the "fail expensive" row, i.e. the dearest tier (4K) for a 1K call.
        (string code, string? tier) = MeteringImageClient.SplitBillingKey(key);
        string? variant = tier ?? (pixels is { } px ? OutputSizePricing.MegapixelVariant(px) : null);
        InferenceProviderCapability capability = CapabilityOf(config.Mode);
        UsageKind unit = UnitOf(config.Mode);

        PriceQuote platform = await prices.PriceAsync(new PriceRequest(
            provider.ProviderId, provider.AccountId, provider.Kind, capability, code, unit, variant,
            UsageBillingMode.Platform, 1m), ct).ConfigureAwait(false);

        long credits = platform.CreditsMillis;
        if (MeteringChatClient.BillingModeOf(provider.OrganizationId) != UsageBillingMode.Platform)
            credits = 0;

        return new ImageUnitPrice(model, key, credits, platform.CostUsd, platform.Variant ?? variant);
    }

    /// <summary>
    /// The step's credit hold in millicredits: pictures x the per-picture price. <c>null</c> (never 0) when it cannot be
    /// sized: no config, an invalid config, no provider, no price.
    /// </summary>
    public static async ValueTask<long?> TryHoldMillisAsync(
        string? configJson, IInferenceProviderResolver providers, IModelPriceService prices, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(prices);

        try
        {
            ImageGenerateStepConfig? config;
            try
            {
                config = string.IsNullOrWhiteSpace(configJson)
                    ? null
                    : JsonSerializer.Deserialize<ImageGenerateStepConfig>(configJson, ImageGenerateStepConfig.JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }

            if (config is null || config.Validate().Count > 0)
                return null;

            ResolvedImageProvider? provider = await ResolveProviderAsync(providers, config, ct).ConfigureAwait(false);
            if (provider is null)
                return null;

            string model = ResolveModel(config.Mode, provider);
            if (model.Length == 0)
                return null;

            ImageUnitPrice price = await PriceOneAsync(prices, config, provider, model, ct).ConfigureAwait(false);
            return price.CreditsMillis * config.ImageCount;
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
