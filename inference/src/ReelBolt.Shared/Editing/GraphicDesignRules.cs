using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Editing;

/// <summary>
/// Where a designed graphic is rendered and where it lands on the picture — the pure arithmetic the
/// Inference API (when it accepts a request) and the WorkflowEngine (when it briefs the designer and
/// names the result) must agree on. See docs/editor.md "Designed graphics".
/// </summary>
/// <remarks>
/// <para>
/// <b>Two shapes of request.</b> With no size, the graphic is designed on a TRANSPARENT frame the size of
/// the whole picture and the placement word tells the designer where on it to draw; the result is then
/// cropped to what was drawn, so the layer is tight around the graphic and sits exactly where it was
/// drawn. With a size, the frame IS the graphic's box (a 600 x 200 badge) and the placement word decides
/// where that box sits on the picture. Either way the work-in-progress region the editor blurs is
/// <see cref="GraphicFrame.Target"/>.
/// </para>
/// <para>
/// <b>Caps.</b> A side is 64..4096 pixels and a frame at most 16.7 megapixels: a Remotion still renders in
/// a headless browser inside the sandbox, and a 4K still is already seconds of work there. A picture larger
/// than the cap is designed on a proportionally smaller frame and the layer is scaled back up
/// (<see cref="GraphicFrame.Scale"/>), so a 8192-wide canvas still gets a whole-picture frame.
/// </para>
/// </remarks>
public static class GraphicDesignRules
{
    public const int MaxDescriptionLength = 2000;
    public const int MinSide = 64;
    public const int MaxSide = 4096;
    public const long MaxPixels = 16_777_216;

    /// <summary>The composition id a NEW graphic's scaffold registers.</summary>
    public const string ScaffoldCompositionId = "Graphic";

    /// <summary>The placement words, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<string> Placements =
        ["center", "top", "bottom", "left", "right", "topLeft", "topRight", "bottomLeft", "bottomRight"];

    /// <summary>The canonical spelling of a placement word (case-insensitive), or null when it is not one.</summary>
    public static string? NormalisePlacement(string? placement)
    {
        if (string.IsNullOrWhiteSpace(placement)) return "center";
        string trimmed = placement.Trim().Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
        if (trimmed.Equals("centre", StringComparison.OrdinalIgnoreCase)) trimmed = "center";
        return Placements.FirstOrDefault(p => string.Equals(p, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The frame a request designs on and where it lands. <paramref name="canvasWidth"/>/
    /// <paramref name="canvasHeight"/> are the picture's size (clamped to at least one pixel);
    /// <paramref name="width"/>/<paramref name="height"/> the requested box, or null for the whole picture.
    /// </summary>
    public static GraphicFrame ResolveFrame(int canvasWidth, int canvasHeight, int? width, int? height, string placement)
    {
        int cw = Math.Max(1, canvasWidth);
        int ch = Math.Max(1, canvasHeight);
        string where = NormalisePlacement(placement) ?? "center";

        if (width is not { } w || height is not { } h)
        {
            double scale = Math.Max(1d, Math.Max(Math.Max((double)cw / MaxSide, (double)ch / MaxSide), Math.Sqrt((double)cw * ch / MaxPixels)));
            int fw = Math.Clamp((int)Math.Round(cw / scale), MinSide, MaxSide);
            int fh = Math.Clamp((int)Math.Round(ch / scale), MinSide, MaxSide);
            GraphicRegion target = WholePictureTarget(cw, ch, where);
            return new GraphicFrame(fw, fh, 0, 0, (double)cw / fw, target, Sized: false, where);
        }

        int bw = Math.Clamp(w, MinSide, MaxSide);
        int bh = Math.Clamp(h, MinSide, MaxSide);
        if ((long)bw * bh > MaxPixels)
        {
            double shrink = Math.Sqrt((double)MaxPixels / ((long)bw * bh));
            bw = Math.Max(MinSide, (int)Math.Floor(bw * shrink));
            bh = Math.Max(MinSide, (int)Math.Floor(bh * shrink));
        }

        double margin = Math.Round(Math.Min(cw, ch) * 0.04);
        (double x, double y) = BoxOrigin(cw, ch, bw, bh, margin, where);
        return new GraphicFrame(bw, bh, x, y, 1, new GraphicRegion(x, y, bw, bh), Sized: true, where);
    }

    /// <summary>The region of a whole-picture frame the placement word points at, in canvas pixels.</summary>
    private static GraphicRegion WholePictureTarget(int cw, int ch, string where)
    {
        (double l, double t, double r, double b) = where switch
        {
            "top" => (0.10, 0.05, 0.90, 0.38),
            "bottom" => (0.10, 0.62, 0.90, 0.95),
            "left" => (0.05, 0.15, 0.48, 0.85),
            "right" => (0.52, 0.15, 0.95, 0.85),
            "topLeft" => (0.05, 0.05, 0.50, 0.48),
            "topRight" => (0.50, 0.05, 0.95, 0.48),
            "bottomLeft" => (0.05, 0.52, 0.50, 0.95),
            "bottomRight" => (0.50, 0.52, 0.95, 0.95),
            _ => (0.15, 0.25, 0.85, 0.75)
        };
        return new GraphicRegion(Math.Round(cw * l), Math.Round(ch * t), Math.Round(cw * (r - l)), Math.Round(ch * (b - t)));
    }

    private static (double X, double Y) BoxOrigin(int cw, int ch, int bw, int bh, double margin, string where)
    {
        double left = margin;
        double centreX = Math.Round((cw - bw) / 2d);
        double right = cw - bw - margin;
        double top = margin;
        double centreY = Math.Round((ch - bh) / 2d);
        double bottom = ch - bh - margin;
        return where switch
        {
            "top" => (centreX, top),
            "bottom" => (centreX, bottom),
            "left" => (left, centreY),
            "right" => (right, centreY),
            "topLeft" => (left, top),
            "topRight" => (right, top),
            "bottomLeft" => (left, bottom),
            "bottomRight" => (right, bottom),
            _ => (centreX, centreY)
        };
    }

    /// <summary>
    /// Where on a WHOLE-PICTURE frame the designer should draw, in words and as a box the brief can quote
    /// (frame pixels). For a sized frame the graphic fills the frame and the placement is the frame's.
    /// </summary>
    public static string PlacementPhrase(string placement) => (NormalisePlacement(placement) ?? "center") switch
    {
        "top" => "across the top third, centred left to right",
        "bottom" => "across the bottom third, centred left to right",
        "left" => "on the left half, centred top to bottom",
        "right" => "on the right half, centred top to bottom",
        "topLeft" => "in the top-left quarter",
        "topRight" => "in the top-right quarter",
        "bottomLeft" => "in the bottom-left quarter",
        "bottomRight" => "in the bottom-right quarter",
        _ => "in the centre"
    };

    /// <summary>
    /// The object key of a verified graphic PNG for <paramref name="documentId"/>:
    /// <c>projects/{p}/agentFiles/image-editor/{documentId}/graphic-{id}.png</c> — under the editor's own
    /// per-document prefix, so it is counted, retained and purged exactly like every other editor object.
    /// </summary>
    public static string GraphicKey(Guid projectId, Guid documentId, Guid id) =>
        $"{ProjectFilePath.BuildStoragePrefix(projectId, "agentFiles")}image-editor/{documentId}/graphic-{id}.png";

    private static readonly Regex GraphicTail = new(
        @"^graphic-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.png$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="key"/> is exactly a <see cref="GraphicKey"/> of this project and document.
    /// The API checks every engine-written key with this before it makes a layer of it.
    /// </summary>
    public static bool IsGraphicKey(string? key, Guid projectId, Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        string prefix = $"{ProjectFilePath.BuildStoragePrefix(projectId, "agentFiles")}image-editor/{documentId}/";
        return key.StartsWith(prefix, StringComparison.Ordinal) && GraphicTail.IsMatch(key[prefix.Length..]);
    }
}

/// <summary>A rectangle in canvas pixels.</summary>
public readonly record struct GraphicRegion(double X, double Y, double Width, double Height);

/// <summary>
/// The render frame of a designed graphic and where it lands: frame pixel (0,0) is canvas pixel
/// (<see cref="X"/>, <see cref="Y"/>) and one frame pixel is <see cref="Scale"/> canvas pixels.
/// </summary>
public readonly record struct GraphicFrame(
    int Width, int Height, double X, double Y, double Scale, GraphicRegion Target, bool Sized, string Placement);
