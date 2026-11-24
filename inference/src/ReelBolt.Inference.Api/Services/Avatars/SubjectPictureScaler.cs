using System.Globalization;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>One subject picture as it is sent: a re-encoded copy no larger than the model needs.</summary>
/// <param name="ContentType">The media type of <paramref name="Bytes"/>.</param>
/// <param name="Bytes">The bytes sent.</param>
/// <param name="Width">Pixel width of the copy, when its header could be read.</param>
/// <param name="Height">Pixel height of the copy, when its header could be read.</param>
public sealed record ScaledSubjectPicture(string ContentType, byte[] Bytes, int? Width, int? Height)
{
    /// <summary>The copy as a <c>data:</c> URI, the form an avatar's picture leaves the host in (no link a vendor could fetch later).</summary>
    public string ToDataUri() => "data:" + ContentType + ";base64," + Convert.ToBase64String(Bytes);
}

/// <summary>
/// Makes the copy of an avatar's or object's picture that is sent to a picture model: the same recipe as the engine's
/// still maker (<c>AvatarStillMaker</c>), so a subject looks the same to the model whichever host sends it.
/// </summary>
public interface ISubjectPictureScaler
{
    /// <summary>
    /// A JPEG copy whose longest side is at most <see cref="FfmpegSubjectPictureScaler.MaxPictureSidePx"/>; the original
    /// when the copy cannot be made and the original is small; null when neither can be sent.
    /// </summary>
    Task<ScaledSubjectPicture?> ScaleAsync(ProjectSubjectPicture picture, CancellationToken ct);
}

/// <summary>
/// <see cref="ISubjectPictureScaler"/> over ffmpeg, with first-party argv: one frame, scaled down only, encoded as
/// JPEG, which also leaves the file's own metadata (camera, place, time) behind. Inputs are limited to the still-image
/// demuxers and the file protocol; the scratch directory is always removed.
/// </summary>
public sealed class FfmpegSubjectPictureScaler : ISubjectPictureScaler
{
    /// <summary>The longest side a picture is sent at (enough for a 1K result; less of a person's photo leaves the host).</summary>
    public const int MaxPictureSidePx = 1536;

    /// <summary>A picture that cannot be re-encoded is sent as it is only below this size.</summary>
    public const long MaxUnscaledBytes = 8L * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner _runner;
    private readonly ILogger<FfmpegSubjectPictureScaler> _logger;

    public FfmpegSubjectPictureScaler(IProcessRunner runner, ILogger<FfmpegSubjectPictureScaler> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>The ffmpeg argv of one copy. Internal so a test can pin it without running a process.</summary>
    internal static IReadOnlyList<string> BuildArgs(string input, string output)
    {
        string side = MaxPictureSidePx.ToString(CultureInfo.InvariantCulture);
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-protocol_whitelist", "file", "-format_whitelist", "image2,png_pipe,jpeg_pipe,webp_pipe",
            "-i", input,
            "-vf", $"scale=w='min({side},iw)':h='min({side},ih)':force_original_aspect_ratio=decrease",
            "-frames:v", "1", "-c:v", "mjpeg", "-q:v", "3", "-f", "image2", output
        ];
    }

    /// <inheritdoc />
    public async Task<ScaledSubjectPicture?> ScaleAsync(ProjectSubjectPicture picture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(picture);
        string scratch = Path.Combine(Path.GetTempPath(), "reelbolt-subject-picture-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            string input = Path.Combine(scratch, "picture.src");
            string output = Path.Combine(scratch, "picture.jpg");
            await File.WriteAllBytesAsync(input, picture.Bytes, ct);

            ProcessRunResult result = await _runner.RunAsync("ffmpeg", BuildArgs(input, output), Timeout, ct);
            if (result.Succeeded && File.Exists(output))
            {
                byte[] scaled = await File.ReadAllBytesAsync(output, ct);
                if (scaled.Length > 0 && string.Equals(ProjectSubjectReader.ContentTypeOf(scaled), "image/jpeg", StringComparison.Ordinal))
                    return await SizedAsync("image/jpeg", scaled, ct);
            }

            _logger.LogWarning("Re-encoding a subject picture failed (exit {Exit}); sending it as it is only if small", result.ExitCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-encoding a subject picture failed; sending it as it is only if small");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                _logger.LogWarning(ex, "Could not remove the subject picture scratch directory {Scratch}", scratch);
            }
        }

        return picture.Bytes.LongLength <= MaxUnscaledBytes
            ? await SizedAsync(picture.ContentType, picture.Bytes, ct)
            : null;
    }

    private static async Task<ScaledSubjectPicture> SizedAsync(string contentType, byte[] bytes, CancellationToken ct)
    {
        (int Width, int Height)? size = null;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            size = await ImageHeader.ReadDisplaySizeAsync(stream, ImageHeader.MaxJpegScanBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A size that cannot be read is simply not stated; the caller decides what that means for its price.
        }

        return new ScaledSubjectPicture(contentType, bytes, size?.Width, size?.Height);
    }
}
