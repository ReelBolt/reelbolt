using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ReelBolt.Shared.Storage;

namespace ReelBolt.WorkflowEngine.Services.Compute;

/// <summary>
/// What one journal entry records.
/// </summary>
public enum SandboxJournalEntryKind
{
    /// <summary>A file was written into the workspace. A blob is stored beside the entry.</summary>
    Write = 0,

    /// <summary>A path (file, or a directory and everything under it) was removed from the workspace. No blob.</summary>
    Delete = 1
}

/// <summary>
/// One recorded workspace mutation, in the order it happened.
/// <para>
/// <see cref="Sequence"/> is the only ordering authority: the entry's object name is derived from it,
/// so a directory listing sorted by name IS the journal's order and no separate index object (which
/// would need a read-modify-write and would therefore race with a concurrent tool call) is needed.
/// </para>
/// </summary>
/// <param name="Sequence">Zero-based, monotonic within one execution.</param>
/// <param name="Path">The workspace-relative path the op named, verbatim — the runner re-validates containment, this never re-writes it.</param>
/// <param name="Kind">Write or delete.</param>
/// <param name="Sha256">Hex digest of the written bytes (null for a delete). This is what makes a hydrate able to skip a file the new workspace already has.</param>
/// <param name="Bytes">Size of the written content in bytes (0 for a delete).</param>
/// <param name="BlobKey">The object-store key the content was stored at, or null for a delete. Never a presigned URL.</param>
/// <param name="RecordedAt">When the engine journalled it (not when the runner executed it).</param>
public sealed record SandboxJournalEntry(
    int Sequence,
    string Path,
    SandboxJournalEntryKind Kind,
    string? Sha256,
    long Bytes,
    string? BlobKey,
    DateTimeOffset RecordedAt);

/// <summary>
/// One line of the manifest a <c>hydrate</c> op carries (docs/runner-protocol.md): where the file
/// goes, what it should digest to, and a single-object GET that may fetch it.
/// </summary>
/// <param name="Url">
/// A presigned GET. It is a bearer secret for one object and must never be logged with its query
/// string — which is why <see cref="SandboxHydrateEntry"/> has no <c>ToString</c> override and is
/// never interpolated into a message anywhere in this feature.
/// </param>
public sealed record SandboxHydrateEntry(string Path, string Sha256, string Url);

/// <summary>
/// The object-store operations <see cref="SandboxWorkspaceJournal"/> needs, in the terms the journal
/// talks about. Its own seam — rather than <c>IAmazonS3</c> directly — for the same reason D12 kept
/// <c>IRunnerOutputStore</c>: the journal's replay logic (which is where the correctness of a
/// rehydrated workspace lives) is then testable with no object store, no container and no network,
/// and the one implementation is the single place that knows the key space.
/// </summary>
public interface ISandboxJournalStore
{
    /// <summary>Store (or overwrite) one object.</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>The object's bytes, or null when it does not exist.</summary>
    Task<byte[]?> TryGetAsync(string key, CancellationToken cancellationToken);

    /// <summary>Every key under <paramref name="prefix"/>, in unspecified order (the caller sorts).</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>Delete one object. Deleting an object that is not there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// A presigned GET for <paramref name="key"/>, which must lie under <c>projects/{projectId}/</c> —
    /// the D2 project boundary, enforced by <see cref="IPresignedUrlService"/> rather than here.
    /// </summary>
    Task<string> CreateGetUrlAsync(Guid projectId, string key, TimeSpan ttl, CancellationToken cancellationToken);
}

/// <summary>
/// D7b: the durable record of what has been written into one execution's sandbox workspace, so a
/// session that outlives its runner can be rebuilt on another one.
///
/// <para>
/// <b>Why this exists.</b> A desktop runner is a laptop. It sleeps, it is closed, it loses its lease
/// and its session — and the sandbox workspace holding every file the Author step wrote goes with it.
/// A reconnected runner keeps its session and answers a repeated <c>opId</c> from its own cache
/// (docs/runner-protocol.md §"Idempotency"), but that cache dies with the process, so it is the
/// ENGINE's journal — not the runner's memory — that has to say what a NEW session must rehydrate.
/// </para>
///
/// <para>
/// <b>Object storage, never engine memory.</b> The plan's rule, and it is load-bearing twice over:
/// the process that opened the workspace may not be the process that recovers it (the engine is
/// horizontally scaled and an execution can be redelivered), and the content blobs are the user's own
/// files. Nothing here caches file content in a field; the only thing engine memory holds is the
/// per-execution sequence cursor, which is a naming device and is re-derived from a listing.
/// </para>
///
/// <para>
/// <b>Key space.</b> Everything sits under
/// <c>projects/{projectId}/agentFiles/sandbox-journal/{executionId}/</c> — inside the project prefix,
/// so <see cref="IPresignedUrlService.CreateGetAsync"/>'s D2 boundary is the thing that authorizes a
/// hydrate URL and this type cannot widen it. Within it, entry <c>N</c> is
/// <c>{N:D8}.json</c> (the metadata) plus, for a write, <c>{N:D8}.bin</c> (the bytes). The fixed-width
/// sequence is what makes an ordinal string sort equal the journal's order.
/// </para>
///
/// <para>
/// <b>What a hydrate manifest is not.</b> It is not the journal. The journal is a history (write,
/// delete, write again); the manifest is the FINAL state of the workspace, folded from that history,
/// because the runner's <c>hydrate</c> op restores a set of files and has no notion of a delete. A
/// path deleted at the end of the run must therefore be absent from the manifest, and a path written
/// twice must appear once, at its last digest.
/// </para>
/// </summary>
public sealed class SandboxWorkspaceJournal
{
    /// <summary>The folder name under <c>projects/{p}/agentFiles/</c> the plan names.</summary>
    public const string FolderName = "sandbox-journal";

    /// <summary>A metadata entry's extension.</summary>
    public const string EntryExtension = ".json";

    /// <summary>A content blob's extension.</summary>
    public const string BlobExtension = ".bin";

    /// <summary>
    /// <see cref="IPresignedUrlService"/> caps a project GET at 15 minutes and the protocol says the
    /// same ("GET URLs are used by <c>stage</c> and <c>hydrate</c>"; "GET URLs expire after 15
    /// minutes"), so a longer value would be rejected one layer down rather than honoured.
    /// </summary>
    public static readonly TimeSpan MaxHydrateUrlTtl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Per-execution sequence cursors. Bounded by <see cref="SealAsync"/>, which every ended execution calls.</summary>
    private readonly ConcurrentDictionary<Guid, Cursor> _cursors = new();

    private readonly ISandboxJournalStore _store;

    public SandboxWorkspaceJournal(ISandboxJournalStore store)
    {
        _store = store;
    }

    /// <summary>The object-key prefix one execution's journal lives under.</summary>
    public static string PrefixFor(Guid projectId, Guid executionId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"projects/{projectId:D}/agentFiles/{FolderName}/{executionId:D}/");

    /// <summary>The metadata object key of entry <paramref name="sequence"/>.</summary>
    public static string EntryKeyFor(Guid projectId, Guid executionId, int sequence)
        => PrefixFor(projectId, executionId) + Sequence(sequence) + EntryExtension;

    /// <summary>The content blob key of entry <paramref name="sequence"/>.</summary>
    public static string BlobKeyFor(Guid projectId, Guid executionId, int sequence)
        => PrefixFor(projectId, executionId) + Sequence(sequence) + BlobExtension;

    private static string Sequence(int sequence) => sequence.ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Record a successful write. The blob is stored first and the metadata entry second, so an entry
    /// can never name a blob that was never written: a crash between the two leaves an orphan blob
    /// (harmless, deleted by <see cref="SealAsync"/>) rather than a manifest line whose URL 404s.
    /// </summary>
    public async Task<SandboxJournalEntry> RecordWriteAsync(
        Guid projectId,
        Guid executionId,
        string path,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Cursor cursor = _cursors.GetOrAdd(executionId, _ => new Cursor());
        await cursor.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int sequence = await NextSequenceAsync(projectId, executionId, cursor, cancellationToken).ConfigureAwait(false);
            string blobKey = BlobKeyFor(projectId, executionId, sequence);

            await _store.PutAsync(blobKey, content, "application/octet-stream", cancellationToken).ConfigureAwait(false);

            SandboxJournalEntry entry = new(
                sequence,
                path,
                SandboxJournalEntryKind.Write,
                Convert.ToHexStringLower(SHA256.HashData(content)),
                content.LongLength,
                blobKey,
                DateTimeOffset.UtcNow);

            await _store.PutAsync(
                EntryKeyFor(projectId, executionId, sequence),
                JsonSerializer.SerializeToUtf8Bytes(entry, Json),
                "application/json",
                cancellationToken).ConfigureAwait(false);

            return entry;
        }
        finally
        {
            cursor.Gate.Release();
        }
    }

    /// <summary>
    /// Record a successful delete. A tombstone rather than a rewrite of the history, for the same
    /// reason the write path appends: the runner executes ops one at a time, but the ENGINE may
    /// journal them from more than one async flow, and an append cannot lose a concurrent op the way
    /// a read-modify-write index can.
    /// </summary>
    public async Task<SandboxJournalEntry> RecordDeleteAsync(
        Guid projectId,
        Guid executionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Cursor cursor = _cursors.GetOrAdd(executionId, _ => new Cursor());
        await cursor.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int sequence = await NextSequenceAsync(projectId, executionId, cursor, cancellationToken).ConfigureAwait(false);

            SandboxJournalEntry entry = new(
                sequence,
                path,
                SandboxJournalEntryKind.Delete,
                Sha256: null,
                Bytes: 0,
                BlobKey: null,
                DateTimeOffset.UtcNow);

            await _store.PutAsync(
                EntryKeyFor(projectId, executionId, sequence),
                JsonSerializer.SerializeToUtf8Bytes(entry, Json),
                "application/json",
                cancellationToken).ConfigureAwait(false);

            return entry;
        }
        finally
        {
            cursor.Gate.Release();
        }
    }

    /// <summary>
    /// The final state of the workspace, as a <c>hydrate</c> manifest: one entry per path that still
    /// exists, each with a presigned GET minted NOW (the protocol is explicit that the engine mints a
    /// URL when it issues the op and never stores one, so a job that queued holds no expired URL).
    /// </summary>
    public async Task<IReadOnlyList<SandboxHydrateEntry>> BuildManifestAsync(
        Guid projectId,
        Guid executionId,
        TimeSpan urlTtl,
        CancellationToken cancellationToken = default)
    {
        if (urlTtl <= TimeSpan.Zero || urlTtl > MaxHydrateUrlTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(urlTtl),
                "The hydrate GET TTL must be in (0, " + MaxHydrateUrlTtl + "]; the protocol caps a hydrate GET at 15 minutes.");
        }

        Dictionary<string, SandboxJournalEntry> final = await ReplayAsync(projectId, executionId, cancellationToken).ConfigureAwait(false);

        List<SandboxHydrateEntry> manifest = new(final.Count);
        foreach (SandboxJournalEntry entry in final.Values.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            if (entry.BlobKey is null || entry.Sha256 is null)
            {
                continue;
            }

            string url = await _store
                .CreateGetUrlAsync(projectId, entry.BlobKey, urlTtl, cancellationToken)
                .ConfigureAwait(false);
            manifest.Add(new SandboxHydrateEntry(entry.Path, entry.Sha256, url));
        }

        return manifest;
    }

    /// <summary>The folded history: path to the entry that last touched it, with every deleted path (and every path under a deleted directory) removed.</summary>
    public async Task<Dictionary<string, SandboxJournalEntry>> ReplayAsync(
        Guid projectId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        List<SandboxJournalEntry> entries = await ReadEntriesAsync(projectId, executionId, cancellationToken).ConfigureAwait(false);

        Dictionary<string, SandboxJournalEntry> state = new(StringComparer.Ordinal);
        foreach (SandboxJournalEntry entry in entries)
        {
            if (entry.Kind == SandboxJournalEntryKind.Delete)
            {
                // A directory delete removes the directory's own entry AND everything beneath it, or
                // a manifest would resurrect files the agent removed with one call.
                string prefix = entry.Path.TrimEnd('/') + "/";
                foreach (string removed in state.Keys
                             .Where(k => string.Equals(k, entry.Path, StringComparison.Ordinal)
                                         || k.StartsWith(prefix, StringComparison.Ordinal))
                             .ToList())
                {
                    state.Remove(removed);
                }

                continue;
            }

            state[entry.Path] = entry;
        }

        return state;
    }

    /// <summary>Every journalled entry, oldest first. Unreadable or unparseable entry objects are skipped with a warning rather than failing the recovery they exist to serve.</summary>
    public async Task<List<SandboxJournalEntry>> ReadEntriesAsync(
        Guid projectId,
        Guid executionId,
        CancellationToken cancellationToken = default)
    {
        string prefix = PrefixFor(projectId, executionId);
        IReadOnlyList<string> keys = await _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false);

        List<SandboxJournalEntry> entries = new();
        foreach (string key in keys.Where(k => k.EndsWith(EntryExtension, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
        {
            byte[]? body = await _store.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                continue;
            }

            try
            {
                SandboxJournalEntry? entry = JsonSerializer.Deserialize<SandboxJournalEntry>(body, Json);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // An entry object this build cannot read is a torn or foreign write. Skipping it
                // degrades the hydration (one file the new session will not have) where throwing
                // would take down the very recovery path it belongs to.
            }
        }

        entries.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        return entries;
    }

    /// <summary>
    /// Delete every object the journal holds for one execution and drop its sequence cursor. Called
    /// when the execution ends: the journal's whole purpose is to span a session loss WITHIN one
    /// execution, and a workspace does not outlive its execution, so keeping the objects would only
    /// grow the bucket. Idempotent, and it never throws for an execution that journalled nothing.
    /// </summary>
    public async Task SealAsync(Guid projectId, Guid executionId, CancellationToken cancellationToken = default)
    {
        _cursors.TryRemove(executionId, out _);

        string prefix = PrefixFor(projectId, executionId);
        IReadOnlyList<string> keys;
        try
        {
            keys = await _store.ListAsync(prefix, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Sealing is hygiene. An object store that will not answer must not turn "the execution
            // finished" into an exception in the runner's finally.
            return;
        }

        foreach (string key in keys)
        {
            try
            {
                await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The sequence the next entry gets. Seeded once per execution from a listing, so a journal
    /// written by a PREVIOUS engine process (an execution recovered after a restart) continues where
    /// it stopped instead of overwriting entry 0.
    /// </summary>
    private async Task<int> NextSequenceAsync(Guid projectId, Guid executionId, Cursor cursor, CancellationToken cancellationToken)
    {
        if (cursor.Next < 0)
        {
            IReadOnlyList<string> keys = await _store
                .ListAsync(PrefixFor(projectId, executionId), cancellationToken)
                .ConfigureAwait(false);

            int highest = -1;
            foreach (string key in keys)
            {
                if (!key.EndsWith(EntryExtension, StringComparison.Ordinal))
                {
                    continue;
                }

                string stem = key[..^EntryExtension.Length];
                int slash = stem.LastIndexOf('/');
                if (slash >= 0 && int.TryParse(stem[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                {
                    highest = Math.Max(highest, parsed);
                }
            }

            cursor.Next = highest + 1;
        }

        int sequence = cursor.Next;
        cursor.Next = sequence + 1;
        return sequence;
    }

    private sealed class Cursor
    {
        /// <summary>Negative means "not seeded yet"; see <see cref="NextSequenceAsync"/>.</summary>
        public int Next { get; set; } = -1;

        /// <summary>Serialises the seed-and-take so two concurrent tool calls cannot both take the same sequence.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

/// <summary>
/// The production <see cref="ISandboxJournalStore"/>, over the two S3 clients the rest of the engine
/// uses (D2's shared factory registers the in-cluster client and the keyed "presign" client built on
/// <c>MinIO:PublicEndpoint</c>).
/// </summary>
public sealed class S3SandboxJournalStore : ISandboxJournalStore
{
    private readonly IAmazonS3 _s3;
    private readonly IPresignedUrlService _presignedUrls;
    private readonly string _bucket;

    public S3SandboxJournalStore(
        IAmazonS3 s3,
        IPresignedUrlService presignedUrls,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _s3 = s3;
        _presignedUrls = presignedUrls;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new(content, writable: false);
        await _s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = stream,
                ContentType = contentType,
                AutoCloseStream = false
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await _s3
                .GetObjectAsync(_bucket, key, cancellationToken)
                .ConfigureAwait(false);
            using MemoryStream buffer = new();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        List<string> keys = new();
        string? token = null;
        do
        {
            ListObjectsV2Response page = await _s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _bucket,
                    Prefix = prefix,
                    ContinuationToken = token
                },
                cancellationToken).ConfigureAwait(false);

            foreach (S3Object entry in page.S3Objects ?? [])
            {
                if (entry.Key is not null)
                {
                    keys.Add(entry.Key);
                }
            }

            token = page.IsTruncated == true ? page.NextContinuationToken : null;
        }
        while (token is not null);

        return keys;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        => await _s3.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);

    public Task<string> CreateGetUrlAsync(Guid projectId, string key, TimeSpan ttl, CancellationToken cancellationToken)
    {
        // Delegated, not reimplemented: CreateGetAsync is D2's project-boundary check and its 15-minute
        // TTL cap, and a journal key is under projects/{projectId}/ by construction — which is exactly
        // why this method does not take a caller-supplied prefix.
        return _presignedUrls.CreateGetAsync(projectId, key, ttl);
    }
}
