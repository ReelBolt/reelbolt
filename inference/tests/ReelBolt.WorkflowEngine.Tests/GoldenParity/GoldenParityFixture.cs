#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The generated fixture media the harness renders from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generated, never committed.</b> Both fixture files are produced by real ffmpeg at test time
/// from <c>lavfi</c> sources (<c>testsrc2</c> and <c>sine</c>), which are deterministic and require
/// no binary blob in the repository. The only committed artifact is the small JSON
/// <see cref="TimelineManifest"/> the seeder consumes.
/// </para>
/// <para>
/// <b>Why the source is 3 s while the edit is 2 s.</b> The negative control shifts a cut's source
/// in-point one frame later; with a source exactly as long as the edit, that shift would shorten the
/// output by a frame and the pair would be rejected by the stream-property check instead of by the
/// threshold under test. The extra second keeps the two edits the same length so PSNR is the only
/// thing separating them.
/// </para>
/// </remarks>
internal static class GoldenParityFixtureSources
{
    /// <summary>Fixture video width, in pixels.</summary>
    public const int Width = 320;

    /// <summary>Fixture video height, in pixels.</summary>
    public const int Height = 180;

    /// <summary>Fixture frame rate, in frames per second.</summary>
    public const double Fps = 30.0;

    /// <summary>Generated source length, in seconds.</summary>
    public const double SourceDurationSec = 3.0;

    /// <summary>Generated audio-bed length, in seconds (the edit's own length).</summary>
    public const double BedDurationSec = 2.0;

    /// <summary>Storage key the committed manifest gives the source file.</summary>
    public const string SourceStorageKey = "golden-parity/fixture-source.mp4";

    /// <summary>Name the committed manifest gives the audio bed (audio is referenced by name in v1).</summary>
    public const string BedStorageKey = "bed.wav";

    /// <summary>
    /// Generates the H.264/AAC source (video + audio) and the PCM audio bed, and returns them with
    /// their content hashes.
    /// </summary>
    public static async Task<GoldenParityFixtureMedia> CreateAsync(
        IVideoToolRunner runner,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        string sourcePath = Path.Combine(workingDirectory, "fixture-source.mp4");
        string bedPath = Path.Combine(workingDirectory, "bed.wav");

        string[] sourceArgs =
        [
            "-y", "-v", "error", "-nostdin",
            "-f", "lavfi", "-i", $"testsrc2=size={Width}x{Height}:rate={Seconds(Fps)}:duration={Seconds(SourceDurationSec)}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={Seconds(SourceDurationSec)}",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-r", Seconds(Fps), "-g", Seconds(Fps),
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000",
            "-shortest",
            sourcePath
        ];

        string[] bedArgs =
        [
            "-y", "-v", "error", "-nostdin",
            "-f", "lavfi", "-i", $"sine=frequency=220:sample_rate=48000:duration={Seconds(BedDurationSec)}",
            "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2",
            bedPath
        ];

        await RunAsync(runner, sourceArgs, "fixture source generation", timeout, cancellationToken).ConfigureAwait(false);
        await RunAsync(runner, bedArgs, "fixture audio bed generation", timeout, cancellationToken).ConfigureAwait(false);

        return new GoldenParityFixtureMedia(
            SourcePath: sourcePath,
            SourceSha256: await Sha256FileAsync(sourcePath, cancellationToken).ConfigureAwait(false),
            BedPath: bedPath,
            BedSha256: await Sha256FileAsync(bedPath, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one fixture-generation invocation through the production seam, failing loudly on a
    /// non-zero exit (a half-written fixture would otherwise be measured as if it were real).
    /// </summary>
    private static async Task RunAsync(
        IVideoToolRunner runner,
        IReadOnlyList<string> args,
        string what,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        VideoToolResult result = await runner.RunFfmpegAsync(args, timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Golden-parity fixture: {what} failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                $"stderr: {result.StdErr.Trim()}");
        }
    }

    /// <summary>The lowercase-hex SHA-256 of a file's bytes.</summary>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string Seconds(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>The two generated fixture files and their content hashes.</summary>
internal sealed record GoldenParityFixtureMedia(
    string SourcePath,
    string SourceSha256,
    string BedPath,
    string BedSha256);

/// <summary>
/// Loading and in-memory variation of the committed v1 compile-result manifest.
/// </summary>
internal static class GoldenParityManifest
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Deserializes the committed <see cref="TimelineManifest"/> JSON, exactly as the Inference API
    /// would hand it to the seeder.
    /// </summary>
    public static TimelineManifest Load(string manifestPath)
    {
        string json = File.ReadAllText(manifestPath);
        TimelineManifest? manifest = JsonSerializer.Deserialize<TimelineManifest>(json, Options);
        return manifest ?? throw new InvalidOperationException(
            $"The golden-parity fixture manifest at '{manifestPath}' deserialized to null.");
    }

    /// <summary>
    /// The same manifest with the <b>second video cut's source in-point moved one frame later</b> —
    /// the negative control's edit.
    /// </summary>
    /// <remarks>
    /// Only <c>SourceStartSec</c> moves. The cut's record range, the timeline's total length and the
    /// audio bed are untouched, so the two edits are the same duration and differ by exactly one
    /// frame of picture at the second cut's in-point. That is what makes the pair a test of the
    /// threshold rather than of the stream-property check.
    /// </remarks>
    /// <param name="manifest">The base manifest.</param>
    /// <param name="frames">How many frames to shift by.</param>
    public static TimelineManifest ShiftSecondVideoCutInPoint(TimelineManifest manifest, int frames)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        double frameDuration = 1.0 / (manifest.OutputVideo.Fps > 0 ? manifest.OutputVideo.Fps : EditTimelineSeeder.FallbackFps);
        double shift = frames * frameDuration;

        var tracks = new List<TimelineTrack>(manifest.Tracks.Count);
        foreach (TimelineTrack track in manifest.Tracks)
        {
            if (!string.Equals(track.Type, "video", StringComparison.Ordinal))
            {
                tracks.Add(track);
                continue;
            }

            var items = new List<TimelineItem>(track.Items.Count);
            for (int i = 0; i < track.Items.Count; i++)
            {
                TimelineItem item = track.Items[i];
                items.Add(i == 1 ? item with { SourceStartSec = (item.SourceStartSec ?? 0.0) + shift } : item);
            }

            tracks.Add(track with { Items = items });
        }

        return manifest with { Tracks = tracks };
    }
}

/// <summary>
/// Resolves the manifest's asset identifiers to <see cref="AssetRef"/>s, and those refs back to the
/// generated local files the spike seam renders from.
/// </summary>
/// <remarks>
/// <para>
/// v1 manifests reference video by storage key and audio by project-file name; the seeder documents
/// that split, and this class is the caller that owns it. Ids are derived deterministically from the
/// identifier (SHA-256, first 16 bytes) so a re-seed produces an identical document.
/// </para>
/// <para>
/// <b>Static and path-free on purpose.</b> The seeder's resolver runs while the harness is generating
/// nothing, and the seam's resolver runs after the fixture files exist — so the mapping has to be
/// expressible in both directions without shared mutable state. Both directions are pure functions
/// of the identifier, which is what makes a re-seed reproducible.
/// </para>
/// </remarks>
internal static class GoldenParityAssets
{
    private static readonly Dictionary<string, string> FileNames = new(StringComparer.Ordinal)
    {
        [GoldenParityFixtureSources.SourceStorageKey] = "fixture-source.mp4",
        [GoldenParityFixtureSources.BedStorageKey] = "bed.wav"
    };

    /// <summary>The seeder's <c>resolveAsset</c> callback.</summary>
    public static AssetRef? Resolve(string identifier) =>
        identifier is not null && FileNames.ContainsKey(identifier)
            ? new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: DeterministicProjectFileId(identifier))
            : null;

    /// <summary>
    /// The local file path an <see cref="AssetRef"/> stands for, inside the generated fixture
    /// directory.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ref is not one this harness created.</exception>
    public static string ResolvePath(AssetRef assetRef, string fixtureDirectory)
    {
        ArgumentNullException.ThrowIfNull(assetRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);

        if (assetRef.Kind != AssetRefKind.ProjectFile || assetRef.ProjectFileId is not { } id)
        {
            throw new NotSupportedException(
                $"The golden-parity fixture resolves only ProjectFile assets; got {assetRef.Kind}.");
        }

        foreach ((string identifier, string fileName) in FileNames)
        {
            if (DeterministicProjectFileId(identifier) == id)
            {
                string path = Path.Combine(fixtureDirectory, fileName);
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"The fixture file for asset '{identifier}' is missing at '{path}'. The harness generates it " +
                        "before any render; a render resolving an absent fixture would otherwise measure nothing.");
                }

                return path;
            }
        }

        throw new InvalidOperationException($"No fixture file is registered for asset id {id}.");
    }

    private static Guid DeterministicProjectFileId(string identifier)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"reelforge.goldenparity.asset:{identifier}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
