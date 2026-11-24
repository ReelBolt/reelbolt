using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// Makes small WebP previews of image outputs so a gallery does not pull 7 MB PNGs as thumbnails.
/// Every failure answers <c>null</c> (the caller serves the original); nothing here throws.
/// </summary>
public static class ImageThumbnailer
{
    public static readonly int[] Widths = [160, 320, 480, 640, 960, 1280, 1920];
    public const long MaxSourceBytes = 40L * 1024 * 1024;
    public const long MaxSourcePixels = 36L * 1000 * 1000;

    /// <summary>
    /// At most this many fetch-and-decode jobs run at once per process. A decode holds the source bytes plus the full
    /// RGBA bitmap (width x height x 4, up to about 144 MB at <see cref="MaxSourcePixels"/>), so the bound is what
    /// keeps a gallery of large pictures from multiplying that. A request that cannot get a slot within
    /// <see cref="SlotWait"/> is served the original instead.
    /// </summary>
    public const int MaxConcurrentJobs = 2;
    public static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(3);
    private static readonly SemaphoreSlim Slots = new(MaxConcurrentJobs, MaxConcurrentJobs);

    public static Task<bool> WaitForSlotAsync(CancellationToken ct) => Slots.WaitAsync(SlotWait, ct);
    public static void ReleaseSlot() => Slots.Release();
    public const int WebpQuality = 80;
    public const long CacheLimitBytes = 64L * 1024 * 1024;

    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = CacheLimitBytes });

    /// <summary>Snaps a requested width up to the next allowed one; null when absent, invalid or above the largest.</summary>
    public static int? SnapWidth(int? requested)
    {
        if (requested is not > 0) return null;
        foreach (int w in Widths)
            if (requested <= w) return w;
        return null;
    }

    /// <summary>True for the extensions a thumbnail can be made from.</summary>
    public static bool IsThumbnailable(string contentType) =>
        contentType is "image/png" or "image/jpeg" or "image/webp";

    public static bool TryGetCached(string bucket, string key, string etag, int width, out byte[]? bytes)
    {
        bool hit = Cache.TryGetValue(CacheKey(bucket, key, etag, width), out byte[]? found);
        bytes = found;
        return hit && found is not null;
    }

    /// <summary>Keeps a preview read back from storage, so the next request skips even that read.</summary>
    public static void Remember(string bucket, string key, string etag, int width, byte[] webp) =>
        Cache.Set(CacheKey(bucket, key, etag, width), webp, new MemoryCacheEntryOptions { Size = webp.Length, SlidingExpiration = TimeSpan.FromHours(1) });

    /// <summary>True when an earlier attempt decided this object gets no preview (serve the original, no decode).</summary>
    public static bool IsKnownUnthumbnailable(string bucket, string key, string etag, int width) =>
        Cache.TryGetValue(CacheKey(bucket, key, etag, width), out byte[]? found) && found is { Length: 0 };

    private static void RememberNegative(string cacheKey) =>
        Cache.Set(cacheKey, Array.Empty<byte>(), new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });

    /// <summary>
    /// Returns WebP bytes at <paramref name="width"/>, or null when the source is too large, already no wider
    /// than the target, undecodable, or anything else goes wrong.
    /// </summary>
    public static byte[]? Create(string bucket, string key, string etag, int width, byte[] source)
    {
        try
        {
            if (source.LongLength > MaxSourceBytes) return null;
            string cacheKey = CacheKey(bucket, key, etag, width);
            if (Cache.TryGetValue(cacheKey, out byte[]? cached) && cached is not null) return cached.Length == 0 ? null : cached;
            using SKCodec? codec = SKCodec.Create(new MemoryStream(source));
            if (codec is null) { RememberNegative(cacheKey); return null; }
            SKImageInfo info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels || info.Width <= width)
            {
                RememberNegative(cacheKey);
                return null;
            }
            using SKBitmap? full = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (full is null) { RememberNegative(cacheKey); return null; }
            int height = Math.Max(1, (int)Math.Round((double)info.Height * width / info.Width));
            using SKBitmap small = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (!full.ScalePixels(small, new SKSamplingOptions(SKCubicResampler.Mitchell))) return null;
            using SKImage image = SKImage.FromBitmap(small);
            using SKData? data = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
            if (data is null) return null;
            byte[] result = data.ToArray();
            if (result.Length >= source.Length) { RememberNegative(cacheKey); return null; }
            Cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = result.Length, SlidingExpiration = TimeSpan.FromHours(1) });
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CacheKey(string bucket, string key, string etag, int width) => $"{bucket}|{key}|{etag}|{width}";
}
