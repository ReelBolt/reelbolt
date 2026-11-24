using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Captures the exact ffmpeg encode a tracked-insert compile builds, against a REAL analysis
/// artifact and real clips, so its memory can be measured outside the test process (see
/// docs/video-editing.md "Memory: every branch starts at zero"). Skipped unless
/// <c>REELBOLT_INSERT_MEMORY_DIR</c> names a directory holding <c>analysis.json</c>, one
/// <c>&lt;projectFileId&gt;.mp4</c> per analyzed source (named after the id in the source's
/// storage key) and <c>content.mp4</c> (the insert content). Writes
/// <c>&lt;dir&gt;/capture-&lt;label&gt;/</c>: a copy of the compile's scratch space and
/// <c>argv.json</c>, the encode's argv with every scratch path rewritten into that copy.
/// <c>REELBOLT_INSERT_MEMORY_SOURCES</c> (comma-separated 0-based source indices) compiles a
/// subset — e.g. <c>0,1</c> for a two-clip composite; <c>REELBOLT_INSERT_MEMORY_LABEL</c>
/// names the capture.
/// </summary>
public sealed class InsertCompileMemoryHarness
{
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private static readonly Guid ContentFileId = Guid.Parse("9063a52b-80a7-4ae5-8e3a-bbe3e25470cf");

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Capture_the_tracked_insert_encode()
    {
        string? dir = Environment.GetEnvironmentVariable("REELBOLT_INSERT_MEMORY_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "analysis.json")))
            return; // gated harness, not a regular test

        string label = Environment.GetEnvironmentVariable("REELBOLT_INSERT_MEMORY_LABEL") ?? "all";
        string capture = Path.Combine(dir, "capture-" + label);
        if (Directory.Exists(capture))
            Directory.Delete(capture, recursive: true);
        Directory.CreateDirectory(capture);

        VideoAnalysisArtifact artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(
            File.ReadAllText(Path.Combine(dir, "analysis.json")), ConfigOptions)!;
        if (Environment.GetEnvironmentVariable("REELBOLT_INSERT_MEMORY_SOURCES") is { Length: > 0 } subset)
            artifact = Subset(artifact, subset.Split(',').Select(int.Parse).ToHashSet());
        string artifactJson = JsonSerializer.Serialize(artifact, ConfigOptions);

        Guid projectId = Guid.NewGuid();
        string contentKey = $"projects/{projectId}/userFiles/screens/{ContentFileId}.mp4";
        var workspace = new Mock<IProjectFileWorkspace>();
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ProjectWorkspaceFile(ContentFileId, projectId, "app-ui.mp4", null, "userFiles",
                contentKey, "video/mp4", new FileInfo(Path.Combine(dir, "content.mp4")).Length, DateTime.UnixEpoch, null)]);
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                if (key == AnalysisKey)
                    File.WriteAllText(dest, artifactJson);
                else if (key == contentKey)
                    File.Copy(Path.Combine(dir, "content.mp4"), dest, overwrite: true);
                else
                    File.Copy(Path.Combine(dir, Path.GetFileName(key)), dest, overwrite: true);
                return Task.CompletedTask;
            });
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>()))
            // D9b: the compiled program is published through IMediaWorkspace.PublishAsync, which
            // verifies that what the object store round-tripped IS the key it asked for — so the
            // stub answers with the key it was handed rather than a canned string.
            .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                $"projects/{pid}/{category}/{fileName}");

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "insert-compile-memory", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 600,
            CompileTimeoutSeconds = 600
        });
        var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        var runner = new CapturingRunner(real, capture);
        var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);

        var executor = new VideoCompileStepExecutor(
            runner, probe, workspace.Object, new Mock<IServiceScopeFactory>().Object, options,
            NullLogger<VideoCompileStepExecutor>.Instance);

        var config = new VideoCompileStepConfig(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Previous),
            AnalysisStepOrder: 1) with
        {
            Mode = VideoCompileMode.Reencode,
            KeepWholeSources = true,
            EnableInserts = true,
            InsertContentProjectFileId = ContentFileId,
            InsertFit = InsertContentFit.Auto,
            InsertReflections = true,
            EnableInsertMatte = true,
            Crf = 16,
            Preset = "slow",
            RegisterProjectFile = false,
            TransitionPolicy = VideoTransitionPolicy.Off,
            OutputFormat = VideoOutputFormat.Landscape,
            OutputFit = VideoOutputFit.Crop,
            MaxInserts = 6
        };
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions)
        };

        await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "{}",
            StepOutputHistory = [new(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey)],
            CurrentStepIndex = 1,
            IterationCount = 0,
            CorrelationId = "memory-harness",
            CancellationToken = CancellationToken.None
        });

        Assert.True(File.Exists(Path.Combine(capture, "argv.json")), "the encode was never reached");
    }

    /// <summary>The artifact restricted to <paramref name="keep"/>'s sources, re-indexed from zero.</summary>
    private static VideoAnalysisArtifact Subset(VideoAnalysisArtifact a, IReadOnlySet<int> keep)
    {
        var map = a.Sources!.Where(s => keep.Contains(s.SourceIndex)).Select((s, n) => (s.SourceIndex, n))
            .ToDictionary(x => x.SourceIndex, x => x.n);
        return a with
        {
            Sources = a.Sources!.Where(s => map.ContainsKey(s.SourceIndex)).Select(s => s with { SourceIndex = map[s.SourceIndex] }).ToList(),
            Shots = a.Shots.Where(s => map.ContainsKey(s.SourceIndex)).Select(s => s with { SourceIndex = map[s.SourceIndex] }).ToList(),
            InsertRegions = a.InsertRegions?.Where(r => map.ContainsKey(r.SourceIndex)).Select(r => r with { SourceIndex = map[r.SourceIndex] }).ToList(),
        };
    }

    /// <summary>Passes every call through, except the encode: its scratch space and argv are copied into <c>capture</c> and it is not run.</summary>
    private sealed class CapturingRunner(IVideoToolRunner inner, string capture) : IVideoToolRunner
    {
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            IsEncode(args) ? Task.FromResult(Capture(args)) : inner.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            IsEncode(args) ? Task.FromResult(Capture(args)) : inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            inner.RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            workspace.RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(
            IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
            Action<string> onStdOutLine) =>
            workspace.RunFfmpegAsync(args, timeout, ct, onStdOutLine);
        // Priority-carrying overloads (editor v2 derivatives scheduling): same capture rule.
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            IsEncode(args) ? Task.FromResult(Capture(args)) : inner.RunFfmpegAsync(args, timeout, ct, priority);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) =>
            IsEncode(args) ? Task.FromResult(Capture(args)) : inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine, priority);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            inner.RunFfprobeAsync(args, timeout, ct, priority);

        private static bool IsEncode(IReadOnlyList<string> args) =>
            args.Contains("-filter_complex_script") || (args.Contains("-map") && args.Contains("-filter_complex"));

        private VideoToolResult Capture(IReadOnlyList<string> args)
        {
            int script = args.ToList().IndexOf("-filter_complex_script");
            string scratch = Path.GetDirectoryName(script >= 0 ? args[script + 1] : args[^1])!;
            foreach (string file in Directory.GetFiles(scratch))
                File.Copy(file, Path.Combine(capture, Path.GetFileName(file)), overwrite: true);
            var rewritten = args.Select(a => a.Replace(scratch, capture, StringComparison.Ordinal)).ToList();
            File.WriteAllText(Path.Combine(capture, "argv.json"), JsonSerializer.Serialize(rewritten));
            return new VideoToolResult(1, string.Empty, "captured, not run", false);
        }
    }
}
