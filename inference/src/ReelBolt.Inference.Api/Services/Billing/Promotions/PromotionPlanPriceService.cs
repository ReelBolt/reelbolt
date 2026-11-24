using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// One plan's price while an automatic plan discount is live. <see cref="Original"/> and
/// <see cref="Discounted"/> are the provider's own formatted strings, verbatim. Never carries a code or an id.
/// </summary>
public sealed record PlanPriceItem(
    string PlanKey,
    bool PerSeat,
    string CurrencyCode,
    string Original,
    string Discounted,
    string? Headline,
    string DurationKind,
    int? DurationMonths,
    DateTime? EndsAt,
    long? OriginalMinor = null,
    long? DiscountedMinor = null,
    int? MinorUnitExponent = null);

/// <summary>The public, anonymous "what would a fresh buyer pay" view of the live automatic plan discounts.</summary>
public interface IPromotionPlanPriceService
{
    /// <summary>
    /// Priced items for the purchasable plans an automatic plan discount applies to. Cached for 60 s
    /// (an empty result too). Fails quiet: a plan whose price cannot be shown exactly is left out.
    /// </summary>
    Task<IReadOnlyList<PlanPriceItem>> GetAsync(CancellationToken ct);
}

/// <summary>
/// Uses the same selection checkout uses for an automatic plan discount (an active, code-less, in-window
/// <see cref="PromotionKind.PlanDiscount"/> whose target price ids include the plan's price), without the
/// per-org caps, and prices each plan with the provider's pricing preview.
/// </summary>
public sealed class PromotionPlanPriceService(
    InferenceApiDbContext db,
    IEnumerable<IBillingProvider> providers,
    IPromotionService promotions,
    IMemoryCache cache,
    ILogger<PromotionPlanPriceService> logger,
    Microsoft.Extensions.Options.IOptions<PromotionOptions>? promotionOptions = null,
    Microsoft.Extensions.Options.IOptions<BillingProviderOptions>? providerSelection = null) : IPromotionPlanPriceService
{
    private readonly PromotionOptions _promotionOptions = promotionOptions?.Value ?? new PromotionOptions();

    public const string CacheKey = "promotions:plan-prices";
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    // Under the site proxy's 3 s upstream timeout, so a slow Paddle answer still lands in one page view.
    public static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(2.5);

    private static readonly SemaphoreSlim SingleFlight = new(1, 1);

    public async Task<IReadOnlyList<PlanPriceItem>> GetAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<PlanPriceItem>? hit) && hit is not null)
        {
            return hit;
        }

        await SingleFlight.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(CacheKey, out hit) && hit is not null)
            {
                return hit;
            }

            // The build runs under its own tokens (the 5 s preview timeout), never the caller's: an aborted
            // request must not discard a finished round of Paddle previews and let the next caller repeat it
            // (S-P2). Only the wait above honours the caller's token.
            IReadOnlyList<PlanPriceItem> items;
            try
            {
                items = await BuildAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Fail quiet (S-P4): a database or provider-selection failure shows no discount, never a 5xx,
                // and is cached like any other result so a failing dependency is not retried per request.
                logger.LogWarning("Public promotion prices could not be built ({Error}); showing none.", ex.GetType().Name);
                items = [];
            }

            cache.Set(CacheKey, items, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheLifetime });
            return items;
        }
        finally
        {
            SingleFlight.Release();
        }
    }

    private async Task<IReadOnlyList<PlanPriceItem>> BuildAsync(CancellationToken ct)
    {
        if (BillingProviderSelector.Active(providers, providerSelection?.Value) is not { } provider)
        {
            return [];
        }

        IReadOnlyList<Promotion> active = await promotions.ActiveAutomaticAsync(ct);
        List<Promotion> discounts = [];
        DateTime openSince = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _promotionOptions.CheckoutReservationMinutes));
        foreach (Promotion p in active.Where(p => p.Kind == PromotionKind.PlanDiscount && !string.IsNullOrEmpty(p.ProviderDiscountId)))
        {
            // A promotion whose global cap is used up (completed uses plus open checkout reservations, the
            // count checkout refuses on) is not shown: the card must never promise a discount checkout
            // would not give (S-P3).
            if (p.MaxRedemptions is { } max)
            {
                int open = await db.PromotionCheckouts.AsNoTracking()
                    .Where(c => c.CompletedAt == null && c.SupersededAt == null && c.CreatedAt > openSince && c.DiscountPromotionId == p.Id)
                    .CountAsync(ct);
                if (p.RedemptionCount + open >= max)
                {
                    continue;
                }
            }

            discounts.Add(p);
        }
        if (discounts.Count == 0)
        {
            return [];
        }

        List<Plan> plans = await db.Plans.AsNoTracking()
            .Where(p => p.IsActive && (p.Kind == PlanKind.Subscription || p.Kind == PlanKind.PerSeat))
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        List<(Plan Plan, string PriceId, Promotion Promotion)> targets = [];
        foreach (Plan plan in plans)
        {
            if (BillingEventProcessor.PriceIdFor(plan, provider.Name, BillingInterval.Monthly) is not { } priceId)
            {
                continue;
            }

            // Oldest first, like checkout's automatic selection.
            Promotion? promo = discounts.FirstOrDefault(d => PromotionRules.ReadStrings(d.TargetPriceIdsJson).Contains(priceId, StringComparer.Ordinal));
            if (promo is not null)
            {
                targets.Add((plan, priceId, promo));
            }
        }

        PlanPriceItem?[] priced = await Task.WhenAll(targets.Select(t => PriceAsync(provider, t.Plan, t.PriceId, t.Promotion, ct)));
        return priced.OfType<PlanPriceItem>().ToList();
    }

    private async Task<PlanPriceItem?> PriceAsync(IBillingProvider provider, Plan plan, string priceId, Promotion promo, CancellationToken ct)
    {
        // A per-seat price has a minimum quantity at the provider (Team: its MinSeats), so a one-seat
        // preview is refused. Preview the smallest purchasable quantity and show Paddle's own per-unit
        // figures (formatted_unit_totals): never a line total divided here.
        bool perSeat = plan.Kind == PlanKind.PerSeat;
        int quantity = perSeat ? Math.Max(1, plan.MinSeats ?? 1) : 1;
        PricePreview preview;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PreviewTimeout);
            preview = await provider.PreviewPriceAsync([new BillingLineItem(priceId, quantity)], promo.ProviderDiscountId, timeout.Token);
        }
        catch (BillingProviderException ex)
        {
            // Paddle's own error, sanitised (status, code, a short detail with addresses masked), so a
            // refused preview is diagnosable from the log alone.
            return Drop(plan.Key, $"preview_failed:{ex.Status}:{ex.Code}:{SanitiseDetail(ex.Detail)}");
        }
        catch (Exception ex)
        {
            // Not a Paddle error envelope (timeout, transport, malformed body): the type only.
            return Drop(plan.Key, $"preview_failed:{ex.GetType().Name}");
        }

        if (preview.RawTax != "0")
        {
            return Drop(plan.Key, "tax_not_zero");
        }

        if (preview.RawDiscount is null or "0")
        {
            return Drop(plan.Key, "no_discount");
        }

        if (promo.DurationKind is not { } duration)
        {
            return Drop(plan.Key, "no_duration");
        }

        string? original = perSeat ? preview.UnitSubtotal : preview.Subtotal;
        string? discounted = perSeat ? preview.UnitTotal : preview.Total;
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(discounted)
            || string.IsNullOrEmpty(preview.CurrencyCode))
        {
            return Drop(plan.Key, perSeat ? "no_unit_totals" : "empty_value");
        }

        return new PlanPriceItem(
            plan.Key,
            perSeat,
            preview.CurrencyCode,
            original,
            discounted,
            string.IsNullOrWhiteSpace(promo.PublicHeadline) ? null : promo.PublicHeadline,
            PromotionRules.Camel(duration),
            promo.DurationMonths,
            PromotionRules.Utc(promo.EndsAt),
            perSeat ? preview.UnitSubtotalMinor : preview.SubtotalMinor,
            perSeat ? preview.UnitTotalMinor : preview.TotalMinor,
            preview.MinorUnitExponent);
    }

    /// <summary>
    /// Paddle's error detail made safe for a log line: control characters removed, anything shaped like
    /// an email address masked, and capped at 200 characters.
    /// </summary>
    public static string SanitiseDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "-";
        }

        string flat = new(detail.Where(c => !char.IsControl(c)).ToArray());
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"[^\s@()<>,;\[\]""]+@[^\s@()<>,;\[\]""]+", "[email]", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return flat.Length > 200 ? flat[..200] : flat;
    }

    private PlanPriceItem? Drop(string planKey, string reason)
    {
        logger.LogWarning("Plan price for {PlanKey} left out of the public promotion prices: {Reason}", planKey, reason);
        return null;
    }
}
