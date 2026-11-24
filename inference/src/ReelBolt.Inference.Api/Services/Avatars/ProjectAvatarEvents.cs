using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>
/// The ONE writer of <c>project_avatar_events</c>, the append-only accountability record of an avatar
/// (docs/design/avatars.md "project_avatar_events"). Every write path (create, re-attest, revoke, delete, retention,
/// vendor-copy deletes, the retention warning, a project delete) goes through <see cref="Add"/>, which only ADDS a row
/// to the context; the caller's own save commits it in the same transaction as the change it records.
/// </summary>
/// <remarks>
/// <b>An event never holds media, file contents, URLs, storage keys, ids of files or the subject's name.</b> The outcomes
/// are a flat object whose keys are snake_case words and whose values are numbers, booleans or snake_case outcome
/// words (<c>deleted</c>, <c>vendor_refused</c>, <c>app</c>, ...). Anything else is a programming error and throws
/// before anything is written, so a careless caller cannot leak a name or a key into a row kept for three years.
/// </remarks>
public static partial class ProjectAvatarEvents
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    /// <summary>Adds one event for <paramref name="avatar"/>. Throws <see cref="ArgumentException"/> for an outcome value that is not a count, a flag or an outcome word.</summary>
    /// <param name="occurredAt">
    /// The event time; null = now. A caller that reasons on its own clock (the retention sweep compares the warning's
    /// time with the attestation's) passes that clock's time, so the comparison never mixes two clocks.
    /// </param>
    public static ProjectAvatarEvent Add(
        InferenceApiDbContext db,
        ProjectAvatar avatar,
        Guid organizationId,
        AvatarEventKind kind,
        Guid? actorUserId,
        IReadOnlyDictionary<string, object>? outcomes = null,
        DateTime? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(avatar);
        var row = new ProjectAvatarEvent
        {
            Id = Guid.NewGuid(),
            AvatarId = avatar.Id,
            ProjectId = avatar.ProjectId,
            OrganizationId = organizationId,
            Kind = kind,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt ?? DateTime.UtcNow,
            ConsentStatementVersion = string.IsNullOrEmpty(avatar.ConsentStatementVersion) ? null : avatar.ConsentStatementVersion,
            OutcomesJson = Serialize(outcomes)
        };
        db.ProjectAvatarEvents.Add(row);
        return row;
    }

    /// <summary>
    /// Adds one event about a workspace avatar library ITEM (docs/design/avatar-objects.md "The workspace avatar
    /// library"): the row's <c>AvatarId</c> is the item's id and its <c>ProjectId</c> is <paramref name="projectId"/>
    /// (the project an item was published from) or <see cref="Guid.Empty"/> when the event names no project. Same
    /// rules as <see cref="Add"/>: counts and outcome words only, committed by the caller's own save.
    /// </summary>
    public static ProjectAvatarEvent AddForLibraryItem(
        InferenceApiDbContext db,
        AvatarLibraryItem item,
        AvatarEventKind kind,
        Guid? actorUserId,
        IReadOnlyDictionary<string, object>? outcomes = null,
        DateTime? occurredAt = null,
        Guid? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var row = new ProjectAvatarEvent
        {
            Id = Guid.NewGuid(),
            AvatarId = item.Id,
            ProjectId = projectId ?? Guid.Empty,
            OrganizationId = item.OrganizationId,
            Kind = kind,
            ActorUserId = actorUserId,
            OccurredAt = occurredAt ?? DateTime.UtcNow,
            ConsentStatementVersion = string.IsNullOrEmpty(item.ConsentStatementVersion) ? null : item.ConsentStatementVersion,
            OutcomesJson = Serialize(outcomes)
        };
        db.ProjectAvatarEvents.Add(row);
        return row;
    }

    /// <summary>
    /// A project delete removes its avatars by cascade (no avatar route runs), so the accountability record is written
    /// here: one <see cref="AvatarEventKind.Deleted"/> per avatar with <c>{"cause":"project_deleted"}</c>, added to the
    /// same save as the project delete. Phase 2 must also remove vendor-side copies on this path.
    /// <para>
    /// Phase 3: the stills made from the project's avatars are bare objects no row names, and the storage sweep only
    /// reaches projects that still exist, so every object under <c>agentFiles/avatars/</c> gets a durable
    /// <c>storage_retention_marks</c> row in the same save (marks outlive the project; the purge deletes them). With
    /// <paramref name="listing"/> null, or a listing that fails, nothing is marked: a project delete is never refused
    /// for it (docs/avatars.md names the gap).
    /// </para>
    /// </summary>
    public static async Task AddForProjectDeleteAsync(
        InferenceApiDbContext db, Project project, Guid? actorUserId, CancellationToken ct, IObjectListing? listing = null)
    {
        List<ProjectAvatar> avatars = await db.ProjectAvatars.AsNoTracking().Where(a => a.ProjectId == project.Id).ToListAsync(ct);
        foreach (ProjectAvatar avatar in avatars)
        {
            int references = await db.ProjectAvatarReferences.CountAsync(r => r.AvatarId == avatar.Id, ct);
            Add(db, avatar, project.OrganizationId, AvatarEventKind.Deleted, actorUserId,
                new Dictionary<string, object> { ["cause"] = "project_deleted", ["references"] = references });
        }

        if (listing is null) return;

        IReadOnlyList<StoredObjectEntry> stills;
        try
        {
            stills = await listing.ListAsync(project.Id, AvatarStillStorage.Category + "/avatars", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        List<string> keys = [.. stills.Select(s => s.StorageKey).Where(k => AvatarStillStorage.AvatarOf(project.Id, k) is not null)];
        if (keys.Count == 0) return;

        HashSet<string> already = [.. await db.StorageRetentionMarks.IgnoreQueryFilters()
            .Where(m => keys.Contains(m.StorageKey)).Select(m => m.StorageKey).ToListAsync(ct)];
        DateTime now = DateTime.UtcNow;
        db.StorageRetentionMarks.AddRange(keys.Where(k => !already.Contains(k)).Select(k => new Shared.Data.Models.Billing.StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            StorageKey = k,
            Category = AvatarStillStorage.Category,
            MarkedAt = now
        }));
    }

    /// <summary>The stored JSON of <paramref name="outcomes"/>, after checking every key and value; null for none.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, object>? outcomes)
    {
        if (outcomes is null || outcomes.Count == 0) return null;
        foreach ((string key, object value) in outcomes)
        {
            if (!Word().IsMatch(key))
                throw new ArgumentException($"Avatar event outcome key '{key}' is not a snake_case word.", nameof(outcomes));
            bool allowed = value switch
            {
                int or long or bool => true,
                string word => Word().IsMatch(word),
                _ => false
            };
            if (!allowed)
                throw new ArgumentException($"Avatar event outcome '{key}' must be a count, a flag or an outcome word.", nameof(outcomes));
        }

        return JsonSerializer.Serialize(new SortedDictionary<string, object>(outcomes.ToDictionary(), StringComparer.Ordinal), Json);
    }
}
