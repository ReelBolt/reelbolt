using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Billing;

/// <summary>
/// The typed view of <c>Billing/model-catalog.seed.json</c>, the price book built by
/// <c>infra/scripts/model-catalog/build_seed.py</c> (never hand-edited; the generator is maintained outside this repository), plus the guard that refuses a seed
/// whose model codes, units or variants the code does not know.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two layers of refusal.</b> <see cref="Load"/> throws <see cref="InvalidDataException"/> for anything that
/// cannot be typed at all (malformed JSON, a missing field, an unknown provider kind or usage unit, a bad date
/// or cost). <see cref="Validate"/> then checks the typed seed against the code's own tables
/// (<see cref="ProviderCapabilityUsage"/>, the fal / MiniMax / Higgsfield / ComfyUI catalogues) and returns
/// one line per problem, so a caller sees every problem at once.
/// </para>
/// <para>
/// <b>Capability is a string</b> because the seed carries <c>"Compute"</c> (the render / encode / analyse
/// seconds billed to kind <c>OpenAICompatible</c>, model <c>compute</c>), which is not (yet) an
/// <see cref="InferenceProviderCapability"/>. Every other capability must parse to the enum.
/// </para>
/// </remarks>
public sealed record ModelCatalogSeed(int SchemaVersion, string CatalogVersion, DateOnly RetrievedAt,
    IReadOnlyList<SeedPriceRow> Rows, IReadOnlyList<SeedCapabilityEntry> Capabilities)
{
    public const string FileName = "model-catalog.seed.json";

    /// <summary>The pseudo-capability for compute seconds; valid only on kind OpenAICompatible, model <see cref="ComputeModelCode"/>.</summary>
    public const string ComputeCapability = "Compute";

    /// <summary>The model code the compute rows are filed under.</summary>
    public const string ComputeModelCode = "compute";

    private static readonly UsageKind[] ComputeUnits =
        [UsageKind.RenderOutputSeconds, UsageKind.EncodeOutputSeconds, UsageKind.AnalyzeMediaSeconds];

    private static readonly string[] PriceSources = ["Table", "Oracle", "Unpriced"];

    /// <summary>Per-megapixel labels on a fal image endpoint that has no billing tier field (informational rows).</summary>
    private static readonly Regex MegapixelVariant = new("^[1-9][0-9]*MP$", RegexOptions.CultureInvariant);

    /// <summary>Reads and types the seed at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a well-formed, fully typed seed.</exception>
    public static ModelCatalogSeed Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads the seed shipped next to the binaries: <c>AppContext.BaseDirectory/Billing/model-catalog.seed.json</c>.</summary>
    public static ModelCatalogSeed LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, "Billing", FileName));

    internal static ModelCatalogSeed Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Every problem with the seed, one line each; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> problems = [];
        foreach (SeedCapabilityEntry entry in Capabilities)
        {
            List<SeedPriceRow> rows = Rows.Where(r => r.Kind == entry.Kind
                && string.Equals(r.ModelCode, entry.ModelCode, StringComparison.Ordinal)
                && string.Equals(r.Capability, entry.Capability, StringComparison.Ordinal)).ToList();
            ValidateEntry(entry, rows, problems);
        }

        foreach (IGrouping<(InferenceProviderKind Kind, string ModelCode, string Capability), SeedCapabilityEntry> group in Capabilities
                     .GroupBy(c => (c.Kind, c.ModelCode, c.Capability)).Where(g => g.Count() > 1))
        {
            problems.Add($"{Where(group.Key.Kind, group.Key.ModelCode, group.Key.Capability)}: duplicate capability entry.");
        }

        ValidateRowIntegrity(problems);

        foreach (IGrouping<(InferenceProviderKind Kind, string DisplayName), string> group in Capabilities
                     .Select(c => (c.Kind, c.DisplayName, c.ModelCode)).Distinct()
                     .GroupBy(c => (c.Kind, c.DisplayName), c => c.ModelCode)
                     .Where(g => g.Count() > 1))
        {
            problems.Add($"{group.Key.Kind}: display name '{group.Key.DisplayName}' is used by more than one model " +
                $"({string.Join(", ", group.OrderBy(c => c, StringComparer.Ordinal))}).");
        }

        return problems;
    }

    /// <summary>
    /// The rows for exactly this (kind, capability, model code) effective at <paramref name="at"/>
    /// (<c>EffectiveFrom &lt;= the UTC date of at &lt; EffectiveTo</c>), ordered by unit then variant (null first).
    /// Seed dates are UTC calendar dates, so a caller's offset never moves a price change to its local midnight.
    /// Matching is ordinal: never case-folded, never a prefix.
    /// </summary>
    public IReadOnlyList<SeedPriceRow> SuggestFor(InferenceProviderKind kind, string capability, string modelCode, DateTimeOffset at)
    {
        DateOnly day = DateOnly.FromDateTime(at.UtcDateTime);
        return Rows
            .Where(r => r.Kind == kind
                && string.Equals(r.Capability, capability, StringComparison.Ordinal)
                && string.Equals(r.ModelCode, modelCode, StringComparison.Ordinal)
                && IsEffective(r, day))
            .OrderBy(r => r.Unit)
            .ThenBy(r => r.Variant is not null)
            .ThenBy(r => r.Variant, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The catalogue entry for exactly this (kind, capability, model code), or null. Ordinal match.</summary>
    public SeedCapabilityEntry? Find(InferenceProviderKind kind, string capability, string modelCode) =>
        Capabilities.FirstOrDefault(c => c.Kind == kind
            && string.Equals(c.Capability, capability, StringComparison.Ordinal)
            && string.Equals(c.ModelCode, modelCode, StringComparison.Ordinal));

    private static bool IsEffective(SeedPriceRow row, DateOnly day) =>
        row.EffectiveFrom <= day && (row.EffectiveTo is not { } to || day < to);

    // ------------------------------------------------------------------ validation

    private void ValidateEntry(SeedCapabilityEntry entry, List<SeedPriceRow> rows, List<string> problems)
    {
        string where = Where(entry.Kind, entry.ModelCode, entry.Capability);

        // Rules 1-3: the capability parses, the kind serves it, every unit is one it meters.
        UsageKind[]? allowedUnits = null;
        if (string.Equals(entry.Capability, ComputeCapability, StringComparison.Ordinal))
        {
            if (entry.Kind != InferenceProviderKind.OpenAICompatible
                || !string.Equals(entry.ModelCode, ComputeModelCode, StringComparison.Ordinal))
            {
                problems.Add($"{where}: capability Compute is only valid on kind OpenAICompatible, model '{ComputeModelCode}'.");
            }

            allowedUnits = ComputeUnits;
        }
        else if (TryParseExact(entry.Capability, out InferenceProviderCapability capability))
        {
            if (!ProviderCapabilityUsage.ServableByKind.TryGetValue(entry.Kind, out InferenceProviderCapability[]? servable)
                || !servable.Contains(capability))
            {
                problems.Add($"{where}: capability {capability} is not servable by kind {entry.Kind} (ProviderCapabilityUsage.ServableByKind).");
            }

            allowedUnits = ProviderCapabilityUsage.ByCapability[capability];
        }
        else
        {
            problems.Add($"{where}: capability '{entry.Capability}' is not an InferenceProviderCapability (nor Compute).");
        }

        if (allowedUnits is not null)
        {
            foreach (UsageKind unit in rows.Select(r => r.Unit).Distinct().Where(u => !allowedUnits.Contains(u)))
            {
                problems.Add($"{where}: unit {unit} is not one capability {entry.Capability} meters " +
                    $"(allowed: {string.Join(", ", allowedUnits)}).");
            }
        }

        // Rule 4: the price source's shape. A Table needs a null-variant row for every required unit on the
        // retrieval date and on every date where any of its rows starts or ends, so a series that closes
        // without a successor (and would silently become unpriced) is refused.
        switch (entry.PriceSource)
        {
            case "Table":
                List<DateOnly> checkpoints = rows.Select(r => r.EffectiveFrom)
                    .Concat(rows.Select(r => r.EffectiveTo).OfType<DateOnly>())
                    .Append(RetrievedAt)
                    .Where(d => d >= RetrievedAt)
                    .Distinct().Order().ToList();
                foreach (UsageKind unit in allowedUnits ?? [])
                {
                    foreach (DateOnly day in checkpoints)
                    {
                        if (!rows.Any(r => r.Unit == unit && r.Variant is null && IsEffective(r, day)))
                        {
                            problems.Add($"{where}: Table has no null-variant {unit} row effective on {day:yyyy-MM-dd}.");
                        }
                    }
                }

                break;
            case "Oracle":
                if (entry.Kind != InferenceProviderKind.Higgsfield)
                {
                    problems.Add($"{where}: price source Oracle is only valid for kind Higgsfield.");
                }

                if (rows.Count > 0)
                {
                    problems.Add($"{where}: price source Oracle must carry no price rows (has {rows.Count}).");
                }

                break;
            case "Unpriced":
                if (rows.Count > 0)
                {
                    problems.Add($"{where}: price source Unpriced must carry no price rows (has {rows.Count}).");
                }

                if (string.IsNullOrWhiteSpace(entry.UnpricedReason))
                {
                    problems.Add($"{where}: price source Unpriced needs a non-empty reason (unpricedReason).");
                }

                break;
            default:
                problems.Add($"{where}: price source '{entry.PriceSource}' is not one of {string.Join(", ", PriceSources)}.");
                break;
        }

        // Rule 5 (T-037): a zero price is a self-hosted default, and only an OpenAICompatible or OpenJev Table may carry one.
        // selfHostedOnly marks exactly those entries; the seeder then prices them at zero only on a local endpoint.
        bool hasZero = rows.Any(r => r.CostUsdPerUnit == 0m);
        if (entry.SelfHostedOnly)
        {
            if (!string.Equals(entry.PriceSource, "Table", StringComparison.Ordinal))
            {
                problems.Add($"{where}: selfHostedOnly needs price source Table (has '{entry.PriceSource}').");
            }

            if (entry.Kind is not (InferenceProviderKind.OpenAICompatible or InferenceProviderKind.OpenJev))
            {
                problems.Add($"{where}: selfHostedOnly is only valid on kind OpenAICompatible or OpenJev (has {entry.Kind}).");
            }

            foreach (SeedPriceRow row in rows.Where(r => r.CostUsdPerUnit != 0m))
            {
                problems.Add($"{where}: selfHostedOnly needs every price at 0; {row.Unit} is {row.CostUsdPerUnit.ToString(CultureInfo.InvariantCulture)}.");
            }
        }
        else if (hasZero && entry.PriceSource == "Table"
                 && (entry.Kind == InferenceProviderKind.OpenAICompatible || entry.Kind == InferenceProviderKind.OpenJev)
                 && !IsGenuinelyFreeCapability(entry))
        {
            problems.Add($"{where}: a zero price on kind {entry.Kind} must carry selfHostedOnly (a self-hosted default).");
        }

        // Rule 6: the code knows this model code and every variant it carries.
        ValidateAgainstCode(entry, rows, where, problems);
    }

    /// <summary>
    /// A capability the VENDOR publishes as free is priced at $0 for everyone, not only for a
    /// self-hosted endpoint, so it does not need <c>selfHostedOnly</c>. Today that is OpenAI's
    /// moderation API (<see cref="InferenceProviderCapability.ContentModeration"/>), whose model
    /// page lists $0 and which is never self-hosted through a provider row.
    /// </summary>
    private static bool IsGenuinelyFreeCapability(SeedCapabilityEntry entry) =>
        string.Equals(entry.Capability, nameof(InferenceProviderCapability.ContentModeration), StringComparison.Ordinal);

    private void ValidateRowIntegrity(List<string> problems)
    {
        foreach (SeedPriceRow row in Rows)
        {
            string where = $"{Where(row.Kind, row.ModelCode, row.Capability)} {row.Unit}{VariantLabel(row.Variant)}";
            if (row.CostUsdPerUnit < 0m)
            {
                problems.Add($"{where}: cost {row.CostUsdPerUnit.ToString(CultureInfo.InvariantCulture)} is negative.");
            }

            if (row.EffectiveTo is { } to && to <= row.EffectiveFrom)
            {
                problems.Add($"{where}: effectiveTo {to:yyyy-MM-dd} is not after effectiveFrom {row.EffectiveFrom:yyyy-MM-dd}.");
            }
        }

        foreach (IGrouping<(InferenceProviderKind Kind, string ModelCode, string Capability, UsageKind Unit, string? Variant), SeedPriceRow> series in Rows
                     .GroupBy(r => (r.Kind, r.ModelCode, r.Capability, r.Unit, r.Variant)))
        {
            string where = $"{Where(series.Key.Kind, series.Key.ModelCode, series.Key.Capability)} {series.Key.Unit}{VariantLabel(series.Key.Variant)}";
            foreach (IGrouping<DateOnly, SeedPriceRow> sameStart in series.GroupBy(r => r.EffectiveFrom).Where(g => g.Count() > 1))
            {
                problems.Add($"{where}: duplicate row effective from {sameStart.Key:yyyy-MM-dd}.");
            }

            List<SeedPriceRow> ordered = series.OrderBy(r => r.EffectiveFrom).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                SeedPriceRow previous = ordered[i - 1];
                if (previous.EffectiveFrom != ordered[i].EffectiveFrom
                    && (previous.EffectiveTo is not { } end || end > ordered[i].EffectiveFrom))
                {
                    problems.Add($"{where}: window from {previous.EffectiveFrom:yyyy-MM-dd} overlaps the one from {ordered[i].EffectiveFrom:yyyy-MM-dd}.");
                }
            }
        }
    }

    private static void ValidateAgainstCode(SeedCapabilityEntry entry, List<SeedPriceRow> rows, string where, List<string> problems)
    {
        List<string> variants = rows.Select(r => r.Variant).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        switch (entry.Kind)
        {
            case InferenceProviderKind.FalAi:
                ValidateFal(entry.ModelCode, variants, where, problems);
                break;
            case InferenceProviderKind.MiniMax
                when string.Equals(entry.Capability, nameof(InferenceProviderCapability.VideoGeneration), StringComparison.Ordinal):
                ValidateMiniMaxVideo(entry.ModelCode, variants, where, problems);
                break;
            case InferenceProviderKind.Higgsfield:
                if (!HiggsfieldVideoGenerationClient.KnownEndpointIds.Contains(entry.ModelCode, StringComparer.Ordinal))
                {
                    problems.Add($"{where}: '{entry.ModelCode}' is not a HiggsfieldVideoGenerationClient endpoint id.");
                }

                break;
            case InferenceProviderKind.ComfyUI:
                if (!ComfyUiWorkflowTemplates.Names.Contains(entry.ModelCode, StringComparer.Ordinal))
                {
                    problems.Add($"{where}: '{entry.ModelCode}' is not a ComfyUiWorkflowTemplates id.");
                }

                break;
        }
    }

    private static void ValidateFal(string modelCode, List<string> variants, string where, List<string> problems)
    {
        if (FalVideoCatalog.Instance.Find(modelCode) is { } video)
        {
            foreach (string variant in variants.Where(v => !video.Resolutions.Contains(v, StringComparer.Ordinal)))
            {
                problems.Add($"{where}: variant '{variant}' is not a resolution FalVideoCatalog lists for '{modelCode}' " +
                    $"({string.Join(", ", video.Resolutions)}).");
            }

            return;
        }

        if (FalImageCatalog.Instance.Find(modelCode) is { } image)
        {
            if (image.BillingTierField is { } tierField)
            {
                IReadOnlyList<string> tiers = image.FieldNamed(tierField)?.Values ?? [];
                foreach (string variant in variants.Where(v => !tiers.Contains(v, StringComparer.Ordinal)))
                {
                    problems.Add($"{where}: variant '{variant}' is not a '{tierField}' tier FalImageCatalog emits for '{modelCode}' " +
                        $"({string.Join(", ", tiers)}).");
                }
            }
            else
            {
                foreach (string variant in variants.Where(v => !MegapixelVariant.IsMatch(v)))
                {
                    problems.Add($"{where}: '{modelCode}' has no billing tier in FalImageCatalog, so variant '{variant}' " +
                        "can only be a megapixel label (<n>MP).");
                }
            }

            return;
        }

        if (FalAiAnalysisClient.KnownModels.Contains(modelCode, StringComparer.Ordinal))
        {
            foreach (string variant in variants)
            {
                problems.Add($"{where}: analysis endpoint '{modelCode}' emits no variant, so '{variant}' can never match.");
            }

            return;
        }

        problems.Add($"{where}: '{modelCode}' is not in FalVideoCatalog, FalImageCatalog or FalAiAnalysisClient.KnownModels.");
    }

    private static void ValidateMiniMaxVideo(string modelCode, List<string> variants, string where, List<string> problems)
    {
        if (variants.Count == 0)
        {
            return;
        }

        List<string> resolutions;
        try
        {
            // Describe is a pure, offline switch on the model id; the client never touches the network here.
            using HttpClient http = new();
            resolutions = new MiniMaxVideoGenerationClient(http, string.Empty).Describe(modelCode).PricePerSecondByResolution.Keys.ToList();
        }
        catch (NotSupportedException)
        {
            problems.Add($"{where}: MiniMaxVideoGenerationClient.Describe does not serve '{modelCode}', so its variants cannot be matched.");
            return;
        }

        foreach (string variant in variants.Where(v => !resolutions.Contains(v, StringComparer.Ordinal)))
        {
            problems.Add($"{where}: variant '{variant}' is not a MiniMaxVideoGenerationClient.Describe resolution " +
                $"({string.Join(", ", resolutions)}).");
        }
    }

    private static bool TryParseExact<T>(string value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        return Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) && Enum.TryParse(value, ignoreCase: false, out parsed);
    }

    private static string Where(InferenceProviderKind kind, string modelCode, string capability) =>
        $"{kind} '{modelCode}' {capability}";

    private static string VariantLabel(string? variant) => variant is null ? "" : $"#{variant}";

    // ------------------------------------------------------------------ reading

    private static ModelCatalogSeed Read(JsonElement root)
    {
        JsonElement schemaVersion = Required(root, "schemaVersion", "root");
        if (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out int version))
        {
            throw new InvalidDataException($"{FileName}: schemaVersion is not an integer.");
        }

        string catalogVersion = RequiredString(root, "catalogVersion", "root");
        DateOnly retrievedAt = RequiredDate(root, "retrievedAt", "root");

        List<SeedPriceRow> rows = [];
        List<SeedCapabilityEntry> capabilities = [];
        foreach (JsonElement provider in RequiredArray(root, "providers", "root"))
        {
            string kindText = RequiredString(provider, "kind", "provider");
            InferenceProviderKind kind = ParseEnum<InferenceProviderKind>(kindText, "provider kind");
            foreach (JsonElement model in RequiredArray(provider, "models", kindText))
            {
                string modelCode = RequiredString(model, "modelCode", kindText);
                string context = $"{kindText} '{modelCode}'";
                string displayName = RequiredString(model, "displayName", context);
                string lineup = RequiredString(model, "lineup", context);
                string? vendor = OptionalString(model, "vendor", context);
                DateOnly? retiresOn = OptionalDate(model, "retiresOn", context);
                string? modelNotes = OptionalString(model, "notes", context) is { } notes && !string.IsNullOrWhiteSpace(notes) ? notes : null;
                foreach (JsonElement cap in RequiredArray(model, "capabilities", context))
                {
                    string capability = RequiredString(cap, "capability", context);
                    string capContext = $"{context} {capability}";
                    capabilities.Add(new SeedCapabilityEntry(kind, modelCode, displayName, lineup, vendor, retiresOn,
                        capability, RequiredString(cap, "priceSource", capContext), OptionalString(cap, "unpricedReason", capContext),
                        modelNotes, OptionalBool(cap, "selfHostedOnly", capContext)));
                    foreach (JsonElement price in RequiredArray(cap, "prices", capContext))
                    {
                        rows.Add(new SeedPriceRow(kind, modelCode, capability,
                            ParseEnum<UsageKind>(RequiredString(price, "unit", capContext), $"{capContext} unit"),
                            OptionalString(price, "variant", capContext),
                            RequiredDecimal(price, "costUsdPerUnit", capContext),
                            RequiredDate(price, "effectiveFrom", capContext),
                            OptionalDate(price, "effectiveTo", capContext),
                            RequiredString(price, "quoted", capContext),
                            RequiredString(price, "sourceUrl", capContext),
                            RequiredDate(price, "retrievedAt", capContext),
                            RequiredString(price, "notes", capContext)));
                    }
                }
            }
        }

        return new ModelCatalogSeed(version, catalogVersion, retrievedAt, rows, capabilities);
    }

    private static T ParseEnum<T>(string value, string what) where T : struct, Enum =>
        TryParseExact(value, out T parsed)
            ? parsed
            : throw new InvalidDataException($"{FileName}: {what} '{value}' is not a {typeof(T).Name}.");

    private static JsonElement Required(JsonElement element, string name, string context) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException($"{FileName}: {context} is missing '{name}'.");

    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement element, string name, string context)
    {
        JsonElement value = Required(element, name, context);
        return value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : throw new InvalidDataException($"{FileName}: {context} '{name}' is not an array.");
    }

    private static string RequiredString(JsonElement element, string name, string context)
    {
        JsonElement value = Required(element, name, context);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"{FileName}: {context} '{name}' is not a string.");
    }

    private static string? OptionalString(JsonElement element, string name, string context)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidDataException($"{FileName}: {context} '{name}' is not a string or null.");
    }

    private static DateOnly RequiredDate(JsonElement element, string name, string context) =>
        ParseDate(RequiredString(element, name, context), name, context);

    /// <summary>An optional boolean: absent is false; present and not a boolean is refused.</summary>
    private static bool OptionalBool(JsonElement element, string name, string context)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return false;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"{FileName}: {context} '{name}' is not a boolean.");
    }

    private static DateOnly? OptionalDate(JsonElement element, string name, string context) =>
        OptionalString(element, name, context) is { } text ? ParseDate(text, name, context) : null;

    private static DateOnly ParseDate(string text, string name, string context) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : throw new InvalidDataException($"{FileName}: {context} '{name}' value '{text}' is not a yyyy-MM-dd date.");

    private static decimal RequiredDecimal(JsonElement element, string name, string context)
    {
        JsonElement value = Required(element, name, context);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"{FileName}: {context} '{name}' is not a decimal.");
    }
}

/// <summary>
/// One (kind, model, capability) of the seed, with how it is priced. <see cref="ModelNotes"/> is the research
/// note's model-level caveat (e.g. a promotional price and its end date), shared by every capability of the model;
/// null when the seed carries none.
/// <para><see cref="SelfHostedOnly"/> (T-037): the Table prices are zero only while the provider's endpoint is local
/// (<see cref="SelfHostedEndpoint.IsLocal"/>); on a hosted endpoint the seeder prices nothing and the entry stays Unpriced.</para>
/// </summary>
public sealed record SeedCapabilityEntry(InferenceProviderKind Kind, string ModelCode, string DisplayName,
    string Lineup, string? Vendor, DateOnly? RetiresOn, string Capability, string PriceSource, string? UnpricedReason,
    string? ModelNotes = null, bool SelfHostedOnly = false);

/// <summary>One price of the seed: USD per <see cref="Unit"/> for (kind, model, capability, variant) over an effective window.</summary>
public sealed record SeedPriceRow(InferenceProviderKind Kind, string ModelCode, string Capability, UsageKind Unit,
    string? Variant, decimal CostUsdPerUnit, DateOnly EffectiveFrom, DateOnly? EffectiveTo,
    string Quoted, string SourceUrl, DateOnly RetrievedAt, string Notes);
