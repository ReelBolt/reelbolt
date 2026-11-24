using System.Globalization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// Renders an <see cref="InsertClip"/> in a timeline region: its media is STRETCHED to the canvas-shaped layer,
/// then pinned into the (keyframed) quad with <c>perspective</c> in destination sense, on a transparent canvas,
/// and overlaid in track order. The viewer (<c>live-geometry.ts insertQuadAt</c> + the compositor) uses the same
/// rules, so preview and export agree.
/// </summary>
/// <remarks>
/// <para><b>Geometry convention.</b> A quad whose corners (the base value and every keyframe) are ALL inside
/// 0..1 is canvas fractions; anything else is document-canvas pixels. Every coordinate is then clamped to
/// -1..2 canvas fractions (<c>GradeFilterBuilder.CornerPin</c>'s clamp) before it becomes a pixel position.
/// Corner order is topLeft, topRight, bottomRight, bottomLeft; ffmpeg's x0/y0 = topLeft, x1/y1 = topRight,
/// x2/y2 = bottomLeft, x3/y3 = bottomRight. The media plays from its own first frame at the timeline rate
/// from the insert's record-in (no source offset, speed, crop, fades or opacity). A still image holds its frame.
/// The insert's chroma <c>Matte</c> is keyed out (<c>GradeFilterBuilder.Keyer</c>, as for a media clip) before the warp.</para>
/// <para>A keyed quad is one <c>perspective</c> with <c>eval=frame</c> and a piecewise expression per
/// coordinate over <c>perspective</c>'s <c>in</c> variable, which is 1-based (frame 1 is the first frame).</para>
/// </remarks>
internal static class InsertLayerBuilder
{
    private const double ClampMin = -1;
    private const double ClampMax = 2;

    /// <summary>Whether the insert has what a render needs (media and a quad).</summary>
    public static bool IsRenderable(InsertClip insert) => insert.AssetRef is not null && insert.Quad is not null;

    /// <summary>The <c>-ss/-t/-i</c> for the insert over the region.</summary>
    public static IEnumerable<string> InputArgs(InsertClip insert, LocalAsset asset, RenderRegion region, double fps)
    {
        double spanSec = (region.EndFrame - region.StartFrame) / fps;
        double offsetSec = Math.Max(0, region.StartFrame - insert.RecordIn) / fps;
        // A still image has no length to seek into: seeking past its single frame would read nothing.
        List<string> args = [];
        if (offsetSec > 0 && asset.Probe.DurationSec > 0.5)
        {
            args.AddRange(["-ss", Num(offsetSec)]);
        }

        args.AddRange([.. UserMediaInputFormats.InputOptions(), "-t", Num(spanSec + 0.5), "-i", asset.LocalPath]);
        return args;
    }

    /// <summary>The chain from the decoded input to the warped, transparent canvas-size layer.</summary>
    public static string Chain(InsertClip insert, RenderRegion region, ResolvedRenderTarget target)
    {
        double fps = target.Fps;
        double spanSec = (region.EndFrame - region.StartFrame) / fps;
        // Padded by cloning the last frame (a short or single-frame media holds), never time-shifted.
        string prefix =
            $"trim=duration={Num(spanSec)},setpts=PTS-STARTPTS,fps={target.RateArg},tpad=stop=-1:stop_mode=clone,trim=duration={Num(spanSec)}," +
            $"scale={target.Width}:{target.Height},setsar=1,";
        // The matte is keyed out of the picture BEFORE the warp, with the same keyer a media clip uses.
        string matte = GradeFilterBuilder.Keyer(insert.Matte);
        if (matte.Length > 0) prefix += matte + ",";
        return prefix + Warp(insert.Quad!, region.StartFrame, target);
    }

    /// <summary>The <c>format=rgba,drawbox,perspective</c> tail for a static or keyed quad.</summary>
    internal static string Warp(Keyframed<QuadCorners> quad, long originFrame, ResolvedRenderTarget target)
    {
        bool fractions = AllPoints(quad).All(p => p.X >= 0 && p.X <= 1 && p.Y >= 0 && p.Y <= 1);
        double sx = fractions ? target.Width : 1;
        double sy = fractions ? target.Height : 1;

        double X(double v) => Math.Round(Math.Clamp(v * sx / target.Width, ClampMin, ClampMax) * target.Width, 2);
        double Y(double v) => Math.Round(Math.Clamp(v * sy / target.Height, ClampMin, ClampMax) * target.Height, 2);

        string clear = "format=rgba,drawbox=x=0:y=0:w=iw:h=ih:t=1:color=black@0:replace=1,";
        Func<QuadCorners, QuadPoint>[] corners = [q => q.TopLeft, q => q.TopRight, q => q.BottomLeft, q => q.BottomRight];

        if (quad.Keys is not { Count: > 0 } keys)
        {
            QuadCorners q = quad.Value!;
            string[] fixedCoords = new string[8];
            for (int c = 0; c < 4; c++)
            {
                QuadPoint p = corners[c](q);
                fixedCoords[c * 2] = Num(X(p.X));
                fixedCoords[(c * 2) + 1] = Num(Y(p.Y));
            }

            return clear + Perspective(fixedCoords, keyed: false);
        }

        string[] exprs = new string[8];
        string time = $"((in-1)/({target.RateArg}))";
        Func<double, double> mapX = X, mapY = Y;
        for (int c = 0; c < 4; c++)
        {
            foreach ((int axis, Func<double, double> map) in new (int, Func<double, double>)[] { (0, mapX), (1, mapY) })
            {
                double Pick(QuadCorners q) => axis == 0 ? corners[c](q).X : corners[c](q).Y;
                Keyframe<double>[] series = [.. keys.Select(k => new Keyframe<double>(k.Frame, map(Pick(k.Value)), k.Interp))];
                exprs[(c * 2) + axis] = KeyframeExpressionBuilder.Build(series, map(Pick(quad.Value ?? keys[0].Value)), originFrame, target.Fps, time);
            }
        }

        // Expressions carry commas: single-quoted, like every other expression option in the graph.
        return clear + Perspective([.. exprs.Select(e => $"'{e}'")], keyed: true);
    }

    private static string Perspective(string[] c, bool keyed) =>
        $"perspective=x0={c[0]}:y0={c[1]}:x1={c[2]}:y1={c[3]}:x2={c[4]}:y2={c[5]}:x3={c[6]}:y3={c[7]}:sense=destination" + (keyed ? ":eval=frame" : string.Empty);

    internal static IEnumerable<QuadPoint> AllPoints(Keyframed<QuadCorners> quad)
    {
        List<QuadCorners> all = [];
        if (quad.Value is { } v) all.Add(v);
        foreach (Keyframe<QuadCorners> key in quad.Keys ?? []) all.Add(key.Value);
        return all.SelectMany(q => new[] { q.TopLeft, q.TopRight, q.BottomRight, q.BottomLeft });
    }

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
