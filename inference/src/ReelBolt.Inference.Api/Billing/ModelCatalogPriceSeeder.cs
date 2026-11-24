using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>What one <see cref="ModelCatalogPriceSeeder.RunAsync"/> did (or, on a dry run, would do).</summary>
/// <param name="Inserted">Price rows opened.</param>
/// <param name="Closed">Seed-owned price rows superseded (end-dated, or removed when not yet in effect).</param>
/// <param name="Unchanged">Seed rows already present with the same cost.</param>
/// <param name="SkippedAdminOwned"><c>{kind}/{model}/{capability}/{unit}/{variant}</c> series an admin owns.</param>
/// <param name="Unpriced"><c>{kind}/{model}/{capability}</c> the seed lists as Unpriced.</param>
/// <param name="NotInSeed"><c>{kind}/{model}/{capability}</c> platform capabilities the seed does not list.</param>
/// <param name="OrphanedSeedRows"><c>{kind}/{model}/{capability}/{unit}/{variant}</c> seed-owned series the seed dropped.</param>
/// <param name="Oracle"><c>{kind}/{model}/{capability}</c> priced from the vendor's quote: no Table rows, seed-owned ones ended.</param>
/// <param name="AdminRetired"><c>{kind}/{model}/{capability}/{unit}/{variant}</c> series an admin ended (never re-opened).</param>
/// <param name="PlaceholdersInForce"><c>{kind}/{model}/{capability}/{unit}/{variant}</c> open PLACEHOLDER rows the seed does not list (kept).</param>
/// <param name="UnverifiedPlaceholders"><c>{kind}/{model}/{capability}/{unit}/{variant}</c> PLACEHOLDER rows whose cost is not the system's figure; left alone.</param>
/// <param name="Actions">Every series decision of the run, in run order (see <see cref="SeriesAction"/>).</param>
public sealed record SeedRunReport(int Inserted, int Closed, int Unchanged, IReadOnlyList<string> SkippedAdminOwned,
    IReadOnlyList<string> Unpriced, IReadOnlyList<string> NotInSeed, IReadOnlyList<string> OrphanedSeedRows,
    IReadOnlyList<string> Oracle, IReadOnlyList<string> AdminRetired, IReadOnlyList<string> PlaceholdersInForce,
    IReadOnlyList<string> UnverifiedPlaceholders, IReadOnlyList<SeriesAction> Actions);

/// <summary>One series decision: <c>insert</c>, <c>close</c> or <c>skip</c>, with its reason and the costs involved.</summary>
/// <param name="AdoptToken">On a <c>placeholder-cost-unverified</c> skip: the token that adopts this exact row (<c>ModelCatalog:SeedPricesAdopt</c>).</param>
public sealed record SeriesAction(string Kind, string ModelCode, string Capability, string Unit, string Variant,
    string Action, string Reason, decimal? OldCost, decimal? NewCost, string? AdoptToken);

/// <summary>
/// Loads <see cref="ModelCatalogSeed"/> into <c>model_capability_prices</c> for every <b>platform</b> capability the
/// seed prices (<c>Table</c>), every boot, idempotently (docs/design/model-catalog.md §2, §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> A row with a credits override is admin-owned, and so is a row whose <c>Notes</c> is null or is
/// neither marker below; an admin's row is never closed or modified and its series is reported in
/// <see cref="SeedRunReport.SkippedAdminOwned"/>. A row whose notes start (ordinal) with <see cref="NotesPrefix"/> is
/// seed-owned: the rows this seeder wrote. A row whose notes start with <see cref="PlaceholderPrefix"/> (ported from
/// PLACEHOLDER rate cards, published vendor list prices and compute defaults) is system-owned only when its cost equals
/// <c>PriceBook.SuggestFor</c> for its unit and variant, or when an exact <c>ModelCatalog:SeedPricesAdopt</c> token
/// names that row (<see cref="SeriesAction.AdoptToken"/>). Any other PLACEHOLDER row is unverified: it is reported in
/// <see cref="SeedRunReport.UnverifiedPlaceholders"/> with its adopt token and is never touched.
/// </para>
/// <para>
/// <b>History is append-only.</b> A changed price, or a PLACEHOLDER row even at the seed's cost, end-dates the
/// seed-owned row at the moment the new one starts (<c>max(seed effectiveFrom, now)</c>, or the exact date of a
/// future-dated seed row) and opens a new row; a cost is never rewritten and nothing is back-dated over past usage.
/// BYO capabilities are never priced.
/// </para>
/// <para>
/// <b>Variants</b> match case-insensitively after trimming (blank = no variant), as the catalog upgrader and
/// <c>ModelPriceService.SelectVariant</c> do, so a ported <c>1080P</c> placeholder is the seed's <c>1080p</c> series.
/// An open PLACEHOLDER row whose (unit, variant) the seed does not list stays in force and is reported
/// (<see cref="SeedRunReport.PlaceholdersInForce"/>): closing it could make the lookup fall back to a cheaper row.
/// </para>
/// <para>
/// <b>Unpriced and Oracle.</b> A capability the seed lists as <c>Unpriced</c>, or one priced from the vendor's quote
/// (seed <c>Oracle</c>, or <c>PriceSource = Oracle</c> on the row), gets no Table rows, and its seed-owned rows in force
/// end now (a not-yet-started one is removed), so the catalog's fully-priced rule hides an Unpriced one. Their notes
/// gain <see cref="EndedMarker"/>, the only note change the seeder makes, so a later seed that prices the capability
/// again recognises the end as its own.
/// </para>
/// <para>
/// <b>Admin retire.</b> A series whose latest row is seed-owned and ended with no successor, at an instant that is
/// neither an end date in the seed nor marked <see cref="EndedMarker"/>, was retired by an admin
/// (<c>RetirePriceAsync</c>): it is never re-opened, only reported (<see cref="SeedRunReport.AdminRetired"/>). A series
/// whose latest row is admin-owned is skipped whole.
/// </para>
/// <para>
/// <b>Concurrency.</b> On PostgreSQL the run is one transaction holding <c>pg_advisory_xact_lock(</c><see
/// cref="AdvisoryLockKey"/><c>)</c>, reading only after the lock is held, so two replicas booting together leave one
/// open row per series. After a run that wrote, the price and resolver caches are invalidated.
/// </para>
/// </remarks>
public static class ModelCatalogPriceSeeder
{
    /// <summary>Provenance marker on every row this seeder writes.</summary>
    public const string NotesPrefix = "seed:";

    /// <summary>Appended to the notes of a seed-owned row the seeder ends because the capability is Unpriced or Oracle.</summary>
    public const string EndedMarker = " | ended by seed:";

    /// <summary>
    /// The skip reason of a self-hosted-only entry (T-037) whose provider endpoint is not local: nothing is priced, and a
    /// zero row it already holds is ended. Reported per seed row (<see cref="SeriesAction"/>).
    /// </summary>
    public const string SelfHostedOnlyNotLocal = "self-hosted-only-endpoint-not-local";

    /// <summary>
    /// Leading marker word on rows the catalog upgrader ports from PLACEHOLDER rate cards (and on published-price and
    /// compute-default notes); the seed supersedes them. <see cref="PricingDefaults.PlaceholderMarker"/>.
    /// </summary>
    public const string PlaceholderPrefix = PricingDefaults.PlaceholderMarker;

    /// <summary>Configuration key: <c>Apply</c> (default) | <c>DryRun</c> | <c>Off</c>.</summary>
    public const string ModeKey = "ModelCatalog:SeedPrices";

    /// <summary>Configuration key: <c>;</c>-separated <see cref="SeriesAction.AdoptToken"/>s the operator adopts (default none).</summary>
    public const string AdoptKey = "ModelCatalog:SeedPricesAdopt";

    /// <summary>Configuration key: how many series action lines a run logs (default 200, 0 = unlimited).</summary>
    public const string LogLimitKey = "ModelCatalog:SeedPricesLogLimit";

    /// <summary>
    /// The <c>pg_advisory_xact_lock</c> key a run holds for its whole transaction (ASCII "MDLSEEDP"), distinct from
    /// <see cref="Data.ModelCatalogUpgrader.AdvisoryLockKey"/> ("MDLCATLG"), so two replicas booting together price once.
    /// </summary>
    public const long AdvisoryLockKey = 0x4D444C5345454450;

    /// <summary>
    /// The boot entry point: reads <see cref="ModeKey"/>, loads and validates the shipped seed, then runs. An invalid
    /// seed or an unknown mode writes nothing.
    /// </summary>
    public static async Task SeedAsync(InferenceApiDbContext db, IConfiguration configuration, ILogger logger,
        IModelPriceService? prices = null, IInferenceProviderResolver? resolver = null, CancellationToken ct = default)
    {
        string mode = configuration[ModeKey] is { Length: > 0 } configured ? configured.Trim() : "Apply";
        bool dryRun;
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Model catalog price seed: {Key} is Off, skipped.", ModeKey);
            return;
        }
        else if (string.Equals(mode, "Apply", StringComparison.OrdinalIgnoreCase))
        {
            dryRun = false;
        }
        else if (string.Equals(mode, "DryRun", StringComparison.OrdinalIgnoreCase))
        {
            dryRun = true;
        }
        else
        {
            logger.LogWarning("Model catalog price seed: {Key} '{Mode}' is not Apply, DryRun or Off; running as DryRun.", ModeKey, mode);
            dryRun = true;
        }

        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        IReadOnlyList<string> problems = seed.Validate();
        if (problems.Count > 0)
        {
            foreach (string problem in problems)
            {
                logger.LogError("Model catalog price seed: invalid seed: {Problem}", problem);
            }

            logger.LogError("Model catalog price seed {CatalogVersion} is invalid ({Count} problems); nothing written.",
                seed.CatalogVersion, problems.Count);
            return;
        }

        IReadOnlySet<string> adopted = ParseTokens(configuration[AdoptKey]);
        int logLimit = int.TryParse(configuration[LogLimitKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit) && limit >= 0
            ? limit
            : DefaultLogLimit;

        await RunAsync(db, seed, DateTime.UtcNow, dryRun, logger, prices, resolver, ct, adopted, logLimit);
    }

    private const int DefaultLogLimit = 200;

    /// <summary>Splits a <c>;</c>-separated token list: trimmed, blanks dropped, compared ordinally.</summary>
    private static IReadOnlySet<string> ParseTokens(string? configured) =>
        new HashSet<string>((configured ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

    /// <summary>
    /// Prices every platform capability from <paramref name="seed"/>; one <c>SaveChangesAsync</c>, none on a dry run. On
    /// PostgreSQL inside one transaction under <see cref="AdvisoryLockKey"/>; invalidates the caches after a write.
    /// </summary>
    public static async Task<SeedRunReport> RunAsync(InferenceApiDbContext db, ModelCatalogSeed seed, DateTime nowUtc,
        bool dryRun, ILogger logger, IModelPriceService? prices = null, IInferenceProviderResolver? resolver = null,
        CancellationToken ct = default, IReadOnlySet<string>? adopted = null, int logLimit = 200)
    {
        DateTime now = nowUtc.Kind switch
        {
            DateTimeKind.Utc => nowUtc,
            DateTimeKind.Local => nowUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
        };

        IReadOnlySet<string> tokens = adopted ?? NoTokens;
        SeedRunReport report;
        if (db.Database.IsNpgsql())
        {
            await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", ct);
            report = await RunLockedAsync(db, seed, now, dryRun, tokens, ct);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct);
            }
        }
        else
        {
            report = await RunLockedAsync(db, seed, now, dryRun, tokens, ct);
        }

        if (!dryRun && report.Inserted + report.Closed > 0)
        {
            resolver?.InvalidateAll();
            prices?.Invalidate();
        }

        Log(logger, seed, dryRun, report, logLimit);
        return report;
    }

    private static readonly IReadOnlySet<string> NoTokens = new HashSet<string>(StringComparer.Ordinal);

    private static async Task<SeedRunReport> RunLockedAsync(InferenceApiDbContext db, ModelCatalogSeed seed, DateTime now,
        bool dryRun, IReadOnlySet<string> adopted, CancellationToken ct)
    {
        // Platform only: the null organization on all three filtered levels. The caller holds the tenant system scope.
        List<ModelCapability> capabilities = await db.ModelCapabilities
            .AsNoTracking()
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .Where(c => c.OrganizationId == null && c.Model.OrganizationId == null && c.Model.Provider.OrganizationId == null)
            .ToListAsync(ct);
        capabilities = [.. capabilities
            .OrderBy(c => c.Model.Provider.Kind)
            .ThenBy(c => c.Model.ModelCode, StringComparer.Ordinal)
            .ThenBy(c => c.Capability)
            .ThenBy(c => c.Id)];

        List<Guid> ids = [.. capabilities.Select(c => c.Id)];
        List<ModelCapabilityPrice> existing = await db.ModelCapabilityPrices
            .Where(p => ids.Contains(p.ModelCapabilityId))
            .ToListAsync(ct);
        ILookup<Guid, ModelCapabilityPrice> pricesByCapability = existing.ToLookup(p => p.ModelCapabilityId);

        Run run = new(seed, now, adopted);
        foreach (ModelCapability capability in capabilities)
        {
            run.Price(capability, [.. pricesByCapability[capability.Id]]);
        }

        if (!dryRun)
        {
            run.Apply(db);
            await db.SaveChangesAsync(ct);
        }

        return run.Report();
    }

    /// <summary>Who owns one price row. <c>Seed</c> and <c>SystemPlaceholder</c> are the seeder's to supersede; the rest are never touched.</summary>
    private enum Owner { Seed, SystemPlaceholder, UnverifiedPlaceholder, Admin }

    /// <summary>
    /// A seed row (<see cref="NotesPrefix"/>, no credits override) is the seeder's on its prefix alone. A PLACEHOLDER row is
    /// the system's only when its cost is the figure the price book suggests for its series, or the operator adopted that exact
    /// row by token; any other PLACEHOLDER row was edited or is not the system's figure, so it is left alone and reported.
    /// </summary>
    private static Owner Classify(ModelCapabilityPrice price, IReadOnlyList<SuggestedPrice> book, IReadOnlySet<string> adopted, string label)
    {
        if (price.CreditsMillisPerUnitOverride is not null || price.Notes is null)
        {
            return Owner.Admin;
        }

        string notes = price.Notes;
        if (notes.StartsWith(NotesPrefix, StringComparison.Ordinal))
        {
            return Owner.Seed;
        }

        if (!notes.StartsWith(PlaceholderPrefix, StringComparison.Ordinal))
        {
            return Owner.Admin;
        }

        decimal cost = Canon(price.CostUsdPerUnit);
        if (book.Any(s => s.Unit == price.Unit && VariantKey(s.Variant) == VariantKey(price.Variant) && Canon(s.CostUsdPerUnit) == cost))
        {
            return Owner.SystemPlaceholder;
        }

        return adopted.Contains(AdoptToken(label, price)) ? Owner.SystemPlaceholder : Owner.UnverifiedPlaceholder;
    }

    /// <summary>The cost as the column stores it (<c>numeric(28,12)</c>, Postgres rounding), so that <c>==</c> ignores scale.</summary>
    private static decimal Canon(decimal cost) => decimal.Round(cost, 12, MidpointRounding.AwayFromZero);

    /// <summary>The cost as a log line and an adopt token show it: canonical, invariant, <c>-</c> when there is none.</summary>
    private static string FormatCost(decimal? cost) =>
        cost is { } value ? Canon(value).ToString("0.############", CultureInfo.InvariantCulture) : "-";

    /// <summary>
    /// The token that adopts exactly one PLACEHOLDER row (<c>ModelCatalog:SeedPricesAdopt</c>): its series, its cost, and its
    /// capability and effective-from instant, so it cannot match a later row of the same cost or another provider's capability.
    /// </summary>
    private static string AdoptToken(string label, ModelCapabilityPrice price)
    {
        string variant = VariantKey(price.Variant) ?? "-";
        return $"{label}/{price.Unit}/{variant}@{FormatCost(price.CostUsdPerUnit)}#{price.ModelCapabilityId:N}/{UtcStamp(price.EffectiveFrom)}";
    }

    /// <summary>
    /// An instant as <c>yyyy-MM-ddTHH:mm:ss.ffffffZ</c>, invariant. <c>Local</c> is converted to UTC; <c>Utc</c> and
    /// <c>Unspecified</c> are taken as UTC (as <see cref="ToUtc"/> writes them), so the token never depends on the host's zone.
    /// </summary>
    private static string UtcStamp(DateTime at)
    {
        DateTime utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static DateTime ToUtc(DateOnly day) => day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static string VariantLabel(string? variant) => variant ?? "-";

    /// <summary>The upgrader's and the price lookup's variant identity: trimmed, case-insensitive, blank = none.</summary>
    private static string? VariantKey(string? variant) =>
        string.IsNullOrWhiteSpace(variant) ? null : variant.Trim().ToUpperInvariant();

    private static bool InForce(ModelCapabilityPrice price, DateTime at) => price.EffectiveTo is not { } end || end > at;

    private const string ActionLine =
        "Model catalog price seed{Mode}: {Action} {Series} unit={Unit} variant={Variant} reason={Reason} old={Old} new={New}{Adopt}";

    private static void Log(ILogger logger, ModelCatalogSeed seed, bool dryRun, SeedRunReport report, int logLimit)
    {
        foreach (string item in report.Unpriced)
        {
            logger.LogWarning("Model catalog price seed: {Capability} is Unpriced in the seed; no price written.", item);
        }

        foreach (string item in report.NotInSeed)
        {
            logger.LogWarning("Model catalog price seed: {Capability} is not in the seed; no price written.", item);
        }

        foreach (string item in report.Oracle)
        {
            logger.LogInformation("Model catalog price seed: {Capability} is priced from the vendor's quote; no Table price written.", item);
        }

        // Skips first, then closes, then inserts, each in run order. Unchanged rows are Debug and do not count toward the cap.
        string mode = dryRun ? " (dry run, nothing written)" : "";
        List<SeriesAction> lines = [.. report.Actions.Where(a => a.Action == "skip" && a.Reason != "unchanged")];
        lines.AddRange(report.Actions.Where(a => a.Action == "close"));
        lines.AddRange(report.Actions.Where(a => a.Action == "insert"));
        int limit = logLimit == 0 ? int.MaxValue : logLimit;

        foreach (SeriesAction action in lines.Take(limit))
        {
            if (action.Action == "skip")
            {
                logger.LogWarning(ActionLine, ActionArgs(action, mode));
            }
            else
            {
                logger.LogInformation(ActionLine, ActionArgs(action, mode));
            }
        }

        if (lines.Count > limit)
        {
            List<SeriesAction> hidden = [.. lines.Skip(limit)];
            logger.LogWarning(
                "… {Hidden} more series action(s) not logged (insert {Inserts}, close {Closes}, skip {Skips}); raise ModelCatalog:SeedPricesLogLimit or read SeedRunReport.Actions.",
                hidden.Count, hidden.Count(a => a.Action == "insert"), hidden.Count(a => a.Action == "close"),
                hidden.Count(a => a.Action == "skip"));
        }

        foreach (SeriesAction action in report.Actions.Where(a => a.Reason == "unchanged"))
        {
            logger.LogDebug(ActionLine, ActionArgs(action, mode));
        }

        logger.LogInformation(
            "Model catalog price seed {CatalogVersion}{DryRun}: {Inserted} inserted, {Closed} closed, {Unchanged} unchanged, " +
            "{Skipped} admin-owned skipped, {Unverified} unverified, {Unpriced} unpriced, {NotInSeed} not in seed, {Orphaned} orphaned, " +
            "{Oracle} oracle, {AdminRetired} admin-retired, {Placeholders} placeholders in force.",
            seed.CatalogVersion, mode, report.Inserted, report.Closed, report.Unchanged,
            report.SkippedAdminOwned.Count, report.UnverifiedPlaceholders.Count, report.Unpriced.Count, report.NotInSeed.Count,
            report.OrphanedSeedRows.Count, report.Oracle.Count, report.AdminRetired.Count, report.PlaceholdersInForce.Count);
    }

    private static object?[] ActionArgs(SeriesAction action, string mode) =>
    [
        mode,
        action.Action,
        $"{action.Kind}/{action.ModelCode}/{action.Capability}",
        action.Unit,
        action.Variant,
        action.Reason,
        FormatCost(action.OldCost),
        FormatCost(action.NewCost),
        action.AdoptToken is null ? string.Empty : $" adopt={action.AdoptToken}",
    ];

    /// <summary>One price window of a (capability, unit, variant) series as the run sees it; nothing touches the DB until Apply.</summary>
    private sealed class Slot(ModelCapabilityPrice row, bool isNew)
    {
        public ModelCapabilityPrice Row { get; } = row;
        public bool IsNew { get; } = isNew;
        public DateTime From => Row.EffectiveFrom;
        public DateTime? To { get; set; } = row.EffectiveTo;
        public bool Removed { get; set; }
        public string? EndNote { get; init; }
        public bool Covers(DateTime at) => !Removed && From <= at && (To is not { } end || end > at);
    }

    private sealed class Run(ModelCatalogSeed seed, DateTime now, IReadOnlySet<string> adopted)
    {
        private readonly List<Slot> _closed = [];
        private readonly List<Slot> _inserted = [];
        private readonly List<Slot> _removed = [];
        private readonly List<string> _skipped = [];
        private readonly List<string> _unpriced = [];
        private readonly List<string> _notInSeed = [];
        private readonly List<string> _orphaned = [];
        private readonly List<string> _oracle = [];
        private readonly List<string> _adminRetired = [];
        private readonly List<string> _placeholders = [];
        private readonly List<string> _unverified = [];
        private readonly List<SeriesAction> _actions = [];
        private readonly HashSet<string> _skipKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Owner> _owner = [];
        private string _kind = string.Empty;
        private string _model = string.Empty;
        private string _capability = string.Empty;
        private string _label = string.Empty;
        private int _unchanged;

        public void Price(ModelCapability capability, List<ModelCapabilityPrice> existing)
        {
            InferenceProviderKind kind = capability.Model.Provider.Kind;
            string modelCode = capability.Model.ModelCode;
            string capabilityName = capability.Capability.ToString();
            string label = $"{kind}/{modelCode}/{capabilityName}";
            _kind = kind.ToString();
            _model = modelCode;
            _capability = capabilityName;
            _label = label;

            // Every existing row is classified once, against the price book's suggestion for this capability.
            IReadOnlyList<SuggestedPrice> book = PriceBook.SuggestFor(kind, capability.Capability, modelCode);
            foreach (ModelCapabilityPrice price in existing)
            {
                _owner[price.Id] = Classify(price, book, adopted, label);
            }

            SeedCapabilityEntry? entry = seed.Find(kind, capabilityName, modelCode);
            if (capability.PriceSource == ModelPriceSource.Oracle
                || (entry is not null && string.Equals(entry.PriceSource, "Oracle", StringComparison.Ordinal)))
            {
                // Priced from the vendor's quote, never from rows: no Table row, and none of ours stays in force.
                _oracle.Add(label);
                EndSeedOwned(label, existing, "Oracle");
                return;
            }

            if (entry is null)
            {
                _notInSeed.Add(label);
                return;
            }

            if (string.Equals(entry.PriceSource, "Unpriced", StringComparison.Ordinal))
            {
                _unpriced.Add(label);
                EndSeedOwned(label, existing, "Unpriced");
                return;
            }

            // T-037: a self-hosted default is zero only while its endpoint is local. On a hosted one the seed prices nothing
            // (the capability stays Unpriced and hidden) and the zero rows it held are ended; admin-owned rows are never touched.
            if (entry.SelfHostedOnly && !SelfHostedEndpoint.IsLocal(capability.Model.Provider.Endpoint))
            {
                _unpriced.Add(label);
                foreach (SeedPriceRow row in seed.Rows.Where(r => r.Kind == kind
                    && string.Equals(r.ModelCode, modelCode, StringComparison.Ordinal)
                    && string.Equals(r.Capability, capabilityName, StringComparison.Ordinal)))
                {
                    Add("skip", SelfHostedOnlyNotLocal, row.Unit, row.Variant, null, 0m);
                }

                EndSeedOwned(label, existing, SelfHostedOnlyNotLocal);
                return;
            }

            if (!string.Equals(entry.PriceSource, "Table", StringComparison.Ordinal))
            {
                return; // Validate() admits only Table, Oracle and Unpriced
            }

            List<SeedPriceRow> rows = [.. seed.Rows.Where(r => r.Kind == kind
                && string.Equals(r.ModelCode, modelCode, StringComparison.Ordinal)
                && string.Equals(r.Capability, capabilityName, StringComparison.Ordinal))];

            foreach (IGrouping<(UsageKind Unit, string? Variant), SeedPriceRow> series in rows
                         .GroupBy(r => (r.Unit, Variant: VariantKey(r.Variant)))
                         .OrderBy(g => g.Key.Unit)
                         .ThenBy(g => g.Key.Variant is not null)
                         .ThenBy(g => g.Key.Variant, StringComparer.Ordinal))
            {
                List<SeedPriceRow> seedRows = [.. series.OrderBy(r => r.EffectiveFrom)];
                string seriesLabel = $"{label}/{series.Key.Unit}/{VariantLabel(seedRows[0].Variant)}";
                List<ModelCapabilityPrice> history = [.. existing
                    .Where(p => p.Unit == series.Key.Unit && VariantKey(p.Variant) == series.Key.Variant)];

                ModelCapabilityPrice? latest = history.OrderByDescending(p => p.EffectiveFrom).ThenBy(p => p.Id).FirstOrDefault();
                if (latest is not null && OwnerOf(latest) is not (Owner.Seed or Owner.SystemPlaceholder))
                {
                    // The admin's series, open or ended: never re-opened over them. An unverified PLACEHOLDER is reported as such.
                    SkipNotOwned(OwnerOf(latest), seriesLabel, latest, seedRows[0].CostUsdPerUnit);
                    continue;
                }

                if (latest is { EffectiveTo: { } ended } && !EndedBySeed(latest, ended, seedRows))
                {
                    Skip(_adminRetired, "admin-retired", seriesLabel, latest, seedRows[0].CostUsdPerUnit);
                    continue;
                }

                PriceSeries(capability.Id, seriesLabel, seedRows, [.. history.Select(p => new Slot(p, isNew: false))]);
            }

            HashSet<(UsageKind, string?)> listed = [.. rows.Select(r => (r.Unit, VariantKey(r.Variant)))];
            foreach (ModelCapabilityPrice price in existing
                         .Where(p => InForce(p, now) && !listed.Contains((p.Unit, VariantKey(p.Variant))))
                         .OrderBy(p => p.Unit).ThenBy(p => p.Variant is not null).ThenBy(p => p.Variant, StringComparer.Ordinal))
            {
                string series = $"{label}/{price.Unit}/{VariantLabel(price.Variant)}";
                switch (OwnerOf(price))
                {
                    case Owner.Seed:
                        Skip(_orphaned, "seed-dropped-series", series, price, null);
                        break;
                    case Owner.SystemPlaceholder:
                        Skip(_placeholders, "placeholder-not-in-seed", series, price, null);
                        break;
                    case Owner.UnverifiedPlaceholder:
                        SkipNotOwned(Owner.UnverifiedPlaceholder, series, price, null);
                        break;
                }
            }
        }

        /// <summary>
        /// The end of a seed-owned series is the seed's own when it is an end date the seed lists for the series, or the
        /// row carries <see cref="EndedMarker"/>. Any other end with no successor is an admin's retire.
        /// </summary>
        private static bool EndedBySeed(ModelCapabilityPrice latest, DateTime ended, List<SeedPriceRow> seedRows) =>
            seedRows.Any(r => r.EffectiveTo is { } to && ToUtc(to) == ended)
            || (latest.Notes?.Contains(EndedMarker, StringComparison.Ordinal) ?? false);

        /// <summary>Ends now every seed-owned row of the capability still in force; reports admin-owned rows in force.</summary>
        private void EndSeedOwned(string label, List<ModelCapabilityPrice> existing, string reason)
        {
            foreach (ModelCapabilityPrice price in existing
                         .Where(p => InForce(p, now))
                         .OrderBy(p => p.Unit).ThenBy(p => p.Variant is not null).ThenBy(p => p.Variant, StringComparer.Ordinal)
                         .ThenBy(p => p.EffectiveFrom))
            {
                Owner owner = OwnerOf(price);
                if (owner is not (Owner.Seed or Owner.SystemPlaceholder))
                {
                    SkipNotOwned(owner, $"{label}/{price.Unit}/{VariantLabel(price.Variant)}", price, null);
                    continue;
                }

                Slot slot = new(price, isNew: false) { EndNote = $"{EndedMarker}{seed.CatalogVersion} ({reason})" };
                if (price.EffectiveFrom >= now)
                {
                    slot.Removed = true; // never in effect: an end before its start is not a window
                    _removed.Add(slot);
                }
                else
                {
                    slot.To = now;
                    _closed.Add(slot);
                }

                RecordClose(slot, reason.ToLowerInvariant(), null);
            }
        }


        private Owner OwnerOf(ModelCapabilityPrice price) => _owner[price.Id];

        /// <summary>A slot the run created has no row yet, so it is the seed's by construction.</summary>
        private Owner OwnerOf(Slot slot) => slot.IsNew ? Owner.Seed : _owner[slot.Row.Id];

        private void Add(string action, string reason, UsageKind unit, string? variant, decimal? oldCost, decimal? newCost, string? adopt = null) =>
            _actions.Add(new SeriesAction(_kind, _model, _capability, unit.ToString(), VariantLabel(variant), action, reason, oldCost, newCost, adopt));

        /// <summary>Lists a skipped series once per reason, and records the skip.</summary>
        private void Skip(List<string> list, string reason, string series, ModelCapabilityPrice price, decimal? newCost)
        {
            if (_skipKeys.Add(SkipKey(reason, series, price)))
            {
                list.Add(series);
                Add("skip", reason, price.Unit, price.Variant, price.CostUsdPerUnit, newCost);
            }
        }

        /// <summary>One skip per reason, series and capability: two providers of the same kind, model and capability are two skips.</summary>
        private static string SkipKey(string reason, string series, ModelCapabilityPrice price) =>
            $"{reason}|{price.ModelCapabilityId:N}|{series}";

        /// <summary>
        /// Skips a row the seeder does not own. An unverified PLACEHOLDER is reported with the token that would adopt it; an
        /// admin's row is reported as admin-owned.
        /// </summary>
        private void SkipNotOwned(Owner owner, string series, ModelCapabilityPrice price, decimal? newCost)
        {
            if (owner == Owner.UnverifiedPlaceholder)
            {
                if (_skipKeys.Add(SkipKey("placeholder-cost-unverified", series, price)))
                {
                    _unverified.Add(series);
                    Add("skip", "placeholder-cost-unverified", price.Unit, price.Variant, price.CostUsdPerUnit, newCost,
                        AdoptToken(_label, price));
                }
            }
            else
            {
                Skip(_skipped, "admin-owned", series, price, newCost);
            }
        }

        /// <summary>Records a close of a row this run wrote or ended. A run-created slot was never written, so it is not recorded.</summary>
        private void RecordClose(Slot slot, string reason, decimal? newCost)
        {
            if (slot.IsNew)
            {
                return;
            }

            Add("close", slot.Removed ? $"{reason},removed-not-yet-in-effect" : reason, slot.Row.Unit, slot.Row.Variant,
                slot.Row.CostUsdPerUnit, newCost);
        }

        private void PriceSeries(Guid capabilityId, string seriesLabel, List<SeedPriceRow> seedRows, List<Slot> slots)
        {
            foreach (SeedPriceRow row in seedRows)
            {
                DateTime? rowEnd = row.EffectiveTo is { } to ? ToUtc(to) : null;
                if (rowEnd is { } ended && ended <= now)
                {
                    continue; // a window already over: never back-date over past usage
                }

                DateTime seedStart = ToUtc(row.EffectiveFrom);
                DateTime start = seedStart > now ? seedStart : now;

                Slot? covering = slots.Where(s => s.Covers(start)).OrderByDescending(s => s.From).FirstOrDefault();
                decimal? replaced = covering?.Row.CostUsdPerUnit;
                if (covering is not null)
                {
                    Owner owner = OwnerOf(covering);
                    if (owner is not (Owner.Seed or Owner.SystemPlaceholder))
                    {
                        SkipNotOwned(owner, seriesLabel, covering.Row, row.CostUsdPerUnit);

                        continue;
                    }

                    // A PLACEHOLDER row at the seed's cost is still superseded: the sourced, dated seed row replaces the
                    // indicative one, so what is in force says where its figure came from.
                    if (covering.Row.CostUsdPerUnit == row.CostUsdPerUnit && owner == Owner.Seed)
                    {
                        _unchanged++;
                        Add("skip", "unchanged", row.Unit, row.Variant, covering.Row.CostUsdPerUnit, row.CostUsdPerUnit);
                        continue;
                    }

                    if (covering.From >= start)
                    {
                        // Starts exactly where the new price starts, so it has never been in effect: drop it rather
                        // than leave a zero-length window that would collide on (capability, unit, variant, from).
                        covering.Removed = true;
                        _removed.Add(covering);
                    }
                    else
                    {
                        covering.To = start;
                        _closed.Add(covering);
                    }

                    RecordClose(covering, owner == Owner.SystemPlaceholder ? "placeholder-superseded" : "superseded", row.CostUsdPerUnit);
                }

                DateTime? end = rowEnd;
                if (slots.Where(s => !s.Removed && s.From > start).Select(s => (DateTime?)s.From).Min() is { } next
                    && (end is null || next < end))
                {
                    end = next;
                }

                ModelCapabilityPrice price = new()
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = capabilityId,
                    Unit = row.Unit,
                    Variant = row.Variant,
                    CostUsdPerUnit = row.CostUsdPerUnit,
                    CreditsMillisPerUnitOverride = null,
                    EffectiveFrom = start,
                    EffectiveTo = end,
                    Notes = $"{NotesPrefix}{seed.CatalogVersion} | {row.Quoted} | {row.SourceUrl} | retrieved {row.RetrievedAt:yyyy-MM-dd}",
                    CreatedAt = now,
                };
                Slot inserted = new(price, isNew: true);
                slots.Add(inserted);
                _inserted.Add(inserted);
                Add("insert", $"seed {row.EffectiveFrom:yyyy-MM-dd}", row.Unit, row.Variant, replaced, row.CostUsdPerUnit);
            }
        }

        public void Apply(InferenceApiDbContext db)
        {
            foreach (Slot slot in _closed.Where(s => !s.IsNew))
            {
                slot.Row.EffectiveTo = slot.To;
                if (slot.EndNote is { } note)
                {
                    slot.Row.Notes += note;
                }
            }

            foreach (Slot slot in _removed.Where(s => !s.IsNew))
            {
                db.ModelCapabilityPrices.Remove(slot.Row);
            }

            foreach (Slot slot in _inserted.Where(s => !s.Removed))
            {
                slot.Row.EffectiveTo = slot.To;
                db.ModelCapabilityPrices.Add(slot.Row);
            }
        }

        public SeedRunReport Report() => new(_inserted.Count, _closed.Count + _removed.Count, _unchanged,
            _skipped, _unpriced, _notInSeed, _orphaned, _oracle, _adminRetired, _placeholders, _unverified, _actions);
    }
}
