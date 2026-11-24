namespace ReelBolt.Shared.Editing;

/// <summary>Where a source picture lands in a canvas, in canvas pixels (negative offsets = cropped away).</summary>
public readonly record struct CanvasFitRect(double OffsetX, double OffsetY, double Width, double Height);

/// <summary>
/// The compile's source-to-canvas fit arithmetic, shared so the editor's lineage seeder and the engine
/// (<c>VideoCompileStepExecutor.SourceCanvasFit.For</c>, which delegates here) use ONE implementation.
/// Mirrors ffmpeg's <c>force_original_aspect_ratio=decrease</c> + centred <c>pad</c>, or with <c>cover</c>
/// <c>increase</c> + <c>crop</c> (optionally placed on a focus point).
/// </summary>
public static class CanvasFitGeometry
{
    /// <param name="focusX">Cover crop centre as a fraction of the source (null or 0.5/0.5 = centred).</param>
    /// <param name="focusPad">The subject focus says the clip must be shown whole: cover is ignored.</param>
    public static CanvasFitRect For(
        int sourceWidth, int sourceHeight, int canvasWidth, int canvasHeight, bool cover,
        double? focusX = null, double? focusY = null, bool focusPad = false)
    {
        int cw = Math.Max(2, canvasWidth);
        int ch = Math.Max(2, canvasHeight);
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new CanvasFitRect(0, 0, cw, ch);

        if (focusPad)
            cover = false;

        double scale = cover
            ? Math.Max(cw / (double)sourceWidth, ch / (double)sourceHeight)
            : Math.Min(cw / (double)sourceWidth, ch / (double)sourceHeight);
        double innerW = sourceWidth * scale;
        double innerH = sourceHeight * scale;
        bool centre = focusX is null || focusY is null || (Math.Abs(focusX.Value - 0.5) < 1e-9 && Math.Abs(focusY.Value - 0.5) < 1e-9);
        if (cover && !centre)
        {
            // Mirrors CoverCropFilter's expression: x = clamp(fx*iw - ow/2, 0, iw - ow).
            double x = Math.Clamp(focusX!.Value * innerW - cw / 2.0, 0, Math.Max(0, innerW - cw));
            double y = Math.Clamp(focusY!.Value * innerH - ch / 2.0, 0, Math.Max(0, innerH - ch));
            return new CanvasFitRect(-x, -y, innerW, innerH);
        }

        return new CanvasFitRect((cw - innerW) / 2.0, (ch - innerH) / 2.0, innerW, innerH);
    }
}
