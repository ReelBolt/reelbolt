using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security T-326/T-029: the call sites that draw text, run against a remote (runner) session, carry no engine-host
/// font path; the same call sites against a local workspace keep the engine's font. Thumbnail is covered in
/// <see cref="ThumbnailGenerateStepExecutorTests"/>; the two compile overlay chains (inline in the compile, no seam
/// short of a whole compile) by the source guard at the end of this class.
/// </summary>
public class RemoteSessionFontExecutorTests
{
    private const string EngineFont = "/usr/share/fonts/dejavu/DejaVuSans.ttf";

    /// <summary>A session workspace that records argv; <see cref="IsRemoteSession"/> says which kind it stands for.</summary>
    internal sealed class RecordingWorkspace(bool remote) : IMediaWorkspace
    {
        public bool IsRemoteSession { get; } = remote;
        public List<string[]> FfmpegCalls { get; } = [];
        public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);
        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) => Task.CompletedTask;

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
        {
            FfmpegCalls.Add([.. argv]);
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onProgress) =>
            RunFfmpegAsync(argv, timeout, ct);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0,
                """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":640,"height":360,"r_frame_rate":"30/1","avg_frame_rate":"30/1","pix_fmt":"yuv420p"}],"format":{"duration":"2.0"}}""",
                string.Empty, false));

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);
        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct) => Task.CompletedTask;

        public Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct)
        {
            Texts[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => Task.FromResult(new byte[] { 0xFF, 0xD8, 0xFF });
        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) => ReadAsync(virtualPath, ct);
        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) => Task.FromResult(new MediaWorkspaceStat(true, 3));
        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) => Task.FromResult(finalStorageKey);
        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) => Task.FromResult(finalStorageKey);
    }

    private static IOptions<VideoEditingOptions> Options(string? fontsDir = null) =>
        Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions { FontFilePath = EngineFont, CaptionFontsDir = fontsDir ?? "/nonexistent-fonts" });

    private static void AssertNoEngineFont(string text)
    {
        text.Should().NotContain("fontfile=").And.NotContain("fontsdir=").And.NotContain("/usr/share/fonts").And.NotContain("'/");
    }

    // ---- VideoCompile captions (ass and drawtext) ----

    internal static VideoCompileStepExecutor Compile(string? fontsDir = null) => new(
        new Mock<IVideoToolRunner>().Object, new Mock<IMediaProbe>().Object, new Mock<IProjectFileWorkspace>().Object,
        new Mock<IServiceScopeFactory>().Object, Options(fontsDir), NullLogger<VideoCompileStepExecutor>.Instance);

    private static readonly List<ResolvedCaptionCue> Cues = [new("vo0", 0, "hello", 0, 1), new("vo0", 1, "world", 1, 2)];

    internal static readonly VideoCompileStepConfig OverlayConfig = new(1, new ExtractInputRef(ExtractInputSource.Previous), 1);

    private static readonly ResolvedOverlay Overlay = new(
        "p0", "LowerThird", "Title", "Sub", DurationMs: 1500, Emphasis: "Normal", OutputStartSec: 1, OutputEndSec: 3,
        Rect: new VideoAnalysisRect(0.1, 0.8, 0.6, 0.15), TextColor: "Light");

    [Fact]
    public void The_compile_text_overlay_stage_in_a_remote_session_carries_no_engine_font()
    {
        string chain = Compile().BuildTextOverlayStage(
            new RecordingWorkspace(remote: true), "[vcut]", [Overlay], 1920, 1080, OverlayConfig, slot => $"overlay-{slot}.txt", "[vout]");

        chain.Should().Contain("drawtext=textfile='overlay-");
        AssertNoEngineFont(chain);
    }

    [Fact]
    public void The_compile_text_overlay_stage_in_a_local_workspace_is_the_old_bytes()
    {
        string chain = Compile().BuildTextOverlayStage(
            new RecordingWorkspace(remote: false), "[vcut]", [Overlay], 1920, 1080, OverlayConfig, slot => $"/scratch/overlay-{slot}.txt", "[vout]");

        chain.Should().Be(DrawtextFilterBuilder.BuildFilterChain(
            "[vcut]", [Overlay], 1920, 1080, OverlayConfig.OverlayFontSizePct, OverlayConfig.OverlayFadeMs,
            OverlayConfig.OverlayFontColor, OverlayConfig.OverlayBoxColor, EngineFont, slot => $"/scratch/overlay-{slot}.txt",
            finalLabel: "[vout]", boxHeightPct: OverlayConfig.OverlayBoxHeightPct, boxWidthPct: OverlayConfig.OverlayBoxWidthPct,
            minCapHeightPct: OverlayConfig.ClampedMinOverlayCapHeightPct));
        chain.Should().Contain($"drawtext=fontfile='{EngineFont}':textfile=");
    }

    [Fact]
    public void Both_compile_filter_graphs_build_their_text_overlay_stage_through_the_one_session_aware_builder()
    {
        string source = EngineSource("Execution/StepExecutors/VideoCompileStepExecutor.cs");

        Regex.Matches(source, @"\bBuildTextOverlayStage\(\s*workspace,").Count.Should().Be(2, "the encode graph and the trial-policy paint graph");
        Regex.Matches(source, @"DrawtextFilterBuilder\.BuildFilterChain\(").Count.Should().Be(1, "only BuildTextOverlayStage builds the drawtext overlay chain");
    }

    private static readonly VideoCompileStepConfig CaptionConfig = new(1, new ExtractInputRef(ExtractInputSource.Previous), 1);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remote_compile_captions_carry_no_engine_font(bool libass)
    {
        string dir = Directory.CreateTempSubdirectory("captionfonts-").FullName;
        try
        {
            RecordingWorkspace session = new(remote: true);

            string filter = await Compile(dir).BuildCaptionsFilterAsync("[vcap]", Cues, 1920, 1080, CaptionConfig, session, libassForTests: libass);

            filter.Should().Contain(libass ? "ass=filename='captions.ass'" : "drawtext=textfile=");
            AssertNoEngineFont(filter);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_compile_captions_keep_the_engine_font(bool libass)
    {
        string dir = Directory.CreateTempSubdirectory("captionfonts-").FullName;
        try
        {
            RecordingWorkspace local = new(remote: false);

            string filter = await Compile(dir).BuildCaptionsFilterAsync("[vcap]", Cues, 1920, 1080, CaptionConfig, local, libassForTests: libass);

            filter.Should().Contain(libass ? $":fontsdir='{dir}'" : $"drawtext=fontfile='{EngineFont}':textfile=");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_remote_session_resolves_caption_fonts_with_no_engine_font_folder()
    {
        string dir = Directory.CreateTempSubdirectory("captionfonts-").FullName;
        try
        {
            VideoCompileStepExecutor executor = Compile(dir);

            executor.CaptionFontsDirFor(new RecordingWorkspace(remote: true)).Should().BeNull();
            executor.CaptionFontsDirFor(new RecordingWorkspace(remote: false)).Should().Be(dir);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- Generated-clip disclosure label ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_disclosure_label_follows_the_session_kind(bool remote)
    {
        RecordingWorkspace session = new(remote);
        GeneratedClipNormalizer normalizer = new(new Mock<IVideoToolRunner>().Object, Options(), NullLogger<GeneratedClipNormalizer>.Instance);

        await normalizer.NormalizeAsync(session, "clip-raw.mp4", "clip-normalized.mp4", CancellationToken.None,
            disclosure: new GeneratedClipDisclosure(VisibleLabel: true));

        string vf = session.FfmpegCalls.Single()[Array.IndexOf(session.FfmpegCalls.Single(), "-vf") + 1];
        if (remote)
        {
            vf.Should().Contain("drawtext=text=");
            AssertNoEngineFont(vf);
        }
        else
        {
            vf.Should().Contain($"drawtext=fontfile='{EngineFont}':text=");
        }
    }

    // ---- Placement stills ----

    private sealed class SessionFactory(IMediaWorkspace session) : IMediaWorkspaceFactory
    {
        public Task<MediaWorkspaceOpenResult> OpenAsync(MediaWorkspaceRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                session, scratch: null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_placement_still_follows_the_session_kind(bool remote)
    {
        RecordingWorkspace session = new(remote);
        FfmpegPlacementStillGrabber grabber = new(new SessionFactory(session), Options(), NullLogger<FfmpegPlacementStillGrabber>.Instance);
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.GraphicsRoom };
        StepExecutionContext context = new()
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "",
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };

        await grabber.GrabAsync(
            context, null!, new GraphicsCanvas(1080, 1920, 0, VideoOutputFormat.Vertical, VideoOutputFit.Crop, null),
            [new PlacementStillRequest(0, "projects/p/userFiles/take.mp4", 3, [new PlacementStillBand("p0", new VideoAnalysisRect(0, 0.7, 1, 0.2))])],
            CancellationToken.None);

        string vf = session.FfmpegCalls.Single()[Array.IndexOf(session.FfmpegCalls.Single(), "-vf") + 1];
        if (remote)
        {
            vf.Should().Contain("drawtext=text='p0'");
            AssertNoEngineFont(vf);
        }
        else
        {
            vf.Should().Contain($"drawtext=fontfile='{EngineFont}':text='p0'");
        }
    }

    // ---- Source guard: every engine-font use at the text call sites goes through the session kind ----

    private static string EngineSource(string rel)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, "inference", "src", "ReelBolt.WorkflowEngine", rel));
    }

    /// <summary>
    /// The engine font and caption font folder at each text call site: (file, guarded uses, of which drawtext font).
    /// Reverting any site to the bare option drops the guarded count and leaves a bare use; both fail.
    /// </summary>
    [Theory]
    [InlineData("Execution/StepExecutors/VideoCompileStepExecutor.cs", 2)] // caption drawtext, the overlay stage (both graphs)
    [InlineData("Execution/StepExecutors/ThumbnailGenerateStepExecutor.cs", 1)]
    [InlineData("Services/Video/PlacementStills.cs", 1)]
    [InlineData("Services/Video/GeneratedClipNormalizer.cs", 1)]
    public void Every_engine_font_use_at_a_text_call_site_goes_through_the_session_kind(string rel, int sites)
    {
        string source = EngineSource(rel);

        Regex.Matches(source, @"DrawtextFilterBuilder\.EngineFontFor\(workspace, _options\.FontFilePath\)").Count.Should().Be(sites, rel);
        Regex.Matches(source, @"_options\.FontFilePath").Count.Should().Be(sites, $"{rel}: every engine font use goes through EngineFontFor");
    }

    [Fact]
    public void The_compile_caption_font_folder_goes_through_the_session_kind()
    {
        string source = EngineSource("Execution/StepExecutors/VideoCompileStepExecutor.cs");

        Regex.Matches(source, @"string\? fontsDir = CaptionFontsDirFor\(workspace\);").Count.Should().Be(1);
        Regex.Matches(source, @"workspace\.IsRemoteSession \? null : _options\.CaptionFontsDir").Count.Should().Be(1, "the face resolution");
        Regex.Matches(source, @"_options\.CaptionFontsDir").Count.Should().Be(3, "CaptionFontsDirFor (2) and the face resolution (1) only");
    }
}
