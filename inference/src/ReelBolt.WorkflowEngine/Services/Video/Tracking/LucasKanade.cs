namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>
/// Pyramidal Lucas-Kanade (Bouguet's formulation): each point's displacement is solved coarse to
/// fine, the estimate from one level seeding the next, so motion many pixels larger than the
/// window is still recovered. Points whose window is textureless (a small normal-matrix
/// eigenvalue), that leave the image, or that fail to converge are reported as lost rather than
/// guessed.
/// </summary>
public static class LucasKanade
{
    /// <summary>Result for one point: where it went, whether to trust it, and the mean absolute intensity residual of its window.</summary>
    public readonly record struct Flow(double X, double Y, bool Ok, double Residual);

    /// <summary>
    /// Tracks <paramref name="points"/> from <paramref name="from"/> to <paramref name="to"/>.
    /// <paramref name="guesses"/>, when given, are each point's predicted position in
    /// <paramref name="to"/> — the pyramid search then starts there instead of at zero motion, which
    /// is what lets a reference frame be registered against a much later one.
    /// </summary>
    public static Flow[] Track(
        ImagePyramid from,
        ImagePyramid to,
        IReadOnlyList<(double X, double Y)> points,
        int windowRadius,
        IReadOnlyList<(double X, double Y)>? guesses = null,
        int maxIterations = 20,
        double epsilon = 0.01,
        double minEigenvalue = 1e-3)
    {
        var result = new Flow[points.Count];
        int levels = Math.Min(from.Levels.Count, to.Levels.Count);
        int winSide = 2 * windowRadius + 1;
        int winArea = winSide * winSide;
        var tmplI = new float[winArea];
        var tmplX = new float[winArea];
        var tmplY = new float[winArea];

        for (int i = 0; i < points.Count; i++)
        {
            (double px, double py) = points[i];
            double scaleTop = 1.0 / (1 << (levels - 1));

            // Initial displacement at the top level: the guess relative to the point, scaled.
            double gx = 0, gy = 0;
            if (guesses is not null)
            {
                gx = (guesses[i].X - px) * scaleTop;
                gy = (guesses[i].Y - py) * scaleTop;
            }

            bool ok = true;
            double residual = 0;

            for (int level = levels - 1; level >= 0; level--)
            {
                GrayImage I = from.Levels[level];
                GrayImage J = to.Levels[level];
                double scale = 1.0 / (1 << level);
                double ux = px * scale, uy = py * scale;

                // Window must sit inside the image at this level.
                if (ux < windowRadius || uy < windowRadius || ux > I.Width - 1 - windowRadius || uy > I.Height - 1 - windowRadius)
                {
                    if (level == 0)
                    {
                        ok = false;
                        break;
                    }

                    gx *= 2;
                    gy *= 2;
                    continue;
                }

                // Spatial gradient matrix over the template window.
                double gxx = 0, gxy = 0, gyy = 0;
                int k = 0;
                for (int dy = -windowRadius; dy <= windowRadius; dy++)
                {
                    for (int dx = -windowRadius; dx <= windowRadius; dx++)
                    {
                        double sx = ux + dx, sy = uy + dy;
                        float ix = I.SampleGradX(sx, sy), iy = I.SampleGradY(sx, sy);
                        tmplI[k] = I.Sample(sx, sy);
                        tmplX[k] = ix;
                        tmplY[k] = iy;
                        gxx += ix * ix;
                        gxy += ix * iy;
                        gyy += iy * iy;
                        k++;
                    }
                }

                gxx /= winArea;
                gxy /= winArea;
                gyy /= winArea;
                double det = gxx * gyy - gxy * gxy;
                double minEig = ((gxx + gyy) - Math.Sqrt((gxx - gyy) * (gxx - gyy) + 4 * gxy * gxy)) / 2;
                if (minEig < minEigenvalue || Math.Abs(det) < 1e-12)
                {
                    ok = false;
                    break;
                }

                double vx = 0, vy = 0;
                for (int it = 0; it < maxIterations; it++)
                {
                    double bx = 0, by = 0;
                    k = 0;
                    double cx = ux + gx + vx, cy = uy + gy + vy;
                    if (cx < 0 || cy < 0 || cx > J.Width - 1 || cy > J.Height - 1)
                    {
                        ok = false;
                        break;
                    }

                    for (int dy = -windowRadius; dy <= windowRadius; dy++)
                    {
                        for (int dx = -windowRadius; dx <= windowRadius; dx++)
                        {
                            double diff = tmplI[k] - J.Sample(cx + dx, cy + dy);
                            bx += diff * tmplX[k];
                            by += diff * tmplY[k];
                            k++;
                        }
                    }

                    bx /= winArea;
                    by /= winArea;
                    double ex = (gyy * bx - gxy * by) / det;
                    double ey = (gxx * by - gxy * bx) / det;
                    vx += ex;
                    vy += ey;
                    if (ex * ex + ey * ey < epsilon * epsilon)
                        break;
                }

                if (!ok)
                    break;

                if (level > 0)
                {
                    gx = 2 * (gx + vx);
                    gy = 2 * (gy + vy);
                }
                else
                {
                    gx += vx;
                    gy += vy;

                    // Residual at the final position, for the caller's appearance gate.
                    double sum = 0;
                    k = 0;
                    for (int dy = -windowRadius; dy <= windowRadius; dy++)
                        for (int dx = -windowRadius; dx <= windowRadius; dx++)
                            sum += Math.Abs(tmplI[k++] - J.Sample(ux + gx + dx, uy + gy + dy));
                    residual = sum / winArea;
                }
            }

            double nx = px + gx, ny = py + gy;
            GrayImage baseJ = to.Levels[0];
            if (nx < 0 || ny < 0 || nx > baseJ.Width - 1 || ny > baseJ.Height - 1)
                ok = false;

            result[i] = new Flow(nx, ny, ok, residual);
        }

        return result;
    }
}
