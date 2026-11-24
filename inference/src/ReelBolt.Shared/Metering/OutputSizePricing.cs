using System.Globalization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Which picture calls are priced by the size of their output, and how that size is written as a price variant. One
/// rule for the image meter (the size MEASURED on the delivered picture) and for the editor's hold (the size EXPECTED
/// before submit), so the two price the same row.
/// </summary>
public static class OutputSizePricing
{
    /// <summary>The FLUX.2 family, priced per output megapixel ("the first megapixel costs a flat rate, then each additional MP adds").</summary>
    private const string Flux2Prefix = "flux-2-";

    /// <summary>
    /// FLUX.2's documented ceiling: "generates images up to 4MP (e.g., 2048x2048)" (help.bfl.ai, "What are the resolution
    /// limits?", read 2026-10-10). The vendor's own example is 2048 x 2048 = 4,194,304 pixels, so that, not 4,000,000, is
    /// the most a call can deliver (the same reading FalImageCatalog uses for fal-ai/flux-2-pro/outpaint).
    /// </summary>
    public const long Flux2MaxOutputPixels = 2048L * 2048;

    /// <summary>The pixel grid the image models deliver on: an expected side is rounded UP to a multiple of this (F4).</summary>
    public const int SizeGrid = 32;

    /// <summary>
    /// True when this call is priced by its output's size alone: a fal endpoint the catalogue marks
    /// <see cref="FalImageEndpoint.PricedPerMegapixel"/> without extra reference pictures, or BFL FLUX.2 text-to-image
    /// (an edit or a referenced generation also pays per reference MP, which the output's size does not show).
    /// </summary>
    public static bool PricesByOutputMegapixels(InferenceProviderKind kind, string? model, bool editsASource, int referenceImages) =>
        kind switch
        {
            // F5: a per-megapixel fal endpoint with reference pictures (qwen edit) is still size-priced: the output's
            // megapixels plus each reference's (MegapixelVariant), never the flat variant-less figure.
            InferenceProviderKind.FalAi => FalImageCatalog.Instance.Find(model ?? string.Empty) is { PricedPerMegapixel: true },
            InferenceProviderKind.BlackForestLabs => !editsASource && referenceImages == 0 && IsFlux2(model),
            _ => false
        };

    /// <summary>The most pixels the model's output can have, from its vendor's documentation; null when none is documented.</summary>
    public static long? MaxOutputPixels(InferenceProviderKind kind, string? model) => kind switch
    {
        InferenceProviderKind.FalAi => FalImageCatalog.Instance.Find(model ?? string.Empty)?.MaxOutputPixels,
        InferenceProviderKind.BlackForestLabs => IsFlux2(model) ? Flux2MaxOutputPixels : null,
        _ => null
    };

    /// <summary>A measured size is never billed above this many times the size the caller expected (T-013 rework 3).</summary>
    public const int ExpectedSizeClampFactor = 4;

    /// <summary>
    /// The size-priced endpoint also bills its input picture's megapixels (fal-ai/flux-2-pro/outpaint:
    /// <see cref="FalImageEndpoint.BillsInputMegapixels"/>), so the variant adds them to the output's.
    /// </summary>
    public static bool BillsInputMegapixels(InferenceProviderKind kind, string? model) =>
        kind == InferenceProviderKind.FalAi && FalImageCatalog.Instance.Find(model ?? string.Empty) is { BillsInputMegapixels: true };

    /// <summary>The endpoint's price is a flat rate per megapixel (<see cref="FalImageEndpoint.LinearPerMegapixel"/>).</summary>
    public static bool IsLinearPerMegapixel(InferenceProviderKind kind, string? model) =>
        kind == InferenceProviderKind.FalAi && FalImageCatalog.Instance.Find(model?.Trim() ?? string.Empty) is { LinearPerMegapixel: true };

    /// <summary>
    /// The most pixels a measured output is billed at (T-013 rework 3: an implausible vendor-stated or decoded size is
    /// clamped, with a Warning): the model's documented maximum output; else <see cref="ExpectedSizeClampFactor"/> times
    /// <paramref name="expectedPixels"/>; else the endpoint's fallback ceiling (<see cref="FallbackMaxOutputPixels"/>).
    /// </summary>
    public static long? BilledOutputCeiling(InferenceProviderKind kind, string? model, long? expectedPixels) =>
        MaxOutputPixels(kind, model) is { } documented and > 0
            ? documented
            : expectedPixels is { } expected and > 0
                ? expected * ExpectedSizeClampFactor
                : FallbackMaxOutputPixels(kind, model);

    /// <summary>
    /// A fixed ceiling for a size-priced endpoint that documents no maximum output, used only when the expected size is
    /// unknown. iclight-v2 and qwen-image-edit-2511: their schemas cap <c>image_size.width</c> and <c>.height</c> at
    /// 14142 (docs/fal-ai-models.md), so 14142 x 14142. seedvr: the editor's largest upscale source (5040 x 2160,
    /// UpscaleSourceLimit) at the x2 factor cap. fill and erase: docs/fal-ai-models.md states no size limit
    /// (the output matches the input), so the most pixels this server decodes at all (PngAlpha.MaxPixels, 64 MP).
    /// </summary>
    public static long? FallbackMaxOutputPixels(InferenceProviderKind kind, string? model) => kind switch
    {
        InferenceProviderKind.FalAi => model?.Trim() switch
        {
            "fal-ai/iclight-v2" or "fal-ai/qwen-image-edit-2511" => 14142L * 14142,
            "fal-ai/seedvr/upscale/image" => UpscaleSourceLimit.MaxPixels * 4,
            "fal-ai/flux-pro/v1/fill" or "fal-ai/flux-pro/v1/erase" => PngAlpha.MaxPixels,
            _ => null
        },
        _ => null
    };

    /// <summary>The largest input picture the model takes as it is, from its vendor's documentation; null when none is documented.</summary>
    public static long? MaxInputPixels(InferenceProviderKind kind, string? model) => kind switch
    {
        InferenceProviderKind.FalAi => FalImageCatalog.Instance.Find(model ?? string.Empty)?.MaxInputPixels,
        _ => null
    };

    /// <summary>
    /// The <c>{n}MP</c> variant of a call: <paramref name="outputPixels"/> in whole binary megapixels (<see cref="PixelsPerMegapixel"/>) rounded UP, plus each of
    /// <paramref name="referencePixels"/> rounded up on its own (input megapixels a per-MP endpoint bills; rounding each
    /// picture is the fail-expensive reading). Null when the output size is not positive.
    /// </summary>
    public static string? MegapixelVariant(long outputPixels, IReadOnlyList<long>? referencePixels = null)
    {
        if (outputPixels <= 0) return null;
        long megapixels = Megapixels(outputPixels);
        foreach (long reference in referencePixels ?? [])
        {
            megapixels += Megapixels(Math.Max(1, reference));
        }

        return megapixels.ToString(CultureInfo.InvariantCulture) + "MP";
    }

    /// <summary>
    /// The <c>{n}MP</c> variant of an endpoint that bills input AND output megapixels (fal-ai/flux-2-pro/outpaint:
    /// "$0.03 for the first megapixel of output, plus $0.015 per extra megapixel of input and output"). The seed's n MP
    /// row prices n MP out with n MP in (4 MP = 0.03 + 7 x 0.015), so n = max(input MP, output MP), each rounded up, is
    /// at or above the vendor's figure; and at least 2 when the output is at most 1 MP, because the 1 MP row ($0.03)
    /// prices no input. Null when the output size is not positive.
    /// </summary>
    public static string? InputAndOutputVariant(long outputPixels, long inputPixels)
    {
        if (outputPixels <= 0) return null;
        long output = Megapixels(outputPixels);
        long megapixels = Math.Max(output, Megapixels(Math.Max(1, inputPixels)));
        if (output <= 1) megapixels = Math.Max(megapixels, 2);
        return megapixels.ToString(CultureInfo.InvariantCulture) + "MP";
    }

    /// <summary>
    /// The pixels of the largest of <paramref name="images"/>, read from each picture's own header first and its stated
    /// size second; null when any of them has no readable size (or there are none).
    /// </summary>
    public static long? MeasuredPixels(IEnumerable<GeneratedImage> images)
    {
        long largest = 0;
        foreach (GeneratedImage image in images)
        {
            long pixels = ImageHeader.TryReadDimensions(image.Bytes, out int width, out int height)
                ? (long)width * height
                : image is { Width: > 0, Height: > 0 } ? (long)image.Width.Value * image.Height.Value : 0;
            if (pixels <= 0) return null;
            largest = Math.Max(largest, pixels);
        }

        return largest > 0 ? largest : null;
    }

    /// <summary>
    /// The pixels of an expected <paramref name="width"/> x <paramref name="height"/> picture with each side rounded UP to
    /// <see cref="SizeGrid"/> (F4: a vendor that rounds 1000 to 1008 or 1024 delivers more than was asked).
    /// </summary>
    public static long GridPixels(double width, double height) => GridSide(width) * GridSide(height);

    private static long GridSide(double side) =>
        Math.Max(SizeGrid, (long)Math.Ceiling(Math.Ceiling(side) / SizeGrid) * SizeGrid);

    /// <summary>
    /// One billed megapixel: 1,048,576 pixels (1024 x 1024), the "binary megapixels" decision, for
    /// every fal and BFL size-priced job, in the hold and the meter alike. 2048 x 2048 is exactly 4 MP.
    /// </summary>
    public const long PixelsPerMegapixel = 1024L * 1024;

    /// <summary><paramref name="pixels"/> in whole binary megapixels (<see cref="PixelsPerMegapixel"/>), rounded UP.</summary>
    public static long Megapixels(long pixels) => (pixels + PixelsPerMegapixel - 1) / PixelsPerMegapixel;

    private static bool IsFlux2(string? model) =>
        model is not null && model.Trim().StartsWith(Flux2Prefix, StringComparison.OrdinalIgnoreCase);
}
