using System;
using System.IO;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that run the REAL <c>ffmpeg</c>/<c>ffprobe</c> binaries.
/// </summary>
/// <remarks>
/// <para>
/// The CI runner has no ffmpeg (the <c>dotnet</c> job installs the SDK only), while the
/// <c>workflow-engine</c> image and every developer box do. A test that starts <c>ffmpeg</c> on a
/// host without it fails with <c>Win32Exception: No such file or directory</c> — a fact about the
/// host, not about the code — so this attribute SKIPS it there, with a message naming the binary,
/// and runs it everywhere the binary exists. The skip is visible in the test summary; nothing is
/// silently green.
/// </para>
/// <para>
/// The golden-parity harness (<c>GoldenParity/</c>) deliberately does NOT use this: a parity gate
/// that skipped on a missing binary could be mistaken for a passing gate, so it fails loudly
/// instead (<c>GoldenParityTooling.ResolveOnPath</c>). These derivative/render tests are
/// behavioural assertions about argv ffmpeg accepts, not a release gate, which is why skipping is
/// the honest choice here and not there.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegToolsFactAttribute : FactAttribute
{
    public FfmpegToolsFactAttribute()
    {
        string? missing = FfmpegToolsOnPath.MissingBinary();
        if (missing is not null)
        {
            Skip = $"'{missing}' is not on PATH; this test runs the real binary.";
        }
    }
}

/// <summary>Theory counterpart of <see cref="FfmpegToolsFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class FfmpegToolsTheoryAttribute : TheoryAttribute
{
    public FfmpegToolsTheoryAttribute()
    {
        string? missing = FfmpegToolsOnPath.MissingBinary();
        if (missing is not null)
        {
            Skip = $"'{missing}' is not on PATH; this test runs the real binary.";
        }
    }
}

internal static class FfmpegToolsOnPath
{
    private static readonly Lazy<string?> Missing = new(Probe);

    /// <summary>The first of <c>ffmpeg</c>/<c>ffprobe</c> that is not on PATH, or null when both are.</summary>
    public static string? MissingBinary() => Missing.Value;

    private static string? Probe()
    {
        foreach (string binary in new[] { "ffmpeg", "ffprobe" })
        {
            if (!Exists(binary))
            {
                return binary;
            }
        }

        return null;
    }

    private static bool Exists(string executableName)
    {
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
        }

        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, executableName)) ||
                File.Exists(Path.Combine(directory, executableName + ".exe")))
            {
                return true;
            }
        }

        return false;
    }
}
