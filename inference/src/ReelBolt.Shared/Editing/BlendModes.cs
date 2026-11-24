namespace ReelBolt.Shared.Editing;

/// <summary>
/// The closed catalogue of <see cref="MediaClip.BlendMode"/> words, and the ONE table mapping each to the
/// ffmpeg <c>blend</c> filter option the export uses. The viewer (GLSL) ports the same formulas.
/// <para>
/// Convention: values are 8-bit (0..255), blended GAMMA-ENCODED in RGB (<c>gbrp</c>, never YUV, no
/// linearisation). <c>A</c> is the BACKDROP (what is composited below; ffmpeg's first <c>blend</c> input,
/// "top") and <c>B</c> is the LAYER (this clip's picture; ffmpeg's second input, "bottom"). The blended colour
/// then replaces the backdrop in proportion to the layer's alpha (opacity x fades x mask):
/// <c>out = backdrop + layerAlpha * (blend(backdrop, layer) - backdrop)</c>. On the bottom-most picture of a
/// region the backdrop is black. Integer division truncates. Transitions ignore the mode.
/// </para>
/// <para>
/// Formulas, measured against libavfilter <c>blend</c> on every (A, B) pair of an 8-bit ramp (exact match unless noted):
/// <code>
/// MULTIPLY(x,a,b) = x * ((a*b)/255)          SCREEN(x,a,b) = 255 - x * (((255-a)*(255-b))/255)
/// normal      identity (no filter, same recipe hash as a clip without the field)
/// multiply    MULTIPLY(1,A,B)
/// screen      SCREEN(1,A,B)
/// overlay     A &lt; 128 ? MULTIPLY(2,A,B) : SCREEN(2,A,B)        (decided by the backdrop)
/// hardLight   B &lt; 128 ? MULTIPLY(2,A,B) : SCREEN(2,A,B)        (decided by the layer)
/// softLight   A*A/255 + 2*B*A*(255-A)/65025                    (Pegtop; float, result truncated, clamped 0..255)
/// darken      min(A,B)
/// lighten     max(A,B)
/// add         min(255, A+B)                                     (ffmpeg "addition")
/// difference  abs(A-B)
/// </code>
/// softLight is passed to ffmpeg as <c>all_expr</c> (not the named <c>softlight</c> mode, whose libavfilter
/// formula is not the textbook one) so the viewer can port the exact expression.
/// </para>
/// </summary>
public static class BlendModes
{
    /// <summary>The wire words, in catalogue order.</summary>
    public static readonly IReadOnlyList<string> Words =
        ["normal", "multiply", "screen", "overlay", "softLight", "hardLight", "darken", "lighten", "add", "difference"];

    private static readonly HashSet<string> WordSet = new(Words, StringComparer.Ordinal);

    /// <summary>True when <paramref name="word"/> is one of <see cref="Words"/> (case-sensitive).</summary>
    public static bool IsKnown(string? word) => word is not null && WordSet.Contains(word);

    /// <summary>True when the clip blends: a known word other than <c>normal</c>. <c>null</c>/<c>normal</c> are plain alpha-over.</summary>
    public static bool IsBlending(string? word) => word is not null && word != "normal" && WordSet.Contains(word);

    /// <summary>
    /// The pinned ffmpeg <c>blend</c> filter option for a blending word (<c>all_mode=multiply</c>, or
    /// <c>all_expr=...</c> for softLight), or <c>null</c> for <c>normal</c>/<c>null</c>/unknown.
    /// </summary>
    public static string? FfmpegOption(string? word) => word switch
    {
        "multiply" => "all_mode=multiply",
        "screen" => "all_mode=screen",
        "overlay" => "all_mode=overlay",
        "softLight" => "all_expr=A*A/255+2*B*A*(255-A)/65025",
        "hardLight" => "all_mode=hardlight",
        "darken" => "all_mode=darken",
        "lighten" => "all_mode=lighten",
        "add" => "all_mode=addition",
        "difference" => "all_mode=difference",
        _ => null,
    };
}
