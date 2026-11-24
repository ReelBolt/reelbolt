using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>
/// What a content-addressed segmentation mask is keyed BY (initiative "prompted region effects",
/// plan §6). Every field changes the mask set a stored entry may serve: a different source, prompt,
/// model, detection floor, object budget, point/box seed, provider or user-selected scope must never
/// serve a previous answer.
/// </summary>
/// <param name="SourceContentHash">SHA-256 of the SOURCE CLIP's bytes — not its key, so re-uploading the same footage under a new file name still hits.</param>
/// <param name="Prompt">The sanitized, length-capped prompt actually sent.</param>
/// <param name="Model">The endpoint id called (e.g. <c>fal-ai/sam-3-1/video-rle</c>).</param>
/// <param name="DetectionThreshold">The clamped <c>detection_threshold</c> sent verbatim.</param>
/// <param name="MaxObjects">The clamped <c>max_num_objects</c> sent verbatim.</param>
/// <param name="Seed">The canonical point/box seed the call carried, or <c>"-"</c> when the prompt alone seeded it.</param>
/// <param name="ProviderFingerprint">Provider id + kind + model (no key material), so repointing the provider row invalidates.</param>
/// <param name="InstanceOrdinal">
/// Which of the instances the prompt matched this mask is (0-based, in the analyze step's deterministic
/// order). One answer can describe several distinct objects (D-23), each with its own mask video, so each
/// needs its own content key; ordinal 0's entry carries <c>InstanceCount</c>, which is how a repeat run
/// reads the whole set back without a vendor call. Appended and defaulted so every existing key
/// derivation keeps its meaning.
/// </param>
/// <param name="MatchScope">
/// The user's <c>ObjectMatchScope</c> for the target: every instance the prompt matches, or one. It is a
/// hashed dimension because it decides how many tracks (and therefore how many mask entries under this
/// key family) the answer produces — <c>SingleTrack</c> on ordinal 0 states <c>InstanceCount = 1</c>,
/// <c>AllInstances</c> may state several. Without it, switching scope served the other scope's cached
/// set and the user's "one object only" choice was silently defeated. Defaulted so existing
/// constructions keep compiling; the content key still changes for every entry because the material
/// gains this field (an old entry is a miss, never a cross-scope hit).
/// </param>
public sealed record SegmentationMaskKey(
    string SourceContentHash,
    string Prompt,
    string Model,
    double DetectionThreshold,
    int MaxObjects,
    string Seed,
    string? ProviderFingerprint,
    int InstanceOrdinal = 0,
    ObjectMatchScope MatchScope = ObjectMatchScope.AllInstances);

/// <summary>
/// One stored mask: everything the analyze step needs to emit a
/// <c>VideoSegmentationTrack</c> WITHOUT calling the vendor again — the storage key and hash of the mask
/// video, the geometry the mask was encoded at, and the quality facts and boxes the vendor's answer
/// produced.
/// </summary>
/// <param name="ContentKey">The content key this entry was found/created under.</param>
/// <param name="MaskStorageKey">Where the FFV1 grey8 mask video lives (<c>projects/{p}/agentFiles/segmentation/{key}.mkv</c>).</param>
/// <param name="MaskContentHash">Lower-case hex SHA-256 of the mask video's bytes; the compile step re-hashes what it downloads and refuses a mismatch.</param>
/// <param name="Width">Frame width the mask was encoded at (the source's own).</param>
/// <param name="Height">Frame height.</param>
/// <param name="FrameCount">Frames the mask holds.</param>
/// <param name="Fps">Frame rate the mask was encoded at.</param>
/// <param name="Coverage">Mean fraction of the frame the object covers (0..1).</param>
/// <param name="Confidence">min(1, mean model score) when the vendor scored, else the coverage-derived proxy.</param>
/// <param name="StartSec">First moment of the track on its source's timeline.</param>
/// <param name="EndSec">Last moment of the track on its source's timeline.</param>
/// <param name="Model">The endpoint id that produced it.</param>
/// <param name="ProviderFingerprint">Provider id + kind + model, never key material.</param>
/// <param name="EmptyFrames">Frames whose mask is entirely background.</param>
/// <param name="LongestGapFrames">Longest consecutive run of empty frames.</param>
/// <param name="DetectionThreshold">The threshold actually sent.</param>
/// <param name="FpsMeasured">How many frames per second the SOURCE probes to (kept apart from <paramref name="Fps"/> so a re-encode is visible).</param>
/// <param name="FirstNonEmptyFrame">First frame (0-based) whose mask is not entirely empty — where the object was first found; <c>null</c> when the mask is empty everywhere, or when the entry was written before this fact (a cache entry still deserializes).</param>
/// <param name="LastNonEmptyFrame">Last frame whose mask is not entirely empty — where the object was last found; <c>null</c> under the same conditions as <paramref name="FirstNonEmptyFrame"/>.</param>
public sealed record SegmentationMaskEntry(
    string ContentKey,
    string MaskStorageKey,
    string MaskContentHash,
    int Width,
    int Height,
    int FrameCount,
    double Fps,
    double Coverage,
    double Confidence,
    double StartSec,
    double EndSec,
    string Model,
    string? ProviderFingerprint,
    int EmptyFrames,
    int LongestGapFrames,
    double DetectionThreshold,
    double FpsMeasured,
    int? FirstNonEmptyFrame = null,
    int? LastNonEmptyFrame = null,
    /// <summary>
    /// How many instances this answer produced. Set on ordinal 0 only (null elsewhere), so a repeat run
    /// that hits the content cache knows how many masks to read back instead of calling the vendor.
    /// </summary>
    int? InstanceCount = null)
{
    /// <summary>
    /// The per-frame normalised boxes as compact <c>[timeSec, x, y, w, h]</c> rows — the OPEN shape the
    /// metadata file stores. The analyze step samples them into the artifact's own bounded box list, so
    /// the cache does not need (and does not restate) the artifact's record shape here.
    /// </summary>
    public IReadOnlyList<double[]>? Boxes { get; init; }

    /// <summary>
    /// How many distinct vendor object slots the answer named, whatever the prompt selected. Equal to or
    /// above the step's <c>maxSegmentationObjects</c> is the vendor's own truncation signal — a further
    /// instance of the object exists that this track does not cover — which is what
    /// <c>VideoSegmentationFacts.Truncated</c> reports.
    /// </summary>
    public int DistinctObjects { get; init; }
}

/// <summary>
/// The content-addressed store for segmentation masks: the only thing that lets a repeated run (or a
/// re-run after a failure) reuse a paid vendor answer with <b>zero</b> provider calls, and the only
/// writer of <c>projects/{projectId}/agentFiles/segmentation/</c> (plan §6).
/// </summary>
public interface ISegmentationMaskStore
{
    /// <summary>The content key <paramref name="key"/> hashes to: lower-case hex SHA-256 over its length-prefixed fields.</summary>
    string ContentKey(SegmentationMaskKey key);

    /// <summary>
    /// The stored mask for <paramref name="contentKey"/>, or <c>null</c> when there is none — a miss is
    /// never an exception. A missing object, an unreadable or unparsable metadata file, and a metadata
    /// file that names a different key are all misses: the caller then calls the vendor and overwrites.
    /// </summary>
    Task<SegmentationMaskEntry?> TryGetAsync(
        Guid projectId,
        string contentKey,
        IMediaWorkspace workspace,
        CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="localMaskPath"/> as this content key's mask video, writes the metadata
    /// sidecar, and answers the entry to hand to the artifact. The hash is computed from the file the
    /// caller is telling us to store, never from the vendor's answer.
    /// </summary>
    Task<SegmentationMaskEntry> PutAsync(
        Guid projectId,
        IMediaWorkspace workspace,
        string localMaskPath,
        string maskVirtualPath,
        SegmentationMaskEntry entry,
        CancellationToken ct);

    /// <summary>
    /// True when the size-bounded vendor copy of the source with this content hash is already stored. A
    /// copy is only ever stored after its size was checked against
    /// <see cref="ReelBolt.Shared.Workflows.SegmentationLimits.VendorSourceMaxBytes"/>.
    /// </summary>
    Task<bool> VendorCopyExistsAsync(Guid projectId, string sourceHash, CancellationToken ct);

    /// <summary>Uploads the session file at <paramref name="virtualPath"/> as that source's size-bounded vendor copy.</summary>
    Task PutVendorCopyAsync(Guid projectId, IMediaWorkspace workspace, string virtualPath, string sourceHash, CancellationToken ct);
}

/// <summary>
/// The default store: object storage is the cache (there is no in-process table to leak across
/// executions), and every key is project-scoped so no run can read another project's mask.
/// </summary>
public sealed class SegmentationMaskStore(
    IProjectFileWorkspace files,
    ILogger<SegmentationMaskStore> logger) : ISegmentationMaskStore
{
    /// <summary>Where every mask lives, relative to the project's own prefix.</summary>
    public const string KeyPrefix = "agentFiles/segmentation/";

    /// <summary>
    /// Bumped whenever the object-SELECTION rule changes, because it changes the mask bytes for an
    /// otherwise identical key (T-020, D-22). The rest of the key covers what the vendor is asked
    /// (source, prompt, model, knobs, seed, provider); this covers what we do with the answer, so a mask
    /// stored under an older rule cannot be served to a newer one. One-time cost: every stored mask is
    /// re-derived on its clip's next run.
    /// </summary>
    public const string SelectionVersion = "reacquire-2";

    /// <summary>Content type the mask video is stored as (Matroska carrying FFV1 grey8).</summary>
    public const string MaskContentType = "video/x-matroska";

    /// <summary>Content type of the metadata sidecar.</summary>
    public const string MetadataContentType = "application/json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The mask video's storage key for <paramref name="contentKey"/>.</summary>
    public static string MaskStorageKeyFor(Guid projectId, string contentKey) =>
        FormattableString.Invariant($"projects/{projectId:D}/{KeyPrefix}{contentKey}.mkv");

    /// <summary>
    /// True when <paramref name="storageKey"/> is a mask video of THIS project under the store's own prefix
    /// (<c>projects/{p}/agentFiles/segmentation/</c>): the only keys a reader may open, whatever an artifact says.
    /// </summary>
    public static bool IsProjectMaskKey(Guid projectId, string? storageKey) =>
        !string.IsNullOrEmpty(storageKey)
        && !storageKey.Contains("..", StringComparison.Ordinal)
        && storageKey.StartsWith(FormattableString.Invariant($"projects/{projectId:D}/{KeyPrefix}"), StringComparison.Ordinal);

    /// <summary>
    /// Re-verifies a downloaded mask: its SHA-256 must equal the content hash the analyze step recorded.
    /// A reader never renders through a mask this answers <c>false</c> for.
    /// </summary>
    public static async Task<bool> VerifyHashAsync(string localMaskPath, string? expectedHash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
            return false;
        string actual = await HashFileAsync(localMaskPath, ct);
        return string.Equals(actual, expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the size-bounded, upright vendor copy of a source lives: cached by source hash. Its own
    /// prefix, so an unbounded <c>upright/</c> mezzanine stored by an earlier build is never handed to a
    /// vendor again.
    /// </summary>
    public static string VendorCopyStorageKeyFor(Guid projectId, string sourceHash) =>
        FormattableString.Invariant($"projects/{projectId:D}/{KeyPrefix}vendor1/{sourceHash}.mp4");

    public Task<bool> VendorCopyExistsAsync(Guid projectId, string sourceHash, CancellationToken ct) =>
        files.ArtifactExistsAsync(projectId, VendorCopyStorageKeyFor(projectId, sourceHash), ct);

    public async Task PutVendorCopyAsync(
        Guid projectId, IMediaWorkspace workspace, string virtualPath, string sourceHash, CancellationToken ct) =>
        await workspace.UploadArtifactAsync(virtualPath, VendorCopyStorageKeyFor(projectId, sourceHash), "video/mp4", ct);

    /// <summary>The metadata sidecar's storage key for <paramref name="contentKey"/>.</summary>
    public static string MetadataStorageKeyFor(Guid projectId, string contentKey) =>
        FormattableString.Invariant($"projects/{projectId:D}/{KeyPrefix}{contentKey}.json");

    public string ContentKey(SegmentationMaskKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        StringBuilder material = new();
        Append(material, key.SourceContentHash);
        Append(material, key.Prompt);
        Append(material, key.Model);
        Append(material, key.DetectionThreshold.ToString("0.######", CultureInfo.InvariantCulture));
        Append(material, key.MaxObjects.ToString(CultureInfo.InvariantCulture));
        Append(material, key.Seed);
        Append(material, key.ProviderFingerprint ?? "-");
        Append(material, SelectionVersion);
        // The user's scope is part of what the vendor answer means (one track or all of them), so it is
        // part of the key: a SingleTrack plan can never be served the AllInstances set, or vice versa.
        Append(material, key.MatchScope.ToString());
        Append(material, key.InstanceOrdinal.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    public async Task<SegmentationMaskEntry?> TryGetAsync(
        Guid projectId,
        string contentKey,
        IMediaWorkspace workspace,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        ArgumentNullException.ThrowIfNull(workspace);

        string maskKey = MaskStorageKeyFor(projectId, contentKey);
        string metadataKey = MetadataStorageKeyFor(projectId, contentKey);

        try
        {
            if (!await files.ArtifactExistsAsync(projectId, maskKey, ct))
            {
                return null;
            }

            string metadataVirtualPath = MetadataVirtualPath(contentKey);
            await workspace.StageAsync(metadataKey, metadataVirtualPath, ct);
            byte[] json = await workspace.ReadAsync(metadataVirtualPath, ct);
            SegmentationMaskEntry? entry = JsonSerializer.Deserialize<SegmentationMaskEntry>(json, JsonOptions);

            if (entry is null || !string.Equals(entry.ContentKey, contentKey, StringComparison.Ordinal))
            {
                logger.LogDebug("Segmentation mask metadata for {ContentKey} does not describe it; treating as a miss", contentKey);
                return null;
            }

            // The metadata names a key; the store owns the path. Trusting a stored path would let one
            // run's metadata point at another project's object.
            return entry with { MaskStorageKey = maskKey };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A corrupt/absent sidecar is a miss, never a crash: the caller re-calls the vendor and
            // overwrites. Any real store outage surfaces on the upload path instead.
            logger.LogDebug(ex, "Segmentation mask metadata for {ContentKey} could not be read; treating as a miss", contentKey);
            return null;
        }
    }

    public async Task<SegmentationMaskEntry> PutAsync(
        Guid projectId,
        IMediaWorkspace workspace,
        string localMaskPath,
        string maskVirtualPath,
        SegmentationMaskEntry entry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(localMaskPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(maskVirtualPath);
        ArgumentNullException.ThrowIfNull(entry);

        string hash = await HashFileAsync(localMaskPath, ct);
        string maskKey = MaskStorageKeyFor(projectId, entry.ContentKey);

        await workspace.UploadArtifactAsync(maskVirtualPath, maskKey, MaskContentType, ct);

        SegmentationMaskEntry stored = entry with { MaskStorageKey = maskKey, MaskContentHash = hash };

        string metadataVirtualPath = MetadataVirtualPath(entry.ContentKey);
        await workspace.WriteBytesAsync(
            metadataVirtualPath,
            JsonSerializer.SerializeToUtf8Bytes(stored, JsonOptions),
            ct);
        await workspace.UploadArtifactAsync(
            metadataVirtualPath, MetadataStorageKeyFor(projectId, entry.ContentKey), MetadataContentType, ct);

        return stored;
    }

    /// <summary>The session path the metadata sidecar is staged into / written at.</summary>
    public static string MetadataVirtualPath(string contentKey) =>
        FormattableString.Invariant($"{SegmentationMaskArgvBuilder.Directory}/mask-{contentKey}.json");

    /// <summary>
    /// Lower-case hex SHA-256 of a file, streamed (never buffered whole): a mask video is tens of
    /// megabytes. The caller has already materialised the file on THIS host — the analyze step forces a
    /// local media workspace for the same reason the feature tracker does.
    /// </summary>
    internal static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void Append(StringBuilder material, string value)
    {
        // Length-prefixed, so no two different field lists can concatenate to the same string.
        material.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
    }
}
