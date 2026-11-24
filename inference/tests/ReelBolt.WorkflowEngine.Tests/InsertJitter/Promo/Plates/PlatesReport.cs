#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Fmt;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

internal static class PlatesReport
{
    private const string Header =
        "| row | frames | SlipRms out / src | SlipP95 out / src | SlipMax out / src | HfRms out / src | HfMax out / src | ErrMean out / src | ErrMax out / src | abs mean out / src | abs max out / src | offset out / src |\n" +
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|";

    private const string HeaderGap =
        "| row | frames | SlipRms out / src | SlipP95 out / src | SlipMax out / src | HfRms out / src | HfMax out / src | mean margin out / src |\n" +
        "|---|---:|---:|---:|---:|---:|---:|---:|";

    private static string LineGap(RowDto r) =>
        $"| {r.Label} | {r.Frames} | {Pair(r.SlipRmsOut, r.SlipRmsSrc)} | {Pair(r.SlipP95Out, r.SlipP95Src)} | {Pair(r.SlipMaxOut, r.SlipMaxSrc)} | {Pair(r.HfRmsOut, r.HfRmsSrc)} | {Pair(r.HfMaxOut, r.HfMaxSrc)} | {Pair(r.OffsetOut, r.OffsetSrc)} |";

    private static string Line(RowDto r) =>
        $"| {r.Label} | {r.Frames} | {Pair(r.SlipRmsOut, r.SlipRmsSrc)} | {Pair(r.SlipP95Out, r.SlipP95Src)} | {Pair(r.SlipMaxOut, r.SlipMaxSrc)} | {Pair(r.HfRmsOut, r.HfRmsSrc)} | {Pair(r.HfMaxOut, r.HfMaxSrc)} | " +
        $"{Pair(r.ErrMeanOut, r.ErrMeanSrc)} | {Pair(r.ErrMaxOut, r.ErrMaxSrc)} | {Pair(r.AbsMeanOut, r.AbsMeanSrc)} | {Pair(r.AbsMaxOut, r.AbsMaxSrc)} | {Pair(r.OffsetOut, r.OffsetSrc)} |";

    public static string Render(PlatesResults r)
    {
        var sb = new StringBuilder();
        void W(string s = "") => sb.AppendLine(s);

        W($"# Raw-plate harness: label `{r.Label}`");
        W();
        W($"Commit `{r.Commit}` on `{r.Branch}`, build {r.BuildConfiguration}, generated {r.GeneratedUtc:yyyy-MM-dd HH:mm} UTC. Production sources modified vs HEAD: " +
          (r.ProductionDirty ? $"YES ({r.DirtyFiles.Length} files: {string.Join(", ", r.DirtyFiles.Take(8))})" : "no") + ".");
        W();
        W($"Units: plate source px (1344x768) and OUTPUT px of the staging canvas (1920x1080 cover crop, x{F(r.OutPerSrc, "0.000000")}), printed `out / src` as in the T-007 report. " +
          "Slip = frame-to-frame change of the error (the wobble); Hf = error minus its centred 1 s mean; edge rows' ErrMean/ErrMax are offset-removed (T-007 convention), `abs` columns keep the constant offset " +
          "(the flat quad against the bowed plate edge, rounded corners); quad rows are absolute corner errors.");
        W();
        W("Rerun: `" + r.Rerun + "`");
        W();
        W("What runs: the REAL `VideoAnalyzeStepExecutor` (real ffprobe/ffmpeg, real `FfmpegFrameGridSampler`, real `ChromaQuadTracker`) on the raw plate with the staging step config; the keyframes are read from the analysis artifact the step writes. " +
          "Nothing of the insert pass is mirrored in the harness, so whatever the step does (grid decode flags, smoothing, tracker options) is what is scored.");
        W();
        W("Pseudo-truth: the plate's own green edges, per frame, by the T-007 integrating meter on a full-chroma native-resolution decode (`" + r.MeterDecodeFilter + "`), dominance `max(0, g - max(r, b))`, seeded from the staging artifact (fixed, so the truth never depends on the code under test).");
        W();

        // --- 1 reproduction
        W("## 1. Reproduction of the staging artifacts");
        W();
        W("| plate | staging artifact | step | frames (run / staging) | corners max / RMS (src px) | surfaceCorners max / RMS (src px) | frames differing > 1e-6 px | field differences | identical |");
        W("|---|---|---|---:|---:|---:|---:|---|---|");
        foreach (PlateResult p in r.Plates)
        {
            ReproDto d = p.Reproduction;
            W($"| {p.Id} | {p.StagingArtifact} | {p.StepStatus} | {d.FramesNew} / {d.FramesStaging} | {F(d.CornersMaxSrc, "0.000000")} / {F(d.CornersRmsSrc, "0.000000")} | {F(d.SurfaceMaxSrc, "0.000000")} / {F(d.SurfaceRmsSrc, "0.000000")} | {d.FramesOver1e6} | {(d.FieldDiffs.Count == 0 ? "none" : string.Join("; ", d.FieldDiffs))} | {(d.Identical ? "yes" : "NO")} |");
        }

        foreach (PlateResult p in r.Plates.Where(p => p.TrackerError is not null))
            W($"{p.Id}: {p.TrackerError}");
        W();

        // --- 2 baseline
        W("## 2. Keyframes against the plate's own measured edges (M1, pseudo-truth)");
        W();
        foreach (PlateResult p in r.Plates)
        {
            W($"### {p.Id}: {p.Film}");
            W();
            W($"The meter found all four plate edges clean on {p.MeterFrames} of {p.Reproduction.FramesStaging} frames (elsewhere an edge is outside the frame, e.g. Film A's top edge late in the push-in); every row below is scored on those frames only. Region facts of this run: " +
              string.Join(", ", p.Region.Select(kv => $"{kv.Key} {F(kv.Value, "0.####")}")) + ".");
            W();
            foreach (ScoreDto? s in new[] { p.Shipped, p.Surface, p.Corners })
            {
                if (s is null)
                    continue;
                W($"**{s.Label}**");
                W();
                W(Header);
                W(Line(s.Quad with { Label = "quad corners" }));
                W(Line(s.EdgesPooled with { Label = "4 edges pooled" }));
                W(Line(s.HorizontalPositionEdges with { Label = "left+right edges (horizontal position)" }));
                W(Line(s.VerticalPositionEdges with { Label = "top+bottom edges (vertical position)" }));
                foreach (RowDto e in s.Edges)
                    W(Line(e));
                W();
                W($"Quad slip by axis (RMS over corners and consecutive frames, out / src px): horizontal {Pair(s.QuadSlipXOut, s.QuadSlipXSrc)}, vertical {Pair(s.QuadSlipYOut, s.QuadSlipYSrc)}.");
                W();
            }
        }

        // --- 3 no truth
        W("## 3. Leave-one-out residual of the keyframes (no truth; T-007 definition)");
        W();
        W("Baseline report values for the shipped tracks: Film A 0.994 RMS / 1.976 p95 / second difference 1.569; Film B 0.873 / 1.981 / 1.762 (source px).");
        W();
        W("| plate | track | N | ResidualRms out / src | ResidualP95 src | SecondDiffRms src | PeakToPeak src |");
        W("|---|---|---:|---:|---:|---:|---:|");
        foreach (PlateResult p in r.Plates)
        {
            if (p.StagingNoTruth is { } n0)
                W($"| {p.Id} | shipped (staging artifact) | {n0.N} | {Pair(n0.ResidualRmsOut, n0.ResidualRmsSrc)} | {F(n0.ResidualP95Src)} | {F(n0.SecondDiffRmsSrc)} | {F(n0.PeakToPeakSrc, "0.00")} |");
            foreach (ScoreDto? s in new[] { p.Surface, p.Corners })
            {
                if (s is { NoTruth: { } n })
                    W($"| {p.Id} | {s.Label} | {n.N} | {Pair(n.ResidualRmsOut, n.ResidualRmsSrc)} | {F(n.ResidualP95Src)} | {F(n.SecondDiffRmsSrc)} | {F(n.PeakToPeakSrc, "0.00")} |");
            }
        }

        W();

        // --- 4 floor
        W("## 4. The meter's floor (known-truth synthetic plate at the plate's geometry, through x264, same decode and meter)");
        W();
        W("Truth = the staging keyframes heavily smoothed (fixed 25-frame local quadratic); rows are measured minus truth, so every SlipRms here is pure measurement noise. The synthetic plate is a flat anti-aliased quad of the plate's own green on a dark bezel (straight edges, no bow, no texture: it encodes at 35-66 kbit/s against the real plates' 650-940 kbit/s, so the codec noise of the real plate is only partly represented; the `harsh` row, with more blur and noise at a higher CRF, is the conservative one). The corners' floor is higher than the edges' because a corner is the intersection of two extrapolated lines.");
        W();
        W(Header);
        foreach (PlateResult p in r.Plates)
        {
            foreach (FloorDto f in p.Floors)
            {
                W(Line(f.VersusTruth.Quad with { Label = $"{p.Id} {f.Id} quad ({f.Spec})" }));
                W(Line(f.VersusTruth.EdgesPooled with { Label = $"{p.Id} {f.Id} 4 edges pooled" }));
            }
        }

        W();
        W("| plate | meter's no-truth residual on the REAL plate's measured quad (upper bound of the noise; includes the plate's own non-quadratic motion), src px | synthetic nominal / harsh, src px |");
        W("|---|---:|---|");
        foreach (PlateResult p in r.Plates)
            W($"| {p.Id} | {F(p.MeterResidualSrc)} ({p.MeterFrames} frames) | {string.Join(" / ", p.Floors.Select(f => F(f.MeasuredResidualSrc)))} |");
        W();

        // --- 5 wobble
        W("## 5. Film A: predicted perceived wobble (T-007 instrument, output px)");
        W();
        W("The content card (the chat window's outer rectangle) is predicted through the track's plate quad and compared with the film's measured screen edges along each edge's inward normal (T-007 M2: the card's margin breathing). `ideal` = the render adds nothing; `corrected` = ideal plus the film's own card-vs-keyframes error (pessimistic bound); `vs plate` = the same prediction against the RAW PLATE's measured edges instead of the film's. T-007's shipped values: ideal 0.663, corrected 0.711 (film measured 0.711).");
        W();
        W(HeaderGap);
        PlateResult? a = r.Plates.FirstOrDefault(p => p.Id == "filmA");
        if (r.FilmMeasured is { } fm)
            W(LineGap(fm.Ideal with { Label = "film, measured (T-007 M2 headline)" }));
        if (a?.WobbleShipped is { } ws)
        {
            W(LineGap(ws.Ideal with { Label = "shipped keyframes: predicted, ideal render" }));
            W(LineGap(ws.Corrected with { Label = "shipped keyframes: predicted, M3-corrected" }));
            W(LineGap(ws.VersusPlate with { Label = "shipped keyframes: predicted vs raw plate edges" }));
        }

        if (a?.WobbleNew is { } wn)
        {
            W(LineGap(wn.Ideal with { Label = $"{r.Label}: predicted, ideal render" }));
            W(LineGap(wn.Corrected with { Label = $"{r.Label}: predicted, M3-corrected" }));
            W(LineGap(wn.VersusPlate with { Label = $"{r.Label}: predicted vs raw plate edges" }));
        }

        W();

        // --- 6 cost
        W("## 6. Cost of the insert pass (inside the real step: grid decode + tracker + everything the step does between its two insert progress reports)");
        W();
        W("| plate | build | wall s | decode (ffmpeg) wall s | tracker wall s | managed CPU s | ffmpeg CPU s (children, whole step) | RSS before MB | peak RSS MB | grid MB |");
        W("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (PlateResult p in r.Plates)
        {
            CostDto c = p.Cost;
            W(c.Measured
                ? $"| {p.Id} | {c.BuildConfiguration} | {F(c.WallSec, "0.0")} | {F(c.DecodeWallSec, "0.00")} | {F(c.TrackerWallSec, "0.0")} | {F(c.SelfCpuSec, "0.0")} | {F(c.ChildCpuSec, "0.0")} | {F(c.RssBaselineMb, "0")} | {F(c.RssPeakMb, "0")} | {F(c.GridMb, "0")} |"
                : $"| {p.Id} | {c.BuildConfiguration} | not measured | | | | | | | |");
        }

        W();
        W("Wall time is on a shared 32-core box with other jobs running, so treat it as an upper bound; managed CPU seconds are the stable figure. Peak RSS is the process's resident high-water mark after a forced GC at the start of the pass (the grid, the tracker's per-frame buffers and everything else the step holds are in it). The ffmpeg CPU figure covers every ffmpeg the step ran (silence, shots, audio, two grids), not only the insert grid.");
        W();

        // --- 7 step facts
        W("## 7. What the step actually did for the insert grid");
        W();
        foreach (PlateResult p in r.Plates)
        {
            W($"- {p.Id}: insert-grid ffmpeg filter `{p.InsertGridDecodeFilter}`");
            foreach (string s in p.SamplerCalls)
                W($"  - sampler call: {s}");
        }

        W();
        W("Staging step config used (sources reduced to the plate): `" + r.StepConfig + "`");
        W();
        return sb.ToString();
    }
}
