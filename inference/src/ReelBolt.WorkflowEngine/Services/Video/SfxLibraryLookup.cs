using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One enabled built-in library sound, including its server-side storage key (never shown to a model or a client).</summary>
public sealed record SfxLibraryEntry(
    Guid Id, string Slug, string Title, string Category, IReadOnlyList<string> Tags, int DurationMs, long SizeBytes,
    string MimeType, string Sha256, string StorageKey);

/// <summary>
/// The engine's read-only view of the built-in sound library (docs/sfx-library.md). This is the TRUSTED lookup: a compile
/// resolves a cue's library item by id here, the key comes from the table and nowhere else (never from a plan, an agent or
/// a request), and only an enabled item under the platform prefix resolves.
/// </summary>
public interface ISfxLibraryLookup
{
    /// <summary>Every enabled item in library order.</summary>
    Task<IReadOnlyList<SfxLibraryEntry>> ListEnabledAsync(CancellationToken ct);

    /// <summary>The enabled item with this id, or null when unknown, disabled or its key is outside the platform prefix.</summary>
    Task<SfxLibraryEntry?> FindEnabledAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// The bytes of the enabled item with this id, read from the key the TABLE holds (confined to the platform prefix and
    /// checked against the row's SHA-256), or null. Library files are tiny, so they are read whole and written into the media
    /// workspace; they never go through the project-scoped download, which keeps its project-prefix guarantee.
    /// </summary>
    Task<byte[]?> ReadBytesAsync(Guid id, CancellationToken ct);

    /// <summary>The largest library object the engine will read.</summary>
    const int MaxObjectBytes = 4 * 1024 * 1024;
}

/// <inheritdoc />
public sealed class EfSfxLibraryLookup(
    WorkflowEngineDbContext db, Amazon.S3.IAmazonS3 s3, Microsoft.Extensions.Configuration.IConfiguration configuration) : ISfxLibraryLookup
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task<byte[]?> ReadBytesAsync(Guid id, CancellationToken ct)
    {
        SfxLibraryEntry? entry = await FindEnabledAsync(id, ct);
        if (entry is null) return null;
        try
        {
            using Amazon.S3.Model.GetObjectResponse response = await s3.GetObjectAsync(
                new Amazon.S3.Model.GetObjectRequest { BucketName = _bucket, Key = entry.StorageKey }, ct);
            if (response.ContentLength > ISfxLibraryLookup.MaxObjectBytes) return null;
            using MemoryStream ms = new();
            await response.ResponseStream.CopyToAsync(ms, ct);
            byte[] bytes = ms.ToArray();
            return string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), entry.Sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
        }
        catch (Amazon.S3.AmazonS3Exception)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<SfxLibraryEntry>> ListEnabledAsync(CancellationToken ct)
    {
        List<SfxLibraryItem> rows = await db.SfxLibraryItems.AsNoTracking()
            .Where(i => i.IsEnabled)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Slug)
            .ToListAsync(ct);
        return [.. rows.Where(r => SfxLibraryKeys.IsLibraryKey(r.StorageKey)).Select(ToEntry)];
    }

    public async Task<SfxLibraryEntry?> FindEnabledAsync(Guid id, CancellationToken ct)
    {
        SfxLibraryItem? row = await db.SfxLibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.IsEnabled, ct);
        return row is not null && SfxLibraryKeys.IsLibraryKey(row.StorageKey) ? ToEntry(row) : null;
    }

    private static SfxLibraryEntry ToEntry(SfxLibraryItem r) =>
        new(r.Id, r.Slug, r.Title, r.Category, r.Tags, r.DurationMs, r.SizeBytes, r.MimeType, r.Sha256, r.StorageKey);
}

/// <summary>The library's content digest, folded into the step-cache key of a step that offers or mixes library sounds.</summary>
public static class SfxLibraryDigest
{
    /// <summary>SHA-256 over every enabled item's id, hash and tags, so a new, changed or taken-down sound misses the cache.</summary>
    public static string Of(IEnumerable<SfxLibraryEntry> entries)
    {
        StringBuilder sb = new();
        foreach (SfxLibraryEntry e in entries.OrderBy(e => e.Id))
            sb.Append(e.Id).Append('|').Append(e.Sha256).Append('|').Append(e.Category).Append('|').AppendJoin(',', e.Tags).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

/// <summary>How library sounds become <c>x{n}</c> offers.</summary>
public static class SfxLibraryOffers
{
    /// <summary>The most library sounds one analysis offers (on top of the project's own files).</summary>
    public const int MaxLibraryOffers = 60;

    /// <summary>Library candidates <c>x0..</c>, in library order: no project file, the item id, its category and tag words.</summary>
    public static IEnumerable<ReelBolt.Shared.Workflows.VideoAnalysisSfxCandidate> Candidates(IReadOnlyList<SfxLibraryEntry> library) =>
        Pick(library).Select((e, i) => new ReelBolt.Shared.Workflows.VideoAnalysisSfxCandidate(
            $"x{i}", Guid.Empty, e.Title, e.MimeType, e.SizeBytes, e.Id, e.Category, e.Tags));

    /// <summary>
    /// At most <see cref="MaxLibraryOffers"/> items. A larger library is sampled round-robin across categories (library order
    /// inside each) so every kind of sound stays on offer, then put back in library order.
    /// </summary>
    public static IReadOnlyList<SfxLibraryEntry> Pick(IReadOnlyList<SfxLibraryEntry> library)
    {
        if (library.Count <= MaxLibraryOffers) return library;
        List<Queue<SfxLibraryEntry>> groups = [.. library.GroupBy(e => e.Category, StringComparer.Ordinal).Select(g => new Queue<SfxLibraryEntry>(g))];
        HashSet<Guid> chosen = [];
        while (chosen.Count < MaxLibraryOffers)
        {
            foreach (Queue<SfxLibraryEntry> g in groups)
            {
                if (g.Count > 0 && chosen.Count < MaxLibraryOffers) chosen.Add(g.Dequeue().Id);
            }
        }

        return [.. library.Where(e => chosen.Contains(e.Id))];
    }

    /// <summary>Adds <c>category</c> and <c>tags</c> to a view node of a library sound; a project file's node is untouched.</summary>
    public static System.Text.Json.Nodes.JsonObject WithSfxLibraryWords(
        this System.Text.Json.Nodes.JsonObject node, ReelBolt.Shared.Workflows.VideoAnalysisSfxCandidate c)
    {
        if (c.LibraryItemId is null) return node;
        node["category"] = c.Category;
        node["tags"] = new System.Text.Json.Nodes.JsonArray([.. (c.Tags ?? []).Select(t => (System.Text.Json.Nodes.JsonNode)t)]);
        return node;
    }
}
