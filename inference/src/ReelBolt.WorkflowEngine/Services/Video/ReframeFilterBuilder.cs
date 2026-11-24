using System.Text;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One span's picture branch when it is reframed and/or padded inside the subject safe area —
/// everything <see cref="ReframeFilterBuilder.SpanVideoChain"/> needs, all first-party values.
/// </summary>
/// <param name="Stage">The span's <see cref="ReframeFilterBuilder.ZoompanStage"/>, or null.</param>
/// <param name="NormalizeSuffix">The encoder's own canvas-fit suffix for this span (may be empty).</param>
/// <param name="SafePad">Pad the span inside the safe margin instead of <paramref name="NormalizeSuffix"/>.</param>
/// <param name="Blur">Fill around the fitted picture with its blurred copy instead of black.</param>
public sealed record ReframeSpanChain(
    int InputIndex,
    int SpanIndex,
    string Start,
    string End,
    string? Stage,
    string NormalizeSuffix,
    bool SafePad,
    double SafeMargin,
    bool Blur,
    int CanvasWidth,
    int CanvasHeight,
    int FpsNum,
    int FpsDen,
    string LengthSuffix,
    string FadeSuffix,
    string? HdrKind = null,
    string NormalizeHead = "");

/// <summary>
/// Filter strings for keyframed reframing and the subject safe-area pad (docs/video-editing.md
/// "Keyframed reframing"). Every number comes from validated config and probed media.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>zoompan</c>, not <c>crop</c> with <c>t</c>-expressions.</b> <c>crop</c> evaluates its
/// width and height once, at configuration, so it can pan but never zoom; a time-varying zoom
/// through it would need a per-frame <c>scale</c> (whose size changes mid-stream are not
/// supported downstream) or an upscale of every frame by the largest zoom (8x — not viable).
/// <c>zoompan</c> evaluates zoom, x and y for every input frame and scales the window back to a
/// fixed size. With <c>d=1</c> it emits exactly one frame per input frame, and with a <c>fps</c>
/// filter in front at the same rate it is fed constant-rate frames from 0, so its own regenerated
/// timestamps equal the input's: frame-accurate, frame rate unchanged. Its window is integer
/// (top-left on the chroma grid); <see cref="ReframeGeometry.PixelWindow"/> mirrors that exactly so
/// geometry lands on the pixels the filter keeps.
/// </para>
/// <para>
/// The stage runs on the span's own clock (<c>it</c> = seconds since the span's first frame, after
/// <c>trim</c> + <c>setpts=PTS-STARTPTS</c>) at the clip's own size, BEFORE the canvas fit, so a
/// reframe composes with Crop, Subject, Pad and the blurred fill unchanged.
/// </para>
/// </remarks>
public static class ReframeFilterBuilder
{
    /// <summary>
    /// The <c>fps,zoompan</c> stage for the span <c>[spanStartSec, spanEndSec)</c> of a source
    /// clip, or null when none of <paramref name="reframes"/> (the clip's, validated) overlaps it.
    /// </summary>
    public static string? ZoompanStage(
        IReadOnlyList<VideoReframe> reframes, double spanStartSec, double spanEndSec,
        int sourceWidth, int sourceHeight, int fpsNum, int fpsDen)
    {
        List<VideoReframe> overlapping = reframes
            .Where(r => Overlaps(r, spanStartSec, spanEndSec))
            .OrderBy(r => r.StartSec)
            .ToList();
        if (overlapping.Count == 0 || sourceWidth <= 0 || sourceHeight <= 0)
            return null;

        double length = spanEndSec - spanStartSec;
        string z = Curve(overlapping, spanStartSec, length, k => k.Zoom, ReframeWindow.Identity.Zoom);
        string cx = Curve(overlapping, spanStartSec, length, k => k.CenterX, ReframeWindow.Identity.CenterX);
        string cy = Curve(overlapping, spanStartSec, length, k => k.CenterY, ReframeWindow.Identity.CenterY);
        string rate = $"{fpsNum}/{fpsDen}";
        return $"fps={rate},zoompan=z='{z}':" +
               $"x='2*floor(max(0,({cx})*iw-iw/zoom/2)/2)':" +
               $"y='2*floor(max(0,({cy})*ih-ih/zoom/2)/2)':" +
               $"d=1:s={sourceWidth}x{sourceHeight}:fps={rate}";
    }

    /// <summary>
    /// The cover crop (<c>crop=cw:ch:x:y</c>, after <c>scale=…:force_original_aspect_ratio=increase</c>) of a span
    /// with a <see cref="ReframeAnchor.Visible"/> reframe, or null when no overlapping reframe is visible-anchored
    /// or the fit crops nothing. On each cropped axis the crop's offset is a piecewise expression in <c>t</c>
    /// (the span's clock, like zoompan's <c>it</c>): <c>x = clamp(S * iw - ow / 2, 0, iw - ow)</c>, where the
    /// strip centre <c>S = 0.5 + min(0, c*z - 0.5) + max(0, c*z - z + 0.5)</c> is
    /// <c>0.5 + (c - clamp(c, 0.5/z, 1 - 0.5/z)) * z</c> written without a clamp of its own; crop floors the
    /// offset onto the chroma grid. Outside a visible reframe the strip holds the clip's own focus
    /// (<paramref name="focusX"/>/<paramref name="focusY"/>, null = centred).
    /// </summary>
    public static string? VisibleCoverCrop(
        IReadOnlyList<VideoReframe> reframes, double spanStartSec, double spanEndSec,
        int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight, double? focusX, double? focusY)
    {
        List<VideoReframe> overlapping = reframes
            .Where(r => Overlaps(r, spanStartSec, spanEndSec))
            .OrderBy(r => r.StartSec)
            .ToList();
        if (!overlapping.Any(r => r.Anchor == ReframeAnchor.Visible) || sourceWidth <= 0 || sourceHeight <= 0)
            return null;
        bool cropX = (long)sourceWidth * canvasHeight > (long)canvasWidth * sourceHeight;
        bool cropY = (long)sourceHeight * canvasWidth > (long)canvasHeight * sourceWidth;
        if (!cropX && !cropY)
            return null;

        double length = spanEndSec - spanStartSec;
        string x = cropX
            ? $"'max(0,min(iw-ow,({StripCurve(overlapping, spanStartSec, length, k => k.CenterX, focusX ?? 0.5)})*iw-ow/2))'"
            : "'(iw-ow)/2'";
        string y = cropY
            ? $"'max(0,min(ih-oh,({StripCurve(overlapping, spanStartSec, length, k => k.CenterY, focusY ?? 0.5)})*ih-oh/2))'"
            : "'(ih-oh)/2'";
        return $"crop={canvasWidth}:{canvasHeight}:{x}:{y}";
    }

    /// <summary>Whether <paramref name="reframe"/> covers any of <c>[spanStartSec, spanEndSec)</c>.</summary>
    public static bool Overlaps(VideoReframe reframe, double spanStartSec, double spanEndSec) =>
        reframe.StartSec < spanEndSec && (reframe.ToSec ?? double.PositiveInfinity) > spanStartSec;

    /// <summary>The even-sized box inside the canvas that a safe-area pad fits the clip into.</summary>
    public static (int Width, int Height) SafeBox(int canvasWidth, int canvasHeight, double margin)
    {
        double keep = 1 - 2 * Math.Clamp(margin, 0, 0.25);
        return (Math.Max(2, (int)(canvasWidth * keep) & ~1), Math.Max(2, (int)(canvasHeight * keep) & ~1));
    }

    /// <summary>
    /// The canvas fit of a span padded inside the safe area: scaled to fit <see cref="SafeBox"/>,
    /// centred on black. <c>SourceCanvasFit.SafePad</c> mirrors it for geometry.
    /// </summary>
    public static string SafePadSuffix(int canvasWidth, int canvasHeight, double margin, int fpsNum, int fpsDen)
    {
        (int w, int h) = SafeBox(canvasWidth, canvasHeight, margin);
        return $",scale={w}:{h}:force_original_aspect_ratio=decrease,pad={canvasWidth}:{canvasHeight}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fpsNum}/{fpsDen}";
    }

    /// <summary>
    /// The span's picture branch (ending in <c>[v{SpanIndex}]</c>) when it has a reframe stage or a
    /// safe-area pad; null when it has neither, leaving the encoder's own branch untouched.
    /// </summary>
    public static IReadOnlyList<string>? SpanVideoChain(ReframeSpanChain s)
    {
        if (s.Stage is null && !s.SafePad)
            return null;

        string head = $"[{s.InputIndex}:v]{s.NormalizeHead}trim=start={s.Start}:end={s.End},setpts=PTS-STARTPTS" +
                      (s.HdrKind is null ? "" : "," + SourceCameraProfile.Filter(s.HdrKind)) +
                      (s.Stage is null ? "" : "," + s.Stage);
        int i = s.SpanIndex;
        if (s.Blur)
        {
            (int fw, int fh) = s.SafePad ? SafeBox(s.CanvasWidth, s.CanvasHeight, s.SafeMargin) : (s.CanvasWidth, s.CanvasHeight);
            return
            [
                $"{head},fps={s.FpsNum}/{s.FpsDen}{s.LengthSuffix},split[vfg{i}][vbg{i}]",
                $"[vbg{i}]scale={s.CanvasWidth}:{s.CanvasHeight}:force_original_aspect_ratio=increase,crop={s.CanvasWidth}:{s.CanvasHeight},gblur=sigma=40,eq=brightness=-0.08[vbgb{i}]",
                $"[vfg{i}]scale={fw}:{fh}:force_original_aspect_ratio=decrease[vfgs{i}]",
                $"[vbgb{i}][vfgs{i}]overlay=(W-w)/2:(H-h)/2,setsar=1{s.FadeSuffix}[v{i}]"
            ];
        }

        string fit = s.SafePad
            ? SafePadSuffix(s.CanvasWidth, s.CanvasHeight, s.SafeMargin, s.FpsNum, s.FpsDen)
            : s.NormalizeSuffix;
        return [$"{head}{fit}{s.LengthSuffix}{s.FadeSuffix}[v{i}]"];
    }

    // ---- expression building ----

    /// <summary>
    /// One property's value over the span's clock as nested <c>if(lt(it,b),…)</c>: the identity
    /// outside every reframe, each reframe's keyframe curve inside it.
    /// </summary>
    private static string Curve(
        IReadOnlyList<VideoReframe> reframes, double spanStartSec, double length,
        Func<ReframeKeyframe, double> value, double identity)
    {
        string id = Num(identity);
        var entries = new List<(double Boundary, string Expr)>();
        string final = id;
        foreach (VideoReframe r in reframes)
        {
            double offset = r.StartSec - spanStartSec;
            entries.Add((offset, id));
            string inner = KeyframeCurve(r.Keyframes, offset, length, value);
            if (r.ToSec is { } to)
                entries.Add((to - spanStartSec, inner));
            else
            {
                final = inner;
                break;
            }
        }

        return Chain(entries, final, length);
    }

    /// <summary>
    /// The strip centre over the span's clock: <paramref name="rest"/> outside every visible reframe, each visible
    /// reframe's strip expression inside it. Written on <c>t</c> (crop's clock), which is the span's own.
    /// </summary>
    private static string StripCurve(
        IReadOnlyList<VideoReframe> reframes, double spanStartSec, double length,
        Func<ReframeKeyframe, double> centre, double rest)
    {
        string id = Num(rest);
        var entries = new List<(double Boundary, string Expr)>();
        string final = id;
        foreach (VideoReframe r in reframes)
        {
            double offset = r.StartSec - spanStartSec;
            entries.Add((offset, id));
            string inner = id;
            if (r.Anchor == ReframeAnchor.Visible)
            {
                string z = KeyframeCurve(r.Keyframes, offset, length, k => k.Zoom);
                string c = KeyframeCurve(r.Keyframes, offset, length, centre);
                inner = $"0.5+min(0,({c})*({z})-0.5)+max(0,({c})*({z})-({z})+0.5)";
            }

            if (r.ToSec is { } to)
                entries.Add((to - spanStartSec, inner));
            else
            {
                final = inner;
                break;
            }
        }

        return Regex.Replace(Chain(entries, final, length), @"\bit\b", "t");
    }

    private static string KeyframeCurve(
        IReadOnlyList<ReframeKeyframe> keys, double offset, double length, Func<ReframeKeyframe, double> value)
    {
        var entries = new List<(double Boundary, string Expr)> { (offset + keys[0].AtSec, Num(value(keys[0]))) };
        for (int i = 0; i < keys.Count - 1; i++)
        {
            ReframeKeyframe a = keys[i], b = keys[i + 1];
            entries.Add((offset + b.AtSec, Segment(value(a), value(b), offset + a.AtSec, b.AtSec - a.AtSec, a.Easing)));
        }

        return Chain(entries, Num(value(keys[^1])), length);
    }

    private static string Segment(double from, double to, double startLocal, double duration, ReframeEasing easing)
    {
        double delta = Math.Round(to - from, 9);
        if (easing == ReframeEasing.Hold || delta == 0)
            return Num(from);
        double start = Math.Round(startLocal, 6);
        string u = start == 0
            ? $"it/{Num(Math.Round(duration, 6))}"
            : start < 0
                ? $"(it+{Num(-start)})/{Num(Math.Round(duration, 6))}"
                : $"(it-{Num(start)})/{Num(Math.Round(duration, 6))}";
        string eased = easing switch
        {
            ReframeEasing.EaseIn => $"({u})*({u})",
            ReframeEasing.EaseOut => $"({u})*(2-({u}))",
            ReframeEasing.EaseInOut => $"({u})*({u})*(3-2*({u}))",
            _ => $"({u})"
        };
        return $"{Num(from)}+({Num(delta)})*{eased}";
    }

    /// <summary>
    /// <c>if(lt(it,b0),e0,if(lt(it,b1),e1,…final))</c>, pruned to the span: an entry whose boundary
    /// is at or before 0 can never be taken; the first whose boundary is at or past the span's
    /// length is always taken, so it ends the chain. Equal neighbours collapse.
    /// </summary>
    private static string Chain(List<(double Boundary, string Expr)> entries, string final, double length)
    {
        var kept = new List<(double Boundary, string Expr)>();
        foreach ((double boundary, string expr) in entries)
        {
            double b = Math.Round(boundary, 6);
            if (b <= 0)
                continue;
            if (b >= length - 1e-9)
            {
                final = expr;
                break;
            }

            kept.Add((b, expr));
        }

        // Collapse a run of identical values into the one after it: if(lt(it,b),v,v) is v.
        var sb = new StringBuilder();
        string tail = final;
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            if (kept[i].Expr == tail)
                continue;
            tail = $"if(lt(it,{Num(kept[i].Boundary)}),{kept[i].Expr},{tail})";
        }

        sb.Append(tail);
        return sb.ToString();
    }

    private static string Num(double value) => FfmpegArgvFormat.Number(Math.Round(value, 9) + 0.0);
}
