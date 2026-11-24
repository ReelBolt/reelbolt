using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <inheritdoc />
/// <remarks>
/// <para>
/// <b>Which capability prices a unit</b> (docs/design/model-catalog.md §5.3), the first step with an effective row
/// for the unit wins:
/// </para>
/// <list type="number">
///   <item><description>the referenced capability, when its model code is the model that ran (or none was reported);</description></item>
///   <item><description>a capability of the same <c>Capability</c> under the same account whose model code is the model that ran;</description></item>
///   <item><description>any platform capability of the same kind, <c>Capability</c> and model code (the legacy
///   <c>AzureOpenAI:*</c> configuration fallback has no row of its own);</description></item>
///   <item><description>fail-expensive: the dearest effective row for the unit among the account's capabilities of that
///   <c>Capability</c>, so an explicitly requested model never prices below the configured one.</description></item>
/// </list>
/// <para>
/// Inside the chosen capability the variant is the exact match, else (no variant requested and a positive
/// <see cref="PriceRequest.PromptTokens"/>) the largest <c>gt{N}k</c> prompt-length tier the prompt exceeds, else the
/// variant-less row, else the dearest variant. A measured <c>{n}MP</c> size with no row of its own takes the next
/// seeded size up, or the largest size scaled per megapixel (<see cref="MegapixelScale"/>).
/// </para>
/// </remarks>
public sealed class ModelPriceService : IModelPriceService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IModelPriceSource _source;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private ModelPriceSnapshot? _snapshot;
    private DateTimeOffset _loadedAt;

    public ModelPriceService(IModelPriceSource source, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    public void Invalidate() => _snapshot = null;

    public async ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelPriceSnapshot snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return Price(snapshot, request, request.At ?? _time.GetUtcNow().UtcDateTime);
    }

    public async ValueTask<PriceQuote> PriceQuotedUsdAsync(
        decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default)
    {
        ModelPriceSnapshot snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return PriceQuotedUsd(snapshot, costUsd, unit, billingMode, at ?? _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>The oracle arm of the pure rule: the quoted USD at the snapshot's markup and the mode's multiplier.</summary>
    public static PriceQuote PriceQuotedUsd(
        ModelPriceSnapshot snapshot, decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime at)
    {
        decimal multiplier = FindMultiplier(snapshot.Multipliers, billingMode, unit, at);
        decimal credits = PricingDefaults.CreditsMillisPerUnit(costUsd, null, snapshot.Markup, snapshot.CreditUsdValue) * multiplier;
        return new PriceQuote(ToMillis(credits, billingMode), billingMode == UsageBillingMode.Platform ? costUsd : 0m, null);
    }

    /// <summary>The pure pricing rule, separated from loading so it is testable without a source.</summary>
    public static PriceQuote Price(ModelPriceSnapshot snapshot, PriceRequest request, DateTime at)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        ModelCapabilityPrice? row = FindRow(snapshot, request, at);
        if (row is null)
        {
            if (request.BillingMode == UsageBillingMode.Platform)
                throw new UnpricedUsageException(request.Unit, request.Kind, request.ModelCode ?? string.Empty);

            // Byo and Runner work is never charged and is not our cost, so a missing price is not an error.
            return new PriceQuote(0, 0m, null);
        }

        decimal multiplier = FindMultiplier(snapshot.Multipliers, request.BillingMode, request.Unit, at);
        decimal perUnit = PerUnit(snapshot, row);
        decimal quantity = request.Quantity * MegapixelScale(request.Variant, row.Variant);
        decimal credits = quantity * perUnit * multiplier;
        decimal costUsd = request.BillingMode == UsageBillingMode.Platform ? quantity * row.CostUsdPerUnit : 0m;
        return new PriceQuote(ToMillis(credits, request.BillingMode), costUsd, row.Id, row.Variant);
    }

    /// <summary>Credits per unit of <paramref name="row"/> at the snapshot's markup and credit value.</summary>
    public static decimal PerUnit(ModelPriceSnapshot snapshot, ModelCapabilityPrice row) =>
        PricingDefaults.CreditsMillisPerUnit(row.CostUsdPerUnit, row.CreditsMillisPerUnitOverride, snapshot.Markup, snapshot.CreditUsdValue);

    private static long ToMillis(decimal credits, UsageBillingMode mode)
    {
        long millis = (long)Math.Round(credits, MidpointRounding.AwayFromZero);

        // Metering must never read as free: a non-zero platform-billed event costs at least 1 millicredit.
        if (millis == 0 && mode == UsageBillingMode.Platform && credits > 0m)
            millis = 1;
        return millis;
    }

    private static ModelCapabilityPrice? FindRow(ModelPriceSnapshot snapshot, PriceRequest request, DateTime at)
    {
        string? used = string.IsNullOrWhiteSpace(request.ModelCode) ? null : request.ModelCode.Trim();
        PricedCapability? referenced = request.ModelCapabilityId is { } id
            ? snapshot.Capabilities.FirstOrDefault(c => c.Id == id)
            : null;
        // The account the referenced capability belongs to NOW wins over the caller's: a resolver snapshot cached before
        // a consolidation still names the merged-away provider, and that stale id would find no fail-expensive row.
        Guid? account = referenced?.ProviderId ?? request.AccountId;

        // 1. The referenced capability, when it ran its own model (or the caller reported none).
        if (referenced is not null && (used is null || SameCode(referenced.ModelCode, used))
            && SelectVariant(snapshot, referenced.Id, request, at) is { } own)
        {
            return own;
        }

        if (used is not null)
        {
            // 2. A sibling capability under the same account serving the model that ran.
            if (account is { } accountId)
            {
                foreach (PricedCapability sibling in Ordered(snapshot.Capabilities.Where(c =>
                             c.ProviderId == accountId && c.Capability == request.Capability && SameCode(c.ModelCode, used))))
                {
                    if (SelectVariant(snapshot, sibling.Id, request, at) is { } row) return row;
                }
            }

            // 3. Any platform capability of the same kind, capability and model code.
            foreach (PricedCapability platform in Ordered(snapshot.Capabilities.Where(c =>
                         c.OrganizationId is null && c.Kind == request.Kind && c.Capability == request.Capability
                         && SameCode(c.ModelCode, used))))
            {
                if (SelectVariant(snapshot, platform.Id, request, at) is { } row) return row;
            }
        }

        // 4. Fail expensive: the dearest effective row for the unit among the account's capabilities of this kind
        //    of work, so an explicitly requested model never prices below what the account was configured with.
        if (account is { } expensiveAccount)
        {
            HashSet<Guid> capabilityIds = snapshot.Capabilities
                .Where(c => c.ProviderId == expensiveAccount && c.Capability == request.Capability)
                .Select(c => c.Id)
                .ToHashSet();
            return snapshot.Prices
                .Where(p => capabilityIds.Contains(p.ModelCapabilityId) && p.Unit == request.Unit && IsEffective(p, at))
                .OrderByDescending(p => PerUnit(snapshot, p))
                .ThenByDescending(p => p.EffectiveFrom)
                .ThenBy(p => p.Id)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>Deterministic order when several capabilities qualify: oldest id first is arbitrary but stable.</summary>
    private static IEnumerable<PricedCapability> Ordered(IEnumerable<PricedCapability> capabilities) =>
        capabilities.OrderBy(c => c.Id);

    private static bool SameCode(string modelCode, string used) =>
        string.Equals(modelCode?.Trim(), used, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Exact variant, else (a measured <c>{n}MP</c> size) on a flat per-megapixel endpoint its dearest per-MP row (scaled
    /// by n / m), otherwise the next seeded size up or the largest size, else the
    /// prompt-length tier (no variant requested), else the variant-less row, else the dearest variant; the later start
    /// breaks a tie.
    /// </summary>
    private static ModelCapabilityPrice? SelectVariant(ModelPriceSnapshot snapshot, Guid capabilityId, PriceRequest request, DateTime at)
    {
        List<ModelCapabilityPrice> rows = snapshot.Prices
            .Where(p => p.ModelCapabilityId == capabilityId && p.Unit == request.Unit && IsEffective(p, at))
            .ToList();
        if (rows.Count == 0) return null;

        string? variant = string.IsNullOrWhiteSpace(request.Variant) ? null : request.Variant.Trim();
        if (variant is not null
            && Latest(rows.Where(p => string.Equals(p.Variant, variant, StringComparison.OrdinalIgnoreCase))) is { } exact)
        {
            return exact;
        }

        // A measured picture size ({n}MP) with no row of its own bills the next seeded size up, or, above the largest,
        // that row scaled per megapixel by Price (MegapixelScale): never a smaller size, never the variant-less figure.
        if (variant is not null && TryParseMegapixels(variant, out long wanted))
        {
            List<(ModelCapabilityPrice Row, long Mp)> sized = [];
            foreach (ModelCapabilityPrice row in rows)
            {
                if (TryParseMegapixels(row.Variant, out long mp)) sized.Add((row, mp));
            }

            if (sized.Count > 0)
            {
                // A flat per-megapixel endpoint (fill: "$0.05 per megapixel") is n x its rate: the row with the highest
                // rate per megapixel (the smallest such size on a tie), which Price scales by n / m (MegapixelScale).
                PricedCapability? capability = snapshot.Capabilities.FirstOrDefault(c => c.Id == capabilityId);
                if (capability is not null && OutputSizePricing.IsLinearPerMegapixel(capability.Kind, capability.ModelCode))
                {
                    return sized
                        .OrderByDescending(s => PerUnit(snapshot, s.Row) / s.Mp)
                        .ThenBy(s => s.Mp)
                        .ThenByDescending(s => s.Row.EffectiveFrom)
                        .ThenBy(s => s.Row.Id)
                        .First().Row;
                }

                long size = sized.Any(s => s.Mp >= wanted) ? sized.Where(s => s.Mp >= wanted).Min(s => s.Mp) : sized.Max(s => s.Mp);
                return Latest(sized.Where(s => s.Mp == size).Select(s => s.Row));
            }
        }

        // A long prompt bills at the largest gt{N}k tier it exceeds; a requested variant is never overridden.
        if (variant is null && request.PromptTokens is > 0 and { } promptTokens
            && LongestTier(rows, promptTokens) is { } tier)
        {
            return tier;
        }

        if (Latest(rows.Where(p => string.IsNullOrEmpty(p.Variant))) is { } any)
            return any;

        return rows
            .OrderByDescending(p => PerUnit(snapshot, p))
            .ThenByDescending(p => p.EffectiveFrom)
            .ThenBy(p => p.Id)
            .First();
    }

    /// <summary>
    /// A megapixel size variant, <c>{n}MP</c> with n a positive whole number (case-insensitive), as the model catalog
    /// seed labels per-megapixel rows and the image meter labels a measured picture. False for any other variant.
    /// </summary>
    public static bool TryParseMegapixels(string? variant, out long megapixels)
    {
        megapixels = 0;
        if (variant is null) return false;
        ReadOnlySpan<char> v = variant.AsSpan().Trim();
        if (v.Length < 3 || char.ToUpperInvariant(v[^1]) != 'P' || char.ToUpperInvariant(v[^2]) != 'M') return false;
        return long.TryParse(v[..^2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                   out megapixels)
               && megapixels > 0;
    }

    /// <summary>
    /// How many of <paramref name="rowVariant"/>'s units a measured <paramref name="requestedVariant"/> is: n / m when
    /// an n MP picture is priced on a smaller m MP row (above the largest seeded size, or on a flat per-megapixel
    /// endpoint's per-MP row), else 1. The vendors behind these
    /// rows price per megapixel with no cap and a non-negative first-unit premium, so the largest row's per-megapixel
    /// rate times n is at or above what n megapixels cost: it fails high.
    /// </summary>
    public static decimal MegapixelScale(string? requestedVariant, string? rowVariant) =>
        TryParseMegapixels(requestedVariant, out long wanted) && TryParseMegapixels(rowVariant, out long priced) && wanted > priced
            ? (decimal)wanted / priced
            : 1m;

    /// <summary>
    /// The prompt-length tier a variant names: <c>gt{N}k</c> (case-insensitive) is a prompt of more than N thousand
    /// tokens. False for any other variant.
    /// </summary>
    public static bool TryParsePromptTier(string? variant, out long thousands)
    {
        thousands = 0;
        if (variant is null) return false;
        ReadOnlySpan<char> v = variant.AsSpan().Trim();
        if (v.Length < 4 || !v.StartsWith("gt", StringComparison.OrdinalIgnoreCase)
            || char.ToLowerInvariant(v[^1]) != 'k')
        {
            return false;
        }

        ReadOnlySpan<char> digits = v[2..^1];
        foreach (char c in digits)
        {
            if (c is < '0' or > '9') return false;
        }

        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out thousands);
    }

    /// <summary>The row of the largest tier N with N × 1000 &lt; <paramref name="promptTokens"/>; the later start breaks a tie.</summary>
    private static ModelCapabilityPrice? LongestTier(List<ModelCapabilityPrice> rows, long promptTokens)
    {
        ModelCapabilityPrice? best = null;
        long bestN = -1;
        foreach (ModelCapabilityPrice row in rows)
        {
            if (!TryParsePromptTier(row.Variant, out long n) || n > (promptTokens - 1) / 1000) continue;
            if (n > bestN
                || (n == bestN && (row.EffectiveFrom > best!.EffectiveFrom
                                   || (row.EffectiveFrom == best.EffectiveFrom && row.Id.CompareTo(best.Id) < 0))))
            {
                best = row;
                bestN = n;
            }
        }

        return best;
    }

    private static ModelCapabilityPrice? Latest(IEnumerable<ModelCapabilityPrice> rows) =>
        rows.OrderByDescending(p => p.EffectiveFrom).ThenBy(p => p.Id).FirstOrDefault();

    /// <summary>The kind-specific multiplier beats the mode's general one; with neither, Platform is 1 and the rest 0 (D10).</summary>
    private static decimal FindMultiplier(
        IReadOnlyList<RateMultiplier> multipliers, UsageBillingMode mode, UsageKind kind, DateTime at)
    {
        RateMultiplier? specific = null;
        RateMultiplier? general = null;
        foreach (RateMultiplier m in multipliers)
        {
            if (m.BillingMode != mode || !IsEffective(m.EffectiveFrom, m.EffectiveTo, at)) continue;
            if (m.UsageKind == kind)
            {
                if (specific is null || m.EffectiveFrom > specific.EffectiveFrom) specific = m;
            }
            else if (m.UsageKind is null)
            {
                if (general is null || m.EffectiveFrom > general.EffectiveFrom) general = m;
            }
        }

        if (specific is not null) return specific.Multiplier;
        if (general is not null) return general.Multiplier;
        return mode == UsageBillingMode.Platform ? 1m : 0m;
    }

    /// <summary>A price row is in force at <paramref name="at"/>.</summary>
    public static bool IsEffective(ModelCapabilityPrice price, DateTime at) => IsEffective(price.EffectiveFrom, price.EffectiveTo, at);

    private static bool IsEffective(DateTime from, DateTime? to, DateTime at) => from <= at && (to is null || at < to);

    private async ValueTask<ModelPriceSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        ModelPriceSnapshot? current = _snapshot;
        if (current is not null && _time.GetUtcNow() - _loadedAt < CacheTtl) return current;

        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _snapshot;
            if (current is not null && _time.GetUtcNow() - _loadedAt < CacheTtl) return current;

            ModelPriceSnapshot loaded = await _source.LoadAsync(ct).ConfigureAwait(false);
            _loadedAt = _time.GetUtcNow();
            _snapshot = loaded;
            return loaded;
        }
        finally
        {
            _refresh.Release();
        }
    }
}
