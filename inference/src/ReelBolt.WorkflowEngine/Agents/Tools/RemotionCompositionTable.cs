using ReelBolt.Shared.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// One registered Remotion composition, as <c>remotion compositions</c> lists it.
/// </summary>
/// <param name="Fps">Null for a still (the CLI prints no frame rate for a one-frame composition).</param>
public sealed record CompositionInfo(string Id, double? Fps, int Width, int Height, int DurationInFrames, bool IsStill)
{
    public double? DurationSeconds =>
        Fps is > 0 ? Math.Round(DurationInFrames / Fps.Value, 2) : null;
}

/// <summary>
/// Pure parser for the table <c>npx remotion compositions</c> prints, so a tool can hand the model
/// structured numbers (frames, fps, size, seconds) instead of a table to read. The format is the
/// CLI's own <c>printCompositions</c> (checked against @remotion/cli 4.0.529):
///
/// <code>
/// The following compositions are available:
///
/// FinalVideo    30      1080x1920      3555 (118.50 sec)
/// Poster                1080x1080      Still
/// </code>
///
/// Anything that is not a row — the banner, blank lines, log noise — is ignored, so a CLI that
/// grows an extra line never breaks the parse; a CLI that changes the row format yields an empty
/// list, which the caller reports instead of guessing.
/// </summary>
public static class RemotionCompositionTable
{
    private static readonly Regex Row = new(
        @"^(?<id>\S+)\s+(?:(?<fps>\d+(?:\.\d+)?)\s+)?(?<w>\d+)x(?<h>\d+)\s+(?:(?<still>Still)|(?<frames>\d+)\s*\((?<sec>[\d.]+)\s*sec\))\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<CompositionInfo> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        List<CompositionInfo> compositions = [];
        foreach (string rawLine in TextLineWindow.SplitLines(SandboxOutputCompactor.StripAnsi(output)))
        {
            Match match = Row.Match(rawLine.Trim());
            if (!match.Success)
                continue;

            bool isStill = match.Groups["still"].Success;
            double? fps = match.Groups["fps"].Success
                ? double.Parse(match.Groups["fps"].Value, CultureInfo.InvariantCulture)
                : null;

            if (!int.TryParse(match.Groups["w"].Value, out int width) ||
                !int.TryParse(match.Groups["h"].Value, out int height))
            {
                continue;
            }

            int frames = 1;
            if (!isStill && !int.TryParse(match.Groups["frames"].Value, out frames))
                continue;

            compositions.Add(new CompositionInfo(match.Groups["id"].Value, fps, width, height, frames, isStill));
        }

        return compositions;
    }
}
