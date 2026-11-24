namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Ready-made promotion bodies the admin form can prefill. A preset is only a
/// <see cref="CreatePromotionDto"/>: creating it goes through the normal <see cref="IPromotionService"/>
/// path, so there is no special case in billing.
/// </summary>
public static class PromotionPresets
{
    /// <summary>Founder price closes to NEW subscribers at 2027-01-31 23:59 Europe/Rome (= 22:59 UTC).</summary>
    public static readonly DateTime FounderPriceEndsAt = new(2027, 1, 31, 22, 59, 0, DateTimeKind.Utc);

    /// <summary>
    /// Founder price: 50% off every sellable plan, automatic (no code), kept for as long as the subscription
    /// stays active (duration <c>forever</c>). A fresh instance each call so callers may edit it.
    /// </summary>
    public static CreatePromotionDto FounderPrice => new()
    {
        Kind = "planDiscount",
        Name = "Founder price",
        PublicHeadline = "Founder price: 50% off",
        DiscountType = "percentage",
        DiscountAmount = "50",
        Duration = "forever",
        EndsAt = FounderPriceEndsAt,
    };
}
