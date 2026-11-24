using Amazon.S3;
using Amazon.S3.Model;
using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// WebP previews kept in object storage next to the project, so the first view of a picture is a small read
/// instead of a decode. Key: <c>projects/{projectId}/agentFiles/previews/{first 32 hex of sha256(sourceKey)}-w{width}.webp</c>,
/// with the source object's ETag in object metadata: a preview whose ETag is not the CURRENT source's is a miss.
/// The folder is not an addressable output, so nothing serves a preview except through the gated download routes.
/// Every method is best effort and never throws.
/// </summary>
public static class ImagePreviewStore
{
    public const string EtagMetadata = "x-amz-meta-source-etag";

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static bool IsImageKey(string key) =>
        ImageExtensions.Contains(Path.GetExtension(key).ToLowerInvariant(), StringComparer.Ordinal);

    private static bool InScope(Guid projectId, string sourceKey) =>
        sourceKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal) && !sourceKey.Contains("..", StringComparison.Ordinal);

    public static string Key(Guid projectId, string sourceKey, int width)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey))).ToLowerInvariant()[..32];
        return $"projects/{projectId}/agentFiles/previews/{hash}-w{width}.webp";
    }

    public static async Task<byte[]?> TryGetAsync(
        IAmazonS3 s3, string bucket, Guid projectId, string sourceKey, string sourceEtag, int width, CancellationToken ct)
    {
        if (!InScope(projectId, sourceKey)) return null;
        try
        {
            using GetObjectResponse obj = await s3.GetObjectAsync(
                new GetObjectRequest { BucketName = bucket, Key = Key(projectId, sourceKey, width) }, ct);
            if (!string.Equals(obj.Metadata[EtagMetadata], sourceEtag, StringComparison.Ordinal)) return null;
            using MemoryStream buffer = new();
            await obj.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static async Task PutAsync(
        IAmazonS3 s3, string bucket, Guid projectId, string sourceKey, string sourceEtag, int width, byte[] webp, CancellationToken ct)
    {
        if (!InScope(projectId, sourceKey)) return;
        try
        {
            PutObjectRequest request = new()
            {
                BucketName = bucket,
                Key = Key(projectId, sourceKey, width),
                InputStream = new MemoryStream(webp),
                ContentType = "image/webp",
            };
            request.Metadata[EtagMetadata] = sourceEtag;
            await s3.PutObjectAsync(request, ct);
        }
        catch (Exception)
        {
            // Best effort: the preview is rebuilt on the next miss.
        }
    }

    /// <summary>
    /// True while the source still exists with the ETag its previews were made from. A vanished, moved or replaced
    /// source (or any error reading it) answers false, so the caller removes what it just wrote.
    /// </summary>
    public static async Task<bool> SourceStillCurrentAsync(
        IAmazonS3 s3, string bucket, string sourceKey, string sourceEtag, CancellationToken ct)
    {
        try
        {
            GetObjectMetadataResponse meta = await s3.GetObjectMetadataAsync(bucket, sourceKey, ct);
            return string.Equals((meta.ETag ?? string.Empty).Trim('"'), sourceEtag, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// After previews were written: re-checks the source (still there, same ETag) and, when given, the caller's hold
    /// check; if either fails the previews just written are deleted. Returns whether they were kept.
    /// </summary>
    public static async Task<bool> KeepOrRemoveAsync(
        IAmazonS3 s3, string bucket, Guid projectId, string sourceKey, string sourceEtag,
        Func<CancellationToken, Task<bool>>? stillAllowed, CancellationToken ct)
    {
        bool keep = await SourceStillCurrentAsync(s3, bucket, sourceKey, sourceEtag, ct);
        if (keep && stillAllowed is not null)
        {
            try { keep = await stillAllowed(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { keep = false; }
        }
        if (!keep) await DeleteAllAsync(s3, bucket, projectId, sourceKey, CancellationToken.None);
        return keep;
    }

    /// <summary>Removes every stored preview of one source object (a delete, a move or a quarantine of the source).</summary>
    public static async Task DeleteAllAsync(IAmazonS3 s3, string bucket, Guid projectId, string sourceKey, CancellationToken ct)
    {
        if (!InScope(projectId, sourceKey) || !IsImageKey(sourceKey)) return;
        try
        {
            DeleteObjectsRequest request = new() { BucketName = bucket, Quiet = true };
            foreach (int width in ImageThumbnailer.Widths)
                request.AddKey(Key(projectId, sourceKey, width));
            await s3.DeleteObjectsAsync(request, ct);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>Batch form of <see cref="DeleteAllAsync(IAmazonS3, string, Guid, string, CancellationToken)"/>.</summary>
    public static async Task DeleteAllAsync(IAmazonS3 s3, string bucket, Guid projectId, IEnumerable<string> sourceKeys, CancellationToken ct)
    {
        try
        {
            IEnumerable<string> keys = sourceKeys
                .Where(k => InScope(projectId, k) && IsImageKey(k))
                .SelectMany(k => ImageThumbnailer.Widths.Select(w => Key(projectId, k, w)));
            foreach (string[] batch in keys.Chunk(1000))
            {
                DeleteObjectsRequest request = new() { BucketName = bucket, Quiet = true };
                foreach (string key in batch) request.AddKey(key);
                await s3.DeleteObjectsAsync(request, ct);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Makes sure previews at <paramref name="widths"/> exist for a source: skips widths already stored (valid for the
    /// current ETag) or not smaller than the source, and decodes the rest under the shared slot bound.
    /// Returns how many previews were written. Never throws.
    /// </summary>
    public static async Task<int> EnsureAsync(
        IAmazonS3 s3, string bucket, Guid projectId, string sourceKey, int[] widths, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? stillAllowed = null)
    {
        try
        {
            if (!InScope(projectId, sourceKey) || !IsImageKey(sourceKey)) return 0;
            GetObjectMetadataResponse meta = await s3.GetObjectMetadataAsync(bucket, sourceKey, ct);
            string etag = (meta.ETag ?? string.Empty).Trim('"');
            if (etag.Length == 0 || meta.ContentLength <= 0 || meta.ContentLength > ImageThumbnailer.MaxSourceBytes) return 0;
            List<int> missing = [];
            foreach (int width in widths)
                if (await TryGetAsync(s3, bucket, projectId, sourceKey, etag, width, ct) is null) missing.Add(width);
            if (missing.Count == 0) return 0;
            if (!await ImageThumbnailer.WaitForSlotAsync(ct)) return 0;
            int written = 0;
            try
            {
                byte[] source;
                using (GetObjectResponse obj = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = sourceKey }, ct))
                {
                    using MemoryStream buffer = new();
                    await obj.ResponseStream.CopyToAsync(buffer, ct);
                    source = buffer.ToArray();
                }
                foreach (int width in missing)
                {
                    byte[]? webp = await Task.Run(() => ImageThumbnailer.Create(bucket, sourceKey, etag, width, source), ct);
                    if (webp is null) continue;
                    await PutAsync(s3, bucket, projectId, sourceKey, etag, width, webp, ct);
                    written++;
                }
            }
            finally
            {
                ImageThumbnailer.ReleaseSlot();
            }
            if (written > 0 && !await KeepOrRemoveAsync(s3, bucket, projectId, sourceKey, etag, stillAllowed, ct)) return 0;
            return written;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
