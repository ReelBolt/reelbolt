using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Serves the brand assets a design-system import stored under
/// <c>design-system/assets/</c>. Deliberately separate from the generic file media route:
/// an SVG leaves here only after the sanitizer rebuilt it again on read (defense in depth),
/// under a content policy that cannot run script, while a raster is served inline and a font
/// is an opaque attachment.
///
/// <para>
/// Every response is a project-scoped, same-project, non-quarantined projection of
/// <c>design-system/assets/</c>; anything else is a 404.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/design-system/assets")]
[Authorize]
public class DesignSystemAssetController : ControllerBase
{
    /// <summary>The route only owns files a design-system import wrote under this prefix.</summary>
    public const string AssetPrefix = "design-system/assets/";

    /// <summary>The content policy an SVG leaves under: inert, sandboxed, no subresources.</summary>
    public const string SvgContentSecurityPolicy = "default-src 'none'; sandbox";

    /// <summary>The largest object this route will buffer, in bytes.</summary>
    private const long MaxAssetBytes = 4L * 1024 * 1024;

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _fileStorage;

    public DesignSystemAssetController(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IFileStorageService fileStorage,
        ILogger<DesignSystemAssetController> logger)
    {
        _db = db;
        _projectAccess = projectAccess;
        _fileStorage = fileStorage;
        _ = logger;
    }

    /// <summary>
    /// Reads one design-system asset of <paramref name="projectId"/> at <paramref name="fileId"/>.
    /// SVG is re-sanitized (404 when the rebuilt document is not usable); a raster is inline with its
    /// stored MIME and a font is <c>application/octet-stream</c> as an attachment. Unknown types 404.
    /// </summary>
    [HttpGet("{fileId:guid}")]
    public async Task<IActionResult> Get(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied)
            return denied;

        ProjectFile? file = await _db.ProjectFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProjectId == projectId, ct);
        if (file is null)
            return NotFound();

        if (file.CsamStatus == CsamScanStatus.Quarantined
            || file.ModerationStatus == FileModerationStatus.Quarantined)
        {
            return NotFound();
        }

        string originalPath = (file.OriginalPath ?? string.Empty).Replace('\\', '/').TrimStart('/');
        if (!originalPath.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        byte[] bytes;
        await using (Stream stream = await _fileStorage.DownloadAsync(projectId, file.StorageKey, ct))
        {
            bytes = await ReadBoundedAsync(stream, MaxAssetBytes, ct);
        }

        if (bytes.LongLength > MaxAssetBytes)
            return NotFound();

        string fileName = SafeContentPolicy.SanitizeFileName(file.OriginalFileName);
        string storedMime = (file.MimeType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        ServedContent served = SafeContentPolicy.Decide(storedMime, fileName);

        if (storedMime == "image/svg+xml")
        {
            // Defense in depth: the stored bytes were sanitized at import, but a future writer could
            // bypass that; an SVG is rebuilt again here and an unusable result is simply not served.
            SvgSanitizeResult sanitized = SvgSanitizer.Sanitize(bytes);
            if (!sanitized.Ok || sanitized.Sanitized is null)
                return NotFound();

            SafeContentPolicy.ApplySecurityHeaders(Response);
            Response.Headers["Content-Security-Policy"] = SvgContentSecurityPolicy;
            Response.Headers["Content-Disposition"] =
                SafeContentPolicy.BuildContentDisposition(inline: true, fileName);
            return File(Encoding.UTF8.GetBytes(sanitized.Sanitized), "image/svg+xml; charset=utf-8");
        }

        if (storedMime is "image/png" or "image/jpeg" or "image/webp")
        {
            SafeContentPolicy.ApplySecurityHeaders(Response);
            Response.Headers["Content-Disposition"] =
                SafeContentPolicy.BuildContentDisposition(served.Inline, fileName);
            return File(bytes, served.ContentType);
        }

        if (storedMime is "font/woff" or "font/woff2" or "font/ttf" or "font/otf")
        {
            SafeContentPolicy.ApplySecurityHeaders(Response);
            Response.Headers["Content-Disposition"] =
                SafeContentPolicy.BuildContentDisposition(inline: false, fileName);
            return File(bytes, served.ContentType);
        }

        return NotFound();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        const int chunkSize = 81920;
        byte[] chunk = new byte[chunkSize];
        using var buffered = new MemoryStream();
        long total = 0;

        while (total <= maxBytes)
        {
            int want = (int)Math.Min(chunkSize, maxBytes + 1 - total);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read <= 0)
                break;

            buffered.Write(chunk, 0, read);
            total += read;
        }

        return buffered.ToArray();
    }
}
