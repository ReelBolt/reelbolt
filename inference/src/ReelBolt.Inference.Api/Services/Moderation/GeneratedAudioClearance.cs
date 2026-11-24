using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Moderation;

/// <summary>
/// Decides, from stored facts only, whether a stored object is wordless audio that our own Music or Sound step made
/// from a checked prompt, and whose bytes are still the ones that step wrote. It is only asked about audio that was
/// heard in full, has one track and came back with an empty transcript (the caller owns conditions c and d).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>(a) A Completed <c>AudioGenerate</c> step result of this project that made the key: its
/// <c>OutputStorageKey</c> is the key, or its manifest (<c>ArtifactStorageKey</c>) lists the key as a take it delivered.
/// A row copied from another (<c>ReusedFromStepResultId</c>) never counts: the source does. A take the step reused or
/// carried from an earlier run is not the step's own: the step that made it counts.</description></item>
/// <item><description>(b) The manifest take carries <c>promptCheck</c> Allow or Review, written by the step at the moment the
/// prompt was judged (an outage writes nothing). The audit rows expire, so they are not read.</description></item>
/// <item><description>(e) The step records no ETag. The object's store time (<c>LastModified</c>) must be no later than the step's
/// <c>CompletedAt</c> plus <see cref="Skew"/>: a rewrite after the step is later than that. From the verdict on, the
/// ETag binds the row as for any other output. A same-size rewrite is therefore not cleared.</description></item>
/// </list>
/// Any fact that cannot be read means not cleared.
/// </remarks>
internal sealed class GeneratedAudioClearance
{
    /// <summary>
    /// Allowed distance between the step's completion time and the object's store time. The object is written before
    /// the step completes, so this only absorbs a clock difference between the store and the database and the
    /// store's one-second resolution; a rewrite inside it is not detectable, which is why it is small.
    /// </summary>
    internal static readonly TimeSpan Skew = TimeSpan.FromSeconds(5);

    private const long MaxManifestBytes = 512 * 1024;
    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ITenantContext? _tenant;
    private readonly ILogger _logger;

    public GeneratedAudioClearance(InferenceApiDbContext db, IFileStorageService storage, ITenantContext? tenant, ILogger logger)
    {
        _db = db;
        _storage = storage;
        _tenant = tenant;
        _logger = logger;
    }

    public async Task<bool> IsClearedAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            StoredObjectMetadata? meta = await _storage.GetMetadataAsync(projectId, storageKey, ct);
            if (meta?.LastModifiedUtc is not DateTime modified) return false;

            // The execution that made the object is named in its key; a take reused from an earlier run is found
            // through that run, so the key's own execution is the one that counts.
            List<Guid> executionIds = GuidPattern.Matches(storageKey)
                .Select(m => Guid.TryParse(m.Value, out Guid g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();

            List<Candidate> candidates;
            using (_tenant.System())
            {
                candidates = await _db.WorkflowStepResults
                    .AsNoTracking()
                    .Where(r => r.WorkflowExecution.ProjectId == projectId
                                && r.WorkflowStep.StepType == StepType.AudioGenerate
                                && r.Status == StepStatus.Completed
                                && r.ReusedFromStepResultId == null
                                && r.CompletedAt != null
                                && r.ArtifactStorageKey != null
                                && (r.OutputStorageKey == storageKey || executionIds.Contains(r.WorkflowExecutionId)))
                    .Select(r => new Candidate(r.ArtifactStorageKey!, r.CompletedAt!.Value))
                    .ToListAsync(ct);
            }

            foreach (Candidate candidate in candidates)
            {
                // (e) the bytes are no newer than the step.
                if (modified > DateTime.SpecifyKind(candidate.CompletedAt, DateTimeKind.Utc) + Skew) continue;
                if (await ManifestClearsAsync(projectId, candidate.ManifestKey, storageKey, ct)) return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The origin of a wordless audio output could not be read; it is not cleared");
            return false;
        }
    }

    private async Task<bool> ManifestClearsAsync(Guid projectId, string manifestKey, string storageKey, CancellationToken ct)
    {
        StoredObjectMetadata? head = await _storage.GetMetadataAsync(projectId, manifestKey, ct);
        if (head is null || head.SizeBytes > MaxManifestBytes) return false;

        await using Stream stream = await _storage.DownloadAsync(projectId, manifestKey, ct);
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxManifestBytes) return false;

        using JsonDocument doc = JsonDocument.Parse(buffer.ToArray());
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;

        // (a) our own Music or Sound step.
        string? kind = Text(root, "kind");
        if (kind is not ("Music" or "SoundEffect")) return false;
        if (!root.TryGetProperty("takes", out JsonElement takes) || takes.ValueKind != JsonValueKind.Array) return false;

        foreach (JsonElement take in takes.EnumerateArray())
        {
            if (take.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(Text(take, "storageKey"), storageKey, StringComparison.Ordinal)) continue;
            if (Text(take, "status") != "succeeded") continue;
            if (Flag(take, "reused") || Flag(take, "carried")) continue;

            // (b) a positive record that the prompt was judged.
            if (Text(take, "promptCheck") is "Allow" or "Review") return true;
        }

        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

    private sealed record Candidate(string ManifestKey, DateTime CompletedAt);
}
