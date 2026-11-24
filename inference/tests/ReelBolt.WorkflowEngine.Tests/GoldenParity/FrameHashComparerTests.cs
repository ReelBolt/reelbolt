#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="FrameHashComparer"/> — the pure comparison, and a real decode of the harness's own
/// outputs.
/// </summary>
/// <remarks>
/// The real-decode facts reuse the files the shared harness already rendered rather than rendering
/// again: a second render of the same document would be a second experiment, not a second assertion.
/// </remarks>
[Collection(GoldenParityCollection.Name)]
public class FrameHashComparerTests
{
    private readonly GoldenParityTestEnvironment _environment;

    public FrameHashComparerTests(GoldenParityTestEnvironment environment) => _environment = environment;

    private FrameHashComparer Comparer => new(_environment.Runner, _environment.ToolTimeout);

    // -------------------------------------------------------------------------------------------
    // The pure comparison.
    // -------------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public void Identical_hash_sets_compare_identical()
    {
        GoldenParityFrameHashes reference = Hashes("a", "b", "c");

        GoldenParityFrameHashComparison comparison = FrameHashComparer.Compare(reference, Hashes("a", "b", "c"));

        comparison.Identical.Should().BeTrue();
        comparison.MismatchCount.Should().Be(0);
        comparison.ComparedFrames.Should().Be(3);
        comparison.FirstMismatchFrame.Should().BeNull();
    }

    [FfmpegToolsFact]
    public void A_single_differing_frame_is_reported_at_its_index()
    {
        GoldenParityFrameHashComparison comparison = FrameHashComparer.Compare(
            Hashes("a", "b", "c", "d"),
            Hashes("a", "b", "X", "d"));

        comparison.Identical.Should().BeFalse();
        comparison.MismatchCount.Should().Be(1);
        comparison.FirstMismatchFrame.Should().Be(2);
        comparison.MismatchFraction.Should().Be(0.25);
    }

    [FfmpegToolsFact]
    public void Unequal_lengths_count_the_unpaired_frames_as_mismatches()
    {
        GoldenParityFrameHashComparison comparison = FrameHashComparer.Compare(
            Hashes("a", "b", "c"),
            Hashes("a", "b"));

        comparison.Identical.Should().BeFalse("two streams of different length are never the same edit");
        comparison.ComparedFrames.Should().Be(2);
        comparison.MismatchCount.Should().Be(1);
        comparison.ReferenceFrameCount.Should().Be(3);
        comparison.CandidateFrameCount.Should().Be(2);
    }

    // -------------------------------------------------------------------------------------------
    // Real decodes of the harness's own outputs.
    // -------------------------------------------------------------------------------------------

    [FfmpegToolsFact]
    public async Task Two_renders_of_the_same_document_with_the_same_argv_decode_to_identical_frames()
    {
        GoldenParityPairResult p1 = _environment.Verdict.Pair("P1");

        GoldenParityFrameHashes reference = await Comparer.ComputeAsync(
            p1.ReferenceOutputPath, p1.ReferenceStreams.Width, p1.ReferenceStreams.Height, _environment.WorkingDirectory, CancellationToken.None);
        GoldenParityFrameHashes candidate = await Comparer.ComputeAsync(
            p1.CandidateOutputPath, p1.CandidateStreams.Width, p1.CandidateStreams.Height, _environment.WorkingDirectory, CancellationToken.None);

        FrameHashComparer.Compare(reference, candidate).Identical.Should().BeTrue();
        reference.StreamHash.Should().Be(candidate.StreamHash);
        reference.FrameCount.Should().Be(60);
    }

    [FfmpegToolsFact]
    public async Task A_re_encoded_pair_shares_length_but_not_frames()
    {
        GoldenParityPairResult p2 = _environment.Verdict.Pair("P2");

        GoldenParityFrameHashes reference = await Comparer.ComputeAsync(
            p2.ReferenceOutputPath, p2.ReferenceStreams.Width, p2.ReferenceStreams.Height, _environment.WorkingDirectory, CancellationToken.None);
        GoldenParityFrameHashes candidate = await Comparer.ComputeAsync(
            p2.CandidateOutputPath, p2.CandidateStreams.Width, p2.CandidateStreams.Height, _environment.WorkingDirectory, CancellationToken.None);

        GoldenParityFrameHashComparison comparison = FrameHashComparer.Compare(reference, candidate);

        comparison.ReferenceFrameCount.Should().Be(comparison.CandidateFrameCount);
        comparison.Identical.Should().BeFalse(
            "a SHA-256 over decoded pixels flips on a single code value, so a real re-encode is never frame-identical");
        comparison.MismatchCount.Should().BeGreaterThan(0);
    }

    [FfmpegToolsFact]
    public async Task Decoding_with_the_wrong_geometry_is_rejected_rather_than_hashed()
    {
        GoldenParityPairResult p1 = _environment.Verdict.Pair("P1");

        // 100x100 is deliberately NOT a divisor of the decoded byte count: the frame size the caller
        // declares does not describe the stream, so there is no honest hash to compute.
        var act = async () => await Comparer.ComputeAsync(
            p1.ReferenceOutputPath, 100, 100, _environment.WorkingDirectory, CancellationToken.None);

        // A frame size that does not divide the dump means the caller's belief about the stream is
        // wrong; hashing it would produce a confident, meaningless comparison.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static GoldenParityFrameHashes Hashes(params string[] hashes) =>
        new(
            Width: 2,
            Height: 2,
            FrameCount: hashes.Length,
            FrameHashes: hashes,
            StreamHash: string.Concat(hashes));
}
