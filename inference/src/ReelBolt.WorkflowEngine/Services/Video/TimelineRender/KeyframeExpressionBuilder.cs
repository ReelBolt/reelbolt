using System.Globalization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;

namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// Turns a <see cref="Keyframed{T}"/> number into one ffmpeg expression over <c>t</c> (seconds).
/// </summary>
/// <remarks>
/// <para>
/// <b>Balanced binary <c>if()</c> tree — spike (b)'s finding, now a hard rule.</b> A left-nested
/// piecewise chain (<c>if(lt(t,k1), seg1, if(lt(t,k2), seg2, …))</c>) hits ffmpeg's expression
/// nesting limit at 101 keyframes — <c>[Eval] Missing ')' or too many args</c> — below the
/// document's own 256-keyframe ceiling. Splitting the keyframe list at its middle boundary and
/// recursing gives depth <c>log2(N)</c>: 256 keyframes nest 8 deep, 1024 nest 10. The two
/// constructions are pixel-identical (same SHA-256 of the raw frame stream at N=100 in the spike),
/// so this is purely a shape change.
/// </para>
/// <para>
/// Interpolations are the closed-form set the spike read back from pixels as exact: linear, the
/// quad/cubic eases, and <c>step</c> (hold). Free-control-point bezier is not in
/// <see cref="InterpKind"/> and is not emitted here. Every number is written with
/// <see cref="CultureInfo.InvariantCulture"/> round-trip formatting.
/// </para>
/// </remarks>
public static class KeyframeExpressionBuilder
{
    /// <summary>
    /// The per-filtergraph keyframed-parameter cap spike (b) measured before <c>execve</c> fails
    /// with an EMPTY stderr (27 at N=100, 10 at N=256): the renderer keeps one keyframed parameter
    /// per filter and this many filters per graph, and refuses beyond it rather than producing a
    /// silent failure.
    /// </summary>
    public const int MaxKeyframedParametersPerGraph = 8;

    /// <summary>
    /// Builds the expression. <paramref name="keys"/> are in TIMELINE frames; <paramref name="originFrame"/>
    /// is the timeline frame at which the filter's own <c>t = 0</c> sits (the clip's record-in for a
    /// per-clip filter, the range start for a program-wide one), and <paramref name="fps"/> converts
    /// frames to the seconds ffmpeg evaluates <c>t</c> in.
    /// </summary>
    /// <returns>An expression yielding the keyframed value at time <c>t</c>, or the constant
    /// <paramref name="fallback"/> when there are no keys.</returns>
    public static string Build(
        IReadOnlyList<Keyframe<double>>? keys,
        double fallback,
        long originFrame,
        double fps,
        string timeVariable = "t")
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), fps, "fps must be positive.");

        if (keys is null || keys.Count == 0)
        {
            return Num(fallback);
        }

        if (keys.Count > EditTimelineValidator.MaxKeyframes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(keys), keys.Count, $"At most {EditTimelineValidator.MaxKeyframes} keyframes per parameter.");
        }

        // Keys must be strictly increasing in frame; the validator enforces this on the document,
        // and the builder refuses rather than silently reordering an authored curve.
        Keyframe<double>[] ordered = keys.ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Frame <= ordered[i - 1].Frame)
            {
                throw new ArgumentException(
                    $"Keyframes must be strictly increasing by frame; key {i} ({ordered[i].Frame}) does not follow {ordered[i - 1].Frame}.",
                    nameof(keys));
            }
        }

        if (ordered.Length == 1)
        {
            return Num(ordered[0].Value);
        }

        // N keys define N+1 pieces: before the first key (held), N-1 segments, after the last (held).
        // Piece p covers [boundary[p-1], boundary[p]) where boundary[k] = time of key k.
        string tree = BuildTree(ordered, 0, ordered.Length, originFrame, fps);
        // `geq` names its time variable `T`; the tree is written over `t`, which only ever appears as
        // the argument of `lt(t,` and `(t-`, so the rename is exact.
        return timeVariable == "t" ? tree : tree.Replace("lt(t,", $"lt({timeVariable},", StringComparison.Ordinal).Replace("(t-", $"({timeVariable}-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the tree over keys [lo, hi): the pieces whose left boundary is a key in that range,
    /// plus the held piece before key <c>lo</c> when <c>lo == 0</c>.
    /// </summary>
    private static string BuildTree(Keyframe<double>[] keys, int lo, int hi, long originFrame, double fps)
    {
        int count = hi - lo;
        if (count == 1)
        {
            // One key: before it hold its value (only meaningful when it is the first key), after it
            // interpolate towards the next key, or hold when it is the last.
            string after = SegmentAfter(keys, lo, originFrame, fps);
            if (lo == 0)
            {
                return $"if(lt(t,{Time(keys[lo].Frame, originFrame, fps)}),{Num(keys[lo].Value)},{after})";
            }

            return after;
        }

        int mid = lo + count / 2;
        string left = BuildTree(keys, lo, mid, originFrame, fps);
        string right = BuildTree(keys, mid, hi, originFrame, fps);
        return $"if(lt(t,{Time(keys[mid].Frame, originFrame, fps)}),{left},{right})";
    }

    /// <summary>The piece from key <paramref name="index"/> to the next key (or held after the last).</summary>
    private static string SegmentAfter(Keyframe<double>[] keys, int index, long originFrame, double fps)
    {
        Keyframe<double> from = keys[index];
        if (index + 1 >= keys.Length)
        {
            return Num(from.Value);
        }

        Keyframe<double> to = keys[index + 1];
        if (from.Interp == InterpKind.Step || from.Value == to.Value)
        {
            return Num(from.Value);
        }

        string t0 = Time(from.Frame, originFrame, fps);
        string t1 = Time(to.Frame, originFrame, fps);
        // u in [0,1): the normalised position inside the segment, clamped so float drift at a
        // boundary can never overshoot the target value.
        string u = $"clip((t-{t0})/({t1}-{t0}),0,1)";
        string eased = Ease(from.Interp, u);
        return $"({Num(from.Value)}+({Num(to.Value - from.Value)})*{eased})";
    }

    /// <summary>The closed-form easing of <paramref name="u"/> for an interpolation kind.</summary>
    public static string Ease(InterpKind interp, string u) => interp switch
    {
        InterpKind.Linear => u,
        InterpKind.EaseInQuad => $"pow({u},2)",
        InterpKind.EaseOutQuad => $"(1-pow(1-{u},2))",
        InterpKind.EaseInOutQuad => $"if(lt({u},0.5),2*pow({u},2),1-pow(-2*{u}+2,2)/2)",
        InterpKind.EaseInCubic => $"pow({u},3)",
        InterpKind.EaseOutCubic => $"(1-pow(1-{u},3))",
        InterpKind.EaseInOutCubic => $"if(lt({u},0.5),4*pow({u},3),1-pow(-2*{u}+2,3)/2)",
        InterpKind.Step => "0",
        _ => throw new ArgumentOutOfRangeException(nameof(interp), interp, "Unsupported interpolation kind.")
    };

    /// <summary>
    /// Wraps a dB-valued expression as the linear gain the <c>volume</c> filter evaluates.
    /// </summary>
    public static string DbToLinear(string dbExpression) => $"pow(10,({dbExpression})/20)";

    /// <summary>The depth of nested <c>if(</c> in an expression — what ffmpeg's evaluator limits.</summary>
    public static int NestingDepth(string expression)
    {
        int depth = 0, max = 0;
        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '(')
            {
                depth++;
                if (depth > max) max = depth;
            }
            else if (expression[i] == ')')
            {
                depth--;
            }
        }

        return max;
    }

    private static string Time(long frame, long originFrame, double fps) => Num((frame - originFrame) / fps);

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
