using Amazon.S3;
using Amazon.S3.Model;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Services.Fonts;

/// <summary>The object-store cache for verified font files, keyed by SHA-256 under <c>font-cache/</c>.</summary>
public sealed partial class S3FontBlobCache(IAmazonS3 s3, IConfiguration configuration) : IFontBlobCache
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    private static string KeyFor(string sha256) =>
        HashPattern().IsMatch(sha256) ? $"font-cache/{sha256}" : throw new ArgumentException("Not a SHA-256 hex digest.", nameof(sha256));

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string sha256, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using GetObjectResponse response = await s3.GetObjectAsync(_bucket, KeyFor(sha256), deadline.Token);
            if (response.ContentLength > GoogleFontFetcher.MaxBytes)
                return null;
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await response.ResponseStream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > GoogleFontFetcher.MaxBytes)
                    return null;
            }
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task PutAsync(string sha256, byte[] bytes, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(bytes);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = KeyFor(sha256), InputStream = body }, cancellationToken);
    }
}
