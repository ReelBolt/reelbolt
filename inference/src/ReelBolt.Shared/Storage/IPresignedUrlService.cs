namespace ReelBolt.Shared.Storage;

/// <summary>
/// Mints presigned object-store URLs for runners (see docs/runner-protocol.md "Presigned URLs and
/// object storage"). A URL grants one object and one method; URLs are bearer secrets and must never
/// be logged with their query string.
/// </summary>
public interface IPresignedUrlService
{
    /// <summary>GET a project object. The key must sit under <c>projects/{projectId}/</c>; ttl is capped at 15 minutes.</summary>
    Task<string> CreateGetAsync(Guid projectId, string key, TimeSpan ttl);

    /// <summary>PUT to <c>runner-staging/{jobId}/{n}</c>; ttl is capped at 30 minutes.</summary>
    Task<PresignedPut> CreatePutAsync(Guid jobId, int n, string contentType, TimeSpan ttl);

    /// <summary>
    /// PUT a browser upload straight to an object's FINAL key, which must sit under
    /// <c>projects/{projectId}/</c>. This is deliberately a separate member from
    /// <see cref="CreatePutAsync"/> rather than a reuse of it: the runner's space is
    /// <c>runner-staging/</c> and its objects are promoted server-side afterwards, while a browser
    /// upload lands on the real key because no server ever sees the bytes and there is nothing to
    /// promote. The key is still the SERVER's to choose (the project prefix is enforced here) and
    /// ttl is capped at <see cref="PresignedUrlService.MaxBrowserUploadPutTtl"/>, which is far
    /// longer than the runner's 30 minutes because it has to cover a slow uplink, not a transfer
    /// between two machines inside a datacentre.
    /// </summary>
    Task<PresignedPut> CreateBrowserUploadPutAsync(Guid projectId, string key, string contentType, TimeSpan ttl);

    /// <summary>
    /// HEAD the staging object, reject it (and delete it) when larger than <paramref name="expectedMaxBytes"/>,
    /// otherwise copy it to <paramref name="finalKey"/> and delete the staging object. Returns the byte size.
    /// </summary>
    Task<long> PromoteAsync(string stagingKey, string finalKey, long expectedMaxBytes, CancellationToken cancellationToken = default);
}

/// <summary>The URL plus the object key it targets, so the engine can later HEAD/promote it.</summary>
public sealed record PresignedPut(string Url, string Key);

public sealed class PresignedObjectTooLargeException(string key, long actualBytes, long maxBytes)
    : InvalidOperationException($"Staged object '{key}' is {actualBytes} bytes, over the {maxBytes}-byte cap.")
{
    public long ActualBytes { get; } = actualBytes;
    public long MaxBytes { get; } = maxBytes;
}
