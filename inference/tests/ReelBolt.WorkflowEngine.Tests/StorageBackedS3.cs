using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Moq;
using ReelBolt.Inference.Api.Services.Storage;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// An <see cref="IAmazonS3"/> double whose objects ARE whatever a mocked
/// <see cref="IFileStorageService"/> serves for the same key, honouring <c>ByteRange</c> the way
/// S3 does. Lets a controller that streams through <see cref="RangedObjectStreamer"/> be tested
/// with the same per-key storage setups (and the same "object is gone" exceptions) its other
/// routes already use, so a test sets an object up once.
/// </summary>
internal static class StorageBackedS3
{
    public static Mock<IAmazonS3> Over(Mock<IFileStorageService> storage)
    {
        Mock<IAmazonS3> s3 = new();

        s3.Setup(c => c.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string bucket, string key, CancellationToken ct) =>
            {
                byte[] bytes = await ReadAsync(storage, key, ct);
                return new GetObjectMetadataResponse { ContentLength = bytes.LongLength };
            });

        s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (GetObjectRequest request, CancellationToken ct) =>
            {
                byte[] bytes = await ReadAsync(storage, request.Key, ct);
                if (request.ByteRange is { } range)
                {
                    long end = Math.Min(range.End, bytes.LongLength - 1);
                    bytes = bytes.AsSpan((int)range.Start, (int)(end - range.Start + 1)).ToArray();
                }

                return new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ContentLength = bytes.LongLength };
            });

        return s3;
    }

    private static async Task<byte[]> ReadAsync(Mock<IFileStorageService> storage, string key, CancellationToken ct)
    {
        // Keys are "projects/{projectId}/...", exactly what the storage service is addressed by.
        Guid projectId = Guid.Parse(key.Split('/')[1]);
        // Not disposed, and rewound: a test that set one MemoryStream up for a key is read twice
        // here (HEAD, then GET), exactly as the real store would serve the object twice.
        Stream stream = await storage.Object.DownloadAsync(projectId, key, ct)
            ?? throw new AmazonS3Exception("no object") { StatusCode = System.Net.HttpStatusCode.NotFound };
        if (stream.CanSeek) stream.Position = 0;
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy, ct);
        return copy.ToArray();
    }
}
