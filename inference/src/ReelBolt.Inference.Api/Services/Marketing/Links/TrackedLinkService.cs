using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing.Links;

/// <summary>The fields of a new tracked link, as the admin sent them (untrimmed, unvalidated).</summary>
public sealed record TrackedLinkInput(
    string? Code,
    string? Channel,
    string? Medium,
    string? Campaign,
    string? Placement,
    string? DestinationPath,
    string? Persona,
    long? CostMinor,
    string? CostCurrency);

/// <summary>A tracked link with the clicks it has had on every day so far (bot hits not included).</summary>
public sealed record TrackedLinkWithClicks(MarketingLink Link, long TotalClicks);

/// <summary>
/// A refusal with a stable code: one of the <c>*_invalid</c> codes of <see cref="TrackedLinkRules.FirstProblem"/>,
/// <c>invalid_body</c>, <c>field_immutable</c> (with <see cref="Field"/>) or <c>link_code_taken</c>.
/// </summary>
public sealed class TrackedLinkRefusal(string code, string? fieldName = null) : Exception(code)
{
    public string Code { get; } = code;

    /// <summary>The JSON field the refusal is about, when it is about one.</summary>
    public string? Field { get; } = fieldName;
}

/// <summary>
/// What the anonymous redirect needs: find an active link by its code, and add one to its counter.
/// An interface so the redirect can be tested against a counter that fails.
/// </summary>
public interface ITrackedLinkDirectory
{
    /// <summary>The link with this code that is not archived, or null. Letter case in <paramref name="code"/> is ignored.</summary>
    Task<MarketingLink?> FindActiveAsync(string code, CancellationToken ct);

    /// <summary>
    /// Adds one to the link's <c>clicks</c> (or <c>bot_hits</c> when <paramref name="bot"/>) for
    /// <paramref name="day"/>, creating the day's row when it is the first hit. One atomic statement.
    /// </summary>
    Task RecordHitAsync(Guid linkId, DateOnly day, bool bot, CancellationToken ct);
}

/// <summary>
/// Tracked links (docs/design/new-user-funnel.md sections 5 and 6): the platform admin's list, create and
/// edit, and the two reads and one write the anonymous redirect makes. Platform-wide, so nothing here is
/// scoped to an organization; the admin controller checks platform authority before it calls in.
/// </summary>
public sealed class TrackedLinkService(InferenceApiDbContext db, TimeProvider? clock = null) : ITrackedLinkDirectory
{
    /// <summary>
    /// The counter upsert, as it runs against PostgreSQL (and SQLite in tests): the primary key
    /// <c>(link_id, day)</c> is the conflict target, so the first hit of a day inserts the row and every
    /// later one adds to it, in ONE statement. Two concurrent redirects therefore never lose a count and
    /// never collide on the key, with no lock and no read-then-write.
    /// </summary>
    public const string RecordHitSql =
        "INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits) VALUES ({0}, {1}, {2}, {3}) "
        + "ON CONFLICT (link_id, day) DO UPDATE SET "
        + "clicks = marketing_link_daily_stats.clicks + EXCLUDED.clicks, "
        + "bot_hits = marketing_link_daily_stats.bot_hits + EXCLUDED.bot_hits";

    /// <summary>The fields of a link that never change once it exists.</summary>
    public static readonly IReadOnlyList<string> ImmutableFields = ["code", "channel", "medium", "campaign"];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Every link, newest first, each with its total clicks.</summary>
    public async Task<IReadOnlyList<TrackedLinkWithClicks>> ListAsync(CancellationToken ct)
    {
        List<MarketingLink> links = await db.MarketingLinks.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt).ThenBy(l => l.Code)
            .ToListAsync(ct);
        Dictionary<Guid, long> clicks = await db.MarketingLinkDailyStats.AsNoTracking()
            .GroupBy(s => s.LinkId)
            .Select(g => new { LinkId = g.Key, Clicks = g.Sum(s => (long)s.Clicks) })
            .ToDictionaryAsync(x => x.LinkId, x => x.Clicks, ct);
        return [.. links.Select(l => new TrackedLinkWithClicks(l, clicks.GetValueOrDefault(l.Id)))];
    }

    /// <summary>
    /// Creates a link. Every string is trimmed first; an empty persona or currency reads as none. Throws
    /// <see cref="TrackedLinkRefusal"/> for the first field outside its closed set or shape, and
    /// <c>link_code_taken</c> when the code already names a link (archived ones included: a code is never reused).
    /// </summary>
    public async Task<MarketingLink> CreateAsync(TrackedLinkInput input, Guid adminUserId, CancellationToken ct)
    {
        MarketingLink link = new()
        {
            Id = Guid.NewGuid(),
            Code = Trim(input.Code),
            Channel = Trim(input.Channel),
            Medium = Trim(input.Medium),
            Campaign = Trim(input.Campaign),
            Placement = Trim(input.Placement),
            DestinationPath = Trim(input.DestinationPath),
            Persona = Blank(input.Persona),
            CostMinor = input.CostMinor,
            CostCurrency = Blank(input.CostCurrency),
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            CreatedByUserId = adminUserId == Guid.Empty ? null : adminUserId,
        };
        if (TrackedLinkRules.FirstProblem(link) is { } problem) throw new TrackedLinkRefusal(problem);
        if (await db.MarketingLinks.AsNoTracking().AnyAsync(l => l.Code == link.Code, ct))
            throw new TrackedLinkRefusal("link_code_taken", "code");

        db.MarketingLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ProjectOrganizationRules.IsUniqueViolation(ex))
        {
            // Two admins created the same code at once: the unique index decided, and this one lost.
            db.Entry(link).State = EntityState.Detached;
            throw new TrackedLinkRefusal("link_code_taken", "code");
        }

        return link;
    }

    /// <summary>
    /// Applies a partial edit: <c>placement</c>, <c>destinationPath</c>, <c>persona</c>, <c>costMinor</c>,
    /// <c>costCurrency</c> and <c>archived</c>. A field that is absent is left alone; <c>persona</c>,
    /// <c>costMinor</c> and <c>costCurrency</c> are cleared by an explicit <c>null</c>. Code, channel,
    /// medium and campaign are immutable: naming one with a different value is refused
    /// (<c>field_immutable</c>), while echoing the stored value back is accepted. Any other field is
    /// ignored. Returns null when no link has this id; throws <see cref="TrackedLinkRefusal"/> and writes
    /// nothing when the edit is not valid.
    /// </summary>
    public async Task<TrackedLinkWithClicks?> PatchAsync(Guid id, JsonElement patch, CancellationToken ct)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new TrackedLinkRefusal("invalid_body");
        MarketingLink? link = await db.MarketingLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (link is null) return null;

        try
        {
            Apply(link, patch);
            if (TrackedLinkRules.FirstProblem(link) is { } problem) throw new TrackedLinkRefusal(problem);
        }
        catch (TrackedLinkRefusal)
        {
            // The entity was changed in memory; forget it so nothing of a refused edit can be saved later.
            db.Entry(link).State = EntityState.Detached;
            throw;
        }

        await db.SaveChangesAsync(ct);
        long clicks = await db.MarketingLinkDailyStats.AsNoTracking()
            .Where(s => s.LinkId == id).SumAsync(s => (long)s.Clicks, ct);
        return new TrackedLinkWithClicks(link, clicks);
    }

    public Task<MarketingLink?> FindActiveAsync(string code, CancellationToken ct)
    {
        // Stored codes are lower-case (the create rule and the pattern), so a code is looked up lowered.
        string wanted = TrackedLinkRules.NormalizeCode(code) ?? string.Empty;
        return db.MarketingLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Code == wanted && l.ArchivedAt == null, ct);
    }

    public Task RecordHitAsync(Guid linkId, DateOnly day, bool bot, CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(RecordHitSql, [linkId, day, bot ? 0 : 1, bot ? 1 : 0], ct);

    private void Apply(MarketingLink link, JsonElement patch)
    {
        foreach (JsonProperty property in patch.EnumerateObject())
        {
            JsonElement value = property.Value;
            switch (property.Name)
            {
                case "code":
                    RefuseChange(property.Name, value, link.Code);
                    break;
                case "channel":
                    RefuseChange(property.Name, value, link.Channel);
                    break;
                case "medium":
                    RefuseChange(property.Name, value, link.Medium);
                    break;
                case "campaign":
                    RefuseChange(property.Name, value, link.Campaign);
                    break;
                case "placement":
                    link.Placement = Text(value) ?? throw new TrackedLinkRefusal("placement_invalid", property.Name);
                    break;
                case "destinationPath":
                    link.DestinationPath = Text(value) ?? throw new TrackedLinkRefusal("destination_path_invalid", property.Name);
                    break;
                case "persona":
                    link.Persona = value.ValueKind == JsonValueKind.Null
                        ? null
                        : Blank(Text(value) ?? throw new TrackedLinkRefusal("persona_invalid", property.Name));
                    break;
                case "costMinor":
                    link.CostMinor = value.ValueKind == JsonValueKind.Null
                        ? null
                        : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long minor)
                            ? minor
                            : throw new TrackedLinkRefusal("cost_invalid", property.Name);
                    break;
                case "costCurrency":
                    link.CostCurrency = value.ValueKind == JsonValueKind.Null
                        ? null
                        : Blank(Text(value) ?? throw new TrackedLinkRefusal("cost_invalid", property.Name));
                    break;
                case "archived":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new TrackedLinkRefusal("invalid_body", property.Name);
                    // Archiving twice keeps the first time; un-archiving makes the link redirect again.
                    link.ArchivedAt = value.GetBoolean() ? link.ArchivedAt ?? _clock.GetUtcNow().UtcDateTime : null;
                    break;
            }
        }
    }

    private static void RefuseChange(string field, JsonElement value, string stored)
    {
        if (value.ValueKind != JsonValueKind.String || !string.Equals(value.GetString(), stored, StringComparison.Ordinal))
            throw new TrackedLinkRefusal("field_immutable", field);
    }

    /// <summary>The trimmed text of a JSON string, or null when the value is not a string.</summary>
    private static string? Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : null;

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
