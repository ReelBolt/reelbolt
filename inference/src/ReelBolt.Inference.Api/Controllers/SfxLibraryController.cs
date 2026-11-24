using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>One library sound as the API shows it: no storage key, no hash, nothing server-side.</summary>
public sealed record SfxLibraryItemDto(
    Guid Id, string Slug, string Title, string Category, IReadOnlyList<string> Tags, int DurationMs, long SizeBytes,
    string MimeType, string Licence, string? Author, string? SourceUrl);

public sealed record SfxLibraryCategoryCount(string Category, int Count);

public sealed record SfxLibraryListResponse(
    IReadOnlyList<SfxLibraryItemDto> Items, int Total, int Page, int PageSize, IReadOnlyList<SfxLibraryCategoryCount> Categories);

/// <summary>
/// The built-in sound-effects library (docs/sfx-library.md): platform content, the same for every project and free of
/// charge (no usage event, no credit, no quota). Any signed-in member may browse and preview. A disabled, missing or
/// unreadable item is the same empty 404; nothing here returns an object-store key.
/// </summary>
[ApiController]
[Route("api/v1/sfx-library")]
[Authorize]
public class SfxLibraryController(InferenceApiDbContext db, ISfxLibraryStorage storage) : ControllerBase
{
    public const int MaxPageSize = 100;

    [HttpGet]
    public async Task<ActionResult<SfxLibraryListResponse>> List(
        [FromQuery] string? query, [FromQuery] string? category, [FromQuery] string? tag,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] int? maxDurationMs = null, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // The whole library is a few dozen to a few hundred small rows: filter in memory so the same code serves any provider.
        List<SfxLibraryItem> all = await db.SfxLibraryItems.AsNoTracking()
            .Where(i => i.IsEnabled)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Slug)
            .ToListAsync(ct);

        IReadOnlyList<SfxLibraryCategoryCount> categories = all
            .GroupBy(i => i.Category, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SfxLibraryCategoryCount(g.Key, g.Count()))
            .ToList();

        IEnumerable<SfxLibraryItem> filtered = all;
        if (!string.IsNullOrWhiteSpace(category))
            filtered = filtered.Where(i => string.Equals(i.Category, category.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tag))
            filtered = filtered.Where(i => i.Tags.Contains(tag.Trim().ToLowerInvariant(), StringComparer.Ordinal));
        if (maxDurationMs is { } maxMs)
            filtered = filtered.Where(i => i.DurationMs <= maxMs);
        if (!string.IsNullOrWhiteSpace(query))
        {
            string q = query.Trim();
            filtered = filtered.Where(i =>
                i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Slug.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        List<SfxLibraryItem> matches = filtered.ToList();
        List<SfxLibraryItemDto> items = matches
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(i => new SfxLibraryItemDto(
                i.Id, i.Slug, i.Title, i.Category, i.Tags, i.DurationMs, i.SizeBytes, i.MimeType, i.Licence, i.Author, i.SourceUrl))
            .ToList();
        return new SfxLibraryListResponse(items, matches.Count, page, pageSize, categories);
    }

    /// <summary>The sound's bytes, inline and Range-aware, under the shared content policy.</summary>
    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct)
    {
        SfxLibraryItem? item = await db.SfxLibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.IsEnabled, ct);
        if (item is null || !SfxLibraryKeys.IsLibraryKey(item.StorageKey)) return NotFound();

        string fileName = item.Slug + Path.GetExtension(item.StorageKey);
        ServedContent served = SafeContentPolicy.Decide(item.MimeType, fileName);
        if (!served.Inline) return NotFound();

        byte[]? bytes = await storage.GetAsync(item.StorageKey, ct);
        if (bytes is null) return NotFound();

        SafeContentPolicy.ApplySecurityHeaders(Response);
        Response.Headers["Content-Disposition"] = SafeContentPolicy.BuildContentDisposition(inline: true, fileName);
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(bytes, served.ContentType, enableRangeProcessing: true);
    }
}
