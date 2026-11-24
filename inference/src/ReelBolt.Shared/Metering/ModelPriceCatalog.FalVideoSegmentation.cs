using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

// The fal.ai TEXT-PROMPTED VIDEO segmentation endpoint, priced in the vendor's own unit: USD per FRAME of
// video input. fal publishes a per-BLOCK sentence ("$0.01 per 16 frames of video input") and rounds a
// request up to a whole block, and a rate card keys on the model name and multiplies a linear quantity,
// so the card is the sentence divided by its block size and VideoSegmentationBilling rounds the quantity
// UP to that block before it is recorded. 120 frames therefore bill 8 blocks / 128 frame-units = $0.08,
// and 1 frame bills one block = $0.01, which is the vendor's own arithmetic.
public static partial class ModelPriceCatalog
{
    /// <summary>The one endpoint this capability ships: fal's SAM 3.1 video RLE track.</summary>
    public const string FalVideoSegmentationModel = "fal-ai/sam-3-1/video-rle";

    /// <summary>The vendor's billing block, in frames of video input.</summary>
    public const int FalVideoSegmentationFramesPerUnit = 16;

    /// <summary>The vendor's published price of one whole block.</summary>
    public const decimal FalVideoSegmentationUsdPerUnit = 0.01m;

    /// <summary>
    /// The per-FRAME price a rate card can express: the vendor's block price divided by its block size.
    /// <see cref="VideoSegmentationBilling.BillableFrames"/> rounds a request up to whole blocks first, so
    /// <c>billableFrames x this</c> is always an exact number of published blocks.
    /// </summary>
    /// <remarks>
    /// A PROPERTY, not a static readonly field: <see cref="ModelPriceCatalog.All"/> reads it from another
    /// part of this partial class while that type's own static initialisers run, and partial-file field
    /// order is not a guarantee C# makes. A computed property is always evaluated after both constants.
    /// </remarks>
    public static decimal FalVideoSegmentationUsdPerFrame =>
        FalVideoSegmentationUsdPerUnit / FalVideoSegmentationFramesPerUnit;

    /// <summary>Source line for the sentence, the page and the date it was read.</summary>
    public const string FalVideoSegmentationSource =
        "published vendor price, fal.ai/models/fal-ai/sam-3-1/video-rle, read 2026-10-18";

    private static IEnumerable<ModelPrice> FalVideoSegmentationPrices()
    {
        yield return new ModelPrice(
            InferenceProviderKind.FalAi,
            InferenceProviderCapability.VideoSegmentation,
            FalVideoSegmentationModel,
            new Dictionary<UsageKind, decimal>
            {
                [UsageKind.VideoSegmentationFrames] = FalVideoSegmentationUsdPerFrame
            },
            FalVideoSegmentationSource + ": \"Your request will cost $0.01 per 16 frames of video input.\" " +
            "The card is that sentence divided by its 16-frame block ($0.000625 per frame) because a rate " +
            "card multiplies a linear quantity; VideoSegmentationBilling rounds the recorded frame count UP " +
            "to whole 16-frame blocks first, so 120 frames bill 8 blocks = $0.08 and 1 frame bills one " +
            "block = $0.01, exactly as the vendor bills them. No second endpoint is priced: a model-keyed " +
            "card cannot express a different model's per-second or per-frame rate, and none has been read.");
    }
}
