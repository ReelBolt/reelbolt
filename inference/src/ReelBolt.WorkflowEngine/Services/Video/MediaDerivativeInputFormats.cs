namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The demuxers a derivative run may open its source with, passed as <c>-format_whitelist</c> to
/// every derivative ffprobe/ffmpeg invocation (security MI-2).
/// </summary>
/// <remarks>
/// <para>
/// The protocol whitelist (<c>file</c>) alone is not enough: a user upload is untrusted, and a
/// crafted HLS playlist, concat list, DASH manifest or SDP file is opened through the <c>file</c>
/// protocol and can then name other local files in the engine container (which mounts
/// <c>/keys</c>). Those demuxers are simply not on this list, so ffmpeg refuses the input with
/// "Format not on whitelist" whatever its content or extension.
/// </para>
/// <para>
/// One name per demuxer is enough: ffmpeg matches when ANY of a demuxer's comma-separated names is
/// listed, so <c>mov</c> admits the <c>mov,mp4,m4a,3gp,3g2,mj2</c> demuxer and <c>matroska</c> the
/// <c>matroska,webm</c> one. Same idea as <c>OutputVerifier.ProbeFormatWhitelist</c>, widened to
/// the containers people upload (MPEG-PS, FLV, ASF/WMV, AIFF, CAF, W64, and MXF from Sony XDCAM
/// and Canon cameras).
/// </para>
/// </remarks>
public static class MediaDerivativeInputFormats
{
    public const string Whitelist =
        "mov,matroska,mp3,wav,aac,flac,ogg,avi,mpegts,mpeg,flv,asf,aiff,caf,w64,mxf";

    /// <summary>
    /// The source probe's argv: <see cref="FfmpegArgvBuilder.BuildProbeArgs"/> plus the derivative
    /// demuxer whitelist. Kept separate because that builder also probes trusted, engine-produced
    /// inputs (stills among them) that the media-container list would refuse.
    /// </summary>
    public static string[] BuildSourceProbeArgs(string localSourcePath) =>
    [
        "-v", "error",
        "-print_format", "json",
        "-show_format", "-show_streams",
        "-protocol_whitelist", "file",
        "-format_whitelist", Whitelist,
        localSourcePath
    ];
}
