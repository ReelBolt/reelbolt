using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the exact argv for one filmstrip sprite sheet: decimate to one frame per
/// <c>densitySec</c>, scale each thumb to <see cref="MediaDerivativeSpec.FilmstripThumbWidth"/>
/// wide, and tile them into a 10×10 MJPEG sheet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately no <c>-frames:v 1</c>.</b> ffmpeg's <c>tile</c> filter emits one image per full
/// 100-frame tile and then a final partial sheet, so a long source yields SEVERAL sheets — that is
/// the point of the <c>sheet-%03d.jpg</c> pattern. Pinning <c>-frames:v 1</c> would silently cap
/// every filmstrip at the first 100 thumbnails and the hover-scrub would go dead past
/// <c>100 * densitySec</c> seconds with no error anywhere. The index in
/// <see cref="FilmstripIndex"/> exists precisely to describe all of them.
/// </para>
/// </remarks>
public static class FilmstripArgvBuilder
{
    /// <summary>One output sheet per full 10×10 tile, zero-padded so lexical order is frame order.</summary>
    public const string SheetPattern = "sheet-%03d.jpg";

    /// <summary>
    /// Builds the argv for ALL requested densities in ONE ffmpeg run: the video is decoded once,
    /// <c>split</c> into one chain per density, and each chain both tiles its sprite sheets and
    /// feeds a <c>showinfo</c> inventory (discarded through <c>nullsink</c>). That replaces the
    /// former two decodes per density (sheet pass + inventory pass), i.e. six full decodes for the
    /// three densities, with one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per chain the order is unchanged: <c>fps=1/N</c> decimation, then <c>scale=160:-2</c> (even
    /// height, MJPEG cannot encode an odd one), then the optional HDR tone-map, then
    /// <c>tile=10x10</c>. The inventory branch is taken straight after <c>fps</c>, exactly where the
    /// former inventory pass put <c>showinfo</c>, so the thumbnail count it reports is the number
    /// of frames that reach <c>tile</c>.
    /// </para>
    /// <para>
    /// <b>Deliberately no <c>-frames:v 1</c></b> — see the type remarks; <c>tile</c> emits one image
    /// per full block and a final partial sheet.
    /// </para>
    /// <para>
    /// <paramref name="keyframesOnly"/> adds <c>-skip_frame nokey</c> as an INPUT option so the
    /// decoder only decodes keyframes. It is only valid for the engine's own proxy, whose GOP is
    /// <c>round(fps * 0.5)</c> frames with scene-cut keyframes off: a keyframe every ~0.5 s, so each
    /// sampled thumbnail is at most one proxy GOP (0.5 s) away from the requested sample time.
    /// Never set it for an arbitrary source, whose keyframe spacing is unknown.
    /// </para>
    /// <para>
    /// <c>-loglevel info</c> is load-bearing — <c>showinfo</c> logs at info level. Each inventory
    /// carries a distinct instance name (<see cref="InventoryFilterName"/>) so one stderr stream
    /// can be split per density with <see cref="InventoryLinePrefix"/>.
    /// </para>
    /// </remarks>
    public static string[] BuildMultiDensity(
        string inputPath,
        IReadOnlyList<(int DensitySec, string OutputPatternPath)> outputs,
        string? hdrKind = null,
        bool keyframesOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count == 0)
            throw new ArgumentException("At least one density is required.", nameof(outputs));

        var splitLabels = new List<string>(outputs.Count);
        var chains = new List<string>(outputs.Count * 4);
        for (int i = 0; i < outputs.Count; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputs[i].OutputPatternPath);
            int density = Math.Max(1, outputs[i].DensitySec);
            string n = FfmpegArgvFormat.Number(density);
            splitLabels.Add($"[v{n}]");
            chains.Add($"[v{n}]fps=1/{n},split=2[a{n}][b{n}]");
            chains.Add(
                $"[a{n}]scale={FfmpegArgvFormat.Number(MediaDerivativeSpec.FilmstripThumbWidth)}:-2," +
                HdrToneMap.Prefix(hdrKind) +
                $"tile={FfmpegArgvFormat.Number(FilmstripIndex.Cols)}x{FfmpegArgvFormat.Number(FilmstripIndex.Rows)}[s{n}]");
            chains.Add($"[b{n}]{InventoryFilterName(density)},nullsink");
        }

        string filter = $"[0:v:0]split={FfmpegArgvFormat.Number(outputs.Count)}{string.Concat(splitLabels)};" + string.Join(';', chains);

        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-hide_banner",
            "-loglevel", "info",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist
        };

        if (keyframesOnly)
        {
            args.Add("-skip_frame");
            args.Add("nokey");
        }

        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-filter_complex");
        args.Add(filter);

        foreach ((int densitySec, string pattern) in outputs)
        {
            args.Add("-map");
            args.Add($"[s{FfmpegArgvFormat.Number(Math.Max(1, densitySec))}]");
            args.Add("-c:v");
            args.Add(MediaDerivativeSpec.FilmstripCodec);
            args.Add("-q:v");
            args.Add(FfmpegArgvFormat.Number(MediaDerivativeSpec.FilmstripQuality));
            args.Add("-f");
            args.Add("image2");
            args.Add(pattern);
        }

        return args.ToArray();
    }

    /// <summary>The <c>showinfo</c> instance name that tags one density's inventory lines.</summary>
    public static string InventoryFilterName(int densitySec) =>
        $"showinfo@d{FfmpegArgvFormat.Number(Math.Max(1, densitySec))}";

    /// <summary>The stderr prefix of one density's <c>showinfo</c> lines (exact, so d1 never matches d10).</summary>
    public static string InventoryLinePrefix(int densitySec) =>
        $"[{InventoryFilterName(densitySec)} @";
}
