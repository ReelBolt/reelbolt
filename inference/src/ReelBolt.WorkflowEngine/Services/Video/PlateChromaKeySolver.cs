namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// The <c>colorkey</c> parameters chosen for one tracked plate, plus the agreement score that won
/// them. <see cref="Score"/> is a soft intersection-over-union against the tracker's own detection
/// of that plate, in 0..1; the compile step refuses to matte below a floor rather than compositing
/// through a key it cannot vouch for.
/// </summary>
public readonly record struct PlateChromaKey(
    byte R, byte G, byte B, double Similarity, double Blend, double Score)
{
    public static readonly PlateChromaKey None = default;

    public bool IsUsable => Score > 0;
}

/// <summary>
/// Chooses the chroma-key parameters that make ffmpeg's <c>colorkey</c> reproduce, at full encode
/// resolution, the same plate the tracker found on its coarse grid — the SHAPE half of tracked
/// compositing (see docs/video-editing.md "Silhouette matting").
///
/// <para>
/// <b>Why this exists.</b> Warping decides WHERE the inserted content goes. It says nothing about
/// WHICH PIXELS it covers, and every version of this feature before it composited a filled
/// quadrilateral — so a phone's rounded bezel corners and camera notch were painted over, and a
/// hand crossing the laptop's screen was painted over too. A real plate's silhouette is not a
/// quadrilateral and cannot be made into one by bending its edges; it has to be cut out per frame.
/// The per-frame cutout the compositor needs already exists in the source: the plate is a known
/// colour, so keying the base frame gives a pixel-exact, full-resolution, anti-aliased matte for
/// free — corners, notch, occluding hands, and motion blur on those hands all included.
/// </para>
/// <para>
/// <b>Why the parameters are searched and not configured.</b> A key needs a colour and a
/// tolerance, and no fixed pair works: on the project's own footage the phone's plate measures RGB
/// (0,232,0) — bright and saturated — while the laptop's measures (31,63,10), dark and nearly
/// neutral. A tolerance that keys one keys the entire room in the other, and <c>chromakey</c>'s
/// luma-independent test fails outright on the dark plate for the same reason. Rather than expose
/// a knob nobody can set correctly without looking at frames, this solver grid-searches colour ×
/// similarity × blend and scores each candidate against ground truth it already has — the plate
/// mask <c>ChromaQuadTracker</c> segmented for that very frame. The best-agreeing parameters win,
/// per plate, with no clip-specific constant anywhere.
/// </para>
/// <para>
/// <b>Scoring.</b> Candidates are judged only inside the plate's own dilated bounding box, because
/// that is the only place the matte decides anything: the warped quad/mesh mask already excludes
/// everything else, so a candidate that keys green foliage across the room is not penalised for
/// it, while one that eats into the bezel — the failure that matters — is. Agreement is a SOFT
/// IoU (sum of minima over sum of maxima), so a candidate whose edge ramp straddles the true
/// boundary scores better than one that steps across it in the wrong place, which is what makes
/// the search prefer a genuinely anti-aliased matte.
/// </para>
/// <para>
/// The distance metric replicates ffmpeg's <c>vf_colorkey</c> exactly —
/// <c>diff = sqrt((dr^2+dg^2+db^2)/3)/255</c>, alpha 0 at <c>diff &lt;= similarity</c> ramping to
/// 1 by <c>similarity + blend</c> — verified against a real ffmpeg run over a synthesized
/// distance ramp, because a score computed against a different formula than the one that will run
/// would be worthless.
/// </para>
/// </summary>
public static class PlateChromaKeySolver
{
    /// <summary>Candidate colours drawn from the plate's own pixels, as percentiles of its luma distribution — robust to the fringe and to a few occluded pixels.</summary>
    private static readonly double[] ColourPercentiles = [0.25, 0.40, 0.50, 0.60, 0.75];

    /// <summary>Similarity sweep. Wide enough to cover a saturated plate against a plain wall and a dim one against a dim room, fine enough that the winner is not a coarse compromise.</summary>
    private const int SimilaritySteps = 40;
    private const double MinSimilarity = 0.010;
    private const double MaxSimilarity = 0.460;

    /// <summary>Blend (edge ramp) sweep, as a fraction of the chosen similarity so the ramp scales with the key's own tolerance.</summary>
    private static readonly double[] BlendFractions = [0.0, 0.15, 0.35, 0.70];

    /// <summary>How far the plate's bounding box is dilated to form the scoring region, as a fraction of its size. This band is the bezel — where a too-greedy key does its visible damage.</summary>
    private const double RoiDilation = 0.25;

    /// <summary>
    /// A key must agree with the tracker at least this well to be used at all; below it the compile
    /// step composites with the plain quad mask, exactly as before matting existed.
    ///
    /// <para>
    /// Raised from 0.75 after the alpha launch film shipped a visibly broken insert through it. The
    /// two real plates in that project score far apart — the desk monitor, shot against a wall,
    /// agrees at 0.996 and mattes beautifully; the phone, shot outdoors against green foliage,
    /// agrees at 0.760 and its composite came out washed green with ragged patches of keyed leaf
    /// around the handset. 0.75 admitted the second one by sixteen thousandths.
    /// </para>
    /// <para>
    /// The scorer was not wrong and did not need changing — this was checked before touching it.
    /// <see cref="RoiDilation"/> already widens the scoring region 25% past the plate, comfortably
    /// outside the 10% a matted insert overscans its quad by, so the foliage that ends up under
    /// the composite IS inside the region agreement is measured over, and 0.760 is an honest
    /// report that roughly a quarter of that region keys wrong. Rendering the phone's matte
    /// confirmed it directly: the handset keys perfectly, notch included, while ragged patches of
    /// leaf and grass key white alongside it. The defect was accepting a quarter-wrong matte as
    /// good enough to carry a composite's alpha, not failing to notice it was wrong.
    /// </para>
    /// <para>
    /// Rejecting a key is a SAFE degradation, not a loss of the feature: the insert falls back to
    /// the plain quad pin that shipped before matting existed, at the cautious configured overscan
    /// rather than 10%. What it gives up is occlusion — a finger crossing the plate edge gets
    /// painted over — which is plainly better than a green-washed panel. Composited locally against
    /// the real phone plate, the no-matte pin measured RGB (15,13,19): clean and dark.
    /// </para>
    /// </summary>
    public const double MinUsableScore = 0.90;

    /// <summary>
    /// One frame of evidence. <paramref name="PlatePixels"/> is the tracker's grown component —
    /// the ground truth a candidate is scored against. <paramref name="CoreMask"/> and
    /// <paramref name="GrowMask"/> are its two detection thresholds, whose DIFFERENCE inside the
    /// plate's own quad is the partially-contaminated blend band the spill term is about.
    /// <paramref name="Quad"/> is that quad, in grid pixel coordinates, TL/TR/BL/BR.
    /// </summary>
    public sealed record Sample(
        byte[] PixelData, int FrameOffset, int Width, int Height, IReadOnlyList<int> PlatePixels,
        bool[]? CoreMask = null, bool[]? GrowMask = null,
        (double X, double Y)[]? Quad = null);

    /// <summary>
    /// How heavily leaving contaminated pixels uncovered counts against a candidate, relative to
    /// its plain agreement score. See <see cref="SpillPenalty"/> for what this buys and why plain
    /// agreement cannot buy it.
    /// </summary>
    private const double SpillPenaltyWeight = 0.6;

    /// <summary>Lexicographic tie-break: any candidate clearing <see cref="MinUsableScore"/> outranks any candidate that does not, whatever their spill.</summary>
    private const double UsableBonus = 100.0;

    /// <summary>
    /// Grid-searches the key for one plate. Returns <see cref="PlateChromaKey.None"/> when there
    /// is nothing to learn from (no samples, no plate pixels) or when the best candidate still
    /// disagrees with the tracker — never a guess.
    /// </summary>
    public static PlateChromaKey Solve(IReadOnlyList<Sample> samples)
    {
        if (samples.Count == 0)
            return PlateChromaKey.None;

        List<(byte R, byte G, byte B)> candidates = CandidateColours(samples);
        if (candidates.Count == 0)
            return PlateChromaKey.None;

        // Per sample, the pixels the score is computed over (the plate's dilated bounding box),
        // whether each is plate, and whether each is a contaminated blend pixel. Built once and
        // reused across every candidate.
        var scored = new List<RoiSamples>(samples.Count);
        foreach (Sample sample in samples)
        {
            RoiSamples roi = BuildRoi(sample);
            if (roi.Offsets.Length > 0)
                scored.Add(roi);
        }

        if (scored.Count == 0)
            return PlateChromaKey.None;

        // Selection and reporting are deliberately different numbers. The spill term STEERS the
        // choice — it is what widens a key just far enough to absorb an occluder's blend rim —
        // but it must not be what the usability gate reads, because a plate with an inherently
        // wide fringe would then have its matte switched off for having a hard problem rather
        // than for being badly solved. What gets stored is plain agreement: how faithfully this
        // key reproduces the tracker's own plate.
        PlateChromaKey best = PlateChromaKey.None;
        double bestSelection = double.NegativeInfinity;
        foreach ((byte r, byte g, byte b) in candidates)
        {
            // The per-pixel distance depends only on the candidate COLOUR, so it is computed once
            // and the similarity/blend sweep below is pure arithmetic over it. That is what keeps
            // a few hundred candidates cheap.
            var diffs = new double[scored.Count][];
            for (int s = 0; s < scored.Count; s++)
            {
                int[] offsets = scored[s].Offsets;
                var d = new double[offsets.Length];
                byte[] pixels = samples[s].PixelData;
                for (int i = 0; i < offsets.Length; i++)
                    d[i] = Distance(pixels, offsets[i], r, g, b);
                diffs[s] = d;
            }

            for (int step = 0; step < SimilaritySteps; step++)
            {
                double similarity = MinSimilarity +
                    (MaxSimilarity - MinSimilarity) * step / (SimilaritySteps - 1.0);

                foreach (double blendFraction in BlendFractions)
                {
                    double blend = similarity * blendFraction;
                    double agreement = SoftIoU(diffs, scored, similarity, blend);
                    double spill = SpillPenalty(diffs, scored, similarity, blend);
                    // Candidates that clear the usability floor are always preferred over ones
                    // that do not, and only among those does the spill term get to choose. Left
                    // as a plain weighted sum, a heavy spill penalty could hand the win to a key
                    // whose agreement had fallen under the floor — which reads downstream as "no
                    // matte at all", the worst of both outcomes.
                    double selection = agreement - SpillPenaltyWeight * spill
                        + (agreement >= MinUsableScore ? UsableBonus : 0);
                    if (selection > bestSelection)
                    {
                        bestSelection = selection;
                        best = new PlateChromaKey(r, g, b, Round(similarity), Round(blend), Math.Round(agreement, 4));
                    }
                }
            }
        }

        return best;
    }

    private static double Round(double v) => Math.Round(v, 4);

    /// <summary>ffmpeg <c>vf_colorkey</c>'s distance, verified empirically against a real run.</summary>
    internal static double Distance(byte[] pixels, int offset, byte r, byte g, byte b)
    {
        double dr = pixels[offset] - r;
        double dg = pixels[offset + 1] - g;
        double db = pixels[offset + 2] - b;
        return Math.Sqrt((dr * dr + dg * dg + db * db) / 3.0) / 255.0;
    }

    /// <summary>
    /// ffmpeg's alpha for a given distance, inverted into COVERAGE (1 = this pixel is plate and
    /// the insert shows here). <c>colorkey</c> makes matching pixels transparent, so coverage is
    /// <c>1 - alpha</c>.
    /// </summary>
    internal static double Coverage(double diff, double similarity, double blend)
    {
        if (blend <= 0.0001)
            return diff > similarity ? 0.0 : 1.0;

        return 1.0 - Math.Clamp((diff - similarity) / blend, 0.0, 1.0);
    }

    /// <summary>
    /// How much of the plate's own contaminated blend band this candidate leaves UNCOVERED, in
    /// 0..1 — and the reason the search needs a second term at all.
    ///
    /// <para>
    /// Where something crosses in front of a plate, the pixels along its edge are a physical
    /// blend of that object and the plate behind it: they carry real green. A key tight enough to
    /// match the tracker's segmentation leaves them in the base picture, so the composite shows a
    /// green rim tracing the occluder — visible on the project's own laptop clip as a halo around
    /// the hand. Plain agreement cannot fix this, because that rim is a handful of pixels against
    /// a whole plate and rounds away in the IoU. Normalising the miss over the BAND ITSELF gives
    /// those pixels weight proportional to the problem they cause, so the search widens the key
    /// far enough to absorb the blend — and no further, since swallowing the occluder's solid
    /// interior costs agreement.
    /// </para>
    /// <para>
    /// The band is the difference between the tracker's own two detection thresholds — pixels
    /// green enough for the permissive grow test but not the strict core test — restricted to
    /// inside the tracked quad, so green scenery behind the plate can never drive the key wider.
    /// </para>
    /// </summary>
    private static double SpillPenalty(
        double[][] diffs, List<RoiSamples> scored, double similarity, double blend)
    {
        double missed = 0, total = 0;
        for (int s = 0; s < scored.Count; s++)
        {
            bool[] fringe = scored[s].Fringe;
            double[] d = diffs[s];
            for (int i = 0; i < d.Length; i++)
            {
                if (!fringe[i])
                    continue;

                total += 1;
                missed += 1 - Coverage(d[i], similarity, blend);
            }
        }

        return total > 0 ? missed / total : 0;
    }

    private static double SoftIoU(
        double[][] diffs, List<RoiSamples> scored, double similarity, double blend)
    {
        double intersection = 0, union = 0;
        for (int s = 0; s < scored.Count; s++)
        {
            bool[] truth = scored[s].Truth;
            double[] d = diffs[s];
            for (int i = 0; i < d.Length; i++)
            {
                double coverage = Coverage(d[i], similarity, blend);
                double t = truth[i] ? 1.0 : 0.0;
                intersection += Math.Min(coverage, t);
                union += Math.Max(coverage, t);
            }
        }

        return union > 0 ? intersection / union : 0;
    }

    /// <summary>
    /// The plate's pixels' colours at several luma percentiles. Percentiles rather than a mean
    /// because a plate routinely carries a bright specular band and a dark fringe, and the mean of
    /// those is a colour that appears nowhere on it.
    /// </summary>
    internal static List<(byte R, byte G, byte B)> CandidateColours(IReadOnlyList<Sample> samples)
    {
        var plate = new List<(int Luma, byte R, byte G, byte B)>();
        foreach (Sample sample in samples)
        {
            foreach (int p in sample.PlatePixels)
            {
                int o = sample.FrameOffset + p * 3;
                if (o + 2 >= sample.PixelData.Length)
                    continue;

                byte r = sample.PixelData[o], g = sample.PixelData[o + 1], b = sample.PixelData[o + 2];
                plate.Add((r * 299 + g * 587 + b * 114, r, g, b));
            }
        }

        if (plate.Count == 0)
            return [];

        plate.Sort((a, b) => a.Luma.CompareTo(b.Luma));

        var colours = new List<(byte, byte, byte)>(ColourPercentiles.Length);
        foreach (double q in ColourPercentiles)
        {
            (int _, byte r, byte g, byte b) = plate[Math.Clamp((int)(plate.Count * q), 0, plate.Count - 1)];
            if (!colours.Contains((r, g, b)))
                colours.Add((r, g, b));
        }

        return colours;
    }

    /// <summary>
    /// The pixels a candidate is scored over: the plate's bounding box dilated by
    /// <see cref="RoiDilation"/>, which is exactly the plate plus the bezel band around it.
    /// </summary>
    /// <summary>The scoring region for one sample: which pixels, whether each is plate, and whether each is contaminated blend.</summary>
    private sealed record RoiSamples(int[] Offsets, bool[] Truth, bool[] Fringe);

    private static RoiSamples BuildRoi(Sample sample)
    {
        if (sample.PlatePixels.Count == 0)
            return new RoiSamples([], [], []);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        var isPlate = new bool[sample.Width * sample.Height];
        foreach (int p in sample.PlatePixels)
        {
            isPlate[p] = true;
            int x = p % sample.Width, y = p / sample.Width;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        int padX = (int)Math.Ceiling((maxX - minX + 1) * RoiDilation);
        int padY = (int)Math.Ceiling((maxY - minY + 1) * RoiDilation);
        int x0 = Math.Max(0, minX - padX), x1 = Math.Min(sample.Width - 1, maxX + padX);
        int y0 = Math.Max(0, minY - padY), y1 = Math.Min(sample.Height - 1, maxY + padY);

        var offsets = new List<int>((x1 - x0 + 1) * (y1 - y0 + 1));
        var truth = new List<bool>(offsets.Capacity);
        var fringe = new List<bool>(offsets.Capacity);
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int p = y * sample.Width + x;
                offsets.Add(sample.FrameOffset + p * 3);
                truth.Add(isPlate[p]);

                bool contaminated =
                    sample.GrowMask is not null && sample.CoreMask is not null &&
                    sample.GrowMask[p] && !sample.CoreMask[p] &&
                    InsideQuad(sample.Quad, x, y);
                fringe.Add(contaminated);
            }
        }

        return new RoiSamples(offsets.ToArray(), truth.ToArray(), fringe.ToArray());
    }

    /// <summary>
    /// Point-in-quad by consistent winding across the four edges, for the TL/TR/BL/BR corner
    /// order the rest of the insert pipeline uses. A null quad admits everything, which is what
    /// makes the fringe term simply inert for a caller that supplies no geometry.
    /// </summary>
    internal static bool InsideQuad((double X, double Y)[]? quad, double x, double y)
    {
        if (quad is not { Length: 4 })
            return true;

        // Perimeter order, not corner-index order.
        (double X, double Y)[] ring = [quad[0], quad[1], quad[3], quad[2]];
        int positive = 0, negative = 0;
        for (int i = 0; i < 4; i++)
        {
            (double ax, double ay) = ring[i];
            (double bx, double by) = ring[(i + 1) % 4];
            double cross = (bx - ax) * (y - ay) - (by - ay) * (x - ax);
            if (cross > 0) positive++;
            else if (cross < 0) negative++;
        }

        return positive == 0 || negative == 0;
    }
}
