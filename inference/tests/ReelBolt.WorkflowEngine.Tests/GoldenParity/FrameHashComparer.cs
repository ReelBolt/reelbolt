#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Per-frame SHA-256 hashes over one video's raw decoded RGB frames.</summary>
/// <param name="Width">Decoded width the frame size was derived from.</param>
/// <param name="Height">Decoded height the frame size was derived from.</param>
/// <param name="FrameCount">Number of complete frames decoded.</param>
/// <param name="FrameHashes">One lowercase-hex SHA-256 per frame, in decode order.</param>
/// <param name="StreamHash">SHA-256 over the concatenated raw bytes — a whole-video bit hash.</param>
public sealed record GoldenParityFrameHashes(
    int Width,
    int Height,
    int FrameCount,
    IReadOnlyList<string> FrameHashes,
    string StreamHash);

/// <summary>The result of comparing two <see cref="GoldenParityFrameHashes"/>.</summary>
/// <param name="ReferenceFrameCount">Frames decoded from the reference output.</param>
/// <param name="CandidateFrameCount">Frames decoded from the candidate output.</param>
/// <param name="ComparedFrames">Frames actually compared (the shorter of the two).</param>
/// <param name="MismatchCount">Compared frames whose hashes differ, plus any unpaired frames.</param>
/// <param name="FirstMismatchFrame">Index of the first differing frame, or <c>null</c>.</param>
/// <param name="Identical">True only when the two decoded streams are bit-identical.</param>
public sealed record GoldenParityFrameHashComparison(
    int ReferenceFrameCount,
    int CandidateFrameCount,
    int ComparedFrames,
    int MismatchCount,
    int? FirstMismatchFrame,
    bool Identical)
{
    /// <summary>The fraction of compared frames that differ, in 0..1.</summary>
    public double MismatchFraction => ComparedFrames == 0 ? 0.0 : MismatchCount / (double)ComparedFrames;
}

/// <summary>
/// Decodes a video to raw RGB frames and hashes each one with SHA-256, then compares two such
/// hashes frame by frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the frames go through a file.</b> <see cref="IVideoToolRunner"/> captures stdout as
/// <i>text</i> lines (bounded, line-buffered), so binary frame data cannot come back through it
/// without corruption. Frames are therefore written by ffmpeg to a raw <c>rgb24</c> file and hashed
/// from disk. Every ffmpeg argument still goes through the single production seam, which is the
/// property that matters: no test builds a shell command line. The raw dump is deleted immediately
/// after hashing — it is roughly 10 MB for the two-second fixture, and there is no reason to keep it.
/// </para>
/// <para>
/// <b>What a frame hash can and cannot decide.</b> A SHA-256 over decoded pixels flips on a
/// one-code-value difference, so it is a <b>bit-exactness</b> test, not a similarity test. The
/// harness's own P1/P2 measurements show that two encodes of the same edit at 47.5 dB PSNR still
/// differ on 39 of 60 frames — so exact frame-hash parity is a usable gate only between two
/// invocations of the <i>same</i> renderer with byte-identical argv, and never across two encode
/// paths. PSNR carries the cross-path gate; see <see cref="GoldenParityThresholds"/>.
/// </para>
/// </remarks>
public sealed class FrameHashComparer
{
    private readonly IVideoToolRunner _runner;
    private readonly TimeSpan _timeout;

    public FrameHashComparer(IVideoToolRunner runner, TimeSpan timeout)
    {
        _runner = runner;
        _timeout = timeout;
    }

    /// <summary>
    /// Decodes <paramref name="videoPath"/> to raw RGB and hashes every frame.
    /// </summary>
    /// <param name="videoPath">The media file to decode.</param>
    /// <param name="width">Expected decoded width; the frame size follows from it.</param>
    /// <param name="height">Expected decoded height.</param>
    /// <param name="workingDirectory">Where the temporary raw dump is written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<GoldenParityFrameHashes> ComputeAsync(
        string videoPath,
        int width,
        int height,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Decoded dimensions must be positive (got {width}x{height}).");
        }

        Directory.CreateDirectory(workingDirectory);
        int frameSize = checked(width * height * 3);
        string rawPath = Path.Combine(
            workingDirectory,
            Path.GetFileNameWithoutExtension(videoPath) + ".golden-parity.rgb24");

        try
        {
            string[] args =
            [
                "-y", "-v", "error", "-nostdin",
                "-i", videoPath,
                "-f", "rawvideo",
                "-pix_fmt", "rgb24",
                rawPath
            ];

            VideoToolResult result = await _runner.RunFfmpegAsync(args, _timeout, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Raw RGB decode failed for '{videoPath}' (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                    $"stderr: {result.StdErr.Trim()}");
            }

            byte[] raw = await File.ReadAllBytesAsync(rawPath, cancellationToken).ConfigureAwait(false);
            if (raw.Length == 0 || raw.Length % frameSize != 0)
            {
                throw new InvalidOperationException(
                    $"Raw RGB decode of '{videoPath}' produced {raw.Length} bytes, which is not a whole number of " +
                    $"{width}x{height} rgb24 frames ({frameSize} bytes each). The declared geometry does not match " +
                    "the actual stream.");
            }

            int frameCount = raw.Length / frameSize;
            var hashes = new List<string>(frameCount);
            for (int frame = 0; frame < frameCount; frame++)
            {
                hashes.Add(Convert.ToHexStringLower(
                    SHA256.HashData(raw.AsSpan(frame * frameSize, frameSize))));
            }

            return new GoldenParityFrameHashes(
                Width: width,
                Height: height,
                FrameCount: frameCount,
                FrameHashes: hashes,
                StreamHash: Convert.ToHexStringLower(SHA256.HashData(raw)));
        }
        finally
        {
            if (File.Exists(rawPath))
            {
                File.Delete(rawPath);
            }
        }
    }

    /// <summary>
    /// Compares two frame-hash sets. Frames beyond the shorter stream count as mismatches, so a
    /// length difference can never read as "identical".
    /// </summary>
    public static GoldenParityFrameHashComparison Compare(
        GoldenParityFrameHashes reference,
        GoldenParityFrameHashes candidate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);

        int compared = Math.Min(reference.FrameCount, candidate.FrameCount);
        int mismatches = Math.Abs(reference.FrameCount - candidate.FrameCount);
        int? firstMismatch = null;

        for (int frame = 0; frame < compared; frame++)
        {
            if (!string.Equals(reference.FrameHashes[frame], candidate.FrameHashes[frame], StringComparison.Ordinal))
            {
                mismatches++;
                firstMismatch ??= frame;
            }
        }

        return new GoldenParityFrameHashComparison(
            ReferenceFrameCount: reference.FrameCount,
            CandidateFrameCount: candidate.FrameCount,
            ComparedFrames: compared,
            MismatchCount: mismatches,
            FirstMismatchFrame: firstMismatch,
            Identical: mismatches == 0 && reference.FrameCount == candidate.FrameCount);
    }
}
