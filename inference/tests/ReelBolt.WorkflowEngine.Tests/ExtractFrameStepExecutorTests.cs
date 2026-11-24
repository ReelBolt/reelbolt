using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: <c>StepType.ExtractFrame</c> end to end over a faked media session.</summary>
public sealed class ExtractFrameStepExecutorTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly string PrefixKey = $"projects/{ProjectId}/outputFiles/";

    [Fact]
    public async Task Last_frame_of_a_previous_step_output_is_published_as_the_step_output_with_its_size()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"Last","maxLongSide":640}""");

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().StartWith(PrefixKey).And.EndWith("/step-2-frame.png");
        result.FailureCode.Should().BeNull();
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output["width"]!.GetValue<int>().Should().Be(640);
        output["height"]!.GetValue<int>().Should().Be(360);
        output["at"]!.GetValue<string>().Should().Be("Last");
        output["atSec"].Should().BeNull();
        f.Session.Staged.Should().Contain("source");
        f.Session.FfmpegCalls.Should().ContainSingle().Which.Should().Contain("-sseof").And.Contain("-update");
        f.Session.Published.Should().ContainSingle().Which.Should().Be(result.OutputStorageKey);
    }

    /// <summary>QA-P1-14: the output carries no storage key (the no-storage-key rule); the frame is
    /// addressed by the step's own <c>OutputStorageKey</c>/<c>projectFileId</c> everywhere else.</summary>
    [Fact]
    public async Task The_output_carries_no_storage_key_and_the_frame_stays_addressable()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"Last"}""");

        result.Status.Should().Be(StepStatus.Completed);
        JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
        output.ContainsKey("frameStorageKey").Should().BeFalse();
        output["fileName"]!.GetValue<string>().Should().Be("step-2-frame.png");
        result.OutputStorageKey.Should().StartWith(PrefixKey).And.EndWith("/step-2-frame.png");
        f.Session.Published.Should().ContainSingle().Which.Should().Be(result.OutputStorageKey);
    }

    [Fact]
    public async Task A_time_frame_reports_atSec_and_registers_the_image_as_a_project_file_by_default()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"Time","atSec":3.5,"format":"jpeg"}""");

        result.Status.Should().Be(StepStatus.Completed);
        result.OutputStorageKey.Should().EndWith("/step-2-frame.jpg");
        JsonNode.Parse(result.Output)!["atSec"]!.GetValue<double>().Should().Be(3.5);
        f.Files.Verify(w => w.RegisterStoredObjectAsync(
            ProjectId, result.OutputStorageKey!, "step-2-frame.jpg", "image/jpeg", It.IsAny<long>(),
            It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), "outputFiles"), Times.Once);
        JsonNode.Parse(result.Output)!["projectFileId"].Should().NotBeNull();
    }

    [Fact]
    public async Task RegisterProjectFile_false_registers_nothing()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"First","registerProjectFile":false}""");

        result.Status.Should().Be(StepStatus.Completed);
        f.Files.Verify(w => w.RegisterStoredObjectAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_project_file_source_resolves_through_the_project_file_list()
    {
        Fixture f = new();
        Guid fileId = Guid.NewGuid();
        f.ProjectFiles.Add(new ProjectWorkspaceFile(fileId, ProjectId, "clip.mp4", null, "userFiles", "projects/p/userFiles/clip.mp4", "video/mp4", 10, DateTime.UtcNow, null));

        StepExecutionResult result = await f.RunAsync($$"""{"source":{"kind":"ProjectFile","projectFileId":"{{fileId}}"},"at":"First"}""");

        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task A_StepResult_source_goes_through_the_shared_resolver_and_a_foreign_result_is_not_found()
    {
        Fixture f = new();
        Guid mine = Guid.NewGuid();
        Guid foreign = Guid.NewGuid();
        await f.SeedResultAsync(mine, ProjectId, $"{PrefixKey}exec/clip.mp4");
        await f.SeedResultAsync(foreign, Guid.NewGuid(), "projects/other/outputFiles/exec/clip.mp4");

        (await f.RunAsync($$"""{"source":{"kind":"StepResult","stepResultId":"{{mine}}"},"at":"First"}""")).Status.Should().Be(StepStatus.Completed);

        StepExecutionResult refused = await f.RunAsync($$"""{"source":{"kind":"StepResult","stepResultId":"{{foreign}}"},"at":"First"}""");
        refused.Status.Should().Be(StepStatus.Failed);
        refused.FailureCode.Should().Be("SOURCE_NOT_FOUND");
        refused.FailureClass.Should().Be(FailureClass.UserInput);

        // The project's own result that has not finished is told apart from a missing one (P0 resolver contract).
        Guid unfinished = Guid.NewGuid();
        await f.SeedResultAsync(unfinished, ProjectId, $"{PrefixKey}exec/clip2.mp4", StepStatus.Running);
        StepExecutionResult notPlayable = await f.RunAsync($$"""{"source":{"kind":"StepResult","stepResultId":"{{unfinished}}"},"at":"First"}""");
        notPlayable.Status.Should().Be(StepStatus.Failed);
        notPlayable.FailureCode.Should().Be("SOURCE_NOT_PLAYABLE");
    }

    [Fact]
    public async Task A_missing_upstream_output_is_a_coded_user_input_failure_before_any_compute()
    {
        Fixture f = new();
        f.History.Clear();

        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"First"}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("SOURCE_UNRESOLVED");
        f.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_config_fails_without_touching_the_session()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"Time"}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONFIG_INVALID");
        f.Session.FfmpegCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_time_past_the_end_of_the_video_is_FRAME_OUT_OF_RANGE()
    {
        Fixture f = new();
        f.Session.WriteNothing = true;

        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"Time","atSec":9999}""");

        result.FailureCode.Should().Be("FRAME_OUT_OF_RANGE");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        f.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ffmpeg_failure_is_a_platform_failure_with_no_publish()
    {
        Fixture f = new();
        f.Session.FfmpegExitCode = 1;

        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"First"}""");

        result.FailureCode.Should().Be("FRAME_EXTRACTION_FAILED");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal);
        f.Session.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unexpected_exception_returns_a_fixed_sentence_and_never_its_message()
    {
        Fixture f = new();
        f.Session.ThrowMessage = "disk /var/lib/reelbolt/projects/secret-key.mp4 on host storage-7.internal:9000";

        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"First"}""");

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("EXTRACT_FRAME_FAILED");
        foreach (string text in new[] { result.Output, result.ErrorDetails ?? string.Empty })
        {
            text.Should().NotContain("secret-key").And.NotContain("storage-7").And.NotContain("/var/lib");
        }

        result.Output.Should().Contain("The frame extraction failed unexpectedly");
    }

    [Fact]
    public async Task The_published_key_never_contains_user_text()
    {
        Fixture f = new();
        StepExecutionResult result = await f.RunAsync("""{"source":{"kind":"StepOutput","stepOrder":1},"at":"First","format":"png"}""");
        result.OutputStorageKey.Should().MatchRegex(@"^projects/[0-9a-f-]+/outputFiles/[0-9a-f-]+/step-2-frame\.png$");
    }

    // ------------------------------------------------------------------ fixture

    private sealed class Fixture
    {
        public SessionWorkspace Session { get; } = new();
        public Mock<IProjectFileWorkspace> Files { get; } = new();
        public List<ProjectWorkspaceFile> ProjectFiles { get; } = [];
        public List<StepOutputHistoryEntry> History { get; } = [new(1, "Generate", "{}", OutputStorageKey: PrefixKey + "exec/clip.mp4")];
        private readonly ServiceProvider _provider;

        public Fixture()
        {
            string db = "extractframe-" + Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(db));
            _provider = services.BuildServiceProvider();

            Files.Setup(w => w.ListFilesAsync(ProjectId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ProjectFiles);
            Files.Setup(w => w.RegisterStoredObjectAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid p, string key, string name, string type, long size, SummaryStatus s, FileIndexingStatus i, CancellationToken c, string cat) =>
                    new ProjectWorkspaceFile(Guid.NewGuid(), p, name, null, cat, key, type, size, DateTime.UtcNow, null));
        }

        public async Task SeedResultAsync(Guid id, Guid projectId, string outputKey, StepStatus status = StepStatus.Completed)
        {
            using IServiceScope scope = _provider.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId };
            db.WorkflowExecutions.Add(execution);
            db.WorkflowStepResults.Add(new WorkflowStepResult { Id = id, WorkflowExecutionId = execution.Id, OutputStorageKey = outputKey, Status = status });
            await db.SaveChangesAsync();
        }

        public async Task<StepExecutionResult> RunAsync(string configJson)
        {
            var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.ExtractFrame, ExtractFrameConfigJson = configJson };
            var executor = new ExtractFrameStepExecutor(
                new Mock<IVideoToolRunner>(MockBehavior.Strict).Object,
                Files.Object,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new VideoEditingOptions()),
                NullLogger<ExtractFrameStepExecutor>.Instance,
                new StubFactory(Session));
            var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId };
            return await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                StepOutputHistory = History,
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });
        }
    }

    /// <summary>A PNG header for a 1280x720 frame, shrunk to the longer side the argv asked for.</summary>
    private static byte[] PngOf(int w, int h)
    {
        byte[] b = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[18] = (byte)(w >> 8); b[19] = (byte)w; b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Staged { get; } = [];
        public List<string> Published { get; } = [];
        public List<string[]> FfmpegCalls { get; } = [];
        public int FfmpegExitCode { get; set; }
        public bool WriteNothing { get; set; }
        public string? ThrowMessage { get; set; }

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
        {
            Staged.Add(virtualPath);
            return Task.CompletedTask;
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(argv, timeout, ct, null);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        {
            FfmpegCalls.Add(argv.ToArray());
            if (ThrowMessage is not null)
                throw new InvalidOperationException(ThrowMessage);
            if (FfmpegExitCode == 0 && !WriteNothing)
            {
                // The filter says how big the frame is: a 1280x720 source scaled to a 640 long side is 640x360.
                bool scaled = argv.Any(a => a.StartsWith("scale=", StringComparison.Ordinal));
                Files[argv[^1]] = scaled ? PngOf(640, 360) : PngOf(1280, 720);
            }

            return Task.FromResult(new VideoToolResult(FfmpegExitCode, string.Empty, "stderr", false));
        }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct) =>
            WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException(virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes) ? new MediaWorkspaceStat(true, bytes.Length) : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
        {
            Published.Add(finalStorageKey);
            return Task.FromResult(finalStorageKey);
        }
    }

    private sealed class StubFactory(SessionWorkspace session) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }
}
