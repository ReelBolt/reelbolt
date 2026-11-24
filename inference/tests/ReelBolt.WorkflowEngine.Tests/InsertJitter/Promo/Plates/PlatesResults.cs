#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>One scored row, all quantities in plate source px and output px of the staging 1920x1080 canvas side by side (out = src x <see cref="PlateScorer.OutPerSrc"/>).</summary>
internal sealed record RowDto(
    string Label, int Frames,
    double SlipRmsSrc, double SlipRmsOut, double SlipP95Src, double SlipP95Out,
    double HfRmsSrc, double HfRmsOut, double HfMaxSrc, double HfMaxOut,
    double ErrMeanSrc, double ErrMeanOut, double ErrMaxSrc, double ErrMaxOut,
    double AbsMeanSrc, double AbsMeanOut, double AbsMaxSrc, double AbsMaxOut,
    double OffsetSrc, double OffsetOut, double SlipMaxSrc, double SlipMaxOut, double LagLsFrames)
{
    /// <param name="toSrc">Multiplier from the row's own unit to source px (1 for rows scored in plate px, 1/OutPerSrc for the T-007 film instrument which works in output px).</param>
    public static RowDto From(PromoScore.Row r, double toSrc = 1.0, double absMean = double.NaN, double absMax = double.NaN)
    {
        double o = PlateScorer.OutPerSrc;
        double S(double v) => v * toSrc;
        double O(double v) => v * toSrc * o;
        JitterStats? s = r.Stats;
        double nan = double.NaN;
        return new RowDto(
            r.Label, r.Frames,
            S(s?.SlipRms ?? nan), O(s?.SlipRms ?? nan), S(s?.SlipP95 ?? nan), O(s?.SlipP95 ?? nan),
            S(s?.HfRms ?? nan), O(s?.HfRms ?? nan), S(r.HfMax), O(r.HfMax),
            S(s?.ErrMean ?? nan), O(s?.ErrMean ?? nan), S(s?.ErrMax ?? nan), O(s?.ErrMax ?? nan),
            S(absMean), O(absMean), S(absMax), O(absMax),
            S(r.OffsetMean), O(r.OffsetMean), S(r.SlipMax), O(r.SlipMax), s?.LagLsFrames ?? nan);
    }

    public static RowDto From(ScoredRow r) => From(r.Row, 1.0, r.AbsMean, r.AbsMax);
}

internal sealed record NoTruthDto(int N, double ResidualRmsSrc, double ResidualRmsOut, double ResidualP95Src, double SecondDiffRmsSrc, double PeakToPeakSrc)
{
    public static NoTruthDto From(JitterStats s) =>
        new(s.N, s.ResidualRms, s.ResidualRms * PlateScorer.OutPerSrc, s.ResidualP95, s.SecondDiffRms, s.PeakToPeak);
}

internal sealed record ScoreDto(
    string Label, int Frames, RowDto Quad, RowDto[] Edges, RowDto EdgesPooled, RowDto HorizontalPositionEdges, RowDto VerticalPositionEdges,
    double QuadSlipXSrc, double QuadSlipYSrc, double QuadSlipXOut, double QuadSlipYOut, NoTruthDto NoTruth)
{
    public static ScoreDto From(TrackScore t) => new(
        t.Label, t.Frames, RowDto.From(t.Quad), t.Edges.Select(RowDto.From).ToArray(), RowDto.From(t.EdgesPooled),
        RowDto.From(t.VerticalEdges), RowDto.From(t.HorizontalEdges),
        t.QuadSlipX, t.QuadSlipY, t.QuadSlipX * PlateScorer.OutPerSrc, t.QuadSlipY * PlateScorer.OutPerSrc, NoTruthDto.From(t.NoTruth));
}

internal sealed record WobbleDto(RowDto Ideal, RowDto Corrected, RowDto VersusPlate);

internal sealed record FloorDto(string Id, string Label, string Spec, ScoreDto VersusTruth, int MeasuredFrames, double MeasuredResidualSrc);

internal sealed record CostDto(
    bool Measured, double WallSec, double DecodeWallSec, double TrackerWallSec, double SelfCpuSec, double ChildCpuSec,
    double RssBaselineMb, double RssPeakMb, double GridMb, string BuildConfiguration);

internal sealed record PlateResult(
    string Id, string Film, string PlateFile, string StagingArtifact,
    string StepStatus, string? StepError, ReproDto Reproduction, string? TrackerError,
    ScoreDto? Shipped, ScoreDto? Surface, ScoreDto? Corners,
    NoTruthDto? StagingNoTruth, int MeterFrames, double MeterResidualSrc,
    IReadOnlyList<FloorDto> Floors, WobbleDto? WobbleShipped, WobbleDto? WobbleNew, CostDto Cost,
    string InsertGridDecodeFilter, IReadOnlyList<string> SamplerCalls, IReadOnlyList<string> FfmpegCalls, IReadOnlyDictionary<string, double> Region);

internal sealed record ReproDto(int FramesNew, int FramesStaging, double CornersMaxSrc, double CornersRmsSrc, double SurfaceMaxSrc, double SurfaceRmsSrc, int FramesOver1e6, IReadOnlyList<string> FieldDiffs, bool Identical)
{
    public static ReproDto From(ReproResult r) => new(r.FramesA, r.FramesB, r.CornersMax, r.CornersRms, r.SurfaceMax, r.SurfaceRms, r.FramesOverTolerance, r.FieldDiffs, r.Identical);
}

internal sealed record PlatesResults(
    string Label, string Commit, bool ProductionDirty, string[] DirtyFiles, string Branch, string BuildConfiguration, DateTime GeneratedUtc,
    string MeterDecodeFilter, string MeterOptions, string StepConfig, double OutPerSrc, IReadOnlyList<PlateResult> Plates, WobbleDto? FilmMeasured, string Rerun);

internal static class Fmt
{
    public static string F(double v, string f = "0.000") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    /// <summary>"out / src" like the T-007 report.</summary>
    public static string Pair(double outV, double srcV) => double.IsNaN(outV) ? "-" : $"{F(outV)} / {F(srcV)}";
}
