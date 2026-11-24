using System.Globalization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The P5 picture chain of one media/generator layer: crop → fit → scale → rotate → alpha, plus
/// the overlay position. Every number comes from a document the validator already range-checked,
/// and every keyframed channel becomes a time expression through <see cref="KeyframeExpressionBuilder"/>
/// (time origin = the region start, so the same curve hashes and renders identically wherever the
/// region sits on the program).
/// </summary>
/// <remarks>
/// <para>
/// <b>Default path is byte-identical to P4.</b> A clip with no transform, no crop and no fades gets
/// exactly the P4 chain (<c>scale … force_original_aspect_ratio=decrease,pad …,setsar=1,format=yuv420p</c>
/// then <c>overlay=eof_action=pass</c>). Only a clip that USES a P5 field takes the new chain, which
/// fits without padding (so position and scale act on the picture, not on a padded canvas), scales
/// by the transform, rotates on a transparent canvas and sets alpha, then overlays at the transform's
/// position. The golden-parity gate therefore keeps measuring the P4 argv.
/// </para>
/// <para>
/// Opacity: a constant alpha is <c>colorchannelmixer=aa=</c>; an animated alpha (a keyframed opacity
/// or a fade) is <c>geq</c> on the alpha plane with the curve as an expression in <c>T</c>. <c>geq</c>
/// is per-pixel and slow, so it is used ONLY when the alpha actually varies within the region.
/// </para>
/// </remarks>
public static class TransformFilterBuilder
{
    /// <summary>What the layer chain needs from the caller: the canvas and the region's time origin.</summary>
    public sealed record LayerContext(ResolvedRenderTarget Target, long RegionStartFrame, long RegionEndFrame);

    /// <summary>The chain for one layer and the overlay options that place it.</summary>
    /// <param name="Chain">Comma-separated filters applied to the fitted layer (may be empty).</param>
    /// <param name="OverlayOptions">Options appended to <c>overlay=</c> (position; empty for the P4 default).</param>
    /// <param name="AnimatedExpressions">How many time expressions the chain carries (budgeting).</param>
    /// <param name="UsesP4Fit">True when the layer takes the exact P4 fit+pad chain.</param>
    public sealed record LayerChain(string Chain, string OverlayOptions, int AnimatedExpressions, bool UsesP4Fit);

    /// <summary>True when the clip renders through the P4 chain unchanged.</summary>
    public static bool IsIdentity(MediaClip clip) =>
        clip.Transform is null && clip.Crop is null && clip.FadeInFrames <= 0 && clip.FadeOutFrames <= 0 && SourceRotationFilter(clip).Length == 0;

    /// <summary>
    /// The filter for the clip's extra clockwise quarter turn of the source picture (empty when none).
    /// Applied right after the autorotated decode and BEFORE crop and fit, so 90/270 swap width and height.
    /// </summary>
    public static string SourceRotationFilter(MediaClip clip) => (clip?.SourceRotation ?? 0) switch
    {
        90 => "transpose=1",
        270 => "transpose=2",
        180 => "hflip,vflip",
        _ => string.Empty,
    };

    /// <summary>
    /// Builds the layer chain for a media clip. The caller has already produced a stream at the
    /// source size and the target rate; this returns what to apply to it and how to overlay it.
    /// </summary>
    public static LayerChain Build(MediaClip clip, LayerContext context) =>
        Build(clip, context, clip?.FadeInFrames ?? 0, clip?.FadeOutFrames ?? 0);

    /// <summary>
    /// As <see cref="Build(MediaClip, LayerContext)"/> with the fades the caller says apply: a
    /// transition leg (RD-6) drops the fade on the side the transition itself blends.
    /// </summary>
    public static LayerChain Build(MediaClip clip, LayerContext context, long fadeInFrames, long fadeOutFrames)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(context);
        ResolvedRenderTarget target = context.Target;

        if (clip.Transform is null && clip.Crop is null && fadeInFrames <= 0 && fadeOutFrames <= 0)
        {
            return new LayerChain(
                $"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease,pad={target.Width}:{target.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p",
                string.Empty,
                0,
                UsesP4Fit: true);
        }

        return BuildDepth(clip.Transform, clip.Crop, fadeInFrames, fadeOutFrames, clip.RecordIn, clip.RecordOut, context);
    }

    /// <summary>
    /// The chain for a title drawn on its own transparent canvas-size <c>rgba</c> layer: the same scale, rotate,
    /// opacity and offset channels a media clip's transform uses, applied around the title's anchor.
    /// </summary>
    public static LayerChain BuildTitle(ClipTransform transform, long recordIn, long recordOut, LayerContext context)
    {
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(context);
        return BuildDepth(transform, null, 0, 0, recordIn, recordOut, context);
    }

    /// <summary>The chain for a generator layer, which has no crop and no fades but may be transformed in a later wave.</summary>
    public static LayerChain BuildGenerator(LayerContext context) =>
        new($"setsar=1,format=yuv420p", string.Empty, 0, UsesP4Fit: true);

    private static LayerChain BuildDepth(
        ClipTransform? transform, ClipCrop? crop, long fadeIn, long fadeOut, long recordIn, long recordOut, LayerContext context)
    {
        ResolvedRenderTarget target = context.Target;
        double fps = target.Fps;
        long origin = context.RegionStartFrame;
        List<string> chain = [];
        int animated = 0;

        if (crop is not null && (crop.Left > 0 || crop.Top > 0 || crop.Right > 0 || crop.Bottom > 0))
        {
            double w = Math.Max(0.01, 1 - crop.Left - crop.Right);
            double h = Math.Max(0.01, 1 - crop.Top - crop.Bottom);
            chain.Add($"crop=iw*{Num(w)}:ih*{Num(h)}:iw*{Num(crop.Left)}:ih*{Num(crop.Top)}");
        }

        // Fit without padding: the picture keeps its aspect inside the canvas, position/scale move it.
        chain.Add($"scale={target.Width}:{target.Height}:force_original_aspect_ratio=decrease");

        string? scaleExpr = Expression(transform?.Scale, 1.0, origin, fps, ref animated, isScale: true);
        string? scaleYExpr = Expression(transform?.ScaleY, 1.0, origin, fps, ref animated, isScale: true);
        if (transform?.ScaleY is { } sy && (sy.Value != 0 || sy.Keys is { Count: > 0 }))
        {
            // Free aspect: Scale is the horizontal factor, ScaleY the vertical one (absent channel = 1).
            if (scaleExpr is not null || scaleYExpr is not null)
                chain.Add($"scale=w='trunc(iw*({scaleExpr ?? "1"})/2)*2':h='trunc(ih*({scaleYExpr ?? "1"})/2)*2':eval=frame");
        }
        else if (scaleExpr is not null)
        {
            // Even dimensions keep yuv420p happy after the final format; the overlay accepts any size.
            chain.Add($"scale=w='trunc(iw*({scaleExpr})/2)*2':h='trunc(ih*({scaleExpr})/2)*2':eval=frame");
        }

        string? rotationExpr = Expression(transform?.Rotation, 0.0, origin, fps, ref animated);
        bool needsAlpha = rotationExpr is not null;

        string? alphaExpr = AlphaExpression(transform?.Opacity, fadeIn, fadeOut, recordIn, recordOut, origin, fps, context.RegionEndFrame, out bool alphaAnimated);
        if (alphaExpr is not null) needsAlpha = true;

        if (needsAlpha) chain.Add("format=rgba");

        if (rotationExpr is not null)
        {
            // The output size is evaluated once, before any frame: `a` is not a constant there and `t`
            // has no value (RD-2), so a static angle sizes the canvas with rotw/roth of the NUMBER,
            // and a keyframed one takes the diagonal, which holds the picture at any angle.
            bool rotationKeyed = transform?.Rotation?.Keys is { Count: > 0 };
            string size = rotationKeyed
                ? "ow='hypot(iw,ih)':oh='ow'"
                : $"ow='rotw(({rotationExpr})*PI/180)':oh='roth(({rotationExpr})*PI/180)'";
            chain.Add($"rotate=a='({rotationExpr})*PI/180':{size}:c=0x00000000");
        }

        if (alphaExpr is not null)
        {
            if (alphaAnimated)
            {
                // geq works on planar formats and names time `T`; the curve was built in `t`.
                animated++;
                chain.Add($"format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*clip({TimeVariable.Replace(alphaExpr, "T")},0,1)'");
            }
            else
            {
                chain.Add($"colorchannelmixer=aa={alphaExpr}");
            }
        }

        chain.Add("setsar=1");

        string x = Position(transform?.PositionX, "W", "w", origin, fps, ref animated);
        string y = Position(transform?.PositionY, "H", "h", origin, fps, ref animated);
        string overlay = $"x='{x}':y='{y}':eval=frame:format=auto";

        return new LayerChain(string.Join(',', chain), overlay, animated, UsesP4Fit: false);
    }

    /// <summary>
    /// The overlay coordinate: centred, offset by the fraction of the canvas the transform asks for.
    /// Constant when the channel is static, a time expression when keyframed.
    /// </summary>
    private static string Position(Keyframed<double>? channel, string canvas, string layer, long origin, double fps, ref int animated)
    {
        string? expr = Expression(channel, 0.0, origin, fps, ref animated);
        return expr is null ? $"({canvas}-{layer})/2" : $"({canvas}-{layer})/2+{canvas}*({expr})";
    }

    /// <summary>
    /// A channel as an ffmpeg expression in <c>t</c>, or null when it sits at its identity value.
    /// A static non-identity value is a constant; keyframes go through the balanced <c>if()</c> tree.
    /// </summary>
    private static string? Expression(Keyframed<double>? channel, double identity, long origin, double fps, ref int animated, bool isScale = false)
    {
        if (channel is null) return null;
        if (channel.Keys is { Count: > 0 } keys)
        {
            double fallback = isScale && channel.Value == 0 ? identity : channel.Value;
            if (keys.Count == 1 && keys[0].Value == identity) return null;
            animated++;
            return KeyframeExpressionBuilder.Build(keys, fallback, origin, fps);
        }

        double value = isScale && channel.Value == 0 ? identity : channel.Value;
        return value == identity ? null : Num(value);
    }

    /// <summary>
    /// Alpha = opacity × fade-in ramp × fade-out ramp, in region-local <c>t</c>. Null when it is
    /// constantly 1 within the region; <paramref name="animated"/> says whether it varies over the region.
    /// </summary>
    internal static string? AlphaExpression(
        Keyframed<double>? opacity, long fadeIn, long fadeOut, long recordIn, long recordOut, long origin, double fps, long regionEnd, out bool animated)
    {
        animated = false;
        List<string> factors = [];

        int dummy = 0;
        string? opacityExpr = Expression(opacity, 1.0, origin, fps, ref dummy);
        if (opacityExpr is not null)
        {
            bool keyed = opacity?.Keys is { Count: > 1 };
            factors.Add(keyed ? $"({opacityExpr})" : opacityExpr);
            if (keyed) animated = true;
        }

        // A fade contributes only when its ramp overlaps this region; a region entirely after a
        // fade-in (or before a fade-out) sees a constant 1 and adds nothing.
        if (fadeIn > 0 && recordIn + fadeIn > origin)
        {
            double t0 = (recordIn - origin) / fps;
            double d = fadeIn / fps;
            factors.Add($"clip((t-({Num(t0)}))/{Num(d)},0,1)");
            animated = true;
        }

        if (fadeOut > 0 && recordOut - fadeOut < regionEnd)
        {
            double t1 = (recordOut - origin) / fps;
            double d = fadeOut / fps;
            factors.Add($"clip(({Num(t1)}-t)/{Num(d)},0,1)");
            animated = true;
        }

        if (factors.Count == 0) return null;
        return string.Join("*", factors);
    }

    private static readonly Regex TimeVariable = new(@"\bt\b", RegexOptions.Compiled);

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
