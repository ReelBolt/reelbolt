using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Avatars.Builds;

/// <summary>The picture model a variant is made with: the default <c>ImageEdit</c> capability, its model and its endpoint.</summary>
public sealed record VariantPictureModel(ResolvedImageProvider Provider, string Model, FalImageEndpoint Endpoint)
{
    /// <summary>Whether the platform pays the vendor for this model (so the work is held and charged); false for a workspace's own key.</summary>
    public bool PlatformManaged => Provider.IsPlatformManaged;
}

/// <summary>One picture as it is sent: its bytes, its type and the size the model is expected to answer at.</summary>
public sealed record VariantPictureInput(byte[] Bytes, string ContentType, int? Width, int? Height)
{
    /// <summary>The picture as a data URI (never a link: the bytes sent are the bytes just checked).</summary>
    public string DataUri => "data:" + ContentType + ";base64," + Convert.ToBase64String(Bytes);

    /// <summary>The pixels on the vendors' 32-pixel grid, when the size is known.</summary>
    public long? GridPixels => Width is > 0 && Height is > 0 ? OutputSizePricing.GridPixels(Width.Value, Height.Value) : null;
}

/// <summary>What one edit answered: the picture, and what the call metered in millicredits.</summary>
public sealed record VariantPictureResult(byte[]? Bytes, string? ContentType, long MeteredMillis, string? Failure);

/// <summary>
/// The picture-model half of a variant build (docs/design/avatar-objects.md "Avatar variants", stage 1), shared by the
/// request (which model, what it costs) and the worker (the edit itself), so the hold and the charge are priced by one
/// rule. It follows the picture editor's instruction edit (<c>EditImageOperationsController</c>, operation
/// <c>instruct</c>) and the engine's still maker (<c>AvatarStillMaker</c>): the default <c>ImageEdit</c> capability,
/// fal.ai only, an instruction-edit endpoint that takes an image array, pictures scaled down to
/// <see cref="MaxPictureSidePx"/> and sent as data URIs with the "do not keep the inputs" switch.
/// </summary>
public sealed class AvatarVariantPictures
{
    /// <summary>The longest side a picture is sent at (the still maker's figure).</summary>
    public const int MaxPictureSidePx = 1536;

    /// <summary>A picture that cannot be scaled down here is sent as it is only below this size (the still maker's figure).</summary>
    public const long MaxUnscaledBytes = 8L * 1024 * 1024;

    private readonly InferenceApiDbContext _db;
    private readonly IInferenceProviderResolver _providers;
    private readonly IImageClientFactory _images;
    private readonly INonWorkflowChargeService? _charges;
    private readonly ILogger<AvatarVariantPictures> _logger;

    public AvatarVariantPictures(
        InferenceApiDbContext db,
        IInferenceProviderResolver providers,
        IImageClientFactory images,
        ILogger<AvatarVariantPictures> logger,
        INonWorkflowChargeService? charges = null)
    {
        _db = db;
        _providers = providers;
        _images = images;
        _logger = logger;
        _charges = charges;
    }

    /// <summary>Whether credits are charged in this deployment.</summary>
    public bool BillingEnabled => _charges is { Options.Enabled: true };

    /// <summary>
    /// The model a variant is made with, or why there is none: <c>409 VARIANT_PROVIDER_MISSING</c> (no default fal.ai
    /// picture-edit model) or <c>409 VARIANT_MODEL_UNSUPPORTED</c> (it cannot take several pictures, or the platform
    /// has no price for it).
    /// </summary>
    public async Task<(VariantPictureModel? Model, AvatarRefusal? Refusal)> ResolveAsync(CancellationToken ct)
    {
        ResolvedImageProvider? provider;
        try
        {
            provider = await _providers.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resolving the picture-edit model for an avatar variant failed");
            provider = null;
        }

        if (provider is null || provider.Kind != InferenceProviderKind.FalAi)
        {
            return (null, AvatarRefusal.Conflict(AvatarBuildCodes.VariantProviderMissing,
                "Making a variant needs a fal.ai picture-edit model set up as the default for picture edits, and there is none. Ask an administrator to add one."));
        }

        string rowModel = provider.ModelName?.Trim() ?? string.Empty;
        string model = rowModel.Length == 0 ? FalImageCatalog.Instance.DefaultFor(FalImageJob.Instruct).Id : rowModel;
        FalImageEndpoint? endpoint = FalImageCatalog.Instance.Find(model);
        if (endpoint is null || endpoint.Job != FalImageJob.Instruct || endpoint.ImageUrlsField is null)
        {
            return (null, AvatarRefusal.Conflict(AvatarBuildCodes.VariantModelUnsupported,
                $"The picture-edit model set up here (\"{model}\") cannot work from several pictures at once, which a variant needs. " +
                "Ask an administrator to set a model that can as the default, for example google/nano-banana-2.1/edit."));
        }

        // The picture editor's rule for a model the row does not name itself: on a platform row with billing on it must
        // be a priced capability, or it would be billed at another model's price.
        if (rowModel.Length == 0 && BillingEnabled && provider.IsPlatformManaged
            && !await EditImageOperationsController.IsPricedPlatformModelAsync(_db, provider.Kind, provider.Capability, model, ct))
        {
            return (null, AvatarRefusal.Conflict(AvatarBuildCodes.VariantModelUnsupported,
                $"The picture-edit model \"{model}\" has no price set up here, so a variant cannot be made with it. Ask an administrator to add and price it."));
        }

        return (new VariantPictureModel(provider, model, endpoint), null);
    }

    /// <summary>Whether <paramref name="model"/> bills by the size of what it delivers (its charge then settles with no ceiling).</summary>
    public static bool SizePriced(VariantPictureModel model, bool withReference) =>
        OutputSizePricing.PricesByOutputMegapixels(model.Provider.Kind, model.Model, editsASource: true, withReference ? 1 : 0);

    /// <summary>The size a picture of <paramref name="width"/> x <paramref name="height"/> is sent at (long side at most <see cref="MaxPictureSidePx"/>).</summary>
    public static (int Width, int Height)? SentSize(int? width, int? height)
    {
        if (width is not > 0 || height is not > 0) return null;
        int longSide = Math.Max(width.Value, height.Value);
        if (longSide <= MaxPictureSidePx) return (width.Value, height.Value);
        double factor = (double)MaxPictureSidePx / longSide;
        return (Math.Max(1, (int)Math.Round(width.Value * factor)), Math.Max(1, (int)Math.Round(height.Value * factor)));
    }

    /// <summary>
    /// What one edit is held at, in millicredits: one unit of the capability's billed unit, at the size the picture is
    /// expected to come back at when the model bills by size (plus the reference picture's size, when one goes along).
    /// 0 when billing is off or the workspace uses its own key.
    /// </summary>
    public async Task<long> EstimatePictureMillisAsync(
        VariantPictureModel model, (int Width, int Height)? sentSize, (int Width, int Height)? referenceSize, CancellationToken ct)
    {
        if (_charges is null || !BillingEnabled) return 0;

        bool withReference = referenceSize is not null;
        string? variant = null;
        if (SizePriced(model, withReference) && sentSize is { } size)
        {
            long pixels = OutputSizePricing.GridPixels(size.Width, size.Height);
            IReadOnlyList<long>? referencePixels = referenceSize is { } reference
                ? [OutputSizePricing.GridPixels(reference.Width, reference.Height)]
                : null;
            variant = OutputSizePricing.MegapixelVariant(pixels, referencePixels);
        }

        return await _charges.EstimateOneUnitAsync(new PriceRequest(
            model.Provider.ProviderId,
            model.Provider.AccountId,
            model.Provider.Kind,
            model.Provider.Capability,
            model.Model,
            BilledUnit(model.Provider.Capability),
            variant,
            model.Provider.IsPlatformManaged ? UsageBillingMode.Platform : UsageBillingMode.Byo,
            1m), ct);
    }

    /// <summary>
    /// Scales <paramref name="bytes"/> down for sending (a JPEG, long side at most <see cref="MaxPictureSidePx"/>). A
    /// picture that cannot be decoded here is sent as it is while it is small; a large one is not sent (null).
    /// </summary>
    public static VariantPictureInput? Prepare(byte[] bytes, string? mimeType, int? width, int? height)
    {
        byte[]? scaled = ObjectReferencePersonCheck.Downscale(bytes, MaxPictureSidePx);
        (int Width, int Height)? sent = SentSize(width, height);
        if (scaled is not null) return new VariantPictureInput(scaled, "image/jpeg", sent?.Width, sent?.Height);
        if (bytes.LongLength > MaxUnscaledBytes) return null;

        string type = string.IsNullOrWhiteSpace(mimeType) ? AvatarBuildFiles.SniffImageMime(bytes) : mimeType.Split(';')[0].Trim().ToLowerInvariant();
        return new VariantPictureInput(bytes, type, width, height);
    }

    /// <summary>
    /// One edit: <paramref name="source"/> is the picture changed, <paramref name="reference"/> (from the second picture
    /// on) the first finished result. The member's instruction goes inside the fixed frame of
    /// <see cref="AvatarVariantInstruction"/>. Never throws for a vendor failure (the picture is then skipped); what the
    /// call metered is answered either way. The caller opens the usage scope the image client meters into.
    /// </summary>
    public async Task<VariantPictureResult> EditAsync(
        VariantPictureModel model, VariantPictureInput source, VariantPictureInput? reference, string? instruction,
        long holdMillis, TimeSpan timeout, CancellationToken ct)
    {
        string prompt = AvatarVariantInstruction.ForPicture(instruction, withReference: reference is not null);
        bool sizePriced = SizePriced(model, reference is not null);
        long before = UsageScope.RunningCreditsMillis;
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(timeout);
        try
        {
            IImageClient client = _images.Get(model.Provider);
            GeneratedImageSet set = await client.EditAsync(
                new ImageEditRequest(model.Model, prompt, source.DataUri,
                    ReferenceImageUrls: reference is null ? null : [reference.DataUri])
                {
                    ReferenceImagePixels = reference?.GridPixels is { } referencePixels ? [referencePixels] : null,
                    ExpectedOutputPixels = source.GridPixels,
                    NoVendorRetention = true
                },
                bounded.Token);

            long metered = Math.Max(0, UsageScope.RunningCreditsMillis - before);
            GeneratedImage? image = set.Images.FirstOrDefault(i => i.Bytes is { Length: > 0 });
            bool billed = BillingEnabled && model.PlatformManaged;
            // The vendor delivered a picture: a billed call is never free. Usage the platform could not price, nothing
            // metered, or (size-priced) a picture whose size could not be measured is charged at what was held for it.
            bool meteringFailed = image is not null
                                  && (UsageScope.Current?.SawUnpricedPlatformUsage == true
                                      || metered <= 0
                                      || (sizePriced && OutputSizePricing.MeasuredPixels([image]) is null));
            if (billed && meteringFailed) metered = Math.Max(metered, holdMillis);

            return image is null
                ? new VariantPictureResult(null, null, metered, "the model answered without a picture")
                : new VariantPictureResult(image.Bytes, AvatarBuildFiles.SniffImageMime(image.Bytes), metered, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The vendor's own words stay in the log; the job only notes that this picture was skipped.
            _logger.LogWarning(ex, "A variant picture edit on {Model} failed", model.Model);
            return new VariantPictureResult(null, null, Math.Max(0, UsageScope.RunningCreditsMillis - before),
                ex is OperationCanceledException or TimeoutException ? "the model did not answer in time" : "the model could not make it");
        }
    }

    private static UsageKind BilledUnit(InferenceProviderCapability capability) =>
        ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? units) && units.Length > 0
            ? units[0]
            : UsageKind.ImageEditImages;
}
