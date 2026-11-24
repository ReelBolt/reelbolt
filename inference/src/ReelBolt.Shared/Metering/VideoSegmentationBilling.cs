using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The vendor's billing arithmetic for <see cref="UsageKind.VideoSegmentationFrames"/>: fal charges
/// "$0.01 per 16 frames of video input" and rounds a request UP to a whole block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the rounding lives here and not on the rate card.</b> A rate card is
/// <c>quantity x cost_usd_per_unit</c> (<see cref="RateCardService.Price"/>); it has no notion of a
/// block. Recording an un-rounded frame count against a per-frame card would therefore under-charge
/// every request whose duration is not an exact multiple of 16 frames — 120 frames would come out at
/// $0.075 instead of the $0.08 the vendor charges. The meter records the frame count rounded up to the
/// block, and the card is the block price divided by the block size, so the two agree by construction.
/// </para>
/// <para>
/// <b>Frames, not blocks, on the ledger.</b> The event row still carries a frame count (128 for a
/// 120-frame clip), not "8". The distinction between the clip's real 120 frames and the 128 billed
/// frames stays visible in <see cref="Inference.VideoSegmentationResult.InputFrames"/> and in the
/// analyze artifact; the usage row is the amount the vendor bills.
/// </para>
/// </remarks>
public static class VideoSegmentationBilling
{
    /// <summary>The vendor's billing block, in frames of video input.</summary>
    public const int FramesPerUnit = ModelPriceCatalog.FalVideoSegmentationFramesPerUnit;

    /// <summary>Whole blocks a request of <paramref name="frames"/> frames is billed as, rounded UP.</summary>
    public static int UnitsFor(int frames)
    {
        if (frames <= 0) return 0;
        return (frames + FramesPerUnit - 1) / FramesPerUnit;
    }

    /// <summary>The frame count to record so a per-frame card charges whole blocks: units x block size.</summary>
    public static int BillableFrames(int frames) => UnitsFor(frames) * FramesPerUnit;

    /// <summary>The USD the vendor would charge for a clip of <paramref name="frames"/> frames.</summary>
    public static decimal UsdFor(int frames) =>
        UnitsFor(frames) * ModelPriceCatalog.FalVideoSegmentationUsdPerUnit;
}
