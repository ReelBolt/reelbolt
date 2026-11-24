using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.EditTimelines;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Restores a timeline from a version (contract section 8 step 5) through the SAME write core the
/// editor's restore PATCH uses (<see cref="EditTimelineCommit"/>): the stored document is validated by
/// <see cref="EditTimelineValidator"/> against the project as it is now, the current revision is kept
/// as a <c>beforeRestore</c> snapshot, the revision is bumped and the op log gets a
/// <c>{type:"restore"}</c> marker. A document the validator refuses is skipped <c>TIMELINE_INVALID</c>.
/// <list type="bullet">
/// <item>A soft-deleted timeline is undeleted and then restored as the next revision.</item>
/// <item>A hard-deleted one is recreated with the stored name and document at revision 1.</item>
/// </list>
/// Project scope soft-deletes live timelines the version does not hold.
/// </summary>
public sealed class TimelineRestoreKindHandler : IProjectRestoreKindHandler
{
    private const string RestoreOpsJson = """[{"type":"restore"}]""";

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ILogger _logger;
    private readonly EditingRetentionOptions _retention;
    private readonly TimeProvider _clock;

    public TimelineRestoreKindHandler(
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger<TimelineRestoreKindHandler>? logger = null,
        IOptions<EditingRetentionOptions>? retention = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _retention = retention?.Value ?? new EditingRetentionOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public ProjectVersionItemKind Kind => ProjectVersionItemKind.Timeline;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        if (!Guid.TryParse(item.ItemKey, out Guid storedId))
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);

        string? json = await RestoreDocument.LoadAsync(_db, context.Project.Id, item.DocumentSha256, ct);
        if (RestoreDocument.Parse(json) is not JsonObject root
            || root["document"] is not JsonNode documentNode)
        {
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);
        }

        EditTimeline? source;
        try
        {
            source = documentNode.Deserialize<EditTimeline>(EditTimelineJson.Options);
        }
        catch (JsonException)
        {
            source = null;
        }

        if (source is null)
            return Skip(item, ProjectHistoryRestoreReasons.ContentGone);

        string? name = RestoreDocument.GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
            name = item.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = source.Name;

        // Ignore the soft-delete filter: a deleted row is restored, not duplicated.
        EditTimelineEntity? live = await _db.EditTimelines
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == storedId, ct);
        if (live is not null && live.ProjectId != context.Project.Id)
            live = null; // the id belongs to another project: never touch it

        // A deleted row's id is free; reuse it (its document names it). An id held by another row in
        // this project cannot be reused, so the recreated timeline takes a new one.
        Guid targetId = storedId;
        if (live is null && await _db.EditTimelines.IgnoreQueryFilters().AnyAsync(t => t.Id == storedId, ct))
            targetId = Guid.NewGuid();

        EditTimeline document = source with
        {
            Id = targetId,
            ProjectId = context.Project.Id,
            Name = name
        };

        // The same validation the editor's PATCH runs before anything is written.
        EditTimelineValidationContextBuilder contextBuilder = new(_db, _storage, _logger);
        ValidationResult validation = EditTimelineValidator.Validate(
            document, await contextBuilder.BuildAsync(context.Project.Id, document, ct));
        if (!validation.IsValid)
            return Skip(item, ProjectHistoryRestoreReasons.TimelineInvalid);

        string documentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options);
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        if (live is null)
        {
            // Hard-deleted: a new timeline at revision 1, exactly like a fresh create.
            EditTimelineEntity created = new()
            {
                Id = targetId,
                ProjectId = context.Project.Id,
                Name = name,
                DocumentJson = documentJson,
                Revision = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.EditTimelines.Add(created);
            _db.EditTimelineSnapshots.Add(EditTimelineCommit.NewSnapshot(
                targetId, 1, documentJson, EditTimelineSnapshotCause.Milestone,
                now, context.UserId, _retention.AutoCheckpointWindow));
            _db.EditTimelineOps.Add(new EditTimelineOp
            {
                Id = Guid.NewGuid(),
                TimelineId = targetId,
                Revision = 1,
                OpsJson = RestoreOpsJson,
                CreatedAt = now
            });
            await _db.SaveChangesAsync(ct);
            return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, name);
        }

        // Live or soft-deleted: undelete in the same save, then commit the stored document as the next
        // revision (beforeRestore snapshot + op-log restore marker), exactly like the restore PATCH.
        if (live.DeletedAt is not null)
            live.DeletedAt = null;
        live.Name = name;

        EditCommitResult commit = await EditTimelineCommit.CommitAsync(
            _db,
            live,
            live.Revision,
            documentJson,
            RestoreOpsJson,
            restore: true,
            now,
            context.UserId,
            _retention.AutoCheckpointWindow,
            ct);

        if (commit.Status == EditCommitStatus.Committed)
            return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, name);

        // Skipped: the undelete and rename above are still tracked and the next handler's SaveChanges
        // would persist them while this item is reported unchanged, so put the row back as loaded.
        var entry = _db.Entry(live);
        if (entry.State == EntityState.Modified)
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }

        return Skip(item, ProjectHistoryRestoreReasons.TimelineInvalid);
    }

    public async Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
        ProjectRestoreWriteContext context, IReadOnlySet<string> presentKeys, CancellationToken ct)
    {
        List<EditTimelineEntity> live = await _db.EditTimelines
            .Where(t => t.ProjectId == context.Project.Id)
            .ToListAsync(ct);

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        List<ProjectRestoreOutcome> outcomes = new();

        foreach (EditTimelineEntity timeline in live)
        {
            if (presentKeys.Contains(timeline.Id.ToString("D")))
                continue;

            timeline.DeletedAt = now;
            timeline.UpdatedAt = now;
            outcomes.Add(ProjectRestoreOutcome.Removed(
                ProjectVersionItemKind.Timeline, timeline.Id.ToString("D"), timeline.Name));
        }

        if (outcomes.Count > 0)
            await _db.SaveChangesAsync(ct);

        return outcomes;
    }

    private static ProjectRestoreOutcome Skip(ProjectVersionItem item, string reason) =>
        ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, reason);
}
