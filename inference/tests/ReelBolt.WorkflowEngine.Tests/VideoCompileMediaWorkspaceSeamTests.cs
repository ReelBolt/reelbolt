using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
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
/// D9b's behaviour-preservation evidence, taken at the COMPILE call site rather than at the seam
/// (D9a's own <see cref="MediaWorkspaceTests"/> already pins the eight ported services' argv
/// byte-identity in isolation).
///
/// <para>
/// The claim this file exists to hold down is the one the whole port rests on:
/// <see cref="IMediaWorkspace.MaterializeAsync"/> with the virtual path a session file was staged
/// under returns EXACTLY the string <see cref="VideoScratchSpace.GetPath"/> returns for the same
/// name. Every session file the compile hands to ffmpeg is materialized through the workspace now,
/// so if those two ever disagreed the encode command would change — silently, and only in
/// production. Each scratch-rooted argument of the real captured encode argv is therefore mapped
/// back to its session-relative name and re-resolved with the LEGACY resolver; the two commands must
/// be byte-identical argument for argument.
/// </para>
///
/// <para>
/// It also pins the two halves of the port a reader cannot see from the outside: the compile never
/// touches <see cref="IMediaProbe.ProbeAsync(string, CancellationToken)"/> (the D9a compatibility
/// adapter) any more, and object storage is reached as <c>stage</c> into the workspace's own session
/// directory plus one <c>publish</c> of the compiled program.
/// </para>
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileMediaWorkspaceSeamTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    [Fact]
    public async Task The_encode_command_is_byte_identical_when_every_session_path_is_re_resolved_by_VideoScratchSpace()
    {
        Fixture fixture = await Fixture.RunAsync(registerProjectFile: false);

        IReadOnlyList<string> encode = fixture.Commands.Last();
        List<string> args = encode.ToList();

        // The argv really does carry workspace paths — otherwise this test would pass vacuously.
        List<string> sessionArgs = args.Where(a => a.StartsWith(fixture.SessionRoot, StringComparison.Ordinal)).ToList();
        sessionArgs.Should().NotBeEmpty("a compile that produced no session paths would prove nothing");

        // The source input and the encoded output, by their own arguments rather than by position.
        int inputIndex = args.IndexOf("-i");
        inputIndex.Should().BeGreaterThan(-1);
        args[inputIndex + 1].Should().Be(fixture.Scratch.GetPath("source-0.mp4"));
        args[^1].Should().Be(fixture.Scratch.GetPath("edited.mp4"));

        // The name each borrowed path was staged under is exactly the name the pre-D9b code used.
        args[inputIndex + 1].Should().EndWith(Path.Combine("source-0.mp4"));
        args[^1].Should().EndWith(Path.Combine("edited.mp4"));

        // THE parity assertion: re-resolving every session path through the legacy scratch resolver
        // (exactly what the pre-D9b executor did) reproduces the command argument for argument.
        List<string> legacyCommand = args
            .Select(a => a.StartsWith(fixture.SessionRoot, StringComparison.Ordinal)
                ? fixture.Scratch.GetPath(Path.GetRelativePath(fixture.SessionRoot, a).Replace('\\', '/'))
                : a)
            .ToList();

        legacyCommand.Should().Equal(args,
            "IMediaWorkspace.MaterializeAsync(vpath) and VideoScratchSpace.GetPath(vpath) must agree byte for byte " +
            "for every name this compile uses — that equality IS the byte-identical-command guarantee");
    }

    [Fact]
    public async Task Downloads_are_staged_into_the_workspace_session_directory_and_probed_by_virtual_path()
    {
        Fixture fixture = await Fixture.RunAsync(registerProjectFile: false);

        // stage: the destination the object store is asked to write to is the path the WORKSPACE
        // resolved for the session-relative name — not a path the executor built itself.
        fixture.Downloads.Should().Contain(d =>
            d.StorageKey == AnalysisKey && d.Destination == fixture.Scratch.GetPath("analysis.json"));
        fixture.Downloads.Should().Contain(d =>
            d.StorageKey == SourceVideoKey && d.Destination == fixture.Scratch.GetPath("source-0.mp4"));

        // ...and the probe that follows is addressed by that same virtual path, through the seam.
        fixture.ProbedVirtualPaths.Should().Contain("source-0.mp4");

        // The compiled program is the media session's own publish: same project scope, same
        // category/fileName the pre-D9b UploadArtifactAsync call produced, and the source is the
        // materialized session file.
        fixture.ProjectFiles.Verify(
            w => w.UploadArtifactAsync(
                ProjectId,
                fixture.Scratch.GetPath("edited.mp4"),
                fixture.ExecutionId.ToString("D") + "/edited.mp4",
                "video/mp4",
                It.IsAny<CancellationToken>(),
                "outputFiles"),
            Times.Once);
    }

    [Fact]
    public async Task A_registered_render_is_published_through_the_session_and_registered_from_the_published_key()
    {
        Fixture fixture = await Fixture.RunAsync(registerProjectFile: true);

        // The registered render is the media session's own publish (the same seam as an unregistered
        // one, so it works on a remote session too), from the workspace's materialized session file,
        // to a "{fileId}.mp4" key under outputFiles; the project-file row is then registered from that
        // key. Nothing reads a local file for it.
        string? publishedName = null;
        fixture.ProjectFiles.Verify(
            w => w.UploadArtifactAsync(
                ProjectId,
                fixture.Scratch.GetPath("edited.mp4"),
                It.Is<string>(name => !name.Contains('/') && name.EndsWith(".mp4")),
                "video/mp4",
                It.IsAny<CancellationToken>(),
                "outputFiles"),
            Times.Once);
        publishedName = fixture.ProjectFiles.Invocations
            .Where(i => i.Method.Name == nameof(IProjectFileWorkspace.UploadArtifactAsync) && (string)i.Arguments[5] == "outputFiles")
            .Select(i => (string)i.Arguments[2])
            .Single();
        fixture.ProjectFiles.Verify(
            w => w.RegisterStoredObjectAsync(
                ProjectId, "projects/" + ProjectId + "/outputFiles/" + publishedName, It.IsAny<string>(), "video/mp4",
                It.IsAny<long>(), SummaryStatus.Done, FileIndexingStatus.NotIndexed, It.IsAny<CancellationToken>(), "outputFiles"),
            Times.Once);
        fixture.ProjectFiles.Verify(
            w => w.UploadBinaryFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SummaryStatus>(),
                It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
        fixture.ProjectFiles.Verify(
            w => w.UploadArtifactAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.Is<string>(name => name.StartsWith("video-analysis/", StringComparison.Ordinal)),
                "application/json", It.IsAny<CancellationToken>(), It.IsAny<string>()),
            Times.AtLeastOnce,
            "the EDL and the timeline manifest stay platform-side uploads (plan D9b, Outputs)");
    }

    [Fact]
    public async Task The_compile_never_uses_the_local_path_compatibility_overload()
    {
        Fixture fixture = await Fixture.RunAsync(registerProjectFile: false);

        // D9b's own acceptance criterion, asserted rather than assumed: every ported service this
        // executor calls is reached through the (workspace, vpath) shape, so the D9a adapters have
        // no caller here. (They are NOT retired: VideoAnalyzeStepExecutor, VoiceoverStepExecutor,
        // ReactRemotionSandboxTools, VideoGenerateStepExecutor, ObjectTrackingService and
        // MusicBeatProbe still call them — see the WP report.)
        fixture.Probe.Verify(
            p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "VideoCompileStepExecutor must not call IMediaProbe's absolute-path compatibility overload");
        fixture.ProbedVirtualPaths.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------------
    // Fixture: one real single-source Reencode compile, driven exactly like the other compile tests.
    // ---------------------------------------------------------------------

    private sealed class Fixture
    {
        public required Mock<IProjectFileWorkspace> ProjectFiles { get; init; }
        public required Mock<IMediaProbe> Probe { get; init; }
        public required List<IReadOnlyList<string>> Commands { get; init; }
        public required List<(string StorageKey, string Destination)> Downloads { get; init; }
        public required List<string> ProbedVirtualPaths { get; init; }
        public required VideoScratchSpace Scratch { get; init; }
        public required string SessionRoot { get; init; }
        public required Guid ExecutionId { get; init; }

        public static async Task<Fixture> RunAsync(bool registerProjectFile)
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

            Guid executionId = Guid.NewGuid();

            var commands = new List<IReadOnlyList<string>>();
            var downloads = new List<(string, string)>();
            var probed = new List<string>();

            var toolRunner = new Mock<IVideoToolRunner>();
            toolRunner
                .Setup(t => t.RunFfmpegAsync(
                    It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VideoToolResult(0, "... drawtext ...", string.Empty, false));
            toolRunner
                .Setup(t => t.RunFfmpegAsync(
                    It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((a, _, _) => commands.Add(a.ToList()))
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
            toolRunner
                .Setup(t => t.RunFfmpegAsync(
                    It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                    It.IsAny<Action<string>>()))
                .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((a, _, _, _) => commands.Add(a.ToList()))
                .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));

            var probe = new Mock<IMediaProbe>();
            probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, CancellationToken>((_, virtualPath, _) =>
                {
                    probed.Add(virtualPath);
                    return Task.FromResult(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
                });

            VideoAnalysisArtifact artifact = new(
                Version: 1,
                Media: new VideoAnalysisMedia(10.0, 30, 1, 1920, 1080),
                Shots: [new VideoAnalysisShot("s0", 0.0, 10.0)],
                SilenceSpans: [],
                Segments: [],
                Words: [],
                OfferedIds: ["s0"],
                Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Placements: null,
                OfferedPlacementIds: null);
            string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());

            string decisionJson = JsonSerializer.Serialize(
                new { keep = new[] { new { fromId = "s0", toId = "s0", reason = "keep" } }, editRationale = "test", suggestedTitle = "Test" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var projectFiles = new Mock<IProjectFileWorkspace>();
            projectFiles
                .Setup(w => w.DownloadStorageKeyToFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
                {
                    downloads.Add((key, destination));
                    if (key == AnalysisKey)
                        File.WriteAllText(destination, artifactJson);
                    else
                        File.WriteAllBytes(destination, [0x00, 0x01, 0x02]);
                    return Task.CompletedTask;
                });
            // Echoes the key back: IMediaWorkspace.PublishAsync verifies that what the object store
            // round-tripped IS the key it asked for, so a canned string would (correctly) fail it.
            projectFiles
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) =>
                    "projects/" + pid + "/" + category + "/" + fileName);
            projectFiles
                .Setup(w => w.UploadBinaryFileAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                    It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                    CancellationToken _, string category, string? _) =>
                    new ProjectWorkspaceFile(
                        Guid.NewGuid(), pid, fileName, null, category,
                        "projects/" + pid + "/" + category + "/" + Guid.NewGuid().ToString("D") + ".mp4",
                        mime, 1024, DateTime.UtcNow, null));
            projectFiles.SetupRegisteredRenderPublish();

            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(Path.GetTempPath(), "video-compile-seam-tests", Guid.NewGuid().ToString("N")),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 30,
                CompileTimeoutSeconds = 30
            });

            VideoCompileStepConfig config = new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1) with
            {
                Mode = VideoCompileMode.Reencode,
                RegisterProjectFile = registerProjectFile,
                LoudnessTarget = ProgramLoudness.Off
            };

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 3,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
            };
            var analyzeStep = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoAnalyze,
                VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                    new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)),
                    JsonOptions())
            };

            var context = new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = executionId, ProjectId = ProjectId },
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory =
                [
                    new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                    new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
                ],
                CurrentStepIndex = 2,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            };

            var executor = new VideoCompileStepExecutor(
                toolRunner.Object, probe.Object, projectFiles.Object, new Mock<IServiceScopeFactory>().Object,
                options, NullLogger<VideoCompileStepExecutor>.Instance);

            StepExecutionResult result = await executor.ExecuteAsync(context);
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

            string sessionRoot = Path.GetFullPath(Path.Combine(
                Path.GetFullPath(options.Value.ScratchPath), executionId.ToString("D"), step.Id.ToString("D")));
            VideoScratchSpace scratch = VideoScratchSpace.Create(options.Value, executionId, step.Id);
            scratch.DirectoryPath.Should().Be(sessionRoot);

            return new Fixture
            {
                ProjectFiles = projectFiles,
                Probe = probe,
                Commands = commands,
                Downloads = downloads,
                ProbedVirtualPaths = probed,
                Scratch = scratch,
                SessionRoot = sessionRoot,
                ExecutionId = executionId
            };
        }
    }

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
