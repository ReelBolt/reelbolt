#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="AudioRmsComparer"/> — the pure level differencing, and real extractions of the
/// harness's own outputs.
/// </summary>
/// <remarks>
/// The pure facts build <see cref="WavRmsSampler.RmsWindow"/> values directly, which is the same
/// shape the production sampler produces; the real facts reuse the harness's rendered files.
/// </remarks>
[Collection(GoldenParityCollection.Name)]
public class AudioRmsComparerTests
{
    private readonly GoldenParityTestEnvironment _environment;

    public AudioRmsComparerTests(GoldenParityTestEnvironment environment) => _environment = environment;

    private AudioRmsComparer Comparer => new(_environment.Runner, _environment.ToolTimeout);

    // -------------------------------------------------------------------------------------------
    // The pure differencing.
    // -------------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void Equal_levels_difference_to_zero()
    {
        GoldenParityAudioRmsMeasurement difference = AudioRmsComparer.Difference(
            Extraction(-20.0, -20.0, -20.0),
            Extraction(-20.0, -20.0, -20.0));

        difference.OverallLevelDiffDb.Should().Be(0.0);
        difference.MaxWindowDiffDb.Should().Be(0.0);
        difference.ComparedWindows.Should().Be(3);
    }

    [FfmpegToolsFact]
    public void A_uniform_level_divergence_is_reported_by_both_figures()
    {
        GoldenParityAudioRmsMeasurement difference = AudioRmsComparer.Difference(
            Extraction(-20.0, -20.0, -20.0),
            Extraction(-26.0, -26.0, -26.0));

        difference.OverallLevelDiffDb.Should().BeApproximately(6.0, 1e-9);
        difference.MaxWindowDiffDb.Should().BeApproximately(6.0, 1e-9);
    }

    [FfmpegToolsFact]
    public void A_divergence_confined_to_one_window_shows_up_only_in_the_window_figure()
    {
        GoldenParityAudioRmsMeasurement difference = AudioRmsComparer.Difference(
            Extraction(-20.0, -20.0, -20.0),
            Extraction(-20.0, -20.0, -40.0));

        // One window out of three dropping 20 dB moves the whole-file energy average by only ~1.7 dB
        // (the average is over energies, so the two untouched windows dominate) while the window
        // figure reports the full 20 dB. That dilution is exactly why the per-window figure is
        // recorded alongside the whole-file one rather than instead of it.
        difference.MaxWindowDiffDb.Should().BeApproximately(20.0, 1e-9);
        difference.OverallLevelDiffDb.Should().BeLessThan(2.0);
        difference.MaxWindowDiffDb.Should().BeGreaterThan(difference.OverallLevelDiffDb * 5.0);
    }

    [FfmpegToolsFact]
    public void A_trailing_partial_window_is_not_compared()
    {
        // The P2 renders end in a 5 ms remainder of encoder tail padding, measured at -80 dBFS on one
        // leg and -70 dBFS on the other; an RMS over that few samples is noise, not a mix level.
        GoldenParityAudioExtraction reference = Extraction(-30.0, -30.0) with
        {
            Windows = [.. Extraction(-30.0, -30.0).Windows, new WavRmsSampler.RmsWindow(0.5, 0.505, -80.0, -80.0)]
        };
        GoldenParityAudioExtraction candidate = Extraction(-30.0, -30.0) with
        {
            Windows = [.. Extraction(-30.0, -30.0).Windows, new WavRmsSampler.RmsWindow(0.5, 0.505, -70.0, -70.0)]
        };

        GoldenParityAudioRmsMeasurement difference = AudioRmsComparer.Difference(reference, candidate);

        difference.MaxWindowDiffDb.Should().Be(0.0);
        difference.ComparedWindows.Should().Be(2);
    }

    // -------------------------------------------------------------------------------------------
    // Real extractions of the harness's own outputs.
    // -------------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public async Task Two_paths_of_the_same_edit_produce_the_same_audio_levels_to_within_the_mix_intermediate()
    {
        GoldenParityPairResult p2 = _environment.Verdict.Pair("P2");
        string directory = Path.Combine(_environment.WorkingDirectory, "audio-comparer");

        GoldenParityAudioRmsMeasurement difference = await Comparer.CompareAsync(
            p2.ReferenceOutputPath, p2.CandidateOutputPath, directory, CancellationToken.None);

        // The two legs are different renderers since P4-Q1 (TimelineRenderer vs the spike path): the
        // production mix goes through a 16-bit PCM intermediate before AAC, the spike does not, and that
        // measured 0.003 dB whole-file / 0.022 dB per window. Bit-identity is P1's claim, not P2's.
        difference.OverallLevelDiffDb.Should().BeLessThan(GoldenParityHarnessTests.MixIntermediateToleranceDb);
        difference.MaxWindowDiffDb.Should().BeLessThan(GoldenParityHarnessTests.MixIntermediateToleranceDb);
        difference.ReferenceSampleFrames.Should().Be(difference.CandidateSampleFrames);
        difference.ReferenceSampleRate.Should().Be(48000);
        difference.CandidateSampleRate.Should().Be(48000);
        difference.ComparedWindows.Should().BeGreaterThan(0);
    }

    [FfmpegToolsFact]
    public async Task A_six_decibel_bed_divergence_is_detected_and_rejected()
    {
        GoldenParityVerdict verdict = _environment.Verdict;
        GoldenParityPairResult p1 = verdict.Pair("P1");

        string probePath = Path.Combine(_environment.WorkingDirectory, "renders", "audio-probe.mp4");
        GoldenParityAudioRmsMeasurement difference = await Comparer.CompareAsync(
            p1.ReferenceOutputPath,
            probePath,
            Path.Combine(_environment.WorkingDirectory, "audio-probe-comparer"),
            CancellationToken.None);

        difference.OverallLevelDiffDb.Should().BeApproximately(6.0, 0.25);
        difference.OverallLevelDiffDb.Should().BeGreaterThan(verdict.Thresholds.MaxAudioRmsDiffDb);
        difference.ReferenceSampleRate.Should().Be(48000);
    }

    [FfmpegToolsFact]
    public async Task Extraction_reads_the_audio_geometry_from_the_file_itself()
    {
        GoldenParityPairResult p1 = _environment.Verdict.Pair("P1");
        string wavPath = Path.Combine(_environment.WorkingDirectory, "audio-comparer", "geometry.wav");

        GoldenParityAudioExtraction extraction = await Comparer.ExtractAsync(p1.ReferenceOutputPath, wavPath, CancellationToken.None);

        extraction.SampleRate.Should().Be(48000);
        extraction.Channels.Should().Be(2);
        extraction.SampleFrames.Should().BeGreaterThan(0);
        extraction.Windows.Should().NotBeEmpty();
        extraction.Argv.Should().Contain("-c:a").And.Contain("pcm_s16le");
    }

    [FfmpegToolsFact]
    public void An_extraction_with_no_windows_has_no_level_rather_than_silence()
    {
        var empty = new GoldenParityAudioExtraction("none.wav", 0, 0, 0, [], ["-i", "none.wav"]);

        Action act = () => _ = empty.OverallLevelDbfs;

        act.Should().Throw<InvalidOperationException>();
    }

    private static GoldenParityAudioExtraction Extraction(params double[] windowLevelsDb)
    {
        var windows = new List<WavRmsSampler.RmsWindow>();
        for (int i = 0; i < windowLevelsDb.Length; i++)
        {
            windows.Add(new WavRmsSampler.RmsWindow(
                StartSec: i * 0.25,
                EndSec: (i + 1) * 0.25,
                RmsDbfs: windowLevelsDb[i],
                PeakDbfs: windowLevelsDb[i]));
        }

        return new GoldenParityAudioExtraction(
            WavPath: "synthetic.wav",
            SampleRate: 48000,
            Channels: 1,
            SampleFrames: (int)(windows.Count * 0.25 * 48000),
            Windows: windows,
            Argv: ["-i", "synthetic.wav"]);
    }
}
