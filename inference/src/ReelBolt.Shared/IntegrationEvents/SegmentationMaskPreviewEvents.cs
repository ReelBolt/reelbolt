using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// Published by the Inference API when the editor asks for a segmentation track's browser-decodable mask
/// preview and none is stored yet. The WorkflowEngine (the only place ffmpeg runs) produces it.
/// </summary>
/// <remarks>
/// A hint, never a source of truth: it carries only ids. The engine re-reads the step result's analysis
/// artifact, finds the track, re-checks the project's output hold and re-hashes the mask before it encodes
/// anything, so a forged or stale message can name nothing the project does not own. Nothing about it is
/// durable: a lost delivery is repaired by the editor's next poll.
/// </remarks>
public record SegmentationMaskPreviewRequested(
    Guid ProjectId,
    Guid StepResultId,
    string TrackId,
    DateTime RequestedAt);

/// <summary>
/// The one definition of a mask preview's identity and object layout, shared by the API (which looks the
/// object up) and the engine (which writes it), so the two can never disagree about a key.
/// </summary>
/// <remarks>
/// The preview is a derived copy of one track's FFV1 mask (which holds exactly that track's object, 255 on
/// 0): H.264 in MP4, <c>yuv420p</c> with the FULL range flag set (luma 0..255 = the mask, chroma neutral),
/// long side at most 640, the mask's own frame rate and frame count. It is stored beside, never inside,
/// <c>agentFiles/segmentation/</c>, which <c>SegmentationMaskStore</c> alone writes. It is free, is not a
/// project file and has no database row: the object store is the cache and the key is the identity.
/// </remarks>
public static class SegmentationMaskPreviewIdentity
{
    /// <summary>
    /// Bump when the encode recipe changes (size, codec, range, keyframe interval): it is part of the cache
    /// key, so an older preview is never served as a newer one.
    /// </summary>
    public const int RecipeVersion = 1;

    /// <summary>Where masks live, relative to the project prefix (the store's own directory).</summary>
    public const string MaskDirectory = "agentFiles/segmentation/";

    /// <summary>Where previews live, relative to the project prefix. Not under <see cref="MaskDirectory"/>.</summary>
    public const string PreviewDirectory = "agentFiles/segmentation-preview/";

    public const string ContentType = "video/mp4";

    /// <summary>
    /// Lower-case hex SHA-256 over the length-prefixed (mask hash, track id, recipe version): the preview's
    /// whole identity. The mask hash already pins the mask bytes; the track id is part of the key by contract.
    /// </summary>
    public static string CacheKey(string maskContentHash, string trackId, int recipeVersion = RecipeVersion)
    {
        ArgumentNullException.ThrowIfNull(maskContentHash);
        ArgumentNullException.ThrowIfNull(trackId);

        StringBuilder material = new();
        Append(material, maskContentHash.Trim().ToLowerInvariant());
        Append(material, trackId);
        Append(material, recipeVersion.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    /// <summary>The leaf the engine uploads the preview as, relative to <c>agentFiles/</c> (the upload's category).</summary>
    public static string UploadFileName(string cacheKey) => $"segmentation-preview/{cacheKey}.mp4";

    /// <summary>The leaf of the "this mask could not be verified, there is no preview" marker.</summary>
    public static string NoneMarkerUploadFileName(string cacheKey) => $"segmentation-preview/{cacheKey}.none";

    public static string StorageKey(Guid projectId, string cacheKey) =>
        FormattableString.Invariant($"projects/{projectId:D}/{PreviewDirectory}{cacheKey}.mp4");

    public static string NoneMarkerKey(Guid projectId, string cacheKey) =>
        FormattableString.Invariant($"projects/{projectId:D}/{PreviewDirectory}{cacheKey}.none");

    /// <summary>True when the key is a mask video of THIS project under the store's own directory.</summary>
    public static bool IsProjectMaskKey(Guid projectId, string? storageKey) =>
        !string.IsNullOrEmpty(storageKey)
        && !storageKey.Contains("..", StringComparison.Ordinal)
        && storageKey.StartsWith(FormattableString.Invariant($"projects/{projectId:D}/{MaskDirectory}"), StringComparison.Ordinal);

    /// <summary>
    /// True when a preview can be attempted for the track at all: an id, a mask under this project's own mask
    /// directory, a SHA-256 hash, and a frame count and rate to hold the encode to.
    /// </summary>
    public static bool IsPreviewable(Guid projectId, VideoSegmentationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return !string.IsNullOrEmpty(track.Id)
            && IsProjectMaskKey(projectId, track.MaskStorageKey)
            && IsSha256Hex(track.MaskContentHash)
            && track.FrameCount > 0
            && double.IsFinite(track.Fps)
            && track.Fps > 0;
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void Append(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
