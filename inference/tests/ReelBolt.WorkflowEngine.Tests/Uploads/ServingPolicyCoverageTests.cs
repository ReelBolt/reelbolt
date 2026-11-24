using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// DS-4: every controller action that streams bytes (<c>File(</c> / <c>StreamAsync(</c>) must go through
/// the safe-serving policy. This reads the controller sources, finds each such action and fails when its
/// body does not apply the security headers and a policy-built disposition (or delegate to
/// <c>RangedObjectStreamer</c>, which does both), so a new streaming route cannot ship without it.
/// </summary>
public sealed class ServingPolicyCoverageTests
{
    /// <summary>
    /// Routes that stream bytes the SERVER generated (not customer bytes with a stored MIME type), so they
    /// need the headers and an attachment disposition but not a MIME decision.
    /// </summary>
    private static readonly HashSet<string> ServerGeneratedBytes = new(StringComparer.Ordinal)
    {
        "StepResultArtifactsController.GetArtifact",
        "StepResultArtifactsController.GetTimeline",
        // The compile writes the .srt/.vtt itself; the content type is fixed by the requested format, never stored.
        "StepResultArtifactsController.GetSubtitles",
        "OrgDesignLibrariesController.Export",
        "DesignSystemsController.Export",
        "EditTimelinesController.ExportEditTimeline",
    };

    /// <summary>
    /// Known gaps outside this change's ownership, listed so the gap is visible and a NEW offender still fails.
    /// Empty: the edit-timeline export now goes through the policy.
    /// </summary>
    private static readonly HashSet<string> KnownGaps = new(StringComparer.Ordinal);

    private static string ControllersDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api", "Controllers")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the test must find inference/src/ReelBolt.Inference.Api/Controllers");
        return Path.Combine(dir!, "src", "ReelBolt.Inference.Api", "Controllers");
    }

    private sealed record Streaming(string Name, string Body);

    private static IEnumerable<Streaming> StreamingActions()
    {
        var signature = new Regex(@"^    (public|private|internal|protected)\b[^;=]*\(", RegexOptions.Compiled);
        var streams = new Regex(@"(?<![A-Za-z.])File\(|StreamAsync\(|new FileStreamResult\(|new FileContentResult\(", RegexOptions.Compiled);

        foreach (string path in Directory.GetFiles(ControllersDirectory(), "*Controller.cs"))
        {
            string[] lines = File.ReadAllLines(path);
            string controller = Path.GetFileNameWithoutExtension(path);

            for (int start = 0; start < lines.Length; start++)
            {
                if (!signature.IsMatch(lines[start]) || lines[start].TrimStart().StartsWith("public static", StringComparison.Ordinal))
                    continue;

                int end = start + 1;
                while (end < lines.Length && !signature.IsMatch(lines[end]) && lines[end] != "}")
                    end++;

                string body = string.Join('\n', lines[start..end]);
                string codeOnly = string.Join('\n', body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                if (!streams.IsMatch(codeOnly))
                    continue;

                Match name = Regex.Match(lines[start], @"(\w+)\s*(<[^>]*>)?\s*\(");
                yield return new Streaming($"{controller}.{name.Groups[1].Value}", codeOnly);
            }
        }
    }

    private static bool AppliesPolicy(string body) =>
        body.Contains("RangedObjectStreamer.StreamAsync(", StringComparison.Ordinal)
        || (body.Contains("SafeContentPolicy.ApplySecurityHeaders(", StringComparison.Ordinal)
            && body.Contains("SafeContentPolicy.BuildContentDisposition(", StringComparison.Ordinal));

    private static bool DecidesMime(string body) =>
        body.Contains("SafeContentPolicy.Decide(", StringComparison.Ordinal)
        || body.Contains("SafeContentPolicy.DecideByMime(", StringComparison.Ordinal)
        || body.Contains("RangedObjectStreamer.StreamAsync(", StringComparison.Ordinal);

    [Fact]
    public void The_scan_finds_the_known_streaming_routes()
    {
        List<string> names = StreamingActions().Select(a => a.Name).ToList();

        names.Should().Contain([
            "MediaController.GetDerivativeContent",
            "StepResultArtifactsController.GetArtifact",
            "ProjectFilesController.Download",
            "DesignSystemsController.Export",
        ]);
    }

    [Fact]
    public void Every_streaming_action_applies_the_security_headers_and_a_policy_disposition()
    {
        List<string> offenders = StreamingActions()
            .Where(a => !KnownGaps.Contains(a.Name) && !AppliesPolicy(a.Body))
            .Select(a => a.Name)
            .ToList();

        string.Join(", ", offenders).Should().BeEmpty("these actions stream bytes without SafeContentPolicy headers/disposition");
    }

    [Fact]
    public void Every_streaming_action_serving_stored_bytes_decides_the_content_type_through_the_policy()
    {
        List<string> offenders = StreamingActions()
            .Where(a => !ServerGeneratedBytes.Contains(a.Name) && !KnownGaps.Contains(a.Name) && !DecidesMime(a.Body))
            .Select(a => a.Name)
            .ToList();

        string.Join(", ", offenders).Should().BeEmpty("these actions take a stored MIME type and serve it without SafeContentPolicy.Decide");
    }
}
