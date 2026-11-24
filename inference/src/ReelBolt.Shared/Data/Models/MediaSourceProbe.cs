using System.Text.Json;

namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// What ffprobe measured about a derivative's SOURCE when the derivative run took it: stored as
/// <see cref="MediaDerivative.SourceProbeJson"/> (<c>media_derivatives.source_probe_json</c>, jsonb)
/// by the engine, read back by the Inference API's media pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, never defaulted.</b> The numbers are ffprobe's own (the engine's
/// <c>FfprobeOutputParser</c>); a field ffprobe did not report stays null. <see cref="HasAudio"/> is
/// the one derived field: true exactly when ffprobe reported an audio stream codec, which is the same
/// rule the proxy and the peaks derivative use to decide whether the source has sound.
/// </para>
/// <para>
/// <b>The JSON shape is the contract between the two processes</b> — web defaults (camelCase), so a
/// stored value reads the same in a database console as in the API response it becomes.
/// </para>
/// </remarks>
public sealed record MediaSourceProbe(
    double DurationSec,
    int FpsNum,
    int FpsDen,
    int Width,
    int Height,
    string? VideoCodec,
    string? AudioCodec,
    string? Container,
    int? AudioChannels,
    int? AudioSampleRateHz,
    long? BitrateBps,
    bool HasAudio,
    // R10.7 (additive, optional): colour facts of the first video stream. A probe stored before these
    // existed deserialises with all three null, which reads as "not HDR / not known".
    string? ColorTransfer = null,
    string? ColorPrimaries = null,
    string? Hdr = null,
    // T-102 (additive, optional): ffmpeg's autorotate changes the decoded pixels (quarter turn, other angle, mirror). Null =
    // a probe stored before this field, i.e. not known.
    bool? AltersPixelsOnDecode = null,
    // log-footage-color P2 (additive, optional): colour/camera facts. Null = not known (a row stored before these).
    string? ColorSpace = null,
    string? ColorRange = null,
    string? PixFmt = null,
    int? BitDepth = null,
    string? Make = null,
    string? Model = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The stored form of this probe.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Reads a stored probe, or null when there is none or it cannot be read. A corrupt value is
    /// "not probed", never an exception: the pool is a listing and must not fail on one bad row.
    /// </summary>
    public static MediaSourceProbe? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<MediaSourceProbe>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
