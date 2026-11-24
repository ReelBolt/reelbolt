using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <summary>Renders and normalises sound with ffmpeg. Both answer null when ffmpeg is not available (never an error).</summary>
public interface ISfxRenderer
{
    /// <summary>Renders one synthesized recipe to a WAV, or null when ffmpeg is missing or failed.</summary>
    Task<byte[]?> RenderAsync(SfxRecipe recipe, CancellationToken ct);

    /// <summary>Trims silence, loudness-normalises and re-encodes a downloaded pack file to the library format, or null.</summary>
    Task<byte[]?> NormalizeAsync(byte[] source, CancellationToken ct);
}

/// <summary>The object store for library bytes, confined to <see cref="ReelBolt.Shared.Data.Models.SfxLibraryKeys.Prefix"/>.</summary>
public interface ISfxLibraryStorage
{
    Task<bool> ExistsAsync(string key, CancellationToken ct);

    Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken ct);

    /// <summary>The object's bytes (library files are small, below <see cref="MaxObjectBytes"/>), or null when absent.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);

    const int MaxObjectBytes = 4 * 1024 * 1024;
}

/// <summary>Downloads a pack file for the seeder. Https only, size-bounded; null on any failure.</summary>
public interface ISfxPackFetcher
{
    Task<byte[]?> FetchAsync(Uri url, CancellationToken ct);
}

/// <inheritdoc />
public sealed class S3SfxLibraryStorage(IAmazonS3 s3, IConfiguration configuration) : ISfxLibraryStorage
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        Guard(key);
        try
        {
            await s3.GetObjectMetadataAsync(_bucket, key, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken ct)
    {
        Guard(key);
        using MemoryStream ms = new(bytes, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = ms, ContentType = contentType }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        Guard(key);
        try
        {
            using GetObjectResponse response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
            if (response.ContentLength > ISfxLibraryStorage.MaxObjectBytes) return null;
            using MemoryStream ms = new();
            await response.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void Guard(string key)
    {
        if (!ReelBolt.Shared.Data.Models.SfxLibraryKeys.IsLibraryKey(key))
            throw new InvalidOperationException("The sound library may only touch its own storage prefix.");
    }
}

/// <inheritdoc />
public sealed class HttpSfxPackFetcher(IHttpClientFactory clients) : ISfxPackFetcher
{
    public const string HttpClientName = "sfx-pack";
    public const long MaxBytes = 8 * 1024 * 1024;

    public async Task<byte[]?> FetchAsync(Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return null;
        try
        {
            HttpClient client = clients.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            using MemoryStream ms = new();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, read);
                if (ms.Length > MaxBytes) return null;
            }

            return ms.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Facts read from a PCM WAV header: the seeder stores duration and hash of exactly the bytes it uploads.</summary>
public static class WavFacts
{
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Duration in ms of a RIFF/WAVE PCM file, or null when the bytes are not a readable WAV.</summary>
    public static int? DurationMs(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;
        int pos = 12;
        int byteRate = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            if (id == "fmt " && pos + 20 <= wav.Length)
                byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 16, 4));
            else if (id == "data" && byteRate > 0)
            {
                long dataBytes = size < 0 || pos + 8 + (long)size > wav.Length ? wav.Length - pos - 8 : size;
                return (int)Math.Round(dataBytes * 1000.0 / byteRate);
            }

            if (size < 0) return null;
            pos += 8 + size + (size & 1);
        }

        return null;
    }
}
