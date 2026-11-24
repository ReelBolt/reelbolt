using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>A malformed list request (bad sort, cursor or query). The controller answers a bare 400 for the list only.</summary>
public sealed class MarketplaceBadRequestException : Exception
{
    public MarketplaceBadRequestException() : base("Bad marketplace list request")
    {
    }
}

/// <summary>
/// Read side of the public marketplace: nothing is written and no request text is logged. A recipe is visible only when it is
/// listed, not hidden by reports, and its CURRENT version has a ready preview with both keys.
/// </summary>
public sealed partial class MarketplacePublicQuery(
    InferenceApiDbContext db,
    IRecipeSearch search,
    IOptionsSnapshot<MarketplaceOptions> options)
{
    public const int DefaultLimit = 24;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 100;

    private enum SortKind { New, Copied, Rated, Relevance }

    private IQueryable<MarketplaceRecipe> Visible() =>
        db.MarketplaceRecipes.AsNoTracking().Where(r =>
            r.Status == RecipeStatus.Listed
            && !r.HiddenByReports
            && r.CurrentVersionId != null
            && db.MarketplaceRecipeVersions.Any(v =>
                v.Id == r.CurrentVersionId
                && v.PreviewStatus == PreviewStatus.Ready
                && v.PreviewKey != null
                && v.PosterKey != null));

    public async Task<RecipeListDto> ListAsync(RecipeListQuery request, CancellationToken ct)
    {
        if (HasControl(request.Q) || HasControl(request.Category) || HasControl(request.Cost) || HasControl(request.StepType)
            || HasControl(request.Language) || HasControl(request.Sort) || HasControl(request.Cursor))
            throw new MarketplaceBadRequestException();
        string? text = Normalize(request.Q);
        if (request.Q is { Length: > MaxQueryLength } || (request.Q is not null && text is { Length: > MaxQueryLength }))
            throw new MarketplaceBadRequestException();
        SortKind sort = ParseSort(request.Sort, text is not null);
        int limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaxLimit);
        (long A, long B, Guid Id)? cursor = ParseCursor(request.Cursor, sort);

        IQueryable<MarketplaceRecipe> source = Visible();
        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            string category = request.Category.Trim();
            source = source.Where(r => r.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(request.Cost))
        {
            string cost = request.Cost.Trim();
            source = source.Where(r => r.CostClass == cost);
        }

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            string language = request.Language.Trim();
            source = source.Where(r => r.Language == language);
        }

        if (!string.IsNullOrWhiteSpace(request.StepType))
        {
            string stepType = request.StepType.Trim();
            source = source.Where(r => r.StepTypes.Contains(stepType));
        }

        int min = Math.Max(1, options.Value.MinRatingsForAverage);

        // The text match (when there is a query) filters every sort; its rank is the first tie-break after the chosen key.
        IQueryable<KeyedRecipe> keyed = text is null
            ? source.Select(r => new KeyedRecipe { Recipe = r, K1 = 0, K2 = r.CopyOrgs })
            : search.Ranked(source, text);
        long ca = cursor?.A ?? 0;
        long cb = cursor?.B ?? 0;
        Guid cid = cursor?.Id ?? Guid.Empty;
        bool has = cursor is not null;
        DateTime at = sort == SortKind.New && has ? new DateTime(ca, DateTimeKind.Utc) : DateTime.MinValue;

        List<KeyedRecipe> rows;
        List<(MarketplaceRecipe Recipe, long A, long B)> page;
        switch (sort)
        {
            case SortKind.New:
                if (has)
                    keyed = keyed.Where(k => k.Recipe.ListedAt < at || (k.Recipe.ListedAt == at && (k.K1 < cb || (k.K1 == cb && k.Recipe.Id.CompareTo(cid) < 0))));
                rows = await keyed.OrderByDescending(k => k.Recipe.ListedAt).ThenByDescending(k => k.K1).ThenByDescending(k => k.Recipe.Id)
                    .Take(limit + 1).ToListAsync(ct);
                page = [.. rows.Select(k => (k.Recipe, k.Recipe.ListedAt?.Ticks ?? 0L, k.K1))];
                break;
            case SortKind.Copied:
                if (has)
                    keyed = keyed.Where(k => k.Recipe.CopyOrgs < ca || (k.Recipe.CopyOrgs == ca && (k.K1 < cb || (k.K1 == cb && k.Recipe.Id.CompareTo(cid) < 0))));
                rows = await keyed.OrderByDescending(k => k.Recipe.CopyOrgs).ThenByDescending(k => k.K1).ThenByDescending(k => k.Recipe.Id)
                    .Take(limit + 1).ToListAsync(ct);
                page = [.. rows.Select(k => (k.Recipe, (long)k.Recipe.CopyOrgs, k.K1))];
                break;
            case SortKind.Rated:
                // The average counts only from the minimum number of ratings; recipes under it come after, in the tie-break order.
                if (has)
                {
                    keyed = keyed.Where(k =>
                        (k.Recipe.RatingCount >= min ? (long)k.Recipe.RatingSum * 1000 / k.Recipe.RatingCount : -1) < ca
                        || ((k.Recipe.RatingCount >= min ? (long)k.Recipe.RatingSum * 1000 / k.Recipe.RatingCount : -1) == ca
                            && (k.K1 < cb || (k.K1 == cb && k.Recipe.Id.CompareTo(cid) < 0))));
                }

                rows = await keyed
                    .OrderByDescending(k => k.Recipe.RatingCount >= min ? (long)k.Recipe.RatingSum * 1000 / k.Recipe.RatingCount : -1)
                    .ThenByDescending(k => k.K1).ThenByDescending(k => k.Recipe.Id)
                    .Take(limit + 1).ToListAsync(ct);
                page = [.. rows.Select(k => (k.Recipe, k.Recipe.RatingCount >= min ? (long)k.Recipe.RatingSum * 1000 / k.Recipe.RatingCount : -1L, k.K1))];
                break;
            default:
                if (has)
                    keyed = keyed.Where(k => k.K1 < ca || (k.K1 == ca && (k.K2 < cb || (k.K2 == cb && k.Recipe.Id.CompareTo(cid) < 0))));
                rows = await keyed.OrderByDescending(k => k.K1).ThenByDescending(k => k.K2).ThenByDescending(k => k.Recipe.Id)
                    .Take(limit + 1).ToListAsync(ct);
                page = [.. rows.Select(k => (k.Recipe, k.K1, k.K2))];
                break;
        }

        bool more = page.Count > limit;
        if (more)
            page = page.GetRange(0, limit);

        Dictionary<Guid, MarketplaceRecipeVersion> versions = await VersionsAsync(page.Select(p => p.Recipe), ct);
        List<RecipeCardDto> items = [];
        foreach ((MarketplaceRecipe recipe, _, _) in page)
        {
            if (recipe.CurrentVersionId is { } vid && versions.TryGetValue(vid, out MarketplaceRecipeVersion? version))
                items.Add(Card(recipe, version, min));
        }

        string? next = more ? EncodeCursor(sort, page[^1].A, page[^1].B, page[^1].Recipe.Id) : null;
        return new RecipeListDto(items, next);
    }

    public async Task<RecipeDetailDto?> DetailAsync(Guid id, CancellationToken ct)
    {
        MarketplaceRecipe? recipe = await Visible().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (recipe?.CurrentVersionId is not { } vid)
            return null;
        MarketplaceRecipeVersion? version = await db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct);
        if (version is null)
            return null;

        int min = Math.Max(1, options.Value.MinRatingsForAverage);
        RecipeCardDto card = Card(recipe, version, min);
        PortableRecipe? snapshot = PortableRecipeJson.TryParse(version.SnapshotJson);
        List<StepDto> steps = [.. (snapshot?.Steps ?? []).OrderBy(s => s.Order).Select(s =>
            new StepDto(s.Order, Humanize(s.StepType.ToString()), s.Label ?? Humanize(s.StepType.ToString()), MarketplacePublishService.LimitsOf(s)))];
        List<PromptDto> prompts = [.. (snapshot?.TextFields() ?? []).Where(t => t.StepOrder is not null)
            .Select(t => new PromptDto(t.StepOrder!.Value, t.Field, t.Text))];
        List<SlotDto> slots = [.. (snapshot?.Slots ?? []).Select(s => new SlotDto(s.Kind, s.StepOrder, s.Label))];
        List<RecipeInputDto> inputs = [.. (snapshot?.Manifest?.Inputs ?? []).Select(i =>
        {
            string key = i.Field ?? i.Role ?? i.Kind;
            return new RecipeInputDto(key, i.Label ?? key, i.Kind);
        })];

        return new RecipeDetailDto(
            card.Id, card.Name, recipe.Description, card.Category, card.Language, card.CostClass, card.StepCount, card.StepTypes,
            card.AuthorLabel, card.CopyCount, card.RatingAverage, card.RatingCount, card.PreviewSeconds, card.ListedAt, card.Version,
            steps, prompts, slots, inputs, CostNote(recipe.CostClass));
    }

    public async Task<RecipeMedia?> MediaAsync(Guid id, CancellationToken ct)
    {
        MarketplaceRecipe? recipe = await Visible().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (recipe?.CurrentVersionId is not { } vid)
            return null;
        MarketplaceRecipeVersion? v = await db.MarketplaceRecipeVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vid, ct);
        return v is null ? null : new RecipeMedia(recipe.Id, v.Id, v.PreviewKey, v.PosterKey, v.PreviewSha256, v.PosterSha256);
    }

    public async Task<FacetsDto> FacetsAsync(CancellationToken ct)
    {
        var rows = await Visible().Select(r => new { r.Category, r.Language, r.CostClass, r.StepTypes }).ToListAsync(ct);
        static List<FacetEntryDto> Count(IEnumerable<string> keys) =>
            [.. keys.Where(k => !string.IsNullOrWhiteSpace(k)).GroupBy(k => k, StringComparer.Ordinal)
                .Select(g => new FacetEntryDto(g.Key, g.Count()))
                .OrderByDescending(f => f.Count).ThenBy(f => f.Key, StringComparer.Ordinal)];
        return new FacetsDto(
            Count(rows.Select(r => r.Category)),
            Count(rows.SelectMany(r => r.StepTypes.Distinct())),
            Count(rows.Select(r => r.Language ?? "")),
            Count(rows.Select(r => r.CostClass)));
    }

    private async Task<Dictionary<Guid, MarketplaceRecipeVersion>> VersionsAsync(IEnumerable<MarketplaceRecipe> recipes, CancellationToken ct)
    {
        List<Guid> ids = [.. recipes.Where(r => r.CurrentVersionId is not null).Select(r => r.CurrentVersionId!.Value)];
        if (ids.Count == 0)
            return [];
        return await db.MarketplaceRecipeVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
    }

    private static RecipeCardDto Card(MarketplaceRecipe r, MarketplaceRecipeVersion v, int min) => new(
        r.Id,
        r.Name,
        Truncate(r.Description, 200),
        r.Category,
        r.Language ?? "",
        r.CostClass,
        r.StepCount,
        r.StepTypes,
        r.AuthorLabel ?? "",
        r.CopyOrgs,
        r.RatingCount >= min && r.RatingCount > 0 ? Math.Round((double)r.RatingSum / r.RatingCount, 2) : null,
        r.RatingCount,
        v.PreviewSeconds ?? 0,
        r.ListedAt,
        v.N);

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        int cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..cut];
    }

    private static bool HasControl(string? value) => value is not null && value.Any(char.IsControl);

    private static string? Normalize(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return null;
        return q.Trim();
    }

    private static SortKind ParseSort(string? sort, bool hasText) => sort?.Trim().ToLowerInvariant() switch
    {
        null or "" => hasText ? SortKind.Relevance : SortKind.New,
        "new" => SortKind.New,
        "copied" => SortKind.Copied,
        "rated" => SortKind.Rated,
        _ => throw new MarketplaceBadRequestException()
    };

    private static string Tag(SortKind sort) => sort switch
    {
        SortKind.New => "n",
        SortKind.Copied => "c",
        SortKind.Rated => "r",
        _ => "q"
    };

    private static string EncodeCursor(SortKind sort, long a, long b, Guid id)
    {
        string raw = string.Create(CultureInfo.InvariantCulture, $"1|{Tag(sort)}|{a}|{b}|{id:D}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (long A, long B, Guid Id)? ParseCursor(string? cursor, SortKind sort)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        if (cursor.Length > 200 || !CursorChars().IsMatch(cursor))
            throw new MarketplaceBadRequestException();
        try
        {
            string padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            string raw = Encoding.ASCII.GetString(Convert.FromBase64String(padded));
            string[] parts = raw.Split('|');
            if (parts.Length != 5 || parts[0] != "1" || parts[1] != Tag(sort)
                || !long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long a)
                || !long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long b)
                || !Guid.TryParseExact(parts[4], "D", out Guid id))
                throw new MarketplaceBadRequestException();
            if (sort == SortKind.New && (a < DateTime.MinValue.Ticks || a > DateTime.MaxValue.Ticks))
                throw new MarketplaceBadRequestException();
            return (a, b, id);
        }
        catch (FormatException)
        {
            throw new MarketplaceBadRequestException();
        }
    }

    private static string CostNote(string costClass) => costClass switch
    {
        AppCostClass.NoCredits => "Running this recipe does not use credits.",
        AppCostClass.UsesCredits => "Running this recipe uses credits.",
        AppCostClass.UsesCreditsAndVoice => "Running this recipe uses credits and needs a voice.",
        AppCostClass.UsesCreditsAndAiShots => "Running this recipe uses credits and generates AI shots.",
        _ => ""
    };

    private static string Humanize(string pascal) => WordBoundary().Replace(pascal, " ");

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex CursorChars();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();
}
