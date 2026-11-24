using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Data;

/// <summary><c>ModelCatalog:Consolidation</c>: what the upgrader does with providers that share one account.</summary>
public enum ConsolidationMode
{
    /// <summary>Merge them (the default).</summary>
    Apply,

    /// <summary>Log what would be merged; write nothing.</summary>
    DryRun,

    /// <summary>Skip consolidation entirely.</summary>
    Off
}

/// <summary>
/// One consolidation group: the provider that survives, the providers merged into it and how many models moved.
/// </summary>
/// <param name="MergedModelCount">The models that moved from the merged providers to the survivor.</param>
public sealed record ConsolidationGroup(
    Guid SurvivorId, string SurvivorName, IReadOnlyList<Guid> MergedProviderIds, int MergedModelCount);

/// <summary>What a consolidation run merges (or, as a dry run, would merge now).</summary>
public sealed record ConsolidationReport(IReadOnlyList<ConsolidationGroup> Groups)
{
    public static readonly ConsolidationReport Empty = new([]);
}

/// <summary>What one upgrader run changed.</summary>
public sealed record ModelCatalogUpgradeResult(
    bool ComputeProviderCreated,
    int PricesPorted,
    bool PricesStamped,
    int VideoVariantsAdded,
    ConsolidationReport Consolidated)
{
    /// <summary>True when the run wrote anything.</summary>
    public bool Changed => ComputeProviderCreated || PricesPorted > 0 || PricesStamped || VideoVariantsAdded > 0
                           || Consolidated.Groups.Count > 0;
}

/// <summary>
/// Upgrader telemetry (docs/design/model-catalog.md §4.2). Tags are low-cardinality words only. Registered through
/// <c>AddReelBoltTelemetry</c>.
/// </summary>
public static class ModelCatalogUpgraderDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.ModelCatalog";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Boot-time consolidations that failed and were rolled back (boot continued).</summary>
    public static readonly Counter<long> ConsolidationFailed =
        Meter.CreateCounter<long>("reelbolt.model_catalog.consolidation_failed", "runs", "Boot-time provider consolidations that failed and were rolled back");

    /// <summary>Any other upgrader step that failed and was rolled back. Tag: <c>step</c>.</summary>
    public static readonly Counter<long> StepFailed =
        Meter.CreateCounter<long>("reelbolt.model_catalog.upgrade_step_failed", "runs", "Boot-time model catalog upgrade steps that failed and were rolled back, tagged with step");
}

/// <summary>
/// Brings the model catalog up to date at API boot (docs/design/model-catalog.md §4.2). Runs every boot from
/// <see cref="DatabaseSeeder.SeedAsync"/> right after the API migration (and the key-ring import it depends on), before
/// anything reads providers, and is idempotent:
/// <list type="number">
///   <item><description>ensures the system compute provider, model and <c>Compute</c> capability
///   (<see cref="WellKnownModelCatalog"/> ids);</description></item>
///   <item><description>ONCE (<c>pricing_settings.catalog_upgraded_at</c> null): ports every platform capability's prices
///   from the frozen <c>rate_cards</c> with the old matching rule, or from the price book on a fresh install, then
///   stamps;</description></item>
///   <item><description>adds the per-resolution video variants of MiniMax and fal.ai video capabilities;</description></item>
///   <item><description>consolidates providers that share one account (<c>ModelCatalog:Consolidation</c>).</description></item>
/// </list>
/// It needs C# rather than SQL because keys are Data Protection ciphertexts (random IV), so only their plaintext can be
/// compared, and because the price port reuses the old pattern-matching rule one last time.
/// </summary>
public sealed class ModelCatalogUpgrader(
    InferenceApiDbContext db,
    ISecretProtector secrets,
    IConfiguration configuration,
    ILogger<ModelCatalogUpgrader> logger,
    IInferenceProviderResolver? resolver = null,
    IModelPriceService? prices = null,
    TimeProvider? time = null)
{
    public const string ConsolidationKey = "ModelCatalog:Consolidation";

    /// <summary>
    /// The <c>pg_advisory_xact_lock</c> key a run holds for its whole transaction, so two replicas booting together never
    /// port prices or consolidate at the same time (the second one waits, then finds the stamp). Arbitrary but constant:
    /// the ASCII of "MDLCATLG".
    /// </summary>
    public const long AdvisoryLockKey = 0x4D444C4341544C47;

    /// <summary>The admin-edited test: credits that differ from the cost at the old credit value by more than this are kept as an override.</summary>
    public const decimal OverrideTolerance = 0.000000001m;

    /// <summary>The credit value every rate card was derived at (D9).</summary>
    private const decimal LegacyCreditUsdValue = 0.006m;

    private static readonly string[] ComputeTiers = [ComputeMetering.Tier720, ComputeMetering.Tier1080, ComputeMetering.TierUhd];

    private DateTime Now => (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    /// <summary>
    /// The configured consolidation mode. Unset means <see cref="ConsolidationMode.Apply"/> (the default); a value that is
    /// not one of the three names falls back to <see cref="ConsolidationMode.DryRun"/> with a warning, so a typo meant to
    /// switch merging off never merges accounts.
    /// </summary>
    public ConsolidationMode Mode
    {
        get
        {
            string? configured = configuration[ConsolidationKey]?.Trim();
            if (string.IsNullOrEmpty(configured)) return ConsolidationMode.Apply;
            if (Enum.GetNames<ConsolidationMode>().FirstOrDefault(n => string.Equals(n, configured, StringComparison.OrdinalIgnoreCase))
                is { } name)
                return Enum.Parse<ConsolidationMode>(name);

            logger.LogWarning(
                "{Key} is '{Value}', which is not Apply, DryRun or Off: falling back to DryRun (nothing is merged; the plan is logged)",
                ConsolidationKey, configured);
            return ConsolidationMode.DryRun;
        }
    }

    /// <summary>
    /// Runs every step; invalidates the resolver and price caches when anything changed. On PostgreSQL the whole run is
    /// one transaction holding <see cref="AdvisoryLockKey"/>, so a second replica booting at the same time waits and then
    /// re-reads the stamp (and the catalog) instead of porting prices or consolidating twice. No lock on other providers.
    /// <para>
    /// Never aborts boot: each step runs behind a savepoint, and a step that throws is rolled back to it (nothing
    /// half-written), logged as an error with its exception type and counted
    /// (<see cref="ModelCatalogUpgraderDiagnostics"/>); the run goes on to the next step, and the next boot retries.
    /// Cancellation (shutdown) still propagates.
    /// </para>
    /// </summary>
    public async Task<ModelCatalogUpgradeResult> RunAsync(CancellationToken ct = default)
    {
        ModelCatalogUpgradeResult result;
        try
        {
            if (db.Database.IsNpgsql())
            {
                await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(ct);
                result = await RunStepsAsync(ct);
                await transaction.CommitAsync(ct);
            }
            else
            {
                result = await RunStepsAsync(ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The transaction (or the lock) itself failed: it is rolled back on dispose, so nothing of this run stays.
            db.ChangeTracker.Clear();
            logger.LogError(ex,
                "Model catalog: the upgrade run failed ({ExceptionType}) and was rolled back; boot continues and the next boot retries",
                ex.GetType().Name);
            ModelCatalogUpgraderDiagnostics.StepFailed.Add(1, new KeyValuePair<string, object?>("step", "run"));
            result = new ModelCatalogUpgradeResult(false, 0, false, 0, ConsolidationReport.Empty);
        }

        if (result.Changed)
        {
            resolver?.InvalidateAll();
            prices?.Invalidate();
        }

        try
        {
            await ReportCoverageAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Model catalog: the price coverage report failed ({ExceptionType}); boot continues", ex.GetType().Name);
            ModelCatalogUpgraderDiagnostics.StepFailed.Add(1, new KeyValuePair<string, object?>("step", "coverage"));
        }

        try
        {
            await WarnStaleLipSyncRowsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Model catalog: the stale lip-sync row check failed ({ExceptionType}); boot continues", ex.GetType().Name);
            ModelCatalogUpgraderDiagnostics.StepFailed.Add(1, new KeyValuePair<string, object?>("step", "lipSyncWarning"));
        }

        return result;
    }

    /// <summary>
    /// Read-only, one Warning: a capability row filed under <c>VideoGeneration</c> that names a talking-head catalog model
    /// (<c>FalVideoJob.TalkingHead</c>) is never picked for lip-sync any more (a talking head resolves the <c>LipSync</c>
    /// capability). Logs the count and ids so an operator can add the model under Lip-sync. Writes nothing.
    /// </summary>
    public async Task<int> WarnStaleLipSyncRowsAsync(CancellationToken ct = default)
    {
        var rows = await db.ModelCapabilities
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Capability == InferenceProviderCapability.VideoGeneration)
            .Select(c => new { c.Id, c.Model.ModelCode })
            .ToListAsync(ct);
        List<Guid> stale =
        [
            .. rows.Where(r => FalAiVideoGenerationClient.Catalog.Find(r.ModelCode?.Trim())?.Job == FalVideoJob.TalkingHead).Select(r => r.Id)
        ];
        if (stale.Count == 0) return 0;

        logger.LogWarning(
            "Model catalog: {Count} VideoGeneration capability row(s) name a talking-head model and are no longer used for lip-sync; " +
            "add the model under Lip-sync. Rows: {RowIds}",
            stale.Count, string.Join(", ", stale));
        return stale.Count;
    }

    /// <summary>The four steps, in order, each behind <see cref="GuardedAsync{T}"/>; inside the locked transaction on PostgreSQL.</summary>
    private async Task<ModelCatalogUpgradeResult> RunStepsAsync(CancellationToken ct)
    {
        bool computeCreated = await GuardedAsync("compute_provider", false, () => EnsureComputeProviderAsync(ct), ct);
        (int ported, bool stamped) = await GuardedAsync("price_port", (0, false), () => PortPricesOnceAsync(ct), ct);
        int variants = await GuardedAsync("video_variants", 0, () => AddVideoResolutionVariantsAsync(ct), ct);

        ConsolidationReport consolidated = ConsolidationReport.Empty;
        switch (Mode)
        {
            case ConsolidationMode.Apply:
                consolidated = await GuardedAsync("consolidation", ConsolidationReport.Empty, () => ApplyConsolidationAsync(ct), ct);
                break;
            case ConsolidationMode.DryRun:
                ConsolidationReport plan = await GuardedAsync("consolidation", ConsolidationReport.Empty, () => PlanConsolidationAsync(ct), ct);
                foreach (ConsolidationGroup group in plan.Groups)
                {
                    logger.LogInformation(
                        "Model catalog (dry run): would merge {Count} provider(s) {Merged} into {SurvivorId} ({SurvivorName}), moving {Models} model(s)",
                        group.MergedProviderIds.Count, string.Join(", ", group.MergedProviderIds), group.SurvivorId,
                        group.SurvivorName, group.MergedModelCount);
                }

                break;
        }

        return new ModelCatalogUpgradeResult(computeCreated, ported, stamped, variants, consolidated);
    }

    /// <summary>
    /// Runs one step behind a savepoint of the current transaction (when there is one). When it throws (other than for
    /// cancellation), rolls back to the savepoint, forgets every tracked entity, logs an error naming the step, the
    /// consolidation group it was on and the exception type (never key material: no step puts a key in an exception),
    /// counts it and returns <paramref name="fallback"/>. Without a transaction the step's own atomicity holds (one
    /// <c>SaveChanges</c>, or the consolidation's own transaction).
    /// </summary>
    private async Task<T> GuardedAsync<T>(string step, T fallback, Func<Task<T>> body, CancellationToken ct)
    {
        IDbContextTransaction? transaction = db.Database.CurrentTransaction;
        string savepoint = "model_catalog_" + step;
        if (transaction is not null) await transaction.CreateSavepointAsync(savepoint, ct);
        try
        {
            T value = await body();
            if (transaction is not null) await transaction.ReleaseSavepointAsync(savepoint, ct);
            return value;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (transaction is not null) await transaction.RollbackToSavepointAsync(savepoint, ct);
            db.ChangeTracker.Clear();
            string context = _consolidating ?? "-";
            _consolidating = null;
            logger.LogError(ex,
                "Model catalog: step {Step} failed ({ExceptionType}) at {Context}; rolled back, nothing of it was written. " +
                "Boot continues and the next boot retries",
                step, ex.GetType().Name, context);
            if (step == "consolidation")
                ModelCatalogUpgraderDiagnostics.ConsolidationFailed.Add(1);
            else
                ModelCatalogUpgraderDiagnostics.StepFailed.Add(1, new KeyValuePair<string, object?>("step", step));
            return fallback;
        }
    }

    /// <summary>The consolidation group being applied, for the error log; null outside one.</summary>
    private string? _consolidating;

    /// <summary>Takes <see cref="AdvisoryLockKey"/> for the current transaction on PostgreSQL (re-entrant); a no-op elsewhere.</summary>
    private async Task LockAsync(CancellationToken ct)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
    }

    // ------------------------------------------------------------------ 1. the system compute provider

    /// <summary>Creates whichever of the compute provider, model and capability is missing. True when it wrote any.</summary>
    public async Task<bool> EnsureComputeProviderAsync(CancellationToken ct = default)
    {
        bool changed = false;
        DateTime now = Now;

        if (!await db.InferenceProviders.IgnoreQueryFilters().AnyAsync(p => p.Id == WellKnownModelCatalog.ComputeProviderId, ct))
        {
            db.InferenceProviders.Add(new InferenceProvider
            {
                Id = WellKnownModelCatalog.ComputeProviderId,
                OrganizationId = null,
                Name = WellKnownModelCatalog.ComputeProviderName,
                Kind = ComputeMetering.ProviderKind,
                Endpoint = string.Empty,
                IsEnabled = true,
                IsSystem = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        if (!await db.InferenceModels.IgnoreQueryFilters().AnyAsync(m => m.Id == WellKnownModelCatalog.ComputeModelId, ct))
        {
            db.InferenceModels.Add(new InferenceModel
            {
                Id = WellKnownModelCatalog.ComputeModelId,
                ProviderId = WellKnownModelCatalog.ComputeProviderId,
                OrganizationId = null,
                ModelCode = WellKnownModelCatalog.ComputeModelCode,
                DisplayName = WellKnownModelCatalog.ComputeModelDisplayName,
                IsEnabled = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        if (!await db.ModelCapabilities.IgnoreQueryFilters().AnyAsync(c => c.Id == WellKnownModelCatalog.ComputeCapabilityId, ct))
        {
            db.ModelCapabilities.Add(new ModelCapability
            {
                Id = WellKnownModelCatalog.ComputeCapabilityId,
                ModelId = WellKnownModelCatalog.ComputeModelId,
                OrganizationId = null,
                Capability = InferenceProviderCapability.Compute,
                IsDefault = true,
                IsEnabled = true,
                PriceSource = ModelPriceSource.Table,
                CreatedAt = now,
                UpdatedAt = now
            });
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Model catalog: created the system compute provider ({ProviderId})", WellKnownModelCatalog.ComputeProviderId);
        }

        return changed;
    }

    // ------------------------------------------------------------------ 2. the one-time price port

    /// <summary>
    /// Ports prices once (design §4.2 step 2): for every platform capability and every unit it meters, the effective
    /// rate card the old rule picks for its model code becomes a variant-less row, its <c>code#tier</c> cards become
    /// variant rows, and compute cards become the compute capability's tier variants. The cost is the card's; the card's
    /// credits become an override only when an admin set them away from <c>cost × 1000 ÷ 0.006</c> (seeded cards are
    /// cost-derived, so they pick up the markup). A future-dated card (an announced price change) is ported too, with its
    /// own <c>effective_from</c>, wherever the old rule would have switched to it; one that cannot be ported because the
    /// capability already has its own rows for that unit is logged. With no rate cards at all it ports from the price
    /// book. Then stamps.
    /// </summary>
    public async Task<(int Ported, bool Stamped)> PortPricesOnceAsync(CancellationToken ct = default)
    {
        PricingSettings settings = await db.PricingSettings.FirstOrDefaultAsync(s => s.Id == PricingSettings.SingletonId, ct)
                                   ?? db.PricingSettings.Add(new PricingSettings()).Entity;
        if (settings.CatalogUpgradedAt is not null) return (0, false);

        DateTime now = Now;
        var capabilities = await db.ModelCapabilities
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.OrganizationId == null)
            .Select(c => new { c.Id, c.Capability, c.Model.ModelCode, c.Model.Provider.Kind })
            .ToListAsync(ct);
        List<Guid> ids = [.. capabilities.Select(c => c.Id)];
        // An install's own prices survive: a (capability, unit, variant) that already has a row gets nothing ported.
        HashSet<(Guid, UsageKind, string)> preexisting = [.. (await db.ModelCapabilityPrices.AsNoTracking()
                .Where(p => ids.Contains(p.ModelCapabilityId))
                .Select(p => new { p.ModelCapabilityId, p.Unit, p.Variant })
                .ToListAsync(ct))
            .Select(p => (p.ModelCapabilityId, p.Unit, VariantKey(p.Variant)))];
        List<RateCard> cards = await db.RateCards.AsNoTracking().ToListAsync(ct);

        int ported = 0;
        HashSet<(Guid, UsageKind, string, DateTime)> added = [];
        void Add(Guid capabilityId, ModelCapabilityPrice row)
        {
            (Guid, UsageKind, string) key = (capabilityId, row.Unit, VariantKey(row.Variant));
            if (preexisting.Contains(key))
            {
                if (row.EffectiveFrom > now)
                {
                    logger.LogWarning(
                        "Model catalog: the {Unit} price ({Variant}) of capability {CapabilityId} due to start {From:O} was not ported, " +
                        "because the capability already had its own price rows for that unit; add it by hand if it still applies",
                        row.Unit, row.Variant ?? "no variant", capabilityId, row.EffectiveFrom);
                }

                return;
            }

            if (!added.Add((key.Item1, key.Item2, key.Item3, row.EffectiveFrom))) return;
            db.ModelCapabilityPrices.Add(row);
            ported++;
        }

        foreach (var capability in capabilities)
        {
            if (cards.Count == 0)
            {
                foreach (SuggestedPrice suggestion in PriceBook.SuggestFor(capability.Kind, capability.Capability, capability.ModelCode))
                    Add(capability.Id, PriceBook.ToRow(capability.Id, suggestion, now));
                continue;
            }

            foreach (ModelCapabilityPrice row in PortFromCards(cards, capability.Id, capability.Kind, capability.Capability,
                         capability.ModelCode, now))
            {
                Add(capability.Id, row);
            }
        }

        settings.CatalogUpgradedAt = now;
        settings.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Model catalog: ported {Count} price row(s) to {Capabilities} platform capabilit(y/ies) from {Source}",
            ported, capabilities.Count, cards.Count == 0 ? "the price book (no rate cards)" : $"{cards.Count} rate card(s)");
        return (ported, true);
    }

    private static IEnumerable<ModelCapabilityPrice> PortFromCards(
        IReadOnlyList<RateCard> cards, Guid capabilityId, InferenceProviderKind kind, InferenceProviderCapability capability,
        string modelCode, DateTime now)
    {
        if (capability == InferenceProviderCapability.Compute)
        {
            List<ModelCapabilityPrice> compute = [];
            foreach (UsageKind unit in PricingDefaults.ComputeUnits)
            {
                foreach (string tier in ComputeTiers)
                {
                    List<RateCard> tierCards = [.. cards.Where(c => c.ProviderKind == ComputeMetering.ProviderKind && c.UsageKind == unit
                        && string.Equals(c.ModelPattern, tier, StringComparison.OrdinalIgnoreCase))];
                    compute.AddRange(Calendar(tierCards, at => Latest(tierCards.Where(c => IsEffective(c, at))), capabilityId, tier, now));
                }
            }

            // An install whose compute cards were all retired still gets the defaults rather than an unpriced compute.
            return compute.Count > 0
                ? compute
                : PricingDefaults.ComputePrices().Select(p => PriceBook.ToRow(capabilityId, p, now));
        }

        if (!ProviderCapabilityUsage.ByCapability.TryGetValue(capability, out UsageKind[]? units)) return [];

        string code = modelCode?.Trim() ?? string.Empty;
        List<ModelCapabilityPrice> rows = [];
        foreach (UsageKind unit in units)
        {
            List<RateCard> matching = [.. cards.Where(c => c.ProviderKind == kind && c.UsageKind == unit && Specificity(c.ModelPattern, code) >= 0)];
            rows.AddRange(Calendar(matching, at => FindLegacyCard(matching, kind, unit, code, at), capabilityId, null, now));

            if (code.Length == 0) continue;
            string prefix = code + "#";
            foreach (IGrouping<string, RateCard> tier in cards
                         .Where(c => c.ProviderKind == kind && c.UsageKind == unit
                                     && c.ModelPattern.Length > prefix.Length
                                     && c.ModelPattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(c => c.ModelPattern[prefix.Length..], StringComparer.OrdinalIgnoreCase))
            {
                List<RateCard> tierCards = [.. tier];
                rows.AddRange(Calendar(tierCards, at => Latest(tierCards.Where(c => IsEffective(c, at))), capabilityId, tier.Key, now));
            }
        }

        return rows;
    }

    /// <summary>
    /// The price rows that reproduce what the old rule (<paramref name="pick"/>) charged from <paramref name="now"/> on: the
    /// card it picks now (with its own <c>effective_from</c>), then, at every later instant one of the
    /// <paramref name="candidates"/> starts or ends, the card it would switch to. A future-dated card is ported with its
    /// own <c>effective_from</c>; a card the old rule falls back to when a more specific one ends starts at that end. A
    /// card already ported is not repeated (its row is still the latest effective one when the old rule returns to it).
    /// </summary>
    private static IEnumerable<ModelCapabilityPrice> Calendar(
        IReadOnlyList<RateCard> candidates, Func<DateTime, RateCard?> pick, Guid capabilityId, string? variant, DateTime now)
    {
        HashSet<RateCard> ported = new(ReferenceEqualityComparer.Instance);
        if (pick(now) is { } current)
        {
            ported.Add(current);
            yield return FromCard(capabilityId, current, variant);
        }

        IEnumerable<DateTime> changes = candidates
            .SelectMany(c => c.EffectiveTo is { } to ? new[] { c.EffectiveFrom, to } : [c.EffectiveFrom])
            .Where(at => at > now)
            .Distinct()
            .Order();
        foreach (DateTime at in changes)
        {
            if (pick(at) is not { } next || !ported.Add(next)) continue;
            ModelCapabilityPrice row = FromCard(capabilityId, next, variant);
            row.EffectiveFrom = at;
            yield return row;
        }
    }

    /// <summary>
    /// The old rate-card rule, kept here for the one-time port only: same provider kind and unit, effective now, and
    /// the most specific pattern for the model (exact name, then the longest <c>prefix*</c>, then <c>*</c>); a later
    /// start breaks a tie.
    /// </summary>
    private static RateCard? FindLegacyCard(IReadOnlyList<RateCard> cards, InferenceProviderKind kind, UsageKind unit, string model, DateTime at)
    {
        RateCard? best = null;
        int bestScore = -1;
        foreach (RateCard card in cards)
        {
            if (card.UsageKind != unit || card.ProviderKind != kind || !IsEffective(card, at)) continue;

            int score = Specificity(card.ModelPattern, model);
            if (score < 0) continue;
            if (score > bestScore || (score == bestScore && card.EffectiveFrom > best!.EffectiveFrom))
            {
                best = card;
                bestScore = score;
            }
        }

        return best;
    }

    private static int Specificity(string pattern, string model)
    {
        if (pattern == "*") return 0;
        if (string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (pattern.EndsWith('*') && model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)) return pattern.Length - 1;
        return -1;
    }

    private static ModelCapabilityPrice FromCard(Guid capabilityId, RateCard card, string? variant)
    {
        decimal derived = card.CostUsdPerUnit * 1000m / LegacyCreditUsdValue;
        bool adminEdited = Math.Abs(card.CreditsMillisPerUnit - derived) > OverrideTolerance;
        return new ModelCapabilityPrice
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = capabilityId,
            Unit = card.UsageKind,
            Variant = variant,
            CostUsdPerUnit = card.CostUsdPerUnit,
            CreditsMillisPerUnitOverride = adminEdited ? card.CreditsMillisPerUnit : null,
            EffectiveFrom = card.EffectiveFrom,
            EffectiveTo = card.EffectiveTo,
            Notes = card.Notes,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static RateCard? Latest(IEnumerable<RateCard> cards) =>
        cards.OrderByDescending(c => c.EffectiveFrom).ThenBy(c => c.Id).FirstOrDefault();

    private static bool IsEffective(RateCard card, DateTime at) =>
        card.EffectiveFrom <= at && (card.EffectiveTo is null || at < card.EffectiveTo);

    private static string VariantKey(string? variant) => variant?.Trim().ToUpperInvariant() ?? string.Empty;

    // ------------------------------------------------------------------ 3. video resolution variants

    /// <summary>
    /// Adds one variant row per resolution of each platform MiniMax / fal.ai video capability's static price table,
    /// where that variant has no row yet (design §4.2 step 3). Returns how many it added.
    /// </summary>
    public async Task<int> AddVideoResolutionVariantsAsync(CancellationToken ct = default)
    {
        var capabilities = await db.ModelCapabilities
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.OrganizationId == null
                        && (c.Capability == InferenceProviderCapability.VideoGeneration || c.Capability == InferenceProviderCapability.LipSync)
                        && (c.Model.Provider.Kind == InferenceProviderKind.MiniMax || c.Model.Provider.Kind == InferenceProviderKind.FalAi))
            .Select(c => new { c.Id, c.Model.ModelCode, c.Model.Provider.Kind })
            .ToListAsync(ct);
        if (capabilities.Count == 0) return 0;

        List<Guid> ids = [.. capabilities.Select(c => c.Id)];
        HashSet<(Guid, string)> existing = [.. (await db.ModelCapabilityPrices.AsNoTracking()
                .Where(p => ids.Contains(p.ModelCapabilityId) && p.Unit == UsageKind.VideoGenSeconds && p.Variant != null)
                .Select(p => new { p.ModelCapabilityId, p.Variant })
                .ToListAsync(ct))
            .Select(p => (p.ModelCapabilityId, VariantKey(p.Variant)))];

        DateTime now = Now;
        int added = 0;
        foreach (var capability in capabilities)
        {
            foreach ((string resolution, decimal usd) in PriceBook.VideoResolutionPrices(capability.Kind, capability.ModelCode))
            {
                if (!existing.Add((capability.Id, VariantKey(resolution)))) continue;
                db.ModelCapabilityPrices.Add(PriceBook.ToRow(capability.Id, new SuggestedPrice(
                    UsageKind.VideoGenSeconds, resolution, usd,
                    PriceBook.PublishedNote($"{capability.Kind} video table (Describe(\"{capability.ModelCode}\").PricePerSecondByResolution), {resolution}")),
                    now));
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Model catalog: added {Count} video resolution price variant(s)", added);
        }

        return added;
    }

    // ------------------------------------------------------------------ 4. consolidation

    /// <summary>
    /// What a consolidation would merge now (the dry run, and the admin report): the same <see cref="Plan"/> over the
    /// same rows the apply reads, so it reports exactly the groups the apply would merge.
    /// </summary>
    public async Task<ConsolidationReport> PlanConsolidationAsync(CancellationToken ct = default)
    {
        List<InferenceProvider> providers = await LoadProvidersAsync(tracked: false, ct);
        return new ConsolidationReport([.. Plan(providers).Select(g => g.Report)]);
    }

    /// <summary>
    /// Merges every group of providers that share one account (design §4.2 step 4). Capability rows never change, so
    /// every capability resolves to the same kind, endpoint, model and key afterwards; a member that was disabled keeps
    /// its models disabled under an enabled survivor, so nothing that was off turns on.
    /// <para>
    /// Concurrency-safe and idempotent: it runs in a transaction holding <see cref="AdvisoryLockKey"/> (RunAsync's, or
    /// its own when called alone) and re-reads the providers inside it, so what another replica merged meanwhile is gone
    /// and never planned again, and a second run finds nothing. Each group is written as FK changes only (moves, then
    /// deletes of the emptied rows), so no row is updated or deleted twice. Throws on failure; the transaction (or
    /// RunAsync's savepoint) rolls everything back.
    /// </para>
    /// </summary>
    public async Task<ConsolidationReport> ApplyConsolidationAsync(CancellationToken ct = default)
    {
        await using IDbContextTransaction? transaction =
            db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        await LockAsync(ct);

        // Re-read inside the lock. A tracked query keeps whatever values an entity tracked before it had, so forget
        // those (everything earlier steps wrote is saved) and read the rows as they are now.
        if (!db.ChangeTracker.HasChanges()) db.ChangeTracker.Clear();
        List<InferenceProvider> providers = await LoadProvidersAsync(tracked: true, ct);
        List<PlannedGroup> plan = Plan(providers);

        DateTime now = Now;
        HashSet<Guid> claimed = [];
        HashSet<Guid> deleted = [];
        List<ConsolidationGroup> applied = [];
        foreach (PlannedGroup group in plan)
        {
            // Plan partitions the providers, so this never fires; it is what makes "never twice" hold by construction.
            if (group.Members.Any(m => !claimed.Add(m.Id)))
            {
                logger.LogWarning("Model catalog: skipped consolidating into {SurvivorId}: one of its providers is already in another group",
                    group.Survivor.Id);
                continue;
            }

            _consolidating = $"group {group.Survivor.Id} <- [{string.Join(", ", group.Report.MergedProviderIds)}]";
            (List<InferenceModel> emptiedModels, List<InferenceProvider> emptiedProviders) = Merge(group, now);

            // Moves first, deletes second: the database cascades a deleted model or provider to whatever still names it.
            await db.SaveChangesAsync(ct);
            foreach (InferenceModel model in emptiedModels.Where(m => deleted.Add(m.Id))) db.InferenceModels.Remove(model);
            foreach (InferenceProvider provider in emptiedProviders.Where(p => deleted.Add(p.Id))) db.InferenceProviders.Remove(provider);
            await db.SaveChangesAsync(ct);
            applied.Add(group.Report);
        }

        _consolidating = null;
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new ConsolidationReport(applied);
    }

    /// <summary>
    /// Applies one group in memory, by foreign key only: the merged providers' models move to the survivor, then inside
    /// the survivor a younger model with the same code joins the older one when their capability sets do not collide
    /// and they are equally enabled (the older display name wins). Navigation collections are never edited by hand, so
    /// EF never sees an orphan to delete on its own: the emptied models and providers are returned, for the caller to
    /// delete once, after the moves are saved.
    /// </summary>
    private (List<InferenceModel> EmptiedModels, List<InferenceProvider> EmptiedProviders) Merge(PlannedGroup group, DateTime now)
    {
        InferenceProvider survivor = group.Survivor;
        bool survivorEnabled = group.Members.Any(p => p.IsEnabled);
        List<InferenceModel> models = [.. group.Members.SelectMany(p => p.Models)];

        // Effective state is provider AND model enabled. Freeze it on the models before the provider flag moves.
        foreach (InferenceProvider member in group.Members)
        {
            foreach (InferenceModel model in member.Models)
            {
                bool effective = member.IsEnabled && model.IsEnabled;
                if (survivorEnabled && model.IsEnabled != effective)
                {
                    model.IsEnabled = effective;
                    model.UpdatedAt = now;
                }
            }
        }

        List<InferenceProvider> emptiedProviders = [];
        foreach (InferenceProvider merged in group.Members.Where(p => p != survivor))
        {
            foreach (InferenceModel model in merged.Models)
            {
                model.ProviderId = survivor.Id;
                model.UpdatedAt = now;
            }

            emptiedProviders.Add(merged);
            logger.LogInformation(
                "Model catalog: consolidated provider {MergedId} ({MergedName}) into {SurvivorId} ({SurvivorName}); same kind, endpoint and account",
                merged.Id, merged.Name, survivor.Id, survivor.Name);
        }

        if (survivor.IsEnabled != survivorEnabled)
        {
            survivor.IsEnabled = survivorEnabled;
        }

        // A header-less image-tool row can survive its own fal row: the survivor takes the account's headers.
        if (group.ExtraHeadersJson is not null && string.IsNullOrWhiteSpace(survivor.ExtraHeadersJson))
        {
            survivor.ExtraHeadersJson = group.ExtraHeadersJson;
        }

        survivor.UpdatedAt = now;

        // Same-code models under the survivor (its own and the moved ones alike).
        Dictionary<Guid, HashSet<InferenceProviderCapability>> capabilitiesOf =
            models.ToDictionary(m => m.Id, m => m.Capabilities.Select(c => c.Capability).ToHashSet());
        List<InferenceModel> kept = [];
        List<InferenceModel> emptiedModels = [];
        foreach (InferenceModel model in models.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id))
        {
            InferenceModel? target = kept.FirstOrDefault(k =>
                string.Equals(k.ModelCode, model.ModelCode, StringComparison.Ordinal)
                && k.IsEnabled == model.IsEnabled
                && !capabilitiesOf[k.Id].Overlaps(capabilitiesOf[model.Id]));
            if (target is null)
            {
                kept.Add(model);
                continue;
            }

            foreach (ModelCapability capability in model.Capabilities)
            {
                capability.ModelId = target.Id;
            }

            capabilitiesOf[target.Id].UnionWith(capabilitiesOf[model.Id]);
            target.UpdatedAt = now;
            emptiedModels.Add(model);
            logger.LogInformation(
                "Model catalog: merged model {ModelId} ({ModelName}) into {TargetId} ({TargetName}) under provider {ProviderId}",
                model.Id, model.DisplayName, target.Id, target.DisplayName, survivor.Id);
        }

        return (emptiedModels, emptiedProviders);
    }

    private sealed record PlannedGroup(
        InferenceProvider Survivor, IReadOnlyList<InferenceProvider> Members, ConsolidationGroup Report, string? ExtraHeadersJson);

    private async Task<List<InferenceProvider>> LoadProvidersAsync(bool tracked, CancellationToken ct)
    {
        IQueryable<InferenceProvider> query = db.InferenceProviders
            .IgnoreQueryFilters()
            .Where(p => !p.IsSystem)
            .Include(p => p.Models).ThenInclude(m => m.Capabilities);
        return await (tracked ? query : query.AsNoTracking()).ToListAsync(ct);
    }

    private List<PlannedGroup> Plan(IReadOnlyList<InferenceProvider> providers)
    {
        List<PlannedGroup> plan = [];
        foreach ((List<InferenceProvider> group, string? headers) in AccountGroups(providers))
        {
            List<InferenceProvider> members = [.. group.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)];
            if (members.Count < 2) continue;

            InferenceProvider survivor = members[0];
            List<InferenceProvider> merged = members.Skip(1).ToList();
            plan.Add(new PlannedGroup(survivor, members, new ConsolidationGroup(
                survivor.Id, survivor.Name, [.. merged.Select(p => p.Id)], merged.Sum(p => p.Models.Count)), headers));
        }

        return [.. plan.OrderBy(g => g.Survivor.CreatedAt).ThenBy(g => g.Survivor.Id)];
    }

    /// <summary>
    /// Providers that share one account (<see cref="KeyOf"/>), split by their extra headers, with one exception:
    /// the pre-catalog CreateImageToolRowsAsync copied the fal row's endpoint, timeout and key ciphertext byte for byte
    /// but never its extra headers. A header-less row whose ciphertext is byte-identical to that of the rows of exactly
    /// one headered set is such a copy, so it joins that set. Returns each set with its headers (null when none).
    /// </summary>
    private IEnumerable<(List<InferenceProvider> Members, string? ExtraHeadersJson)> AccountGroups(IReadOnlyList<InferenceProvider> providers)
    {
        foreach (IGrouping<AccountKey, InferenceProvider> account in providers
                     .Select(p => (Provider: p, Key: KeyOf(p)))
                     .Where(x => x.Key is not null)
                     .GroupBy(x => x.Key!, x => x.Provider))
        {
            Dictionary<string, List<InferenceProvider>> byHeaders = account
                .GroupBy(HeadersOf, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            if (byHeaders.TryGetValue(string.Empty, out List<InferenceProvider>? bare))
            {
                foreach (InferenceProvider copy in bare.Where(p => !string.IsNullOrEmpty(p.ApiKeyEncrypted)).ToList())
                {
                    List<List<InferenceProvider>> sources = [.. byHeaders
                        .Where(h => h.Key.Length > 0
                                    && h.Value.Any(p => string.Equals(p.ApiKeyEncrypted, copy.ApiKeyEncrypted, StringComparison.Ordinal)))
                        .Select(h => h.Value)];
                    if (sources.Count != 1) continue;

                    sources[0].Add(copy);
                    bare.Remove(copy);
                }
            }

            foreach ((string headers, List<InferenceProvider> members) in byHeaders)
            {
                yield return (members, headers.Length == 0 ? null : headers);
            }
        }
    }

    private static string HeadersOf(InferenceProvider provider) =>
        string.IsNullOrWhiteSpace(provider.ExtraHeadersJson) ? string.Empty : provider.ExtraHeadersJson;

    /// <summary>
    /// What makes two providers one account (extra headers aside, see <see cref="AccountGroups"/>): organization (the
    /// platform, or one same org; never across orgs), kind, trimmed endpoint, the SHA-256 of the decrypted key, OAuth
    /// mode and timeout. Null, so never grouped (the 1:1 split already resolves every id, so skipping is always safe),
    /// for a provider whose key is not an account identity we can verify: no key (key-less rows of one kind and
    /// endpoint are separate local services as often as not), the <c>env:</c> ambient-credential sentinel, or a key the
    /// key ring cannot decrypt.
    /// </summary>
    private AccountKey? KeyOf(InferenceProvider provider)
    {
        if (string.IsNullOrEmpty(provider.ApiKeyEncrypted) || InferenceProviderAdminService.IsAmbientSentinel(provider.ApiKeyEncrypted))
            return null;

        if (!secrets.TryUnprotect(provider.ApiKeyEncrypted, out string plaintext))
        {
            logger.LogWarning(
                "Model catalog: the key of provider {ProviderId} ({ProviderName}) cannot be decrypted; it is never consolidated",
                provider.Id, provider.Name);
            return null;
        }

        if (string.IsNullOrWhiteSpace(plaintext) || InferenceProviderAdminService.IsAmbientSentinel(plaintext)) return null;

        string keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext)));
        return new AccountKey(provider.OrganizationId, provider.Kind, (provider.Endpoint ?? string.Empty).Trim(), keyHash,
            provider.PersonalOAuthMode, provider.TimeoutSeconds);
    }

    private sealed record AccountKey(
        Guid? OrganizationId, InferenceProviderKind Kind, string Endpoint, string KeyHash, bool PersonalOAuthMode,
        int? TimeoutSeconds);

    // ------------------------------------------------------------------ coverage

    private async Task ReportCoverageAsync(CancellationToken ct)
    {
        IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(db, Now, ct);
        if (gaps.Count == 0) return;

        logger.LogError(
            "Price coverage gap: {Count} enabled platform capabilit(y/ies) have no effective price for a unit they meter, so " +
            "usage through them is recorded at zero cost and zero credits: {Rows}",
            gaps.Count,
            string.Join("; ", gaps.Select(g =>
                $"{g.ProviderName}/{g.ModelDisplayName} ({g.Kind} {g.Capability}): {string.Join(", ", g.MissingUnits)}")));
    }
}
