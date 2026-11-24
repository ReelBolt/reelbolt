using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems.Diff;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Layer-2 reads for project versions: manifest changes, per-item diffs and a file item's bytes
/// (contract section 7 and the last row of section 5). Shares the capture service's context, storage
/// and clock, and uses <see cref="IProjectHistoryService.BuildLiveManifestAsync"/> for "compare with now".
/// </summary>
public sealed partial class ProjectHistoryService : IProjectHistoryDiffService
{
    /// <summary>The most changes a <c>changes</c> response lists (contract section 7).</summary>
    public const int MaxChanges = 5000;

    private const int FileReadChunkSize = 64 * 1024;

    /// <inheritdoc />
    public async Task<ProjectHistoryChanges?> ComputeChangesAsync(
        Guid projectId,
        Guid fromVersionId,
        Guid? toVersionId,
        CancellationToken ct)
    {
        ProjectVersion? fromVersion = await LoadVersionWithItemsAsync(projectId, fromVersionId, ct);
        if (fromVersion is null)
            return null;

        DiffTarget? target = await ResolveTargetAsync(projectId, toVersionId, ct);
        if (target is null)
            return null;

        Dictionary<(ProjectVersionItemKind Kind, string Key), DiffSide> fromItems = Index(fromVersion);
        Dictionary<Guid, ProjectFile> liveFiles = await LoadLiveFilesAsync(
            projectId,
            FileIds(fromVersion.Items.Select(i => (i.Kind, i.ItemKey)))
                .Concat(FileIds(target.Items.Keys))
                .Distinct()
                .ToArray(),
            ct);

        List<ProjectHistoryItemChange> changes = new();
        int unchanged = 0;

        foreach (KeyValuePair<(ProjectVersionItemKind Kind, string Key), DiffSide> pair in fromItems)
        {
            if (!target.Items.TryGetValue(pair.Key, out DiffSide? to))
            {
                changes.Add(new ProjectHistoryItemChange(
                    pair.Key.Kind,
                    pair.Key.Key,
                    pair.Value.Name,
                    null,
                    ProjectHistoryChangeKinds.Removed));
                continue;
            }

            bool contentChanged = FileContentChanged(pair.Value, to, liveFiles);
            bool moved = !string.Equals(pair.Value.Name, to.Name, StringComparison.Ordinal);

            if (!contentChanged && !moved)
            {
                unchanged++;
                continue;
            }

            string change = contentChanged
                ? moved ? ProjectHistoryChangeKinds.ChangedAndMoved : ProjectHistoryChangeKinds.Changed
                : ProjectHistoryChangeKinds.Moved;

            changes.Add(new ProjectHistoryItemChange(
                pair.Key.Kind,
                pair.Key.Key,
                to.Name ?? pair.Value.Name,
                moved ? pair.Value.Name : null,
                change));
        }

        foreach (KeyValuePair<(ProjectVersionItemKind Kind, string Key), DiffSide> pair in target.Items)
        {
            if (fromItems.ContainsKey(pair.Key))
                continue;

            changes.Add(new ProjectHistoryItemChange(
                pair.Key.Kind,
                pair.Key.Key,
                pair.Value.Name,
                null,
                ProjectHistoryChangeKinds.Added));
        }

        changes.Sort(static (left, right) =>
        {
            int byKind = left.Kind.CompareTo(right.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(left.Key, right.Key);
        });

        bool truncated = changes.Count > MaxChanges;
        if (truncated)
            changes = changes.Take(MaxChanges).ToList();

        return new ProjectHistoryChanges(changes, unchanged, truncated);
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryItemDiff?> ComputeItemDiffAsync(
        Guid projectId,
        Guid fromVersionId,
        ProjectVersionItemKind kind,
        string key,
        Guid? toVersionId,
        CancellationToken ct)
    {
        ProjectVersion? fromVersion = await LoadVersionWithItemsAsync(projectId, fromVersionId, ct);
        if (fromVersion is null)
            return null;

        ProjectVersionItem? fromItem = fromVersion.Items
            .FirstOrDefault(i => i.Kind == kind && string.Equals(i.ItemKey, key, StringComparison.Ordinal));
        if (fromItem is null)
            return null;

        DiffTarget? target = await ResolveTargetAsync(projectId, toVersionId, ct);
        if (target is null)
            return null;

        target.Items.TryGetValue((kind, key), out DiffSide? to);
        DiffSide from = ToSide(fromItem);
        Project? project = await LoadProjectAsync(projectId, ct);
        ProjectHistoryContentGuard guard = new(_db, projectId);

        return kind switch
        {
            ProjectVersionItemKind.File => await BuildFileDiffAsync(
                projectId, project, fromVersion.CreatedAt, from, to, target.CreatedAt, guard, ct),
            ProjectVersionItemKind.Workflow => await BuildWorkflowDiffAsync(projectId, from, to, ct),
            ProjectVersionItemKind.Timeline => await BuildTimelineDiffAsync(projectId, from, to, ct),
            ProjectVersionItemKind.DesignSystem => await BuildDesignSystemDiffAsync(from, to, ct),
            ProjectVersionItemKind.Pronunciation => await BuildPronunciationDiffAsync(projectId, from, to, ct),
            ProjectVersionItemKind.Project => await BuildProjectDiffAsync(projectId, from, to, ct),
            _ => null
        };
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryItemContent?> GetFileContentAsync(
        Guid projectId,
        Guid fromVersionId,
        ProjectVersionItemKind kind,
        string key,
        CancellationToken ct)
    {
        if (kind != ProjectVersionItemKind.File)
            return null;

        ProjectVersion? version = await LoadVersionWithItemsAsync(projectId, fromVersionId, ct);
        if (version is null)
            return null;

        ProjectVersionItem? item = version.Items
            .FirstOrDefault(i => i.Kind == ProjectVersionItemKind.File
                                 && string.Equals(i.ItemKey, key, StringComparison.Ordinal));
        if (item is null || item.ErasedAt is not null)
            return null;

        Project? project = await LoadProjectAsync(projectId, ct);
        if (project is null)
            return null;

        DiffSide side = ToSide(item);
        ProjectFile? live = await LoadLiveFileAsync(projectId, side.Key, ct);

        // The output hold and the quarantine markers decide before any bytes are read, so a held output
        // cannot leak through its historical copy either.
        ProjectHistoryContentGuard guard = new(_db, projectId);
        if (await guard.RefusalAsync(
                project, version.CreatedAt, live, ct,
                platformMade: item.PlatformMade, outputProducedAt: item.OutputProducedAt) is not null)
            return null;

        if (side.ContentId is not null)
        {
            ProjectHistoryBlob? blob = await _db.ProjectHistoryBlobs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.ProjectId == projectId && b.ContentId == side.ContentId,
                    ct);

            if (blob is not null)
            {
                Stream? protectedCopy = await TryOpenAsync(projectId, blob.StorageKey, ct);
                return protectedCopy is null
                    ? null
                    : new ProjectHistoryItemContent(
                        protectedCopy, side.MimeType, DisplayFileName(side.Name), side.SizeBytes);
            }
        }

        if (ContentId.IsUnverified(side.ContentId))
            return null;

        if (live is null || IsQuarantined(live))
            return null;

        if (!ProjectHistoryStorageKeys.IsProtectedLiveKey(projectId, live.StorageKey)
            || !await LiveMatchesAsync(projectId, live, side, ct))
        {
            return null;
        }

        Stream? stream = await TryOpenAsync(projectId, live.StorageKey, ct);
        string? liveName = string.IsNullOrWhiteSpace(live.OriginalFileName)
            ? side.Name
            : live.OriginalFileName;
        return stream is null
            ? null
            : new ProjectHistoryItemContent(
                stream,
                side.MimeType,
                DisplayFileName(liveName),
                side.SizeBytes);
    }

    // ── file diff ──────────────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildFileDiffAsync(
        Guid projectId,
        Project? project,
        DateTime fromVersionCreatedAt,
        DiffSide from,
        DiffSide? to,
        DateTime? toVersionCreatedAt,
        ProjectHistoryContentGuard guard,
        CancellationToken ct)
    {
        long fromSize = from.SizeBytes;
        long toSize = to?.SizeBytes ?? 0;

        ProjectFile? fromLive = await LoadLiveFileAsync(projectId, from.Key, ct);
        if (await guard.RefusalAsync(
                project, fromVersionCreatedAt, fromLive, ct,
                platformMade: from.PlatformMade, outputProducedAt: from.OutputProducedAt) is not null)
            return FileDiff(from, ProjectHistoryDiffModes.Unavailable, fromSize, toSize);

        ProjectFile? toLive = null;
        if (to is not null)
        {
            toLive = await LoadLiveFileAsync(projectId, to.Key, ct);
            if (await guard.RefusalAsync(
                    project, toVersionCreatedAt, toLive, ct,
                    platformMade: to.PlatformMade, outputProducedAt: to.OutputProducedAt) is not null)
                return FileDiff(from, ProjectHistoryDiffModes.Unavailable, fromSize, toSize);
        }

        bool fromText = ProjectFileTextPolicy.IsLikelyText(from.MimeType, from.Name);
        bool toText = to is null || ProjectFileTextPolicy.IsLikelyText(to.MimeType, to.Name);

        if (!fromText || !toText)
            return FileDiff(from, ProjectHistoryDiffModes.Binary, fromSize, toSize);

        if (fromSize > LineDiff.MaxBytesPerSide || toSize > LineDiff.MaxBytesPerSide)
            return FileDiff(from, ProjectHistoryDiffModes.TooLarge, fromSize, toSize);

        (byte[]? fromBytes, bool fromTooLarge) = await ReadFileBytesAsync(projectId, fromLive, from, ct);
        if (fromTooLarge)
            return FileDiff(from, ProjectHistoryDiffModes.TooLarge, fromSize, toSize);

        byte[]? toBytes;
        bool toTooLarge = false;
        if (to is null)
        {
            toBytes = [];
        }
        else
        {
            (toBytes, toTooLarge) = await ReadFileBytesAsync(projectId, toLive, to, ct);
        }

        if (toTooLarge)
            return FileDiff(from, ProjectHistoryDiffModes.TooLarge, fromSize, toSize);

        if (fromBytes is null || toBytes is null)
            return FileDiff(from, ProjectHistoryDiffModes.Binary, fromSize, toSize);

        if (fromBytes.Length > LineDiff.MaxBytesPerSide || toBytes.Length > LineDiff.MaxBytesPerSide)
            return FileDiff(from, ProjectHistoryDiffModes.TooLarge, fromSize, toSize);

        string? fromTextContent = LineDiff.DecodeUtf8(fromBytes);
        string? toTextContent = LineDiff.DecodeUtf8(toBytes);
        if (fromTextContent is null || toTextContent is null)
            return FileDiff(from, ProjectHistoryDiffModes.Binary, fromSize, toSize);

        LineDiffResult diff = LineDiff.Compute(fromTextContent, toTextContent, ct);
        if (diff.TooLarge)
            return FileDiff(from, ProjectHistoryDiffModes.TooLarge, fromSize, toSize);

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.File,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.Text,
            Hunks: diff.Hunks,
            FromSize: fromSize,
            ToSize: toSize);
    }

    private static ProjectHistoryItemDiff FileDiff(DiffSide from, string mode, long fromSize, long toSize) =>
        new(
            ProjectVersionItemKind.File,
            from.Key,
            from.Name,
            mode,
            FromSize: fromSize,
            ToSize: toSize);

    /// <summary>
    /// The item's bytes: the protected blob first, else the live object only after its ETag proves it is
    /// still the captured object. The caller has already passed the item through
    /// <see cref="ProjectHistoryContentGuard"/>, so a held output never reaches this read. Never a
    /// quarantined row or a key outside userFiles/agentFiles.
    /// </summary>
    private async Task<(byte[]? Bytes, bool TooLarge)> ReadFileBytesAsync(
        Guid projectId,
        ProjectFile? live,
        DiffSide item,
        CancellationToken ct)
    {
        if (item.ErasedAt is not null)
            return (null, false);

        if (item.ContentId is not null)
        {
            ProjectHistoryBlob? blob = await _db.ProjectHistoryBlobs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    b => b.ProjectId == projectId && b.ContentId == item.ContentId,
                    ct);

            if (blob is not null)
                return await ReadFromStoreAsync(projectId, blob.StorageKey, ct);
        }

        if (ContentId.IsUnverified(item.ContentId))
            return (null, false);

        if (live is null || IsQuarantined(live))
            return (null, false);

        if (!ProjectHistoryStorageKeys.IsProtectedLiveKey(projectId, live.StorageKey)
            || !await LiveMatchesAsync(projectId, live, item, ct))
        {
            return (null, false);
        }

        return await ReadFromStoreAsync(projectId, live.StorageKey, ct);
    }

    private async Task<(byte[]? Bytes, bool TooLarge)> ReadFromStoreAsync(
        Guid projectId,
        string storageKey,
        CancellationToken ct)
    {
        Stream? stream = await TryOpenAsync(projectId, storageKey, ct);
        if (stream is null)
            return (null, false);

        await using (stream)
        {
            using MemoryStream buffer = new();
            byte[] chunk = new byte[FileReadChunkSize];
            while (true)
            {
                int read = await stream.ReadAsync(chunk, ct);
                if (read == 0)
                    break;

                if (buffer.Length + read > LineDiff.MaxBytesPerSide)
                    return (null, true);

                buffer.Write(chunk, 0, read);
            }

            return (buffer.ToArray(), false);
        }
    }

    private async Task<Stream?> TryOpenAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        try
        {
            return await _storage.DownloadAsync(projectId, storageKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read project-history content at {StorageKey}.", storageKey);
            return null;
        }
    }

    // ── workflow diff ───────────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildWorkflowDiffAsync(
        Guid projectId,
        DiffSide from,
        DiffSide? to,
        CancellationToken ct)
    {
        JsonObject before = ParseObject(await LoadDocumentJsonAsync(projectId, from, ct)) ?? new JsonObject();
        JsonObject after = to is null
            ? new JsonObject()
            : ParseObject(await LoadDocumentJsonAsync(projectId, to, ct)) ?? new JsonObject();

        Dictionary<int, JsonObject> beforeSteps = Steps(before);
        Dictionary<int, JsonObject> afterSteps = Steps(after);

        List<ProjectHistoryWorkflowStep> added = new();
        foreach (KeyValuePair<int, JsonObject> step in afterSteps.OrderBy(p => p.Key))
        {
            if (!beforeSteps.ContainsKey(step.Key))
                added.Add(StepIdentity(step.Key, step.Value));
        }

        List<ProjectHistoryWorkflowStep> removed = new();
        foreach (KeyValuePair<int, JsonObject> step in beforeSteps.OrderBy(p => p.Key))
        {
            if (!afterSteps.ContainsKey(step.Key))
                removed.Add(StepIdentity(step.Key, step.Value));
        }

        List<ProjectHistoryWorkflowStepChange> changed = new();
        foreach (KeyValuePair<int, JsonObject> step in beforeSteps.OrderBy(p => p.Key))
        {
            if (!afterSteps.TryGetValue(step.Key, out JsonObject? candidate))
                continue;

            List<string> fields = ChangedFields(step.Value, candidate);
            if (fields.Count == 0)
                continue;

            ProjectHistoryWorkflowStep identity = StepIdentity(step.Key, candidate);
            changed.Add(new ProjectHistoryWorkflowStepChange(
                identity.Order, identity.Type, identity.AgentName, fields));
        }

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.Workflow,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.Workflow,
            Workflow: new ProjectHistoryWorkflowDiff(added, removed, changed));
    }

    private static Dictionary<int, JsonObject> Steps(JsonObject document)
    {
        Dictionary<int, JsonObject> steps = new();
        if (document["steps"] is not JsonArray array)
            return steps;

        foreach (JsonNode? node in array)
        {
            if (node is JsonObject step && step["order"] is { } orderNode && TryInt(orderNode, out int order))
                steps[order] = step;
        }

        return steps;
    }

    private static ProjectHistoryWorkflowStep StepIdentity(int order, JsonObject step) =>
        new(order, StringValue(step, "stepType"), StringValue(step, "agentName"));

    /// <summary>Top-level step properties that differ, excluding the identity <c>order</c>.</summary>
    private static List<string> ChangedFields(JsonObject before, JsonObject after)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> pair in before)
            names.Add(pair.Key);
        foreach (KeyValuePair<string, JsonNode?> pair in after)
            names.Add(pair.Key);

        List<string> changed = new();
        foreach (string name in names)
        {
            if (string.Equals(name, "order", StringComparison.Ordinal))
                continue;

            JsonNode? beforeValue = before.TryGetPropertyValue(name, out JsonNode? left) ? left : null;
            JsonNode? afterValue = after.TryGetPropertyValue(name, out JsonNode? right) ? right : null;
            if (!JsonNode.DeepEquals(beforeValue, afterValue))
                changed.Add(name);
        }

        return changed;
    }

    // ── timeline diff ───────────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildTimelineDiffAsync(
        Guid projectId,
        DiffSide from,
        DiffSide? to,
        CancellationToken ct)
    {
        JsonObject before = ParseObject(await LoadDocumentJsonAsync(projectId, from, ct)) ?? new JsonObject();
        JsonObject after = to is null
            ? new JsonObject()
            : ParseObject(await LoadDocumentJsonAsync(projectId, to, ct)) ?? new JsonObject();

        int fromRevision = from.TimelineRevision ?? IntValue(before, "revision");
        int toRevision = to?.TimelineRevision ?? IntValue(after, "revision");

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.Timeline,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.Timeline,
            Timeline: new ProjectHistoryTimelineDiff(
                fromRevision,
                toRevision,
                TimelineSummary(before["document"] as JsonObject),
                TimelineSummary(after["document"] as JsonObject)));
    }

    /// <summary>Tracks, clip items and the furthest record-out in frames, read from the timeline document.</summary>
    private static ProjectHistoryTimelineSummary TimelineSummary(JsonObject? document)
    {
        if (document is null || document["tracks"] is not JsonArray tracks)
            return new ProjectHistoryTimelineSummary(0, 0, 0);

        int clips = 0;
        long duration = 0;
        foreach (JsonNode? trackNode in tracks)
        {
            if (trackNode is not JsonObject track || track["items"] is not JsonArray items)
                continue;

            foreach (JsonNode? itemNode in items)
            {
                if (itemNode is not JsonObject item)
                    continue;

                if (StringValue(item, "type") is { } type
                    && type.EndsWith("Clip", StringComparison.Ordinal))
                {
                    clips++;
                }

                duration = Math.Max(duration, LongValue(item, "recordOut"));
            }
        }

        if (document["inOut"] is JsonObject inOut)
            duration = Math.Max(duration, LongValue(inOut, "outFrame"));

        return new ProjectHistoryTimelineSummary(tracks.Count, clips, duration);
    }

    // ── design-system diff ──────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildDesignSystemDiffAsync(
        DiffSide from,
        DiffSide? to,
        CancellationToken ct)
    {
        JsonObject? before = await LoadDesignDocumentAsync(from.DesignSystemVersionId, ct);
        JsonObject? after = to is null ? null : await LoadDesignDocumentAsync(to.DesignSystemVersionId, ct);

        DesignDiff diff = DesignDocumentDiffer.Diff(before, after ?? new JsonObject(), ct);

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.DesignSystem,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.DesignSystem,
            DesignSystem: diff.ToJson());
    }

    private async Task<JsonObject?> LoadDesignDocumentAsync(Guid? versionId, CancellationToken ct)
    {
        if (versionId is not { } id)
            return null;

        string? json = await _db.DesignSystemVersions
            .AsNoTracking()
            .Where(v => v.Id == id)
            .Select(v => v.DocumentJson)
            .FirstOrDefaultAsync(ct);

        return ParseObject(json);
    }

    // ── pronunciation diff ──────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildPronunciationDiffAsync(
        Guid projectId,
        DiffSide from,
        DiffSide? to,
        CancellationToken ct)
    {
        Dictionary<string, PronunciationEntry> before = PronunciationEntries(
            ParseArray(await LoadDocumentJsonAsync(projectId, from, ct)));
        Dictionary<string, PronunciationEntry> after = to is null
            ? new Dictionary<string, PronunciationEntry>(StringComparer.Ordinal)
            : PronunciationEntries(ParseArray(await LoadDocumentJsonAsync(projectId, to, ct)));

        List<string> added = after.Keys
            .Where(term => !before.ContainsKey(term))
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToList();
        List<string> removed = before.Keys
            .Where(term => !after.ContainsKey(term))
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToList();
        List<string> changed = before.Keys
            .Where(term => after.TryGetValue(term, out PronunciationEntry candidate)
                           && !candidate.Equals(before[term]))
            .OrderBy(term => term, StringComparer.Ordinal)
            .ToList();

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.Pronunciation,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.Pronunciation,
            Pronunciation: new ProjectHistoryPronunciationDiff(added, removed, changed));
    }

    private static Dictionary<string, PronunciationEntry> PronunciationEntries(JsonArray? array)
    {
        Dictionary<string, PronunciationEntry> entries = new(StringComparer.Ordinal);
        if (array is null)
            return entries;

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject entry)
                continue;

            string? term = StringValue(entry, "term");
            if (term is null)
                continue;

            entries[term] = new PronunciationEntry(
                StringValue(entry, "spelling"),
                BoolValue(entry, "caseSensitive"));
        }

        return entries;
    }

    private readonly record struct PronunciationEntry(string? Spelling, bool CaseSensitive);

    // ── project diff ───────────────────────────────────────────────────────────────────────────

    private async Task<ProjectHistoryItemDiff> BuildProjectDiffAsync(
        Guid projectId,
        DiffSide from,
        DiffSide? to,
        CancellationToken ct)
    {
        JsonObject before = ParseObject(await LoadDocumentJsonAsync(projectId, from, ct)) ?? new JsonObject();
        JsonObject after = to is null
            ? new JsonObject()
            : ParseObject(await LoadDocumentJsonAsync(projectId, to, ct)) ?? new JsonObject();

        string? beforeName = StringValue(before, "name");
        string? afterName = StringValue(after, "name");
        string? beforeDescription = StringValue(before, "description");
        string? afterDescription = StringValue(after, "description");

        ProjectHistoryFieldChange? name = string.Equals(beforeName, afterName, StringComparison.Ordinal)
            ? null
            : new ProjectHistoryFieldChange(beforeName, afterName);
        ProjectHistoryFieldChange? description = string.Equals(beforeDescription, afterDescription, StringComparison.Ordinal)
            ? null
            : new ProjectHistoryFieldChange(beforeDescription, afterDescription);

        return new ProjectHistoryItemDiff(
            ProjectVersionItemKind.Project,
            from.Key,
            from.Name,
            ProjectHistoryDiffModes.Project,
            Project: new ProjectHistoryProjectDiff(name, description));
    }

    // ── change classification ──────────────────────────────────────────────────────────────────

    private static bool FileContentChanged(
        DiffSide from,
        DiffSide to,
        IReadOnlyDictionary<Guid, ProjectFile> liveFiles)
    {
        if (from.Kind == ProjectVersionItemKind.File)
            return !FileContentEquals(from, to, liveFiles);

        if (from.Kind == ProjectVersionItemKind.DesignSystem)
        {
            if (from.DocumentSha256 is not null && to.DocumentSha256 is not null)
            {
                return !string.Equals(
                    from.DocumentSha256, to.DocumentSha256, StringComparison.Ordinal);
            }

            return from.DesignSystemVersionId != to.DesignSystemVersionId;
        }

        return !string.Equals(from.DocumentSha256, to.DocumentSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file's identity comparison (contract section 7). Equal content ids are unchanged. When one side
    /// was captured as a <c>sha256:</c> and the other only as an <c>etag:</c>/<c>unverified:</c> marker, the
    /// live row decides only when its hash matches the sha side and its hash ETag matches the etag side.
    /// </summary>
    private static bool FileContentEquals(
        DiffSide from,
        DiffSide to,
        IReadOnlyDictionary<Guid, ProjectFile> liveFiles)
    {
        if (string.Equals(from.ContentId, to.ContentId, StringComparison.Ordinal))
            return true;

        if (from.ContentId is null || to.ContentId is null || from.SizeBytes != to.SizeBytes)
            return false;

        string? fromSha = ShaHex(from.ContentId);
        string? toSha = ShaHex(to.ContentId);
        if (fromSha is not null && toSha is not null)
            return false;

        string? sha = fromSha ?? toSha;
        if (sha is null || !Guid.TryParse(from.Key, out Guid fileId)
            || !liveFiles.TryGetValue(fileId, out ProjectFile? live))
        {
            return false;
        }

        // Both sides must be proven equal to the same live bytes: the sha side equals the live row's hash,
        // AND the etag side's captured ETag is the very ETag that hash was taken at (so the live bytes
        // are still the ones the etag-side version saw). A same-size edit changes the ETag, so it reads as
        // changed. An unverified side carries no ETag and so can never be proven.
        string? etagSideETag = fromSha is not null ? to.StorageETag : from.StorageETag;
        return ContentId.IsSha256Hex(live.ContentSha256)
               && string.Equals(live.ContentSha256, sha, StringComparison.OrdinalIgnoreCase)
               && live.SizeBytes == from.SizeBytes
               && !string.IsNullOrEmpty(etagSideETag)
               && string.Equals(live.ContentSha256ETag, etagSideETag, StringComparison.Ordinal);
    }

    private static string? ShaHex(string contentId)
    {
        if (contentId is null || !ContentId.IsSha256(contentId))
            return null;

        return contentId[ContentId.Sha256Prefix.Length..];
    }

    // ── shared reads ──────────────────────────────────────────────────────────────────────────

    private async Task<ProjectVersion?> LoadVersionWithItemsAsync(
        Guid projectId,
        Guid versionId,
        CancellationToken ct)
    {
        DateTime now = Now;
        return await _db.ProjectVersions
            .AsNoTracking()
            .Include(v => v.Items)
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
    }

    private async Task<Project?> LoadProjectAsync(Guid projectId, CancellationToken ct) =>
        await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);

    private async Task<ProjectFile?> LoadLiveFileAsync(Guid projectId, string key, CancellationToken ct)
    {
        if (!Guid.TryParse(key, out Guid fileId))
            return null;

        return await _db.ProjectFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
    }

    private async Task<bool> LiveMatchesAsync(
        Guid projectId,
        ProjectFile live,
        DiffSide item,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.StorageETag) || string.IsNullOrWhiteSpace(live.StorageKey))
            return false;

        if (!string.IsNullOrEmpty(live.ContentSha256ETag)
            && string.Equals(live.ContentSha256ETag, item.StorageETag, StringComparison.Ordinal))
        {
            return true;
        }

        string? etag = await _storage.GetObjectETagAsync(projectId, live.StorageKey, ct);
        return string.Equals(etag, item.StorageETag, StringComparison.Ordinal);
    }

    private async Task<Dictionary<Guid, ProjectFile>> LoadLiveFilesAsync(
        Guid projectId,
        IReadOnlyCollection<Guid> fileIds,
        CancellationToken ct)
    {
        if (fileIds.Count == 0)
            return new Dictionary<Guid, ProjectFile>();

        return await _db.ProjectFiles
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
    }

    private async Task<DiffTarget?> ResolveTargetAsync(
        Guid projectId,
        Guid? toVersionId,
        CancellationToken ct)
    {
        if (toVersionId is { } versionId)
        {
            ProjectVersion? version = await LoadVersionWithItemsAsync(projectId, versionId, ct);
            return version is null
                ? null
                : new DiffTarget(version.CreatedAt, Index(version));
        }

        ProjectHistoryManifest? manifest = await BuildLiveManifestAsync(projectId, ct);
        return manifest is null
            ? null
            : new DiffTarget(null, manifest.Items
                .Select(item => ToSide(item))
                .ToDictionary(i => (i.Kind, i.Key)));
    }

    private async Task<string?> LoadDocumentJsonAsync(
        Guid projectId,
        DiffSide item,
        CancellationToken ct)
    {
        if (item.DocumentJson is not null)
            return item.DocumentJson;

        if (item.DocumentSha256 is null)
            return null;

        return await _db.ProjectHistoryDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Sha256 == item.DocumentSha256)
            .Select(d => d.Json)
            .FirstOrDefaultAsync(ct);
    }

    private static Dictionary<(ProjectVersionItemKind Kind, string Key), DiffSide> Index(ProjectVersion version) =>
        version.Items
            .Select(item => ToSide(item))
            .ToDictionary(i => (i.Kind, i.Key));

    private static IEnumerable<Guid> FileIds(IEnumerable<(ProjectVersionItemKind Kind, string Key)> items)
    {
        foreach ((ProjectVersionItemKind kind, string key) in items)
        {
            if (kind == ProjectVersionItemKind.File && Guid.TryParse(key, out Guid fileId))
                yield return fileId;
        }
    }

    private static bool IsQuarantined(ProjectFile file) =>
        file.ModerationStatus == FileModerationStatus.Quarantined
        || file.CsamStatus == CsamScanStatus.Quarantined;

    private static string? DisplayFileName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Path.GetFileName(name);

    private static DiffSide ToSide(ProjectVersionItem item) =>
        new(
            item.Kind,
            item.ItemKey,
            item.Name,
            item.Category,
            item.MimeType,
            item.SizeBytes,
            item.ContentId,
            item.StorageKey,
            item.StorageETag,
            item.DocumentSha256,
            DocumentJson: null,
            item.TimelineRevision,
            item.DesignSystemVersionId,
            item.ErasedAt,
            item.OutputProducedAt,
            item.PlatformMade);

    private static DiffSide ToSide(ProjectHistoryManifestItem item) =>
        new(
            item.Kind,
            item.Key,
            item.Name,
            item.Category,
            item.MimeType,
            item.SizeBytes,
            item.ContentId,
            item.StorageKey,
            item.StorageETag,
            item.DocumentSha256,
            item.DocumentJson,
            item.TimelineRevision,
            item.DesignSystemVersionId,
            ErasedAt: null,
            OutputProducedAt: item.OutputProducedAt,
            PlatformMade: item.PlatformMade);

    // ── JSON helpers ───────────────────────────────────────────────────────────────────────────

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringValue(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue<string>(out string? text)
            ? text
            : null;

    private static int IntValue(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out JsonNode? node) && TryInt(node, out int value) ? value : 0;

    private static long LongValue(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue<long>(out long number)
            ? number
            : 0;

    private static bool BoolValue(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue<bool>(out bool flag)
        && flag;

    private static bool TryInt(JsonNode? node, out int value)
    {
        value = 0;
        return node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out value);
    }

    private sealed record DiffSide(
        ProjectVersionItemKind Kind,
        string Key,
        string? Name,
        string? Category,
        string? MimeType,
        long SizeBytes,
        string? ContentId,
        string? StorageKey,
        string? StorageETag,
        string? DocumentSha256,
        string? DocumentJson,
        int? TimelineRevision,
        Guid? DesignSystemVersionId,
        DateTime? ErasedAt,
        DateTime? OutputProducedAt = null,
        bool PlatformMade = false);

    private sealed record DiffTarget(
        DateTime? CreatedAt,
        Dictionary<(ProjectVersionItemKind Kind, string Key), DiffSide> Items);
}
