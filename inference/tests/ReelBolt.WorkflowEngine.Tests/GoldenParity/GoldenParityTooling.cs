#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Thrown when the host cannot provide real ffmpeg/ffprobe to the golden-parity harness.
/// Deliberately an exception rather than a skip: the brief's rule for this spike is that a missing
/// ffmpeg must surface as an explicit failure with a clear reason and may <b>never</b> be softened
/// into a green pass. Every test that depends on measurement therefore fails loudly rather than
/// reporting a vacuous success.
/// </summary>
public sealed class GoldenParityToolingUnavailableException : Exception
{
    public GoldenParityToolingUnavailableException(string message) : base(message)
    {
    }

    public GoldenParityToolingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Host-tooling discovery and construction for the golden-parity harness: locating the real
/// <c>ffmpeg</c>/<c>ffprobe</c> binaries, building the production <see cref="IVideoToolRunner"/>
/// over them, and proving the binary actually executes.
/// </summary>
/// <remarks>
/// The harness invokes ffmpeg <b>only</b> through <see cref="IVideoToolRunner"/> — the single
/// production seam — so every argv it produces is a real <c>IReadOnlyList&lt;string&gt;</c> that the
/// production runner would accept, never a shell string. This class is the one place that decides
/// <i>which</i> binary that runner points at.
/// </remarks>
internal static class GoldenParityTooling
{
    /// <summary>The ffmpeg executable name resolved on <c>PATH</c>.</summary>
    public const string FfmpegExecutable = "ffmpeg";

    /// <summary>The ffprobe executable name resolved on <c>PATH</c>.</summary>
    public const string FfprobeExecutable = "ffprobe";

    /// <summary>
    /// Resolves an executable name against this process's <c>PATH</c>, returning the absolute path
    /// of the first match.
    /// </summary>
    /// <exception cref="GoldenParityToolingUnavailableException">
    /// The name is not present in any <c>PATH</c> directory.
    /// </exception>
    public static string ResolveOnPath(string executableName)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string directory in pathVariable.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                // Windows keeps the extension; every other platform uses the bare name.
                foreach (string candidate in CandidateFileNames(executableName))
                {
                    string fullPath = Path.Combine(directory, candidate);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
        }

        throw new GoldenParityToolingUnavailableException(
            $"'{executableName}' was not found on PATH. The golden-parity harness measures real encodings and " +
            "cannot run without it; a missing binary is reported as a failure rather than skipped so it can " +
            "never look like a passing parity gate.");
    }

    /// <summary>
    /// Builds the production <see cref="FfmpegVideoToolRunner"/> pointed at the given binaries.
    /// </summary>
    /// <remarks>
    /// Constructed directly, as the brief suggests, rather than through a DI container: the runner's
    /// only state is a concurrency semaphore, and a single job is what a parity measurement wants.
    /// </remarks>
    public static FfmpegVideoToolRunner CreateRunner(string ffmpegPath, string ffprobePath) =>
        new(
            Options.Create(new VideoEditingOptions
            {
                FfmpegPath = ffmpegPath,
                FfprobePath = ffprobePath,
                MaxConcurrentJobs = 1
            }),
            NullLogger<FfmpegVideoToolRunner>.Instance);

    /// <summary>
    /// Proves the runner can actually execute ffmpeg and returns the first line of its version
    /// banner (recorded in the measurements artifact, so a set of numbers can always be traced to
    /// the exact binary that produced them).
    /// </summary>
    /// <exception cref="GoldenParityToolingUnavailableException">
    /// The binary cannot be started, or exits non-zero.
    /// </exception>
    public static async Task<string> RequireFfmpegVersionAsync(
        IVideoToolRunner runner,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        VideoToolResult result;
        try
        {
            result = await runner
                .RunFfmpegAsync(["-version"], timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A missing or non-executable binary surfaces as Win32Exception from Process.Start;
            // anything else here is equally fatal to a measurement-only harness.
            throw new GoldenParityToolingUnavailableException(
                $"The configured ffmpeg could not be started ({ex.GetType().Name}: {ex.Message}). The " +
                "golden-parity harness measures real encodings and cannot run without it.",
                ex);
        }

        if (!result.Succeeded)
        {
            throw new GoldenParityToolingUnavailableException(
                $"`ffmpeg -version` exited {result.ExitCode} (timedOut={result.TimedOut}). The golden-parity " +
                $"harness measures real encodings and cannot run without it. stderr: {result.StdErr.Trim()}");
        }

        string banner = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "ffmpeg (version banner empty)";

        return banner;
    }

    /// <summary>
    /// The directory of the source file that calls this method, resolved at compile time.
    /// </summary>
    /// <remarks>
    /// Used to locate the committed fixture manifest. The test project does not (and, under this
    /// ticket's write surface, may not) add a <c>CopyToOutputDirectory</c> item for it in the
    /// <c>.csproj</c>, so the fixture is read from the source tree; see
    /// <see cref="ResolveFixtureFile"/> for the fallback that covers a relocated working directory.
    /// </remarks>
    public static string CallerDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath) ?? string.Empty;

    /// <summary>
    /// Locates a committed fixture file by name, preferring the source tree the calling test file
    /// lives in and falling back to a bounded upward search from the test assembly's own location.
    /// </summary>
    /// <exception cref="GoldenParityToolingUnavailableException">The fixture was not found.</exception>
    public static string ResolveFixtureFile(string fileName, [CallerFilePath] string callerFilePath = "")
    {
        string fromSourceTree = Path.Combine(CallerDirectory(callerFilePath), "Fixtures", fileName);
        if (File.Exists(fromSourceTree))
        {
            return fromSourceTree;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        var searched = new List<string>();
        for (int depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "GoldenParity", "Fixtures", fileName);
            searched.Add(candidate);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new GoldenParityToolingUnavailableException(
            $"Fixture '{fileName}' was not found next to the calling test file ('{fromSourceTree}', exists=" +
            $"{File.Exists(fromSourceTree)}, directoryExists={Directory.Exists(Path.GetDirectoryName(fromSourceTree))}) " +
            $"or within 12 directory levels above the test assembly (baseDirectory='{AppContext.BaseDirectory}', " +
            $"searched: {string.Join(" | ", searched)}). The harness fixture is committed under " +
            "inference/tests/ReelBolt.WorkflowEngine.Tests/GoldenParity/Fixtures/.");
    }

    private static IEnumerable<string> CandidateFileNames(string executableName)
    {
        yield return executableName;

        if (OperatingSystem.IsWindows())
        {
            yield return executableName + ".exe";
        }
    }
}
