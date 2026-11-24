using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>What was left out of an unpack: counts by fixed reason code and a few example paths (display text only).</summary>
public sealed record ArchiveUnpackSkippedResponse(IReadOnlyDictionary<string, int> Counts, IReadOnlyList<ArchiveUnpackSkippedSample> Samples);

public sealed record ArchiveUnpackSkippedSample(string Path, string Reason);

/// <summary>One unpack as the project API shows it. Never carries a storage key or a bucket.</summary>
public sealed record ArchiveUnpackResponse(
    Guid Id,
    string ArchiveName,
    string Status,
    string? FailureCode,
    int FilesCreated,
    long BytesCreated,
    string TargetDirectory,
    ArchiveUnpackSkippedResponse Skipped,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary>
/// Zip unpacking: re-run (or retry) the unpack of an uploaded zip, and list a project's recent unpacks. The unpack itself
/// runs in a background worker; these routes only queue it and report it. Every action resolves the project through
/// <see cref="IProjectAccessService"/> once: 404 across workspaces, 403 for a role below the permission.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}")]
[Authorize]
public class ProjectArchiveUnpacksController : ControllerBase
{
    private const int RecentLimit = 20;

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IArchiveUnpackScheduler _scheduler;

    public ProjectArchiveUnpacksController(
        InferenceApiDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser, IArchiveUnpackScheduler scheduler)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
        _scheduler = scheduler;
    }

    /// <summary>
    /// Queues an unpack of one zip of the project (also the retry after a failure). 202 with the new unpack; 404 for a file
    /// outside the project; 400 NOT_AN_ARCHIVE for a file that is not a zip; 409 UNPACK_IN_PROGRESS while one is active.
    /// Edit and a verified email are required.
    /// </summary>
    [HttpPost("files/{fileId:guid}/unpack")]
    public async Task<ActionResult<ArchiveUnpackResponse>> Unpack(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file is null || file.CsamStatus == CsamScanStatus.Quarantined) return NotFound();

        if (!ArchiveUnpackPaths.IsZipFile(file))
            return BadRequest(new { code = "NOT_AN_ARCHIVE", message = "Only an uploaded .zip file can be unpacked." });

        ProjectArchiveUnpack? queued = await _scheduler.EnqueueAsync(file, access.Project!.OrganizationId, _currentUser.UserId, ct);
        if (queued is null)
            return Conflict(new { code = "UNPACK_IN_PROGRESS", message = "This zip is already being unpacked." });

        return Accepted(ToResponse(queued));
    }

    /// <summary>The project's recent unpacks (at most 20): active ones first, then the newest. View role.</summary>
    [HttpGet("archive-unpacks")]
    public async Task<ActionResult<IReadOnlyList<ArchiveUnpackResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        List<ProjectArchiveUnpack> rows = await _db.ProjectArchiveUnpacks.AsNoTracking()
            .Where(u => u.ProjectId == projectId)
            .OrderBy(u => u.Status == ArchiveUnpackRunStatus.Pending || u.Status == ArchiveUnpackRunStatus.Running ? 0 : 1)
            .ThenByDescending(u => u.CreatedAt)
            .Take(RecentLimit)
            .ToListAsync(ct);
        return Ok(rows.Select(ToResponse).ToList());
    }

    private static ArchiveUnpackResponse ToResponse(ProjectArchiveUnpack row) => new(
        row.Id,
        row.ArchiveName,
        row.Status.ToString(),
        row.FailureCode,
        row.FilesCreated,
        row.BytesCreated,
        row.TargetDirectory,
        ParseSkipped(row.SkippedJson),
        row.CreatedAt,
        row.FinishedAt);

    private static ArchiveUnpackSkippedResponse ParseSkipped(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ArchiveUnpackSkippedResponse(new Dictionary<string, int>(), []);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            Dictionary<string, int> counts = [];
            if (doc.RootElement.TryGetProperty("counts", out JsonElement c) && c.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in c.EnumerateObject())
                    if (p.Value.TryGetInt32(out int n)) counts[p.Name] = n;
            }

            List<ArchiveUnpackSkippedSample> samples = [];
            if (doc.RootElement.TryGetProperty("samples", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in s.EnumerateArray().Take(ProjectArchiveUnpack.MaxSkippedSamples))
                {
                    string? path = item.TryGetProperty("path", out JsonElement pe) ? pe.GetString() : null;
                    string? reason = item.TryGetProperty("reason", out JsonElement re) ? re.GetString() : null;
                    if (path is not null && reason is not null) samples.Add(new ArchiveUnpackSkippedSample(path, reason));
                }
            }

            return new ArchiveUnpackSkippedResponse(counts, samples);
        }
        catch (JsonException)
        {
            return new ArchiveUnpackSkippedResponse(new Dictionary<string, int>(), []);
        }
    }
}
