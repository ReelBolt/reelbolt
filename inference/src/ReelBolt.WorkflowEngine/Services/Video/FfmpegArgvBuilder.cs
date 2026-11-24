using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Builds the exact argv token arrays for every ffmpeg/ffprobe invocation this feature makes.
/// Centralizing this (rather than letting each detector interpolate its own strings) is what
/// makes R8 (culture-invariant numeric formatting), R11 (no unsanitized config/model input
/// reaching argv) and the "-protocol_whitelist file" containment discipline enforceable in one
/// place and testable without a real ffmpeg process.
///
/// Every returned array is a `string[]` of individually-quoted tokens, never a joined command
/// line — <see cref="IVideoToolRunner"/> passes these straight into
/// <c>ProcessStartInfo.ArgumentList</c>, so there is no shell to (mis)quote for and no injection
/// surface via spaces or shell metacharacters in a path.
/// </summary>
public static class FfmpegArgvBuilder
{
    /// <summary>
    /// Global flags common to every ffmpeg invocation in this feature: no interactive stdin
    /// (<c>-nostdin</c>), no version banner, unconditional overwrite of scratch outputs
    /// (<c>-y</c>), and a locked-down protocol whitelist so nothing this process spawns can
    /// touch anything but a local file (no <c>http://</c>, no <c>concat:</c>/<c>subfile:</c>
    /// tricks reaching outside the scratch dir) — see plan §2.
    /// </summary>
    private static readonly string[] BaseFlags = { "-nostdin", "-hide_banner", "-y" };

    /// <summary>
    /// The per-input containment options every builder here places before its <c>-i</c>: the
    /// <c>file</c> protocol only, and the demuxer whitelist (<see cref="UserMediaInputFormats"/>,
    /// security ENG-3), because these passes read a customer's own upload. ffmpeg applies them to the
    /// NEXT input only, so a builder with several inputs repeats them before each one.
    /// </summary>
    private static readonly string[] ProtocolWhitelist = UserMediaInputFormats.InputOptions();

    /// <summary>The protocol half alone, for the one builder that carries its own narrower demuxer list.</summary>
    private static readonly string[] ProtocolOnly = { "-protocol_whitelist", "file" };

    /// <summary>
    /// R09.1 motion pass: two samples a second through <c>signalstats</c>, printing each frame's mean luma difference
    /// (<c>YDIF</c>) to stdout. Audio is not decoded (<c>-an</c>); the frames are discarded (<c>-f null</c>).
    /// </summary>
    public static string[] BuildMotionArgs(string inputPath, int threads)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-vf");
        args.Add("fps=2,scale=320:-2,signalstats,metadata=print:key=lavfi.signalstats.YDIF:file=-");
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args.ToArray();
    }

    public static string[] BuildProbeArgs(string inputPath) =>
        new[]
        {
            "-v", "error",
            "-print_format", "json",
            "-show_format", "-show_streams",
            "-protocol_whitelist", "file",
            "-format_whitelist", UserMediaInputFormats.Whitelist,
            inputPath
        };

    /// <summary>
    /// <c>-af silencedetect=noise={db}dB:d={sec}</c>, discarding decoded output to <c>-f null -</c>.
    /// silencedetect logs its start/end markers at ffmpeg's "info" level, so loglevel is raised
    /// from the feature's default "error" specifically so <see cref="ISilenceDetector"/> has
    /// something to parse on stderr. <c>-vn</c> is load-bearing, not cosmetic: without it, ffmpeg's
    /// default stream selection still decodes the best video stream into the null muxer even
    /// though nothing here reads it — harmless on a small clip, but on real 4K/60+ source footage
    /// it turns an audio-only pass into a full software video decode of the entire file (minutes
    /// becoming the better part of an hour), which is exactly the kind of source this feature
    /// exists to handle. Mirrors <c>BuildExtractAudioArgs</c>'s <c>-vn</c> below.
    /// </summary>
    public static string[] BuildSilenceDetectArgs(string inputPath, double thresholdDb, double minSilenceSeconds)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "info" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-vn");
        args.Add("-af");
        args.Add($"silencedetect=noise={FfmpegArgvFormat.Number(thresholdDb)}dB:d={FfmpegArgvFormat.Number(minSilenceSeconds)}");
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args.ToArray();
    }

    /// <summary>
    /// <c>-vf select='gt(scene,{threshold})',showinfo</c>, discarding decoded output to
    /// <c>-f null -</c>. showinfo logs one line per frame it receives at ffmpeg's "info" level
    /// (only frames the <c>select</c> filter passed through, i.e. detected scene changes), so
    /// loglevel is raised the same way as silencedetect. <c>-an</c> skips decoding the audio
    /// stream this pass never touches — same rationale as <c>-vn</c> on
    /// <see cref="BuildSilenceDetectArgs"/> above, cheaper here since audio decode is the
    /// lightweight side, but still wasted work on a long source file.
    /// </summary>
    public static string[] BuildShotDetectArgs(string inputPath, double sceneThreshold, int threads)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "info" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-vf");
        args.Add($"select='gt(scene,{FfmpegArgvFormat.Number(sceneThreshold)})',showinfo");
        args.Add("-f");
        args.Add("null");
        args.Add("-");
        return args.ToArray();
    }

    /// <summary>
    /// Decimates/downscales to a raw RGB24 pixel grid for <see cref="IFrameGridSampler"/>:
    /// <c>-vf fps={sampleFps},scale={gridWidth}:{gridHeight}:flags=area,format=rgb24</c>, written
    /// as headerless <c>-f rawvideo -pix_fmt rgb24</c>. <c>flags=area</c> is load-bearing — a true
    /// box-average downscale, so each output pixel is the exact mean of its source block. No audio
    /// or subtitle streams are decoded (<c>-an -sn</c>).
    /// <para>
    /// <see cref="GridChroma.FullChroma"/> (the insert tracker's grid only) upsamples the source's
    /// subsampled chroma to 4:4:4 with a bilinear filter BEFORE the box-average downscale
    /// (<c>scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p,scale={w}:{h}:flags=area+full_chroma_int</c>),
    /// so the grid's chroma edges sit where its luma edges do rather than being nearest-neighbour
    /// smeared by the default 4:2:0 to RGB conversion.
    /// </para>
    /// <para>
    /// A positive <paramref name="maxFrames"/> stops the output at that many frames (<c>-frames:v</c>), whatever the
    /// file's own duration says, so a file that is longer than its header says cannot fill the scratch disk. 0 (the
    /// default) adds nothing: the argv is the one it always was.
    /// </para>
    /// </summary>
    public static string[] BuildGridSampleArgs(
        string inputPath, string outputRawPath, double sampleFps, int gridWidth, int gridHeight, int threads,
        GridChroma chroma = GridChroma.Default, long maxFrames = 0)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-sn");
        args.Add("-vf");
        args.Add(
            chroma == GridChroma.FullChroma
                ? $"fps={FfmpegArgvFormat.Number(sampleFps)}," +
                  "scale=iw:ih:flags=bilinear+full_chroma_inp,format=yuv444p," +
                  $"scale={FfmpegArgvFormat.Number(gridWidth)}:{FfmpegArgvFormat.Number(gridHeight)}:flags=area+full_chroma_int," +
                  "format=rgb24"
                : $"fps={FfmpegArgvFormat.Number(sampleFps)}," +
                  $"scale={FfmpegArgvFormat.Number(gridWidth)}:{FfmpegArgvFormat.Number(gridHeight)}:flags=area," +
                  "format=rgb24");
        if (maxFrames > 0)
        {
            args.Add("-frames:v");
            args.Add(FfmpegArgvFormat.Number(maxFrames));
        }

        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("rgb24");
        args.Add(outputRawPath);
        return args.ToArray();
    }

    /// <summary>
    /// Grayscale twin of <see cref="BuildGridSampleArgs"/> for the feature tracker:
    /// <c>-vf fps={sampleFps},scale={w}:{h}:flags=bicubic,format=gray</c>, headerless
    /// <c>-f rawvideo -pix_fmt gray</c>. Bicubic, not area: the tracker needs gradients, which a box
    /// average blurs more than it has to.
    /// <para>
    /// The pass reads a customer's file and writes one buffer the caller holds in memory, so it is held to
    /// the same bounds as the events pass: the input must be one of the media containers
    /// (<see cref="MediaDerivativeInputFormats.Whitelist"/>), and the output stops at
    /// <paramref name="maxFrames"/> frames and the matching byte count, whatever the file's own
    /// duration says.
    /// </para>
    /// </summary>
    public static string[] BuildGraySampleArgs(
        string inputPath, string outputRawPath, double sampleFps, int width, int height, int threads, long maxFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolOnly);
        args.Add("-format_whitelist");
        args.Add(MediaDerivativeInputFormats.Whitelist);
        args.Add("-threads");
        args.Add(FfmpegArgvFormat.Number(threads));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-an");
        args.Add("-sn");
        args.Add("-vf");
        args.Add(
            $"fps={FfmpegArgvFormat.Number(sampleFps)}," +
            $"scale={FfmpegArgvFormat.Number(width)}:{FfmpegArgvFormat.Number(height)}:flags=bicubic," +
            "format=gray");
        args.Add("-frames:v");
        args.Add(FfmpegArgvFormat.Number(maxFrames));
        args.Add("-fs");
        args.Add(FfmpegArgvFormat.Number(maxFrames * Math.Max(1L, (long)width * height)));
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("gray");
        args.Add(outputRawPath);
        return args.ToArray();
    }

    /// <summary>
    /// Extracts 16 kHz mono signed-16-bit PCM WAV — the fixed format both silence detection and
    /// WS3's ASR expect. An optional <c>[startSec, startSec+durationSec)</c> range restricts the
    /// extraction to a chunk (used for ASR chunk splitting); <c>-ss</c> is placed before
    /// <c>-i</c> for fast input seeking.
    /// </summary>
    public static string[] BuildExtractAudioArgs(
        string inputPath,
        string outputWavPath,
        double? startSeconds = null,
        double? durationSeconds = null)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        if (startSeconds.HasValue)
        {
            args.Add("-ss");
            args.Add(FfmpegArgvFormat.Number(startSeconds.Value));
        }

        args.Add("-i");
        args.Add(inputPath);

        if (durationSeconds.HasValue)
        {
            args.Add("-t");
            args.Add(FfmpegArgvFormat.Number(durationSeconds.Value));
        }

        args.Add("-vn");
        args.Add("-ac");
        args.Add("1");
        args.Add("-ar");
        args.Add("16000");
        args.Add("-sample_fmt");
        args.Add("s16");
        args.Add("-f");
        args.Add("wav");
        args.Add(outputWavPath);
        return args.ToArray();
    }

    /// <summary>
    /// Extracts a single representative frame as a JPEG for Phase 2 vision captioning:
    /// <c>-ss {atSec} -i {inputPath} -frames:v 1 -vf scale='min({maxWidth},iw)':-2 -f image2
    /// -c:v mjpeg -q:v 4 {outputJpgPath}</c>. <c>-ss</c> is placed before <c>-i</c> for fast input
    /// seeking, matching <see cref="BuildExtractAudioArgs"/>'s convention. The scale expression
    /// never upscales (<c>min(maxWidth, iw)</c>) and preserves aspect ratio (<c>-2</c> on height).
    /// </summary>
    public static string[] BuildKeyframeArgs(string inputPath, string outputJpgPath, double atSec, int maxWidth, string? hdrKind = null)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-ss");
        args.Add(FfmpegArgvFormat.Number(Math.Max(0, atSec)));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-vf");
        args.Add($"scale='min({FfmpegArgvFormat.Number(maxWidth)},iw)':-2" + HdrToneMap.Suffix(hdrKind));
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("mjpeg");
        args.Add("-q:v");
        args.Add("4");
        args.Add(outputJpgPath);
        return args.ToArray();
    }

    /// <summary>
    /// <see cref="BuildKeyframeArgs"/> with a normalized crop applied before the scale. The crop
    /// rectangle is clamped into the frame here, so a region hanging off an edge still yields a
    /// valid filter.
    /// </summary>
    public static string[] BuildCroppedKeyframeArgs(
        string inputPath, string outputJpgPath, double atSec,
        double x, double y, double width, double height, int maxWidth, string? hdrKind = null)
    {
        double w = Math.Clamp(width, 0.01, 1), h = Math.Clamp(height, 0.01, 1);
        double cx = Math.Clamp(x, 0, 1 - w), cy = Math.Clamp(y, 0, 1 - h);
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-ss");
        args.Add(FfmpegArgvFormat.Number(Math.Max(0, atSec)));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-vf");
        args.Add(
            $"crop=trunc(iw*{FfmpegArgvFormat.Number(w)}/2)*2:trunc(ih*{FfmpegArgvFormat.Number(h)}/2)*2:" +
            $"iw*{FfmpegArgvFormat.Number(cx)}:ih*{FfmpegArgvFormat.Number(cy)}," +
            // Always to maxWidth, upscaling a small crop: the point of the zoomed pass is to give
            // the vision model more pixels on the object than the full frame did.
            $"scale={FfmpegArgvFormat.Number(maxWidth)}:-2:flags=lanczos" + HdrToneMap.Suffix(hdrKind));
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("mjpeg");
        args.Add("-q:v");
        args.Add("3");
        args.Add(outputJpgPath);
        return args.ToArray();
    }

    /// <summary>
    /// N cheap input seeks hstacked into ONE contact-sheet JPEG — deliberately NOT a single-input
    /// select+tile pass, which would decode the shot's entire range instead of seeking N times.
    /// Each pane is scaled to <c>maxWidth/N</c> so the combined image stays at <paramref name="maxWidth"/>
    /// and the vision call's per-image token cost stays roughly flat. Emits <c>-ss {t_i} -i {input}</c>
    /// for each sample time, then an hstack filter_complex producing one JPEG.
    /// </summary>
    public static string[] BuildContactSheetArgs(
        string inputPath, string outputJpgPath, IReadOnlyList<double> atSecs, int maxWidth, string? hdrKind = null)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };

        int n = Math.Max(1, atSecs.Count);
        foreach (double atSec in atSecs)
        {
            // Per input: the options before the first -i do not reach the second.
            args.AddRange(ProtocolWhitelist);
            args.Add("-ss");
            args.Add(FfmpegArgvFormat.Number(Math.Max(0, atSec)));
            args.Add("-i");
            args.Add(inputPath);
        }

        int paneW = Math.Max(64, maxWidth / n);
        var filterParts = new List<string>();
        for (int i = 0; i < n; i++)
            filterParts.Add($"[{i}:v]scale={FfmpegArgvFormat.Number(paneW)}:-2{HdrToneMap.Suffix(hdrKind)}[p{i}]");

        string stackInputs = string.Concat(Enumerable.Range(0, n).Select(i => $"[p{i}]"));
        filterParts.Add($"{stackInputs}hstack=inputs={FfmpegArgvFormat.Number(n)}[out]");

        args.Add("-filter_complex");
        args.Add(string.Join(";", filterParts));
        args.Add("-map");
        args.Add("[out]");
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("mjpeg");
        args.Add("-q:v");
        args.Add("4");
        args.Add(outputJpgPath);
        return args.ToArray();
    }

    /// <summary>
    /// Dumps ONE native-resolution, square, centred grayscale patch as headerless rawvideo, for a
    /// pure-C# Laplacian-variance focus metric (Phase 4, D5-adjacent "sharpness"). Native resolution
    /// is load-bearing: the Phase 1 grid's box-average downscale destroys exactly the high-frequency
    /// content a sharpness metric measures, which is why this cannot be derived from grid.rgb (see
    /// docs/video-editing.md).
    /// </summary>
    /// <param name="patch">
    /// MUST be &lt;= min(probedWidth, probedHeight) — the caller computes it from the ffprobe
    /// result, because a crop larger than the frame fails the whole invocation.
    /// </param>
    /// <remarks>
    /// Deliberately its own invocation rather than a second output on <see cref="BuildKeyframeArgs"/>:
    /// a fault here must never be able to take Phase 2 captioning down with it.
    /// </remarks>
    public static string[] BuildSharpnessPatchArgs(string inputPath, string outputRawPath, double atSec, int patch)
    {
        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-ss");
        args.Add(FfmpegArgvFormat.Number(Math.Max(0, atSec)));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-vf");
        args.Add(
            $"crop={FfmpegArgvFormat.Number(patch)}:{FfmpegArgvFormat.Number(patch)}:" +
            $"(iw-{FfmpegArgvFormat.Number(patch)})/2:(ih-{FfmpegArgvFormat.Number(patch)})/2,format=gray");
        args.Add("-f");
        args.Add("rawvideo");
        args.Add("-pix_fmt");
        args.Add("gray");
        args.Add(outputRawPath);
        return args.ToArray();
    }

    /// <summary>
    /// ONE frame decoded at <paramref name="atSec"/>, fitted into an exact
    /// <paramref name="width"/>x<paramref name="height"/> box and encoded as JPEG — the thumbnail
    /// step's own extraction, deliberately NOT <see cref="BuildKeyframeArgs"/>.
    /// <para>
    /// <b>Why a separate method rather than a parameter on the existing one.</b> The keyframe
    /// builder's argv is a pinned, byte-for-byte contract for the vision-captioning path (its
    /// <c>-q:v 4</c> and its <c>scale='min(N,iw)':-2</c> are asserted directly), and the two
    /// callers genuinely want different things: captioning wants a cheap, downscaled frame whose
    /// aspect ratio is whatever the source had, while a cover image wants an exact canvas and a
    /// chosen fit. Sharing them would mean one caller's change silently re-rendering the other's
    /// output.
    /// </para>
    /// </summary>
    /// <param name="inputPath">The source file, as <see cref="IMediaWorkspace.MaterializeAsync"/> returned it.</param>
    /// <param name="outputJpgPath">Where the JPEG is written.</param>
    /// <param name="atSec">The moment to sample, in seconds of the input's own timeline.</param>
    /// <param name="width">Exact output width in pixels.</param>
    /// <param name="height">Exact output height in pixels.</param>
    /// <param name="cover">
    /// True fills the whole box and crops the overflow (<c>force_original_aspect_ratio=increase</c>
    /// then <c>crop</c>); false fits the whole frame inside it and pads the excess axis
    /// (<c>decrease</c> then <c>pad</c>).
    /// </param>
    /// <param name="quality">ffmpeg <c>-q:v</c> for the mjpeg encoder; 2 is best, 31 is worst.</param>
    /// <param name="overlay">
    /// The optional burned-in text band, or null for none. A non-null band ALWAYS carries a
    /// non-empty <see cref="ThumbnailOverlay.TextFilePath"/>: the text itself never enters this
    /// filter string, it is written to its own session file and referenced with
    /// <c>textfile=</c> + <c>expansion=none</c> — the same two-layer defence
    /// <c>DrawtextFilterBuilder</c> documents for overlays and captions.
    /// </param>
    public static string[] BuildThumbnailArgs(
        string inputPath,
        string outputJpgPath,
        double atSec,
        int width,
        int height,
        bool cover,
        int quality,
        ThumbnailOverlay? overlay = null,
        string? hdrKind = null)
    {
        // Last line of defence. A non-finite moment would be written into -ss as "NaN"/"Infinity",
        // which ffmpeg cannot parse; negatives are clamped rather than refused because a seek to 0
        // is the caller's obvious intent.
        if (!double.IsFinite(atSec))
            throw new ArgumentOutOfRangeException(nameof(atSec), atSec, "The thumbnail moment must be a finite number of seconds.");

        int w = Math.Clamp(width, 16, 4096);
        int h = Math.Clamp(height, 16, 4096);
        int q = Math.Clamp(quality, 2, 31);

        var filters = new List<string>(4);
        if (hdrKind is not null)
            filters.Add(HdrToneMap.Filter(hdrKind));
        if (cover)
        {
            filters.Add($"scale={w}:{h}:force_original_aspect_ratio=increase");
            filters.Add($"crop={w}:{h}");
        }
        else
        {
            filters.Add($"scale={w}:{h}:force_original_aspect_ratio=decrease");
            filters.Add($"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black");
        }

        if (overlay is not null)
            filters.Add(BuildThumbnailDrawtext(overlay));

        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-ss");
        args.Add(FfmpegArgvFormat.Number(Math.Max(0, atSec)));
        args.Add("-i");
        args.Add(inputPath);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-vf");
        args.Add(string.Join(",", filters));
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("mjpeg");
        args.Add("-q:v");
        args.Add(FfmpegArgvFormat.Number(q));
        args.Add(outputJpgPath);
        return args.ToArray();
    }

    /// <summary>
    /// The one drawtext filter for a thumbnail text band. The text is referenced by <c>textfile=</c> with
    /// <c>expansion=none</c>, never interpolated. A <c>none</c> box colour means NO box: ffmpeg's drawtext
    /// cannot parse <c>boxcolor=none</c> (it fails the whole graph with "Cannot find color 'none'"), so the box
    /// is switched off with <c>box=0</c> and no <c>boxcolor</c> is emitted at all.
    /// </summary>
    private static string BuildThumbnailDrawtext(ThumbnailOverlay overlay)
    {
        // The band position is a fraction of the canvas height, expressed through drawtext's
        // own text_h so the box stays centred on the band whatever the font size resolves to.
        string y = overlay.BandCenterFraction switch
        {
            0 => "(h-text_h)/2",
            _ => $"h*{FfmpegArgvFormat.Number(overlay.BandCenterFraction)}-text_h/2"
        };

        bool boxed = !string.Equals(overlay.BoxColor, "none", StringComparison.OrdinalIgnoreCase);
        string box = boxed ? $":box=1:boxborderw=14:boxcolor={overlay.BoxColor}" : ":box=0";

        return $"drawtext={DrawtextFilterBuilder.FontFileOption(overlay.FontFilePath)}" +
            $"textfile='{DrawtextFilterBuilder.EscapeFilterPath(overlay.TextFilePath)}'" +
            ":expansion=none" +
            $":fontsize={FfmpegArgvFormat.Number(overlay.FontSizePx)}" +
            $":fontcolor={overlay.FontColor}" +
            box +
            ":x=(w-text_w)/2" +
            $":y={y}";
    }

    /// <summary>
    /// The argv for the SECOND thumbnail stage: burn the text band onto the already-extracted frame
    /// (a JPEG written by <see cref="BuildThumbnailArgs"/>). Splitting the stages means a failure names the
    /// stage that broke (frame extraction vs text overlay). Same textfile=/expansion=none discipline.
    /// </summary>
    public static string[] BuildThumbnailOverlayArgs(
        string frameJpgPath, string outputJpgPath, int quality, ThumbnailOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        int q = Math.Clamp(quality, 2, 31);

        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        args.Add("-i");
        args.Add(frameJpgPath);
        args.Add("-frames:v");
        args.Add("1");
        args.Add("-vf");
        args.Add(BuildThumbnailDrawtext(overlay));
        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add("mjpeg");
        args.Add("-q:v");
        args.Add(FfmpegArgvFormat.Number(q));
        args.Add(outputJpgPath);
        return args.ToArray();
    }

    /// <summary>
    /// The argv for one <c>ExtractFrame</c> frame grab: <c>First</c> takes the first decoded frame, <c>Time</c>
    /// seeks before the input (a fast keyframe seek, then the decoder runs up to the exact frame), and
    /// <c>Last</c> seeks to one second before the end and keeps overwriting a single image
    /// (<c>-update 1</c>) so the final decodable frame is the one that stays. A crop (fractions of the frame) is
    /// applied before the downscale, which shrinks only the longer side and never upscales. Every number is
    /// formatted here from a typed value; nothing a user typed reaches the argv as text.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not <c>png</c> or <c>jpeg</c>.</exception>
    public static string[] BuildExtractFrameArgs(
        string inputPath,
        string outputPath,
        FrameAt at,
        double? atSec,
        string format,
        int? maxLongSide,
        NormalizedCrop? crop,
        string? hdrKind = null)
    {
        bool jpeg = format switch
        {
            "png" => false,
            "jpeg" => true,
            _ => throw new ArgumentException($"Unsupported frame format '{format}'.", nameof(format))
        };

        var filters = new List<string>(4);
        if (hdrKind is not null)
            filters.Add(HdrToneMap.Filter(hdrKind));
        if (crop is not null)
        {
            static string F(double v) => Math.Round(Math.Clamp(v, 0, 1), 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            filters.Add(
                $"crop=w=trunc(iw*{F(crop.W)}/2)*2:h=trunc(ih*{F(crop.H)}/2)*2:x=trunc(iw*{F(crop.X)}/2)*2:y=trunc(ih*{F(crop.Y)}/2)*2");
        }

        if (maxLongSide is { } side)
        {
            string n = FfmpegArgvFormat.Number(Math.Clamp(side, ExtractFrameStepConfig.MinLongSide, ExtractFrameStepConfig.MaxLongSideLimit));
            filters.Add($"scale=w='if(gt(iw,ih),min({n},iw),-2)':h='if(gt(iw,ih),-2,min({n},ih))'");
        }

        if (jpeg)
            filters.Add("format=yuvj420p");

        List<string> args = new(BaseFlags) { "-loglevel", "error" };
        args.AddRange(ProtocolWhitelist);
        switch (at)
        {
            case FrameAt.Time:
                args.Add("-ss");
                args.Add(Math.Round(Math.Clamp(atSec ?? 0, 0, 360000), 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case FrameAt.Last:
                args.Add("-sseof");
                args.Add("-1");
                break;
        }

        args.Add("-i");
        args.Add(inputPath);
        if (at == FrameAt.Last)
        {
            args.Add("-update");
            args.Add("1");
        }
        else
        {
            args.Add("-frames:v");
            args.Add("1");
        }

        if (filters.Count > 0)
        {
            args.Add("-vf");
            args.Add(string.Join(",", filters));
        }

        args.Add("-f");
        args.Add("image2");
        args.Add("-c:v");
        args.Add(jpeg ? "mjpeg" : "png");
        if (jpeg)
        {
            args.Add("-q:v");
            args.Add("2");
        }

        args.Add(outputPath);
        return args.ToArray();
    }
}

/// <summary>
/// The burned-in text band for <see cref="FfmpegArgvBuilder.BuildThumbnailArgs"/>: everything the
/// drawtext filter needs, with the TEXT itself expressed as a session file path rather than a
/// string. <b>Every one of these values is first-party</b> — the colours are allowlisted by the
/// caller, the size and band come from clamped integers, and the path is one the media workspace
/// minted — so nothing here is a place a caller-supplied string reaches ffmpeg's filter parser.
/// </summary>
/// <param name="TextFilePath">The path string of the session file holding the sanitized text.</param>
/// <param name="FontFilePath">The installed font's path (see <c>VideoEditingOptions.FontFilePath</c>), or null in a remote session (no <c>fontfile=</c>).</param>
/// <param name="FontSizePx">Absolute font size in pixels, already derived from the canvas height.</param>
/// <param name="FontColor">An allowlisted ffmpeg colour for the glyphs.</param>
/// <param name="BoxColor">An allowlisted ffmpeg colour for the box behind them, or <c>none</c>.</param>
/// <param name="BandCenterFraction">
/// Where the band's centre sits vertically, as a fraction of the canvas height (0 means centred).
/// </param>
public sealed record ThumbnailOverlay(
    string TextFilePath,
    string? FontFilePath,
    int FontSizePx,
    string FontColor,
    string BoxColor,
    double BandCenterFraction);

/// <summary>
/// How a raw RGB grid is decoded from the source's (usually 4:2:0) pixel format. <see cref="Default"/> is
/// the decode every grid has always used. <see cref="FullChroma"/> is for the insert-plate tracker only:
/// it keeps the plate's chroma edge at full resolution before the downscale.
/// </summary>
public enum GridChroma
{
    Default = 0,
    FullChroma = 1,
}
