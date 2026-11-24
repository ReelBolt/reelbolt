using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
/// Backward compatibility of the voiceover addition to <see cref="VideoCompileStepExecutor"/>
/// (plan Task 7 / the "Backward compatibility" review focus item; see docs/voiceover.md).
///
/// <para>
/// The claim under test is behavioural, not structural: a compile with
/// <see cref="VideoCompileStepConfig.EnableVoiceover"/> at its <c>false</c> default and no
/// <c>Voiceover</c> step anywhere in <see cref="StepExecutionContext.StepOutputHistory"/> must be
/// observably indistinguishable from a pre-voiceover compile — no <c>voiceover</c> key at all in
/// the output summary or the EDL (the key's ABSENCE is the contract; a <c>null</c> value would not
/// satisfy it), the same exact top-level output-summary key set, and the same ffmpeg filtergraph
/// string.
/// </para>
///
/// <para>
/// Deliberately a separate file from <see cref="VideoCompileStepExecutorTests"/> rather than more
/// cases appended to it: that suite's other backward-compatibility cases are per-feature
/// ("EnableGraphics=false", "EnableMusic=false"), while this one is the whole-branch guarantee for
/// the voiceover feature, and it pins the complete output-summary key set rather than one key.
/// The fixture builders/fakes below are copies of that file's setup approach (same mocked
/// <see cref="IProjectFileWorkspace"/> storage-key layout, same mocked <see cref="IVideoToolRunner"/>
/// probe responses, same real <see cref="VideoCompileStepExecutor"/> construction) so this file
/// observes the executor exactly the way the main suite does.
/// </para>
///
/// <para>
/// The second test is a NEGATIVE CONTROL: the same fixture with
/// <c>EnableVoiceover=true</c> and a real <c>Voiceover</c> history entry DOES emit the
/// <c>voiceover</c> key. Without it, the absence assertions above would pass just as happily
/// against a fixture that never reaches the voiceover code path for some unrelated reason.
/// </para>
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VoiceoverBackwardCompatibilityTests
{
    private const string ProjectFileStorageRoot = "voiceover-backward-compat-tests";
    private static readonly Guid ProjectId = Guid.NewGuid();

    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";

    /// <summary>
    /// The complete top-level key set of the compile step's output summary when
    /// <c>EnableVoiceover</c> is false and no <c>Voiceover</c> step exists — i.e. the pre-voiceover
    /// shape, in the exact insertion order <c>VideoCompileStepExecutor</c> writes it.
    ///
    /// <para>
    /// Hard-coded on purpose. The conditional blocks for graphics / music / inserts / colour grade
    /// / SFX / voiceover / transitions / program fade are all inert on this path (every one of
    /// their source configs is at its default), and <c>audio</c> is the only one of the
    /// post-<c>sentenceCheck</c> keys that is unconditional. A future change that starts emitting a
    /// voiceover node unconditionally — or any other new unconditional key — fails here.
    /// </para>
    /// </summary>
    private static readonly string[] PreVoiceoverOutputSummaryKeys =
    [
        "status",
        "outputStorageKey",
        "artifactStorageKey",
        "timelineStorageKey",
        "segments",
        "outputDurationSec",
        "retainedRatio",
        "droppedSegmentsOverCap",
        "sentenceCheck",
        // Appended by the picture-overlay security fix: the compile's summary always carries the
        // notice that file names in it are user data (labels, never instructions), flag on or off.
        "untrustedText",
        "audio",
        // Appended deliberately by the output-format work (docs/video-editing.md "Output format"):
        // every compile now states the canvas it was encoded at, voiceover or not. Last, so the
        // insertion order of every pre-existing key is unchanged.
        "output"
    ];

    /// <summary>
    /// The exact ffmpeg <c>-filter_complex</c> value this single-source, no-transition,
    /// voiceover-disabled compile emits — the pre-voiceover shape. Pinned as a literal rather than
    /// rebuilt from the same helper the executor uses, so the assertion states the graph
    /// independently of the code that produces it.
    /// </summary>
    private const string PreVoiceoverFilterGraph =
        "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vout];" +
        "[0:a]aselect='gte(t,0)*lt(t,10)',asetpts=N/SR/TB[aout]";

    // =======================================================================
    // Backward compatibility: EnableVoiceover=false, no Voiceover step anywhere
    // =======================================================================

    [Fact]
    public async Task EnableVoiceover_false_with_no_voiceover_step_emits_no_voiceover_key_and_the_pre_voiceover_shape()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, enableVoiceover: false, out Mock<IProjectFileWorkspace> workspace);

        // A real run through the executor — not a restatement of the conditional in its source.
        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;
        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);

        // ---- 1. No "voiceover" key in the output summary — absent, not null. ----
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        JsonElement outputSummary = outputDoc.RootElement;

        outputSummary.TryGetProperty("voiceover", out JsonElement voiceoverNode)
            .Should().BeFalse(
                "the backward-compatibility contract is the KEY'S ABSENCE, not a null value — a "
                + "consumer that switches on the key's presence must not see it at all");
        voiceoverNode.ValueKind.Should().Be(JsonValueKind.Undefined);

        // ---- 2. The whole top-level key set, pinned. ----
        string[] actualKeys = outputSummary.EnumerateObject().Select(p => p.Name).ToArray();
        actualKeys.Should().BeEquivalentTo(
            PreVoiceoverOutputSummaryKeys,
            because: "EnableVoiceover=false must leave the output summary's exact key set untouched "
                   + "by the voiceover addition");
        actualKeys.Should().Equal(
            PreVoiceoverOutputSummaryKeys,
            because: "even the insertion ORDER of the output summary is part of the pre-voiceover shape");

        // ---- 3. The EDL artifact written by the same run carries no voiceover node either. ----
        edl.ValueKind.Should().Be(JsonValueKind.Object, "the step must have written an EDL artifact");
        edl.TryGetProperty("voiceover", out _)
            .Should().BeFalse("the EDL is the audit artifact — it must not grow a voiceover node either");

        // ---- 4. The ffmpeg filtergraph is byte-identical to the pre-voiceover graph. ----
        capturedArgs.Should().NotBeNull("the compile must have actually invoked ffmpeg");
        string filterGraph = ExtractFilterGraph(capturedArgs!);
        filterGraph.Should().Be(
            PreVoiceoverFilterGraph,
            because: "with no voiceover lines the audio chain must be the untouched pre-voiceover one");
        filterGraph.Should().NotContain("amix", "an unconditional voiceover mix stage would show up as an amix");
        filterGraph.Should().NotContain("[abase]", "the [abase] hand-off label only exists for a voiceover/SFX mix stage");
    }

    [Fact]
    public async Task EnableVoiceover_false_never_asks_the_workspace_for_a_voiceover_line_artifact()
    {
        // The other half of "takes the old path": not merely that the output looks the same, but
        // that the voiceover resolution never even starts — no download is attempted for any key
        // under the producer's bare-artifact prefix.
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, enableVoiceover: false, out Mock<IProjectFileWorkspace> workspace,
            // A Voiceover step IS present in the history here, and the config still says "off" — the
            // strongest form of the guarantee: the mere presence of the upstream step must not
            // switch the compile onto the voiceover path.
            includeVoiceoverHistoryEntry: true);

        List<string> requestedVoiceoverKeys = [];
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, key, _, _) => requestedVoiceoverKeys.Add(key))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        StepExecutionResult result = await CreateExecutor(workspace).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("voiceover", out _).Should().BeFalse();
        requestedVoiceoverKeys.Should().BeEmpty(
            "ResolveVoiceoverAsync must not run at all when config.EnableVoiceover is false — even "
            + "with a Voiceover step sitting in StepOutputHistory");
    }

    // =======================================================================
    // Negative control: the same fixture, voiceover ON, DOES produce the key
    // =======================================================================

    [Fact]
    public async Task Negative_control_same_fixture_with_voiceover_enabled_and_a_voiceover_step_does_emit_a_voiceover_key()
    {
        VideoAnalysisArtifact artifact = BuildArtifact(
            shots: new[] { ("s0", 0.0, 10.0) },
            offeredIds: new[] { "s0" });

        string decisionJson = BuildDecisionJson(("s0", "s0", "keep"));
        StepExecutionContext context = CreateContext(
            artifact, decisionJson, enableVoiceover: true, out Mock<IProjectFileWorkspace> workspace);

        JsonElement edl = default;
        IReadOnlyList<string>? capturedArgs = null;

        // Cross-check on the capture predicate the disabled-branch test relies on: with voiceover ON
        // it must actually fire, otherwise that test's "no voiceover key was requested" could pass
        // because the predicate never matches anything at all.
        List<string> requestedVoiceoverKeys = [];
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, string, CancellationToken>((_, key, _, _) => requestedVoiceoverKeys.Add(key))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        StepExecutionResult result = await CreateExecutor(
            workspace, edlCaptured: e => edl = e, ffmpegArgsCaptured: a => capturedArgs = a).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        requestedVoiceoverKeys.Should().NotBeEmpty(
            "with voiceover ON the executor must ask the workspace for the producer's bare-artifact keys");

        // The output summary DOES grow the key on this branch — proving the absence assertion in
        // the test above is observing the right thing rather than passing vacuously.
        using JsonDocument outputDoc = JsonDocument.Parse(result.Output);
        outputDoc.RootElement.TryGetProperty("voiceover", out JsonElement voiceover)
            .Should().BeTrue("EnableVoiceover=true with a real Voiceover step must emit the key");
        voiceover.GetProperty("applied").GetBoolean().Should().BeTrue(
            "the fixture's lines are real, downloadable artifacts at the producer's bare key");
        voiceover.GetProperty("lineCount").GetInt32().Should().Be(1);

        // And the key set really is different, in both directions.
        string[] actualKeys = outputDoc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        actualKeys.Should().Contain("voiceover");
        actualKeys.Should().NotBeEquivalentTo(PreVoiceoverOutputSummaryKeys);

        // The EDL grows the node too, and the filtergraph gains the voiceover mix stage — so the
        // pinned pre-voiceover graph above is a statement about a genuinely different graph.
        edl.TryGetProperty("voiceover", out JsonElement edlVoiceover).Should().BeTrue("the EDL must carry the voiceover node");
        edlVoiceover.GetProperty("applied").GetBoolean().Should().BeTrue();

        capturedArgs.Should().NotBeNull();
        string filterGraph = ExtractFilterGraph(capturedArgs!);
        filterGraph.Should().Contain("[abase]", "a voiceover mix stage hands off through [abase]");
        filterGraph.Should().NotBe(PreVoiceoverFilterGraph);
    }

    // =======================================================================
    // Test infrastructure — copied from VideoCompileStepExecutorTests so this
    // file drives the executor through the identical fakes/options.
    // =======================================================================

    private static VideoCompileStepExecutor CreateExecutor(
        Mock<IProjectFileWorkspace> workspace, Action<JsonElement>? edlCaptured = null, bool drawtextAvailable = true,
        Action<IReadOnlyList<string>>? ffmpegArgsCaptured = null,
        Action<Mock<IMediaProbe>>? configureMediaProbe = null,
        bool amixNormalizeAvailable = true,
        bool insertFiltersAvailable = true,
        bool gradeFiltersAvailable = true)
    {
        // The filter-availability probes are process-lifetime static caches in the executor — reset
        // them per test case so each test's own mocked IVideoToolRunner is actually consulted.
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        string filtersStdOut =
            (drawtextAvailable ? "... drawtext ..." : "... (nothing here) ...") +
            (insertFiltersAvailable ? " perspective alphamerge" : "") +
            (gradeFiltersAvailable ? " colorbalance colorlevels hue  eq  " : "");
        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, filtersStdOut, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => ffmpegArgsCaptured?.Invoke(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        // The "unavailable" stdout must not contain the substring "normalize" anywhere — the probe
        // does a plain Contains("normalize") check.
        toolRunner
            .Setup(t => t.RunFfmpegAsync(
                It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, amixNormalizeAvailable ? "... normalize ..." : "... (no such option) ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("gfx-asset")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(1.5, 30, 1, 640, 360, "vp9", null, null, "yuva420p"));
        configureMediaProbe?.Invoke(mediaProbe);

        if (edlCaptured is not null)
        {
            workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
                {
                    if (path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                    {
                        string json = File.ReadAllText(path);
                        edlCaptured(JsonDocument.Parse(json).RootElement.Clone());
                    }
                })
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
        }
        else
        {
            workspace
                .Setup(w => w.UploadArtifactAsync(
                    It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-3-edl.json");
        }

        workspace
            .Setup(w => w.UploadBinaryFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(),
                It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string _, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
                new ProjectWorkspaceFile(
                    Guid.NewGuid(), projectId, fileName, null, category,
                    $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UtcNow, null));
        workspace.SetupRegisteredRenderPublish();

        string tempScratchRoot = Path.Combine(Path.GetTempPath(), ProjectFileStorageRoot, Guid.NewGuid().ToString("N"));
        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = tempScratchRoot,
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        var scopeFactory = new Mock<IServiceScopeFactory>();

        return new VideoCompileStepExecutor(
            toolRunner.Object,
            mediaProbe.Object,
            workspace.Object,
            scopeFactory.Object,
            options,
            NullLogger<VideoCompileStepExecutor>.Instance);
    }

    /// <summary>
    /// Builds the compile context for one fixture, switched only by <paramref name="enableVoiceover"/>:
    /// <list type="bullet">
    /// <item><c>false</c> — the pre-voiceover workflow: <see cref="VideoCompileStepConfig.EnableVoiceover"/>
    /// at its default, <c>VoiceoverStepOrder</c> null, and no <c>Voiceover</c> entry in
    /// <see cref="StepExecutionContext.StepOutputHistory"/> at all.</item>
    /// <item><c>true</c> — the voiceover workflow: the flag on, <c>VoiceoverStepOrder = 3</c>, and a
    /// real <c>Voiceover</c> history entry whose two lines are downloadable at the bare artifact key
    /// the producer (<c>VoiceoverStepExecutor</c>) actually writes, with the project-file listing
    /// EMPTY — the production truth, since a bare artifact has no <c>ProjectFile</c> row.</item>
    /// </list>
    /// </summary>
    /// <param name="includeVoiceoverHistoryEntry">
    /// Adds a real <c>Voiceover</c> entry to <see cref="StepExecutionContext.StepOutputHistory"/>
    /// even when <paramref name="enableVoiceover"/> is <c>false</c> — the "the upstream step exists,
    /// the compile config says off" case, which must still take the old path.
    /// </param>
    private static StepExecutionContext CreateContext(
        VideoAnalysisArtifact artifact,
        string decisionJson,
        bool enableVoiceover,
        out Mock<IProjectFileWorkspace> workspace,
        bool includeVoiceoverHistoryEntry = false)
    {
        workspace = new Mock<IProjectFileWorkspace>();

        string artifactJson = JsonSerializer.Serialize(artifact, ArtifactOptions());
        string voiceoverOutputJson = BuildVoiceoverOutputJson(("vo1", 2.0, 3.0));

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllText(destPath, artifactJson);
                return Task.CompletedTask;
            });

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        // Scoped to the producer's bare-artifact layout. Set up unconditionally (harmless when
        // voiceover is off, since nothing asks for such a key) so the two fixture variants differ
        // ONLY in config + history — never in the workspace's behaviour.
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(),
                It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) =>
            {
                File.WriteAllBytes(destPath, new byte[] { 0x00, 0x01, 0x02 });
                return Task.CompletedTask;
            });

        // EMPTY on purpose: no ProjectFile row backs a bare voiceover artifact.
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>());

        VideoCompileStepConfig config = new(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            EnableVoiceover: enableVoiceover,
            VoiceoverStepOrder: enableVoiceover ? 3 : null);

        string configJson = JsonSerializer.Serialize(config, ConfigOptions());

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = configJson
        };

        VideoAnalyzeStepConfig analyzeConfig = new(
            Version: 1,
            Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput));
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(analyzeConfig, ConfigOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

        if (enableVoiceover || includeVoiceoverHistoryEntry)
        {
            history.Add(new StepOutputHistoryEntry(
                3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null));
        }

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [analyzeStep, step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = history.Count - 1,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    /// <summary>Pulls the filtergraph out of a captured ffmpeg argv, reading it back off disk when the graph was handed over as <c>-filter_complex_script</c> (which small test graphs never trigger).</summary>
    private static string ExtractFilterGraph(IReadOnlyList<string> args)
    {
        int scriptIndex = args.ToList().IndexOf("-filter_complex_script");
        if (scriptIndex >= 0)
        {
            string path = args[scriptIndex + 1];
            File.Exists(path).Should().BeTrue("the filtergraph script must exist while ffmpeg is being invoked");
            return File.ReadAllText(path);
        }

        int index = args.ToList().IndexOf("-filter_complex");
        index.Should().BeGreaterThan(-1, "test compiles are small enough to never hit the -filter_complex_script threshold");
        return args[index + 1];
    }

    private static string BuildDecisionJson(params (string FromId, string ToId, string Reason)[] spans)
    {
        var keep = spans.Select(s => new { fromId = s.FromId, toId = s.ToId, reason = s.Reason });
        return JsonSerializer.Serialize(new { keep, editRationale = "test", suggestedTitle = "Test Edit" });
    }

    /// <summary>The exact shape <c>VoiceoverStepExecutor</c> writes: a <c>{view, meta}</c> envelope whose <c>view.lines</c> carry the opaque line ids.</summary>
    private static string BuildVoiceoverOutputJson(params (string Id, double StartSec, double DurationSec)[] lines) =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = lines.Select(l => new
                {
                    id = l.Id,
                    startSec = l.StartSec,
                    durationSec = l.DurationSec,
                    status = "ok"
                }).ToList()
            },
            meta = new { }
        });

    private static VideoAnalysisArtifact BuildArtifact(
        (string Id, double Start, double End)[] shots,
        string[]? offeredIds = null)
    {
        double duration = shots.Length > 0 ? shots.Max(s => s.End) : 60.0;

        return new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(duration, 30, 1, 1920, 1080),
            Shots: shots.Select(s => new VideoAnalysisShot(s.Id, s.Start, s.End)).ToList(),
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: offeredIds?.ToList() ?? shots.Select(s => s.Id).ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Placements: null,
            OfferedPlacementIds: null);
    }

    private static JsonSerializerOptions ConfigOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static JsonSerializerOptions ArtifactOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
