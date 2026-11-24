#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The PSNR figures ffmpeg's <c>psnr</c> filter reported for one pair of video streams.
/// </summary>
/// <param name="AverageDb">The filter's <c>average</c> figure — the gated quantity.</param>
/// <param name="YDb">Luma PSNR, or <c>null</c> when not reported.</param>
/// <param name="UDb">Cb PSNR, or <c>null</c> when not reported.</param>
/// <param name="VDb">Cr PSNR, or <c>null</c> when not reported.</param>
/// <param name="MinDb">Worst-frame PSNR — the figure that shows a single bad frame.</param>
/// <param name="MaxDb">Best-frame PSNR (frequently infinite for identical frames).</param>
/// <param name="Argv">The ffmpeg argv the measurement used.</param>
public sealed record GoldenParityPsnrMeasurement(
    double AverageDb,
    double? YDb,
    double? UDb,
    double? VDb,
    double? MinDb,
    double? MaxDb,
    IReadOnlyList<string> Argv);

/// <summary>
/// Measures PSNR between two decoded video streams using ffmpeg's own <c>psnr</c> filter.
/// </summary>
/// <remarks>
/// <para>
/// The filter reports its summary line on stderr at info level
/// (<c>[Parsed_psnr_0 @ …] PSNR y:… u:… v:… average:… min:… max:…</c>), which
/// <see cref="IVideoToolRunner"/> captures as text — the one measurement in this harness that can
/// legitimately read the seam's captured output. Identical streams report <c>inf</c>, preserved
/// here as <see cref="double.PositiveInfinity"/> rather than collapsed to a large number, because
/// "bit-identical" and "very close" are different findings.
/// </para>
/// <para>
/// <b>Callers must compare stream properties first.</b> The filter silently compares streams of
/// different length by stopping at the shorter one, and its result over misaligned streams is a
/// plausible, meaningless number. <see cref="StreamPropertyComparer"/> exists for exactly that
/// reason, and the harness gates on it before this measurement is allowed to matter.
/// </para>
/// </remarks>
public sealed class PsnrComparer
{
    /// <summary>The filter's trailing summary line. The last match in stderr wins.</summary>
    private static readonly Regex SummaryLine = new(
        @"PSNR\s+y:(?<y>-?[\d.]+|inf|-inf)\s+u:(?<u>-?[\d.]+|inf|-inf)\s+v:(?<v>-?[\d.]+|inf|-inf)\s+" +
        @"average:(?<average>-?[\d.]+|inf|-inf)\s+min:(?<min>-?[\d.]+|inf|-inf)\s+max:(?<max>-?[\d.]+|inf|-inf)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IVideoToolRunner _runner;
    private readonly TimeSpan _timeout;

    public PsnrComparer(IVideoToolRunner runner, TimeSpan timeout)
    {
        _runner = runner;
        _timeout = timeout;
    }

    /// <summary>
    /// Measures PSNR of <paramref name="candidatePath"/> against <paramref name="referencePath"/>.
    /// </summary>
    public async Task<GoldenParityPsnrMeasurement> CompareAsync(
        string referencePath,
        string candidatePath,
        CancellationToken cancellationToken = default)
    {
        string[] args =
        [
            "-v", "info", "-nostdin",
            "-i", referencePath,
            "-i", candidatePath,
            "-lavfi", "psnr",
            "-f", "null", "-"
        ];

        VideoToolResult result = await _runner.RunFfmpegAsync(args, _timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"PSNR measurement failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                $"stderr: {result.StdErr.Trim()}");
        }

        MatchCollection matches = SummaryLine.Matches(result.StdErr);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                "ffmpeg ran the psnr filter but printed no PSNR summary line, so no measurement can be " +
                $"recorded. stderr tail: {Tail(result.StdErr)}");
        }

        Match summary = matches[^1];
        return new GoldenParityPsnrMeasurement(
            AverageDb: ParseDb(summary.Groups["average"].Value),
            YDb: ParseNullableDb(summary.Groups["y"].Value),
            UDb: ParseNullableDb(summary.Groups["u"].Value),
            VDb: ParseNullableDb(summary.Groups["v"].Value),
            MinDb: ParseNullableDb(summary.Groups["min"].Value),
            MaxDb: ParseNullableDb(summary.Groups["max"].Value),
            Argv: args);
    }

    private static double ParseDb(string value) =>
        value switch
        {
            "inf" => double.PositiveInfinity,
            "-inf" => double.NegativeInfinity,
            _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
        };

    private static double? ParseNullableDb(string value) =>
        string.IsNullOrEmpty(value) ? null : ParseDb(value);

    private static string Tail(string value, int max = 1200) =>
        value.Length <= max ? value : value[^max..];
}
