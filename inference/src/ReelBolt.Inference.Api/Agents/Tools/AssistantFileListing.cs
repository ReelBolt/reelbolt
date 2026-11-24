using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// The query, paging and cursor logic behind the assistant's <c>ListProjectFiles</c> tool: filters run in SQL,
/// results page by a keyset (<c>uploadedAt</c> descending, then <c>id</c>) so inserts between pages never shift
/// or duplicate a row, and one page is held under a byte budget however long the summaries are.
/// </summary>
/// <remarks>
/// The cursor is opaque (base64url) and carries a checksum bound to the project and the active filters, so a
/// hand-edited cursor, or one replayed under different filters, is refused rather than guessed at. The checksum
/// is an integrity check, not a secret: project scoping is applied by the caller's query either way.
/// </remarks>
internal static class AssistantFileListing
{
    internal const int DefaultLimit = 100;
    internal const int MaxLimit = 500;
    internal const int MaxSummaryChars = 300;

    /// <summary>Soft cap on one page's serialized rows; a page that would exceed it ends early with a cursor.</summary>
    internal const int PageByteBudget = 56 * 1024;

    private const string BadCursor = """{"error":"invalid cursor: pass the nextCursor from the previous ListProjectFiles call unchanged, with the same filters"}""";

    /// <param name="projectId">The project the (already project-scoped) <paramref name="projectFiles"/> belongs to.</param>
    internal static string List(
        Guid projectId,
        IQueryable<ProjectFile> projectFiles,
        string projectName,
        string? pathPrefix,
        string? mimePrefix,
        DateTime? since,
        string? nameContains,
        int? limit,
        string? cursor)
    {
        int take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        string? path = NullIfBlank(pathPrefix)?.Trim().TrimStart('/');
        string? mime = NullIfBlank(mimePrefix)?.Trim();
        string? name = NullIfBlank(nameContains)?.Trim().ToLowerInvariant();
        DateTime? sinceUtc = since is { } s ? (s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime()) : null;

        IQueryable<ProjectFile> query = projectFiles;
        // A quarantined row is not merely hidden from the UI: the assistant must not read its name,
        // summary or path either.
        query = query.Where(f => f.ModerationStatus != FileModerationStatus.Quarantined);
        if (path is { Length: > 0 })
        {
            string rooted = "/" + path;
            query = query.Where(f => (f.DirectoryPath != null && (f.DirectoryPath.StartsWith(path) || f.DirectoryPath.StartsWith(rooted)))
                                     || (f.OriginalPath != null && f.OriginalPath.StartsWith(path)));
        }
        if (mime is { Length: > 0 })
            query = query.Where(f => f.MimeType.StartsWith(mime));
        if (sinceUtc is { } from)
            query = query.Where(f => f.UploadedAt >= from);
        if (name is { Length: > 0 })
            query = query.Where(f => f.OriginalFileName.ToLower().Contains(name));

        string scope = $"{projectId:N}|{path}|{mime}|{sinceUtc?.Ticks}|{name}";
        int total = query.Count();

        if (NullIfBlank(cursor) is { } rawCursor)
        {
            if (!TryDecode(rawCursor, scope, out DateTime lastAt, out Guid lastId))
                return BadCursor;
            query = query.Where(f => f.UploadedAt < lastAt || (f.UploadedAt == lastAt && f.Id.CompareTo(lastId) < 0));
        }

        List<ProjectFile> fetched = query
            .OrderByDescending(f => f.UploadedAt).ThenByDescending(f => f.Id)
            .Take(take + 1)
            .ToList();

        JsonArray rows = [];
        bool anyTruncated = false;
        int bytes = 0;
        ProjectFile? last = null;
        foreach (ProjectFile f in fetched.Take(take))
        {
            JsonObject row = Row(f, out bool truncated);
            int size = Encoding.UTF8.GetByteCount(row.ToJsonString());
            if (last is not null && bytes + size > PageByteBudget)
                break;
            bytes += size;
            rows.Add(row);
            anyTruncated |= truncated;
            last = f;
        }

        JsonObject result = new()
        {
            ["projectName"] = projectName,
            ["files"] = rows,
            ["total"] = total,
            ["truncatedSummaries"] = anyTruncated
        };
        if (last is not null && fetched.Count > rows.Count)
            result["nextCursor"] = Encode(last.UploadedAt, last.Id, scope);
        return result.ToJsonString();
    }

    private static JsonObject Row(ProjectFile f, out bool truncated)
    {
        string? summary = f.AgentSummary;
        truncated = summary is { Length: > MaxSummaryChars };
        if (truncated)
            summary = string.Concat(summary!.AsSpan(0, MaxSummaryChars - 1), "…");
        return new JsonObject
        {
            ["id"] = f.Id,
            ["originalFileName"] = f.OriginalFileName,
            ["directoryPath"] = f.DirectoryPath,
            ["category"] = f.Category,
            ["mimeType"] = f.MimeType,
            ["sizeBytes"] = f.SizeBytes,
            ["agentSummary"] = summary,
            ["summaryStatus"] = f.SummaryStatus.ToString(),
            ["indexingStatus"] = f.IndexingStatus.ToString(),
            ["uploadedAt"] = f.UploadedAt
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Checksum(long ticks, Guid id, string scope) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v1|{ticks}|{id:N}|{scope}")))[..16];

    private static string Encode(DateTime uploadedAt, Guid id, string scope)
    {
        long ticks = uploadedAt.Ticks;
        string payload = $"{ticks}.{id:N}.{Checksum(ticks, id, scope)}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string cursor, string scope, out DateTime uploadedAt, out Guid id)
    {
        uploadedAt = default;
        id = default;
        try
        {
            string b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('.');
            if (parts.Length != 3 || !long.TryParse(parts[0], out long ticks) || ticks is < 0 or > 3155378975999999999L
                || !Guid.TryParseExact(parts[1], "N", out id))
                return false;
            if (!string.Equals(parts[2], Checksum(ticks, id, scope), StringComparison.Ordinal))
                return false;
            uploadedAt = new DateTime(ticks, DateTimeKind.Utc);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }
}
