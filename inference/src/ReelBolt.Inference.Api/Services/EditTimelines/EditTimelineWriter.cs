using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.EditTimelines;

public sealed class EditTimelineWriter : IEditTimelineWriter
{
    private readonly InferenceApiDbContext _db;
    private readonly EditTimelineValidationContextBuilder _contexts;
    private readonly ITenantContext? _tenant;
    private readonly TimeProvider _clock;
    private readonly EditingRetentionOptions _retention;

    public EditTimelineWriter(
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger<EditTimelineWriter> logger,
        ITenantContext? tenant = null,
        TimeProvider? clock = null,
        Microsoft.Extensions.Options.IOptions<EditingRetentionOptions>? retention = null)
    {
        _db = db;
        _contexts = new EditTimelineValidationContextBuilder(db, storage, logger);
        _tenant = tenant;
        _clock = clock ?? TimeProvider.System;
        _retention = retention?.Value ?? new EditingRetentionOptions();
    }

    public async Task<ServerEditResult> ApplyServerEditAsync(
        Guid projectId, Guid timelineId, Func<EditTimeline, ServerEditMutation?> mutate, CancellationToken ct)
    {
        // A server-side edit has no request organization: the scope is the platform's, and every read below is pinned to the project.
        using IDisposable system = _tenant.System();

        for (int attempt = 0; ; attempt++)
        {
            // A copy tracked earlier in this scope may be stale (another writer has committed since): read the row afresh.
            foreach (var tracked in _db.ChangeTracker.Entries<EditTimelineEntity>().Where(e => e.Entity.Id == timelineId).ToList())
            {
                tracked.State = EntityState.Detached;
            }

            EditTimelineEntity? timeline = await _db.EditTimelines
                .FirstOrDefaultAsync(t => t.Id == timelineId && t.ProjectId == projectId, ct);
            if (timeline is null) return new ServerEditResult(ServerEditOutcome.NotFound);

            EditTimeline? current;
            try
            {
                current = JsonSerializer.Deserialize<EditTimeline>(timeline.DocumentJson, EditTimelineJson.Options);
            }
            catch (JsonException)
            {
                current = null;
            }

            if (current is null)
            {
                return new ServerEditResult(ServerEditOutcome.Invalid, Message: "The stored timeline could not be read.");
            }

            ServerEditMutation? mutation = mutate(current);
            if (mutation is null) return new ServerEditResult(ServerEditOutcome.NoChange, timeline.Revision);

            string documentJson = JsonSerializer.Serialize(mutation.Document, EditTimelineJson.Options);
            if (Encoding.UTF8.GetByteCount(documentJson) > EditTimelinesController.MaxDocumentBytes
                || Encoding.UTF8.GetByteCount(mutation.OpsJson) > EditTimelinesController.MaxOpsBytes)
            {
                return new ServerEditResult(ServerEditOutcome.TooLarge, Message: "The edit is too large to store.");
            }

            EditTimelineValidationContext context = await _contexts.BuildAsync(projectId, mutation.Document, ct);
            List<(string Path, string Message)> violations = EditTimelineValidator.Validate(mutation.Document, context).Violations.ToList();
            if (mutation.Document.Id != timeline.Id) violations.Add(("id", $"id must be this timeline's id {timeline.Id}"));
            if (mutation.Document.ProjectId != projectId) violations.Add(("projectId", $"projectId must be this timeline's project {projectId}"));
            if (violations.Count > 0) return new ServerEditResult(ServerEditOutcome.Invalid, Violations: violations);

            EditCommitResult commit = await EditTimelineCommit.CommitAsync(
                _db, timeline, timeline.Revision, documentJson, mutation.OpsJson, restore: false,
                _clock.GetUtcNow().UtcDateTime, createdByUserId: null, _retention.AutoCheckpointWindow, ct);

            switch (commit.Status)
            {
                case EditCommitStatus.Committed:
                    return new ServerEditResult(ServerEditOutcome.Applied, commit.Revision);
                case EditCommitStatus.Gone:
                    return new ServerEditResult(ServerEditOutcome.NotFound);
                case EditCommitStatus.Conflict when attempt >= 1:
                    return new ServerEditResult(ServerEditOutcome.Conflict, commit.Revision);
            }

            // A concurrent edit won this round: read the timeline again and redo the mutation on top of it.
        }
    }
}
