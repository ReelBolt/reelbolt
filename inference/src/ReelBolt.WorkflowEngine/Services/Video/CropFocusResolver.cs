using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Where a clip's crop is centred when it is reframed into a canvas of another aspect, as
/// fractions (0..1) of the source frame, and what that position was taken from.
/// </summary>
/// <param name="Pad">
/// True when no crop window can keep at least <see cref="CropFocusResolver.MinKeptFraction"/> of the
/// subject (an end card's headline, a monitor filling a 16:9 frame): the clip is then shown whole
/// and padded per <c>CanvasFill</c> instead of having its subject cut.
/// </param>
public sealed record CropFocus(double X, double Y, string Basis, bool Pad = false)
{
    /// <summary>The plain centre crop — exactly <c>OutputFit = Crop</c>.</summary>
    public static readonly CropFocus Centre = new(0.5, 0.5, "centre");

    public bool IsCentre => !Pad && Math.Abs(X - 0.5) < 1e-9 && Math.Abs(Y - 0.5) < 1e-9;
}

/// <summary>
/// <c>OutputFit = Subject</c>: one crop position per source clip, chosen from what the analysis
/// measured inside the parts of that clip the edit KEEPS. A tracked screen (every insert/object
/// track keyframe in a kept window) wins outright — it is measured every frame and is what a
/// tracked-insert reel is about; otherwise the subject boxes the vision provider located
/// (<c>LocateSubjects</c>), weighted by how long each shot is on screen; otherwise the centre.
/// </summary>
/// <remarks>
/// The crop is static per clip: the window keeps as much of the weighted subject as it can, and among
/// equally good positions sits nearest its weighted centre (see <see cref="Place"/>). When that window
/// keeps less than <see cref="MinKeptFraction"/> of the MAIN object (the one on screen longest), the
/// clip is padded instead (<see cref="CropFocus.Pad"/>) — "keep the subject in shot" taken literally.
/// </remarks>
public static class CropFocusResolver
{
    /// <summary>
    /// The least of the subject (weighted, both axes multiplied) a crop may keep before the clip is
    /// shown whole instead. 0.87: a subject a little wider than the window loses its edges, one
    /// a quarter wider (a headline, a monitor in a 9:16 crop of a 16:9 shot) is not cut.
    /// </summary>
    public const double MinKeptFraction = 0.87;

    /// <param name="keptWindows">Source-timeline windows the edit keeps, per source index.</param>
    /// <param name="sourceMedia">Each source's dimensions.</param>
    /// <param name="canvas">The output canvas.</param>
    public static IReadOnlyDictionary<int, CropFocus> Resolve(
        VideoAnalysisArtifact artifact,
        IReadOnlyList<(int SourceIndex, double Start, double End)> keptWindows,
        Func<int, VideoAnalysisMedia> sourceMedia,
        VideoAnalysisMedia canvas)
    {
        var result = new Dictionary<int, CropFocus>();
        foreach (IGrouping<int, (int SourceIndex, double Start, double End)> group in keptWindows.GroupBy(w => w.SourceIndex))
        {
            var windows = group.Select(w => (w.Start, w.End)).ToList();
            (double visibleW, double visibleH) = VisibleFraction(sourceMedia(group.Key), canvas);

            List<WeightedBox> tracked = TrackedBoxes(artifact, group.Key, windows);
            if (tracked.Count > 0)
            {
                result[group.Key] = Place(tracked, visibleW, visibleH, "tracked");
                continue;
            }

            List<WeightedBox> subjects = SubjectBoxes(artifact, group.Key, windows);
            result[group.Key] = subjects.Count > 0 ? Place(subjects, visibleW, visibleH, "subject") : CropFocus.Centre;
        }

        return result;
    }

    /// <summary>The fraction of the source frame a cover-fit crop into <paramref name="canvas"/> keeps, per axis.</summary>
    internal static (double W, double H) VisibleFraction(VideoAnalysisMedia source, VideoAnalysisMedia canvas)
    {
        if (source.Width <= 0 || source.Height <= 0 || canvas.Width <= 0 || canvas.Height <= 0)
            return (1, 1);
        double scale = Math.Max(canvas.Width / (double)source.Width, canvas.Height / (double)source.Height);
        return (Math.Min(1, canvas.Width / (source.Width * scale)), Math.Min(1, canvas.Height / (source.Height * scale)));
    }

    /// <param name="Object">Which object the box belongs to (a track, or one shot's subject): the pad
    /// decision is about the MAIN object, never about every secondary one fitting too.</param>
    internal readonly record struct WeightedBox(double X0, double Y0, double X1, double Y1, double Weight, string Object = "");

    internal static CropFocus Place(IReadOnlyList<WeightedBox> boxes, double visibleW, double visibleH, string basis)
    {
        double totalWeight = boxes.Sum(b => b.Weight);
        if (totalWeight <= 0)
            return CropFocus.Centre;

        double cx = boxes.Sum(b => (b.X0 + b.X1) / 2 * b.Weight) / totalWeight;
        double cy = boxes.Sum(b => (b.Y0 + b.Y1) / 2 * b.Weight) / totalWeight;
        double x = Axis(boxes.Select(b => (b.X0, b.X1, b.Weight)).ToList(), cx, visibleW);
        double y = Axis(boxes.Select(b => (b.Y0, b.Y1, b.Weight)).ToList(), cy, visibleH);

        // How much of the MAIN object (the one on screen longest) that window keeps.
        IGrouping<string, WeightedBox> main = boxes.GroupBy(b => b.Object).MaxBy(g => g.Sum(b => b.Weight))!;
        double mainWeight = main.Sum(b => b.Weight);
        double kept = main.Sum(b => b.Weight
            * Kept(b.X0, b.X1, x, visibleW)
            * Kept(b.Y0, b.Y1, y, visibleH)) / mainWeight;

        return kept < MinKeptFraction
            ? new CropFocus(0.5, 0.5, basis, Pad: true)
            : new CropFocus(x, y, basis);
    }

    /// <summary>The fraction of <c>[lo, hi]</c> inside a window of width <paramref name="visible"/> centred at <paramref name="centre"/>.</summary>
    private static double Kept(double lo, double hi, double centre, double visible)
    {
        if (visible >= 1 - 1e-9)
            return 1;
        double length = Math.Max(1e-9, hi - lo);
        return Math.Clamp((Math.Min(centre + visible / 2, hi) - Math.Max(centre - visible / 2, lo)) / length, 0, 1);
    }

    /// <summary>Grid step of the window search, as a fraction of the frame.</summary>
    private const double SearchStep = 0.002;

    /// <summary>
    /// The window centre along one axis that keeps the most weighted subject inside a window
    /// <paramref name="visible"/> wide; among equally good positions, the one nearest the weighted
    /// centre. A single subject that fits is therefore centred in the window, and of two subjects
    /// too far apart to both fit, the one on screen longer is kept whole rather than both halved.
    /// </summary>
    private static double Axis(IReadOnlyList<(double Lo, double Hi, double Weight)> spans, double weightedCentre, double visible)
    {
        double half = visible / 2;
        if (half >= 0.5 - 1e-9)
            return 0.5;

        double bestScore = double.NegativeInfinity, best = 0.5, bestDistance = double.PositiveInfinity;
        int steps = (int)Math.Ceiling((1 - visible) / SearchStep);
        for (int i = 0; i <= steps; i++)
        {
            double centre = Math.Min(1 - half, half + i * SearchStep);
            double lo = centre - half, hi = centre + half;
            double score = 0;
            foreach ((double sLo, double sHi, double weight) in spans)
            {
                double length = Math.Max(1e-9, sHi - sLo);
                score += weight * Math.Max(0, Math.Min(hi, sHi) - Math.Max(lo, sLo)) / length;
            }

            double distance = Math.Abs(centre - weightedCentre);
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance))
            {
                bestScore = score;
                best = centre;
                bestDistance = distance;
            }
        }

        return Math.Round(best, 3);
    }

    private static bool InKept(IReadOnlyList<(double Start, double End)> windows, double t) =>
        windows.Any(w => t >= w.Start - 1e-6 && t < w.End + 1e-6);

    private static double Overlap(IReadOnlyList<(double Start, double End)> windows, double start, double end) =>
        windows.Sum(w => Math.Max(0, Math.Min(w.End, end) - Math.Max(w.Start, start)));

    private static List<WeightedBox> TrackedBoxes(
        VideoAnalysisArtifact artifact, int sourceIndex, IReadOnlyList<(double Start, double End)> windows)
    {
        var boxes = new List<WeightedBox>();
        foreach (VideoInsertRegionTrack track in artifact.InsertRegions ?? [])
        {
            if (track.SourceIndex != sourceIndex)
                continue;
            foreach (VideoInsertQuadKeyframe k in track.Keyframes)
            {
                if (!InKept(windows, k.TimeSec))
                    continue;
                double x0 = Math.Min(Math.Min(k.X0, k.X1), Math.Min(k.X2, k.X3));
                double x1 = Math.Max(Math.Max(k.X0, k.X1), Math.Max(k.X2, k.X3));
                double y0 = Math.Min(Math.Min(k.Y0, k.Y1), Math.Min(k.Y2, k.Y3));
                double y1 = Math.Max(Math.Max(k.Y0, k.Y1), Math.Max(k.Y2, k.Y3));
                boxes.Add(new WeightedBox(Math.Clamp(x0, 0, 1), Math.Clamp(y0, 0, 1), Math.Clamp(x1, 0, 1), Math.Clamp(y1, 0, 1), 1, track.Id));
            }
        }

        return boxes;
    }

    private static List<WeightedBox> SubjectBoxes(
        VideoAnalysisArtifact artifact, int sourceIndex, IReadOnlyList<(double Start, double End)> windows)
    {
        var boxes = new List<WeightedBox>();
        foreach (VideoAnalysisShot shot in artifact.Shots)
        {
            if (shot.SourceIndex != sourceIndex || shot.Subject is not { } s)
                continue;
            double weight = Overlap(windows, shot.StartSec, shot.EndSec);
            if (weight <= 0)
                continue;
            boxes.Add(new WeightedBox(
                Math.Clamp(s.X, 0, 1), Math.Clamp(s.Y, 0, 1),
                Math.Clamp(s.X + s.W, 0, 1), Math.Clamp(s.Y + s.H, 0, 1), weight, shot.Id));
        }

        return boxes;
    }
}
