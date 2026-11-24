namespace ReelBolt.WorkflowEngine.Services.Storage;

/// <summary>
/// A stored object that is larger than the caller's bound
/// (<see cref="IProjectFileWorkspace.DownloadStorageKeyToFileAsync(Guid,string,string,long,CancellationToken)"/>).
/// Thrown BEFORE the object is buffered, so a caller enforcing a cap never pays for the bytes it refuses.
///
/// <para>
/// Deliberately a distinct type: a caller maps this to its own fixed sentence (a cap is the author's
/// configuration, a storage failure is not), and must never render <see cref="Exception.Message"/> — it names a
/// size and a limit, which is server-side detail.
/// </para>
/// </summary>
public sealed class StorageObjectTooLargeException(long maxBytes, long? observedBytes)
    : Exception(observedBytes is { } observed
        ? $"The stored object is {observed} bytes; at most {maxBytes} are allowed."
        : $"The stored object exceeded the {maxBytes}-byte bound while it was being read.")
{
    /// <summary>The bound the caller asked for.</summary>
    public long MaxBytes { get; } = maxBytes;

    /// <summary>The object's own declared length, when it was already known; null when the stream passed the bound.</summary>
    public long? ObservedBytes { get; } = observedBytes;
}
