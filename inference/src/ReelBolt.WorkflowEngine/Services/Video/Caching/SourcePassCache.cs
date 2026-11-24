using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video.Caching;

/// <summary>
/// One stored source as the pass cache knows it: the object and the version of its bytes. A source with no
/// version has no identity, and nothing is cached for it.
/// </summary>
/// <param name="ProjectId">The project the object belongs to. Every cache entry lives under this project.</param>
/// <param name="StorageKey">The object's storage key.</param>
/// <param name="Version">The object's version as the store reports it (its ETag).</param>
public sealed record SourcePassIdentity(Guid ProjectId, string StorageKey, string Version);

/// <summary>Comparing two reads of one source's identity.</summary>
public static class SourcePassIdentityCheck
{
    /// <summary>
    /// <paramref name="before"/> when <paramref name="after"/> names the same object with the same version, else
    /// null. Bytes read between two reads that agree are bytes of that version; when they differ, or the second
    /// read gave nothing, the bytes have no known version and nothing is cached for them.
    /// </summary>
    public static SourcePassIdentity? Unchanged(SourcePassIdentity? before, SourcePassIdentity? after) =>
        before is not null && after is not null &&
        before.ProjectId == after.ProjectId &&
        string.Equals(before.StorageKey, after.StorageKey, StringComparison.Ordinal) &&
        string.Equals(before.Version, after.Version, StringComparison.Ordinal)
            ? before
            : null;
}

/// <summary>
/// Keeps what one analysis pass found on one stored source, so the next step that asks the same question of
/// the same bytes does not decode the video again. A silence, shot or event entry holds times and numbers. A
/// frame grid entry holds scaled-down pictures of the source (its sampled frames at the grid's size: 32x18 by
/// default for the visual grid, up to 640x360 for the insert grid), so it is content of the source and is kept
/// no longer than the retention sweep allows. Every call fails open: a read that goes wrong is a miss and a
/// write that goes wrong is skipped.
/// </summary>
public interface ISourcePassCache
{
    /// <summary>The identity of a stored source, or null when it has none (the cache is off, or the store gave no version).</summary>
    Task<SourcePassIdentity?> IdentifyAsync(Guid projectId, string storageKey, CancellationToken ct);

    /// <summary>The stored payload of <paramref name="entryKey"/>, or null when there is none or it could not be read.</summary>
    Task<byte[]?> TryReadAsync(SourcePassIdentity source, string entryKey, CancellationToken ct);

    /// <summary>Stores <paramref name="payload"/> under <paramref name="entryKey"/>. A payload over the size limit is not stored.</summary>
    Task WriteAsync(SourcePassIdentity source, string entryKey, byte[] payload, CancellationToken ct);
}

/// <summary>
/// The default pass cache: object storage is the cache, under <c>projects/{projectId}/agentFiles/analysis-cache/</c>.
/// Entries have no <c>project_files</c> row, like segmentation masks, and do not count toward the storage quota.
/// An entry of a source that changed, was deleted, or of an older analyzer revision is never read again. Nothing
/// in the engine removes entries, and nothing bounds how many a project has. The API's storage retention run
/// (where it is switched on) removes them: by age for a project that exists (only keys of the shape
/// <see cref="EntryKey"/> makes, which its sweep checks), and through the marks a project delete writes for
/// every entry of the deleted project. An account purge deletes the whole project prefix. Deleting a file does
/// not delete its entries: the entry name is a hash and does not say which file it belongs to. With the
/// retention run off, entries stay until the prefix is deleted.
/// </summary>
public sealed class SourcePassCache(
    IProjectFileWorkspace files,
    IOptions<VideoEditingOptions> options,
    ILogger<SourcePassCache> logger) : ISourcePassCache
{
    /// <summary>Where every entry lives, relative to the project's own prefix.</summary>
    public const string KeyPrefix = "agentFiles/analysis-cache/";

    /// <summary>The largest stored payload. A larger one is not written, and is refused on read.</summary>
    public const long MaxPayloadBytes = 64L * 1024 * 1024;

    /// <summary>Content type of an entry.</summary>
    public const string ContentType = "application/octet-stream";

    /// <summary>Subdirectory of <see cref="VideoEditingOptions.ScratchPath"/> an entry passes through on its way in or out.</summary>
    public const string ScratchSubdirectory = "pass-cache";

    private readonly VideoEditingOptions _options = options.Value;

    /// <summary>
    /// The storage key of one entry: SHA-256 over the length-prefixed source key, source version, pass name, pass
    /// revision, parameter string and the VideoAnalyze revision tag. Any change to one of them is another entry,
    /// so a new analyzer revision reads nothing an older one wrote.
    /// </summary>
    public static string EntryKey(SourcePassIdentity source, string pass, string passRevision, string parameters)
    {
        ArgumentNullException.ThrowIfNull(source);

        StringBuilder material = new();
        Append(material, source.StorageKey);
        Append(material, source.Version);
        Append(material, pass);
        Append(material, passRevision);
        Append(material, parameters);
        Append(material, DeterministicStepRevisions.For(StepType.VideoAnalyze));

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
        return FormattableString.Invariant($"projects/{source.ProjectId:D}/{KeyPrefix}{hash}.bin");
    }

    /// <summary>True when <paramref name="entryKey"/> is an entry of this project under the cache's own prefix.</summary>
    public static bool IsEntryKey(Guid projectId, string? entryKey) =>
        !string.IsNullOrEmpty(entryKey)
        && !entryKey.Contains("..", StringComparison.Ordinal)
        && entryKey.StartsWith(FormattableString.Invariant($"projects/{projectId:D}/{KeyPrefix}"), StringComparison.Ordinal);

    public async Task<SourcePassIdentity?> IdentifyAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        if (!_options.SourcePassCache.Enabled || string.IsNullOrEmpty(storageKey))
            return null;

        try
        {
            string? version = await files.GetObjectVersionAsync(projectId, storageKey, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(version) ? null : new SourcePassIdentity(projectId, storageKey, version);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Pass cache: the version of {StorageKey} could not be read; nothing is cached for it", storageKey);
            return null;
        }
    }

    public async Task<byte[]?> TryReadAsync(SourcePassIdentity source, string entryKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_options.SourcePassCache.Enabled || !IsEntryKey(source.ProjectId, entryKey))
            return null;

        string? temp = null;
        try
        {
            if (!await files.ArtifactExistsAsync(source.ProjectId, entryKey, ct).ConfigureAwait(false))
                return null;

            temp = NewScratchFile();
            await files.DownloadStorageKeyToFileAsync(source.ProjectId, entryKey, temp, MaxPayloadBytes, ct).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(temp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Pass cache: entry {EntryKey} could not be read; treating as a miss", entryKey);
            return null;
        }
        finally
        {
            Remove(temp);
        }
    }

    public async Task WriteAsync(SourcePassIdentity source, string entryKey, byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(payload);
        if (!_options.SourcePassCache.Enabled || !IsEntryKey(source.ProjectId, entryKey))
            return;

        if (payload.LongLength > MaxPayloadBytes)
        {
            logger.LogDebug("Pass cache: entry {EntryKey} is {Bytes} bytes, over the limit; not stored", entryKey, payload.LongLength);
            return;
        }

        string? temp = null;
        try
        {
            temp = NewScratchFile();
            await File.WriteAllBytesAsync(temp, payload, ct).ConfigureAwait(false);

            string prefix = FormattableString.Invariant($"projects/{source.ProjectId:D}/agentFiles/");
            await files
                .UploadArtifactAsync(source.ProjectId, temp, entryKey[prefix.Length..], ContentType, ct, "agentFiles")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pass cache: entry {EntryKey} could not be stored; continuing without it", entryKey);
        }
        finally
        {
            Remove(temp);
        }
    }

    private string NewScratchFile()
    {
        string directory = Path.Combine(Path.GetFullPath(_options.ScratchPath), ScratchSubdirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
    }

    private void Remove(string? path)
    {
        if (path is null)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Pass cache: scratch file {Path} could not be removed", path);
        }
    }

    private static void Append(StringBuilder material, string value)
    {
        // Length-prefixed, so no two different component lists can concatenate to the same string.
        material.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
    }
}
