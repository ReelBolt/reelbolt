using ReelBolt.Inference.Api.Services.Auth;
using Amazon.S3;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using System.Net;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Inline, Range-aware playback of a project file (video/audio/image preview in the file manager).
/// Separate from the attachment-style <c>/download</c> so previews can seek and play on iOS Safari.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/files/{fileId:guid}/media")]
[Authorize]
public class ProjectFileMediaController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IAmazonS3 _s3;
    private readonly string _defaultBucket;
    private readonly PublishModeration? _publishModeration;

    public ProjectFileMediaController(InferenceApiDbContext db, ICurrentUser currentUser, IProjectAccessService projectAccess, IAmazonS3 s3, IConfiguration configuration, PublishModeration? publishModeration = null)
    {
        _publishModeration = publishModeration;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _s3 = s3;
        _defaultBucket = configuration["MinIO:BucketName"] ?? "reelforge";
    }

    [HttpGet]
    public async Task<IActionResult> Stream(Guid projectId, Guid fileId, CancellationToken ct, [FromQuery] string? w = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectFile? file = await _db.ProjectFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file == null) return NotFound();
        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined) return NotFound();
        if (await Services.Outputs.OutputGate.IsFileHeldAsync(_db, file, ct)) return Services.Outputs.OutputGate.Unavailable();

        // Only the project's own objects: the key is whatever the upload recorded, but never trust it blindly.
        if (!file.StorageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal)) return Forbid();

        // The file may share the key of a step output: then the stored verdict decides, as for download.
        if (_publishModeration is not null
            && await _publishModeration.GateStoredFileAsync(projectId, file.StorageKey, ct) is { } refused)
            return refused.ToRefusal(this);

        string bucket = string.IsNullOrWhiteSpace(file.StorageBucket) ? _defaultBucket : file.StorageBucket;
        string contentType = string.IsNullOrWhiteSpace(file.MimeType) ? "application/octet-stream" : file.MimeType;

        try
        {
            // Pictures also get ETag/Cache-Control and an optional `w` WebP preview; everything else is unchanged.
            string mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
            bool previewable = ImageThumbnailer.IsThumbnailable(mime);
            bool isPicture = previewable || mime == "image/gif";
            return await RangedObjectStreamer.StreamAsync(
                this, _s3, bucket, file.StorageKey, contentType, file.OriginalFileName, ct, null,
                isPicture, previewable ? ImageThumbnailer.SnapWidth(int.TryParse(w, out int requestedWidth) ? requestedWidth : null) : null, projectId);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound
            || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal)
            || string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal))
        {
            return NotFound("File not found in storage.");
        }
    }
}
