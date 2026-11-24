using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// Keeps a full Remotion render inside the sandbox's memory limit.
///
/// <para>
/// Left to itself Remotion 4 opens one browser tab per half of the HOST's cores (it reads
/// <c>os.cpus()</c>, not the container's CPU quota) and gives the <c>&lt;OffthreadVideo&gt;</c> frame
/// cache half of the memory free when the render starts (again the host's view). In a sandbox
/// capped at a few GB, a composition that plays several staged videos at once then has the
/// kernel kill the compositor (<c>Compositor quit with signal SIGKILL</c>, a memory-cgroup OOM)
/// and the render exits 1. So every render through <c>RenderVideoAndUploadToStorage</c> gets
/// <c>--concurrency</c> and <c>--offthreadvideo-cache-size-in-bytes</c> unless the model set them
/// (both exist in the pinned Remotion 4.0.542; the cache flag since 4.0.23).
/// </para>
///
/// <para>
/// A render that is still killed is retried once inside the same tool call with
/// <c>--concurrency=1</c> and half the cache. That retry is not a second render for the
/// render-once guard and never adds a failure of its own to the failed-render cap: the call
/// records at most one failure, the retry's.
/// </para>
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Configuration key of the default number of frames Remotion renders in parallel.</summary>
    internal const string RenderConcurrencyKey = "Compute:Render:DefaultConcurrency";

    /// <summary>Configuration key of the default <c>&lt;OffthreadVideo&gt;</c> frame cache size, in bytes.</summary>
    internal const string OffthreadVideoCacheBytesKey = "Compute:Render:OffthreadVideoCacheBytes";

    /// <summary>Two tabs: matches the sandbox's default 2-CPU quota, so it costs no speed there.</summary>
    internal const int DefaultRenderConcurrency = 2;

    /// <summary>256 MiB of decoded video frames, well inside a 2-4 GB sandbox next to Chromium itself.</summary>
    internal const long DefaultOffthreadVideoCacheBytes = 256L * 1024 * 1024;

    /// <summary>The smallest cache the out-of-memory retry asks for.</summary>
    internal const long MinOutOfMemoryRetryCacheBytes = 32L * 1024 * 1024;

    /// <summary>One tab renders about half as fast as two, so the retry's deadline is doubled (still capped).</summary>
    internal const double OutOfMemoryRetryDeadlineFactor = 2.0;

    internal const string ConcurrencyFlag = "--concurrency";
    internal const string OffthreadVideoCacheFlag = "--offthreadvideo-cache-size-in-bytes";

    /// <summary>The plain sentence the model gets when the retry ran out of memory too.</summary>
    internal const string OutOfMemoryRenderMessage =
        "The video renderer ran out of memory inside the sandbox, even after it was retried automatically with " +
        "one frame at a time (--concurrency=1) and a smaller video cache.";

    internal static readonly string[] OutOfMemoryRenderGuidance =
    [
        "Have fewer videos decoding at the same moment: do not play several <OffthreadVideo>/<Video> clips on top of each other, " +
        "and wrap each clip in a <Sequence> that lasts only as long as it is on screen, so it is not mounted the rest of the time.",
        "Use shorter source clips, or lower-resolution versions of them if the project has them, and play only the part you need " +
        "(trimBefore/trimAfter or startFrom/endAt) instead of a whole long file.",
        "Keep the real footage: do not replace the videos with drawn or animated stand-ins.",
        "Then call RenderVideoAndUploadToStorage again."
    ];

    /// <summary>Default <c>--concurrency</c> for a render whose args do not set one.</summary>
    private readonly int _renderConcurrency;

    /// <summary>Default <c>--offthreadvideo-cache-size-in-bytes</c> for a render whose args do not set one.</summary>
    private readonly long _offthreadVideoCacheBytes;

    /// <summary>The largest cache a render is sized to from its staged videos (see ReactRemotionSandboxTools.VideoProxy.cs).</summary>
    private readonly long _offthreadVideoCacheMaxBytes;

    /// <summary>The configured longest side a staged video keeps before a delivery copy is made.</summary>
    private readonly int _stagedVideoMaxLongSide;

    /// <summary>Runs one full render (<c>npx remotion render ...</c>), its time excluded from the agent's own run budget.</summary>
    private async Task<SandboxCommandInvocation> RunFullRenderAsync(ISandboxSession session, IReadOnlyList<string> args, int timeoutSeconds)
    {
        // A long film legitimately renders for hours and the render's own deadline already bounds
        // it, so its time does not count against the agent's run timeout.
        using (AgentRunBudget.Current?.Exclude(TimeSpan.FromSeconds(timeoutSeconds + SandboxCommandTimeoutHeadroomSeconds)))
        {
            return await ExecuteSandboxCommandInternal(
                session,
                new SandboxExecRequest("npx", ["remotion", "render", .. args], TimeoutSeconds: timeoutSeconds),
                operation: "render");
        }
    }

    /// <summary>Whether a failed render's own text carries a kill / out-of-memory marker.</summary>
    private static bool RenderWasKilled(SandboxCommandInvocation result) =>
        !result.Success && ReadRenderFailureSignals(result.Payload?.Error, result.Payload?.Output).Killed;

    /// <summary>
    /// <paramref name="args"/> with <c>--concurrency=<paramref name="concurrency"/></c> and
    /// <c>--offthreadvideo-cache-size-in-bytes=<paramref name="cacheBytes"/></c> appended, each only
    /// when the args do not already set that flag (either <c>--flag=v</c> or <c>--flag v</c>): a
    /// value the model chose is kept as it is.
    /// </summary>
    internal static List<string> WithRenderMemoryDefaults(IReadOnlyList<string> args, int concurrency, long cacheBytes)
    {
        List<string> result = [.. args];
        if (!HasFlag(args, ConcurrencyFlag))
            result.Add(ConcurrencyFlag + "=" + concurrency.ToString(CultureInfo.InvariantCulture));
        if (!HasFlag(args, OffthreadVideoCacheFlag))
            result.Add(OffthreadVideoCacheFlag + "=" + cacheBytes.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>
    /// The args of the one out-of-memory retry: <paramref name="args"/> without any
    /// <c>--concurrency</c> or cache flag, plus <c>--concurrency=1</c> and half the cache the killed
    /// render used (the model's number when it gave one, else <paramref name="defaultCacheBytes"/>),
    /// never below <see cref="MinOutOfMemoryRetryCacheBytes"/> and never above what it used.
    /// </summary>
    internal static List<string> OutOfMemoryRetryArgs(IReadOnlyList<string> args, long defaultCacheBytes)
    {
        long usedCache = FlagValue(args, OffthreadVideoCacheFlag) is { } text
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
                ? parsed
                : defaultCacheBytes;
        long retryCache = Math.Min(usedCache, Math.Max(MinOutOfMemoryRetryCacheBytes, usedCache / 2));

        List<string> result = WithoutFlags(args, ConcurrencyFlag, OffthreadVideoCacheFlag);
        result.Add(ConcurrencyFlag + "=1");
        result.Add(OffthreadVideoCacheFlag + "=" + retryCache.ToString(CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>Share of the render deadline after which a kill is the executor's timeout rather than the kernel's out-of-memory kill.</summary>
    internal const double RenderDeadlineKillFraction = 0.95;

    /// <summary>Whether a render that ran for <paramref name="elapsed"/> under a <paramref name="timeoutSeconds"/> deadline was stopped by that deadline.</summary>
    internal static bool RenderReachedDeadline(TimeSpan elapsed, int timeoutSeconds) =>
        timeoutSeconds > 0 && elapsed.TotalSeconds >= timeoutSeconds * RenderDeadlineKillFraction;

    /// <summary>The model-facing error of a render stopped at its deadline: the tool's usual error with a timeout sentence, not the memory one.</summary>
    private static string RenderTimeoutErrorJson(object? errorResponse, int deadlineSeconds)
    {
        JsonObject node = errorResponse is null
            ? new JsonObject { ["success"] = false }
            : JsonSerializer.SerializeToNode(errorResponse, ResultJsonOptions) as JsonObject ?? new JsonObject { ["success"] = false };

        node["timedOut"] = true;
        node["message"] = $"The video renderer did not finish within its time limit ({deadlineSeconds.ToString(CultureInfo.InvariantCulture)} seconds) and was stopped. " +
            "It was not retried. Make the render lighter (a shorter composition, fewer simultaneous videos or effects) or render it in smaller parts.";
        return node.ToJsonString(ResultJsonOptions);
    }

    /// <summary>The model-facing error of a render that ran out of memory twice: the tool's usual error with the plain sentence and memory guidance.</summary>
    private static string OutOfMemoryErrorJson(object? errorResponse)
    {
        JsonObject node = errorResponse is null
            ? new JsonObject { ["success"] = false }
            : JsonSerializer.SerializeToNode(errorResponse, ResultJsonOptions) as JsonObject ?? new JsonObject { ["success"] = false };

        node["outOfMemory"] = true;
        node["message"] = OutOfMemoryRenderMessage;
        node["guidance"] = new JsonArray([.. OutOfMemoryRenderGuidance.Select(g => (JsonNode)g)]);
        return node.ToJsonString(ResultJsonOptions);
    }

    /// <summary>The memory flags in <paramref name="args"/>, for the log.</summary>
    private static IEnumerable<string> MemoryFlagsOf(IReadOnlyList<string> args) =>
        args.Where(a => IsFlag(a, ConcurrencyFlag) || IsFlag(a, OffthreadVideoCacheFlag));

    private static bool IsFlag(string arg, string flag) =>
        arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal);

    private static bool HasFlag(IReadOnlyList<string> args, string flag) => args.Any(a => IsFlag(a, flag));

    private static string? FlagValue(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
                return args[i][(flag.Length + 1)..];
            if (args[i] == flag && i + 1 < args.Count)
                return args[i + 1];
        }
        return null;
    }

    private static List<string> WithoutFlags(IReadOnlyList<string> args, params string[] flags)
    {
        List<string> result = [];
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (flags.Contains(arg, StringComparer.Ordinal))
            {
                // The flag's value is the next element unless that is itself a flag.
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    i++;
                continue;
            }

            if (flags.Any(flag => arg.StartsWith(flag + "=", StringComparison.Ordinal)))
                continue;

            result.Add(arg);
        }
        return result;
    }
}
