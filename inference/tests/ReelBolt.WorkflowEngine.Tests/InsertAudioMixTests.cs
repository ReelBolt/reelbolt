using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Sound from an inserted asset (docs/video-editing.md "Sound from an inserted asset") through the
/// REAL <see cref="VideoCompileStepExecutor"/> on the single-source <c>select</c> encode path. The
/// multi-source <c>concat</c> twins live in <see cref="VideoMultiSourceTests"/>. Fixture builders
/// follow <see cref="CaptionCompileTests"/>; the insert is the deterministic project-file content
/// (<see cref="VideoCompileStepConfig.InsertContentProjectFileId"/>), so no plan step is needed and
/// the voiceover step can sit at order 3 exactly as it does there.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class InsertAudioMixTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid ContentFileId = Guid.NewGuid();
    private static readonly Guid MusicFileId = Guid.NewGuid();
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const string SourceVideoKey = "projects/p/outputFiles/e/render.mp4";
    private const string MusicKey = "projects/p/userFiles/music.mp3";
    private static string ContentKey => $"projects/{ProjectId}/userFiles/{ContentFileId}.mp4";

    // =======================================================================
    // (b) Flag off: byte-identical to the compile before this feature existed
    // =======================================================================

    /// <summary>
    /// The argv and filtergraph of the applied-insert compile below, captured from the executor
    /// BEFORE <c>VideoCompileStepConfig.MixInsertAudio</c> existed (commit 4c02c5c8c) and
    /// pinned verbatim, with only the per-run scratch paths reduced to their file names. With the
    /// flag off (the default) not one character may move.
    /// </summary>
    private const string PreFeatureArgv = "-nostdin\n-hide_banner\n-y\n-loglevel\nerror\n-progress\npipe:1\n-protocol_whitelist\nfile\n-format_whitelist\n" + UserMediaInputFormats.Whitelist + "\n-i\n<file:source-0.mp4>\n-protocol_whitelist\nfile\n-format_whitelist\n" + UserMediaInputFormats.Whitelist + "\n-i\n<file:insert-asset-<guid>.mp4>\n-filter_complex\n[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vcut];[0:a]aselect='gte(t,0)*lt(t,10)',asetpts=N/SR/TB[aout];[1:v]scale=588:552:flags=bilinear,fps=30/1,format=yuv444p,pad=712:582:62:15:black,fillborders=left=62:right=62:top=15:bottom=15:mode=smear,perspective=x0='if(lt(in,181),-58.43356166814552+(in-1)*0.5333333333333333,37.56643833185448)':y0='-12.906493345164101':x1='if(lt(in,181),652.9852138420586+(in-1)*0.5333333333333333,748.9852138420586)':y1='13.7717107364685':x2='if(lt(in,181),-37.78521384205863+(in-1)*0.5333333333333333,58.214786157941376)':y2='567.8282892635315':x3='if(lt(in,181),673.6335616681456+(in-1)*0.533333333333334,769.6335616681457)':y3='594.5064933451641':sense=destination:eval=frame:interpolation=cubic,setpts=PTS-STARTPTS+2/TB[sic0];color=c=white:size=588x552:rate=30/1:duration=7,format=gray,pad=712:582:62:15:black,perspective=x0='if(lt(in,181),-58.43356166814552+(in-1)*0.5333333333333333,37.56643833185448)':y0='-12.906493345164101':x1='if(lt(in,181),652.9852138420586+(in-1)*0.5333333333333333,748.9852138420586)':y1='13.7717107364685':x2='if(lt(in,181),-37.78521384205863+(in-1)*0.5333333333333333,58.214786157941376)':y2='567.8282892635315':x3='if(lt(in,181),673.6335616681456+(in-1)*0.533333333333334,769.6335616681457)':y3='594.5064933451641':sense=destination:eval=frame,setpts=PTS-STARTPTS+2/TB[sim0];[sic0][sim0]alphamerge[sia0];[vcut][sia0]overlay=x=566:y=206:eof_action=pass:enable='between(t,2,8)'[vout]\n-map\n[vout]\n-map\n[aout]\n-c:v\nlibx264\n-crf\n20\n-preset\nveryfast\n-pix_fmt\nyuv420p\n-color_range\ntv\n-movflags\n+faststart\n-c:a\naac\n<file:edited.mp4>";

    [Fact]
    public async Task MixInsertAudio_off_leaves_an_applied_insert_compile_byte_identical_to_before_the_feature()
    {
        StepExecutionContext context = CreateContext(cfg => cfg, out Mock<IProjectFileWorkspace> workspace);

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, a => argv = Normalize(a), e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        edl.GetProperty("inserts").GetProperty("appliedInsertCount").GetInt32().Should().Be(1);
        argv.Should().Be(PreFeatureArgv);
    }

    /// <summary>
    /// Security ENG-3: the whitelists are per-input options, so the source AND the insert content
    /// (and the insert's second input for its sound) each carry their own pair.
    /// </summary>
    [Fact]
    public async Task Every_file_input_of_the_compile_carries_both_input_whitelists()
    {
        StepExecutionContext context = CreateContext(cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace);

        IReadOnlyList<string>? raw = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => raw = a.ToList()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        raw!.Count(a => a == "-i").Should().BeGreaterThanOrEqualTo(3);
        UserMediaInputHardeningTests.AssertEveryFileInputGuarded(raw!);
    }

    /// <summary>
    /// Security ENG-3: a source named as a playlist is refused with the author's own coded error
    /// before it is downloaded, and nothing is encoded.
    /// </summary>
    [Fact]
    public async Task A_source_named_as_a_playlist_is_refused_as_the_authors_input_before_any_download()
    {
        // The same source as every other test here, under a playlist name.
        string playlistKey = Path.ChangeExtension(SourceVideoKey, ".m3u8");
        StepExecutionContext context = CreateContext(cfg => cfg, out Mock<IProjectFileWorkspace> workspace);
        List<StepOutputHistoryEntry> history = (List<StepOutputHistoryEntry>)context.StepOutputHistory;
        history[0] = history[0] with { OutputStorageKey = playlistKey };

        IReadOnlyList<string>? raw = null;
        StepExecutionResult result = await CreateExecutor(workspace, a => raw = a.ToList()).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        JsonElement error = output.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(UserMediaInputFormats.RefusedCode);
        error.GetProperty("message").GetString().Should().Be(UserMediaInputFormats.RefusedMessage);
        raw.Should().BeNull("nothing is encoded from a refused source");
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), playlistKey, It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// Security ENG-3: a list file under a media name is caught by the probe's demuxer whitelist,
    /// and that too is the author's coded error, not an unexpected platform failure.
    /// </summary>
    [Fact]
    public async Task A_source_the_probe_refuses_for_its_format_is_the_authors_input()
    {
        StepExecutionContext context = CreateContext(cfg => cfg, out Mock<IProjectFileWorkspace> workspace);

        StepExecutionResult result = await CreateExecutor(
            workspace,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.StartsWith("source-", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MediaFormatNotAllowedException("source-0.mp4")))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Failed);
        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(UserMediaInputFormats.RefusedCode);
    }

    // =======================================================================
    // (a) Flag on: the insert's own sound, at its picture's moment, once
    // =======================================================================

    /// <summary>
    /// The sound starts with the picture and is played ONCE at its own length. The tracked plate is
    /// on screen for [2, 8) s while the content is only 4 s long, so the two claims are told apart:
    /// the delay is the picture's own start and the play length is the CONTENT's, not the window's.
    /// </summary>
    [Fact]
    public async Task MixInsertAudio_on_starts_the_inserts_sound_with_its_picture_and_plays_it_once_at_its_own_length()
    {
        StepExecutionContext context = CreateContext(cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace);

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, a => argv = Normalize(a), e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string graph = Graph(argv!);

        // The picture's own window, gated on the output timeline.
        (double pictureStart, double pictureEnd) = PictureWindow(graph);
        pictureStart.Should().Be(2.0);
        pictureEnd.Should().Be(8.0);

        // The sound's adelay IS the picture's start — they begin together.
        (double soundStartSec, double soundPlaySec) = InsertAudioBranch(graph);
        soundStartSec.Should().Be(pictureStart);
        // Once, at its own 4-second length — never stretched over the 6-second window…
        soundPlaySec.Should().Be(4.0);
        // …and never looped.
        argv!.Should().NotContain("-stream_loop");
        // The sound has its OWN -i, a second input on the same staged content file the picture
        // insert already reads — exactly where a sound-effect clip's input would sit.
        argv!.Split('\n').Count(l => l.StartsWith("<file:insert-asset-", StringComparison.Ordinal)).Should().Be(2);

        JsonElement audio = edl.GetProperty("inserts").GetProperty("audio");
        audio.GetProperty("requested").GetInt32().Should().Be(1);
        audio.GetProperty("mixed").GetInt32().Should().Be(1);
        audio.GetProperty("absent").GetInt32().Should().Be(0);
        audio.GetProperty("gainDb").GetInt32().Should().Be(0);
        JsonElement region = audio.GetProperty("regions")[0];
        region.GetProperty("regionId").GetString().Should().Be("r0");
        region.GetProperty("mixed").GetBoolean().Should().BeTrue();
        region.GetProperty("outputStartSec").GetDouble().Should().Be(2.0);
        region.GetProperty("playSec").GetDouble().Should().Be(4.0);
    }

    /// <summary>
    /// A pinned project video with sound (<c>InsertContentProjectFileId</c>) reaches the programme at
    /// <c>InsertAudioGainDb</c>, and the applied insert itself says so (<c>audio {mixed, gainDb}</c>).
    /// </summary>
    [Fact]
    public async Task A_pinned_insert_reports_on_the_insert_itself_that_its_sound_was_mixed_at_the_configured_gain()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { MixInsertAudio = true, InsertAudioGainDb = -6 }, out Mock<IProjectFileWorkspace> workspace);

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, a => argv = Normalize(a), e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        Graph(argv!).Should().Contain("volume=0.50119", "-6 dB is the pinned screen's own gain on its sound branch");
        JsonElement audio = edl.GetProperty("inserts").GetProperty("appliedInserts")[0].GetProperty("audio");
        audio.GetProperty("mixed").GetBoolean().Should().BeTrue();
        audio.GetProperty("gainDb").GetInt32().Should().Be(-6);
    }

    /// <summary>
    /// The sound is trimmed to the insert's on-screen window: the plate is tracked over [6, 9) s —
    /// a 3-second window, SHORTER than the 4-second content — so only the first 3 seconds play.
    /// </summary>
    [Fact]
    public async Task MixInsertAudio_on_trims_the_inserts_sound_to_a_window_shorter_than_its_content()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace,
            track: Track(startSec: 6.0, endSec: 9.0));

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, a => argv = Normalize(a), e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string graph = Graph(argv!);

        (double pictureStart, double pictureEnd) = PictureWindow(graph);
        pictureStart.Should().Be(6.0);
        pictureEnd.Should().Be(9.0);
        (double soundStartSec, double soundPlaySec) = InsertAudioBranch(graph);
        soundStartSec.Should().Be(pictureStart);
        soundPlaySec.Should().Be(3.0);

        edl.GetProperty("inserts").GetProperty("audio").GetProperty("regions")[0]
            .GetProperty("playSec").GetDouble().Should().Be(3.0);
    }

    /// <summary>
    /// A portion of the plate's window the cut removed is not heard: the tracked region lives inside
    /// a shot the edit dropped, so no insert is applied and the content never even becomes an input.
    /// </summary>
    [Fact]
    public async Task MixInsertAudio_on_mixes_nothing_for_an_insert_the_cut_removed()
    {
        StepExecutionContext context = CreateContext(
            cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace,
            shots: [("s0", 0.0, 5.0), ("s1", 5.0, 10.0)], keep: ["s1"], track: Track(startSec: 1.0, endSec: 4.0));

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(workspace, a => argv = Normalize(a), e => edl = e).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(0);
        inserts.GetProperty("droppedInserts").EnumerateArray().Should().Contain(d =>
            d.GetProperty("regionId").GetString() == "r0" && d.GetProperty("reason").GetString() == "cut_away");

        JsonElement audio = inserts.GetProperty("audio");
        audio.GetProperty("requested").GetInt32().Should().Be(0);
        audio.GetProperty("mixed").GetInt32().Should().Be(0);
        audio.GetProperty("regions").GetArrayLength().Should().Be(0);

        Graph(argv!).Should().NotContain("[sfx0]");
        argv!.Should().NotContain("insert-asset");
    }

    /// <summary>
    /// Content with no sound track — a still image always counts here — is the warning
    /// <c>insert_audio_absent</c>, never a failure: the insert still composites.
    /// </summary>
    [Fact]
    public async Task MixInsertAudio_reports_an_insert_with_no_sound_track_and_still_composites_it()
    {
        StepExecutionContext context = CreateContext(cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace);

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, a => argv = Normalize(a), e => edl = e,
            configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("insert-asset")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(4, 30, 1, 1080, 1920, "h264", null, null)))
            .ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(1, "the picture still composites");

        JsonElement audio = inserts.GetProperty("audio");
        audio.GetProperty("mixed").GetInt32().Should().Be(0);
        audio.GetProperty("absent").GetInt32().Should().Be(1);
        audio.GetProperty("regions")[0].GetProperty("reason").GetString().Should().Be("no_audio_stream");
        Graph(argv!).Should().NotContain("[sfx0]");

        using JsonDocument output = JsonDocument.Parse(result.Output);
        output.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("code").GetString()).Should().Contain("insert_audio_absent");
    }

    /// <summary>
    /// Without <c>normalize</c> in the build's <c>amix</c>, mixing an insert would quietly reduce
    /// every other input's level, so nothing is mixed and the degrade is reported — the cut itself
    /// is unaffected.
    /// </summary>
    [Fact]
    public async Task MixInsertAudio_without_the_amix_normalize_option_mixes_nothing()
    {
        StepExecutionContext context = CreateContext(cfg => cfg with { MixInsertAudio = true }, out Mock<IProjectFileWorkspace> workspace);

        string? argv = null;
        JsonElement edl = default;
        StepExecutionResult result = await CreateExecutor(
            workspace, a => argv = Normalize(a), e => edl = e, amixNormalizeAvailable: false).ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonElement inserts = edl.GetProperty("inserts");
        inserts.GetProperty("appliedInsertCount").GetInt32().Should().Be(1);

        JsonElement audio = inserts.GetProperty("audio");
        audio.GetProperty("unavailable").GetBoolean().Should().BeTrue();
        audio.GetProperty("mixed").GetInt32().Should().Be(0);
        Graph(argv!).Should().NotContain("[sfx0]");
    }

    /// <summary>The tracked insert's own picture window, read back from the filtergraph's <c>enable=</c> gate.</summary>
    private static (double Start, double End) PictureWindow(string graph)
    {
        System.Text.RegularExpressions.Match m = Regex.Match(graph, @"enable='between\(t,(?<start>[0-9.]+),(?<end>[0-9.]+)\)'");
        m.Success.Should().BeTrue(because: graph);
        return (double.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups["end"].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The insert-audio cue's own branch: where it starts (from <c>adelay</c>) and how long it plays
    /// (from <c>atrim</c>). Both are read from the graph the executor actually built, so a change in
    /// the picture window moves them too — the sound cannot drift away from the picture unnoticed.
    /// </summary>
    private static (double StartSec, double PlaySec) InsertAudioBranch(string graph)
    {
        System.Text.RegularExpressions.Match m = Regex.Match(
            graph,
            @"\[[0-9]+:a\]atrim=end=(?<dur>[0-9.]+),asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo," +
            @"volume=[0-9.]+,afade=t=out:st=[0-9.]+:d=[0-9.]+,adelay=(?<ms>[0-9]+)\|[0-9]+\[sfx0\]");
        m.Success.Should().BeTrue(because: graph);
        return (double.Parse(m.Groups["ms"].Value, CultureInfo.InvariantCulture) / 1000.0,
                double.Parse(m.Groups["dur"].Value, CultureInfo.InvariantCulture));
    }

    // =======================================================================
    // Fixture
    // =======================================================================

    /// <summary>
    /// Every argv element that is a scratch path reduced to its file name (with any 32-hex GUID
    /// replaced), and the filtergraph — inline or read back from its script file — appended
    /// verbatim, so two runs of the same compile produce the same string.
    /// </summary>
    private static string Normalize(IReadOnlyList<string> args)
    {
        static string Clean(string s) => Regex.Replace(s, "[0-9a-f]{32}", "<guid>");
        var parts = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (i > 0 && args[i - 1] == "-filter_complex_script")
            {
                parts.Add("<script>");
                parts.Add(File.ReadAllText(a));
                continue;
            }

            parts.Add(Path.IsPathRooted(a) ? $"<file:{Clean(Path.GetFileName(a))}>" : a);
        }

        return string.Join("\n", parts);
    }

    private static string Graph(string normalizedArgv)
    {
        string[] lines = normalizedArgv.Split('\n');
        int i = Array.FindIndex(lines, l => l is "-filter_complex" or "<script>");
        i.Should().BeGreaterThan(-1);
        return lines[i + 1];
    }

    private static VideoInsertRegionTrack Track(double confidence = 0.9, double startSec = 2.0, double endSec = 8.0) => new(
        Id: "r0", ShotId: "s0", StartSec: startSec, EndSec: endSec,
        Keyframes:
        [
            new VideoInsertQuadKeyframe(startSec, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72),
            new VideoInsertQuadKeyframe(endSec, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72)
        ],
        Confidence: confidence, MeanAreaRatio: 0.12, MeanAspectRatio: 0.6,
        MotionClass: "Slow", ColorName: "green");

    private static VideoCompileStepExecutor CreateExecutor(
        Mock<IProjectFileWorkspace> workspace,
        Action<IReadOnlyList<string>>? encodeCaptured = null,
        Action<JsonElement>? edlCaptured = null,
        Action<Mock<IMediaProbe>>? configureMediaProbe = null,
        bool amixNormalizeAvailable = true)
    {
        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();

        // Only the main encode is captured: it is the one ffmpeg call carrying a filtergraph.
        void Capture(IReadOnlyList<string> args)
        {
            if (args.Contains("-filter_complex") || args.Contains("-filter_complex_script"))
                encodeCaptured?.Invoke(args);
        }

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext ... perspective alphamerge colorbalance colorlevels hue eq", string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Capture(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => Capture(args))
            .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, amixNormalizeAvailable ? "... normalize ..." : "... amix has no such option ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
        // The insert content: a 4-second clip WITH a sound track, shorter than its 6-second window,
        // so "plays once at its own length" and "trimmed to the window" are told apart.
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("insert-asset")), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(4, 30, 1, 1080, 1920, "h264", "aac", 48000));
        configureMediaProbe?.Invoke(mediaProbe);

        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
            {
                if (edlCaptured is not null && path.EndsWith("edl.json", StringComparison.OrdinalIgnoreCase))
                    edlCaptured(JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone());
            })
            .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
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

        var options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "insert-audio-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 30,
            CompileTimeoutSeconds = 30
        });

        return new VideoCompileStepExecutor(
            toolRunner.Object, mediaProbe.Object, workspace.Object, new Mock<IServiceScopeFactory>().Object,
            options, NullLogger<VideoCompileStepExecutor>.Instance);
    }

    /// <summary>
    /// One 10-second source with one shot, one offered plate tracked over [2, 8) s, and the project
    /// video <see cref="ContentFileId"/> composited into it by config. A voiceover step's output is
    /// always in the history at order 3 (one line at 2 s, 3 s long) and a music file is always in
    /// the project; whether either is USED is the test's config.
    /// </summary>
    private static StepExecutionContext CreateContext(
        Func<VideoCompileStepConfig, VideoCompileStepConfig> configure,
        out Mock<IProjectFileWorkspace> workspace,
        (string Id, double Start, double End)[]? shots = null,
        string[]? keep = null,
        VideoInsertRegionTrack? track = null)
    {
        workspace = new Mock<IProjectFileWorkspace>();
        shots ??= [("s0", 0.0, 10.0)];
        keep ??= shots.Select(s => s.Id).ToArray();

        VideoAnalysisArtifact artifact = new(
            Version: 1,
            Media: new VideoAnalysisMedia(10.0, 30, 1, 1920, 1080),
            Shots: shots.Select(s => new VideoAnalysisShot(s.Id, s.Start, s.End)).ToList(),
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: shots.Select(s => s.Id).ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Placements: null,
            OfferedPlacementIds: null)
        {
            InsertRegions = [track ?? Track()],
            OfferedInsertRegionIds = ["r0"]
        };
        string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());

        string decisionJson = JsonSerializer.Serialize(new
        {
            keep = keep.Select(id => new { fromId = id, toId = id, reason = "keep" }).ToArray(),
            editRationale = "test",
            suggestedTitle = "Test"
        });

        string voiceoverOutputJson = JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = new object[]
                {
                    new
                    {
                        id = "vo0",
                        startSec = 2.0,
                        durationSec = 3.0,
                        status = "ok",
                        storageKey = $"projects/{ProjectId}/agentFiles/voiceover/lines/abc.wav",
                        text = "deploys go from forty minutes to four"
                    }
                }
            },
            meta = new { }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        static Task WriteBytes(string destPath) { File.WriteAllBytes(destPath, [0x00, 0x01, 0x02]); return Task.CompletedTask; }

        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), AnalysisKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => { File.WriteAllText(destPath, artifactJson); return Task.CompletedTask; });
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), SourceVideoKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => WriteBytes(destPath));
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), ContentKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => WriteBytes(destPath));
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), MusicKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => WriteBytes(destPath));
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.Is<string>(s => s.Contains("/agentFiles/voiceover/", StringComparison.Ordinal)), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, _, destPath, _) => WriteBytes(destPath));
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectWorkspaceFile>
            {
                new(ContentFileId, ProjectId, "chat.mp4", null, "userFiles", ContentKey, "video/mp4", 1024, DateTime.UtcNow, null),
                new(MusicFileId, ProjectId, "music.mp3", null, "userFiles", MusicKey, "audio/mpeg", 4096, DateTime.UtcNow, null)
            });

        VideoCompileStepConfig config = configure(new VideoCompileStepConfig(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1,
            PrePaddingMs: 0,
            PostPaddingMs: 0,
            EnableInserts: true,
            InsertContentProjectFileId: ContentFileId,
            // No dip detection: the content is a fixture, and its own ffmpeg call is not under test.
            InsertContentDips: InsertContentDipMode.Keep));

        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 4,
            StepType = StepType.VideoCompile,
            VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions())
        };
        var analyzeStep = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(
                new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions())
        };

        List<StepOutputHistoryEntry> history =
        [
            new StepOutputHistoryEntry(0, "Render", "{}", OutputStorageKey: SourceVideoKey, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
            new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
            new StepOutputHistoryEntry(3, "Voiceover", voiceoverOutputJson, OutputStorageKey: null, ArtifactStorageKey: null)
        ];

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

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}
