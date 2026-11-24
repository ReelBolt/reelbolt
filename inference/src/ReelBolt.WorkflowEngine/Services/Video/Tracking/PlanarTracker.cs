namespace ReelBolt.WorkflowEngine.Services.Video.Tracking;

/// <summary>A clip's sampled grayscale frames, with a small pyramid cache.</summary>
public sealed class GrayFrames
{
    private readonly byte[] _data;
    private readonly Dictionary<int, ImagePyramid> _cache = new();
    private readonly Queue<int> _order = new();
    private int _cachedLevels;

    public int Width { get; }
    public int Height { get; }
    public int Count { get; }
    public double Fps { get; }

    public GrayFrames(byte[] data, int width, int height, int count, double fps)
    {
        _data = data;
        Width = width;
        Height = height;
        Count = Math.Min(count, width * height > 0 ? data.Length / (width * height) : 0);
        Fps = fps;
    }

    public GrayImage Image(int frame) => GrayImage.FromGray8(_data, frame * Width * Height, Width, Height);

    /// <summary>The frame's pyramid, cached for the handful of frames a tracking pass touches at once.</summary>
    public ImagePyramid Pyramid(int frame, int levels)
    {
        if (levels != _cachedLevels)
        {
            _cache.Clear();
            _order.Clear();
            _cachedLevels = levels;
        }

        if (_cache.TryGetValue(frame, out ImagePyramid? p))
            return p;

        p = new ImagePyramid(Image(frame), levels);
        _cache[frame] = p;
        _order.Enqueue(frame);
        while (_order.Count > 12)
            _cache.Remove(_order.Dequeue());
        return p;
    }
}

/// <summary>Planar tracker knobs. Every value is author config, a tuned value, or a compiled default — never model output.</summary>
public sealed record PlanarTrackerOptions(
    TrackMotionModel Model = TrackMotionModel.Homography,
    int WindowRadius = 10,
    int PyramidLevels = 4,
    int MaxFeatures = 200,
    int MinFeatures = 10,
    /// <summary>Forward-backward error (px) above which a point's flow is discarded.</summary>
    double ForwardBackwardThreshold = 1.0,
    /// <summary>RANSAC inlier threshold (px at tracking resolution).</summary>
    double RansacThreshold = 2.0,
    /// <summary>Appearance (NCC against the seed/recent template) below which a frame counts as lost.</summary>
    double MinAppearance = 0.3,
    /// <summary>Consecutive lost frames tolerated before a track ends.</summary>
    int MaxLostFrames = 3,
    /// <summary>Re-register every frame against the SEED frame (initialized from the chained estimate), which keeps drift from accumulating while the target still resembles its seed.</summary>
    bool RefineAgainstReference = true,
    /// <summary>Largest per-frame area change tolerated before an estimate is rejected as a blunder.</summary>
    double MaxAreaChangePerFrame = 0.35,
    /// <summary>Stop after this many frames in each direction (0 = to the clip's end).</summary>
    int MaxFramesPerDirection = 0,
    /// <summary>
    /// Features are looked for in the quad grown by this fraction about its centroid. The target's
    /// own outline — a plate's border, a sign's corners — sits exactly ON the quad, and those are
    /// the strongest, best-spread corners it has; a strict inside test discards them and leaves
    /// only interior texture (a plate's text), from which the corners are extrapolated. Background
    /// features caught in the margin move differently and RANSAC rejects them.
    /// </summary>
    double DetectionMargin = 0.15);

/// <summary>One frame of a planar track: the quad (TL, TR, BR, BL, tracking-resolution pixels) and how much to trust it.</summary>
public sealed record TrackedQuad(
    int FrameIndex,
    (double X, double Y)[] Corners,
    double Confidence,
    double Appearance,
    int Inliers);

/// <summary>
/// Markerless planar tracker: follows a quadrilateral region — a licence plate, a sign, a screen
/// without a green plate — from a seed frame forward and backward through a clip.
///
/// <para>
/// Per frame: Shi-Tomasi features of the region are flowed with pyramidal Lucas-Kanade and kept
/// only if flowing them back lands within <see cref="PlanarTrackerOptions.ForwardBackwardThreshold"/>
/// of where they started; a RANSAC fit of the chosen <see cref="TrackMotionModel"/> gives the
/// frame's motion. That chained estimate is then used only as the starting guess for registering
/// the SEED frame's features directly against the current frame, so error does not accumulate
/// frame over frame the way pure chaining does (it falls back to the chain when the target has
/// changed too much to register). Features are re-detected inside the region when too few survive,
/// and an appearance check — normalized cross-correlation of the rectified region against its seed
/// and a slowly updated recent template — ends the track when the target is occluded, leaves the
/// frame or changes beyond recognition, instead of letting it slide onto the background.
/// </para>
/// </summary>
public static class PlanarTracker
{
    private const int PatchSide = 24;

    /// <summary>
    /// Tracks <paramref name="seedCorners"/> (TL, TR, BR, BL at tracking resolution) from
    /// <paramref name="seedFrame"/> in both directions. The result is ordered by frame and always
    /// contains the seed frame itself.
    /// </summary>
    public static List<TrackedQuad> Track(
        GrayFrames frames, int seedFrame, (double X, double Y)[] seedCorners, PlanarTrackerOptions options)
    {
        var seed = new TrackedQuad(seedFrame, seedCorners, 1.0, 1.0, 0);
        List<TrackedQuad> backward = TrackDirection(frames, seedFrame, seedCorners, options, -1);
        List<TrackedQuad> forward = TrackDirection(frames, seedFrame, seedCorners, options, +1);
        backward.Reverse();
        var all = new List<TrackedQuad>(backward.Count + 1 + forward.Count);
        all.AddRange(backward);
        all.Add(seed);
        all.AddRange(forward);
        return all;
    }

    /// <summary>One direction from the seed (exclusive). Empty when nothing trackable is in the region.</summary>
    public static List<TrackedQuad> TrackDirection(
        GrayFrames frames, int seedFrame, (double X, double Y)[] seedCorners, PlanarTrackerOptions options, int direction)
    {
        var output = new List<TrackedQuad>();
        if (seedFrame < 0 || seedFrame >= frames.Count)
            return output;

        int levels = Math.Clamp(options.PyramidLevels, 1, 6);
        int win = Math.Clamp(options.WindowRadius, 3, 30);
        ImagePyramid refPyr = frames.Pyramid(seedFrame, levels);
        GrayImage refImage = refPyr.Levels[0];

        double diag = Math.Max(
            Dist(seedCorners[0], seedCorners[2]),
            Dist(seedCorners[1], seedCorners[3]));
        double minDistance = Math.Max(2.0, diag / 30.0);

        (double X, double Y)[] seedRegion = Grow(seedCorners, options.DetectionMargin);
        List<(double X, double Y)> refPts = FeatureDetector.Detect(refImage, seedRegion, options.MaxFeatures, 0.01, minDistance);
        if (refPts.Count < options.MinFeatures)
            refPts = FeatureDetector.Detect(refImage, seedRegion, options.MaxFeatures, 0.001, Math.Max(1.5, minDistance / 2));
        int minimal = Geometry.MinimalSampleSize(options.Model);
        if (refPts.Count < Math.Max(minimal + 2, 4))
            return output;

        float[] seedTemplate = Rectify(refImage, seedCorners);
        float[] recentTemplate = (float[])seedTemplate.Clone();

        Transform2D total = Transform2D.Identity;          // seed coords -> current frame
        var trackedIdx = Enumerable.Range(0, refPts.Count).ToList();
        var trackedPos = new List<(double X, double Y)>(refPts);
        (double X, double Y)[] prevCorners = seedCorners;
        int lost = 0;
        int limit = options.MaxFramesPerDirection > 0 ? options.MaxFramesPerDirection : int.MaxValue;

        for (int f = seedFrame + direction, steps = 1;
             f >= 0 && f < frames.Count && steps <= limit;
             f += direction, steps++)
        {
            ImagePyramid prevPyr = frames.Pyramid(f - direction, levels);
            ImagePyramid curPyr = frames.Pyramid(f, levels);

            // 1. Frame-to-frame flow with a forward-backward consistency check.
            LucasKanade.Flow[] fwd = LucasKanade.Track(prevPyr, curPyr, trackedPos, win);
            var fwdPts = fwd.Select(p => (p.X, p.Y)).ToList();
            LucasKanade.Flow[] bwd = LucasKanade.Track(curPyr, prevPyr, fwdPts, win);

            var stepSrc = new List<(double X, double Y)>();
            var stepDst = new List<(double X, double Y)>();
            var stepIdx = new List<int>();
            double fb2 = options.ForwardBackwardThreshold * options.ForwardBackwardThreshold;
            for (int i = 0; i < trackedPos.Count; i++)
            {
                if (!fwd[i].Ok || !bwd[i].Ok)
                    continue;
                double ex = bwd[i].X - trackedPos[i].X, ey = bwd[i].Y - trackedPos[i].Y;
                if (ex * ex + ey * ey > fb2)
                    continue;
                stepSrc.Add(trackedPos[i]);
                stepDst.Add((fwd[i].X, fwd[i].Y));
                stepIdx.Add(trackedIdx[i]);
            }

            int minInliers = Math.Max(minimal + 1, Math.Min(options.MinFeatures, refPts.Count / 3));
            var step = Geometry.EstimateRansac(stepSrc, stepDst, options.Model, options.RansacThreshold, minInliers, seed: f);
            if (step is null)
            {
                if (++lost > options.MaxLostFrames)
                    break;
                continue;
            }

            Transform2D predicted = step.Value.Transform.After(total);
            Transform2D chosen = predicted;
            int inliers = step.Value.InlierCount;
            double inlierRatio = step.Value.InlierCount / (double)Math.Max(1, trackedPos.Count);
            var nextIdx = new List<int>();
            var nextPos = new List<(double X, double Y)>();
            for (int i = 0; i < stepIdx.Count; i++)
            {
                if (step.Value.Inliers[i])
                {
                    nextIdx.Add(stepIdx[i]);
                    nextPos.Add(stepDst[i]);
                }
            }

            // 2. Drift control: register the SEED's features straight onto this frame, starting
            //    from the chained prediction.
            if (options.RefineAgainstReference)
            {
                var guesses = refPts.Select(p => predicted.Apply(p.X, p.Y)).ToList();
                LucasKanade.Flow[] reg = LucasKanade.Track(refPyr, curPyr, refPts, win, guesses);
                var regSrc = new List<(double X, double Y)>();
                var regDst = new List<(double X, double Y)>();
                var regIdx = new List<int>();
                for (int i = 0; i < refPts.Count; i++)
                {
                    if (!reg[i].Ok)
                        continue;
                    regSrc.Add(refPts[i]);
                    regDst.Add((reg[i].X, reg[i].Y));
                    regIdx.Add(i);
                }

                var registered = Geometry.EstimateRansac(regSrc, regDst, options.Model, options.RansacThreshold, minInliers, seed: f + 7919);
                if (registered is not null &&
                    registered.Value.InlierCount >= Math.Max(minInliers, step.Value.InlierCount / 2) &&
                    MaxCornerShift(registered.Value.Transform, predicted, seedCorners) < Math.Max(3.0, diag * 0.08))
                {
                    chosen = registered.Value.Transform;
                    inliers = registered.Value.InlierCount;
                    inlierRatio = Math.Max(inlierRatio, registered.Value.InlierCount / (double)Math.Max(1, regSrc.Count));
                    nextIdx.Clear();
                    nextPos.Clear();
                    for (int i = 0; i < regIdx.Count; i++)
                    {
                        if (registered.Value.Inliers[i])
                        {
                            nextIdx.Add(regIdx[i]);
                            nextPos.Add(regDst[i]);
                        }
                    }
                }
            }

            (double X, double Y)[] corners = seedCorners.Select(c => chosen.Apply(c.X, c.Y)).ToArray();

            // 3. Sanity: a convex quad whose area did not jump, and whose centre is still roughly in frame.
            double prevArea = Math.Abs(Geometry.Area(prevCorners));
            double area = Math.Abs(Geometry.Area(corners));
            double change = prevArea > 0 ? area / prevArea : 0;
            double cx = corners.Average(c => c.X), cy = corners.Average(c => c.Y);
            bool sane = Geometry.IsConvex(corners) &&
                        change > 1 / (1 + options.MaxAreaChangePerFrame) && change < 1 + options.MaxAreaChangePerFrame &&
                        cx > -frames.Width * 0.25 && cx < frames.Width * 1.25 && cy > -frames.Height * 0.25 && cy < frames.Height * 1.25;
            if (!sane)
            {
                if (++lost > options.MaxLostFrames)
                    break;
                continue;
            }

            // 4. Appearance: is this still the thing we seeded on?
            float[] patch = Rectify(curPyr.Levels[0], corners);
            double appearance = Math.Max(Ncc(patch, seedTemplate), Ncc(patch, recentTemplate));
            if (appearance < options.MinAppearance)
            {
                if (++lost > options.MaxLostFrames)
                    break;
                continue;
            }

            lost = 0;
            total = chosen;
            prevCorners = corners;
            for (int i = 0; i < recentTemplate.Length; i++)
                recentTemplate[i] = 0.9f * recentTemplate[i] + 0.1f * patch[i];

            double confidence = Math.Clamp(inlierRatio, 0, 1) * Math.Clamp((appearance - 0.2) / 0.6, 0, 1);
            output.Add(new TrackedQuad(f, corners, Math.Round(confidence, 4), Math.Round(appearance, 4), inliers));

            // 5. Replenish features when too few survive, mapping the new ones back to seed space
            //    so they can take part in reference registration too.
            if (nextPos.Count < Math.Max(options.MinFeatures, refPts.Count / 2) && Invert(total) is Transform2D inverse)
            {
                List<(double X, double Y)> fresh = FeatureDetector.Detect(
                    curPyr.Levels[0], Grow(corners, options.DetectionMargin), options.MaxFeatures - nextPos.Count, 0.01, minDistance);
                foreach ((double X, double Y) p in fresh)
                {
                    if (nextPos.Any(q => Math.Abs(q.X - p.X) < minDistance && Math.Abs(q.Y - p.Y) < minDistance))
                        continue;
                    refPts.Add(inverse.Apply(p.X, p.Y));
                    nextIdx.Add(refPts.Count - 1);
                    nextPos.Add(p);
                }
            }

            trackedIdx = nextIdx;
            trackedPos = nextPos;
            if (trackedPos.Count < minimal + 1)
                break;
        }

        return output;
    }

    /// <summary>Samples the quad's interior onto a <see cref="PatchSide"/> square — the region as if seen straight on.</summary>
    internal static float[] Rectify(GrayImage image, (double X, double Y)[] corners)
    {
        var square = new (double X, double Y)[] { (0, 0), (1, 0), (1, 1), (0, 1) };
        Transform2D? map = Geometry.Fit(square, corners, TrackMotionModel.Homography);
        var patch = new float[PatchSide * PatchSide];
        if (map is null)
            return patch;

        for (int y = 0; y < PatchSide; y++)
        {
            for (int x = 0; x < PatchSide; x++)
            {
                (double sx, double sy) = map.Value.Apply((x + 0.5) / PatchSide, (y + 0.5) / PatchSide);
                patch[y * PatchSide + x] = image.Sample(sx, sy);
            }
        }

        return patch;
    }

    /// <summary>Zero-mean normalized cross-correlation in -1..1; 0 for a flat patch.</summary>
    internal static double Ncc(float[] a, float[] b)
    {
        double ma = a.Average(), mb = b.Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double x = a[i] - ma, y = b[i] - mb;
            num += x * y;
            da += x * x;
            db += y * y;
        }

        return da < 1e-9 || db < 1e-9 ? 0 : num / Math.Sqrt(da * db);
    }

    internal static Transform2D? Invert(Transform2D h)
    {
        double a = h.H0, b = h.H1, c = h.H2, d = h.H3, e = h.H4, f = h.H5, g = h.H6, k = h.H7, i = h.H8;
        double co0 = e * i - f * k, co1 = -(d * i - f * g), co2 = d * k - e * g;
        double det = a * co0 + b * co1 + c * co2;
        if (Math.Abs(det) < 1e-12)
            return null;
        return new Transform2D(
            co0 / det, -(b * i - c * k) / det, (b * f - c * e) / det,
            co1 / det, (a * i - c * g) / det, -(a * f - c * d) / det,
            co2 / det, -(a * k - b * g) / det, (a * e - b * d) / det);
    }

    /// <summary>The quad scaled by <c>1 + margin</c> about its centroid.</summary>
    internal static (double X, double Y)[] Grow((double X, double Y)[] corners, double margin)
    {
        double cx = corners.Average(c => c.X), cy = corners.Average(c => c.Y);
        double k = 1 + Math.Max(0, margin);
        return corners.Select(c => (cx + (c.X - cx) * k, cy + (c.Y - cy) * k)).ToArray();
    }

    private static double MaxCornerShift(Transform2D a, Transform2D b, (double X, double Y)[] corners) =>
        corners.Max(c => Dist(a.Apply(c.X, c.Y), b.Apply(c.X, c.Y)));

    internal static double Dist((double X, double Y) p, (double X, double Y) q) =>
        Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
}
