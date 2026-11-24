using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Interchange;

namespace ReelBolt.Inference.Api.Services.Editing;

/// <summary>
/// One media source of a hand-off package. The storage key is server-side only: it is never written to the package, a
/// manifest or a header.
/// </summary>
/// <param name="StorageKey">Null when the reference does not resolve inside the timeline's own project (listed as missing).</param>
public sealed record HandOffSource(
    AssetRef Reference,
    string DisplayName,
    string SafeName,
    string? StorageKey,
    string MimeType,
    long? SizeBytes,
    string? Sha256,
    double? DurationSec,
    IReadOnlyList<string> UsedBy)
{
    public bool Resolvable => !string.IsNullOrEmpty(StorageKey);
}

/// <summary>The distinct sources of a document with their names, plus the totals the cap is judged on.</summary>
public sealed record HandOffPlan(IReadOnlyList<HandOffSource> Sources)
{
    public int MediaFiles => Sources.Count(s => s.Resolvable);

    public long MediaBytes => Sources.Where(s => s.Resolvable).Sum(s => s.SizeBytes ?? 0);

    /// <summary>The names the interchange writers use, from the SAME assignment the zip entries use.</summary>
    public HandOffMediaNames Names() => new(Sources.ToDictionary(
        s => s.Reference,
        s => new HandOffMediaEntry(s.SafeName, s.SizeBytes, s.DurationSec)));
}

/// <summary>
/// R12.2: resolves the media a timeline references into hand-off sources. Every reference is looked up INSIDE the
/// timeline's own project (a file or step result of another project simply does not resolve and is reported missing),
/// so the trust boundary is the same as render and seed.
/// </summary>
public static class HandOffPlanner
{
    public static async Task<HandOffPlan> PlanAsync(
        InferenceApiDbContext db, IFileStorageService storage, Guid projectId, EditTimeline document, int maxFiles, CancellationToken ct)
    {
        List<AssetRef> references = TimelineAssetReferences.MediaSources(document).Where(r => r is not null).Distinct().ToList();
        List<Guid> fileIds = references.Where(r => r.ProjectFileId is not null).Select(r => r.ProjectFileId!.Value).ToList();
        List<Guid> stepIds = references.Where(r => r.StepResultId is not null).Select(r => r.StepResultId!.Value).ToList();

        var files = fileIds.Count == 0
            ? []
            : await db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id)
                    && f.CsamStatus != CsamScanStatus.Quarantined
                    && f.ModerationStatus != FileModerationStatus.Quarantined
                    && !f.StorageKey.Contains("/quarantine/"))
                .Select(f => new { f.Id, f.OriginalFileName, f.StorageKey, f.MimeType, f.SizeBytes, f.StorageMetadataJson, f.UploadedAt, f.OutputProducedAt })
                .ToListAsync(ct);
        var results = stepIds.Count == 0
            ? []
            : await db.WorkflowStepResults.AsNoTracking()
                .Where(r => stepIds.Contains(r.Id) && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null)
                .Select(r => new { r.Id, r.OutputStorageKey })
                .ToListAsync(ct);

        // Same rule as MediaController: a step result under quarantine/, or one a quarantined project file row names, is absent.
        if (results.Count > 0)
        {
            results = results.Where(r => !r.OutputStorageKey!.Contains("/quarantine/", StringComparison.Ordinal)).ToList();
            List<string> stepKeys = results.Select(r => r.OutputStorageKey!).ToList();
            List<string> blocked = stepKeys.Count == 0 ? [] : await db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == projectId
                    && (stepKeys.Contains(f.StorageKey) || (f.CsamQuarantineKey != null && stepKeys.Contains(f.CsamQuarantineKey)))
                    && (f.CsamStatus == CsamScanStatus.Quarantined || f.ModerationStatus == FileModerationStatus.Quarantined || f.CsamQuarantineKey != null))
                .Select(f => f.CsamQuarantineKey != null && stepKeys.Contains(f.CsamQuarantineKey) ? f.CsamQuarantineKey : f.StorageKey)
                .ToListAsync(ct);
            if (blocked.Count > 0) results = results.Where(r => !blocked.Contains(r.OutputStorageKey!)).ToList();
        }

        // A source whose bytes are not served is left out like a missing one: the package lists it as not included.
        HashSet<Guid> heldResults = await Outputs.OutputGate.HeldResultIdsAsync(db, projectId, results.Select(r => r.Id).ToList(), ct);
        if (heldResults.Count > 0) results = results.Where(r => !heldResults.Contains(r.Id)).ToList();
        HashSet<Guid> heldFiles = await Outputs.OutputGate.HeldFileIdsAsync(
            db, projectId,
            files.Select(f => (f.Id, f.StorageKey, f.UploadedAt, f.OutputProducedAt)), ct);
        if (heldFiles.Count > 0) files = files.Where(f => !heldFiles.Contains(f.Id)).ToList();
        List<string> keys = files.Select(f => f.StorageKey).Concat(results.Select(r => r.OutputStorageKey!)).Where(k => !string.IsNullOrEmpty(k)).ToList();
        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, projectId, keys, ct);

        Dictionary<AssetRef, List<string>> usedBy = [];
        foreach (Item item in document.Tracks.SelectMany(t => t.Items))
        {
            IEnumerable<AssetRef> refs = item switch
            {
                MediaClip clip => [clip.AssetRef],
                InsertClip { AssetRef: { } insert } => [insert],
                MulticamClip multicam => (multicam.Angles ?? []).Where(a => a?.AssetRef is not null).Select(a => a!.AssetRef!),
                _ => []
            };
            foreach (AssetRef reference in refs)
            {
                if (!usedBy.TryGetValue(reference, out List<string>? names)) usedBy[reference] = names = [];
                string label = string.IsNullOrWhiteSpace(item.Name) ? "Clip" : item.Name.Trim();
                if (!names.Contains(label, StringComparer.Ordinal)) names.Add(label);
            }
        }

        Dictionary<Guid, (string Name, string Key, string Mime, long Size, string? Sha)> fileById = files.ToDictionary(
            f => f.Id, f => (f.OriginalFileName, f.StorageKey, f.MimeType, f.SizeBytes, ReadSha256(f.StorageMetadataJson)));
        Dictionary<Guid, string> keyByStep = results.ToDictionary(r => r.Id, r => r.OutputStorageKey!);

        // Names first (a missing source still gets one, so the timeline file's reference matches the manifest).
        List<(AssetRef, string?)> naming = [];
        foreach (AssetRef reference in references)
        {
            string? display = reference.ProjectFileId is { } fid && fileById.TryGetValue(fid, out var f) ? f.Name
                : reference.StepResultId is { } sid && keyByStep.TryGetValue(sid, out string? key) ? LeafOf(key)
                : usedBy.TryGetValue(reference, out List<string>? u) ? u.FirstOrDefault() : null;
            naming.Add((reference, display));
        }

        IReadOnlyDictionary<AssetRef, string> safeNames = HandOffNaming.Assign(naming);
        bool measureStepResults = references.Count <= Math.Max(1, maxFiles);
        List<HandOffSource> sources = [];
        foreach ((AssetRef reference, string? display) in naming)
        {
            string? key = null;
            string mime = "application/octet-stream";
            long? size = null;
            string? sha = null;
            if (reference.ProjectFileId is { } fileId && fileById.TryGetValue(fileId, out var file) && !string.IsNullOrEmpty(file.Key))
            {
                (key, mime, size, sha) = (file.Key, file.Mime, file.Size, file.Sha);
            }
            else if (reference.StepResultId is { } stepId && keyByStep.TryGetValue(stepId, out string? stepKey))
            {
                key = stepKey;
                mime = MimeFromName(LeafOf(stepKey));
                if (measureStepResults)
                {
                    StoredObjectMetadata? meta = null;
                    try { meta = await storage.GetMetadataAsync(projectId, stepKey, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { meta = null; }
                    if (meta is null) key = null; // the object is gone: listed as missing
                    else size = meta.SizeBytes;
                }
            }

            double? duration = key is not null && probes.TryGetValue(key, out MediaSourceProbe? probe) && probe.DurationSec > 0 ? probe.DurationSec : null;
            sources.Add(new HandOffSource(
                reference, display ?? "Missing media", safeNames[reference], key, mime, size, sha, duration,
                usedBy.TryGetValue(reference, out List<string>? clips) ? clips : []));
        }

        return new HandOffPlan(sources);
    }

    internal static string LeafOf(string storageKey)
    {
        int slash = storageKey.LastIndexOf('/');
        return slash >= 0 ? storageKey[(slash + 1)..] : storageKey;
    }

    internal static string MimeFromName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };

    /// <summary>A SHA-256 only where one is already stored with the file (never computed on the request path).</summary>
    private static string? ReadSha256(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("sha256", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.String) continue;
                string value = property.Value.GetString() ?? "";
                return value.Length == 64 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
            }
        }
        catch (JsonException)
        {
            // an unreadable metadata blob is "not known"
        }

        return null;
    }
}
