using System.Globalization;
using System.Text;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>
/// Turns decoded per-frame masks into ONE full-length grey8 video, with first-party ffmpeg argv only
/// (initiative "prompted region effects", plan §3, §5): the model supplies masks, this file supplies
/// every argument and every byte.
///
/// <para>
/// <b>Why frames, and not a raw stream.</b> The mask has to be a real video at the source's exact size,
/// frame count and rate so the compile step can conform it with the same geometry as the picture. The
/// obvious encoding — one headerless <c>-f rawvideo</c> input — would need a temporary file of
/// <c>width × height × frames</c> bytes, which is 3.7 GB for a single 60 s 1080p30 clip. A sequence of
/// 1-bit <c>pbm</c> frames holds the same binary mask in one bit per pixel (466 MB worst case, and this
/// step's own cap bounds the clip), is written without any image library, and is read by ffmpeg's own
/// <c>image2</c> demuxer with nothing but <c>-f image2 -i {pattern}</c>. The mask is binary by
/// construction, so nothing is lost: 1 bit IS the mask.
/// </para>
///
/// <para>
/// <b>Determinism.</b> Every number formatted here is invariant-culture; there is no user text in any
/// argument (the prompt never reaches argv — it goes only to the model); the frame rate is the source's
/// own probed rate. The output is FFV1 grey8 in Matroska, lossless, because the compile step must read
/// back the exact mask this step verified, not an approximation of it.
/// </para>
/// </summary>
public static class SegmentationMaskArgvBuilder
{
    /// <summary>The session directory the mask frames and the encoded mask live in.</summary>
    public const string Directory = "segmentation";

    /// <summary>The <c>image2</c> pattern the frames are numbered by (1-based, zero-padded to six digits).</summary>
    public const string FramePattern = Directory + "/mask-%06d.pbm";

    /// <summary>The session-relative path of frame <paramref name="index"/> (1-based, the pattern's own numbering).</summary>
    public static string FrameVirtualPath(int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index), index, "Frame numbering starts at 1.");
        return string.Create(CultureInfo.InvariantCulture, $"{Directory}/mask-{index:D6}.pbm");
    }

    /// <summary>
    /// The argv that encodes the written frames into <paramref name="outputPath"/>. Exact and
    /// deterministic: the same frame count and rate always produce the same element list.
    /// </summary>
    /// <param name="framePattern">The <c>image2</c> input pattern, as <see cref="FramePattern"/> spells it (or its materialised path).</param>
    /// <param name="fps">The source's frame rate — the mask must be at exactly this rate.</param>
    /// <param name="outputPath">Where the FFV1 grey8 Matroska lands.</param>
    public static IReadOnlyList<string> BuildEncodeArgs(string framePattern, double fps, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(framePattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), fps, "A mask video needs a positive frame rate.");

        string rate = fps.ToString("0.######", CultureInfo.InvariantCulture);

        return
        [
            "-hide_banner",
            "-nostdin",
            "-y",
            "-f", "image2",
            "-start_number", "1",
            "-framerate", rate,
            "-i", framePattern,
            "-an",
            "-sn",
            "-dn",
            "-c:v", "ffv1",
            "-pix_fmt", "gray",
            "-r", rate,
            outputPath
        ];
    }

    /// <summary>Session path the size-bounded vendor copy is written at.</summary>
    public const string VendorCopyVirtualPath = Directory + "/vendor-copy.mp4";

    /// <summary>
    /// The argv for the copy the vendor downloads instead of a rotated or over-size original. ffmpeg's
    /// autorotate is ON (the default; never <c>-noautorotate</c>), video only, no scaling, every frame
    /// kept (<c>-fps_mode passthrough</c>) and all metadata cleared so the output carries NO display
    /// matrix: the vendor decodes the same upright frames, at the same width and height, every engine
    /// decode sees, so masks index the source's own grid. H.264 at CRF 20 under a VBV ceiling of
    /// <paramref name="maxrateKbps"/> (1 s buffer), which bounds the file to roughly
    /// <c>maxrate × duration</c>. The caller still checks the delivered size before minting a URL.
    /// </summary>
    public static IReadOnlyList<string> BuildVendorCopyArgs(string inputPath, string outputPath, int maxrateKbps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxrateKbps, 1);
        string rate = maxrateKbps.ToString(CultureInfo.InvariantCulture) + "k";
        return
        [
            "-hide_banner",
            "-nostdin",
            "-y",
            // The customer's source: the same per-input whitelists as every other user-media input (security ENG-3).
            .. UserMediaInputFormats.InputOptions(),
            "-i", inputPath,
            "-map", "0:v:0",
            "-an",
            "-sn",
            "-dn",
            "-map_metadata", "-1",
            "-map_chapters", "-1",
            "-fps_mode", "passthrough",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "20",
            "-maxrate", rate,
            "-bufsize", rate,
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath
        ];
    }

    /// <summary>
    /// One decoded mask frame as a binary <c>pbm</c> (<c>P4</c>) file: the magic, then the dimensions,
    /// then <c>width × height</c> bits packed row-major, most significant bit first, each ROW padded to a
    /// whole byte. **A set bit is BLACK (0) in PBM**, so the packing is inverted against the mask's own
    /// polarity: a BACKGROUND pixel (mask byte 0) sets its bit, an OBJECT pixel clears it. The encoded
    /// grey video therefore reads 255 on the object and 0 on the background — the polarity the artifact
    /// contract and the compile step assert (255 = object). Packing object=1 here instead stores the
    /// inverse mask and paints the effect on the background (caught end to end by T-009b, F-9).
    /// </summary>
    public static byte[] EncodePbmFrame(ReadOnlySpan<byte> mask, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "A mask frame needs a positive width.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "A mask frame needs a positive height.");
        if (mask.Length < width * height)
        {
            throw new ArgumentException(
                $"A mask frame holds {mask.Length} bytes, fewer than the {width}x{height} = {width * height} it must cover.",
                nameof(mask));
        }

        int rowBytes = (width + 7) / 8;
        byte[] header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture, $"P4\n{width} {height}\n"));
        byte[] pbm = new byte[header.Length + (rowBytes * height)];
        header.CopyTo(pbm, 0);

        for (int y = 0; y < height; y++)
        {
            int rowStart = header.Length + (y * rowBytes);
            ReadOnlySpan<byte> source = mask.Slice(y * width, width);
            for (int x = 0; x < width; x++)
            {
                if (source[x] != 0) continue; // a set PBM bit is BLACK: invert so 255 = object
                pbm[rowStart + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        return pbm;
    }
}
