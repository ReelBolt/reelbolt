using System.Globalization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The P7 picture builders: a grade stack as an ffmpeg filter chain, the closed effects catalogue,
/// a shape mask, a chroma keyer, an auto-reframe crop and a manual corner pin. Every number comes
/// from a document the validator range-checked; every filter name is first-party. Nothing here is a
/// pass-through of a user string into a filter — colours are re-serialised from a validated
/// <c>#RRGGBB</c>, LUT paths are the renderer's own scratch paths.
/// </summary>
/// <remarks>
/// <para><b>The grade mapping, in order per node:</b> exposure, offset and lift/gamma/gain masters
/// plus contrast-about-pivot in ONE <c>lutrgb</c> (so a pure luminance change costs one pass),
/// per-channel lift/gamma/gain through <c>colorbalance</c> shadows/midtones/highlights, per-channel
/// offset through the same <c>lutrgb</c> expressions, temperature through <c>colortemperature</c>,
/// tint through <c>colorbalance</c> green/magenta midtones, hue and saturation through <c>hue</c>,
/// curves through <c>curves</c>, the LUT through <c>lut3d</c> (tetrahedral). A node whose every part
/// is at identity emits nothing; a bypassed node or stack emits nothing.</para>
/// </remarks>
public static class GradeFilterBuilder
{
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}\\z", RegexOptions.Compiled);

    /// <summary>A LUT the chain references: which node asked, and the scratch path the renderer must write.</summary>
    /// <param name="Lut">The grade node's LUT reference and intensity.</param>
    /// <param name="Path">The canonical file ffmpeg reads: the 3D cube, or the 1D table for a 1D LUT, or the shaper for shaper + 3D at intensity 1.</param>
    /// <param name="Kind">What the chain emits for it (a shaper + 3D LUT below intensity 1 is one baked 3D table).</param>
    /// <param name="CubePath">Shaper + 3D at intensity 1 only: the 3D cube file read by the <c>lut3d</c> after the <c>lut1d</c> at <paramref name="Path"/>.</param>
    public sealed record LutRequest(GradeLut Lut, string Path, CubeLutKind Kind = CubeLutKind.ThreeD, string? CubePath = null);

    /// <summary>
    /// The chain for a grade stack, or an empty string. <paramref name="lutPathFor"/> names the scratch
    /// path for the n-th LUT in this stack; every request is reported so the caller materialises it.
    /// </summary>
    public static string Build(GradeStack? stack, Func<int, string> lutPathFor, List<LutRequest> lutRequests, ref int lutIndex,
        string labelPrefix = "gq", long originFrame = 0, double fps = 30, IReadOnlyDictionary<AssetRef, CubeLut>? luts = null)
    {
        if (stack is null || !stack.Enabled || stack.Nodes is not { Count: > 0 } nodes) return string.Empty;
        List<string> parts = [];
        int secondary = 0;
        foreach (GradeNode node in nodes)
        {
            if (!node.Enabled) continue;
            List<string> correction = [];
            if (node.Primaries is { } p && !p.IsIdentity()) correction.AddRange(Primaries(p));
            if (node.Curves is { } c && !c.IsIdentity()) correction.Add(Curves(c));
            if (node.Lut is { } lut && lut.Intensity > 0)
            {
                string path = lutPathFor(lutIndex++);
                CubeLutKind kind = luts is not null && luts.TryGetValue(lut.File, out CubeLut? parsed) ? parsed.KindAtIntensity(lut.Intensity) : CubeLutKind.ThreeD;
                bool shaper = kind == CubeLutKind.ShaperThreeD && luts![lut.File].Kind == CubeLutKind.ShaperThreeD;
                string? cubePath = shaper ? Path.ChangeExtension(path, null) + "-3d.cube" : null;
                lutRequests.Add(new LutRequest(lut, path, kind, cubePath));
                correction.Add(kind switch
                {
                    CubeLutKind.OneD => $"lut1d=file='{EscapePath(path)}':interp=linear",
                    CubeLutKind.ShaperThreeD => $"lut1d=file='{EscapePath(path)}':interp=linear,lut3d=file='{EscapePath(cubePath!)}':interp=tetrahedral",
                    _ => $"lut3d=file='{EscapePath(path)}':interp=tetrahedral",
                });
            }

            if (correction.Count == 0) continue;
            string? matte = Matte(node, originFrame, fps);
            if (matte is null)
            {
                // P7: a whole-frame correction, byte for byte.
                parts.AddRange(correction);
                continue;
            }

            // P9: a secondary — the correction is computed on a copy and merged back only where the
            // qualifier/window matte (measured on the UNGRADED pixels) says so. gbrap keeps any alpha
            // an upstream keyer made; the matte's alpha plane is 255, so alpha passes as it was.
            string l = $"{labelPrefix}{secondary++}";
            parts.Add(
                $"format=gbrap,split=3[{l}o][{l}c][{l}m];" +
                $"[{l}c]{string.Join(',', correction)},format=gbrap[{l}g];" +
                $"[{l}m]geq=r='{matte}':g='{matte}':b='{matte}':a='255'[{l}k];" +
                $"[{l}o][{l}g][{l}k]maskedmerge");
        }

        return string.Join(',', parts);
    }

    /// <summary>
    /// P9: the 0..255 matte expression (for <c>geq</c> on gbrap) of a node's qualifier and window, or
    /// null when the node is whole-frame. Inline expressions, never <c>st()</c>/<c>ld()</c> registers:
    /// <c>geq</c> evaluates slices in parallel and registers would race.
    /// </summary>
    public static string? Matte(GradeNode node, long originFrame, double fps)
    {
        GradeQualifier? q = node.Qualifier is { } candidate && !candidate.SelectsAll() ? candidate : null;
        GradeWindow? w = node.Window;
        if (q is null && w is null) return null;
        List<string> factors = [];
        if (q is not null)
        {
            const string R = "(r(X,Y)/255)", G = "(g(X,Y)/255)", B = "(b(X,Y)/255)";
            string max = $"max(max({R},{G}),{B})", min = $"min(min({R},{G}),{B})";
            string lum = $"(({max}+{min})/2)";
            string sat = $"(({max}-{min})/(1-abs(2*{lum}-1)+0.00001))";
            string hue = $"mod(atan2(1.7320508*({G}-{B}),2*{R}-{G}-{B})*57.2957795+360,360)";
            double soft = Math.Max(0.002, q.Softness);
            List<string> k = [];
            if (q.HueWidth < 180)
            {
                string distance = $"abs(mod({hue}-{Num(q.HueCenter)}+540,360)-180)";
                k.Add($"clip(({Num(q.HueWidth)}-{distance})/{Num(Math.Max(1, soft * 180))}+0.5,0,1)");
            }

            // A bound at the end of the scale selects nothing out: a fully saturated (or pure white) pixel sits exactly ON
            // satMax=1 / lumMax=1, where a soft edge would have half-selected it, so only a real limit gets an edge.
            if (q.SatMin > 0) k.Add($"clip(({sat}-{Num(q.SatMin)})/{Num(soft)}+0.5,0,1)");
            if (q.SatMax < 1) k.Add($"clip(({Num(q.SatMax)}-{sat})/{Num(soft)}+0.5,0,1)");
            if (q.LumMin > 0) k.Add($"clip(({lum}-{Num(q.LumMin)})/{Num(soft)}+0.5,0,1)");
            if (q.LumMax < 1) k.Add($"clip(({Num(q.LumMax)}-{lum})/{Num(soft)}+0.5,0,1)");
            string selected = k.Count == 0 ? "1" : string.Join('*', k);
            factors.Add(q.Invert ? $"(1-{selected})" : selected);
        }

        if (w?.Shape is { } shape)
        {
            string cx = w.OffsetX is { } ox ? $"({Num(shape.X)}+{Offset(ox, originFrame, fps)})" : Num(shape.X);
            string cy = w.OffsetY is { } oy ? $"({Num(shape.Y)}+{Offset(oy, originFrame, fps)})" : Num(shape.Y);
            double f = Math.Max(0.0005, shape.Feather);
            double hw = Math.Max(0.001, shape.W / 2), hh = Math.Max(0.001, shape.H / 2);
            string inside = shape.Kind == "ellipse"
                ? $"clip((1-sqrt(pow((X/W-{cx})/{Num(hw)},2)+pow((Y/H-{cy})/{Num(hh)},2)))/{Num(f)}+0.5,0,1)"
                : $"clip(min(min(X/W-({cx}-{Num(hw)}),({cx}+{Num(hw)})-X/W),min(Y/H-({cy}-{Num(hh)}),({cy}+{Num(hh)})-Y/H))/{Num(f)}+0.5,0,1)";
            factors.Add(shape.Invert ? $"(1-{inside})" : inside);
        }

        return $"255*{string.Join('*', factors)}";
    }

    private static string Offset(Keyframed<double> channel, long originFrame, double fps) =>
        channel.Keys is { Count: > 0 } keys
            ? $"({KeyframeExpressionBuilder.Build(keys, channel.Value, originFrame, fps, "T")})"
            : Num(channel.Value);

    /// <summary>
    /// True when a node carries both a tint and a gamma-wheel green: the export then writes ONE colorbalance
    /// <c>gm</c> for the two. The region recipe hash marks such nodes so a region cached before the
    /// merge re-renders; every other node's recipe is unchanged.
    /// </summary>
    public static bool MergesTintIntoGammaGreen(GradePrimaries p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Tint != 0 && p.Gamma is { G: not 0 };
    }

    /// <summary>True when the stack would emit at least one filter.</summary>
    public static bool IsActive(GradeStack? stack)
    {
        if (stack is null || !stack.Enabled || stack.Nodes is not { Count: > 0 } nodes) return false;
        return nodes.Any(n => n.Enabled && ((n.Primaries is { } p && !p.IsIdentity()) || (n.Curves is { } c && !c.IsIdentity()) || (n.Lut is { } l && l.Intensity > 0)));
    }

    private static IEnumerable<string> Primaries(GradePrimaries p)
    {
        // 1. Luminance in one lutrgb: exposure (stops) → offset master → lift/gamma/gain masters →
        //    contrast about the pivot. `val` is 0..255; everything works in 0..1 and clips.
        double exposure = Math.Pow(2, p.Exposure);
        double offsetMaster = p.Offset?.Master ?? 0;
        double lift = p.Lift?.Master ?? 0;
        double gamma = p.Gamma?.Master ?? 0;
        double gain = p.Gain?.Master ?? 0;
        bool lumaChange = p.Exposure != 0 || offsetMaster != 0 || lift != 0 || gamma != 0 || gain != 0 || p.Contrast != 1;
        bool rgbOffset = p.Offset is { } o && (o.R != 0 || o.G != 0 || o.B != 0);
        if (lumaChange || rgbOffset)
        {
            string Expr(double channelOffset)
            {
                // x = val/255 · 2^exposure + offset
                string x = $"(val/255*{Num(exposure)}+{Num(offsetMaster + channelOffset)})";
                // lift raises the floor, gain the ceiling: x' = x·(1+gain) + lift·(1−x)
                if (lift != 0 || gain != 0) x = $"({x}*{Num(1 + gain)}+{Num(lift)}*(1-{x}))";
                // gamma master: pow(x, 1/(1+gamma)) — positive gamma brightens midtones
                if (gamma != 0) x = $"pow(clip({x},0,1),{Num(1 / Math.Max(0.05, 1 + gamma))})";
                // contrast about the pivot
                if (p.Contrast != 1) x = $"(({x}-{Num(p.Pivot)})*{Num(p.Contrast)}+{Num(p.Pivot)})";
                return $"clip({x},0,1)*255";
            }

            yield return $"lutrgb=r='{Expr(p.Offset?.R ?? 0)}':g='{Expr(p.Offset?.G ?? 0)}':b='{Expr(p.Offset?.B ?? 0)}'";
        }

        // 2. Per-channel lift/gamma/gain → colorbalance shadows/midtones/highlights; tint → green/magenta midtones.
        List<string> balance = [];
        void Wheel(GradeWheel? w, string suffix, double? mergedGreen = null)
        {
            if (w is null && mergedGreen is null) return;
            if (w?.R is { } r && r != 0) balance.Add($"r{suffix}={Num(Math.Clamp(r, -1, 1))}");
            double g = mergedGreen ?? w?.G ?? 0;
            if (g != 0) balance.Add($"g{suffix}={Num(Math.Clamp(g, -1, 1))}");
            if (w?.B is { } b && b != 0) balance.Add($"b{suffix}={Num(Math.Clamp(b, -1, 1))}");
        }

        // ffmpeg keeps the LAST colorbalance gm, so a separate tint gm silently replaced gamma green.
        // When a node carries both they are ONE gm in the gamma-green slot; either alone
        // keeps its exact old text and position, so stacks without both emit byte-identical filters.
        bool mergeTint = MergesTintIntoGammaGreen(p);
        double mergedGreen = Math.Clamp((p.Gamma?.G ?? 0) + (-p.Tint * 0.5), -1, 1);
        Wheel(p.Lift, "s");
        Wheel(p.Gamma, "m", mergeTint ? mergedGreen : null);
        Wheel(p.Gain, "h");
        if (p.Tint != 0 && !mergeTint)
        {
            // Positive tint = magenta (less green), as a grading panel reads it.
            balance.Add($"gm={Num(Math.Clamp(-p.Tint * 0.5, -1, 1))}");
        }

        if (balance.Count > 0) yield return "colorbalance=" + string.Join(':', balance);

        // 3. Temperature: positive = warmer. The filter re-balances toward the given illuminant, so a
        //    lower kelvin reads warmer; 6500 K is neutral.
        if (p.Temperature != 0)
        {
            int kelvin = (int)Math.Round(6500 - p.Temperature * 2500);
            yield return $"colortemperature=temperature={kelvin}:mix=1";
        }

        // 4. Hue and saturation.
        if (p.Hue != 0 || p.Saturation != 1)
        {
            List<string> hue = [];
            if (p.Hue != 0) hue.Add($"h={Num(p.Hue)}");
            if (p.Saturation != 1) hue.Add($"s={Num(Math.Clamp(p.Saturation, 0, 3))}");
            yield return "hue=" + string.Join(':', hue);
        }
    }

    private static string Curves(GradeCurves c)
    {
        List<string> parts = [];
        if (c.Y is { Count: > 0 }) parts.Add($"m='{Points(c.Y)}'");
        if (c.R is { Count: > 0 }) parts.Add($"r='{Points(c.R)}'");
        if (c.G is { Count: > 0 }) parts.Add($"g='{Points(c.G)}'");
        if (c.B is { Count: > 0 }) parts.Add($"b='{Points(c.B)}'");
        return "curves=" + string.Join(':', parts);
    }

    private static string Points(IReadOnlyList<CurvePoint> points)
    {
        // ffmpeg wants the end points; a curve that omits them keeps them at the identity.
        List<CurvePoint> ordered = points.OrderBy(pt => pt.X).ToList();
        if (ordered[0].X > 0) ordered.Insert(0, new CurvePoint(0, 0));
        if (ordered[^1].X < 1) ordered.Add(new CurvePoint(1, 1));
        return string.Join(' ', ordered.Select(pt => $"{Num(Math.Clamp(pt.X, 0, 1))}/{Num(Math.Clamp(pt.Y, 0, 1))}"));
    }

    // ── Effects ──────────────────────────────────────────────────────────────

    /// <summary>The chain for a list of effects, or an empty string. Unknown words are skipped (the validator refused them upstream).</summary>
    public static string Effects(IReadOnlyList<ClipEffect>? effects)
    {
        if (effects is not { Count: > 0 }) return string.Empty;
        List<string> parts = [];
        foreach (ClipEffect effect in effects)
        {
            if (!effect.Enabled) continue;
            double a = Math.Clamp(double.IsFinite(effect.Amount) ? effect.Amount : 0, 0, effect.Type == "blur" ? EditTimelineValidator.MaxBlurAmount : 1);
            if (a == 0) continue;
            switch (effect.Type)
            {
                case "blur":
                    parts.Add($"gblur=sigma={Num(Math.Round(a * 20, 3))}");
                    break;
                case "sharpen":
                    parts.Add($"unsharp=la={Num(Math.Round(a * 3, 3))}");
                    break;
                case "vignette":
                    parts.Add($"vignette=a={Num(Math.Round(a * Math.PI / 2.5, 4))}");
                    break;
                case "grain":
                    parts.Add($"noise=alls={(int)Math.Round(a * 40)}:allf=t+u");
                    break;
                case "pixelate":
                    {
                        int block = Math.Max(2, (int)Math.Round(a * 64));
                        parts.Add($"pixelize=w={block}:h={block}");
                        break;
                    }
                case "mono":
                    parts.Add($"hue=s={Num(Math.Round(1 - a, 3))}");
                    break;
                default:
                    break;
            }
        }

        return string.Join(',', parts);
    }

    // ── Keyer ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>colorkey</c> plus, when spill suppression is asked for, <c>despill</c> of the key's dominant
    /// channel (green or blue; other key colours have no despill and skip it). Leaves the stream in
    /// <c>rgba</c>.
    /// </summary>
    public static string Keyer(ChromaKeyer? keyer)
    {
        if (keyer is null) return string.Empty;
        string color = HexColor.IsMatch(keyer.Color ?? string.Empty) ? keyer.Color! : "#00FF00";
        double similarity = Math.Clamp(keyer.Similarity, 0.01, 1);
        double blend = Math.Clamp(keyer.Blend, 0, 1);
        List<string> parts = [$"format=rgba,colorkey=color=0x{color[1..]}:similarity={Num(similarity)}:blend={Num(blend)}"];
        if (keyer.Spill > 0)
        {
            int r = Convert.ToInt32(color.Substring(1, 2), 16);
            int g = Convert.ToInt32(color.Substring(3, 2), 16);
            int b = Convert.ToInt32(color.Substring(5, 2), 16);
            string? type = g >= r && g >= b ? "green" : b > r ? "blue" : null;
            if (type is not null) parts.Add($"despill=type={type}:mix={Num(Math.Clamp(keyer.Spill, 0, 1))}");
        }

        return string.Join(',', parts);
    }

    // ── Mask ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The mask as an alpha multiply through <c>geq</c> on a planar RGBA stream, in canvas fractions
    /// of the LAYER's own size (the layer is fitted before masking): a rectangle or ellipse with a
    /// feathered edge, optionally inverted. The expression uses only X/Y/W/H and constants.
    /// </summary>
    public static string Mask(ShapeMask? mask)
    {
        if (mask is null || mask.IsSegmentationMask()) return string.Empty; // a segmentation matte is alphamerged on the leg, not drawn here
        double x0 = mask.X - mask.W / 2, x1 = mask.X + mask.W / 2;
        double y0 = mask.Y - mask.H / 2, y1 = mask.Y + mask.H / 2;
        double f = Math.Max(0.0005, mask.Feather);
        string inside = mask.Kind == "ellipse"
            // Distance from the ellipse boundary along the normalised radius: 1 at the centre, 0 at the edge.
            ? $"clip((1-sqrt(pow((X/W-{Num(mask.X)})/{Num(Math.Max(0.001, mask.W / 2))},2)+pow((Y/H-{Num(mask.Y)})/{Num(Math.Max(0.001, mask.H / 2))},2)))/{Num(f)}+0.5,0,1)"
            // Rectangle: the smallest distance to any edge, ramped over the feather.
            : $"clip(min(min(X/W-{Num(x0)},{Num(x1)}-X/W),min(Y/H-{Num(y0)},{Num(y1)}-Y/H))/{Num(f)}+0.5,0,1)";
        string alpha = mask.Invert ? $"(1-{inside})" : inside;
        return $"format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*{alpha}',format=rgba";
    }

    // ── Reframe and corner pin ───────────────────────────────────────────────

    /// <summary>A crop to the canvas aspect around the subject point, applied to the SOURCE before the fit.</summary>
    public static string Reframe(ReframeTarget? reframe, int canvasWidth, int canvasHeight)
    {
        if (reframe is null || canvasHeight <= 0) return string.Empty;
        string aspect = Num(Math.Round((double)canvasWidth / canvasHeight, 6));
        double x = Math.Clamp(reframe.X, 0, 1), y = Math.Clamp(reframe.Y, 0, 1);
        return $"crop=w='min(iw,ih*{aspect})':h='min(ih,iw/{aspect})':x='clip(iw*{Num(x)}-ow/2,0,iw-ow)':y='clip(ih*{Num(y)}-oh/2,0,ih-oh)'";
    }

    /// <summary>
    /// The picture, already fitted and padded to the canvas, pinned onto the four corners (canvas
    /// fractions) with <c>perspective</c> in destination sense on a transparent canvas.
    /// </summary>
    /// <remarks>
    /// <c>perspective</c> does not leave the area outside the quad empty: it clamps to the source's
    /// edge pixels and smears them over the canvas (RD-3). Clearing a one-pixel transparent frame
    /// around the source first makes every clamped pixel transparent, so the surround stays clear and
    /// the quad's edge is anti-aliased into it.
    /// </remarks>
    public static string CornerPin(QuadCorners? pin, int canvasWidth, int canvasHeight)
    {
        if (pin is null) return string.Empty;
        string P(double v, int size) => Num(Math.Round(Math.Clamp(v, -1, 2) * size, 2));
        return "format=rgba,drawbox=x=0:y=0:w=iw:h=ih:t=1:color=black@0:replace=1," +
            $"perspective=x0={P(pin.TopLeft.X, canvasWidth)}:y0={P(pin.TopLeft.Y, canvasHeight)}:x1={P(pin.TopRight.X, canvasWidth)}:y1={P(pin.TopRight.Y, canvasHeight)}:" +
            $"x2={P(pin.BottomLeft.X, canvasWidth)}:y2={P(pin.BottomLeft.Y, canvasHeight)}:x3={P(pin.BottomRight.X, canvasWidth)}:y3={P(pin.BottomRight.Y, canvasHeight)}:sense=destination";
    }

    // ── Title templates ──────────────────────────────────────────────────────

    /// <summary>
    /// The drawtext options a title template adds (position, box, border) — the closed catalogue
    /// <c>EditTimelineValidator.TitleTemplates</c>. Unknown or null is the plain centred title.
    /// </summary>
    public static (string Position, string Decoration) TitleTemplate(string? template, int canvasHeight)
    {
        int pad = Math.Max(8, canvasHeight / 54);
        return template switch
        {
            "lowerThird" => ("x=w/12:y=h-text_h-h/8", $"box=1:boxcolor=black@0.55:boxborderw={pad}"),
            "boxed" => ("x=(w-text_w)/2:y=(h-text_h)/2", $"box=1:boxcolor=black@0.6:boxborderw={pad}"),
            "outline" => ("x=(w-text_w)/2:y=(h-text_h)/2", "borderw=4:bordercolor=black"),
            "card" => ("x=(w-text_w)/2:y=(h-text_h)/2", $"box=1:boxcolor=black@0.8:boxborderw={pad * 3}"),
            _ => ("x=(w-text_w)/2:y=(h-text_h)/2", "borderw=2:bordercolor=black@0.6"),
        };
    }

    private static string EscapePath(string path) => path.Replace("\\", "/").Replace("'", @"'\''").Replace(":", @"\:");

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
