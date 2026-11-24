using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

public sealed partial class AvatarLibraryService
{
    /// <summary>
    /// The storage sweep's orphan pass for the library prefix: deletes every library object older than
    /// <paramref name="minAge"/> that nothing names any more, that is an object whose item row is gone (an item delete
    /// that could not remove it, or a purged organization) or that no reference row and no signed-release key of its
    /// item names (a copy a failed publish left behind). The age keeps a publish that is still copying out of reach.
    /// Runs across every organization, so the caller holds a system tenant scope. Answers how many objects it deleted;
    /// at most <paramref name="maxDeletes"/> a call. A listing failure propagates (the caller logs and moves on).
    /// </summary>
    public async Task<int> SweepOrphanObjectsAsync(DateTime nowUtc, TimeSpan minAge, int maxDeletes, CancellationToken ct)
    {
        IReadOnlyList<StoredObjectEntry> listed = await _store.ListAllAsync(ct);
        DateTime cutoff = nowUtc - minAge;
        List<(string Key, Guid ItemId)> old = [];
        foreach (StoredObjectEntry entry in listed)
        {
            if (entry.LastModifiedUtc > cutoff) continue;
            if (AvatarLibraryStorage.Parse(entry.StorageKey) is { } parsed) old.Add((entry.StorageKey, parsed.ItemId));
        }

        if (old.Count == 0) return 0;

        Guid[] itemIds = [.. old.Select(o => o.ItemId).Distinct()];
        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (Guid[] chunk in itemIds.Chunk(500))
        {
            named.UnionWith(await _db.AvatarLibraryReferences.AsNoTracking()
                .Where(r => chunk.Contains(r.LibraryItemId))
                .Select(r => r.StorageKey)
                .ToListAsync(ct));
            named.UnionWith(await _db.AvatarLibraryItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => chunk.Contains(i.Id) && i.ConsentDocumentStorageKey != null)
                .Select(i => i.ConsentDocumentStorageKey!)
                .ToListAsync(ct));
        }

        List<string> orphans = [.. old.Where(o => !named.Contains(o.Key)).Select(o => o.Key).Take(Math.Max(1, maxDeletes))];
        if (orphans.Count == 0) return 0;

        (int deleted, int failed) = await DeleteLibraryObjectsAsync(orphans);
        _logger.LogInformation("Avatar library orphan pass removed {Deleted} object(s) nothing names ({Failed} failed)", deleted, failed);
        return deleted;
    }
}
