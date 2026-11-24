namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Orientation-aware coarse corners for a chroma plate's pixel component.
///
/// <para>
/// <b>Why.</b> The extreme-point corner fit (TL = min x+y, TR = max x−y, …) assumes a roughly
/// axis-aligned plate. Rotate the plate and two of its four extremes converge onto the SAME
/// physical corner; at exactly 45° <c>min(x+y)</c> and <c>max(x−y)</c> are both the top corner,
/// and the "quad" is a triangle covering half the component. The degeneracy guard rejected that
/// exact case, but real footage does not sit on the degenerate angle — it passes THROUGH it. On
/// a generated tablet clip turned from landscape to portrait and a phone whipped up toward the
/// camera, every frame between ~30° and ~60° fitted a kite, the edge-line refinement that
/// starts from those corners followed them, and the composite was garbage for the whole turn.
/// </para>
///
/// <para>
/// <b>How.</b> The plate's own orientation is measured first — the direction of the minimum-area
/// rectangle enclosing the component's convex hull (rotating calipers over the hull's edges, the
/// classic result that one side of that rectangle is collinear with a hull edge) — and the
/// extreme-point rule is then applied in the plate's OWN frame, i.e. to coordinates rotated by
/// minus that angle. In the plate's frame the four extremes are always its four corners,
/// whatever its angle in the image. For an axis-aligned plate the angle is ~0 and the result is
/// the extreme-point fit as before. The angle is normalised to (−45°, 45°], so which physical
/// corner is called "top-left" flips as a plate passes 45°; <see cref="ChromaQuadTracker"/>
/// re-labels each new detection against the track's previous sample so the labelling follows
/// the physical corners through the turn.
/// </para>
///
/// <para>Pure, deterministic, no allocation beyond the hull; never throws.</para>
/// </summary>
internal static class ChromaQuadOrientation
{
    /// <summary>
    /// The angle (radians, in (−π/4, π/4]) of the minimum-area rectangle enclosing the component's
    /// convex hull — the plate's own orientation modulo 90°. 0 for a component too small to have a
    /// hull.
    /// </summary>
    internal static double DominantAngle(List<int> component, int width, int height)
    {
        List<(double X, double Y)> hull = ConvexHull(BoundaryPixels(component, width, height));
        if (hull.Count < 3)
            return 0;

        double bestArea = double.MaxValue;
        double bestAngle = 0;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            double ex = b.X - a.X, ey = b.Y - a.Y;
            double len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-9)
                continue;
            ex /= len;
            ey /= len;

            // Bounding box of the hull in the frame aligned with this edge.
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in hull)
            {
                double u = p.X * ex + p.Y * ey;
                double v = -p.X * ey + p.Y * ex;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            double area = (maxU - minU) * (maxV - minV);
            if (area < bestArea - 1e-9)
            {
                bestArea = area;
                bestAngle = Math.Atan2(ey, ex);
            }
        }

        return Normalize(bestAngle);
    }

    /// <summary>Folds an angle into (−π/4, π/4] — a rectangle's orientation is only defined modulo a quarter turn.</summary>
    internal static double Normalize(double angle)
    {
        const double quarter = Math.PI / 2;
        angle = Math.IEEERemainder(angle, quarter);
        if (angle <= -Math.PI / 4)
            angle += quarter;
        else if (angle > Math.PI / 4)
            angle -= quarter;
        return angle;
    }

    /// <summary>
    /// Extreme-point corners taken in the frame rotated by <paramref name="angle"/>: TL minimises
    /// and BR maximises <c>x'+y'</c>, TR maximises and BL minimises <c>x'−y'</c>. Returned in
    /// ORIGINAL grid pixel coordinates. With <paramref name="angle"/> = 0 this is exactly the
    /// classic fit. Ties resolve to the first pixel in raster order, so the result is total.
    /// </summary>
    internal static ((double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Br, (double X, double Y) Bl) ExtremeCorners(
        List<int> component, int width, double angle)
    {
        double c = Math.Cos(-angle), s = Math.Sin(-angle);
        double minSum = double.MaxValue, maxSum = double.MinValue, minDiff = double.MaxValue, maxDiff = double.MinValue;
        (double X, double Y) tl = default, tr = default, br = default, bl = default;
        foreach (int p in component)
        {
            int x = p % width, y = p / width;
            double rx = x * c - y * s, ry = x * s + y * c;
            double sum = rx + ry, diff = rx - ry;
            if (sum < minSum - 1e-9) { minSum = sum; tl = (x, y); }
            if (sum > maxSum + 1e-9) { maxSum = sum; br = (x, y); }
            if (diff > maxDiff + 1e-9) { maxDiff = diff; tr = (x, y); }
            if (diff < minDiff - 1e-9) { minDiff = diff; bl = (x, y); }
        }

        return (tl, tr, br, bl);
    }

    /// <summary>Pixels of the component with at least one 4-neighbour outside it (or on the grid border) — all the hull can ever touch, at a fraction of the count.</summary>
    private static List<(double X, double Y)> BoundaryPixels(List<int> component, int width, int height)
    {
        var inside = new HashSet<int>(component);
        var boundary = new List<(double X, double Y)>();
        foreach (int p in component)
        {
            int x = p % width, y = p / width;
            if (x == 0 || y == 0 || x == width - 1 || y == height - 1
                || !inside.Contains(p - 1) || !inside.Contains(p + 1)
                || !inside.Contains(p - width) || !inside.Contains(p + width))
            {
                boundary.Add((x, y));
            }
        }

        return boundary;
    }

    /// <summary>Andrew's monotone chain. Counter-clockwise in image coordinates (y down), collinear points dropped.</summary>
    internal static List<(double X, double Y)> ConvexHull(List<(double X, double Y)> points)
    {
        if (points.Count < 3)
            return points;

        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        var hull = new List<(double X, double Y)>(sorted.Count * 2);

        // Lower hull, then upper hull.
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            IEnumerable<(double X, double Y)> seq = pass == 0 ? sorted : Enumerable.Reverse(sorted);
            foreach (var p in seq)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1); // the last point of each pass is the first of the next
        }

        return hull;

        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
    }
}
