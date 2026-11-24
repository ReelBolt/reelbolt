using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// Streams an S3 object with HTTP Range support (206 Partial Content).
/// <c>File(stream, ..., enableRangeProcessing: true)</c> silently does nothing for S3's
/// non-seekable response stream, and without ranges iOS Safari refuses to play a video at all and
/// every browser loses seeking. Here the range is pushed down to S3 as a <c>ByteRange</c>.
/// </summary>
public static class RangedObjectStreamer
{
    public enum RangeKind { None, Satisfiable, Unsatisfiable }

    /// <summary>
    /// The most an OPEN-ENDED range (<c>bytes=N-</c>) is answered with. A media element asks for
    /// <c>bytes=0-</c> and reads only what it needs; answering with the whole object (a 21 MB render)
    /// leaves a response the browser has stopped reading still open. Firefox does exactly that with
    /// a video it is only previewing, and over HTTP/3 every stream shares one connection-level flow
    /// control window, so a few paused renders exhausted it and every later request on the
    /// connection hung, never reaching the origin. The <c>Content-Range</c> says what was sent and
    /// players request the next span themselves (RFC 9110 §14.4), so playback and seeking are
    /// unchanged. Cloudflare also no longer pulls the whole object from the origin per preview.
    /// An explicit <c>bytes=N-M</c> range is honoured as asked.
    /// </summary>
    public const long OpenEndedChunkBytes = 1024 * 1024;

    /// <summary>
    /// Resolves a single-range <c>Range: bytes=...</c> header against an object of <paramref name="length"/> bytes.
    /// Multi-range requests are served as the full object (legal: a server may ignore Range).
    /// </summary>
    public static (RangeKind Kind, long Start, long End) ParseRange(string? header, long length)
    {
        if (string.IsNullOrWhiteSpace(header)) return (RangeKind.None, 0, length - 1);
        if (!RangeHeaderValue.TryParse(header, out RangeHeaderValue? range) ||
            !string.Equals(range.Unit.Value, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.Ranges.Count != 1)
        {
            return (RangeKind.None, 0, length - 1);
        }

        RangeItemHeaderValue item = range.Ranges.First();
        if (length <= 0) return (RangeKind.Unsatisfiable, 0, 0);

        long start;
        long end;
        if (item.From is null)
        {
            // Suffix range: the last N bytes.
            if (item.To is null or <= 0) return (RangeKind.Unsatisfiable, 0, 0);
            start = Math.Max(0, length - item.To.Value);
            end = length - 1;
        }
        else
        {
            start = item.From.Value;
            end = item.To is null
                ? Math.Min(length - 1, start + OpenEndedChunkBytes - 1)
                : Math.Min(item.To.Value, length - 1);
        }

        if (start >= length || start > end) return (RangeKind.Unsatisfiable, 0, 0);
        return (RangeKind.Satisfiable, start, end);
    }

    public static async Task<IActionResult> StreamAsync(
        ControllerBase controller,
        IAmazonS3 s3,
        string bucket,
        string key,
        string contentType,
        string fileName,
        CancellationToken ct,
        string? saveAs = null,
        bool imageCaching = false,
        int? thumbnailWidth = null,
        Guid? projectId = null)
    {
        HttpResponse response = controller.Response;
        GetObjectMetadataResponse meta = await s3.GetObjectMetadataAsync(bucket, key, ct);
        long length = meta.ContentLength;
        string? plainTag = null;
        string? thumbTag = null;
        int? width = null;
        bool hasRange = !string.IsNullOrWhiteSpace(controller.Request.Headers[HeaderNames.Range].ToString());
        if (imageCaching)
        {
            string etag = string.IsNullOrEmpty(meta.ETag) ? $"len{length}" : meta.ETag.Trim('"');
            plainTag = $"\"{etag}\"";
            if (thumbnailWidth is { } tw && saveAs is null && !hasRange)
            {
                width = tw;
                thumbTag = $"\"{etag}-w{tw}\"";
            }
            if (!hasRange)
            {
                string? matched = MatchingTag(controller.Request.Headers[HeaderNames.IfNoneMatch].ToString(), thumbTag, plainTag);
                if (matched is not null)
                {
                    SafeContentPolicy.ApplySecurityHeaders(response);
                    response.Headers[HeaderNames.ETag] = matched;
                    response.Headers[HeaderNames.CacheControl] = matched == thumbTag ? ThumbnailCacheControl : ImageCacheControl;
                    return controller.StatusCode(StatusCodes.Status304NotModified);
                }
            }
        }

        ServedContent served = SafeContentPolicy.Decide(contentType, fileName);
        SafeContentPolicy.ApplySecurityHeaders(response);

        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        // `saveAs` is a download request: always an attachment, under the caller's chosen name (sanitized,
        // keeping the stored object's extension). Playback never sets it, so inline streaming is unchanged.
        response.Headers[HeaderNames.ContentDisposition] = saveAs is null
            ? SafeContentPolicy.BuildContentDisposition(served.Inline, fileName)
            : SafeContentPolicy.BuildContentDisposition(false, SafeContentPolicy.WithExtensionOf(saveAs, fileName));

        if (imageCaching)
        {
            response.Headers[HeaderNames.ETag] = plainTag;
            response.Headers[HeaderNames.CacheControl] = ImageCacheControl;
        }
        if (width is { } targetWidth)
        {
            IActionResult? thumb = await TryThumbnailAsync(controller, s3, bucket, key, fileName, meta, plainTag!, thumbTag!, targetWidth, ct, projectId);
            if (thumb is not null) return thumb;
        }
        (RangeKind kind, long start, long end) = ParseRange(controller.Request.Headers[HeaderNames.Range].ToString(), length);

        if (kind == RangeKind.Unsatisfiable)
        {
            response.Headers[HeaderNames.ContentRange] = $"bytes */{length}";
            return controller.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        GetObjectRequest request = new() { BucketName = bucket, Key = key };
        if (kind == RangeKind.Satisfiable)
        {
            request.ByteRange = new ByteRange(start, end);
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers[HeaderNames.ContentRange] = $"bytes {start}-{end}/{length}";
            response.ContentLength = end - start + 1;
        }
        else
        {
            response.ContentLength = length;
        }

        GetObjectResponse obj = await s3.GetObjectAsync(request, ct);
        return new FileStreamResult(obj.ResponseStream, served.ContentType);
    }

    public const string ImageCacheControl = "private, max-age=3600";
    public const string ThumbnailCacheControl = "private, max-age=86400";

    private static string? MatchingTag(string header, string? thumbTag, string plainTag)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string[] tags = header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string Strip(string t) => t.StartsWith("W/", StringComparison.Ordinal) ? t[2..] : t;
        if (thumbTag is not null && tags.Any(t => Strip(t) == thumbTag)) return thumbTag;
        return tags.Any(t => Strip(t) == plainTag || t == "*") ? plainTag : null;
    }

    /// <summary>
    /// Serves a WebP preview, or (after the body has been read) the original bytes when the source is too big,
    /// already narrow enough or undecodable. Null means nothing was read and the caller streams the original.
    /// </summary>
    private static async Task<IActionResult?> TryThumbnailAsync(
        ControllerBase controller, IAmazonS3 s3, string bucket, string key, string fileName,
        GetObjectMetadataResponse meta, string plainTag, string thumbTag, int width, CancellationToken ct, Guid? projectId = null)
    {
        if (meta.ContentLength <= 0 || meta.ContentLength > ImageThumbnailer.MaxSourceBytes) return null;
        HttpResponse response = controller.Response;
        string etag = plainTag.Trim('"');
        byte[]? bytes;
        byte[]? original = null;
        if (ImageThumbnailer.IsKnownUnthumbnailable(bucket, key, etag, width)) return null;
        if (!ImageThumbnailer.TryGetCached(bucket, key, etag, width, out bytes)
            && projectId is Guid pid && !string.IsNullOrEmpty(meta.ETag)
            && await ImagePreviewStore.TryGetAsync(s3, bucket, pid, key, etag, width, ct) is { } stored)
        {
            // Stored preview: no source read, no decode, no slot. Only reached after the source's metadata call succeeded.
            bytes = stored;
            ImageThumbnailer.Remember(bucket, key, etag, width, stored);
        }
        else if (bytes is null)
        {
            // Bounded: fetch + decode holds the source and a full bitmap, so only a few run at once; a request that
            // waits too long is simply served the original (nothing has been read yet, so the caller streams it).
            if (!await ImageThumbnailer.WaitForSlotAsync(ct)) return null;
            try
            {
                GetObjectResponse obj = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
                using (obj)
                {
                    using MemoryStream buffer = new();
                    await obj.ResponseStream.CopyToAsync(buffer, ct);
                    original = buffer.ToArray();
                }
                bytes = await Task.Run(() => ImageThumbnailer.Create(bucket, key, etag, width, original), ct);
                if (bytes is not null && projectId is Guid putProject && !string.IsNullOrEmpty(meta.ETag))
                {
                    await ImagePreviewStore.PutAsync(s3, bucket, putProject, key, etag, width, bytes, ct);
                    // The source may have been deleted, moved or replaced while this ran: then what was just written goes.
                    await ImagePreviewStore.KeepOrRemoveAsync(s3, bucket, putProject, key, etag, null, ct);
                }
            }
            finally
            {
                ImageThumbnailer.ReleaseSlot();
            }
        }
        SafeContentPolicy.ApplySecurityHeaders(response);
        response.Headers[HeaderNames.AcceptRanges] = "bytes";
        if (bytes is null)
        {
            ServedContent servedOriginal = SafeContentPolicy.Decide(Path.GetExtension(fileName) switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            }, fileName);
            response.Headers[HeaderNames.ContentDisposition] = SafeContentPolicy.BuildContentDisposition(servedOriginal.Inline, fileName);
            response.ContentLength = original!.Length;
            return new FileStreamResult(new MemoryStream(original), servedOriginal.ContentType);
        }
        response.Headers[HeaderNames.ETag] = thumbTag;
        response.Headers[HeaderNames.CacheControl] = ThumbnailCacheControl;
        string thumbName = Path.ChangeExtension(fileName, ".webp");
        response.Headers[HeaderNames.ContentDisposition] = SafeContentPolicy.BuildContentDisposition(
            SafeContentPolicy.Decide("image/webp", thumbName).Inline, thumbName);
        response.ContentLength = bytes.Length;
        return new FileStreamResult(new MemoryStream(bytes), "image/webp");
    }
}
