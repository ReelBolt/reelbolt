using Amazon.S3;
using System.Net;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// Which object-store failures are transient. A delete whose rows are already committed may keep its
/// durable deletion mark and let the retention sweep retry only for these; anything else — a scope
/// violation, a programming error, a cancellation — must surface rather than be swallowed behind a
/// success the caller was told about.
/// </summary>
public static class StorageFailure
{
    /// <summary>
    /// True for a provider error worth retrying: an S3 5xx or throttling answer, or a transport failure.
    /// <see cref="UnauthorizedAccessException"/> (a key outside the project's scope) is deliberately not
    /// transient: it means the call was wrong, and the sweep would fail the same way.
    /// </summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        AmazonS3Exception s3 => s3.StatusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests,
        HttpRequestException => true,
        IOException => true,
        _ => false,
    };
}
