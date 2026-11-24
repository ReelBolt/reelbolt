using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Still images as edit sources (docs/video-editing.md "Still images"). The analyze step renders an
/// image source ONCE into a short silent clip with a gentle push-in (a still that does not move reads
/// as a frozen frame in a reel) and records that clip as the source, exactly like an author-trimmed
/// window — so shot ids, the editor, captions, music and the compile all work on it unchanged.
/// </summary>
public static class StillImageClip
{
    /// <summary>One beat of a move: the smallest piece of a still the editor can keep.</summary>
    public const double BeatSeconds = 1.5;

    /// <summary>Beats per move, so a move is kept for one beat (a quick hit) or all of them.</summary>
    public const int BeatsPerMove = 2;

    /// <summary>How far a push-in or pull-out travels (1.08 = 8%), and the zoom a pan holds to have room to travel.</summary>
    public const double PushIn = 0.08, PanZoom = 1.12;

    /// <summary>The longest side of the rendered clip, so a 6000 px photo does not become a 6K video.</summary>
    public const int MaxSide = 1920;

    public const int Fps = 30;

    /// <summary>Longest photo description a still's first beat carries.</summary>
    public const int MaxPhotoChars = 200;

    /// <summary>The moves, in clip order. The pan runs along the image's long side.</summary>
    public static IReadOnlyList<string> Moves(int width, int height) =>
        ["push in", "pull out", height > width ? "pan down" : "pan right"];

    /// <summary>The whole clip's length: every move's beats back to back.</summary>
    public static double TotalSeconds => 3 * BeatsPerMove * BeatSeconds;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tif", ".tiff", ".avif"
    };

    private static readonly HashSet<string> ImageCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "mjpeg", "webp", "bmp", "tiff", "av1_image", "jpegls"
    };

    /// <summary>Whether a storage key names an image by its extension.</summary>
    public static bool IsImagePath(string? path) =>
        !string.IsNullOrEmpty(path) && ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Whether a probe describes a single still picture: an image codec and no real duration. A
    /// motion-JPEG video has a duration, so it stays a video.
    /// </summary>
    public static bool IsImageProbe(MediaProbeResult probe) =>
        probe.VideoCodec is { } codec && ImageCodecs.Contains(codec) && probe.DurationSec < 0.2;

    /// <summary>The clip's frame size: the image's own aspect, longest side at most <see cref="MaxSide"/>, both sides even.</summary>
    public static (int Width, int Height) Size(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (1080, 1080);
        double scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2);
        return (Even(width * scale), Even(height * scale));
    }

    /// <summary>
    /// ffmpeg argv: the image looped at <see cref="Fps"/>, upscaled 2x before <c>zoompan</c> (which
    /// moves in whole pixels and judders at the output size), then the three <see cref="Moves"/> back
    /// to back — push in about the centre, pull back out, and a pan along the long side at
    /// <see cref="PanZoom"/> — encoded H.264 with no audio stream.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(string input, string output, int width, int height)
    {
        int m = (int)Math.Round(BeatsPerMove * BeatSeconds * Fps);
        string M = m.ToString(CultureInfo.InvariantCulture), M2 = (2 * m).ToString(CultureInfo.InvariantCulture);
        string p = PushIn.ToString("0.###", CultureInfo.InvariantCulture), pz = PanZoom.ToString("0.###", CultureInfo.InvariantCulture);
        bool vertical = height > width;
        string z = $"if(lt(on,{M}),1+{p}*on/{M},if(lt(on,{M2}),1+{p}-{p}*(on-{M})/{M},{pz}))";
        string centreX = "iw/2-(iw/zoom/2)", centreY = "ih/2-(ih/zoom/2)";
        string panX = vertical ? centreX : $"if(lt(on,{M2}),{centreX},(iw-iw/zoom)*(on-{M2})/{M})";
        string panY = vertical ? $"if(lt(on,{M2}),{centreY},(ih-ih/zoom)*(on-{M2})/{M})" : centreY;
        string frames = (3 * m).ToString(CultureInfo.InvariantCulture);
        return
        [
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            .. UserMediaInputFormats.InputOptions(),
            "-loop", "1", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-i", input,
            "-vf",
            $"scale={width * 2}:{height * 2}:force_original_aspect_ratio=increase,crop={width * 2}:{height * 2}," +
            $"zoompan=z='{z}':x='{panX}':y='{panY}':d=1:s={width}x{height}:fps={Fps},format=yuv420p",
            "-frames:v", frames, "-an",
            "-c:v", "libx264", "-crf", "16", "-preset", "veryfast", "-movflags", "+faststart", output
        ];
    }

    /// <summary>
    /// The clip's scenes: one per beat of each move, so the analysis offers every beat as its own
    /// shot. The description leads with what the photo shows (<paramref name="photo"/>, from one
    /// vision caption) and names the move and the beat, which is how the editor chooses the motion
    /// and, by keeping one beat or both, its length.
    /// </summary>
    public static IReadOnlyList<ReelBolt.Shared.Workflows.DeclaredScene> Scenes(int width, int height, string photo)
    {
        int beatFrames = (int)Math.Round(BeatSeconds * Fps);
        string described = photo.Length <= MaxPhotoChars ? photo : photo[..MaxPhotoChars].TrimEnd() + "…";
        var scenes = new List<ReelBolt.Shared.Workflows.DeclaredScene>();
        int frame = 0;
        foreach (string move in Moves(width, height))
        {
            for (int beat = 1; beat <= BeatsPerMove; beat++)
            {
                // Only the first beat carries what the photo shows: six copies of one caption per
                // photo pushed a 66-shot view over its budget, and the view's only remedy (dropping
                // detail to None) then stripped every caption, move names included.
                string what = frame == 0 ? $"{described} Still photo" : "Same photo as this source's first shot";
                scenes.Add(new ReelBolt.Shared.Workflows.DeclaredScene(
                    $"{move} ({beat}/{BeatsPerMove})", frame, beatFrames,
                    $"{what}: slow {move}, beat {beat} of {BeatsPerMove} (one beat for a quick hit, both for the whole move)."));
                frame += beatFrames;
            }
        }

        return scenes;
    }
}
