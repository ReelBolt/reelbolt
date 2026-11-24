namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Input hardening for the steps that read a customer's own media through ffmpeg/ffprobe
/// (VideoAnalyze, VideoCompile, TimelineRender): the demuxer whitelist, the per-input options that
/// carry it, and the extension a staged copy may keep (security ENG-3, the same threat as MI-2 in
/// <see cref="MediaDerivativeInputFormats"/>).
/// </summary>
/// <remarks>
/// <para>
/// An upload is untrusted. A playlist or list file (HLS, concat, DASH, SDP) is opened through the
/// <c>file</c> protocol and then names other local files, so the protocol whitelist alone does not
/// contain it. Those demuxers are not on <see cref="Whitelist"/>, and ffmpeg refuses the input with
/// "Format not on whitelist" whatever its content or extension.
/// </para>
/// <para>
/// The list is <see cref="MediaDerivativeInputFormats.Whitelist"/> plus <c>ac3</c> and the still-image
/// demuxers, because these steps also take pictures (a still source, an overlay, an insert) and read
/// the engine's own intermediates through the same inputs (FFV1 Matroska masks, WAV, PNG, MP4).
/// <c>image2</c> is what ffmpeg picks for a <c>.jpg</c> name; the <c>_pipe</c> names are what it
/// picks from the content. An engine-authored <c>-f lavfi</c> or <c>-f concat</c> input is not user
/// media and must NOT carry these options: neither format is on the list.
/// </para>
/// </remarks>
public static class UserMediaInputFormats
{
    public const string Whitelist =
        MediaDerivativeInputFormats.Whitelist +
        ",ac3,image2,jpeg_pipe,png_pipe,webp_pipe,bmp_pipe,tiff_pipe,jpegls_pipe,gif,apng";

    /// <summary>The failure code a refused source answers: the customer's input, never refunded as ours.</summary>
    public const string RefusedCode = "SOURCE_INVALID";

    public const string RefusedMessage =
        "This file is a playlist or another format that is not a video, audio or image file. " +
        "Upload the media file itself (for example MP4, MOV, MP3, WAV, PNG or JPG), then run it again.";

    /// <summary>Same code and class as <see cref="RefusedMessage"/>, for a file no reader recognises (often damaged or cut short).</summary>
    public const string UnreadableMessage =
        "This file could not be read as a video, audio or image file: it looks damaged or incomplete (for example an " +
        "upload that was cut short), or it is in a format the platform does not read. Upload the file again, " +
        "or export it as MP4, MOV, MP3, WAV, PNG or JPG, then run it again.";

    /// <summary>
    /// The two per-input options, to be placed before one <c>-i</c>. ffmpeg applies them to the next
    /// input only, so every user-media <c>-i</c> needs its own pair.
    /// </summary>
    public static string[] InputOptions() =>
    [
        "-protocol_whitelist", "file",
        "-format_whitelist", Whitelist
    ];

    /// <summary>
    /// The extension of a staged copy: the storage key's own when it is a known media or image
    /// extension (so ffmpeg's demuxer choice is what it always was), the neutral <c>.bin</c> for any
    /// other, and <paramref name="whenNone"/> when the key has none. Never a playlist or list
    /// extension (<c>.m3u8</c>, <c>.ffconcat</c>, <c>.txt</c>, ...): ffmpeg only reads an HLS playlist
    /// as one under such a name.
    /// </summary>
    public static string SafeExtension(string? storageKey, string whenNone)
    {
        string extension = string.IsNullOrWhiteSpace(storageKey) ? string.Empty : Path.GetExtension(storageKey);
        if (extension.Length == 0)
            return whenNone;

        return ImageExtensions.Contains(extension) ? extension : MediaDerivativeDispatcher.SafeExtension(storageKey);
    }

    /// <summary>Whether a storage key is named as a playlist or list file, which is never media.</summary>
    public static bool IsListFile(string? storageKey) =>
        !string.IsNullOrWhiteSpace(storageKey) && ListExtensions.Contains(Path.GetExtension(storageKey));

    /// <summary>Whether a failed ffmpeg/ffprobe run was the demuxer whitelist refusing the input.</summary>
    public static bool IsWhitelistRefusal(string? stdErr) =>
        stdErr is not null && stdErr.Contains("not on whitelist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a failed ffprobe run found content no whitelisted demuxer recognises ("Invalid data found when
    /// processing input"): a TGA or PAM picture staged as <c>.bin</c>, a playlist under a media name, or a
    /// damaged or truncated media file. Only a probe of the customer's own staged upload may read this as the
    /// customer's input; an engine intermediate that fails this way is a platform failure.
    /// </summary>
    public static bool IsUnrecognisedContent(string? stdErr) =>
        stdErr is not null && stdErr.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase);

    /// <summary>The message for <paramref name="refusal"/>: a damaged-looking file is not called a playlist.</summary>
    public static string MessageFor(MediaFormatNotAllowedException refusal) =>
        refusal.Unrecognised ? UnreadableMessage : RefusedMessage;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".avif", ".gif", ".apng"
    };

    private static readonly HashSet<string> ListExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u8", ".m3u", ".ffconcat", ".concat", ".mpd", ".sdp", ".pls", ".xspf", ".asx", ".ism"
    };
}

/// <summary>
/// ffprobe refused a file because its format is not on the demuxer whitelist: a playlist, a list file
/// or another non-media format. It is an <see cref="InvalidOperationException"/> like every other
/// probe failure, so a caller that drops an unreadable asset keeps doing so; a caller that reads a
/// source catches it to answer <see cref="UserMediaInputFormats.RefusedCode"/>.
/// </summary>
public sealed class MediaFormatNotAllowedException : InvalidOperationException
{
    public MediaFormatNotAllowedException(string virtualPath, bool unrecognised = false)
        : base(unrecognised
            ? $"'{virtualPath}' could not be read by any whitelisted demuxer (damaged, incomplete or not a media file)."
            : $"'{virtualPath}' is not a media file the platform reads (its format is not on the input whitelist).")
    {
        Unrecognised = unrecognised;
    }

    /// <summary>True when no whitelisted demuxer recognised the content (rather than the whitelist refusing a named format).</summary>
    public bool Unrecognised { get; }
}
