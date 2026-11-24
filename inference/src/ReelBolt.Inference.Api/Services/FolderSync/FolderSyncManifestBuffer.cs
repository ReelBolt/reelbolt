using System.Collections.Concurrent;

namespace ReelBolt.Inference.Api.Services.FolderSync;

/// <summary>What adding a manifest batch did.</summary>
public enum ManifestBatchOutcome
{
    Accepted,
    TooLarge,
    Invalid
}

/// <summary>
/// Collects the batches of a manifest until its final batch arrives (docs/design/local-folder-sync-and-storage.md,
/// "Manifest reconciliation"). Keyed by (link, manifestId); batches are 0-based, may arrive in any order and
/// may be re-sent (the same batch number replaces itself). A pending manifest expires after
/// <see cref="Lifetime"/>. In memory and per API process — a manifest whose batches land on two replicas
/// is answered <c>manifest_incomplete</c>, and the desktop app starts a new one.
/// </summary>
public sealed class FolderSyncManifestBuffer(TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public const int MaxFilesPerBatch = 2_000;

    public const int MaxFilesPerManifest = 200_000;

    public const int MaxManifestIdLength = 100;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid LinkId, string ManifestId), Pending> _pending = new();

    private sealed class Pending
    {
        public readonly Dictionary<int, IReadOnlyList<ManifestFileEntry>> Batches = [];
        public DateTimeOffset ExpiresAt;
    }

    /// <summary>Stores one batch. <paramref name="totalFiles"/> is the manifest's running file count.</summary>
    public ManifestBatchOutcome Add(Guid linkId, string manifestId, int batch, IReadOnlyList<ManifestFileEntry> files, out int totalFiles)
    {
        totalFiles = 0;
        if (batch < 0 || files.Count > MaxFilesPerBatch || string.IsNullOrWhiteSpace(manifestId) || manifestId.Length > MaxManifestIdLength)
            return ManifestBatchOutcome.Invalid;

        DateTimeOffset now = _clock.GetUtcNow();
        Prune(now);

        Pending pending = _pending.GetOrAdd((linkId, manifestId), _ => new Pending { ExpiresAt = now + Lifetime });
        lock (pending)
        {
            pending.ExpiresAt = now + Lifetime;
            int without = pending.Batches.Where(b => b.Key != batch).Sum(b => b.Value.Count);
            if (without + files.Count > MaxFilesPerManifest)
            {
                _pending.TryRemove((linkId, manifestId), out _);
                return ManifestBatchOutcome.TooLarge;
            }

            pending.Batches[batch] = files;
            totalFiles = without + files.Count;
            return ManifestBatchOutcome.Accepted;
        }
    }

    /// <summary>
    /// Takes the complete manifest after its final batch (<paramref name="finalBatch"/>) was added: every
    /// batch 0..final present and none beyond it. Null when incomplete; the pending manifest is dropped
    /// either way.
    /// </summary>
    public IReadOnlyList<ManifestFileEntry>? Take(Guid linkId, string manifestId, int finalBatch)
    {
        if (!_pending.TryRemove((linkId, manifestId), out Pending? pending)) return null;
        lock (pending)
        {
            if (pending.Batches.Keys.Any(k => k > finalBatch)) return null;
            List<ManifestFileEntry> all = [];
            for (int i = 0; i <= finalBatch; i++)
            {
                if (!pending.Batches.TryGetValue(i, out IReadOnlyList<ManifestFileEntry>? files)) return null;
                all.AddRange(files);
            }

            return all;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<(Guid LinkId, string ManifestId), Pending> entry in _pending)
        {
            if (entry.Value.ExpiresAt <= now) _pending.TryRemove(entry.Key, out _);
        }
    }
}
