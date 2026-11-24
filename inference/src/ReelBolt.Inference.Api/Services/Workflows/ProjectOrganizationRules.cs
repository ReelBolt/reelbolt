using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>Name rules, caps, palette and shared write helpers for project tags and folders, kept in one place.</summary>
public static class ProjectOrganizationRules
{
    public const int MaxTagNameLength = 40;
    public const int MaxFolderNameLength = 60;
    public const int MaxTagsPerOrganization = 200;
    public const int MaxFoldersPerOrganization = 500;
    public const int MaxTagsPerProject = 20;
    public const int MaxOrganizeProjects = 100;
    public const string DefaultColor = "gray";

    /// <summary>The closed colour palette.</summary>
    public static readonly IReadOnlyList<string> Palette =
        ["gray", "red", "orange", "yellow", "green", "teal", "blue", "indigo", "violet", "pink"];

    /// <summary>Trimmed name when it is 1..<paramref name="maxLength"/> characters with no control characters, otherwise null.</summary>
    public static string? NormalizeName(string? name, int maxLength)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength) return null;
        foreach (char c in trimmed) if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) return null;
        return trimmed;
    }

    /// <summary>The value the case-insensitive unique index is built on.</summary>
    public static string NameKey(string normalizedName) => normalizedName.ToLowerInvariant();

    /// <summary>The palette word for <paramref name="color"/> (null = default), or null when it is not in the palette.</summary>
    public static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return DefaultColor;
        string word = color.Trim().ToLowerInvariant();
        return Palette.Contains(word) ? word : null;
    }

    public const int MaxTagFilters = 20;

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction (joining an open one), holding a per-organization
    /// advisory lock on PostgreSQL (rolled back instead of committed when <paramref name="commitWhen"/> says no) so a cap check inside it cannot race. A no-op lock on SQLite/InMemory
    /// (InMemory also runs without a transaction).
    /// </summary>
    public static async Task<T> InTransactionAsync<T>(
        InferenceApiDbContext db, string lockScope, Guid orgId, Func<Task<T>> work, CancellationToken ct, Func<T, bool>? commitWhen = null)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return await work();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                string key = lockScope + ":" + orgId;
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))", ct);
            }
            T result = await work();
            if (commitWhen is null || commitWhen(result)) await tx.CommitAsync(ct);
            else await tx.RollbackAsync(ct);
            return result;
        });
    }

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.GetType().Name == "SqliteException"
            && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));

    /// <summary>Tags per project id, sorted by name, for building responses.</summary>
    public static async Task<Dictionary<Guid, List<ProjectTagRef>>> LoadTagsAsync(
        InferenceApiDbContext db, IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        var rows = await db.ProjectTagLinks
            .Where(l => projectIds.Contains(l.ProjectId))
            .Select(l => new { l.ProjectId, l.Tag.Id, l.Tag.Name, l.Tag.Color })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.ProjectId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ProjectTagRef(r.Id, r.Name, r.Color)).ToList());
    }

    /// <summary>
    /// Sets UpdatedAt (and optionally clears the folder) on the projects in <paramref name="projects"/> so the
    /// ConditionalGet validators and updated-at sorting move. Set-based on a relational provider, tracked otherwise.
    /// </summary>
    public static async Task BumpAsync(
        InferenceApiDbContext db, IQueryable<Project> projects, DateTime now, bool clearFolder, CancellationToken ct)
    {
        if (db.Database.IsRelational())
        {
            if (clearFolder)
                await projects.ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now).SetProperty(p => p.FolderId, (Guid?)null), ct);
            else
                await projects.ExecuteUpdateAsync(s => s.SetProperty(p => p.UpdatedAt, now), ct);
            return;
        }

        foreach (Project p in await projects.ToListAsync(ct))
        {
            p.UpdatedAt = now;
            if (clearFolder) p.FolderId = null;
        }
    }
}
