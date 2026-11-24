namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One mesh keyframe: the <c>(Rows+1) x (Cols+1)</c> control-point grid in PIXEL space on the
/// canonical encode frame, row-major from the top-left, keyed by the insert branch's own frame
/// index (frame 0 = the insert's on-screen start). Exactly the
/// <see cref="InsertQuadFrame"/> contract, generalised from four corners to a grid — and, like it,
/// every number was computed by deterministic C# (<c>ChromaQuadTracker</c> corners +
/// <c>ChromaPlateEdgeProfiler</c> bows, run through <c>InsertSurfaceMesh</c> and
/// <c>OutputTimeline</c>), never model-supplied.
/// </summary>
public sealed record InsertMeshFrame(long FrameIndex, (double X, double Y)[][] ControlPoints);

/// <summary>
/// The non-planar half of a resolved insert, hung off <see cref="ResolvedScreenInsert.Mesh"/>.
/// Present only when the plate's measured silhouette curvature cleared
/// <c>VideoCompileStepConfig.MinInsertCurvature</c>, so a flat plate never pays for the mesh
/// machinery and keeps the original single-<c>perspective</c> filtergraph. When present, these keyframes REPLACE
/// <see cref="ResolvedScreenInsert.Keyframes"/> for rendering — the flat corner series stays on
/// the record because the EDL block still reports it, and because it is what the insert degrades
/// back to if the mesh is rejected.
/// </summary>
public sealed record InsertMeshPlan(int Rows, int Cols, IReadOnlyList<InsertMeshFrame> Keyframes);

/// <summary>
/// Builds the ffmpeg filter-chain fragment that corner-pins a rendered scene into a tracked
/// NON-PLANAR region — a curved, bent or otherwise non-flat chroma plate (see
/// docs/video-editing.md "Non-planar surfaces and multi-plate compositing"). Pure — no I/O, no
/// ffmpeg.
///
/// <para>
/// <b>The mechanism.</b> A single projective transform cannot represent a non-planar surface at
/// all, so the surface is tiled into <c>Rows x Cols</c> cells and each cell gets its own
/// <c>perspective</c> warp — the standard piecewise-projective mesh approximation. The
/// alternative considered was ffmpeg's <c>remap</c> filter, which takes an arbitrary per-pixel
/// coordinate map and would need no tiling at all; it was rejected on two measured grounds.
/// <c>remap</c> samples NEAREST-NEIGHBOUR from integer <c>gray16</c> coordinate maps, which
/// shimmers badly on exactly the content this feature exists to composite (a UI with text, scaled
/// down into a phone-sized region), and it would require writing gigabytes of raw per-frame map
/// streams to scratch and decoding them back — a new I/O mechanism, where the tiled form stays
/// what every other filter builder here already is: a pure string. The tiling costs encode time
/// instead, and that was measured as cheap: a 3x6 mesh at 1080p ran at 94 fps against 253 fps for
/// the single-quad path.
/// </para>
/// <para>
/// <b>Per cell</b>, on its own small canvas sized to that cell's destination bounding box over the
/// whole insert (NOT the full frame — that is what keeps a 18-cell mesh affordable, and keeps the
/// source cell being resampled at roughly its destination scale):
/// </para>
/// <code>
/// [asset] fps, format, setpts(+outputStart), split into Rows*Cols
/// [cell]  crop to this cell's source sub-rectangle (floor-tiled, so cells abut exactly),
///         scale to (cw-2)x(ch-2), pad to cw x ch and smear the content's edge pixels
///         over the 1px border (fillborders mode=smear, not a black rim),
///         perspective sense=destination eval=frame interpolation=cubic -> warped cell
///         (the cell MASK's perspective stays bilinear: see <see cref="ScreenInsertFilterBuilder.ContentInterpolation"/>)
/// color=white (cw-2)x(ch-2), pad 1px black, format=gray, SAME perspective -> warped cell mask
/// alphamerge ; overlay at the cell canvas origin, enable='between(t,start,end)'
/// </code>
/// <para>
/// <b>Seams.</b> Two things keep the cell boundaries invisible. The <c>perspective</c> corner
/// values are not the cell's mesh corners but the exact projective EXTRAPOLATION of them out to
/// the padded canvas corners (<see cref="ExtrapolateToCanvas"/>) — so the content's own corners,
/// which sit 1px inside the canvas because of the border, land precisely on the mesh points
/// rather than 1px short of them, which would open a gap at every interior boundary. On top of
/// that each cell is grown by <see cref="InteriorOverlapPx"/> on every side that HAS a neighbour,
/// drawn over the already-composited one, which is what covers the warp's bilinear alpha ramp.
/// The mesh's outer boundary is never grown: it must stay exactly where the silhouette was
/// measured.
/// </para>
/// <para>
/// Not one model-originated character reaches this string: the model contributed only an offered
/// region id, and every number below came from
/// <c>ChromaQuadTracker</c>/<c>ChromaPlateEdgeProfiler</c>/<c>InsertSurfaceMesh</c>/<c>OutputTimeline</c>/ffprobe,
/// formatted through <see cref="FfmpegArgvFormat"/> exactly like every other filter builder here.
/// </para>
/// </summary>
public static class ScreenInsertMeshFilterBuilder
{
    /// <summary>Extra seconds of synthesized mask beyond the on-screen window — same rationale as the planar builder's constant of the same name.</summary>
    private const double MaskDurationMarginSec = 1.0;

    /// <summary>Slack in pixels between a cell's destination bounding box and its canvas, so the projectively-extrapolated canvas corners always stay on the canvas.</summary>
    private const int CanvasMarginPx = 4;

    /// <summary>
    /// Destination-pixel overlap grown onto each cell side that HAS a neighbour, on top of the
    /// exact border extrapolation. Not cosmetic: <c>perspective</c> interpolates bilinearly, so
    /// both the warped content and the warped white mask fade out over roughly a pixel at the
    /// cell edge. Two abutting cells each contributing ~50% alpha there composites to ~75%
    /// coverage, and the plate shows through the remainder — real footage rendered a visible
    /// green hairline along every interior boundary until this was widened enough for each cell's
    /// FULLY opaque interior to cover its neighbour's ramp.
    /// </summary>
    private const double InteriorOverlapPx = 2.0;

    /// <summary>Smallest cell canvas worth building; below this the mesh is not viable and the caller degrades to the planar path.</summary>
    internal const int MinCanvasPx = 8;

    /// <summary>
    /// Emits one mesh insert's segments into <paramref name="segments"/>, chaining from
    /// <paramref name="currentLabel"/> and ending at <paramref name="outLabel"/> — the same
    /// per-insert contract <see cref="ScreenInsertFilterBuilder"/> uses for a flat insert, so the
    /// two flavours interleave inside one chain and the compile step's input bookkeeping,
    /// stage ordering and label plumbing are all untouched.
    /// </summary>
    public static void AppendInsert(
        List<string> segments,
        string currentLabel,
        ResolvedScreenInsert insert,
        InsertMeshPlan mesh,
        int index,
        int inputIndex,
        string outLabel,
        // The whole-frame plate matte, or null when this insert is not matted. Each cell crops
        // its own window out of it — see the per-cell crop below.
        string? matteLabel = null)
    {
        int i = index;
        int rows = Math.Max(1, mesh.Rows);
        int cols = Math.Max(1, mesh.Cols);
        int cellCount = rows * cols;

        string fps = $"{FfmpegArgvFormat.Number(insert.FpsNum)}/{FfmpegArgvFormat.Number(insert.FpsDen)}";
        string start = FfmpegArgvFormat.Number(insert.OutputStartSec);
        string end = FfmpegArgvFormat.Number(insert.OutputEndSec);
        double maskDuration = Math.Max(0.1, insert.OutputEndSec - insert.OutputStartSec) + MaskDurationMarginSec;

        // A matted insert's cells are padded from t=0 rather than time-shifted (see
        // ScreenInsertFilterBuilder.DelayToOutputStart — it is what bounds the compile's memory).
        // The pad must follow each cell's `perspective`, whose `in` counts that filter's own input
        // frames, so the shared content is only re-based to zero here and each cell is padded
        // after its warp; an unmatted insert keeps the single shift before the split.
        string contentDelay = insert.Matte is null
            ? ScreenInsertFilterBuilder.DelayToOutputStart(insert)
            : "setpts=PTS-STARTPTS";
        string cellDelay = insert.Matte is null
            ? string.Empty
            : "," + ScreenInsertFilterBuilder.DelayToOutputStart(insert);

        // The asset is conformed and time-shifted ONCE, before the split: `perspective`'s
        // per-frame `in` counts its own input frames and is unaffected by the PTS shift, so
        // doing this here rather than per cell costs nothing and keeps the graph readable.
        segments.Add(
            $"[{inputIndex}:v]{ScreenInsertFilterBuilder.Prefix(insert.ContentPrefilter)}fps={fps},format=yuv444p,{contentDelay},split={FfmpegArgvFormat.Number(cellCount)}" +
            string.Concat(Enumerable.Range(0, cellCount).Select(c => $"[mi{i}c{c}]")));

        // The matte is a whole-frame stream but every cell composites on its own small canvas, so
        // the matte is split once and each copy cropped to that cell's fixed canvas window. A
        // label can only be consumed once, which is why the split count is fixed here rather than
        // each cell referencing the matte directly.
        var matteCells = new Queue<string>();
        if (matteLabel is not null)
        {
            segments.Add(
                $"{matteLabel}split={FfmpegArgvFormat.Number(cellCount)}" +
                string.Concat(Enumerable.Range(0, cellCount).Select(c => $"[mmt{i}c{c}]")));
            for (int c = 0; c < cellCount; c++)
                matteCells.Enqueue($"[mmt{i}c{c}]");
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int cell = r * cols + c;
                CellPlacement placement = PlaceCell(mesh, r, c, rows, cols);
                string perspective = BuildCellPerspective(placement);
                string contentPerspective = BuildCellPerspective(placement, cubic: true);

                // Floor-tiled fractional crop: cell j's right edge is cell j+1's left edge by
                // construction, whatever the asset's real dimensions turn out to be, so the
                // source tiling has neither gaps nor double-sampled columns.
                string cropW = $"floor(iw*{FfmpegArgvFormat.Number(c + 1)}/{FfmpegArgvFormat.Number(cols)})-floor(iw*{FfmpegArgvFormat.Number(c)}/{FfmpegArgvFormat.Number(cols)})";
                string cropH = $"floor(ih*{FfmpegArgvFormat.Number(r + 1)}/{FfmpegArgvFormat.Number(rows)})-floor(ih*{FfmpegArgvFormat.Number(r)}/{FfmpegArgvFormat.Number(rows)})";
                string cropX = $"floor(iw*{FfmpegArgvFormat.Number(c)}/{FfmpegArgvFormat.Number(cols)})";
                string cropY = $"floor(ih*{FfmpegArgvFormat.Number(r)}/{FfmpegArgvFormat.Number(rows)})";

                segments.Add(
                    $"[mi{i}c{cell}]crop=w='{cropW}':h='{cropH}':x='{cropX}':y='{cropY}'," +
                    $"scale={FfmpegArgvFormat.Number(placement.CanvasWidth - 2)}:{FfmpegArgvFormat.Number(placement.CanvasHeight - 2)}:flags=bicubic," +
                    $"format=yuv444p,pad={FfmpegArgvFormat.Number(placement.CanvasWidth)}:{FfmpegArgvFormat.Number(placement.CanvasHeight)}:1:1:black," +
                    $"{ScreenInsertFilterBuilder.SmearBorders(new ScreenInsertFilterBuilder.PrescaleRegion(placement.CanvasWidth - 2, placement.CanvasHeight - 2, 1, 1), placement.CanvasWidth, placement.CanvasHeight)}," +
                    $"{contentPerspective}{cellDelay}[mc{i}c{cell}]");

                segments.Add(
                    $"color=c=white:size={FfmpegArgvFormat.Number(placement.CanvasWidth - 2)}x{FfmpegArgvFormat.Number(placement.CanvasHeight - 2)}:rate={fps}:duration={FfmpegArgvFormat.Number(maskDuration)}," +
                    $"format=gray,pad={FfmpegArgvFormat.Number(placement.CanvasWidth)}:{FfmpegArgvFormat.Number(placement.CanvasHeight)}:1:1:black," +
                    $"{perspective},{ScreenInsertFilterBuilder.DelayToOutputStart(insert)}[mm{i}c{cell}]");

                string cellMask = $"[mm{i}c{cell}]";
                if (matteLabel is not null)
                {
                    // Crop the whole-frame matte to this cell's own constant canvas window, then
                    // AND it with the warped cell mask — the same conjunction the planar path
                    // makes, restricted to this canvas.
                    segments.Add(
                        $"{matteCells.Dequeue()}crop=" +
                        $"w={FfmpegArgvFormat.Number(placement.CanvasWidth)}:h={FfmpegArgvFormat.Number(placement.CanvasHeight)}:" +
                        $"x={FfmpegArgvFormat.Number(placement.OriginX)}:y={FfmpegArgvFormat.Number(placement.OriginY)}:exact=1[mmc{i}c{cell}]");
                    segments.Add($"{cellMask}[mmc{i}c{cell}]blend=all_mode=multiply[mmm{i}c{cell}]");
                    cellMask = $"[mmm{i}c{cell}]";
                }

                segments.Add($"[mc{i}c{cell}]{cellMask}alphamerge[ma{i}c{cell}]");

                bool isFinalCell = cell == cellCount - 1;
                string cellOut = isFinalCell ? outLabel : $"[mo{i}c{cell}]";
                segments.Add(
                    $"{currentLabel}[ma{i}c{cell}]overlay=x={FfmpegArgvFormat.Number(placement.OriginX)}:y={FfmpegArgvFormat.Number(placement.OriginY)}" +
                    $":eof_action=pass:enable='between(t,{start},{end})'{cellOut}");

                currentLabel = cellOut;
            }
        }
    }

    /// <summary>Which sides of a cell abut another cell, and so need overlap grown onto them.</summary>
    internal readonly record struct CellNeighbours(bool Left, bool Right, bool Top, bool Bottom);

    /// <summary>One cell's fixed canvas plus its per-frame extrapolated corner series.</summary>
    internal sealed record CellPlacement(
        int OriginX, int OriginY, int CanvasWidth, int CanvasHeight,
        IReadOnlyList<(long Frame, (double X, double Y)[] Corners)> Frames);

    /// <summary>
    /// Sizes cell <c>(row, col)</c>'s canvas to the union of its destination quad over every
    /// keyframe (plus <see cref="CanvasMarginPx"/>), then expresses each keyframe's corners in
    /// canvas-local coordinates, projectively extrapolated out to the padded canvas corners.
    /// A constant canvas and a constant overlay offset mean only the corner expressions vary
    /// per frame — the same shape the planar builder already produces.
    /// </summary>
    internal static CellPlacement PlaceCell(InsertMeshPlan mesh, int row, int col, int rows, int cols)
    {
        // Overlap is grown only onto sides that actually have a neighbour: the mesh's OUTER
        // boundary must stay exactly where the silhouette was measured, since that is the whole
        // point of the non-planar path (VideoCompileStepConfig.InsertOverscan is the separate,
        // deliberate knob for spilling past the plate edge).
        var neighbours = new CellNeighbours(
            Left: col > 0, Right: col < cols - 1, Top: row > 0, Bottom: row < rows - 1);

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (InsertMeshFrame frame in mesh.Keyframes)
        {
            foreach ((double x, double y) in CellCorners(frame, row, col))
            {
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        int originX = (int)Math.Floor(minX) - CanvasMarginPx;
        int originY = (int)Math.Floor(minY) - CanvasMarginPx;
        int canvasWidth = Math.Max(MinCanvasPx, (int)Math.Ceiling(maxX) - originX + CanvasMarginPx);
        int canvasHeight = Math.Max(MinCanvasPx, (int)Math.Ceiling(maxY) - originY + CanvasMarginPx);

        var frames = new List<(long, (double X, double Y)[])>(mesh.Keyframes.Count);
        foreach (InsertMeshFrame frame in mesh.Keyframes)
        {
            (double X, double Y)[] corners = CellCorners(frame, row, col);
            (double X, double Y)[] local =
            [
                (corners[0].X - originX, corners[0].Y - originY),
                (corners[1].X - originX, corners[1].Y - originY),
                (corners[2].X - originX, corners[2].Y - originY),
                (corners[3].X - originX, corners[3].Y - originY),
            ];

            frames.Add((frame.FrameIndex, ExtrapolateToCanvas(local, canvasWidth, canvasHeight, neighbours)));
        }

        return new CellPlacement(originX, originY, canvasWidth, canvasHeight, frames);
    }

    /// <summary>The four mesh points bounding cell <c>(row, col)</c>, in TL/TR/BL/BR order.</summary>
    internal static (double X, double Y)[] CellCorners(InsertMeshFrame frame, int row, int col) =>
    [
        frame.ControlPoints[row][col],
        frame.ControlPoints[row][col + 1],
        frame.ControlPoints[row + 1][col],
        frame.ControlPoints[row + 1][col + 1],
    ];

    /// <summary>
    /// Given where a cell's CONTENT corners must land, returns where its padded CANVAS corners
    /// must land. The content occupies <c>[1, w-1] x [1, h-1]</c> of the canvas because of the
    /// 1px black border the warp needs, so asking <c>perspective</c> to send the canvas corners
    /// to the mesh points would land the content one border-pixel short on every side — a visible
    /// gap at every interior cell boundary. This extrapolates the cell's own projective map out
    /// past the content, exactly rather than by a scale-about-the-centroid approximation, and
    /// adds <see cref="OverlapPx"/> so neighbouring cells overdraw each other slightly.
    /// Falls back to the raw corners for a degenerate cell, which the caller has already gated on.
    /// </summary>
    internal static (double X, double Y)[] ExtrapolateToCanvas(
        (double X, double Y)[] contentCorners, int canvasWidth, int canvasHeight,
        CellNeighbours neighbours = default)
    {
        InsertSurfaceMesh.UnitSquareHomography? h = InsertSurfaceMesh.UnitSquareHomography.Solve(
            contentCorners[0], contentCorners[1], contentCorners[2], contentCorners[3]);
        if (h is null)
            return contentCorners;

        double spanU = Math.Max(1, canvasWidth - 2);
        double spanV = Math.Max(1, canvasHeight - 2);
        double left = (1 + (neighbours.Left ? InteriorOverlapPx : 0)) / spanU;
        double right = (1 + (neighbours.Right ? InteriorOverlapPx : 0)) / spanU;
        double top = (1 + (neighbours.Top ? InteriorOverlapPx : 0)) / spanV;
        double bottom = (1 + (neighbours.Bottom ? InteriorOverlapPx : 0)) / spanV;

        return
        [
            h.Map(-left, -top),
            h.Map(1 + right, -top),
            h.Map(-left, 1 + bottom),
            h.Map(1 + right, 1 + bottom),
        ];
    }

    /// <summary>
    /// The <c>perspective</c> filter for one cell — <c>sense=destination</c>, <c>eval=frame</c>,
    /// each of the eight coordinates a piecewise-linear expression in <c>in</c> built by the
    /// planar builder's <see cref="ScreenInsertFilterBuilder.BuildPiecewiseExpr"/> (shared
    /// deliberately: a mesh cell's corner series is the same kind of series as a whole plate's,
    /// and a second implementation would be a second thing to keep correct).
    /// </summary>
    internal static string BuildCellPerspective(CellPlacement placement, bool cubic = false)
    {
        string Expr(int corner, bool isX) =>
            ScreenInsertFilterBuilder.BuildInterpolationExpr(
                placement.Frames
                    .Select(f => (f.Frame + ScreenInsertFilterBuilder.PerspectiveFirstFrame, isX ? f.Corners[corner].X : f.Corners[corner].Y))
                    .ToList());

        return
            $"perspective=x0='{Expr(0, true)}':y0='{Expr(0, false)}'" +
            $":x1='{Expr(1, true)}':y1='{Expr(1, false)}'" +
            $":x2='{Expr(2, true)}':y2='{Expr(2, false)}'" +
            $":x3='{Expr(3, true)}':y3='{Expr(3, false)}'" +
            ":sense=destination:eval=frame" + (cubic ? ScreenInsertFilterBuilder.ContentInterpolation : string.Empty);
    }
}
