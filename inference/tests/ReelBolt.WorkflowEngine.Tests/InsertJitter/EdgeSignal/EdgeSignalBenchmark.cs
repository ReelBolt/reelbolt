#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>
/// T-003 (initiative insert-tracking-smoothness): do detection-side changes remove the 4:2:0 chroma staircase from the edge
/// signal? Gated by <see cref="JitterBenchFactAttribute"/> (never in CI). Each part writes a section of
/// <c>/tmp/insert-tracking/edge-signal/report.md</c>; <c>REELBOLT_EDGE_SCENES</c> restricts the scenes,
/// <c>REELBOLT_EDGE_PARALLEL</c> sets the scene parallelism (default 4). Parts: <c>E1_decode_chroma_handling</c>,
/// <c>E1_flag_ablation</c>, <c>E2_luma_guided_crossing</c>, <c>E3_hires_second_look</c>, <c>E4_per_track_constants</c>,
/// <c>Combinations_and_gray_plane</c>, <c>Guarded_second_look</c>, and (separately, run alone) <c>EdgeCostBenchmark</c>.
/// The 10 catalogue clips are read from <c>/tmp/insert-tracking/edge-signal/clips</c> (symlinks to the benchmark's clips;
/// anything missing is rendered there). Rerun from <c>inference/</c>:
/// <c>REELBOLT_JITTER_BENCH=1 dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter "FullyQualifiedName~InsertJitter.EdgeSignal"</c>.
/// </summary>
public class EdgeSignalBenchmark
{
    private readonly ITestOutputHelper _output;
    private readonly object _lock = new();

    public EdgeSignalBenchmark(ITestOutputHelper output) => _output = output;

    private void Log(string m)
    {
        lock (_lock)
            _output.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    }

    private static int Par => int.TryParse(Environment.GetEnvironmentVariable("REELBOLT_EDGE_PARALLEL"), out int p) && p > 0 ? p : 4;

    // ---- candidate catalogues (shared by parts and by the cost pass) -----------------------------

    internal static readonly Candidate Baseline = new("base", "production: fps, scale area, rgb24; production S1 fit");

    /// <summary>Control: the prototype fit in chroma mode must reproduce the production S1/S4 exactly.</summary>
    internal static readonly Candidate Control = new("ctl-chroma", "prototype fit, chroma crossing, integer extreme seeds (must equal base)", S1Fit: new EdgeFitOptions());

    internal static IReadOnlyList<Candidate> E1Candidates() =>
    [
        Baseline,
        Control,
        .. EdgeDecode.All.Where(v => v.Id != EdgeDecode.BaseId).Select(v => new Candidate("dec-" + v.Id, v.Label, v.Id))
    ];

    [JitterBenchFact]
    public void E1_decode_chroma_handling()
    {
        IReadOnlyList<Candidate> cands = E1Candidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section(
            "E1 decode chroma handling (production S1 fit, only the decode argv changes)",
            "Same clips, same tracker, same fit: only the ffmpeg `-vf` chain that produces the RGB grid differs. `ctl-chroma` is the prototype fit in chroma mode: it must equal `base` (control).",
            cells, cands));
        sb.AppendLine(DecodeCostTable(cells, cands));
        EdgeSupport.WriteSection("10-e1-decode", sb.ToString());
    }

    // ---- E2: luma-guided crossing ------------------------------------------------------------------

    private const string DecA = "e1a";
    private const string DecB = "e1b-bilinear";

    internal static Candidate Dec(string id) => new("dec-" + id, EdgeDecode.Get(id).Label, id);

    internal static IReadOnlyList<Candidate> E2Candidates() =>
    [
        Baseline,
        Dec(DecA),
        Dec(DecB),
        new("e2-half", "luma half-level crossing, band 2, contrast>=16, BT.601", S1Fit: new EdgeFitOptions(Crossing.LumaHalf)),
        new("e2-int", "luma coverage-integral crossing, band 2, contrast>=16, BT.601", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral)),
        new("e2-int-709", "as e2-int with BT.709 luma weights (wrong matrix for these clips)", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral), Matrix: LumaMatrix.Bt709),
        new("e2-int-c8", "as e2-int, contrast threshold 8", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral, MinContrast: 8)),
        new("e2-int-c32", "as e2-int, contrast threshold 32", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral, MinContrast: 32)),
        new("e2-int-b3", "as e2-int, band 3", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral, Band: 3)),
        new("e2-int-pe", "as e2-int, per-edge fallback (no luma/chroma mixing on one line)", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral, PerEdgeFallback: true)),
        new("e2a-int", "e1a decode + e2-int", DecA, new EdgeFitOptions(Crossing.LumaIntegral)),
        new("e2b-int", "e1b-bilinear decode + e2-int", DecB, new EdgeFitOptions(Crossing.LumaIntegral)),
        new("e2b-half", "e1b-bilinear decode + e2-half", DecB, new EdgeFitOptions(Crossing.LumaHalf))
    ];

    [JitterBenchFact]
    public void E2_luma_guided_crossing()
    {
        IReadOnlyList<Candidate> cands = E2Candidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section(
            "E2 luma-guided crossing (prototype copy of the line fit; chroma dominance still finds each edge, luma places it)",
            "Chroma dominance locates each probe's edge exactly as production does; the sub-pixel crossing is then read from luma (Y recomputed from the decoded RGB grid) inside a band around it, with a per-probe fallback to the chroma crossing when the luma step is under the contrast threshold.",
            cells, cands));
        sb.AppendLine(LumaUseTable(cells, cands));
        EdgeSupport.WriteSection("20-e2-luma", sb.ToString());
    }

    internal static string LumaUseTable(IReadOnlyList<EdgeSupport.Cell> cells, IReadOnlyList<Candidate> cands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#### Share of probes placed on luma (rest fell back to chroma) and mean luma-minus-chroma crossing shift, grid px along the outward normal (1 grid px = ~2.8 source px on the 1344 scenes)");
        sb.AppendLine();
        string[] scenes = EdgeSupport.Scenes.Select(s => s.Name).Where(n => cells.Any(c => c.Scene == n)).ToArray();
        sb.AppendLine("| candidate | " + string.Join(" | ", scenes) + " |");
        sb.AppendLine("|---|" + string.Join("|", scenes.Select(_ => "---:")) + "|");
        foreach (Candidate c in cands.Where(c => c.S1Fit is { Crossing: not Crossing.Chroma }))
        {
            sb.Append($"| {c.Id} |");
            foreach (string s in scenes)
            {
                var cell = cells.FirstOrDefault(x => x.Scene == s && x.Candidate == c.Id && x.Stage == "S4");
                sb.Append(' ').Append(cell is null ? "." : string.Create(CultureInfo.InvariantCulture, $"{cell.LumaFraction:0.00} / {cell.MeanShift:+0.00;-0.00}")).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ---- E4: per-track constants -------------------------------------------------------------------

    internal static IReadOnlyList<Candidate> E4Candidates() =>
    [
        Baseline,
        new("e4-level", "chroma crossing, plate level = track median", S1Fit: new EdgeFitOptions(), TrackLevel: true),
        new("e4-seed", "chroma crossing, probes seeded from the previous frame's fit", S1Fit: new EdgeFitOptions(), Seed: SeedKind.Previous),
        new("e4-both", "chroma crossing, track-median level + previous-frame seeds", S1Fit: new EdgeFitOptions(), TrackLevel: true, Seed: SeedKind.Previous),
        Dec(DecB),
        new("e4b-both", "e1b-bilinear decode + e4-both", DecB, new EdgeFitOptions(), TrackLevel: true, Seed: SeedKind.Previous),
        new("e4-int-both", "e2-int + e4-both (base decode)", S1Fit: new EdgeFitOptions(Crossing.LumaIntegral), TrackLevel: true, Seed: SeedKind.Previous)
    ];

    [JitterBenchFact]
    public void E4_per_track_constants()
    {
        IReadOnlyList<Candidate> cands = E4Candidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        EdgeSupport.WriteSection("40-e4-track", EdgeSupport.Section(
            "E4 per-track constants",
            "`e4-level`: the dominance threshold is half the TRACK's median plate level instead of each frame's own median. `e4-seed`: the probes of frame f are seeded from frame f-1's fitted corners (first frame / failed fit: the integer extreme corners).",
            cells, cands));
    }

    // ---- E3: hi-resolution second look -------------------------------------------------------------

    internal static IReadOnlyList<Candidate> E3Candidates() =>
    [
        Baseline,
        Dec(DecB),
        new("e3-2x-int", "production S1 + luma second look at 2x the grid (coverage integral, band 2, seeds = the S2 corners)", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaIntegral)),
        new("e3-nat-int", "production S1 + luma second look at native resolution", HiRes: HiResKind.GrayNative, HiResFit: new EdgeFitOptions(Crossing.LumaIntegral)),
        new("e3-2x-half", "as e3-2x-int with the half-level crossing", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaHalf)),
        new("e3-nat-half", "as e3-nat-int with the half-level crossing", HiRes: HiResKind.GrayNative, HiResFit: new EdgeFitOptions(Crossing.LumaHalf)),
        new("e3b-nat-int", "e1b-bilinear decode + native luma second look", DecB, HiRes: HiResKind.GrayNative, HiResFit: new EdgeFitOptions(Crossing.LumaIntegral))
    ];

    [JitterBenchFact]
    public void E3_hires_second_look()
    {
        IReadOnlyList<Candidate> cands = E3Candidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section(
            "E3 re-fit at higher resolution (luma, narrow band around the grid fit)",
            "After the temporal edge refiner, each measured frame's four lines are re-located on a second, streamed luma decode (2x the grid, or the source's native size) inside +-2 grid px of the grid fit. Probes without luma contrast are dropped; an edge with under 40% surviving probes keeps its grid line.",
            cells, cands));
        sb.AppendLine(HiResUseTable(cells, cands));
        EdgeSupport.WriteSection("30-e3-hires", sb.ToString());
    }

    internal static string HiResUseTable(IReadOnlyList<EdgeSupport.Cell> cells, IReadOnlyList<Candidate> cands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#### Second look: wall ms per clip (fitting only, excluding the decode; measured under contention) / share of probes with usable luma / mean shift of the line vs the grid fit, grid px outward");
        sb.AppendLine();
        string[] scenes = EdgeSupport.Scenes.Select(s => s.Name).Where(n => cells.Any(c => c.Scene == n)).ToArray();
        sb.AppendLine("| candidate | " + string.Join(" | ", scenes) + " |");
        sb.AppendLine("|---|" + string.Join("|", scenes.Select(_ => "---:")) + "|");
        foreach (Candidate c in cands.Where(c => c.HiRes != HiResKind.None))
        {
            sb.Append($"| {c.Id} |");
            foreach (string s in scenes)
            {
                var cell = cells.FirstOrDefault(x => x.Scene == s && x.Candidate == c.Id && x.Stage == "S4");
                sb.Append(' ').Append(cell is null ? "." : string.Create(CultureInfo.InvariantCulture, $"{cell.HiResMs:0} / {cell.HiLumaFraction:0.00} / {cell.HiMeanShift:+0.00;-0.00}")).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ---- combinations and the luma-from-gray-plane family ---------------------------------------------

    private static readonly EdgeFitOptions Half = new(Crossing.LumaHalf);

    internal static IReadOnlyList<Candidate> ComboCandidates() =>
    [
        Baseline,
        Dec(DecA),
        Dec(DecB),
        new("e3g-half", "production decode + second look on the decoder's gray plane at the GRID size (luma signal only, no extra resolution)", HiRes: HiResKind.GrayGrid, HiResFit: Half),
        new("e3-2x-half", "production decode + 2x-grid luma second look, half-level crossing", HiRes: HiResKind.Gray2x, HiResFit: Half),
        new("e3-nat-half", "production decode + native luma second look, half-level crossing", HiRes: HiResKind.GrayNative, HiResFit: Half),
        new("e3-2x-half-c8", "as e3-2x-half, contrast threshold 8", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaHalf, MinContrast: 8)),
        new("e3-2x-half-c32", "as e3-2x-half, contrast threshold 32", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaHalf, MinContrast: 32)),
        new("e3-2x-half-b1.5", "as e3-2x-half, band 1.5", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaHalf, Band: 1.5)),
        new("e3-2x-half-b1", "as e3-2x-half, band 1.0", HiRes: HiResKind.Gray2x, HiResFit: new EdgeFitOptions(Crossing.LumaHalf, Band: 1.0)),
        new("e3a-2x-half", "e1a decode + e3-2x-half", DecA, HiRes: HiResKind.Gray2x, HiResFit: Half),
        new("e3b-g-half", "e1b-bilinear decode + grid gray-plane second look", DecB, HiRes: HiResKind.GrayGrid, HiResFit: Half),
        new("e3b-2x-half", "e1b-bilinear decode + e3-2x-half", DecB, HiRes: HiResKind.Gray2x, HiResFit: Half),
        new("e3b-nat-half", "e1b-bilinear decode + e3-nat-half", DecB, HiRes: HiResKind.GrayNative, HiResFit: Half),
        new("e2g-half", "S1 luma from the gray plane, half-level crossing, production decode", S1Fit: Half, GrayLuma: true),
        new("e2bg-half", "S1 luma from the gray plane, half-level crossing, e1b-bilinear decode", DecB, Half, GrayLuma: true),
        new("e2bg-half-b1.5", "as e2bg-half, band 1.5", DecB, new EdgeFitOptions(Crossing.LumaHalf, Band: 1.5), GrayLuma: true)
    ];

    [JitterBenchFact]
    public void Combinations_and_gray_plane()
    {
        IReadOnlyList<Candidate> cands = ComboCandidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section(
            "Combinations and the gray-plane family",
            "Half-level crossing only (the coverage integral failed the occlusion scene in E2/E3). `GrayLuma`/`GrayGrid` read the decoder's own luma plane (a second 8-bit output of the same decode) instead of luma recomputed from the clipped RGB grid. `e3*` candidates are second looks after the temporal edge refiner (they change S3/S4, not S1).",
            cells, cands));
        sb.AppendLine(LumaUseTable(cells, cands));
        sb.AppendLine(HiResUseTable(cells, cands));
        EdgeSupport.WriteSection("50-combos", sb.ToString());
    }

    // ---- guard for the hi-resolution second look ------------------------------------------------------

    private static EdgeFitOptions Guarded(double contrast = 16, double band = 2) =>
        new(Crossing.LumaHalf, Band: band, MinContrast: contrast, MaxRefineShift: 1.5, RequireSpread: true);

    internal static IReadOnlyList<Candidate> GuardCandidates() =>
    [
        Baseline,
        Dec(DecA),
        Dec(DecB),
        new("g-2x", "production decode + 2x luma second look, half-level, guarded (corner shift <= 1.5 grid px, probes spread over >= 3 quarters)", HiRes: HiResKind.Gray2x, HiResFit: Guarded()),
        new("g-nat", "production decode + native luma second look, guarded", HiRes: HiResKind.GrayNative, HiResFit: Guarded()),
        new("g-2x-c8", "as g-2x, contrast threshold 8", HiRes: HiResKind.Gray2x, HiResFit: Guarded(contrast: 8)),
        new("ga-2x", "e1a decode + g-2x", DecA, HiRes: HiResKind.Gray2x, HiResFit: Guarded()),
        new("ga-nat", "e1a decode + g-nat", DecA, HiRes: HiResKind.GrayNative, HiResFit: Guarded()),
        new("gb-2x", "e1b-bilinear decode + g-2x", DecB, HiRes: HiResKind.Gray2x, HiResFit: Guarded()),
        new("gb-nat", "e1b-bilinear decode + g-nat", DecB, HiRes: HiResKind.GrayNative, HiResFit: Guarded()),
        new("gb-2x-b1", "e1b-bilinear decode + g-2x, band 1", DecB, HiRes: HiResKind.Gray2x, HiResFit: Guarded(band: 1))
    ];

    [JitterBenchFact]
    public void Guarded_second_look()
    {
        IReadOnlyList<Candidate> cands = GuardCandidates();
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section(
            "Guarded hi-resolution second look",
            "The unguarded second look (E3) failed the occlusion scene at contrast 8 and with the e1a decode (a 47 px corner error: a clustered minority of probes tilted an edge). The guarded fit rejects a re-fit whose corner moves more than 1.5 grid px from its seed and keeps the seed line of an edge whose surviving probes do not span three of its four quarters.",
            cells, cands));
        sb.AppendLine(HiResUseTable(cells, cands));
        EdgeSupport.WriteSection("60-guarded", sb.ToString());
    }

    [JitterBenchFact]
    public void E1_flag_ablation()
    {
        IReadOnlyList<Candidate> cands =
        [
            Baseline,
            Dec("e1a-int"),
            Dec("e1a-intinp"),
            Dec("e1a"),
            Dec("e1b-bilinear-fast"),
            Dec("e1b-bilinear")
        ];
        var cells = EdgeSupport.RunAll(cands, Par, Log);
        var sb = new StringBuilder();
        sb.AppendLine(EdgeSupport.Section("E1 flag ablation (which swscale flag removes the staircase)", "Production S1 fit; only the grid decode chain changes.", cells, cands));
        sb.AppendLine(DecodeCostTable(cells, cands));
        EdgeSupport.WriteSection("15-e1-ablation", sb.ToString());
    }

    internal static string DecodeCostTable(IReadOnlyList<EdgeSupport.Cell> cells, IReadOnlyList<Candidate> cands)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#### Decode CPU seconds per clip (ffmpeg `-benchmark` utime+stime, measured while sibling jobs ran; indicative)");
        sb.AppendLine();
        string[] scenes = EdgeSupport.Scenes.Select(s => s.Name).Where(n => cells.Any(c => c.Scene == n)).ToArray();
        sb.AppendLine("| decode | " + string.Join(" | ", scenes) + " |");
        sb.AppendLine("|---|" + string.Join("|", scenes.Select(_ => "---:")) + "|");
        foreach (Candidate c in cands.GroupBy(c => c.DecodeId).Select(g => g.First()))
        {
            sb.Append($"| {c.DecodeId} |");
            foreach (string s in scenes)
            {
                var cell = cells.FirstOrDefault(x => x.Scene == s && x.Candidate == c.Id && x.Stage == "S4");
                sb.Append(' ').Append(cell is null ? "." : cell.DecodeCpuSeconds.ToString("0.00", CultureInfo.InvariantCulture)).Append(" |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
