using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Two guards for compositions that play project media (production run 36df4287): before Remotion runs,
/// a <c>staticFile()</c> target that was never staged is refused by name (<see cref="StaticFilePreflight"/>);
/// and when a still or render dies reading a video ("Could not extract frame", offthread-video-server),
/// the failure says so in plain words and lists the videos that ARE staged, with their sizes.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Added to a failure whose output shows Remotion could not read a frame of a video.</summary>
    internal const string VideoReadHint =
        "Remotion could not read a frame from a video file the composition plays (<OffthreadVideo>/<Video> of a staticFile). " +
        "That file is missing from public/, in a codec the renderer cannot decode, or too large. Compare every video path " +
        "in the composition with stagedVideos: stage a missing one with StageProjectFile, using the exact id or file name " +
        "ListProjectFiles shows. If the file is staged, use another of the project's video files (an H.264 MP4 decodes " +
        "best) or play a shorter part of it. stagedVideos gives each staged video's size in pixels; failingVideos names the " +
        "ones the renderer was reading when it gave up.";

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".mpg", ".mpeg", ".ogv",
    };

    private const int MaxListedVideos = 30;
    private const int MaxVideoListDirectories = 50;
    private const int MaxVideoListDepth = 6;

    /// <summary>
    /// The <c>MISSING_STATIC_FILE</c> refusal (<c>{"ok": false, "error", "missing"}</c>) when the composition
    /// points <c>staticFile()</c> at a file not under <c>public/</c>; null when every target is there, or when
    /// the check could not be made (the command then runs exactly as before).
    /// </summary>
    private async Task<string?> MissingStaticFileRefusalAsync(ISandboxSession session)
    {
        IReadOnlyList<MissingStaticFile>? missing;
        try
        {
            missing = await StaticFilePreflight.FindMissingAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "staticFile preflight could not run for execution {ExecutionId}; proceeding", session.ExecutionId);
            return null;
        }

        if (missing is null || missing.Count == 0)
            return null;

        _logger.LogInformation(
            "Refused a sandbox command for execution {ExecutionId}: {Count} staticFile target(s) missing from public/: {Missing}",
            session.ExecutionId,
            missing.Count,
            string.Join(", ", missing.Take(StaticFilePreflight.MaxReported).Select(m => m.StaticPath)));

        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = StaticFilePreflight.RefusalMessage(missing),
            missing = missing.Take(StaticFilePreflight.MaxReported)
                .Select(m => new { path = "public/" + m.StaticPath, usedIn = $"{m.SourceFile}:{m.Line}" })
                .ToArray()
        }, ResultJsonOptions);
    }

    /// <summary>
    /// The <c>HTML5_VIDEO_IN_RENDER</c> refusal (<c>{"ok": false, "error", "uses"}</c>) when the composition plays a
    /// clip through remotion's HTML5 <c>&lt;Video&gt;</c>/<c>&lt;Html5Video&gt;</c> (<see cref="Html5VideoPreflight"/>);
    /// null when it does not, or when the check could not be made (the render then runs exactly as before).
    /// </summary>
    private async Task<string?> Html5VideoRefusalAsync(ISandboxSession session)
    {
        IReadOnlyList<Html5VideoUse>? uses;
        try
        {
            uses = await Html5VideoPreflight.FindAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HTML5 video preflight could not run for execution {ExecutionId}; proceeding", session.ExecutionId);
            return null;
        }

        if (uses is null || uses.Count == 0)
            return null;

        _logger.LogInformation(
            "Refused a render for execution {ExecutionId}: {Count} use(s) of remotion's HTML5 video element: {Uses}",
            session.ExecutionId,
            uses.Count,
            string.Join(", ", uses.Take(Html5VideoPreflight.MaxReported).Select(u => $"{u.SourceFile}:{u.Line}")));

        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = Html5VideoPreflight.RefusalMessage(uses),
            uses = uses.Take(Html5VideoPreflight.MaxReported)
                .Select(u => new { component = u.Component, usedIn = $"{u.SourceFile}:{u.Line}" })
                .ToArray()
        }, ResultJsonOptions);
    }

    /// <summary>Whether a failed command's output shows Remotion failing to read a video frame.</summary>
    internal static bool IsVideoFrameReadFailure(string? error, string? output)
    {
        string combined = $"{error}\n{output}";
        return combined.Contains("Could not extract frame", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("offthread-video-server", StringComparison.OrdinalIgnoreCase)
            // A frame the compositor did not deliver in time: delayRender() on Remotion's /proxy?src= frame route.
            || (combined.Contains("delayRender", StringComparison.Ordinal)
                && combined.Contains("/proxy?src=", StringComparison.Ordinal));
    }

    private static readonly System.Text.RegularExpressions.Regex ProxySrcPattern = new(
        @"proxy\?src=([^&\s""']+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The <c>public/...</c> paths of the videos a failed render names (Remotion's <c>/proxy?src=</c> frame
    /// URLs, and any staged video path quoted in the text), each once, at most ten.
    /// </summary>
    internal static IReadOnlyList<string> FailingVideoPaths(string? error, string? output, IEnumerable<string> stagedPaths)
    {
        string combined = $"{error}\n{output}";
        List<string> found = [];
        foreach (System.Text.RegularExpressions.Match match in ProxySrcPattern.Matches(combined))
        {
            string src = Uri.UnescapeDataString(match.Groups[1].Value);
            int at = src.IndexOf("/public/", StringComparison.Ordinal);
            if (at >= 0)
                found.Add(src[(at + 1)..]);
        }
        foreach (string path in stagedPaths)
        {
            if (path.StartsWith(StaticFileDirectory, StringComparison.Ordinal)
                && combined.Contains(path[StaticFileDirectory.Length..], StringComparison.Ordinal))
                found.Add(path);
        }
        return [.. found.Distinct(StringComparer.Ordinal).Take(10)];
    }

    /// <summary>
    /// <paramref name="invocation"/>'s error response, with <c>videoHint</c> and <c>stagedVideos</c> added when
    /// the failure is a video frame read; the response unchanged otherwise (or if anything here fails).
    /// </summary>
    private async Task<object?> WithVideoReadHintAsync(ISandboxSession session, SandboxCommandInvocation invocation)
    {
        if (!IsVideoFrameReadFailure(invocation.Payload?.Error, invocation.Payload?.Output))
            return invocation.ErrorResponse;

        try
        {
            if (JsonSerializer.SerializeToNode(invocation.ErrorResponse, ResultJsonOptions) is not JsonObject node)
                return invocation.ErrorResponse;

            IReadOnlyList<(string Path, long Size)> videos = await ListStagedVideosAsync(session);
            Dictionary<string, StagedVideoFacts> facts = await ReadStagedVideoManifestAsync(session);
            node["videoHint"] = VideoReadHint;
            node["stagedVideos"] = new JsonArray([
                .. videos.Select(v => (JsonNode)DescribeVideoForFailure(v.Path, v.Size, facts))
            ]);

            IReadOnlyList<string> failing = FailingVideoPaths(
                invocation.Payload?.Error, invocation.Payload?.Output, videos.Select(v => v.Path));
            if (failing.Count > 0)
            {
                node["failingVideos"] = new JsonArray([
                    .. failing.Select(path => (JsonNode)DescribeVideoForFailure(
                        path, videos.FirstOrDefault(v => v.Path == path).Size, facts))
                ]);
                node["videoHint"] = "Remotion could not read a frame of " + string.Join(", ", failing.Select(path =>
                    facts.TryGetValue(path, out StagedVideoFacts? f) && f.Width > 0
                        ? $"{path} ({f.Width}x{f.Height})"
                        : path)) + ". " + VideoReadHint;
            }
            return node;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not add the video read hint for execution {ExecutionId}", session.ExecutionId);
            return invocation.ErrorResponse;
        }
    }

    private static JsonObject DescribeVideoForFailure(string path, long size, Dictionary<string, StagedVideoFacts> facts)
    {
        JsonObject entry = new() { ["path"] = path, ["sizeBytes"] = size };
        if (facts.TryGetValue(path, out StagedVideoFacts? f) && f.Width > 0 && f.Height > 0)
        {
            entry["width"] = f.Width;
            entry["height"] = f.Height;
            if (FpsText(f.FpsNum, f.FpsDen) is { } fps)
                entry["fps"] = fps;
            entry["proxied"] = f.Proxied;
            if (f.Proxied)
            {
                entry["sourceWidth"] = f.SourceWidth;
                entry["sourceHeight"] = f.SourceHeight;
            }
        }
        return entry;
    }

    /// <summary>The video files under <c>public/</c> (bounded walk), path and size; empty when nothing can be listed.</summary>
    private static async Task<IReadOnlyList<(string Path, long Size)>> ListStagedVideosAsync(ISandboxSession session)
    {
        var videos = new List<(string Path, long Size)>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((StaticFileDirectory.TrimEnd('/'), 0));
        int directories = 0;

        while (pending.Count > 0 && videos.Count < MaxListedVideos && ++directories <= MaxVideoListDirectories)
        {
            (string directory, int depth) = pending.Dequeue();
            IReadOnlyList<SandboxFileEntry> entries;
            try
            {
                entries = await session.ListFilesAsync(directory, CancellationToken.None);
            }
            catch (SandboxRequestFailedException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            foreach (SandboxFileEntry entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                string path = directory + "/" + entry.Name;
                if (entry.IsDir)
                {
                    if (depth + 1 <= MaxVideoListDepth && !entry.Name.StartsWith('.'))
                        pending.Enqueue((path, depth + 1));
                }
                else if (VideoExtensions.Contains(Path.GetExtension(entry.Name)) && videos.Count < MaxListedVideos)
                {
                    videos.Add((path, entry.Size));
                }
            }
        }

        return videos;
    }
}
