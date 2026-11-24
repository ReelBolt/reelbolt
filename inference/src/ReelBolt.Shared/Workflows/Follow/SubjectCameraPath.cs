namespace ReelBolt.Shared.Workflows.Follow;

/// <summary>
/// Pure, deterministic core of "follow the subject": turns a subject path (boxes in SOURCE-frame
/// fractions) into a smooth camera path (centre + zoom keys) that keeps the subject roughly
/// centred and never lets the visible window leave the source frame. No I/O, clocks or randomness.
/// </summary>
public static class SubjectCameraPath
{
    /// <remarks>
    /// Containment keeps the VISIBLE window (framing-aware) inside the source. The engine's own
    /// clamp rule uses the full-frame window and is looser for a cover fit, so the two only agree
    /// once the engine anchors reframes to the visible window (a later layer).
    /// </remarks>
    /// <summary>Computes the camera path over <paramref name="fromSec"/>..<paramref name="toSec"/> (source seconds).</summary>
    public static CameraPathResult Compute(IReadOnlyList<SubjectSample> samples, double fromSec, double toSec, FollowOptions options)
    {
        Validate(samples, fromSec, toSec, options);
        FollowFraming fr = options.Framing;
        double zoomCap = ComputeZoomCap(options);
        double[] t = BuildGrid(fromSec, toSec);
        int n = t.Length;

        // 1. Resample onto the grid; lost points have known[i] == false.
        List<SubjectSample> present = PreparePresent(samples);
        var cx = new double[n];
        var cy = new double[n];
        var bw = new double[n];
        var bh = new double[n];
        var known = new bool[n];
        Resample(present, t, cx, cy, bw, bh, known);

        if (!known.Any(k => k))
        {
            return new CameraPathResult(
                [new CameraKey(Round(fromSec), 0.5, 0.5, 1.0), new CameraKey(Round(toSec), 0.5, 0.5, 1.0)],
                zoomCap, false, [new FollowLostSpan(Round(fromSec), Round(toSec))], 0, 0);
        }

        List<FollowLostSpan> lostSpans = LostSpans(t, known);

        // 3. Targets for present points.
        double fill = FollowTuning.Fill(options.Tightness);
        var tx = new double[n];
        var ty = new double[n];
        var tz = new double[n]; // ln(zoom)
        bool capLimited = false;
        double lnCap = Math.Log(zoomCap);
        for (int i = 0; i < n; i++)
        {
            if (!known[i])
                continue;
            tx[i] = cx[i];
            ty[i] = cy[i];
            double w = Math.Max(bw[i], 1e-6), h = Math.Max(bh[i], 1e-6);
            double wanted = Math.Min(fill * fr.VisibleHeight / h, fill * fr.VisibleWidth / w);
            if (wanted > zoomCap * FollowTuning.CapLimitedFactor)
                capLimited = true;
            tz[i] = Math.Clamp(Math.Log(Math.Max(wanted, 1.0)), 0, lnCap);
        }

        // 4. Lost points.
        FillLost(t, known, tx, ty, tz, options.OnLost);

        // 5. Dead zone.
        var ix = new double[n];
        var iy = new double[n];
        var iz = new double[n];
        DeadZone(fr, tx, ty, tz, ix, iy, iz);

        // 6. Zero-phase smoothing, then speed limits.
        double tau = FollowTuning.TimeConstantSec(options.Smoothness);
        ZeroPhase(ix, t, tau);
        ZeroPhase(iy, t, tau);
        ZeroPhase(iz, t, tau);
        double maxZoomRate = FollowTuning.MaxZoomLnPerSec(options.Smoothness);
        double maxPan = FollowTuning.MaxPanWindowsPerSec(options.Smoothness);
        Slew(iz, i => maxZoomRate * (t[i] - t[i - 1]));
        for (int i = 0; i < n; i++)
            iz[i] = Math.Clamp(iz[i], 0, lnCap);
        Slew(ix, i => maxPan * fr.VisibleWidth / Math.Exp(Math.Max(iz[i], iz[i - 1])) * (t[i] - t[i - 1]));
        Slew(iy, i => maxPan * fr.VisibleHeight / Math.Exp(Math.Max(iz[i], iz[i - 1])) * (t[i] - t[i - 1]));

        // 7. Containment.
        var z = new double[n];
        int clampCount = 0;
        for (int i = 0; i < n; i++)
        {
            z[i] = Math.Clamp(Math.Exp(iz[i]), 1.0, zoomCap);
            double halfW = 0.5 * fr.VisibleWidth / z[i], halfH = 0.5 * fr.VisibleHeight / z[i];
            double nx = Math.Clamp(ix[i], halfW, 1 - halfW);
            double ny = Math.Clamp(iy[i], halfH, 1 - halfH);
            if (Math.Abs(nx - ix[i]) > 1e-9 || Math.Abs(ny - iy[i]) > 1e-9)
                clampCount++;
            ix[i] = nx;
            iy[i] = ny;
        }

        // 7b. Keep the subject in the picture. Smoothing, the dead zone and the pan-speed cap all let the camera
        // trail a fast subject; where the subject's box fits the visible window, the window is moved just enough
        // to hold it (with a small margin), within the pan-speed cap; a subject larger than the window keeps the
        // smoothed path. Lost points
        // keep the path above. The frame bound still wins: a window never leaves the source.
        var loX = new double[n];
        var hiX = new double[n];
        var loY = new double[n];
        var hiY = new double[n];
        for (int i = 0; i < n; i++)
        {
            double halfW = 0.5 * fr.VisibleWidth / z[i], halfH = 0.5 * fr.VisibleHeight / z[i];
            (loX[i], hiX[i]) = (halfW, 1 - halfW);
            (loY[i], hiY[i]) = (halfH, 1 - halfH);
            if (!known[i])
                continue;
            (double lx, double hx) = KeepInside(cx[i], bw[i], halfW);
            (double ly, double hy) = KeepInside(cy[i], bh[i], halfH);
            (loX[i], hiX[i]) = (Math.Max(halfW, Math.Min(lx, 1 - halfW)), Math.Min(1 - halfW, Math.Max(hx, halfW)));
            (loY[i], hiY[i]) = (Math.Max(halfH, Math.Min(ly, 1 - halfH)), Math.Min(1 - halfH, Math.Max(hy, halfH)));
            if (hiX[i] < loX[i])
                hiX[i] = loX[i];
            if (hiY[i] < loY[i])
                hiY[i] = loY[i];
            ix[i] = Math.Clamp(ix[i], loX[i], hiX[i]);
            iy[i] = Math.Clamp(iy[i], loY[i], hiY[i]);
        }

        // The pan-speed cap still holds: a subject faster than it (or a jittering box) is followed as closely as
        // the cap allows, never with a jump. The forward-and-backward slew lets the camera start early. It uses
        // KeepInsideSpeedShare of the cap, leaving headroom for the key decimation's linear chords.
        double keepPan = maxPan * FollowTuning.KeepInsideSpeedShare;
        Slew(ix, i => keepPan * fr.VisibleWidth / Math.Max(z[i], z[i - 1]) * (t[i] - t[i - 1]));
        Slew(iy, i => keepPan * fr.VisibleHeight / Math.Max(z[i], z[i - 1]) * (t[i] - t[i - 1]));
        for (int i = 0; i < n; i++)
        {
            double halfW = 0.5 * fr.VisibleWidth / z[i], halfH = 0.5 * fr.VisibleHeight / z[i];
            ix[i] = Math.Clamp(ix[i], halfW, 1 - halfW);
            iy[i] = Math.Clamp(iy[i], halfH, 1 - halfH);
            loX[i] = Math.Min(loX[i], ix[i]);
            hiX[i] = Math.Max(hiX[i], ix[i]);
            loY[i] = Math.Min(loY[i], iy[i]);
            hiY[i] = Math.Max(hiY[i], iy[i]);
        }

        // 8. Decimation (a key is also added wherever the straight line between keys would let the subject out).
        int maxKeys = options.MaxKeys;
        List<int> kept = Decimate(fr, t, ix, iy, z, maxKeys, FollowTuning.DecimationStartTolerance, loX, hiX, loY, hiY);

        // 9. Round (zoom first, then centre inside the rounded zoom's inward-rounded bounds).
        double zMaxRounded = Math.Floor(zoomCap * 1e5) / 1e5;
        var keys = new List<CameraKey>(kept.Count);
        foreach (int i in kept)
        {
            double zr = Math.Clamp(Round(z[i]), 1.0, zMaxRounded);
            double halfW = 0.5 * fr.VisibleWidth / zr, halfH = 0.5 * fr.VisibleHeight / zr;
            double time = i == 0 ? Round(fromSec) : i == n - 1 ? Round(toSec) : Round(t[i]);
            keys.Add(new CameraKey(time, ClampInward(ix[i], halfW), ClampInward(iy[i], halfH), zr));
        }

        double maxErr = MaxError(fr, t, ix, iy, z, keys);
        return new CameraPathResult(keys, zoomCap, capLimited, lostSpans, clampCount, Round(maxErr));
    }

    private static void Validate(IReadOnlyList<SubjectSample> samples, double fromSec, double toSec, FollowOptions o)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(o);
        if (o.Framing is null)
            throw new ArgumentException("Framing is required.");
        if (!double.IsFinite(fromSec) || !double.IsFinite(toSec) || !double.IsFinite(o.MaxZoomLimit))
            throw new ArgumentException("Times and limits must be finite numbers.");
        if (fromSec < 0)
            throw new ArgumentException("fromSec must not be negative.");
        if (toSec <= fromSec)
            throw new ArgumentException("toSec must be after fromSec.");
        if (toSec - fromSec > MaxRangeSeconds)
            throw new ArgumentException($"A follow covers at most {MaxRangeSeconds} seconds.");
        if (samples is not null && samples.Count > MaxSamples)
            throw new ArgumentException($"At most {MaxSamples} subject samples.");
        if (o.MaxKeys < 2)
            throw new ArgumentException("MaxKeys must be at least 2.");
        if (o.SourceHeightPx <= 0 || o.OutputHeightPx <= 0)
            throw new ArgumentException("SourceHeightPx and OutputHeightPx must be positive.");
        if (!double.IsFinite(o.Framing.VisibleWidth) || !double.IsFinite(o.Framing.VisibleHeight)
            || o.Framing.VisibleWidth < 0.01 || o.Framing.VisibleWidth > 1 || o.Framing.VisibleHeight < 0.01 || o.Framing.VisibleHeight > 1)
            throw new ArgumentException("Framing must be within [0.01, 1] on both axes.");
        foreach (SubjectSample s in samples)
        {
            if (s is null || !double.IsFinite(s.TimeSec) || !double.IsFinite(s.X) || !double.IsFinite(s.Y)
                || !double.IsFinite(s.Width) || !double.IsFinite(s.Height))
                throw new ArgumentException("Subject samples must contain finite numbers.");
        }
    }

    private static double ComputeZoomCap(FollowOptions o)
    {
        double sharp = Math.Max(1.0, o.SourceHeightPx * o.Framing.VisibleHeight / o.OutputHeightPx);
        double cap = o.AllowSoftZoom ? Math.Max(sharp, FollowTuning.SoftZoomCap) : sharp;
        cap = Math.Min(cap, Math.Min(VideoReframe.MaxZoom, o.MaxZoomLimit));
        return Math.Max(cap, 1.0);
    }

    /// <summary>
    /// The longest range one call may cover: 4 h, above every source-length cap the gates allow, so the
    /// 10 Hz grid stays bounded (144,001 points) whatever a caller passes. Callers clamp first.
    /// </summary>
    public const double MaxRangeSeconds = 4 * 3600;

    /// <summary>The most subject samples one call accepts (a 4 h track at 30 Hz is 432,000).</summary>
    public const int MaxSamples = 500_000;

    private static double[] BuildGrid(double fromSec, double toSec)
    {
        double step = 1.0 / FollowTuning.GridHz;
        var t = new List<double> { fromSec };
        for (int i = 1; fromSec + i * step < toSec - 1e-4; i++)
            t.Add(fromSec + i * step);
        t.Add(toSec);
        return [.. t];
    }

    private static List<SubjectSample> PreparePresent(IReadOnlyList<SubjectSample> samples)
    {
        var byTime = new SortedDictionary<double, SubjectSample>();
        foreach (SubjectSample s in samples)
        {
            if (!s.Present)
                continue;
            double x = Math.Clamp(s.X, 0, 1), y = Math.Clamp(s.Y, 0, 1);
            double w = Math.Clamp(s.Width, 0, 1 - x), h = Math.Clamp(s.Height, 0, 1 - y);
            byTime[s.TimeSec] = new SubjectSample(s.TimeSec, x, y, w, h, true);
        }

        return [.. byTime.Values];
    }

    private static void Resample(List<SubjectSample> p, double[] t, double[] cx, double[] cy, double[] bw, double[] bh, bool[] known)
    {
        const double eps = 1e-9;
        int j = 0;
        for (int i = 0; i < t.Length; i++)
        {
            while (j < p.Count && p[j].TimeSec < t[i] - eps)
                j++;
            if (j == p.Count)
                continue; // after the last present sample
            SubjectSample b = p[j];
            if (Math.Abs(b.TimeSec - t[i]) <= eps)
            {
                Set(i, b.X + b.Width / 2, b.Y + b.Height / 2, b.Width, b.Height);
                continue;
            }

            if (j == 0)
                continue; // before the first present sample
            SubjectSample a = p[j - 1];
            if (b.TimeSec - a.TimeSec >= FollowTuning.BridgeSec - eps)
                continue;
            double u = (t[i] - a.TimeSec) / (b.TimeSec - a.TimeSec);
            Set(i,
                Lerp(a.X + a.Width / 2, b.X + b.Width / 2, u),
                Lerp(a.Y + a.Height / 2, b.Y + b.Height / 2, u),
                Lerp(a.Width, b.Width, u),
                Lerp(a.Height, b.Height, u));
        }

        void Set(int i, double x, double y, double w, double h)
        {
            cx[i] = x;
            cy[i] = y;
            bw[i] = w;
            bh[i] = h;
            known[i] = true;
        }
    }

    private static List<FollowLostSpan> LostSpans(double[] t, bool[] known)
    {
        var spans = new List<FollowLostSpan>();
        int n = t.Length;
        for (int i = 0; i < n; i++)
        {
            if (known[i])
                continue;
            int j = i;
            while (j + 1 < n && !known[j + 1])
                j++;
            double from = i > 0 ? t[i - 1] : t[0];
            double to = j + 1 < n ? t[j + 1] : t[n - 1];
            if (to - from >= FollowTuning.LostSpanMinSec - 1e-9)
                spans.Add(new FollowLostSpan(Round(from), Round(to)));
            i = j;
        }

        return spans;
    }

    private static void FillLost(double[] t, bool[] known, double[] tx, double[] ty, double[] tz, FollowOnLost onLost)
    {
        int n = t.Length;
        for (int i = 0; i < n; i++)
        {
            if (known[i])
                continue;
            int j = i;
            while (j + 1 < n && !known[j + 1])
                j++;
            bool leading = i == 0;
            int src = leading ? j + 1 : i - 1; // run end is < n when leading, since some point is known
            for (int k = i; k <= j; k++)
            {
                if (onLost == FollowOnLost.Hold)
                {
                    tx[k] = tx[src];
                    ty[k] = ty[src];
                    tz[k] = tz[src];
                }
                else if (leading)
                {
                    tx[k] = 0.5;
                    ty[k] = 0.5;
                    tz[k] = 0;
                }
                else
                {
                    double e = t[k] - t[i - 1];
                    double f = Math.Clamp((e - FollowTuning.WidenHoldSec) / FollowTuning.WidenRampSec, 0, 1);
                    tx[k] = Lerp(tx[src], 0.5, f);
                    ty[k] = Lerp(ty[src], 0.5, f);
                    tz[k] = Lerp(tz[src], 0, f);
                }
            }

            i = j;
        }
    }

    private static void DeadZone(FollowFraming fr, double[] tx, double[] ty, double[] tz, double[] ix, double[] iy, double[] iz)
    {
        double cx = tx[0], cy = ty[0], cz = tz[0];
        for (int i = 0; i < tx.Length; i++)
        {
            if (Math.Abs(tz[i] - cz) > FollowTuning.ZoomDeadZoneLn || (tz[i] <= 0 && cz > 0))
                cz = tz[i];
            double zoom = Math.Exp(cz);
            double hw = FollowTuning.DeadZoneFraction * fr.VisibleWidth / zoom / 2;
            double hh = FollowTuning.DeadZoneFraction * fr.VisibleHeight / zoom / 2;
            if (tx[i] > cx + hw)
                cx = tx[i] - hw;
            else if (tx[i] < cx - hw)
                cx = tx[i] + hw;
            if (ty[i] > cy + hh)
                cy = ty[i] - hh;
            else if (ty[i] < cy - hh)
                cy = ty[i] + hh;
            ix[i] = cx;
            iy[i] = cy;
            iz[i] = cz;
        }
    }

    private static void ZeroPhase(double[] v, double[] t, double tau)
    {
        for (int i = 1; i < v.Length; i++)
        {
            double a = 1 - Math.Exp(-(t[i] - t[i - 1]) / tau);
            v[i] = v[i - 1] + a * (v[i] - v[i - 1]);
        }

        for (int i = v.Length - 2; i >= 0; i--)
        {
            double a = 1 - Math.Exp(-(t[i + 1] - t[i]) / tau);
            v[i] = v[i + 1] + a * (v[i] - v[i + 1]);
        }
    }

    /// <summary>Forward then backward limit of the step between point i-1 and i to <paramref name="limit"/>(i).</summary>
    private static void Slew(double[] v, Func<int, double> limit)
    {
        for (int i = 1; i < v.Length; i++)
            v[i] = v[i - 1] + Math.Clamp(v[i] - v[i - 1], -limit(i), limit(i));
        for (int i = v.Length - 2; i >= 0; i--)
            v[i] = v[i + 1] + Math.Clamp(v[i] - v[i + 1], -limit(i + 1), limit(i + 1));
    }

    /// <summary>
    /// One greedy top-down pass: start with both ends and repeatedly split the segment with the
    /// worst point error at that point, until MaxKeys keys or the worst error is within tolerance.
    /// </summary>
    private static List<int> Decimate(
        FollowFraming fr, double[] t, double[] x, double[] y, double[] z, int maxKeys, double tol,
        double[] loX, double[] hiX, double[] loY, double[] hiY)
    {
        int n = t.Length;
        var kept = new List<int> { 0, n - 1 };
        var heap = new PriorityQueue<(int A, int B, int At), double>();
        Enqueue(0, n - 1);
        while (kept.Count < maxKeys && heap.TryDequeue(out (int A, int B, int At) seg, out double negWorst))
        {
            if (-negWorst <= tol)
                break;
            kept.Add(seg.At);
            Enqueue(seg.A, seg.At);
            Enqueue(seg.At, seg.B);
        }

        kept.Sort();
        return kept;

        void Enqueue(int a, int b)
        {
            double worst = -1;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double u = (t[i] - t[a]) / (t[b] - t[a]);
                double ex = Lerp(x[a], x[b], u), ey = Lerp(y[a], y[b], u);
                double e = PointError(fr, x[i], y[i], z[i], ex, ey, Lerp(z[a], z[b], u));
                // Letting the subject out of the window outranks any smoothness error. The chord may use up the
                // KeepInsideMargin spare room (so keys are not spent on it), never more.
                double outsideX = Math.Max(loX[i] - ex, ex - hiX[i]) / (fr.VisibleWidth / z[i]);
                double outsideY = Math.Max(loY[i] - ey, ey - hiY[i]) / (fr.VisibleHeight / z[i]);
                double outside = Math.Max(outsideX, outsideY);
                if (outside > FollowTuning.KeepInsideMargin)
                    e = Math.Max(e, 1 + outside);
                if (e > worst)
                {
                    worst = e;
                    at = i;
                }
            }

            if (at >= 0)
                heap.Enqueue((a, b, at), -worst);
        }
    }

    private static double PointError(FollowFraming fr, double x, double y, double z, double ex, double ey, double ez)
    {
        double dx = Math.Abs(x - ex) / (fr.VisibleWidth / z);
        double dy = Math.Abs(y - ey) / (fr.VisibleHeight / z);
        double dz = Math.Abs(Math.Log(z) - Math.Log(Math.Max(ez, 1e-9)));
        return Math.Max(dx, Math.Max(dy, dz));
    }

    private static double MaxError(FollowFraming fr, double[] t, double[] x, double[] y, double[] z, List<CameraKey> keys)
    {
        double max = 0;
        int k = 0;
        for (int i = 0; i < t.Length; i++)
        {
            while (k < keys.Count - 2 && keys[k + 1].TimeSec < t[i])
                k++;
            CameraKey a = keys[k], b = keys[k + 1];
            double u = Math.Clamp((t[i] - a.TimeSec) / (b.TimeSec - a.TimeSec), 0, 1);
            max = Math.Max(max, PointError(fr, x[i], y[i], z[i], Lerp(a.CenterX, b.CenterX, u), Lerp(a.CenterY, b.CenterY, u), Lerp(a.Zoom, b.Zoom, u)));
        }

        return max;
    }

    /// <summary>
    /// The window centres (along one axis) that keep a box of centre <paramref name="c"/> and size
    /// <paramref name="size"/> inside a window of half-size <paramref name="half"/>, with
    /// <see cref="FollowTuning.KeepInsideMargin"/> of the window spare on each side; unbounded when it does not fit
    /// (a subject larger than the window keeps the smoothed path rather than a jittery hard centre).
    /// </summary>
    private static (double Lo, double Hi) KeepInside(double c, double size, double half)
    {
        double margin = FollowTuning.KeepInsideMargin * 2 * half;
        double lo = c + size / 2 - half + margin;
        double hi = c - size / 2 + half - margin;
        return lo <= hi ? (lo, hi) : (double.NegativeInfinity, double.PositiveInfinity);
    }

    private static double ClampInward(double c, double half)
    {
        double lo = Math.Ceiling(half * 1e5) / 1e5;
        double hi = Math.Floor((1 - half) * 1e5) / 1e5;
        return lo > hi ? 0.5 : Math.Clamp(Round(c), lo, hi);
    }

    private static double Lerp(double a, double b, double u) => a + (b - a) * u;

    private static double Round(double v) => Math.Round(v, FollowTuning.Decimals);
}
