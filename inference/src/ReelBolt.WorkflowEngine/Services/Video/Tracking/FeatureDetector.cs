namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// Shi-Tomasi "good features to track": points whose structure tensor's smaller eigenvalue is
/// large — corners and texture, the only places Lucas-Kanade can resolve motion in both
/// directions. Restricted to a region (the quad being tracked) and spread by a minimum distance so
/// a handful of strong corners cannot crowd out the rest of the target.
/// </summary>
public static class FeatureDetector
{
    /// <summary>
    /// Up to <paramref name="maxCorners"/> features inside <paramref name="region"/> (a convex
    /// polygon in pixel coordinates, or null for the whole image), strongest first. A candidate must
    /// reach <paramref name="qualityLevel"/> of the strongest response and sit at least
    /// <paramref name="minDistance"/> px from every stronger accepted feature.
    /// </summary>
    public static List<(double X, double Y)> Detect(
        GrayImage image,
        IReadOnlyList<(double X, double Y)>? region,
        int maxCorners,
        double qualityLevel = 0.01,
        double minDistance = 5,
        int blockRadius = 2)
    {
        int w = image.Width, h = image.Height;
        float[] gx = image.GradX, gy = image.GradY;

        // Bounding box of the region (plus the block radius), so the tensor is only computed where
        // it is needed — the target is usually a small part of the frame.
        int bx0 = 1, by0 = 1, bx1 = w - 2, by1 = h - 2;
        if (region is { Count: >= 3 })
        {
            bx0 = Math.Max(1, (int)Math.Floor(region.Min(p => p.X)) - blockRadius);
            by0 = Math.Max(1, (int)Math.Floor(region.Min(p => p.Y)) - blockRadius);
            bx1 = Math.Min(w - 2, (int)Math.Ceiling(region.Max(p => p.X)) + blockRadius);
            by1 = Math.Min(h - 2, (int)Math.Ceiling(region.Max(p => p.Y)) + blockRadius);
        }

        if (bx1 <= bx0 || by1 <= by0)
            return [];

        int rw = bx1 - bx0 + 1, rh = by1 - by0 + 1;
        var response = new float[rw * rh];
        float maxResponse = 0;

        for (int y = by0; y <= by1; y++)
        {
            for (int x = bx0; x <= bx1; x++)
            {
                double a = 0, b = 0, c = 0;
                for (int dy = -blockRadius; dy <= blockRadius; dy++)
                {
                    int yy = Math.Clamp(y + dy, 0, h - 1);
                    for (int dx = -blockRadius; dx <= blockRadius; dx++)
                    {
                        int xx = Math.Clamp(x + dx, 0, w - 1);
                        float ix = gx[yy * w + xx], iy = gy[yy * w + xx];
                        a += ix * ix;
                        b += ix * iy;
                        c += iy * iy;
                    }
                }

                double minEig = ((a + c) - Math.Sqrt((a - c) * (a - c) + 4 * b * b)) / 2;
                float r = (float)Math.Max(0, minEig);
                response[(y - by0) * rw + (x - bx0)] = r;
                if (r > maxResponse)
                    maxResponse = r;
            }
        }

        if (maxResponse <= 0)
            return [];

        float threshold = (float)(maxResponse * qualityLevel);
        var candidates = new List<(float R, int X, int Y)>();
        for (int y = by0 + 1; y < by1; y++)
        {
            for (int x = bx0 + 1; x < bx1; x++)
            {
                float r = response[(y - by0) * rw + (x - bx0)];
                if (r < threshold)
                    continue;

                // 3x3 non-maximum suppression.
                bool isMax = true;
                for (int dy = -1; dy <= 1 && isMax; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && response[(y + dy - by0) * rw + (x + dx - bx0)] > r)
                        {
                            isMax = false;
                            break;
                        }

                if (!isMax)
                    continue;

                if (region is { Count: >= 3 } && !Geometry.PointInPolygon(region, x, y))
                    continue;

                candidates.Add((r, x, y));
            }
        }

        // Strongest first; ties by position so the result is deterministic.
        candidates.Sort((p, q) =>
        {
            int byR = q.R.CompareTo(p.R);
            if (byR != 0) return byR;
            int byY = p.Y.CompareTo(q.Y);
            return byY != 0 ? byY : p.X.CompareTo(q.X);
        });

        var accepted = new List<(double X, double Y)>();
        double minD2 = minDistance * minDistance;
        foreach ((float _, int x, int y) in candidates)
        {
            bool farEnough = true;
            foreach ((double ax, double ay) in accepted)
            {
                double ddx = ax - x, ddy = ay - y;
                if (ddx * ddx + ddy * ddy < minD2)
                {
                    farEnough = false;
                    break;
                }
            }

            if (!farEnough)
                continue;

            accepted.Add((x, y));
            if (accepted.Count >= maxCorners)
                break;
        }

        return accepted;
    }
}
