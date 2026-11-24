#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="PsnrComparer"/> — real ffmpeg <c>psnr</c> filter measurements over the harness's own
/// rendered outputs.
/// </summary>
[Collection(GoldenParityCollection.Name)]
public class PsnrComparerTests
{
    private readonly GoldenParityTestEnvironment _environment;

    public PsnrComparerTests(GoldenParityTestEnvironment environment) => _environment = environment;

    private PsnrComparer Comparer => new(_environment.Runner, _environment.ToolTimeout);

    [FfmpegToolsFact]
    public async Task A_file_compared_with_itself_reports_infinite_psnr_not_a_large_number()
    {
        GoldenParityPairResult p1 = _environment.Verdict.Pair("P1");

        GoldenParityPsnrMeasurement measurement = await Comparer.CompareAsync(
            p1.ReferenceOutputPath, p1.ReferenceOutputPath, CancellationToken.None);

        measurement.AverageDb.Should().Be(double.PositiveInfinity);
        measurement.Argv.Should().Contain("psnr");
    }

    [FfmpegToolsFact]
    public async Task A_re_encoded_pair_measures_a_finite_psnr_above_the_threshold()
    {
        GoldenParityPairResult p2 = _environment.Verdict.Pair("P2");

        GoldenParityPsnrMeasurement measurement = await Comparer.CompareAsync(
            p2.ReferenceOutputPath, p2.CandidateOutputPath, CancellationToken.None);

        double.IsFinite(measurement.AverageDb).Should().BeTrue();
        measurement.AverageDb.Should().BeGreaterThan(_environment.Verdict.Thresholds.MinPsnrDb);

        // The per-plane and worst-frame figures are reported so a single bad frame cannot hide
        // behind a good average.
        measurement.YDb.Should().NotBeNull();
        measurement.MinDb.Should().NotBeNull();
        measurement.MinDb!.Value.Should().BeLessThanOrEqualTo(measurement.AverageDb);
    }

    [FfmpegToolsFact]
    public async Task A_one_frame_edit_measures_a_finite_psnr_below_the_threshold()
    {
        GoldenParityPairResult p3 = _environment.Verdict.Pair("P3");

        GoldenParityPsnrMeasurement measurement = await Comparer.CompareAsync(
            p3.ReferenceOutputPath, p3.CandidateOutputPath, CancellationToken.None);

        double.IsFinite(measurement.AverageDb).Should().BeTrue();
        measurement.AverageDb.Should().BeLessThan(_environment.Verdict.Thresholds.MinPsnrDb);
    }

    [FfmpegToolsFact]
    public async Task PSNR_is_the_quantity_that_separates_the_two_paths_from_a_real_edit()
    {
        GoldenParityVerdict verdict = _environment.Verdict;

        // Measured, side by side, in one test: this ordering is the whole calibration.
        verdict.Pair("P3").Psnr.AverageDb.Should().BeLessThan(verdict.Thresholds.MinPsnrDb);
        verdict.Thresholds.MinPsnrDb.Should().BeLessThan(verdict.Pair("P2").Psnr.AverageDb);
        verdict.Pair("P1").Psnr.AverageDb.Should().Be(double.PositiveInfinity);

        await Task.CompletedTask;
    }
}
