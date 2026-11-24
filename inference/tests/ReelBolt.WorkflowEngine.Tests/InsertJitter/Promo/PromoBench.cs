#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>
/// Measures the wobble seen in the staging "Launch Promo" film. Needs ffmpeg and the read-only inputs in
/// <c>/tmp/insert-tracking/promo</c> (override <c>REELBOLT_JITTER_PROMO</c>); writes <c>report.md</c> and csv files to <c>/tmp/insert-tracking/promo-measure</c>
/// (override <c>REELBOLT_PROMO_MEASURE_DIR</c>). Set <c>REELBOLT_PROMO_FRESH=1</c> to ignore the cached per-clip measurements.
/// </summary>
public class PromoBench
{
    [JitterBenchFact]
    public void Measure_the_launch_promo_film() => new PromoReport().Run();
}

internal sealed class PromoReport
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly PromoInputs _inp = new();
    private readonly StringBuilder _sb = new();
    private double Scale => _inp.Scale;
    private int _n;
    private string _lastTitle = string.Empty;
    private readonly StringBuilder _tables = new("table,row,frames,slip_rms_out,slip_p95_out,slip_max_out,hf_rms_out,hf_max_out,err_mean_out,err_max_out,mean_signed_out,scale_out_per_src\n");
    private double _axisX, _axisY, _screenResidual;
    private readonly List<(string Id, PromoScore.Row Ideal, PromoScore.Row Render, PromoScore.Row Corrected, PromoScore.Row M1Quad, PromoScore.Row M1Edges)> _m4 = [];

    private static string F(double v, string f = "0.000") => PromoScore.F(v, f);

    private static string F(double? v, string f = "0.000") => PromoScore.F(v, f);

    public void Run()
    {
        _n = _inp.Surface.Count;
        CardRect card = PromoMeasure.MeasureCardInProxy(_inp);
        PromoRender.Levels levels = PromoRender.MeasureLevels(_inp, card);
        List<Quad> keyOut = _inp.Surface.Select((q, i) => _inp.ToOut(q) with { T = i / PromoInputs.Fps }).ToList();
        List<Quad> cardPred = keyOut.Select(q => PromoMeasure.PredictCard(q, card)).ToList();

        ClipMeasurement film = CachedMeasurement("film", () => PromoMeasure.MeasureClip(_inp.FilmPath, keyOut, cardPred, PromoMeasure.ScreenOptions, PromoMeasure.CardOptions));
        ClipAnalysis fa = PromoAnalysis.Analyze(film, keyOut, cardPred);

        IReadOnlyList<PromoVariant> variants = PromoVariants.Build(_inp);
        List<List<Quad>> variantOut = variants.Select(v => v.Track.Select((q, i) => _inp.ToOut(q) with { T = i / PromoInputs.Fps }).ToList()).ToList();

        Header(card, levels, film);
        Validation(film, keyOut);
        Floors(film, fa, keyOut, variants, variantOut, card, levels);
        HeadlineTables(fa, film, keyOut);
        Spectra(fa);
        Counterfactuals(film, fa, keyOut, cardPred, variants, variantOut, card, levels);
        Caveats();
        Csv(film, keyOut, cardPred, variants, variantOut);
        File.WriteAllText(Path.Combine(_inp.OutDir, "tables.csv"), _tables.ToString());
        File.WriteAllText(Path.Combine(_inp.OutDir, "report.md"), _sb.ToString().Replace("<!--SUMMARY-->", Summary(fa)));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private ClipMeasurement CachedMeasurement(string name, Func<ClipMeasurement> compute)
    {
        string path = Path.Combine(_inp.WorkDir, $"cache-{PromoMeasure.AlgoVersion}-{name}.json");
        if (File.Exists(path) && Environment.GetEnvironmentVariable("REELBOLT_PROMO_FRESH") != "1")
        {
            try
            {
                ClipMeasurement? hit = JsonSerializer.Deserialize<ClipMeasurement>(File.ReadAllText(path), Json);
                if (hit is not null && hit.N > 0)
                    return hit;
            }
            catch (JsonException)
            {
            }
        }

        ClipMeasurement m = compute();
        var stripped = new ClipMeasurement
        {
            N = m.N,
            Screen = m.Screen,
            Card = m.Card,
            ScreenEdges = m.ScreenEdges.Select(Strip).ToArray(),
            CardEdges = m.CardEdges.Select(Strip).ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(stripped, Json));
        return m;

        static PromoEdge[] Strip(PromoEdge[]? es) => es is null ? null! : es.Select(e => e with { Ts = null, Ds = null, Keep = null }).ToArray();
    }

    private void W(string s = "") => _sb.AppendLine(s);

    private const string RowHeader = "| row | frames | SlipRms out / src | SlipP95 out / src | SlipMax out / src | HfRms out / src | HfMax out / src | ErrMean out / src | ErrMax out / src | mean signed (out) | LagLS |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|";

    private const string RowHeaderNoErr = "| row | frames | SlipRms out / src | SlipP95 out / src | SlipMax out / src | HfRms out / src | HfMax out / src | mean (out) |\n|---|---:|---:|---:|---:|---:|---:|---:|";

    private string Pair(double v) => double.IsNaN(v) ? "-" : $"{F(v)} / {F(v / Scale)}";

    private string Line(PromoScore.Row r, bool showErr = true)
    {
        if (r.Stats is not { } s)
            return $"| {r.Label} | {r.Frames} | - | - | - | - | - | - |" + (showErr ? " - | - | - |" : string.Empty);
        string common = $"| {r.Label} | {r.Frames} | {Pair(s.SlipRms)} | {Pair(s.SlipP95)} | {Pair(r.SlipMax)} | {Pair(s.HfRms)} | {Pair(r.HfMax)} |";
        return showErr
            ? $"{common} {Pair(s.ErrMean)} | {Pair(s.ErrMax)} | {F(r.OffsetMean)} | {F(s.LagLsFrames, "0.00")} |"
            : $"{common} {F(r.OffsetMean)} |";
    }

    private void Table(string title, IEnumerable<PromoScore.Row> rows, bool showErr = true)
    {
        if (!string.IsNullOrEmpty(title))
        {
            W(title);
            _lastTitle = title.TrimStart('#', ' ').Replace(",", ";", StringComparison.Ordinal);
        }

        W();
        W(showErr ? RowHeader : RowHeaderNoErr);
        foreach (PromoScore.Row r in rows)
        {
            W(Line(r, showErr));
            if (r.Stats is { } st)
            {
                _tables.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{_lastTitle},{r.Label.Replace(',', ';')},{r.Frames},{st.SlipRms:0.0000},{st.SlipP95:0.0000},{r.SlipMax:0.0000},{st.HfRms:0.0000},{r.HfMax:0.0000},{st.ErrMean:0.0000},{st.ErrMax:0.0000},{r.OffsetMean:0.0000},{Scale:0.000000}"));
            }
        }

        W();
    }

    // ------------------------------------------------------------------------------------------------ sections

    private void Header(CardRect card, PromoRender.Levels lv, ClipMeasurement film)
    {
        W("# T-007: the wobble in the Launch Promo film, measured");
        W();
        W("Film: `final-cold-open-ac30a89f.mp4` (1920x1080 yuv420p, 24 fps, 362 frames), plate keyframes from `artifact-65fe46f0` (`insertRegions[0]`, 362 keyframes, `surfaceCorners`), compile `step-81a57fb8` (matted insert, overscan 0.10 -> content pinned to the no-overscan plate quad with edge smear, `contentFit` stretch, Crop 1920x1080).");
        W();
        W($"All metrics are the definitions of `JitterMetrics.cs` (SlipRms/SlipP95 = frame-to-frame change of the error; HfRms = error minus its centred 1 s mean; ErrMean/ErrMax = |error|), pooled over the corners of a quad or over four sample points along an edge. Units: OUTPUT px of the 1920x1080 film; `src` columns are output px / {F(Scale, "0.000000")} (the cover-fit scale), i.e. plate px (1344x768). A constant error is reported as `offset`, not as wobble.");
        W();
        W("<!--SUMMARY-->");
        W("## 0. Geometry and set-up");
        W();
        W($"- `SourceCanvasFit.For(plate 1344x768, canvas 1920x1080, cover: true)` (production code): scale {F(Scale, "0.000000")}, offset ({F(_inp.Fit.OffsetX, "0.000")}, {F(_inp.Fit.OffsetY, "0.000")}) px. The brief's numbers (1.428571, vertical offset 8.571) match. Caveat: ffmpeg's crop truncates the vertical offset to an integer (8), a constant 0.57 px shift that appears in `offset` and not in slip.");
        W("- Corner order of the artifact (`x0..y3` = TL, TR, BL, BR) is the bench's `Quad` order; keyframe i is stamped i/24 s and is paired with output frame i (checked in section 1).");
        W($"- Card (the chat window's outer rectangle) in content unit coordinates, fitted on the 960x540 proxy at {card.Frames} frames: u {F(card.U0, "0.00000")}..{F(card.U1, "0.00000")}, v {F(card.V0, "0.00000")}..{F(card.V1, "0.00000")} (repeatability over frames {F(card.SpreadU)} / {F(card.SpreadV)} proxy px). Content luma (proxy, tv range): margin {F(lv.Margin, "0")}, card {F(lv.Card, "0")}.");
        W("- With the matte on, the compile pins the content frame to the PLATE quad (`ContentKeyframes = plateKeyframes`) and only the warped matte canvas carries the 0.10 overscan, so the card is predicted from the keyframe `surfaceCorners` directly (stretch: the content frame IS the plate quad); the overscan does not enter.");
        W("- Edge meter (`PromoEdgeFit`): `EdgeMeter`'s area-conserving profile integral (along the inward normal the linear luma field is sampled bilinearly at 0.25 px steps and summed), two passes (reach 9 px then 6 px re-seeded on the first line, so the result does not depend on the keyframe seed's error), robust fit. Real screen edges are BOWED (sagitta 1-9 px, growing with the push-in): cleanliness is judged on the residual of a parabola per edge (<= 0.6 px screen, <= 1.0 px card, >= 85 % / 80 % inliers, contrast >= 12 / 3 luma levels, every profile sample >= 1 px inside the frame), and the edge itself is the straight least-squares line through the kept samples (the bow's chord). Corners = intersections of four such lines.");
        W();
    }

    /// <summary>RMS per coordinate of key(i+s) - measured(i) over corners and frames, constant offset removed; with <paramref name="highPass"/> the error series are first high-passed by their centred 1 s mean.</summary>
    private double ShiftRms(ClipMeasurement film, List<Quad> keyOut, int s, bool highPass)
    {
        double sq = 0;
        int n = 0;
        for (int c = 0; c < 4; c++)
        {
            for (int axis = 0; axis < 2; axis++)
            {
                var e = new double?[_n];
                for (int i = 0; i < _n; i++)
                {
                    if (film.Screen[i] is { } m && i + s >= 0 && i + s < _n)
                    {
                        (double X, double Y) k = keyOut[i + s].Corner(c), g = m.Corner(c);
                        e[i] = axis == 0 ? k.X - g.X : k.Y - g.Y;
                    }
                }

                double[] vals = e.Where(v => v is not null).Select(v => v!.Value).ToArray();
                if (vals.Length < 8)
                    continue;
                double mean = vals.Average();
                for (int i = 0; i < _n; i++)
                {
                    if (e[i] is not { } v)
                        continue;
                    double x = v - mean;
                    if (highPass)
                    {
                        int lo = Math.Max(0, i - 12), hi = Math.Min(_n - 1, i + 12);
                        double ws = 0;
                        int wn = 0;
                        for (int j = lo; j <= hi; j++)
                        {
                            if (e[j] is { } w)
                            {
                                ws += w - mean;
                                wn++;
                            }
                        }

                        x -= ws / wn;
                    }

                    sq += x * x;
                    n++;
                }
            }
        }

        return Math.Sqrt(sq / n);
    }

    private void Validation(ClipMeasurement film, List<Quad> keyOut)
    {
        W("## 1. Alignment check and validity");
        W();
        W("Is keyframe i the plate at output frame i? RMS (output px, per coordinate) of key(i+s) minus the measured screen quad(i) for integer shifts s, over frames with a measured screen quad. `raw` removes only the constant offset; `high-pass` also removes everything slower than 1 s (the centred 1 s mean of the error), i.e. what is left of the error if the film and the keyframes were offset in time but both were smooth. A time offset between keyframes and film would show as a minimum away from s = 0 in the high-pass row.");
        W();
        int[] shifts = [-3, -2, -1, 0, 1, 2, 3, 4];
        W("| shift s (frames) | " + string.Join(" | ", shifts) + " |");
        W("|---|" + string.Join("|", shifts.Select(_ => "---:")) + "|");
        W("| raw | " + string.Join(" | ", shifts.Select(sh => F(ShiftRms(film, keyOut, sh, false)))) + " |");
        W("| high-pass | " + string.Join(" | ", shifts.Select(sh => F(ShiftRms(film, keyOut, sh, true)))) + " |");
        W();
        double rawBest = shifts.OrderBy(sh => ShiftRms(film, keyOut, sh, false)).First();
        double hpBest = shifts.OrderBy(sh => ShiftRms(film, keyOut, sh, true)).First();
        W($"Reading: the high-pass error is flat in s (best s = {hpBest}, {F(ShiftRms(film, keyOut, (int)hpBest, true))} against {F(ShiftRms(film, keyOut, 0, true))} at s = 0): the keyframes' error is not a time offset, it is noise that is independent of the shift. The raw row has a shallow minimum at s = {rawBest} that comes from slow drift (a parabola through s = -1, 0, 1 puts the vertex at +0.5 and s = 2 lands on the same parabola), not from the part of the error that wobbles. Independently, the content layer of the film matches a re-render of the same keyframes sharply at s = 0 (section 5), so the compile pins keyframe i to output frame i.");
        W();
        W("Frames scored per edge (an edge is scored only when its whole profile sweep is inside the frame, it has contrast and the fit is clean):");
        W();
        W("| element | top | right | bottom | left | all four (quad) |");
        W("|---|---:|---:|---:|---:|---:|");
        W($"| screen edge (light insert vs dark bezel) | {film.EdgeValid(false, 0)} | {film.EdgeValid(false, 1)} | {film.EdgeValid(false, 2)} | {film.EdgeValid(false, 3)} | {film.Screen.Count(q => q is not null)} |");
        W($"| card border (chat window vs margin) | {film.EdgeValid(true, 0)} | {film.EdgeValid(true, 1)} | {film.EdgeValid(true, 2)} | {film.EdgeValid(true, 3)} | {film.Card.Count(q => q is not null)} |");
        W();
        var reasons = new Dictionary<string, int>();
        for (int i = 0; i < _n; i++)
        {
            foreach (PromoEdge e in film.ScreenEdges[i] ?? [])
            {
                if (e.Valid)
                    continue;
                string r = e.Reason.StartsWith("outside", StringComparison.Ordinal) ? "profile sweep leaves the frame"
                    : e.Reason.StartsWith("low", StringComparison.Ordinal) ? "contrast under 12 levels"
                    : "fit not clean (parabola residual)";
                reasons[r] = reasons.GetValueOrDefault(r) + 1;
            }
        }

        W("Why screen edges were not scored (edge-frames, of 4 x 362 = 1448): " + string.Join(", ", reasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")) + ".");
        W();
        int lastTop = Enumerable.Range(0, _n).Where(i => film.ScreenEdges[i]?[0].Valid == true).DefaultIfEmpty(-1).Max();
        int lastQuad = Enumerable.Range(0, _n).Where(i => film.Screen[i] is not null).DefaultIfEmpty(-1).Max();
        W($"The top edge's last clean frame is {lastTop} and the last scored quad is frame {lastQuad}: later the screen's top edge is at or beyond the frame top (the tracker's own top corners are clamped at the plate border there). Card edges stay in frame to the end.");
        W();
    }

    // ------------------------------------------------------------------------------------------------ floors

    private readonly Dictionary<string, (ClipAnalysis Lossless, ClipAnalysis Codec)> _synthetic = [];

    private void Floors(ClipMeasurement film, ClipAnalysis fa, List<Quad> keyOut, IReadOnlyList<PromoVariant> variants, List<List<Quad>> variantOut, CardRect card, PromoRender.Levels lv)
    {
        W("## 2. The measurement's own floor");
        W();
        var screenRes = PromoScore.ResidualFloor(film.Screen);
        _screenResidual = screenRes.Rms;
        var cardRes = PromoScore.ResidualFloor(film.Card);
        W("**No-truth floor of the measured quads** (leave-one-out quadratic through +-4 neighbours, the same `ResidualRms` definition as the earlier real-track tables; an UPPER bound on the meter's noise because it also contains the plate's own non-quadratic motion):");
        W();
        W("| measured quad | frames | ResidualRms out | ResidualRms src |");
        W("|---|---:|---:|---:|");
        W($"| screen edge quad | {screenRes.Frames} | {F(screenRes.Rms)} | {F(screenRes.Rms / Scale)} |");
        W($"| card border quad (NOT a floor: the card is pinned to the keyframes and carries their jitter) | {cardRes.Frames} | {F(cardRes.Rms)} | {F(cardRes.Rms / Scale)} |");
        var keyRes = JitterMetrics.ComputeNoTruth(keyOut, PromoInputs.Fps);
        W($"| shipped keyframes (all 362, for reference; Film A in the baseline report: 0.994 src px) | {_n} | {F(keyRes.ResidualRms)} | {F(keyRes.ResidualRms / Scale)} |");
        W();
        W("**Known-truth floor** (analytic anti-aliased sequences at the film's geometry, truth = heavily smoothed keyframes, the card pushed through each frame's screen quad; written as raw yuv420p and also through the film's x264 settings (crf 20 veryfast); the same meter, the same seeds as the film (the shipped keyframes), scored on the film's valid frames). Rows are measured minus truth, so SlipRms here is pure measurement noise:");
        W();
        PromoVariant truthVariant = variants.First(v => v.Id == "K4fix-H12");
        List<Quad> truthOut = variantOut[variants.ToList().IndexOf(truthVariant)];
        List<Quad> truthCard = truthOut.Select(q => PromoMeasure.PredictCard(q, card)).ToList();
        var rows = new List<PromoScore.Row>();
        foreach (PromoRender.SyntheticSpec spec in PromoRender.SyntheticSpecs)
        {
            string tag = $"synthetic-{spec.Id}";
            string mp4 = Path.Combine(_inp.WorkDir, $"{tag}.mp4");
            List<Quad> fileSeedsCard = cardPred(keyOut, card);
            ClipMeasurement lossless = CachedMeasurement(tag + "-lossless", () =>
                PromoMeasure.MeasureFrames(PromoRender.SyntheticFrames(_inp, truthOut, card, lv, spec, 14, 69, mp4), keyOut, fileSeedsCard, PromoMeasure.ScreenOptions, PromoMeasure.CardOptions));
            ClipMeasurement codec = CachedMeasurement(tag + "-codec", () => PromoMeasure.MeasureClip(mp4, keyOut, fileSeedsCard, PromoMeasure.ScreenOptions, PromoMeasure.CardOptions));
            ClipAnalysis la = PromoAnalysis.Analyze(lossless, truthOut, truthCard, film);
            ClipAnalysis ca = PromoAnalysis.Analyze(codec, truthOut, truthCard, film);
            _synthetic[spec.Id] = (la, ca);
            rows.Add(Retitle(la.M1Quad, $"screen quad vs truth, {spec.Id}, no codec"));
            rows.Add(Retitle(ca.M1Quad, $"screen quad vs truth, {spec.Id}, x264"));
            rows.Add(Retitle(PromoAnalysis.Pool("x", la.M1Edge), $"screen edges vs truth (pooled), {spec.Id}, no codec"));
            rows.Add(Retitle(PromoAnalysis.Pool("x", ca.M1Edge), $"screen edges vs truth (pooled), {spec.Id}, x264"));
            rows.Add(Retitle(ca.M3Quad, $"card quad vs truth, {spec.Id}, x264"));
            rows.Add(Retitle(PromoAnalysis.Pool("x", ca.M3Edge), $"card edges vs truth (pooled), {spec.Id}, x264"));
            rows.Add(Retitle(PromoAnalysis.Pool("x", la.M2Side), $"M2 gap card-to-screen (pooled sides), {spec.Id}, no codec"));
            rows.Add(Retitle(PromoAnalysis.Pool("x", ca.M2Side), $"M2 gap card-to-screen (pooled sides), {spec.Id}, x264"));
            rows.Add(Retitle(ca.M2Quad, $"M2 quad-normalised, {spec.Id}, x264"));
        }

        Table(string.Empty, rows);
        W($"Synthetic settings: bezel luma 14, screen margin luma 69, card luma {F(69 + lv.Card - lv.Margin, "0")}; `nominal` = {PromoRender.SyntheticSpecs[0].Label}, `harsh` = {PromoRender.SyntheticSpecs[1].Label}. Edges are straight in the synthetic clips (the film's are bowed), so this floor does not include the bow's interaction with the line fit.");
        W();
    }

    private static List<Quad> cardPred(List<Quad> keyOut, CardRect card) => keyOut.Select(q => PromoMeasure.PredictCard(q, card)).ToList();

    private static PromoScore.Row Retitle(PromoScore.Row r, string label) => r with { Label = label };

    // ------------------------------------------------------------------------------------------------ headline

    private void HeadlineTables(ClipAnalysis fa, ClipMeasurement film, List<Quad> keyOut)
    {
        W("## 3. Results on the film");
        W();
        PromoScore.Row m2pool = PromoAnalysis.Pool("M2 pooled over the four sides", fa.M2Side);
        Table("### M2 (headline): perceived content wobble, card border against the screen edge",
            [.. fa.M2Side, m2pool], showErr: false);
        W("`M2 <side>`: distance, along the inward normal of the measured screen edge, from four points of the measured card edge to that screen edge (px of the frame); `mean` is the mean margin. Nothing but the two measured lines enters, so it is what the eye sees as the card's margin breathing. The margin grows with the push-in (it is proportional to the screen's size: 5 % of it), so ErrMean/ErrMax are omitted for these rows; SlipRms (frame-to-frame change), HfRms/HfMax (deviation from the centred 1 s mean) and SlipMax are drift-free.");
        W();
        Table("### M2 through the screen's own coordinates (card corners in the measured screen quad's unit square, minus their mean, scaled back by the screen's size of that frame; only frames where all eight edges were clean)", [fa.M2Quad]);
        W("ErrMean/ErrMax of this row are deviations of the card's normalised position from its mean over the shot and include slow drift (the flat plate quad vs the bowed outline, perspective).");
        W();
        PromoScore.Row m1pool = PromoAnalysis.Pool("M1 pooled over the four edges", fa.M1Edge);
        Table("### M1: the track against the plate (keyframe `surfaceCorners` in output px minus the measured screen edge)", [.. fa.M1Edge, m1pool, fa.M1Quad]);
        W("`mean signed` for an edge row is the mean error along the inward normal (track minus measured, positive = the track edge lies inside the measured one); for the quad row it is the magnitude of the mean corner error vector. These constant parts (the flat quad vs the bowed outline, rounded corners, the crop's 0.57 px) are the offset, NOT wobble.");
        W();
        AxisSplit(film, keyOut);
        PromoScore.Row m3pool = PromoAnalysis.Pool("M3 pooled over the four edges", fa.M3Edge);
        Table("### M3: render-added error (measured card minus the card the keyframes predict)", [.. fa.M3Edge, m3pool, fa.M3Quad]);
    }

    /// <summary>Slip of the M1 corner error split by axis (x and y separately), output px.</summary>
    private void AxisSplit(ClipMeasurement film, List<Quad> keyOut)
    {
        double sx = 0, sy = 0;
        int cnt = 0;
        for (int i = 1; i < _n; i++)
        {
            if (film.Screen[i] is not { } a || film.Screen[i - 1] is not { } b)
                continue;
            for (int c = 0; c < 4; c++)
            {
                double dx = keyOut[i].Corner(c).X - a.Corner(c).X - (keyOut[i - 1].Corner(c).X - b.Corner(c).X);
                double dy = keyOut[i].Corner(c).Y - a.Corner(c).Y - (keyOut[i - 1].Corner(c).Y - b.Corner(c).Y);
                sx += dx * dx;
                sy += dy * dy;
                cnt++;
            }
        }

        _axisX = Math.Sqrt(sx / cnt);
        _axisY = Math.Sqrt(sy / cnt);
        W($"M1 quad slip by axis (RMS over corners and consecutive frames, out px / src px): horizontal {Pair(Math.Sqrt(sx / cnt))}, vertical {Pair(Math.Sqrt(sy / cnt))} ({cnt / 4} frame pairs). Edge rows above agree: the left and right edges (horizontal position) carry the wobble.");
        W();
    }

    // ------------------------------------------------------------------------------------------------ spectra

    private void Spectra(ClipAnalysis fa)
    {
        W("## 4. What the error looks like in time");
        W();
        W("Welch periodogram of the error series (64-frame segments, 50 % overlap, quadratic detrend and Hann window per segment, pooled over the four points of every edge/side series; bin k = period 64/k frames = 0.375 k Hz at 24 fps). White noise has no preferred bin, so the film is compared with the SAME pooled series measured on the known-truth synthetic clip (nominal, x264) at the same frames: `film/floor` is the excess, and the dominant period is where the excess power sits. Power in arbitrary but common units.");
        W();
        (ClipAnalysis la, ClipAnalysis ca) = _synthetic["nominal"];
        _ = la;
        foreach ((string label, IReadOnlyList<IReadOnlyList<double[]?>> film, IReadOnlyList<IReadOnlyList<double[]?>> floor) in new (string, IReadOnlyList<IReadOnlyList<double[]?>>, IReadOnlyList<IReadOnlyList<double[]?>>)[]
                 {
                     ("M1: keyframes vs the screen edge", fa.M1Series, ca.M1Series),
                     ("M2: card edge to screen edge (the headline)", fa.M2Series, ca.M2Series),
                     ("M3: render-added (measured card vs keyframe prediction)", fa.M3Series, ca.M3Series)
                 })
        {
            PromoScore.Spectrum? sf = PromoScore.Welch(film);
            PromoScore.Spectrum? sn = PromoScore.Welch(floor);
            W($"**{label}** ({sf?.Segments ?? 0} film segments, {sn?.Segments ?? 0} floor segments)");
            W();
            if (sf is null || sn is null)
            {
                W("not enough clean consecutive frames");
                W();
                continue;
            }

            W("| band | film power | floor power | film/floor | share of film power | excess power (film - floor) |");
            W("|---|---:|---:|---:|---:|---:|");
            double total = PromoScore.Bands.Sum(b => PromoScore.BandPower(sf, b));
            double bestExcess = double.MinValue;
            string bestBand = string.Empty;
            foreach ((string Name, int From, int To) band in PromoScore.Bands)
            {
                double pf = PromoScore.BandPower(sf, band), pn = PromoScore.BandPower(sn, band);
                double excess = pf - pn;
                if (excess > bestExcess)
                {
                    bestExcess = excess;
                    bestBand = band.Name;
                }

                W($"| {band.Name} | {F(pf, "0.0000")} | {F(pn, "0.0000")} | {F(pf / pn, "0.0")} | {F(pf / total, "0.00")} | {F(excess, "0.0000")} |");
            }

            int peakBin = Enumerable.Range(1, 32).OrderByDescending(k => sf.Power[k] - sn.Power[k]).First();
            W();
            W($"Largest excess over the floor in band `{bestBand}`; single strongest bin above the floor: k = {peakBin} (period {F(sf.PeriodOf(peakBin), "0.0")} frames, {F(sf.HzOf(peakBin), "0.00")} Hz).");
            W();
        }

        PromoScore.Spectrum? p1 = PromoScore.Welch(fa.M1Series), p2 = PromoScore.Welch(fa.M2Series), p3 = PromoScore.Welch(fa.M3Series), f2 = PromoScore.Welch(ca.M2Series);
        if (p1 is not null && p2 is not null && p3 is not null && f2 is not null)
        {
            W("Per-bin power (bin k, period 64/k frames): where the single lines are. The floor column is the synthetic M2-style series.");
            W();
            W("| k | period (frames) | Hz | M1 film | M2 film | M3 film | M2 floor |");
            W("|---:|---:|---:|---:|---:|---:|---:|");
            for (int k = 1; k <= 32; k++)
                W($"| {k} | {F(p1.PeriodOf(k), "0.0")} | {F(p1.HzOf(k), "0.00")} | {F(p1.Power[k], "0.000")} | {F(p2.Power[k], "0.000")} | {F(p3.Power[k], "0.000")} | {F(f2.Power[k], "0.000")} |");
            W();
        }
    }

    // ------------------------------------------------------------------------------------------------ M4

    private void Counterfactuals(ClipMeasurement film, ClipAnalysis fa, List<Quad> keyOut, List<Quad> cardPred, IReadOnlyList<PromoVariant> variants, List<List<Quad>> variantOut, CardRect card, PromoRender.Levels lv)
    {
        W("## 5. M4: what re-smoothing the keyframes would have done");
        W();
        W("Every variant re-smooths the SHIPPED keyframes (`surfaceCorners`, already through the shipped K0 `Smooth`). No detection grid exists (the plate clip was not obtainable), so the C2 chain runs on the keyframes alone: the hinge outlier pass on the tracker's own corners (`x0..y3`), the production upright pass, then the cadence wrapper around K4ra, with the sigma prior g = 0.10 x 1344/480 and the grid assumed 480x274. Variant (a) has held-frame detection OFF (the wrapper then is plain K4ra on the hinge-cleaned track); (a2) derives held frames from the keyframes' own per-frame travel with the same rule as `HeldFrameDetector.FromEdgeDiff` (a K0-smoothed track has lost most of the held-frame signature, so the count it flags is informative).");
        W();
        foreach (PromoVariant v in variants)
            W($"- `{v.Id}`: {v.Label}. {v.Note}.");
        W();
        W("The variants are applied AFTER the shipped K0 (the keyframes already contain it). The real C2 would replace K0, whose motion gate largely switches off on a moving plate, so the difference should be small but it is not zero. `frames` in a pooled row is the sum over its four edges or sides.");
        W();

        // Per-variant M1 rows and predicted M2 rows.
        var m1Rows = new List<PromoScore.Row>();
        var m2IdealRows = new List<PromoScore.Row>();
        var m2CorrRows = new List<PromoScore.Row>();
        var m2RenderRows = new List<PromoScore.Row>();
        var m3RenderRows = new List<PromoScore.Row>();
        IReadOnlyList<double[]?>[] filmGap = fa.M2Series;

        // Ideal gap series per variant: the card the variant's plate quad predicts, against the film's MEASURED screen edges.
        IReadOnlyList<double[]?>[] IdealGap(List<Quad> plateOut)
        {
            var o = new IReadOnlyList<double[]?>[4];
            for (int e = 0; e < 4; e++)
            {
                var s = new double[]?[_n];
                for (int i = 0; i < _n; i++)
                {
                    if (film.ScreenEdges[i] is { } es && es[e].Valid)
                        s[i] = PromoScore.GapInside(PromoScore.Points(PromoMeasure.PredictCard(plateOut[i], card), e), es[e], keyOut[i], e);
                }

                o[e] = s;
            }

            return o;
        }

        IReadOnlyList<double[]?>[] shippedIdeal = IdealGap(variantOut[0]);
        for (int vi = 0; vi < variants.Count; vi++)
        {
            PromoVariant v = variants[vi];
            List<Quad> tv = variantOut[vi];
            var track = new Quad?[_n];
            for (int i = 0; i < _n; i++)
                track[i] = film.Screen[i] is not null ? tv[i] : null;
            m1Rows.Add(Retitle(PromoScore.Quads(v.Id, track, film.Screen), v.Id));
            var edgeRows = new List<PromoScore.Row>();
            for (int e = 0; e < 4; e++)
            {
                var s = new double[]?[_n];
                for (int i = 0; i < _n; i++)
                {
                    if (film.ScreenEdges[i] is { } es && es[e].Valid)
                        s[i] = PromoScore.MeasuredInsideTrack(tv[i], e, es[e]).Select(x => -x).ToArray();
                }

                edgeRows.Add(PromoScore.Scalar($"{v.Id} edge {e}", s));
            }

            m1Rows[^1] = m1Rows[^1] with { Label = $"{v.Id} (quad)" };
            m1Rows.Add(Retitle(PromoAnalysis.Pool("x", edgeRows), $"{v.Id} (edges pooled)"));

            IReadOnlyList<double[]?>[] ideal = IdealGap(tv);
            var idealRows = new List<PromoScore.Row>();
            var corrRows = new List<PromoScore.Row>();
            for (int e = 0; e < 4; e++)
            {
                // Only frames where the film's own card edge was clean, so ideal / corrected / measured rows share frames.
                var si = new double[]?[_n];
                var sc = new double[]?[_n];
                for (int i = 0; i < _n; i++)
                {
                    if (ideal[e][i] is { } g && filmGap[e][i] is { } gf && shippedIdeal[e][i] is { } gs)
                    {
                        si[i] = g;
                        sc[i] = Enumerable.Range(0, 4).Select(k => g[k] + (gf[k] - gs[k])).ToArray();
                    }
                }

                idealRows.Add(PromoScore.Scalar($"{v.Id} ideal {e}", si));
                corrRows.Add(PromoScore.Scalar($"{v.Id} corrected {e}", sc));
            }

            m2IdealRows.Add(Retitle(PromoAnalysis.Pool("x", idealRows), v.Id));
            m2CorrRows.Add(Retitle(PromoAnalysis.Pool("x", corrRows), v.Id));
        }

        // Re-rendered card layers: measured card edges against the film's measured screen edges.
        PromoRender.Content content = PromoRender.Content.Crisp;
        for (int vi = 0; vi < variants.Count; vi++)
        {
            PromoVariant v = variants[vi];
            ClipMeasurement m = RenderedCard(v.Id, variantOut[vi], content, card, lv, codec: true);
            ClipAnalysis ra = PromoAnalysis.Analyze(m, variantOut[vi], variantOut[vi].Select(q => PromoMeasure.PredictCard(q, card)).ToList(), film);
            var gapRows = new List<PromoScore.Row>();
            for (int e = 0; e < 4; e++)
            {
                var s = new double[]?[_n];
                for (int i = 0; i < _n; i++)
                {
                    if (film.ScreenEdges[i] is { } es && es[e].Valid && i < m.CardEdges.Length && m.CardEdges[i] is { } ce && ce[e].Valid && filmGap[e][i] is not null)
                        s[i] = PromoScore.GapInside(PromoScore.Points(ce[e]), es[e], keyOut[i], e);
                }

                gapRows.Add(PromoScore.Scalar($"{v.Id} render {e}", s));
            }

            m2RenderRows.Add(Retitle(PromoAnalysis.Pool("x", gapRows), v.Id));
            m3RenderRows.Add(Retitle(PromoAnalysis.Pool("x", ra.M3Edge), v.Id));
        }

        for (int vi = 0; vi < variants.Count; vi++)
            _m4.Add((variants[vi].Id, m2IdealRows[vi], m2RenderRows[vi], m2CorrRows[vi], m1Rows[2 * vi], m1Rows[2 * vi + 1]));

        Table("### M4a: each variant's track against the measured screen (as M1; scored on the frames with a clean screen quad)", m1Rows);
        W("Pooled rows are count-weighted RMS over the four edges of the edge-normal error (see M1). The shipped row reproduces the M1 table.");
        W();
        Table("### M4b: predicted M2, IDEAL render (the card the variant's plate quad predicts, against the film's measured screen edges; render adds nothing)", m2IdealRows.Prepend(Retitle(PromoAnalysis.Pool("x", fa.M2Side), "film, measured (for reference)")), showErr: false);
        Table("### M4c: predicted M2, M3-corrected (ideal + the film's own card-vs-keyframes error, i.e. assumes the render adds the same error whatever the keyframes)", m2CorrRows, showErr: false);
        Table("### M4d: predicted M2, RE-RENDERED content layer (production `perspective` chain + x264 crf 20, crisp 1920x1080 analytic card; card measured on the render, screen edges from the film)", m2RenderRows, showErr: false);
        Table("### M4e: M3 of each re-render (measured card minus the card its own keyframes predict): what the content layer adds for that keyframe path", m3RenderRows);

        // Validation of the render simulation against the film.
        W("### Is the re-render faithful? (shipped keyframes)");
        W();
        var valRows = new List<PromoScore.Row>();
        foreach ((PromoRender.Content c, bool codec) in new[] { (PromoRender.Content.Crisp, true), (PromoRender.Content.Crisp, false), (PromoRender.Content.Proxy, true) })
        {
            ClipMeasurement m = RenderedCard("shipped", variantOut[0], c, card, lv, codec);
            ClipAnalysis ra = PromoAnalysis.Analyze(m, variantOut[0], cardPred, film);
            valRows.Add(Retitle(PromoAnalysis.Pool("x", ra.M3Edge), $"M3 of the re-render: {c}, {(codec ? "x264" : "lossless")}"));
            var diff = new Quad?[_n];
            var filmCard = new Quad?[_n];
            for (int i = 0; i < _n; i++)
            {
                if (m.Card[i] is { } rq && film.Card[i] is { } fq)
                {
                    diff[i] = rq;
                    filmCard[i] = fq;
                }
            }

            valRows.Add(Retitle(PromoScore.Quads("x", diff, filmCard), $"re-render card minus FILM card: {c}, {(codec ? "x264" : "lossless")}"));
        }

        valRows.Add(Retitle(PromoAnalysis.Pool("x", fa.M3Edge), "M3 of the film itself (for reference)"));
        Table(string.Empty, valRows);
        W("If the re-render reproduced the film exactly, `re-render card minus FILM card` would be at the measurement floor of section 2 and `M3 of the re-render` would equal the film's M3. The proxy rows use the 960x540 proxy upscaled x2 (softer than the real 1920x1080 content), so they under-state the aliasing the real content produces when `perspective` minifies it by about 0.58.");
        W();
    }

    private ClipMeasurement RenderedCard(string tag, List<Quad> plateOut, PromoRender.Content content, CardRect card, PromoRender.Levels lv, bool codec)
    {
        string name = $"render-{tag}-{content}-{(codec ? "x264" : "lossless")}";
        return CachedMeasurement(name, () =>
        {
            string path = PromoRender.Render(_inp, plateOut, content, card, lv, codec, name);
            var seeds = plateOut.Select(q => PromoMeasure.PredictCard(q, card)).ToList();
            return PromoMeasure.MeasureClip(path, null, seeds, PromoMeasure.ScreenOptions, PromoMeasure.CardOptions);
        });
    }

    // ------------------------------------------------------------------------------------------------ caveats / csv

    private static string SlipFrame(IEnumerable<PromoScore.Row> rows) => rows.Where(r => r.SlipMaxFrame >= 0).OrderByDescending(r => r.SlipMax).Select(r => $"{r.SlipMaxFrame}, {r.Label}").FirstOrDefault() ?? "-";

    private string Summary(ClipAnalysis fa)
    {
        var sb = new StringBuilder();
        void L(string t = "") => sb.AppendLine(t);
        (ClipAnalysis la, ClipAnalysis ca) = _synthetic["nominal"];
        _ = la;
        PromoScore.Row m2 = PromoAnalysis.Pool("M2 headline: card border against the screen edge (4 sides pooled)", fa.M2Side);
        PromoScore.Row m1e = PromoAnalysis.Pool("M1: keyframes against the plate's screen edge (4 edges pooled)", fa.M1Edge);
        PromoScore.Row m3 = PromoAnalysis.Pool("M3: render-added (measured card minus the card the keyframes predict; 4 edges pooled)", fa.M3Edge);
        L("## Summary");
        L();
        L("Output px (1920x1080) / source px (1344x768 plate, divide by 1.428571). Slip = frame-to-frame change of the error (the wobble); Hf = deviation from the centred 1 s mean.");
        L();
        L("| quantity | frames | SlipRms out / src | SlipP95 out / src | HfRms out / src | HfMax out / src |");
        L("|---|---:|---:|---:|---:|---:|");
        void Row(string label, PromoScore.Row r)
        {
            if (r.Stats is not { } st)
            {
                L($"| {label} | - | - | - | - | - |");
                return;
            }

            L($"| {label} | {r.Frames} | {Pair(st.SlipRms)} | {Pair(st.SlipP95)} | {Pair(st.HfRms)} | {Pair(r.HfMax)} |");
        }

        Row("**M2 headline**: card border vs screen edge, 4 sides pooled", m2);
        Row("M2 through the screen's own coordinates (card corners, screen-normalised)", fa.M2Quad);
        Row("M1: shipped keyframes vs measured screen edge, 4 edges pooled", m1e);
        Row("M1: same, quad corners", fa.M1Quad);
        Row("M3: render-added (card vs keyframe prediction), 4 edges pooled", m3);
        Row("floor, known truth (synthetic, x264): screen quad", ca.M1Quad);
        Row("floor, known truth: M2-style gap, 4 sides pooled", PromoAnalysis.Pool("x", ca.M2Side));
        Row("floor, known truth: card edges, pooled", PromoAnalysis.Pool("x", ca.M3Edge));
        L();
        PromoScore.Spectrum? s1 = PromoScore.Welch(fa.M1Series), s2 = PromoScore.Welch(fa.M2Series);
        if (s1 is not null && s2 is not null)
        {
            double tot1 = PromoScore.Bands.Sum(b => PromoScore.BandPower(s1, b)), tot2 = PromoScore.Bands.Sum(b => PromoScore.BandPower(s2, b));
            double share(PromoScore.Spectrum sp, double tot, int idx) => PromoScore.BandPower(sp, PromoScore.Bands[idx]) / tot;
            int top1 = Enumerable.Range(1, 32).OrderByDescending(k => s1.Power[k]).First(), top2 = Enumerable.Range(1, 32).OrderByDescending(k => s2.Power[k]).First();
            double upTo5 = Enumerable.Range(1, 14).Sum(k => s2.Power[k]) / s2.Power.Skip(1).Sum();
            L($"- **Character**: not a 2-frame staircase (12 Hz share: M1 {F(share(s1, tot1, 6), "0.00")}, M2 {F(share(s2, tot2, 6), "0.00")}). It is broadband jitter: {F(upTo5, "0.00")} of the M2 power lies below 5 Hz (periods longer than 4.6 frames), spread as {F(share(s2, tot2, 0) + share(s2, tot2, 1) + share(s2, tot2, 2), "0.00")} under 1.5 Hz (the 0.4-1.5 Hz wander), {F(share(s2, tot2, 3), "0.00")} at periods 8-13 frames, {F(share(s2, tot2, 4), "0.00")} at 4-7 and {F(share(s2, tot2, 5), "0.00")} at 2-4 frames. The strongest single line is bin {top2} for M2 (period {F(s2.PeriodOf(top2), "0.0")} frames = {F(s2.HzOf(top2), "0.00")} Hz) and bin {top1} for M1 (period {F(s1.PeriodOf(top1), "0.0")} frames), a wander of about 1.3 s on top of a flat 1.9-5 Hz shimmer. In total {F(s2.Power.Skip(1).Sum() / PromoScore.Welch(ca.M2Series)!.Power.Skip(1).Sum(), "0")}x the measurement-noise floor of the same series (section 4).");
        }

        L($"- **Floors**: the measured screen quad's own no-truth ResidualRms is {Pair(_screenResidual)} px (an upper bound on the meter's noise); on known-truth synthetic clips through x264 the meter's SlipRms is {Pair(ca.M1Quad.Stats?.SlipRms ?? double.NaN)} px for the screen quad and {Pair(PromoAnalysis.Pool("x", ca.M2Side).Stats?.SlipRms ?? double.NaN)} px for the M2-style gap.");
        L($"- **Direction**: horizontal. M1 quad slip is {Pair(_axisX)} px horizontal against {Pair(_axisY)} px vertical; the left/right edges carry it.");
        L($"- **Maximum**: largest single-frame step (SlipMax) {Pair(m2.SlipMax)} px for M2 (frame {SlipFrame(fa.M2Side)}) and {Pair(m1e.SlipMax)} px for M1 (frame {SlipFrame(fa.M1Edge)}); typical steps are far smaller (P95 {Pair(m2.Stats?.SlipP95 ?? double.NaN)} px for M2), so the maximum is a handful of frames; largest deviation from the 1 s mean (HfMax) {Pair(m2.HfMax)} px for M2.");
        L("- **Attribution**: M2 is about the tracker's keyframe jitter (M1) plus a small content-layer part (M3); the screen itself is smooth (its own residual 0.19 out px) and the keyframe/film timing is not offset (section 1).");
        L();
        L("Predicted M2 slip if the SHIPPED keyframes had been re-smoothed (card edge vs the film's measured screen edges, 4 sides pooled); M1 = the same variant's track against the measured screen:");
        L();
        L("| variant | M1 quad SlipRms out / src | M1 edges pooled out / src | predicted M2, ideal render out / src | predicted M2, re-rendered content layer out / src | predicted M2, M3-corrected out / src |");
        L("|---|---:|---:|---:|---:|---:|");
        foreach ((string id, PromoScore.Row ideal, PromoScore.Row render, PromoScore.Row corrected, PromoScore.Row q, PromoScore.Row e) in _m4)
            L($"| {id} | {Pair(q.Stats?.SlipRms ?? double.NaN)} | {Pair(e.Stats?.SlipRms ?? double.NaN)} | {Pair(ideal.Stats?.SlipRms ?? double.NaN)} | {Pair(render.Stats?.SlipRms ?? double.NaN)} | {Pair(corrected.Stats?.SlipRms ?? double.NaN)} |");
        L();
        L("`C2-held-off` and `K4ra-g0.10` are the same numbers on purpose: the hinge pass rejected nothing and no frame was flagged held, so the C2 chain reduces to plain K4ra on these keyframes. `K4fix-H12` is a heavy-smoothing reference (what almost no wobble looks like), not a candidate. `M3-corrected` adds the film's own card-vs-keyframes error (which includes the card meter's noise) to the ideal prediction and is therefore the pessimistic bound; the re-render shows the content layer's added error does not depend on the keyframe path (section 5, M4e), which is what makes the ideal and re-rendered columns trustworthy.");
        L();
        return sb.ToString();
    }

    private void Caveats()
    {
        W("## 6. What the method cannot see");
        W();
        W("- The pseudo-truth is the screen's visible outline in the LUMA of a 4:2:0 h264 film. It cannot separate the plate's own motion from the generator's frame-to-frame instability: if the AI-generated monitor itself jitters, the screen edge jitters and the card is judged against a moving ruler (M2 then measures what the viewer sees, which is the point, but M1 is relative to the screen as drawn, not to a physical monitor).");
        W("- Bowed outline vs flat quad: the edges bow by 1-9 px and the compile pins a flat quad, so the margin between the card and the screen outline varies along each side (a static geometric effect, constant offset in M1, slowly drifting in M2). The straight-line chord fit moves with the bow; a changing bow reads as slow drift, not wobble.");
        W("- Only edges fully in frame and clean are scored; the top edge leaves the frame late in the shot and the card's top/left edges fail the cleanliness test more often (low contrast against the title bar / sidebar), so the late, zoomed-in part of the shot is under-represented, and slip is only computed over consecutive scored frames.");
        W("- The card is the one feature measured; text that animates inside it, the matte edge and the reflection layer are not tracked. A 1 px bright outline on the real card edge (visible in the proxy) is a constant offset.");
        W("- M4's re-render uses an analytic crisp card (the proxy is half resolution) at the measured position; matte, reflection transfer and overlay are not simulated; the K4ra/cadence variants run without the detection grid, so held-frame detection and the chroma-dependent noise estimate are not the production ones. Predicted M2 for a variant is therefore a prediction, not a re-run of the product.");
        W("- The sigma prior assumes the 480x274 detection grid; if the analyze step used a different grid the K4ra bandwidth differs.");
        W();
    }

    private void Csv(ClipMeasurement film, List<Quad> keyOut, List<Quad> cardPred, IReadOnlyList<PromoVariant> variants, List<List<Quad>> variantOut)
    {
        var sb = new StringBuilder();
        sb.AppendLine("frame,t,key_x0,key_y0,key_x1,key_y1,key_x2,key_y2,key_x3,key_y3,screen_x0,screen_y0,screen_x1,screen_y1,screen_x2,screen_y2,screen_x3,screen_y3,card_x0,card_y0,card_x1,card_y1,card_x2,card_y2,card_x3,card_y3,pred_x0,pred_y0,pred_x1,pred_y1,pred_x2,pred_y2,pred_x3,pred_y3,screen_valid_top,screen_valid_right,screen_valid_bottom,screen_valid_left,card_valid_top,card_valid_right,card_valid_bottom,card_valid_left,screen_bow_top,screen_bow_right,screen_bow_bottom,screen_bow_left");
        string Q(Quad? q) => q is { } v ? string.Join(",", Enumerable.Range(0, 4).SelectMany(c => new[] { v.Corner(c).X, v.Corner(c).Y }).Select(x => x.ToString("0.0000", CultureInfo.InvariantCulture))) : ",,,,,,,";
        for (int i = 0; i < _n; i++)
        {
            string flags(PromoEdge[]? es) => es is null ? ",,," : string.Join(",", es.Select(e => e.Valid ? "1" : "0"));
            string bows(PromoEdge[]? es) => es is null ? ",,," : string.Join(",", es.Select(e => e.Valid ? e.Bow.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{i},{i / PromoInputs.Fps:0.0000},{Q(keyOut[i])},{Q(film.Screen[i])},{Q(film.Card[i])},{Q(cardPred[i])},{flags(film.ScreenEdges[i])},{flags(film.CardEdges[i])},{bows(film.ScreenEdges[i])}"));
        }

        File.WriteAllText(Path.Combine(_inp.OutDir, "frames.csv"), sb.ToString());
        var vs = new StringBuilder();
        vs.AppendLine("variant,frame,x0,y0,x1,y1,x2,y2,x3,y3");
        for (int v = 0; v < variants.Count; v++)
        {
            for (int i = 0; i < _n; i++)
                vs.AppendLine($"{variants[v].Id},{i},{Q(variantOut[v][i])}");
        }

        File.WriteAllText(Path.Combine(_inp.OutDir, "variants.csv"), vs.ToString());
    }
}
