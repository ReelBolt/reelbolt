using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>The canonical JSON body a document kind's item points at, or null when the row is gone.</summary>
internal static class RestoreDocument
{
    public static async Task<string?> LoadAsync(
        InferenceApiDbContext db, Guid projectId, string? sha256, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sha256))
            return null;

        return await db.ProjectHistoryDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && d.Sha256 == sha256)
            .Select(d => d.Json)
            .FirstOrDefaultAsync(ct);
    }

    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The string member, or null when absent or of another JSON type.</summary>
    public static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue(out string? text)
            ? text
            : null;

    public static bool? GetBool(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue(out bool flag)
            ? flag
            : null;
}

/// <summary>Restores the project item: its name and description (contract section 8 step 5).</summary>
public sealed class ProjectRestoreKindHandler(InferenceApiDbContext db) : IProjectRestoreKindHandler
{
    public ProjectVersionItemKind Kind => ProjectVersionItemKind.Project;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        string? json = await RestoreDocument.LoadAsync(db, context.Project.Id, item.DocumentSha256, ct);
        if (RestoreDocument.Parse(json) is not JsonObject document)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.ContentGone);

        string? name = RestoreDocument.GetString(document, "name");
        if (string.IsNullOrEmpty(name))
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.ContentGone);

        context.Project.Name = name;
        context.Project.Description = RestoreDocument.GetString(document, "description");
        await db.SaveChangesAsync(ct);

        return ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, name);
    }
}

/// <summary>
/// Restores the pronunciation glossary (contract section 8 step 5): the captured set replaces the live
/// one in a single save, so a partially applied glossary is never observable.
/// </summary>
public sealed class PronunciationRestoreKindHandler(InferenceApiDbContext db) : IProjectRestoreKindHandler
{
    public ProjectVersionItemKind Kind => ProjectVersionItemKind.Pronunciation;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        string? json = await RestoreDocument.LoadAsync(db, context.Project.Id, item.DocumentSha256, ct);
        if (RestoreDocument.Parse(json) is not JsonArray captured)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.ContentGone);

        List<ProjectPronunciationEntry> live = await db.ProjectPronunciationEntries
            .Where(e => e.ProjectId == context.Project.Id)
            .ToListAsync(ct);

        db.ProjectPronunciationEntries.RemoveRange(live);

        DateTime now = DateTime.UtcNow;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        bool dropped = false;

        foreach (JsonNode? node in captured)
        {
            if (node is not JsonObject entry)
            {
                dropped = true;
                continue;
            }

            string term = PronunciationGlossaryValidator.Normalize(RestoreDocument.GetString(entry, "term"));
            string spelling = PronunciationGlossaryValidator.Normalize(RestoreDocument.GetString(entry, "spelling"));
            bool caseSensitive = RestoreDocument.GetBool(entry, "caseSensitive") ?? false;

            if (PronunciationGlossaryValidator.Validate(term, spelling).Count > 0 || !seen.Add(term))
            {
                dropped = true;
                continue;
            }

            if (count >= ProjectPronunciationEntry.MaxEntriesPerProject)
            {
                dropped = true;
                break;
            }

            db.ProjectPronunciationEntries.Add(new ProjectPronunciationEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = context.Project.Id,
                Term = term,
                Spelling = spelling,
                CaseSensitive = caseSensitive,
                CreatedAt = now,
                UpdatedAt = now
            });
            count++;
        }

        await db.SaveChangesAsync(ct);
        return ProjectRestoreOutcome.Restored(
            item.Kind, item.ItemKey, item.Name,
            dropped ? ProjectHistoryRestoreNotices.PronunciationEntrySkipped : null);
    }
}

/// <summary>
/// Restores the design system (contract section 8 step 5) through
/// <see cref="IDesignSystemService.RestoreAsync"/> with the pinned version id; a version that is already
/// current is left alone.
/// </summary>
public sealed class DesignSystemRestoreKindHandler(
    InferenceApiDbContext db, IDesignSystemService? designSystems = null) : IProjectRestoreKindHandler
{
    public ProjectVersionItemKind Kind => ProjectVersionItemKind.DesignSystem;

    public async Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context, ProjectVersionItem item, CancellationToken ct)
    {
        if (item.DesignSystemVersionId is not { } versionId)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.DesignVersionGone);

        bool exists = await db.DesignSystemVersions
            .AsNoTracking()
            .AnyAsync(v => v.Id == versionId, ct);
        if (!exists)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.DesignVersionGone);

        bool alreadyCurrent = await db.DesignSystems
            .AsNoTracking()
            .AnyAsync(s => s.ProjectId == context.Project.Id && s.CurrentVersionId == versionId, ct);
        if (alreadyCurrent)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.AlreadyCurrent);

        if (designSystems is null)
            return ProjectRestoreOutcome.Skipped(item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.NotYetSupported);

        DesignSystemWriteResult result = await designSystems.RestoreAsync(
            context.Project.Id, versionId, context.UserId ?? Guid.Empty, ct);
        return result.Status switch
        {
            DesignSystemWriteStatus.Ok => ProjectRestoreOutcome.Restored(item.Kind, item.ItemKey, item.Name),
            DesignSystemWriteStatus.NotFound => ProjectRestoreOutcome.Skipped(
                item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.DesignVersionGone),
            _ => ProjectRestoreOutcome.Skipped(
                item.Kind, item.ItemKey, item.Name, ProjectHistoryRestoreReasons.DesignVersionInvalid)
        };
    }
}
