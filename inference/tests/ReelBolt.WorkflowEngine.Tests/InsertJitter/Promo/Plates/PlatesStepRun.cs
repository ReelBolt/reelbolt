#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>One ffmpeg/ffprobe process the step started, as the tool runner saw it.</summary>
internal sealed record ToolCall(string Tool, string[] Argv, double WallSec, double StartedAtSec);

/// <summary>What the insert-tracking part of one step run cost (the window between the step's "Tracking insert regions" progress report and the last "Insert region" one).</summary>
internal sealed record InsertPassCost(
    double WallSec, double DecodeWallSec, double TrackerWallSec,
    double SelfCpuSec, double ChildCpuSec,
    double RssBaselineMb, double RssPeakMb, double GridMb, bool Measured, string DecodeVf = "");

internal sealed record StepRunResult(
    string Status, string? Error, string ArtifactJson, IReadOnlyList<ToolCall> Calls, IReadOnlyList<ToolCall> SamplerCalls, InsertPassCost Cost,
    IReadOnlyList<string> Progress, string ConfigJson, double TotalWallSec);

/// <summary>
/// Runs the REAL <see cref="VideoAnalyzeStepExecutor"/> on one raw plate with the staging step configuration (detectInsertRegions, insertRegionColor green, defaults
/// otherwise), exactly the executor the engine runs: real ffprobe/ffmpeg, real <see cref="FfmpegFrameGridSampler"/>, real detectors. Only storage is faked (the plate is
/// "downloaded" from a read-only file; the analysis artifact is captured instead of uploaded). Because the whole step runs, whatever the step does for the insert pass
/// (grid decode flags, tracker options, smoothing, anything wired later) is what is measured; nothing of the pass is mirrored here.
/// <para>
/// The tool runner and the frame-grid sampler are wrapped in reflection proxies (<see cref="DispatchProxy"/>) rather than hand-written decorators, so a changed
/// interface member (an added <c>GridChroma</c> parameter, say) needs no edit here and is recorded in the report: the ffmpeg argv of every call and the sampler's
/// parameter values are written to <c>results.json</c>.
/// </para>
/// </summary>
internal static class PlatesStepRun
{
    public const string StagingStepConfigPath = "/tmp/insert-tracking/promo/stepconfig-order5.json";

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The staging step's analyze config, with its two sources reduced to the plate (<c>StepOutput</c> of step 1), and the insert pass switched as asked.</summary>
    public static VideoAnalyzeStepConfig StagingConfig(bool detectInsertRegions = true)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(StagingStepConfigPath))!;
        string cfgText = root["videoAnalyzeConfigJson"]!.GetValue<string>();
        JsonObject cfg = JsonNode.Parse(cfgText)!.AsObject();
        cfg.Remove("sources");
        cfg["detectInsertRegions"] = detectInsertRegions;
        return JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(cfg.ToJsonString(), ConfigOptions)!;
    }

    public static string StagingConfigText() => JsonSerializer.Serialize(StagingConfig(), ConfigOptions);

    public static async Task<StepRunResult> RunAsync(string platePath, VideoAnalyzeStepConfig config, string scratchRoot)
    {
        Guid projectId = Guid.Parse("8a7d0dcd-8f3e-447b-9c15-dfb3ec788293");
        Guid executionId = Guid.NewGuid();
        string plateKey = $"projects/{projectId}/agentFiles/generated/{executionId}/step-1-v1.mp4";
        string? artifactJson = null;

        var projectFiles = new Mock<IProjectFileWorkspace>();
        projectFiles.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        projectFiles
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                if (key != plateKey)
                    throw new FileNotFoundException("the harness only serves the plate: " + key);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(platePath, dest, overwrite: true);
                return Task.CompletedTask;
            });
        projectFiles
            .Setup(w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Returns<Guid, string, string, string, CancellationToken, string>((pid, localPath, fileName, _, _, category) =>
            {
                if (fileName.EndsWith("analysis.json", StringComparison.Ordinal) && fileName.Contains("step-", StringComparison.Ordinal))
                    artifactJson = File.ReadAllText(localPath);
                return Task.FromResult($"projects/{pid}/{category}/{fileName}");
            });

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = scratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 600,
            CompileTimeoutSeconds = 600
        });
        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        IVideoToolRunner runner = ForwardingProxy.Create<IVideoToolRunner>(real, out ToolCallRecorder recorder);
        var realSampler = new FfmpegFrameGridSampler(runner, options, NullLogger<FfmpegFrameGridSampler>.Instance);
        IFrameGridSampler sampler = ForwardingProxy.Create<IFrameGridSampler>(realSampler, out ToolCallRecorder samplerRecorder);
        var probe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance);

        var executor = new VideoAnalyzeStepExecutor(
            probe,
            new FfmpegSilenceDetector(runner, options, NullLogger<FfmpegSilenceDetector>.Instance),
            new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance),
            new FfmpegAudioExtractor(runner, options, NullLogger<FfmpegAudioExtractor>.Instance),
            sampler,
            new FfmpegKeyframeExtractor(runner, options, NullLogger<FfmpegKeyframeExtractor>.Instance),
            new FfmpegSharpnessSampler(runner, options, NullLogger<FfmpegSharpnessSampler>.Instance),
            new Mock<IShotCaptioner>().Object,
            projectFiles.Object,
            new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IInferenceProviderResolver>().Object,
            options,
            NullLogger<VideoAnalyzeStepExecutor>.Instance,
            videoToolRunner: runner);

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, ConfigOptions)
        };

        var window = new PassWindow();
        var progress = new List<string>();
        var ctx = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = executionId, ProjectId = projectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "{}",
            StepOutputHistory = [new(1, "plate", "{}", OutputStorageKey: plateKey)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "plates-harness",
            CancellationToken = CancellationToken.None,
            ProgressReporter = (label, _, _, _, _, _) =>
            {
                progress.Add(label);
                window.OnProgress(label);
                return Task.CompletedTask;
            }
        };

        recorder.Start();
        var sw = Stopwatch.StartNew();
        StepExecutionResult result = await executor.ExecuteAsync(ctx);
        double total = sw.Elapsed.TotalSeconds;
        if (artifactJson is null)
            throw new InvalidOperationException("the step produced no analysis artifact (status " + result.Status + "): " + result.ErrorDetails);

        IReadOnlyList<ToolCall> calls = recorder.Calls;
        InsertPassCost cost = window.ToCost(calls, samplerRecorder);
        return new StepRunResult(result.Status.ToString(), result.ErrorDetails, artifactJson, calls, samplerRecorder.Calls, cost, progress, JsonSerializer.Serialize(config, ConfigOptions), total);
    }

    // -------------------------------------------------------------------------------------------------- cost window

    /// <summary>Process CPU, children CPU and resident-set peak between the step's two insert-pass progress reports.</summary>
    private sealed class PassWindow
    {
        private bool _open;
        private double _t0, _selfCpu0, _childCpu0, _rssBase;
        private (double T, double Self, double Child, double PeakMb)? _end;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void OnProgress(string label)
        {
            if (!_open && label.StartsWith("Tracking insert regions", StringComparison.Ordinal))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                ProcStat.ResetPeak();
                _rssBase = ProcStat.RssMb();
                (_selfCpu0, _childCpu0) = ProcStat.Cpu();
                _t0 = _clock.Elapsed.TotalSeconds;
                _open = true;
                _end = null;
                return;
            }

            if (!_open)
                return;
            // The window closes at the last "Insert region ..." report; when nothing was found it closes at the first report after the start.
            bool regionReport = label.StartsWith("Insert region", StringComparison.Ordinal);
            if (regionReport || _end is null)
            {
                (double s, double c) = ProcStat.Cpu();
                _end = (_clock.Elapsed.TotalSeconds, s, c, ProcStat.PeakMb());
            }
        }

        public InsertPassCost ToCost(IReadOnlyList<ToolCall> calls, ToolCallRecorder samplerRecorder)
        {
            if (!_open || _end is not { } e)
                return new InsertPassCost(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false);
            double wall = e.T - _t0;
            // The insert grid decode is the ffmpeg process the sampler ran inside the window with the biggest raw output; the visual grid is 32x18.
            ToolCall? decode = calls.Where(c => c.Tool == "ffmpeg" && c.StartedAtSec >= _t0 - 0.05 && c.StartedAtSec <= e.T && c.Argv.Contains("rawvideo")).OrderByDescending(c => c.WallSec).FirstOrDefault();
            double decodeWall = decode?.WallSec ?? 0;
            double gridMb = samplerRecorder.GridBytes / 1048576.0;
            int vf = decode is null ? -1 : Array.IndexOf(decode.Argv, "-vf");
            string decodeVf = vf >= 0 && vf + 1 < decode!.Argv.Length ? decode.Argv[vf + 1] : string.Empty;
            return new InsertPassCost(wall, decodeWall, wall - decodeWall, e.Self - _selfCpu0, e.Child - _childCpu0, _rssBase, e.PeakMb, gridMb, true, decodeVf);
        }
    }

    // -------------------------------------------------------------------------------------------------- proxy

    internal sealed class ToolCallRecorder
    {
        private readonly Stopwatch _clock = new();
        private readonly List<ToolCall> _calls = [];

        public void Start() => _clock.Restart();

        public IReadOnlyList<ToolCall> Calls
        {
            get
            {
                lock (_calls)
                    return _calls.ToList();
            }
        }

        /// <summary>Bytes of the largest raw grid the wrapped sampler returned (frames x grid x 3).</summary>
        public long GridBytes { get; set; }

        public void Add(ToolCall c)
        {
            lock (_calls)
                _calls.Add(c);
        }

        public double Now => _clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Forwards every call of <typeparamref name="T"/> to a target by reflection. For a tool runner each ffmpeg/ffprobe invocation that reaches the base overloads is
    /// recorded once (argv, wall time); for the frame-grid sampler the parameter values of each call are recorded as a pseudo-call and the size of the returned grid kept.
    /// </summary>
    internal class ForwardingProxy : DispatchProxy
    {
        private object _target = null!;
        private ToolCallRecorder _recorder = null!;

        public static T Create<T>(T target, out ToolCallRecorder recorder)
            where T : class
        {
            T proxy = Create<T, ForwardingProxy>();
            var fp = (ForwardingProxy)(object)proxy;
            fp._target = target;
            fp._recorder = recorder = new ToolCallRecorder();
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ParameterInfo[] ps = targetMethod!.GetParameters();
            double started = _recorder.Now;
            var sw = Stopwatch.StartNew();
            object? ret;
            try
            {
                ret = targetMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            bool tool = targetMethod.Name is "RunFfmpegAsync" or "RunFfprobeAsync";
            bool viaWorkspace = ps.Any(p => p.ParameterType == typeof(IMediaWorkspace));
            string[]? argv = args?.OfType<IReadOnlyList<string>>().FirstOrDefault()?.ToArray();
            if (tool && !viaWorkspace && argv is not null && ret is Task t)
            {
                string name = targetMethod.Name == "RunFfmpegAsync" ? "ffmpeg" : "ffprobe";
                t.ContinueWith(_ => _recorder.Add(new ToolCall(name, argv, sw.Elapsed.TotalSeconds, started)), TaskContinuationOptions.ExecuteSynchronously);
            }
            else if (targetMethod.Name.StartsWith("Sample", StringComparison.Ordinal) && ret is Task<FrameGridResult> grid)
            {
                string desc = string.Join(", ", ps.Zip(args ?? [], (p, v) => v is IMediaWorkspace or CancellationToken ? null : $"{p.Name}={Format(v)}").Where(s => s is not null));
                grid.ContinueWith(g =>
                {
                    if (g.IsCompletedSuccessfully)
                        _recorder.GridBytes = Math.Max(_recorder.GridBytes, g.Result.PixelData.LongLength);
                    _recorder.Add(new ToolCall("sampler:" + targetMethod.Name, [desc], sw.Elapsed.TotalSeconds, started));
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            return ret;
        }

        private static string Format(object? v) => v switch
        {
            null => "null",
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? string.Empty
        };
    }

    // -------------------------------------------------------------------------------------------------- /proc

    internal static class ProcStat
    {
        private const double Ticks = 100.0; // CLK_TCK on Linux

        public static (double Self, double Child) Cpu()
        {
            try
            {
                string stat = File.ReadAllText("/proc/self/stat");
                string[] f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                double v(int i) => double.Parse(f[i], CultureInfo.InvariantCulture) / Ticks;
                return (v(11) + v(12), v(13) + v(14));
            }
            catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or UnauthorizedAccessException)
            {
                return (Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds, double.NaN);
            }
        }

        public static void ResetPeak()
        {
            try
            {
                File.WriteAllText("/proc/self/clear_refs", "5");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Falls back to the process-lifetime peak (reported as such by PeakMb being >= RssMb).
            }
        }

        public static double RssMb() => Kb("VmRSS") / 1024.0;

        public static double PeakMb() => Kb("VmHWM") / 1024.0;

        private static double Kb(string key)
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/self/status"))
                {
                    if (line.StartsWith(key + ":", StringComparison.Ordinal))
                        return double.Parse(line.Split(':')[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
            }

            return double.NaN;
        }
    }
}
